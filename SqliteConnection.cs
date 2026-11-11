using System.Diagnostics;
using System.Globalization;
using Larik.Data;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using DbConnectionState = System.Data.ConnectionState;

namespace Larik.Data.Sqlite;

/// <summary>
/// One SQLite session, held per query tab. The physical connection carries the tab's state
/// (transactions, attached databases, temp tables); nothing lives in a shared static, so a
/// <c>BEGIN</c> in one tab never leaks into another.
/// </summary>
internal sealed class SqliteConnectionSession : IDatabaseConnection
{
    private readonly ConnectionProfile _profile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private string? _version;

    public SqliteConnectionSession(ConnectionProfile profile) => _profile = profile;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public string? ServerVersion => _version;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await OpenCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Opens the profile's file while the caller already holds <see cref="_gate"/>. A failure leaves
    /// the session faulted so the shell can retry or show the message; the connection itself is
    /// disposed before the exception escapes.
    /// </summary>
    private async Task OpenCoreAsync(CancellationToken cancellationToken)
    {
        if (State == ConnectionState.Connected && _connection is { State: DbConnectionState.Open })
        {
            return;
        }

        State = ConnectionState.Connecting;

        try
        {
            var builder = SqliteProfile.Build(_profile);

            // Replacing a handle that an earlier failure left behind: a closed or broken connection
            // must not stay owned by the session.
            if (_connection is { } previous)
            {
                _connection = null;
                try
                {
                    await previous.DisposeAsync();
                }
                catch
                {
                }
            }

            var connection = new SqliteConnection(builder.ConnectionString);

            try
            {
                await connection.OpenAsync(cancellationToken);
                var version = await ReadVersionAsync(connection, cancellationToken);
                _connection = connection;
                _version = $"SQLite {version}";
                State = ConnectionState.Connected;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
        catch
        {
            State = ConnectionState.Faulted;
            throw;
        }
    }

    private static async Task<string> ReadVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? "unknown"
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "unknown";
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { } connection)
            {
                _connection = null;
                try
                {
                    await connection.CloseAsync();
                    await connection.DisposeAsync();
                }
                catch
                {
                    // Disconnecting is best effort; a broken handle must not block the shell.
                }
            }

            _version = null;
            State = ConnectionState.Disconnected;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken cancellationToken = default) =>
        (await GetDatabaseDetailsAsync(cancellationToken))
            .Select(database => database.Name)
            .ToList();

