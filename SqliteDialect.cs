using System.Globalization;
using Larik.Data;

namespace Larik.Data.Sqlite;

/// <summary>
/// SQLite as this app's editor understands it: single-quoted strings, double-quoted (or bracketed)
/// identifiers, <c>--</c> and <c>/* */</c> comments, <c>:name</c>/<c>@name</c>/<c>$name</c>
/// parameters and <c>LIMIT</c>. Owned by the provider assembly on purpose; the shell has no SQLite
/// keyword, quoting or splitting knowledge of its own.
/// <para>
/// Tokenizing and splitting come from the shared <see cref="SqlLanguage"/> engine. The only override
/// is <see cref="Split"/>: a <c>CREATE TRIGGER … BEGIN … END</c> body contains semicolons that are
/// part of one statement, not statement boundaries.
/// </para>
/// </summary>
public sealed class SqliteDialect : SqlLanguage, ISqlDialect
{
    /// <summary>
    /// Words the editor paints as keywords. This is SQLite's keyword list minus the words that only
    /// ever appear inside pragma arguments, plus the statement and clause words users actually type.
    /// </summary>
    private static readonly IReadOnlySet<string> SqliteTokenKeywords = new HashSet<string>(
        new[]
        {
            "ABORT", "ACTION", "ADD", "AFTER", "ALL", "ALTER", "ALWAYS", "ANALYZE", "AND", "AS",
            "ASC", "ATTACH", "AUTOINCREMENT", "BEFORE", "BEGIN", "BETWEEN", "BY", "CASCADE", "CASE",
            "CAST", "CHECK", "COLLATE", "COLUMN", "COMMIT", "CONFLICT", "CONSTRAINT", "CREATE",
            "CROSS", "CURRENT", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "DATABASE",
            "DEFAULT", "DEFERRABLE", "DEFERRED", "DELETE", "DESC", "DETACH", "DISTINCT", "DO",
            "DROP", "EACH", "ELSE", "END", "ESCAPE", "EXCEPT", "EXCLUDE", "EXCLUSIVE", "EXISTS",
            "EXPLAIN", "FAIL", "FILTER", "FIRST", "FOLLOWING", "FOR", "FOREIGN", "FROM", "FULL",
            "GENERATED", "GLOB", "GROUP", "GROUPS", "HAVING", "IF", "IGNORE", "IMMEDIATE", "IN",
            "INDEX", "INDEXED", "INITIALLY", "INNER", "INSERT", "INSTEAD", "INTERSECT", "INTO", "IS",
            "ISNULL", "JOIN", "KEY", "LAST", "LEFT", "LIKE", "LIMIT", "MATCH", "MATERIALIZED",
            "NATURAL", "NO", "NOT", "NOTHING", "NOTNULL", "NULL", "NULLS", "OF", "OFFSET", "ON",
            "OR", "ORDER", "OTHERS", "OUTER", "OVER", "PARTITION", "PLAN", "PRAGMA", "PRECEDING",
            "PRIMARY", "QUERY", "RAISE", "RANGE", "RECURSIVE", "REFERENCES", "REGEXP", "REINDEX",
            "RELEASE", "RENAME", "REPLACE", "RESTRICT", "RETURNING", "RIGHT", "ROLLBACK", "ROW",
            "ROWS", "SAVEPOINT", "SELECT", "SET", "TABLE", "TEMP", "TEMPORARY", "THEN", "TIES",
            "TO", "TRANSACTION", "TRIGGER", "UNBOUNDED", "UNION", "UNIQUE", "UPDATE", "USING",
            "VACUUM", "VALUES", "VIEW", "VIRTUAL", "WHEN", "WHERE", "WINDOW", "WITH", "WITHOUT",
        },
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] CompletionKeywords =
    [
        "SELECT", "FROM", "WHERE", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN",
        "NATURAL JOIN", "ON", "GROUP BY", "ORDER BY", "HAVING", "LIMIT", "OFFSET", "DISTINCT", "AS",
        "AND", "OR", "NOT", "NULL", "IS NULL", "IS NOT NULL", "IN", "EXISTS", "BETWEEN", "LIKE",
        "GLOB", "CASE WHEN", "ELSE", "END", "UNION", "UNION ALL", "INTERSECT", "EXCEPT", "WITH",
        "RECURSIVE", "INSERT INTO", "VALUES", "ON CONFLICT", "RETURNING", "UPDATE", "SET",
        "DELETE FROM", "CREATE TABLE", "CREATE TEMP TABLE", "CREATE INDEX", "CREATE UNIQUE INDEX",
        "CREATE VIEW", "CREATE TRIGGER", "CREATE VIRTUAL TABLE", "ALTER TABLE", "DROP TABLE",
        "DROP INDEX", "DROP VIEW", "DROP TRIGGER", "ATTACH DATABASE", "DETACH DATABASE", "PRAGMA",
        "VACUUM", "ANALYZE", "REINDEX", "BEGIN", "BEGIN IMMEDIATE", "COMMIT", "ROLLBACK",
        "SAVEPOINT", "RELEASE", "EXPLAIN QUERY PLAN",
        "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "IFNULL", "NULLIF", "IIF", "CAST",
        "ROW_NUMBER", "OVER", "PARTITION BY", "DATE", "DATETIME", "STRFTIME", "JSON_EXTRACT",
        "JSON_EACH", "GROUP_CONCAT",
    ];

