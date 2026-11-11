using Larik.Data;
using Larik.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>
/// End-to-end tests against real temporary SQLite files. No server, no environment switches: every
/// test owns a fresh database and deletes it afterwards.
/// </summary>
public sealed class SqliteProviderTests
{
    private static ConnectionProfile Profile(string file, params (string Key, string Value)[] values)
    {
        var profile = new ConnectionProfile { ProviderId = SqliteProvider.ProviderId };
        profile.Values["file"] = file;
        foreach (var (key, value) in values)
        {
            profile.Values[key] = value;
        }

        return profile;
    }

    private static IDatabaseConnection Connection(ConnectionProfile profile) =>
        new SqliteProvider().CreateConnection(profile);

    private static async Task ExecuteOrThrowAsync(IDatabaseConnection connection, string sql)
    {
        var results = await connection.ExecuteBatchAsync(sql, "main", QueryExecutionOptions.Default);
        var error = results.FirstOrDefault(result => result.IsError);
        Assert.True(error is null, error?.Message);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "larik-sqlite-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string DatabaseFile(string name = "test.db") => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            // Disposed sessions return their native handle to Microsoft.Data.Sqlite's pool; clearing
            // it releases the file so the directory can be removed on Windows.
            SqliteConnection.ClearAllPools();

            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException)
            {
            }
        }
    }

    // ------------------------------------------------------------------ connect

    [Fact]
    public async Task Open_creates_the_file_and_reports_version_and_databases()
    {
        using var temp = new TempDirectory();
        var file = temp.DatabaseFile();
        await using var connection = Connection(Profile(file));

        await connection.OpenAsync();

        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.StartsWith("SQLite 3.", connection.ServerVersion);
        Assert.True(System.IO.File.Exists(file));
        Assert.Contains("main", await connection.GetDatabasesAsync());
        Assert.Equal("main", await connection.GetCurrentDatabaseAsync());
    }

    [Fact]
    public async Task An_empty_file_setting_is_rejected()
    {
        await using var connection = Connection(new ConnectionProfile { ProviderId = SqliteProvider.ProviderId });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.OpenAsync());

        Assert.Contains("database file", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ConnectionState.Faulted, connection.State);
    }

    [Fact]
    public async Task Missing_file_and_folder_fail_with_a_useful_message()
    {
        using var temp = new TempDirectory();

        await using var noCreate = Connection(Profile(
            temp.DatabaseFile("missing.db"),
            ("createIfMissing", "false")));

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => noCreate.OpenAsync());
        Assert.Contains("does not exist", missing.Message, StringComparison.OrdinalIgnoreCase);

        await using var noFolder = Connection(Profile(
            System.IO.Path.Combine(temp.Path, "nope", "x.db")));

        var folder = await Assert.ThrowsAsync<InvalidOperationException>(() => noFolder.OpenAsync());
        Assert.Contains("does not exist", folder.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Memory_databases_are_private_to_each_session()
    {
        await using var first = Connection(Profile(":memory:"));
        await using var second = Connection(Profile(":memory:"));
        await first.OpenAsync();
        await second.OpenAsync();

        await ExecuteOrThrowAsync(first, "CREATE TABLE only_here (id INTEGER);");

        Assert.Contains(await first.GetSchemaAsync("main"), schema => schema.Name == "only_here");
        Assert.DoesNotContain(await second.GetSchemaAsync("main"), schema => schema.Name == "only_here");
    }

    // ------------------------------------------------------------------ execution

    [Fact]
    public async Task Batch_runs_statements_and_reports_rows_and_affected_counts()
    {
        using var temp = new TempDirectory();
        await using var connection = Connection(Profile(temp.DatabaseFile()));
        await connection.OpenAsync();

        var results = await connection.ExecuteBatchAsync("""
            CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, score REAL);
            INSERT INTO users (name, score) VALUES ('Ada', 9.5), ('Grace', 8.25);
            SELECT id, name, score FROM users ORDER BY id;
            """, "main", QueryExecutionOptions.Default);

        Assert.Equal(3, results.Count);
        Assert.False(results[0].IsError);
        Assert.Equal(2, results[1].RecordsAffected);
        Assert.Equal("2 rows affected", results[1].Message);

        var select = results[2];
        Assert.Equal(["id", "name", "score"], select.Columns.Select(column => column.Name).ToArray());
        Assert.NotNull(select.Columns[0].DataType);
        Assert.Equal(2, select.Rows.Count);
        Assert.Equal("1", select.Rows[0][0]);
        Assert.Equal("Ada", select.Rows[0][1]);
        Assert.Equal("9.5", select.Rows[0][2]);
        Assert.Equal("Grace", select.Rows[1][1]);
    }

    [Fact]
    public async Task The_row_cap_truncates_and_says_so()
    {
        using var temp = new TempDirectory();
        await using var connection = Connection(Profile(temp.DatabaseFile()));
        await connection.OpenAsync();

        await ExecuteOrThrowAsync(connection, """
            CREATE TABLE nums (n INTEGER);
            INSERT INTO nums (n) VALUES (1), (2), (3), (4), (5), (6), (7);
            """);

        var select = await connection.ExecuteAsync(
            "SELECT n FROM nums ORDER BY n;",
            "main",
            new QueryExecutionOptions { MaxRows = 5 });

        Assert.Equal(5, select.Rows.Count);
        Assert.True(select.IsTruncated);
        Assert.Equal(5, select.RowLimit);
        Assert.Equal(new string?[] { "1", "2", "3", "4", "5" }, select.Rows.Select(row => row[0]).ToArray());
    }

    [Fact]
    public async Task A_statement_error_is_a_result_and_stops_the_batch()
    {
        using var temp = new TempDirectory();
        await using var connection = Connection(Profile(temp.DatabaseFile()));
        await connection.OpenAsync();

        var results = await connection.ExecuteBatchAsync("""
            CREATE TABLE t (id INTEGER PRIMARY KEY);
            INSERT INTO t VALUES (1);
            INSERT INTO t VALUES (1);
            SELECT * FROM t;
            """, "main", QueryExecutionOptions.Default);

        Assert.Equal(3, results.Count);
        Assert.True(results[2].IsError);
        Assert.Contains("UNIQUE", results[2].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Blob_and_null_values_round_trip_as_sqlite_literals()
    {
        using var temp = new TempDirectory();
        await using var connection = Connection(Profile(temp.DatabaseFile()));
        await connection.OpenAsync();

        await ExecuteOrThrowAsync(
            connection,
            "CREATE TABLE b (data BLOB, note TEXT); INSERT INTO b VALUES (X'00FF10', NULL);");

        var result = await connection.ExecuteAsync("SELECT data, note FROM b;", "main");

        Assert.Equal("X'00FF10'", result.Rows[0][0]);
        Assert.Null(result.Rows[0][1]);
    }

    // ------------------------------------------------------------------ metadata

    [Fact]
    public async Task Schema_reports_tables_views_columns_keys_and_foreign_keys()
    {
        using var temp = new TempDirectory();
        await using var connection = Connection(Profile(temp.DatabaseFile()));
        await connection.OpenAsync();

        await ExecuteOrThrowAsync(connection, """
            CREATE TABLE authors (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            CREATE TABLE books (
                id INTEGER PRIMARY KEY,
                author_id INTEGER NOT NULL REFERENCES authors(id),
                title TEXT
            );
            CREATE VIEW book_titles AS SELECT id, title FROM books;
            """);

        var schema = await connection.GetSchemaAsync("main");

        Assert.DoesNotContain(
            schema,
            table => table.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase));

        var books = Assert.Single(schema, table => table.Name == "books");
        Assert.Equal(["id", "author_id", "title"], books.Columns);
        Assert.Equal(["id"], books.PrimaryKey);
        Assert.Equal(TableKind.Table, books.Kind);

        var foreignKey = Assert.Single(books.ForeignKeys);
        Assert.Equal("author_id", foreignKey.Column);
        Assert.Equal("authors", foreignKey.ReferencedTable);
        Assert.Equal("id", foreignKey.ReferencedColumn);
        Assert.Null(foreignKey.ReferencedSchema);

        var authors = Assert.Single(schema, table => table.Name == "authors");
        Assert.False(authors.ColumnDetails.Single(column => column.Name == "name").IsNullable);

        var view = Assert.Single(schema, table => table.Name == "book_titles");
        Assert.Equal(TableKind.View, view.Kind);
    }

    [Fact]
    public async Task A_foreign_key_written_without_a_target_column_resolves_to_the_primary_key()
    {
        using var temp = new TempDirectory();
        await using var connection = Connection(Profile(temp.DatabaseFile()));
        await connection.OpenAsync();

        await ExecuteOrThrowAsync(connection, """
            CREATE TABLE parents (code TEXT PRIMARY KEY);
            CREATE TABLE kids (parent_code TEXT REFERENCES parents);
            """);

        var schema = await connection.GetSchemaAsync("main");
        var kids = Assert.Single(schema, table => table.Name == "kids");
        var foreignKey = Assert.Single(kids.ForeignKeys);

        Assert.Equal("parents", foreignKey.ReferencedTable);
        Assert.Equal("code", foreignKey.ReferencedColumn);
    }

    [Fact]
    public async Task Attached_databases_appear_in_the_schema_as_qualified_names()
    {
        using var temp = new TempDirectory();
        var other = temp.DatabaseFile("other.db");
        await using var connection = Connection(Profile(temp.DatabaseFile()));
        await connection.OpenAsync();

        await ExecuteOrThrowAsync(
            connection,
            $"ATTACH DATABASE '{other.Replace("'", "''")}' AS extra; " +
            "CREATE TABLE extra.notes (id INTEGER PRIMARY KEY, body TEXT);");

        // The picker stays on main: SQLite cannot switch the default database for the session.
        Assert.Equal(["main"], await connection.GetDatabasesAsync());

        var schema = await connection.GetSchemaAsync("main");
        var notes = Assert.Single(schema, table => table.Name == "notes");
        Assert.Equal("extra", notes.Schema);
    }

    // ------------------------------------------------------------------ foreign keys setting

    [Fact]
    public async Task Foreign_keys_are_enforced_by_default_and_can_be_turned_off()
    {
        using var temp = new TempDirectory();
        var file = temp.DatabaseFile();

        await using (var enforcing = Connection(Profile(file)))
        {
            await enforcing.OpenAsync();

            var results = await enforcing.ExecuteBatchAsync("""
                CREATE TABLE parents (id INTEGER PRIMARY KEY);
                CREATE TABLE kids (parent_id INTEGER REFERENCES parents(id));
                INSERT INTO kids VALUES (99);
                """, "main", QueryExecutionOptions.Default);

            Assert.True(results[2].IsError);
            Assert.Contains("FOREIGN KEY", results[2].Message, StringComparison.OrdinalIgnoreCase);
        }

        await using var relaxed = Connection(Profile(file, ("foreignKeys", "false")));
        await relaxed.OpenAsync();

        var insert = await relaxed.ExecuteAsync("INSERT INTO kids VALUES (99);", "main");

        Assert.False(insert.IsError);
        Assert.Equal(1, insert.RecordsAffected);
    }

    // ------------------------------------------------------------------ transactions

    [Fact]
    public async Task Transactions_begin_commit_and_rollback_are_reflected_and_enforced()
    {
        using var temp = new TempDirectory();
        await using var connection = Connection(Profile(temp.DatabaseFile()));
        await connection.OpenAsync();

        await ExecuteOrThrowAsync(connection, "CREATE TABLE t (id INTEGER);");
        Assert.Equal(SessionTransactionState.None, await connection.GetTransactionStateAsync());

        await ExecuteOrThrowAsync(connection, "BEGIN;");
        var open = await connection.GetTransactionStateAsync();
        Assert.True(open.HasTransaction);
        Assert.True(open.CanCommit);
        Assert.True(open.CanRollback);

        await ExecuteOrThrowAsync(connection, "INSERT INTO t VALUES (1);");
        await connection.RollbackTransactionAsync();
        Assert.Equal(SessionTransactionState.None, await connection.GetTransactionStateAsync());

        var count = await connection.ExecuteAsync("SELECT COUNT(*) FROM t;", "main");
        Assert.Equal("0", count.Rows[0][0]);

        await ExecuteOrThrowAsync(connection, "BEGIN; INSERT INTO t VALUES (2);");
        await connection.CommitTransactionAsync();
        Assert.Equal(SessionTransactionState.None, await connection.GetTransactionStateAsync());

        count = await connection.ExecuteAsync("SELECT COUNT(*) FROM t;", "main");
        Assert.Equal("1", count.Rows[0][0]);
    }

    [Fact]
    public async Task Savepoints_keep_the_transaction_open()
    {
        using var temp = new TempDirectory();
        await using var connection = Connection(Profile(temp.DatabaseFile()));
        await connection.OpenAsync();
        await ExecuteOrThrowAsync(connection, "CREATE TABLE t (id INTEGER);");

        await ExecuteOrThrowAsync(connection, "BEGIN; SAVEPOINT s; INSERT INTO t VALUES (3);");
        Assert.True((await connection.GetTransactionStateAsync()).HasTransaction);

        await ExecuteOrThrowAsync(connection, "ROLLBACK TO s;");
        Assert.True((await connection.GetTransactionStateAsync()).HasTransaction);

        await connection.CommitTransactionAsync();
        var count = await connection.ExecuteAsync("SELECT COUNT(*) FROM t;", "main");
        Assert.Equal("0", count.Rows[0][0]);
    }

    [Fact]
    public async Task A_failed_statement_does_not_abort_the_transaction()
    {
        using var temp = new TempDirectory();
        await using var connection = Connection(Profile(temp.DatabaseFile()));
        await connection.OpenAsync();
        await ExecuteOrThrowAsync(connection, "BEGIN;");

        var failure = await connection.ExecuteAsync("INSERT INTO missing_table VALUES (1);", "main");
        Assert.True(failure.IsError);
        Assert.True((await connection.GetTransactionStateAsync()).HasTransaction);

        var insert = await connection.ExecuteAsync("CREATE TABLE t (id INTEGER); INSERT INTO t VALUES (1);", "main");
        Assert.False(insert.IsError);

        await connection.CommitTransactionAsync();
        Assert.Equal(SessionTransactionState.None, await connection.GetTransactionStateAsync());
    }

    // ------------------------------------------------------------------ read-only and triggers

    [Fact]
    public async Task A_read_only_connection_reads_but_refuses_writes()
    {
        using var temp = new TempDirectory();
        var file = temp.DatabaseFile();

        await using (var writer = Connection(Profile(file)))
        {
            await writer.OpenAsync();
            await ExecuteOrThrowAsync(writer, "CREATE TABLE t (id INTEGER); INSERT INTO t VALUES (1);");
        }

        await using var reader = Connection(Profile(file, ("readOnly", "true")));
        await reader.OpenAsync();

        var select = await reader.ExecuteAsync("SELECT COUNT(*) FROM t;", "main");
        Assert.Equal("1", select.Rows[0][0]);

        var write = await reader.ExecuteAsync("INSERT INTO t VALUES (2);", "main");
        Assert.True(write.IsError);
        Assert.Contains("readonly", write.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_trigger_body_runs_as_one_statement()
    {
        using var temp = new TempDirectory();
        await using var connection = Connection(Profile(temp.DatabaseFile()));
        await connection.OpenAsync();

        await ExecuteOrThrowAsync(connection, """
            CREATE TABLE users (id INTEGER PRIMARY KEY, active INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE audit (user_id INTEGER, reason TEXT);
            CREATE TRIGGER audit_users AFTER UPDATE ON users
            WHEN NEW.active = 1
            BEGIN
                INSERT INTO audit VALUES (OLD.id, 'active');
                UPDATE audit SET reason = CASE WHEN reason = 'active' THEN 'changed' ELSE reason END;
            END;
            INSERT INTO users (id) VALUES (1);
            """);

        await ExecuteOrThrowAsync(connection, "UPDATE users SET active = 1 WHERE id = 1;");

        var audit = await connection.ExecuteAsync("SELECT user_id, reason FROM audit;", "main");
        Assert.Single(audit.Rows);
        Assert.Equal("1", audit.Rows[0][0]);
        Assert.Equal("changed", audit.Rows[0][1]);
    }
}