    /// <summary>
    /// A SQLite connection exposes one database, <c>main</c>. Other files are attached into it and
    /// appear in the schema as qualified names (<c>extra.notes</c>), not as switchable databases:
    /// SQLite has no <c>USE</c> and unqualified names always resolve to <c>main</c>.
    /// </summary>
    public async Task<IReadOnlyList<DatabaseInfo>> GetDatabaseDetailsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await OpenCoreAsync(cancellationToken);
            return [new DatabaseInfo("main")];
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<List<string>> ReadDatabaseNamesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var names = new List<string> { "main" };

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA database_list;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(1);
            if (name.Equals("temp", StringComparison.OrdinalIgnoreCase)
                || names.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// SQLite has no <c>USE</c>; the session always executes against <c>main</c>, and other files are
    /// reached with <c>ATTACH</c>. Reporting <c>main</c> keeps the tab and rail honest.
    /// </summary>
    public Task<string?> GetCurrentDatabaseAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(State == ConnectionState.Connected ? "main" : null);

    // ------------------------------------------------------------------ metadata

    public async Task<IReadOnlyList<TableSchema>> GetSchemaAsync(
        string? database,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await OpenCoreAsync(cancellationToken);
            var connection = _connection!;

            try
            {
                var databases = await ReadDatabaseNamesAsync(connection, cancellationToken);
                var entries = new List<TableEntry>();

                foreach (var db in databases)
                {
                    List<ObjectEntry> objects;
                    try
                    {
                        objects = await ReadObjectsAsync(connection, db, cancellationToken);
                    }
                    catch (SqliteException)
                    {
                        // One attached file that is locked or unreadable must not hide main's schema.
                        continue;
                    }

                    foreach (var obj in objects)
                    {
                        var (columns, primaryKey) = await ReadColumnsAsync(connection, db, obj.Name, cancellationToken);
                        entries.Add(new TableEntry(db, obj.Name, obj.IsView, columns, primaryKey));
                    }
                }

                var primaryKeys = entries.ToDictionary(
                    entry => Key(entry.Database, entry.Name),
                    entry => entry.PrimaryKey,
                    StringComparer.Ordinal);

                var schemas = new List<TableSchema>(entries.Count);
                foreach (var entry in entries)
                {
                    var foreignKeys = entry.IsView
                        ? []
                        : await ReadForeignKeysAsync(
                            connection,
                            entry.Database,
                            entry.Name,
                            primaryKeys,
                            cancellationToken);

                    schemas.Add(new TableSchema
                    {
                        Name = entry.Name,
                        // Main is the default schema; attached files are only qualified when needed.
                        Schema = entry.Database == "main" ? null : entry.Database,
                        Kind = entry.IsView ? TableKind.View : TableKind.Table,
                        Columns = entry.Columns.Select(column => column.Name).ToList(),
                        ColumnDetails = entry.Columns,
                        PrimaryKey = entry.PrimaryKey,
                        ForeignKeys = foreignKeys,
                    });
                }

                return schemas;
            }
            catch (SqliteException)
            {
                // Schema is a browsing nicety; a locked or permission-failed catalog must not break
                // the session. Opening the file itself is a real error and already surfaced above.
                return [];
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record TableEntry(
        string Database,
        string Name,
        bool IsView,
        List<TableColumn> Columns,
        List<string> PrimaryKey);

    private sealed record ObjectEntry(string Name, bool IsView);

    private static string Key(string database, string table) => $"{database}\u001F{table}";

    private static async Task<List<ObjectEntry>> ReadObjectsAsync(
        SqliteConnection connection,
        string database,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadTableListAsync(connection, database, cancellationToken);
        }
        catch (SqliteException)
        {
            // pragma_table_list appeared in SQLite 3.37. The bundled engine is newer, but the
            // fallback keeps the provider usable if it is ever paired with an older native library.
            return await ReadMasterAsync(connection, database, cancellationToken);
        }
    }

    /// <summary>
    /// <c>pragma_table_list</c> separates real tables from virtual tables and their shadow tables,
    /// so an FTS index does not fill the browser with its internal storage.
    /// </summary>
    private static async Task<List<ObjectEntry>> ReadTableListAsync(
        SqliteConnection connection,
        string database,
        CancellationToken cancellationToken)
    {
        var objects = new List<ObjectEntry>();

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT name, type FROM pragma_table_list WHERE schema = {QuoteString(database)} ORDER BY name;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            var type = reader.GetString(1);
            if (type == "shadow" || name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            objects.Add(new ObjectEntry(name, type == "view"));
        }

        return objects;
    }

    private static async Task<List<ObjectEntry>> ReadMasterAsync(
        SqliteConnection connection,
        string database,
        CancellationToken cancellationToken)
    {
        var objects = new List<ObjectEntry>();

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT name, type FROM {QuoteIdentifier(database)}.sqlite_master " +
            "WHERE type IN ('table', 'view') ORDER BY name;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            objects.Add(new ObjectEntry(name, reader.GetString(1).Equals("view", StringComparison.OrdinalIgnoreCase)));
        }

        return objects;
    }

    private static async Task<(List<TableColumn> Columns, List<string> PrimaryKey)> ReadColumnsAsync(
        SqliteConnection connection,
        string database,
        string table,
        CancellationToken cancellationToken)
    {
        var columns = new List<TableColumn>();
        var keyed = new List<(int Ordinal, string Name)>();

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {QuoteIdentifier(database)}.table_info({QuoteString(table)});";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(1);
            var type = reader.IsDBNull(2) ? null : reader.GetString(2);
            var nullable = reader.GetInt64(3) == 0;
            var keyOrdinal = (int)reader.GetInt64(5);

            columns.Add(new TableColumn
            {
                Name = name,
                DataType = string.IsNullOrWhiteSpace(type) ? null : type,
                IsNullable = nullable,
            });

            if (keyOrdinal > 0)
            {
                keyed.Add((keyOrdinal, name));
            }
        }

        return (columns, keyed.OrderBy(key => key.Ordinal).Select(key => key.Name).ToList());
    }