    private static readonly string[] CompletionFunctions =
    [
        "ABS", "AVG", "CEIL", "CEILING", "CHANGES", "CHAR", "COALESCE", "CONCAT", "CONCAT_WS",
        "COUNT", "CUME_DIST", "DATE", "DATETIME", "DENSE_RANK", "FIRST_VALUE", "FLOOR", "FORMAT",
        "GLOB", "GROUP_CONCAT", "HEX", "IFNULL", "IIF", "INSTR", "JSON", "JSON_ARRAY", "JSON_ARRAY_LENGTH",
        "JSON_EACH", "JSON_EXTRACT", "JSON_GROUP_ARRAY", "JSON_GROUP_OBJECT", "JSON_OBJECT",
        "JSON_PATCH", "JSON_QUOTE", "JSON_REMOVE", "JSON_REPLACE", "JSON_SET", "JSON_TREE",
        "JSON_TYPE", "JSON_VALID", "JULIANDAY", "LAG", "LAST_INSERT_ROWID", "LAST_VALUE", "LEAD",
        "LENGTH", "LIKELIHOOD", "LIKELY", "LOWER", "LTRIM", "MAX", "MIN", "NTH_VALUE", "NTILE",
        "NULLIF", "OCTET_LENGTH", "PERCENT_RANK", "PRINTF", "QUOTE", "RANDOM", "RANDOMBLOB", "RANK",
        "REPLACE", "ROUND", "ROW_NUMBER", "RTRIM", "SIGN", "SOUNDEX", "SQLITE_SOURCE_ID",
        "SQLITE_VERSION", "STRFTIME", "SUBSTR", "SUBSTRING", "SUM", "TOTAL", "TOTAL_CHANGES",
        "TRIM", "TYPEOF", "UNICODE", "UNIXEPOCH", "UPPER", "ZEROBLOB",
    ];

    private static readonly string[] CompletionTableContext =
    [
        "FROM", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "OUTER JOIN",
        "CROSS JOIN", "NATURAL JOIN", "INTO", "UPDATE", "TABLE", "DELETE FROM", "ATTACH",
    ];

    public static SqliteDialect Instance { get; } = new();

    private SqliteDialect()
        : base(new SqlLanguageOptions
        {
            TokenKeywords = SqliteTokenKeywords,
            // SQLite accepts [x] and `x` as identifiers for SQL Server/MySQL compatibility.
            BracketIdentifiers = true,
            UnicodeStringPrefix = false,
            // :name, @name and $name are bound parameters; $ is also legal inside an identifier.
            IdentifierStartCharacters = ['@', ':', '$'],
            IdentifierPartCharacters = ['$'],
            VariablePrefixes = ['@', ':', '$'],
            // A double quote is an identifier (or, with DQS on, a string); it is never a SQLite
            // string delimiter, so it is skipped but not coloured.
            StringQuotes = ['\''],
            QuotedIdentifierQuotes = ['"', '`'],
            DollarQuotedStrings = false,
            EscapeStringPrefix = false,
            NestedBlockComments = false,
            LineCommentPrefixes = ["--"],
            BlockCommentOpen = "/*",
            BlockCommentClose = "*/",
            BatchSeparator = null,
            SplitOnSemicolon = true,
        })
    {
    }

