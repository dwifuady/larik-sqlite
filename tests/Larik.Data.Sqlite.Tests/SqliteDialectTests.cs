using Larik.Data;
using Larik.Data.Sqlite;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>
/// Editor-language golden tests for the SQLite provider: SQLite quoting, parameters, comments and
/// the trigger rule that keeps a <c>BEGIN … END</c> body in one statement.
/// </summary>
public sealed class SqliteDialectTests
{
    private static readonly SqliteDialect Sqlite = SqliteDialect.Instance;

    private static string[] Statements(string text) =>
        Sqlite.Split(text)
            .Select(span => text.Substring(span.Start, span.Length))
            .ToArray();

    // ------------------------------------------------------------------ splitting

    [Fact]
    public void Splits_statements_on_semicolons_and_keeps_the_terminator()
    {
        var statements = Statements("SELECT 1; SELECT * FROM \"users\"");

        Assert.Equal(["SELECT 1;", "SELECT * FROM \"users\""], statements);
    }

    [Fact]
    public void There_is_no_go_batch_separator()
    {
        // GO is a SQL Server client separator; SQLite has no such batch boundary.
        Assert.Single(Sqlite.Split("SELECT 1 GO SELECT 2"));
    }

    [Fact]
    public void Ignores_semicolons_inside_strings_and_comments()
    {
        Assert.Single(Sqlite.Split("SELECT 'a;b';"));
        Assert.Single(Sqlite.Split("-- a;b\nSELECT 1"));
        Assert.Single(Sqlite.Split("/* a;b */ SELECT 1"));
    }

    [Fact]
    public void Ignores_semicolons_inside_every_identifier_quote_form()
    {
        Assert.Single(Sqlite.Split("SELECT \"a;b\" FROM \"t\";"));
        Assert.Single(Sqlite.Split("SELECT `a;b` FROM `t`;"));
        Assert.Single(Sqlite.Split("SELECT [a;b] FROM [t];"));
    }

    [Fact]
    public void A_trigger_body_stays_with_its_create_statement()
    {
        const string document = """
            CREATE TRIGGER audit_users AFTER UPDATE ON users
            WHEN NEW.active = 1
            BEGIN
                INSERT INTO audit VALUES (OLD.id, 'a;b');
                UPDATE stats SET n = CASE WHEN n > 0 THEN n + 1 ELSE 1 END;
            END;
            SELECT 1;
            """;

        var statements = Statements(document);

        Assert.Equal(2, statements.Length);
        Assert.StartsWith("CREATE TRIGGER", statements[0]);
        Assert.EndsWith("END;", statements[0]);
        Assert.Contains("CASE WHEN n > 0", statements[0]);
        Assert.Equal("SELECT 1;", statements[1]);
    }

    [Fact]
    public void A_single_statement_trigger_is_not_merged_with_what_follows()
    {
        var statements = Statements("""
            CREATE TRIGGER set_updated AFTER UPDATE ON t
            UPDATE t SET changed = 1;
            SELECT 1;
            """);

        Assert.Equal(2, statements.Length);
        Assert.Equal("SELECT 1;", statements[1]);
    }

    [Fact]
    public void Consecutive_triggers_keep_their_own_bodies()
    {
        var statements = Statements("""
            CREATE TEMP TRIGGER one AFTER INSERT ON a BEGIN
                INSERT INTO log VALUES ('a');
            END;
            CREATE TRIGGER two AFTER INSERT ON b BEGIN
                INSERT INTO log VALUES ('b');
            END;
            SELECT 1;
            """);

        Assert.Equal(3, statements.Length);
        Assert.StartsWith("CREATE TEMP TRIGGER", statements[0]);
        Assert.StartsWith("CREATE TRIGGER", statements[1]);
        Assert.Equal("SELECT 1;", statements[2]);
    }

    [Fact]
    public void Statement_at_caret_inside_a_trigger_returns_the_whole_trigger()
    {
        const string document = """
            CREATE TRIGGER t AFTER INSERT ON a BEGIN
                INSERT INTO log VALUES (1);
                INSERT INTO log VALUES (2);
            END;
            SELECT 1;
            """;

        var caret = document.IndexOf("VALUES (2)", StringComparison.Ordinal);
        var statement = Sqlite.StatementAt(document, caret);
        var text = document.Substring(statement.Start, statement.Length);

        Assert.StartsWith("CREATE TRIGGER", text);
        Assert.EndsWith("END;", text);
    }

    // ------------------------------------------------------------------ tokenizing

    [Fact]
    public void Paints_keywords_and_functions()
    {
        var tokens = Sqlite.Tokenize("SELECT COUNT(*) FROM users WHERE active = 1");

        Assert.Contains(tokens, token => token is { Kind: QueryTokenKind.Keyword, Start: 0, Length: 6 });
        Assert.Contains(tokens, token => token is { Kind: QueryTokenKind.Function, Start: 7, Length: 5 });
        Assert.Contains(tokens, token => token is { Kind: QueryTokenKind.Keyword, Start: 16, Length: 4 }); // FROM
    }