    private static async Task<List<ForeignKeySchema>> ReadForeignKeysAsync(
        SqliteConnection connection,
        string database,
        string table,
        IReadOnlyDictionary<string, List<string>> primaryKeys,
        CancellationToken cancellationToken)
    {
        var rows = new List<(int Id, int Sequence, string ReferencedTable, string Column, string? ReferencedColumn)>();

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {QuoteIdentifier(database)}.foreign_key_list({QuoteString(table)});";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        var foreignKeys = new List<ForeignKeySchema>();
        foreach (var row in rows)
        {
            var target = row.ReferencedColumn;
            if (string.IsNullOrEmpty(target))
            {
                // REFERENCES parent written without a column means the parent's primary key, at the
                // same ordinal for a composite key.
                if (!primaryKeys.TryGetValue(Key(database, row.ReferencedTable), out var keys)
                    || row.Sequence >= keys.Count)
                {
                    continue;
                }

                target = keys[row.Sequence];
            }

            foreignKeys.Add(new ForeignKeySchema(
                $"fk_{table}_{row.Id}",
                row.Column,
                row.ReferencedTable,
                target)
            {
                ReferencedSchema = database == "main" ? null : database,
            });
        }

        return foreignKeys;
    }

    // ------------------------------------------------------------------ execution