    public override string Id => "sqlite";

    public IReadOnlyList<string> Keywords => CompletionKeywords;

    public IReadOnlyList<string> Functions => CompletionFunctions;

    public IReadOnlyList<string> TableContextKeywords => CompletionTableContext;

    /// <summary>
    /// The shared splitter, plus one SQLite rule: semicolons inside a trigger body do not end the
    /// <c>CREATE TRIGGER</c> statement. Without this, executing the document would try to run half a
    /// trigger and report a syntax error.
    /// </summary>
    public override IReadOnlyList<StatementSpan> Split(string? text)
    {
        var statements = base.Split(text);
        if (statements.Count < 2)
        {
            return statements;
        }

        var source = text!;
        var merged = new List<StatementSpan>(statements.Count);
        var index = 0;

        while (index < statements.Count)
        {
            var first = statements[index];
            if (!IsCreateTrigger(source.AsSpan(first.Start, first.Length)))
            {
                merged.Add(first);
                index++;
                continue;
            }

            var depth = 0;
            var sawBegin = false;
            var end = first.End;
            ScanTriggerWords(source.AsSpan(first.Start, first.Length), ref depth, ref sawBegin);
            index++;

            // A trigger without BEGIN is a single statement and was already split correctly.
            while (sawBegin && depth > 0 && index < statements.Count)
            {
                var next = statements[index];
                ScanTriggerWords(source.AsSpan(next.Start, next.Length), ref depth, ref sawBegin);
                end = next.End;
                index++;
            }

            merged.Add(new StatementSpan(first.Start, end - first.Start));
        }

        return merged;
    }

    /// <summary><c>SELECT * FROM "public"."orders" LIMIT 100</c>; the schema browser's preview.</summary>
    public string PreviewStatement(string? schema, string name, int limit) =>
        RowsStatement(schema, name, [], orderByColumn: null, limit);

    public string RowsStatement(
        string? schema,
        string table,
        IReadOnlyList<RowFilter> filters,
        string? orderByColumn,
        int limit)
    {
        var where = filters.Count == 0
            ? string.Empty
            : " WHERE " + string.Join(
                " AND ",
                filters.Select(filter => filter.Value is null
                    ? $"{QuoteIdentifier(filter.Column)} IS NULL"
                    : $"{QuoteIdentifier(filter.Column)} = {FormatLiteral(filter.Value, filter.DataType)}"));

        var order = string.IsNullOrWhiteSpace(orderByColumn)
            ? string.Empty
            : $" ORDER BY {QuoteIdentifier(orderByColumn)}";

        return $"SELECT * FROM {QualifiedName(schema, table)}{where}{order} LIMIT {Math.Max(1, limit)}";
    }

    /// <summary>
    /// SQLite literals: numbers stay bare when the declared column has a numeric affinity, blobs
    /// keep their <c>X'…'</c> form, and everything else is a single-quoted string. The grid's values
    /// are already provider-formatted, so this converts back to a literal rather than parsing
    /// arbitrary user input.
    /// </summary>
    public string FormatLiteral(string? value, string? dataType)
    {
        if (value is null)
        {
            return "NULL";
        }

        var type = (dataType ?? string.Empty).Trim().ToLowerInvariant();

        // X'00FF' (our grid form) and 0x00FF (what a user may type) are blobs, not text.
        if (value.StartsWith("X'", StringComparison.OrdinalIgnoreCase) && value.EndsWith('\''))
        {
            return value;
        }

        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && value.Length > 2)
        {
            return $"X'{value[2..]}'";
        }

        if (type is "boolean" or "bool")
        {
            return value switch
            {
                "true" => "1",
                "false" => "0",
                // The grid already carries SQLite's 0/1 representation; anything else is not a
                // boolean literal, so it must not be silently rewritten.
                _ => value,
            };
        }

        if (HasNumericAffinity(type)
            && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return value;
        }

