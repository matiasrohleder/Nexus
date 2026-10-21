# Nexus

A **lightweight, cross-OS database administrator**. Built with **C# / .NET 10 + Avalonia UI**
(native desktop on Linux, Windows and macOS — not a browser shell).

![Nexus](docs/nexus-screenshot.png)

## Supported engines (10)

| Engine | Query | Explorer | Row editing | Sessions/Kill | Users | Activity profiler | Backup |
|---|---|---|---|---|---|---|---|
| PostgreSQL | SQL | ✔ | ✔ | ✔ | ✔ | ✔ pg_stat_activity | pg_dump |
| MySQL | SQL | ✔ | ✔ | ✔ | ✔ | ✔ processlist | mysqldump |
| MariaDB | SQL | ✔ | ✔ | ✔ | ✔ | ✔ processlist | mariadb-dump |
| SQL Server | SQL | ✔ | ✔ | ✔ | ✔ | ✔ dm_exec_requests | hint |
| Oracle | SQL | ✔ | ✔ | ✔ | ✔ | ✔ v$session | hint |
| SQLite | SQL | ✔ | ✔ | — | — | — | sqlite3 |
| DuckDB | SQL | ✔ | ✔ | — | — | — | ✔ built-in |
| ClickHouse | SQL | ✔ | read-only | ✔ | ✔ | ✔ system.processes | hint |
| MongoDB | mongosh-style | ✔ collections | — | ✔ killOp | ✔ | ✔ currentOp | mongodump |
| Redis | commands + INSPECT | ✔ keys (SCAN) | — | ✔ CLIENT KILL | ✔ ACL | ✔ SLOWLOG | redis-cli |

Every engine is exercised by an automated end-to-end test matrix against real instances
(`--selftest <engine>`): connect, CRUD, introspection, PK detection, DDL, stats, sessions,
transactions with rollback.

## Features

### Connections
- **Encrypted at rest** (AES-256-GCM) — passwords never touch disk in clear text.
- Redesigned dialog: engine tiles, **smart defaults per engine** (port/user/database),
  **paste a URL** (`postgres://user:pass@host/db`, `mongodb://…`, ADO.NET strings) to auto-fill,
  file picker for SQLite/DuckDB, color swatches, Enter = Test.

### Query editor
- Multi-tab, syntax highlighting, line numbers; **F5 / Ctrl+Enter** runs (selection-aware).
- **Stop button** — real query cancellation.
- **Ctrl+Space autocomplete** — SQL keywords + your schemas/tables/columns.
- **EXPLAIN** button (engines that support it), export results to **CSV/JSON**,
  open/save **.sql** files (Ctrl+O/Ctrl+S), results capped at 10 000 rows to protect memory.

### Administration
- **Edit Data** grid: insert/update/delete rows with PK detection, committed in one
  transaction (all-or-nothing); tables without a PK open read-only.
- Object explorer context menu: View Data, Edit Data, **View DDL**, **Import CSV**,
  Truncate, Drop (with confirmation).
- **Server panel** per connection: active sessions + **Kill**, users/roles, table sizes,
  server variables (filterable), **Backup** (runs the engine's tool if installed — pg_dump,
  mysqldump, mongodump, redis-cli, sqlite3; DuckDB uses native `EXPORT DATABASE`).

### Profiler & history
- **Profiler**: every query Nexus runs + optional **server activity** polling (queries from
  *any* client) on engines that expose it.
- **History**: last 500 queries persisted across sessions; double-click to reopen.
- **Snippets**: save (★) and reuse queries.

## Run

Requires the .NET 10 SDK.

```bash
./dist/Nexus            # published binary
# or: dotnet run -c Release
# or: ./run.sh
```

Desktop launcher: `cp nexus.desktop ~/.local/share/applications/`

## Verify

```bash
# Full PostgreSQL suite
NEXUS_TEST_HOST=… NEXUS_TEST_PORT=… NEXUS_TEST_DB=… NEXUS_TEST_USER=… NEXUS_TEST_PW=… \
  dotnet run -- --selftest

# Any engine (mysql, mariadb, sqlserver, oracle, sqlite, duckdb, clickhouse, mongodb, redis)
dotnet run -- --selftest mysql          # network engines use NEXUS_TEST_* vars
NEXUS_TEST_FILE=/tmp/t.db dotnet run -- --selftest sqlite   # file engines
```

## Architecture

```
Providers/   IDatabaseProvider (capability-based contract) · SqlProviderBase (shared ADO.NET)
             Postgres · MySql/MariaDb · SqlServer · Oracle · Sqlite · DuckDb · ClickHouse
             Mongo (mongosh-style parser) · Redis (command tokenizer + INSPECT)
Services/    CryptoService (AES-GCM) · ConnectionStore · QueryProfiler · QueryHistoryStore
             SchemaCache (autocomplete) · CsvService · BackupService · AppSettings
ViewModels/  MainWindow · ConnectionEditor · QueryDocument · TableEditor · ServerAdmin · Profiler
Views/       Avalonia XAML (dark, IDE-style; sidebar dockable left/right)
```

Extensions remain out of scope by design — Nexus stays lightweight.
