# Larik SQLite add-on

A SQLite provider for [Larik SQL Studio](https://github.com/dwifuady/larik-winui). It opens a local
database file and gives the editor the engine's own SQL, including parameters, `PRAGMA`, views,
triggers and transactions.

This repository is the add-on's home. It consumes the `Larik.Data` contract package, not a project
reference, so it can be released independently of the app.

## Connect

Set **Database file** to the full path of a `.db` file, for example `C:\data\app.db`. Relative paths
are rejected because they would resolve against the app's working directory, which is not what the
path in the dialog appears to mean.

| Setting | Default | Notes |
| --- | --- | --- |
| Database file | — | Required. `:memory:` opens a private in-memory database. |
| Read-only | off | Opens the file without write access and reports a missing file instead of creating it. |
| Create if missing | on (Advanced) | Turn off to fail fast on a typo in the path. |
| Foreign keys | on (Advanced) | Runs `PRAGMA foreign_keys` for this connection. |
| Busy timeout (s) | 5 (Advanced) | How long to wait for another connection's lock. `0` fails immediately. |

One query tab is one connection, so a `BEGIN`, `ATTACH`, temp table or `PRAGMA` belongs to that tab.
`:memory:` is therefore private per tab, not a shared scratch database. File databases are shared
with any other process using the same file; the busy timeout covers short lock contention.

**Attached files.** `ATTACH DATABASE 'other.db' AS extra` works, and `extra` tables appear in the
schema browser and completion as `"extra"."table"`. The database picker stays on `main`: SQLite has
no `USE`, and unqualified names always resolve to `main`, so a "switch database" action would be a
lie. Query attached tables with their qualifier.

There is no login or password field. SQLite encrypts nothing by itself, and the bundled native
`e_sqlite3` build does not include SQLCipher; a password setting would only suggest protection that
does not exist.

## What works

- **SQL**: the engine's SQL, including `PRAGMA`, `ATTACH`/`DETACH`, `VACUUM`,
  `EXPLAIN QUERY PLAN`, `UPSERT` and CTEs. Parameter syntax (`:id`, `@id`, `$id`) is highlighted; the
  studio sends statements as text, so parameters are not bound to values.
- **Schema**: tables, views, columns, declared types, nullability, primary keys and foreign keys,
  including composite keys and `REFERENCES parent` written without a target column. Virtual tables
  are listed; their FTS shadow tables are hidden.
- **Transactions**: `BEGIN`, `COMMIT`, `ROLLBACK` and savepoints. The toolbar's commit/rollback
  actions read `sqlite3_get_autocommit` from the live handle, so they reflect the session exactly.
  A failed statement does **not** abort a SQLite transaction.
- **Triggers**: a `CREATE TRIGGER … BEGIN … END` body is kept as one statement even though it
  contains semicolons. This applies to both F5 and Ctrl+Enter.
- **Results**: the provider row cap (`EffectiveMaxRows`) truncates and reports it, blobs are shown
  as `X'…'`, and statement errors become result tabs instead of crashing the session.

Known limits, on purpose:

- No stored procedures or functions: SQLite has none. The routine list is empty.
- No row-count estimate: SQLite has no metadata counter, so a foreign-key reference preview uses the
  filtered lookup path.
- The provider does not change the journal mode. A database already in WAL keeps WAL; a fresh one
  keeps SQLite's default.
- The native engine is the package's `e_sqlite3` build, pinned per `win-x64`/`win-arm64` release zip.

## Build and test

```powershell
dotnet restore
dotnet test tests\Larik.Data.Sqlite.Tests\Larik.Data.Sqlite.Tests.csproj
```

The tests create and delete their own temporary database files; nothing needs to be installed.

## Package

```powershell
dotnet restore
pwsh ./scripts/package.ps1 -Version 1.0.0 -Rid win-x64
```

The script publishes the provider, removes the host's `Larik.Data.dll`, adds `larik-addon.json`,
`LICENSE` and `THIRD-PARTY-NOTICES.md`, and writes `out/larik.sqlite-<version>-<rid>.zip`. The zip
contains the managed assemblies and the RID's native `e_sqlite3.dll`.

## Release

Push a `v*` tag (for example `v1.0.0`). The workflow runs the tests, packages `win-x64` and
`win-arm64`, and attaches both zips plus `SHA256SUMS.txt` to a GitHub Release.

After the release, add its URL and SHA-256 to the
[`larik-addons`](https://github.com/dwifuady/larik-addons) index.

## Contract

Built against `Larik.Data` 1.0.0 and declares `>=1.0 <2.0` in `addon/larik-addon.json`. The app
binds the contract to its own assembly, so the add-on must not ship `Larik.Data.dll`.

The provider contract and the add-on format are documented in
[`docs/ADDONS.md`](https://github.com/dwifuady/larik-winui/blob/main/docs/ADDONS.md).