        return $"'{value.Replace("'", "''")}'";
    }

    public string QuoteIdentifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

    private string QualifiedName(string? schema, string name) =>
        string.IsNullOrWhiteSpace(schema)
            ? QuoteIdentifier(name)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(name)}";

    /// <summary>
    /// SQLite's type-affinity rules, so a value is only emitted bare when the column would store it
    /// as a number. TEXT and BLOB affinities always quote (a TEXT column can legitimately contain
    /// "007", and emitting 7 would silently change it).
    /// </summary>
    private static bool HasNumericAffinity(string type)
    {
        if (type.Contains("int", StringComparison.Ordinal))
        {
            return true;
        }

        if (type.Contains("char", StringComparison.Ordinal)
            || type.Contains("clob", StringComparison.Ordinal)
            || type.Contains("text", StringComparison.Ordinal)
            || type.Contains("blob", StringComparison.Ordinal))
        {
            return false;
        }

        // Empty declarations are BLOB affinity; REAL and NUMERIC (DECIMAL, DATE, BOOLEAN, …) are numeric.
        return type.Length > 0;
    }

    // ------------------------------------------------------------------ trigger-aware splitting

    private static bool IsCreateTrigger(ReadOnlySpan<char> statement)
    {
        var index = 0;
        if (!NextWord(statement, ref index).Equals("CREATE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var second = NextWord(statement, ref index);
        if (second.Equals("TEMP", StringComparison.OrdinalIgnoreCase)
            || second.Equals("TEMPORARY", StringComparison.OrdinalIgnoreCase))
        {
            second = NextWord(statement, ref index);
        }

        return second.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Counts BEGIN/CASE openers against END closers, ignoring strings, quoted identifiers, bracketed
    /// identifiers and comments. CASE counts as an opener so a <c>CASE … END</c> inside a trigger body
    /// does not make the body look finished.
    /// </summary>
    private static void ScanTriggerWords(ReadOnlySpan<char> text, ref int depth, ref bool sawBegin)
    {
        var index = 0;

        while (index < text.Length)
        {
            var current = text[index];

            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '-' && index + 1 < text.Length && text[index + 1] == '-')
            {
                while (index < text.Length && text[index] is not ('\n' or '\r'))
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var close = text[(index + 2)..].IndexOf("*/", StringComparison.Ordinal);
                index = close < 0 ? text.Length : index + 2 + close + 2;
                continue;
            }

            if (current is '\'' or '"' or '`')
            {
                index = SkipQuoted(text, index, current);
                continue;
            }

            if (current == '[')
            {
                index = SkipBracket(text, index);
                continue;
            }

            if (char.IsLetter(current) || current == '_')
            {
                var start = index;
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_'))
                {
                    index++;
                }

                var word = text[start..index];
                if (word.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
                {
                    depth++;
                    sawBegin = true;
                }
                else if (word.Equals("CASE", StringComparison.OrdinalIgnoreCase))
                {
                    depth++;
                }
                else if (word.Equals("END", StringComparison.OrdinalIgnoreCase))
                {
                    depth--;
                }

                continue;
            }

            index++;
        }
    }

    /// <summary>Reads the next bare word, skipping whitespace and the two SQLite comment forms.</summary>
    private static ReadOnlySpan<char> NextWord(ReadOnlySpan<char> text, ref int index)
    {
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            if (index + 1 < text.Length && text[index] == '-' && text[index + 1] == '-')
            {
                while (index < text.Length && text[index] is not ('\n' or '\r'))
                {
                    index++;
                }

                continue;
            }

            if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '*')
            {
                var close = text[(index + 2)..].IndexOf("*/", StringComparison.Ordinal);
                index = close < 0 ? text.Length : index + 2 + close + 2;
                continue;
            }

            break;
        }

        var start = index;
        while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_'))
        {
            index++;
        }

        return text[start..index];
    }

    private static int SkipQuoted(ReadOnlySpan<char> text, int index, char quote)
    {
        index++;
        while (index < text.Length)
        {
            if (text[index] != quote)
            {
                index++;
                continue;
            }

            // SQL escapes a quote by doubling it.
            if (index + 1 < text.Length && text[index + 1] == quote)
            {
                index += 2;
                continue;
            }

            return index + 1;
        }

        return text.Length;
    }

    private static int SkipBracket(ReadOnlySpan<char> text, int index)
    {
        index++;
        while (index < text.Length)
        {
            if (text[index] != ']')
            {
                index++;
                continue;
            }

            if (index + 1 < text.Length && text[index + 1] == ']')
            {
                index += 2;
                continue;
            }

            return index + 1;
        }

        return text.Length;
    }
}