    public Task<QueryResult> ExecuteAsync(
        string sql,
        string? database,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(sql, database, QueryExecutionOptions.Default, cancellationToken);

    public async Task<QueryResult> ExecuteAsync(
        string sql,
        string? database,
        QueryExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        var results = await ExecuteBatchAsync(sql, database, options, cancellationToken);
        return results.Count > 0
            ? results[0]
            : QueryResult.Error("No result returned.", TimeSpan.Zero, DatabaseName(database));
    }

    /// <summary>
    /// SQLite has no <c>GO</c>, so the editor sends the document as one batch. Execution still splits
    /// it into statements so each reports its own result and a failure is attributable to the one
    /// that caused it. The dialect keeps a trigger body whole despite its internal semicolons.
    /// </summary>
    public async Task<IReadOnlyList<QueryResult>> ExecuteBatchAsync(
        string sql,
        string? database,
        QueryExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await OpenCoreAsync(cancellationToken);

            var statements = SqliteDialect.Instance.Split(sql);
            if (statements.Count == 0)
            {
                return [QueryResult.Error("Nothing to run — the editor is empty.", stopwatch.Elapsed, DatabaseName(database))];
            }

            var results = new List<QueryResult>(statements.Count);
            foreach (var span in statements)
            {
                var statement = sql.Substring(span.Start, span.Length);
                var result = await ExecuteStatementAsync(statement, database, options, stopwatch, cancellationToken);
                results.Add(result);

                if (result.IsError)
                {
                    break;
                }
            }

            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqliteException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (SqliteException ex)
        {
            return [QueryResult.Error(ex.Message, stopwatch.Elapsed, DatabaseName(database))];
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<QueryResult> ExecuteStatementAsync(
        string statement,
        string? database,
        QueryExecutionOptions options,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = _connection!.CreateCommand();
            command.CommandText = statement;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (reader.FieldCount == 0)
            {
                // No columns: drain the reader so RecordsAffected is final.
                while (await reader.ReadAsync(cancellationToken))
                {
                }

                var affected = reader.RecordsAffected;
                return new QueryResult
                {
                    Columns = [],
                    Rows = [],
                    RecordsAffected = affected > 0 ? affected : 0,
                    Message = affected > 0 ? $"{affected} rows affected" : "Statement completed",
                    Elapsed = stopwatch.Elapsed,
                    Database = DatabaseName(database),
                    Statement = statement,
                };
            }

            var columns = new List<QueryColumn>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var type = reader.GetDataTypeName(i);
                columns.Add(new QueryColumn
                {
                    Name = reader.GetName(i),
                    DataType = string.IsNullOrWhiteSpace(type) ? null : type,
                });
            }

            var rowLimit = options.EffectiveMaxRows;
            var rows = new List<string?[]>();
            var truncated = false;

            // Read one row past the cap only to know whether more data existed; the rest of a large
            // result is never materialized.
            while (await reader.ReadAsync(cancellationToken))
            {
                if (rows.Count >= rowLimit)
                {
                    truncated = true;
                    break;
                }

                var values = new string?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    values[i] = reader.IsDBNull(i) ? null : Format(reader.GetValue(i));
                }

                rows.Add(values);
            }

            return new QueryResult
            {
                Columns = columns,
                Rows = rows,
                Message = $"{rows.Count} rows",
                Elapsed = stopwatch.Elapsed,
                Database = DatabaseName(database),
                Statement = statement,
                IsTruncated = truncated,
                RowLimit = rowLimit,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (SqliteException ex)
        {
            return QueryResult.Error(ex.Message, stopwatch.Elapsed, DatabaseName(database));
        }
        catch (InvalidOperationException ex)
        {
            return QueryResult.Error(ex.Message, stopwatch.Elapsed, DatabaseName(database));
        }
    }

    private static string DatabaseName(string? database) =>
        string.IsNullOrWhiteSpace(database) ? "main" : database.Trim();

    /// <summary>
    /// Grid text for one value. SQLite's storage classes are integer, real, text, blob and null; the
    /// driver can also hand back DateTime for declared date columns. Blobs use SQLite's own literal
    /// form so a copied value is valid SQL.
    /// </summary>
    internal static string? Format(object value) => value switch
    {
        null or DBNull => null,
        byte[] bytes => $"X'{Convert.ToHexString(bytes)}'",
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        TimeSpan span => span.ToString(),
        bool flag => flag ? "1" : "0",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    // ------------------------------------------------------------------ transactions

    public bool SupportsTransactions => true;

    /// <summary>
    /// SQLite exposes the truth directly: <c>sqlite3_get_autocommit</c> is zero exactly while an
    /// explicit transaction (or a savepoint outside one) is open. Unlike PostgreSQL there is no
    /// aborted state to track: a failed statement leaves the transaction open and usable.
    /// </summary>
    public async Task<SessionTransactionState> GetTransactionStateAsync(CancellationToken cancellationToken = default)
    {
        // The handle must not be read while another statement is running on it.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _connection is { State: DbConnectionState.Open } connection
                ? ReadTransactionState(connection)
                : SessionTransactionState.None;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static SessionTransactionState ReadTransactionState(SqliteConnection connection)
    {
        try
        {
            return raw.sqlite3_get_autocommit(connection.Handle) == 0
                ? new SessionTransactionState(1, 1)
                : SessionTransactionState.None;
        }
        catch
        {
            // The handle is gone or the provider is not initialized; "unknown" must not crash a tab.
            return SessionTransactionState.None;
        }
    }

    public Task CommitTransactionAsync(CancellationToken cancellationToken = default) =>
        ExecuteTransactionControlAsync("COMMIT;", cancellationToken);

    public Task RollbackTransactionAsync(CancellationToken cancellationToken = default) =>
        ExecuteTransactionControlAsync("ROLLBACK;", cancellationToken);

    private async Task ExecuteTransactionControlAsync(string sql, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await OpenCoreAsync(cancellationToken);
            if (!ReadTransactionState(_connection!).HasTransaction)
            {
                // The shell only offers commit/rollback while its guard says a transaction is open;
                // racing it to the finish line must not turn into a SQL error.
                return;
            }

            await using var command = _connection!.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ helpers

    private static string QuoteIdentifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

    /// <summary>
    /// A literal for a PRAGMA argument. PRAGMA does not take parameters, so the value is embedded;
    /// single-quoting with doubling keeps table names containing quotes intact.
    /// </summary>
    private static string QuoteString(string value) => $"'{value.Replace("'", "''")}'";

    public async ValueTask DisposeAsync()
    {
        try
        {
            await CloseAsync();
        }
        catch
        {
            // Disposal never throws: the app is shutting the session down either way.
        }

        _gate.Dispose();
    }
}