    [Fact]
    public void Paints_named_parameters_as_variables()
    {
        const string text = "SELECT @id, :name, $value";

        static string[] Variables(string source, IReadOnlyList<QueryToken> tokens) =>
            tokens.Where(token => token.Kind == QueryTokenKind.Variable)
                .Select(token => source.Substring(token.Start, token.Length))
                .ToArray();

        Assert.Equal(["@id", ":name", "$value"], Variables(text, Sqlite.Tokenize(text)));
    }

    [Fact]
    public void Quoted_identifiers_are_not_painted_as_strings()
    {
        const string text = "SELECT \"a;b\", [c;d] FROM \"users\"";
        var tokens = Sqlite.Tokenize(text);

        Assert.DoesNotContain(tokens, token => token.Kind == QueryTokenKind.String);
        Assert.DoesNotContain(tokens, token => text.Substring(token.Start, token.Length) == "[c;d]");
    }

    [Fact]
    public void Single_quoted_strings_stay_strings_and_escape_by_doubling()
    {
        const string text = "SELECT 'a''b'";
        var token = Assert.Single(Sqlite.Tokenize(text), token => token.Kind == QueryTokenKind.String);

        Assert.Equal("'a''b'", text.Substring(token.Start, token.Length));
    }

    // ------------------------------------------------------------------ completion metadata

    [Fact]
    public void Completion_metadata_covers_the_sqlite_surface()
    {
        Assert.Contains("SELECT", Sqlite.Keywords);
        Assert.Contains("PRAGMA", Sqlite.Keywords);
        Assert.Contains("CREATE TRIGGER", Sqlite.Keywords);
        Assert.Contains("GROUP_CONCAT", Sqlite.Functions);
        Assert.Contains("JSON_EXTRACT", Sqlite.Functions);
        Assert.Contains("FROM", Sqlite.TableContextKeywords);
        Assert.Contains("ATTACH", Sqlite.TableContextKeywords);
    }

    [Fact]
    public void Quotes_identifiers_with_double_quotes()
    {
        Assert.Equal("\"users\"", Sqlite.QuoteIdentifier("users"));
        Assert.Equal("\"a\"\"b\"", Sqlite.QuoteIdentifier("a\"b"));
    }

    [Fact]
    public void Preview_and_lookup_use_limit()
    {
        Assert.Equal("SELECT * FROM \"users\" LIMIT 100", Sqlite.PreviewStatement(null, "users", 100));

        Assert.Equal(
            "SELECT * FROM \"books\" WHERE \"author_id\" = 7 LIMIT 25",
            Sqlite.RowsStatement(null, "books", [new RowFilter("author_id", "7", "INTEGER")], null, 25));

        Assert.Equal(
            "SELECT * FROM \"extra\".\"notes\" LIMIT 200",
            Sqlite.RowsStatement("extra", "notes", [], orderByColumn: null, 200));
    }

    [Fact]
    public void Format_literal_keeps_numbers_bare_and_quotes_text()
    {
        Assert.Equal("42", Sqlite.FormatLiteral("42", "INTEGER"));
        Assert.Equal("3.5", Sqlite.FormatLiteral("3.5", "REAL"));
        Assert.Equal("'007'", Sqlite.FormatLiteral("007", "TEXT"));
        Assert.Equal("'it''s'", Sqlite.FormatLiteral("it's", "TEXT"));
        Assert.Equal("NULL", Sqlite.FormatLiteral(null, "INTEGER"));
        Assert.Equal("X'00FF'", Sqlite.FormatLiteral("X'00FF'", "BLOB"));
        Assert.Equal("X'00FF'", Sqlite.FormatLiteral("0x00FF", "BLOB"));
        Assert.Equal("1", Sqlite.FormatLiteral("true", "BOOLEAN"));
    }

    // ------------------------------------------------------------------ provider wiring

    [Fact]
    public void Provider_exposes_the_sqlite_dialect()
    {
        var provider = new SqliteProvider();

        Assert.Equal("sqlite", provider.Id);
        Assert.Equal("SQLite", provider.DisplayName);
        Assert.Equal("sqlite", provider.Dialect);
        Assert.Same(SqliteDialect.Instance, provider.EditorLanguage);
        Assert.Same(SqliteDialect.Instance, EditorLanguageHelpers.Resolve(provider));
    }

    [Fact]
    public void Provider_describes_the_file_instead_of_a_server()
    {
        var provider = new SqliteProvider();

        var file = new ConnectionProfile { ProviderId = SqliteProvider.ProviderId };
        file.Values["file"] = @"C:\data\app.db";

        var memory = new ConnectionProfile { ProviderId = SqliteProvider.ProviderId };
        memory.Values["file"] = ":memory:";

        Assert.Equal(@"SQLite · app.db", provider.DescribeEndpoint(file));
        Assert.Equal("SQLite · in-memory", provider.DescribeEndpoint(memory));
        Assert.Null(provider.DescribeEndpoint(new ConnectionProfile { ProviderId = SqliteProvider.ProviderId }));
        Assert.Null(provider.DescribeIdentity(file));
    }

    [Fact]
    public void The_database_file_is_the_only_required_setting()
    {
        var provider = new SqliteProvider();

        var file = Assert.Single(provider.Settings, setting => setting.Key == "file");
        Assert.True(file.Required);
        Assert.All(provider.Settings.Where(setting => setting.Key != "file"), setting => Assert.False(setting.Required));
    }
}
