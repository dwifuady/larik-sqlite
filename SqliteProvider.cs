using Larik.Data;
using Microsoft.Data.Sqlite;

namespace Larik.Data.Sqlite;

/// <summary>
/// SQLite provider. A connection is one database file; the shell and editor talk only to the
/// contracts, and this assembly owns the file handling and the SQLite dialect. Nothing in the app
/// knows SQLite exists.
/// </summary>
public sealed class SqliteProvider : IDatabaseProvider
{
    public const string ProviderId = "sqlite";

    public string Id => ProviderId;

    public string DisplayName => "SQLite";

    public string Dialect => ProviderId;

    public IEditorLanguage? EditorLanguage => SqliteDialect.Instance;

    public string? DescribeEndpoint(ConnectionProfile profile)
    {
        var file = SqliteProfile.ResolveFile(profile);
        return file is null ? null : $"SQLite · {SqliteProfile.DescribeFile(file)}";
    }

    /// <summary>SQLite has no login; the identity line is redundant with the file name.</summary>
    public string? DescribeIdentity(ConnectionProfile profile) => null;

    public IReadOnlyList<ProviderSetting> Settings { get; } =
    [
        new()
        {
            Key = "file",
            Label = "Database file",
            Required = true,
            Placeholder = @"C:\data\app.db",
            Hint = "Full path to a SQLite file. Use :memory: for a private in-memory database (one per query tab).",
        },
        new()
        {
            Key = "readOnly",
            Label = "Read-only",
            Kind = ProviderSettingKind.Boolean,
            DefaultValue = "false",
            Hint = "Open the file without write access. A missing file is reported instead of created.",
        },
        new()
        {
            Key = "createIfMissing",
            Label = "Create if missing",
            Kind = ProviderSettingKind.Boolean,
            DefaultValue = "true",
            IsAdvanced = true,
            Hint = "Create the file when it does not exist. Turn off to fail fast on a typo in the path.",
        },
        new()
        {
            Key = "foreignKeys",
            Label = "Foreign keys",
            Kind = ProviderSettingKind.Boolean,
            DefaultValue = "true",
            IsAdvanced = true,
            Hint = "Enforce foreign-key constraints for this connection (PRAGMA foreign_keys).",
        },
        new()
        {
            Key = "busyTimeout",
            Label = "Busy timeout (s)",
            Kind = ProviderSettingKind.Number,
            DefaultValue = "5",
            IsAdvanced = true,
            Hint = "How long to wait for another connection's lock before reporting 'database is locked'. 0 fails immediately.",
        },
    ];

    public IDatabaseConnection CreateConnection(ConnectionProfile profile) => new SqliteConnectionSession(profile);
}

/// <summary>
/// Turns the saved profile into a connection string, with the checks a file-based engine needs and
/// the dialog path cannot express. Kept separate from the session so it is unit-testable.
/// </summary>
internal static class SqliteProfile
{
    private const string MemoryFile = ":memory:";

    public static string? ResolveFile(ConnectionProfile profile)
    {
        var raw = profile.Get("file");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var file = Environment.ExpandEnvironmentVariables(raw.Trim());

        // A path copied from Explorer often arrives wrapped in quotes.
        if (file.Length >= 2 && file[0] == '"' && file[^1] == '"')
        {
            file = file[1..^1].Trim();
        }

        return file.Length == 0 ? null : file;
    }

    public static bool IsMemory(string file) =>
        string.Equals(file, MemoryFile, StringComparison.OrdinalIgnoreCase);

    public static SqliteConnectionStringBuilder Build(ConnectionProfile profile)
    {
        var file = ResolveFile(profile);
        if (file is null)
        {
            throw new InvalidOperationException(
                "The SQLite connection needs a database file path, or :memory:.");
        }

        var memory = IsMemory(file);
        var readOnly = !memory && profile.GetBool("readOnly", false);
        var create = !memory && profile.GetBool("createIfMissing", true);

        if (!memory)
        {
            if (!Path.IsPathFullyQualified(file))
            {
                throw new InvalidOperationException(
                    $"Use a full path for the SQLite database file, not '{file}'.");
            }

            var full = Path.GetFullPath(file);
            var exists = File.Exists(full);

            if (!exists && (readOnly || !create))
            {
                var hint = readOnly
                    ? "Check the path, or turn off Read-only."
                    : "Check the path, or turn on Create if missing.";
                throw new InvalidOperationException($"The database file '{full}' does not exist. {hint}");
            }

            var directory = Path.GetDirectoryName(full);
            if (!exists && create && !string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                throw new InvalidOperationException(
                    $"The folder '{directory}' does not exist. Create it first, or choose a file in an existing folder.");
            }

            file = full;
        }

        return new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = memory
                ? SqliteOpenMode.Memory
                : readOnly
                    ? SqliteOpenMode.ReadOnly
                    : create
                        ? SqliteOpenMode.ReadWriteCreate
                        : SqliteOpenMode.ReadWrite,
            // A pooled in-memory connection would outlive the tab and quietly share state.
            Pooling = !memory,
            DefaultTimeout = Math.Max(0, profile.GetInt("busyTimeout", 5)),
            ForeignKeys = profile.GetBool("foreignKeys", true),
        };
    }

    public static string DescribeFile(string file) =>
        IsMemory(file) ? "in-memory" : Path.GetFileName(file);
}
