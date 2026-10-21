# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**Nexus** is a lightweight, cross-OS database administrator. C# / .NET 10 + Avalonia UI 12 (MVVM via CommunityToolkit.Mvvm), dark IDE-style UI. Supports 10 engines: PostgreSQL, MySQL, MariaDB, SQL Server, Oracle, SQLite, DuckDB, ClickHouse (SQL) plus MongoDB (mongosh-style queries) and Redis (raw commands + `INSPECT <key>` pseudo-command). Extensions are permanently out of scope — the product stays lightweight.

## Commands

```bash
dotnet build -c Debug                 # build
dotnet run -c Debug                   # run the GUI
dotnet publish -c Release -o dist     # publish (framework-dependent); binary at dist/Nexus
./run.sh                              # run published binary (falls back to dotnet run)
```

### Tests (self-test matrix against real databases)

There is no unit-test project; verification is an end-to-end self-test built into the app:

```bash
# Full PostgreSQL suite (SelfTest.cs) — needs the seeded "demo" DB (see containers below)
NEXUS_TEST_HOST=localhost NEXUS_TEST_PORT=55432 NEXUS_TEST_DB=demo \
NEXUS_TEST_USER=postgres NEXUS_TEST_PW=nexus123 dotnet run -- --selftest

# Generic per-engine matrix (SelfTestMatrix.cs): connect, CRUD, introspection, PK detection,
# DDL, stats, sessions, transaction commit/rollback, explicit-column SELECT
dotnet run -- --selftest mysql            # network engines use the NEXUS_TEST_* vars
NEXUS_TEST_FILE=/tmp/t.db dotnet run -- --selftest sqlite    # file-based engines
# ids: postgresql mysql mariadb sqlserver oracle sqlite duckdb clickhouse mongodb redis
```

Exit code 0 = all checks passed. Both harnesses set `QueryProfiler.Instance.DirectDispatch = true`
and `IsCapturing = true` (the profiler is opt-in in the app but tests rely on it).

### Docker test containers

| Engine | Container | Port | Credentials |
|---|---|---|---|
| PostgreSQL | nexus-pg | 55432 | postgres / nexus123 (db `demo` seeded with `customers`) |
| MySQL | nexus-mysql | 53306 | root / nexus123 (db `demo`) |
| MariaDB | nexus-mariadb | 53307 | root / nexus123 (db `demo`) |
| SQL Server | nexus-mssql | 51433 | sa / Nexus123! |
| Oracle | nexus-oracle | 51521 | system / nexus123 (service `FREEPDB1`, image gvenzl/oracle-free:slim — slow start, ~1.5 GB RAM) |
| ClickHouse | nexus-clickhouse | 58123 | default / nexus123 |
| MongoDB | nexus-mongo | 57017 | (no auth) |
| Redis | nexus-redis | 56379 | (no auth) |

The user's own PostgreSQL 18 runs in container `PostgresRollyCodes` on **port 5432** with databases
Phoenix/Phoenix2/Phoenix3 (EF Core apps). Never create/drop objects there except clearly-named
disposable test artifacts (`_nexus_*`), and clean them up.

## Architecture

Everything database-facing goes through **`Providers/IDatabaseProvider`**; UI code never touches a
driver. Key ideas that span multiple files:

- **Capability flags drive the UI** (`ProviderCapabilities`): `SupportsSchemas` (containers are
  schemas vs databases), `SupportsServerActivity/Users/KillSession/Explain/RowEditing`,
  `IsFileBased` (SQLite/DuckDB get a file picker instead of host/port), `BackupTool`. Views/VMs
  check flags — they never check provider ids except for narrow idioms (e.g. SQLite TRUNCATE,
  Mongo container pinning).
- **`SqlProviderBase`** holds all shared ADO.NET logic: execution with real cancellation
  (`CancellationToken` + `DbCommand.Cancel`), result mapping, the 10k-row cap, transaction batches
  (`ExecuteTransactionAsync`, used by the data editor and CSV import), profiler logging, and
  dialect hooks (`QuoteIdent`, `BuildSelectQuery`, `BuildDropDatabaseSql`, introspection SQL…).
  The 8 SQL engines subclass it; Mongo/Redis implement the interface directly with their own
  command parsers.
- **Introspection queries embed values as escaped literals via `Lit()`** instead of ADO.NET
  parameters — parameter syntax differs per driver (`@p` / `:p` / `$p`), and the values come from
  the engine's own catalogs. Keep doing this in new introspection code.
- **Object explorer tree** (`MainWindowViewModel` loaders + `TreeNodeViewModel`): connection →
  *databases* (PG/MSSQL via `GetDatabasesAsync`) → schemas → objects → columns. Database nodes
  carry a **cloned `SavedConnection` pinned to that database**, so everything downstream (queries,
  Edit Data, DDL, drops) just uses `node.Connection`. Because of clones, always resolve back to the
  saved profile by `Id` (`ResolveSaved`) before mutating the connection list. Nodes lazy-load via
  `EnsureLoadedAsync`; `Parent` is set through `AddChild` and enables in-place refresh after DDL.
- **Documents are heterogeneous tabs** (`DocumentViewModel` base): `QueryDocumentViewModel` (SQL
  editor + results + DB picker), `TableEditorViewModel` (editable grid, PK-based UPDATE/INSERT/
  DELETE committed in one transaction; `"(null)"` cell text means SQL NULL), `ServerAdminViewModel`
  (sessions/users/sizes/variables/backup). The `ViewLocator` maps `FooViewModel` → `Views.FooView`.
- **Dialogs**: confirmations/notices use the **in-window overlay** in `MainWindow`
  (`ShowOverlayAsync` + `TaskCompletionSource`) — never OS-level modal windows from context-menu
  actions; they get lost behind the main window on Wayland (this was a real bug). VMs receive UI
  affordances as injected delegates (`Confirm`, `Notify`, `PickCsvFile`, `PickSavePath`…), wired in
  the views' `DataContextChanged`.
- **Dropping databases** (`DropDatabaseAsync`): runs from a maintenance DB (never the one being
  dropped) and calls `PrepareDropDatabaseAsync` first to clear the driver's connection pools —
  Nexus's own pooled connections from tree browsing otherwise block the drop. Offers force-drop /
  CASCADE on dependency errors; nothing may fail silently (always `Notify`).
- **Persistence** (all under `~/.config/Nexus/`): `connections.json` (passwords AES-256-GCM
  encrypted via `CryptoService`; key in `key.bin`, mode 0600 — plaintext never touches disk),
  `settings.json` (UI prefs, e.g. sidebar side), `history.json` / `snippets.json`. Old files
  without newer fields migrate via serializer defaults (e.g. `Provider` defaults to postgresql).
- **Singletons**: `QueryProfiler.Instance` (all query logging; UI-thread marshalled unless
  `DirectDispatch`), `QueryHistoryStore.Instance`, `SchemaCache` (autocomplete words per
  connection; invalidate on tree refresh).

### Avalonia gotchas specific to this repo

- Compiled bindings are on by default; `RelativeSource`/`DataContext.*` bindings need
  `x:CompileBindings="False"` on the element (see MainWindow tree/tabs).
- AvaloniaEdit consumes Enter before window KeyBindings — Ctrl+Enter is intercepted with a
  **tunnel** handler on the editor (`QueryDocumentView`). Same technique selects tree nodes on
  right-click (`MainWindow.OnExplorerPointerPressed`) because Avalonia doesn't select on
  right-click by itself.
- Results/data grids build `DataGridTextColumn`s in code-behind with reflection bindings to a
  string indexer (`Binding("[i]")`) because the schema is only known at runtime.
- Package name quirk: the editor package is `Avalonia.AvaloniaEdit` (theme at
  `avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml`); DataGrid version lags core Avalonia.

### Headless testing / screenshots (scratchpad `Shot/` harness pattern)

For GUI-level verification use `Avalonia.Headless` + `UseSkia` and `SetupWithoutStarting`, then a
**synchronous** `RunJobs` pump loop. **Never `await` at top level after setup** — continuations
queue on the dispatcher nobody is pumping (deadlock); wrap async calls in
`Task.Run(...).GetAwaiter().GetResult()`. Real input can be simulated with
`win.MouseDown/MouseUp`, and `CaptureRenderedFrame()` saves PNGs (docs/ screenshots were made this
way). To show query results in a screenshot, execute on the thread pool *before* building the UI
and inject into the VM.

## Versioning

Semantic versioning starting at **0.1.0**. The single source of truth is `<Version>` in
`Nexus.csproj`; `Services/AppInfo.cs` reads it from the assembly at runtime and the top bar
renders it as "Nexus — {version}" (`MainWindowViewModel.VersionText`). To release: bump
`<Version>`, republish. Don't hardcode the version anywhere else.

## Product decisions (don't regress)

- Window/app title is just **"Nexus"** — no subtitle; the version appears next to it as
  "— 0.1.0". No "New Connection" button in the top bar (sidebar ＋ only). Sidebar side is
  user-toggleable (⇄) and persisted.
- New query tabs start **empty**; the **profiler starts off** (opt-in).
- "View Data" generates an **explicit, line-broken column list** (one column per line, dialect
  LIMIT/TOP/FETCH), falling back to `*` only if introspection fails.
- Query errors must be loud: red selectable banner above results + Messages tab + status.
- Run = F5 **and** Ctrl+Enter. Selection-aware (runs highlighted text if any).

## Next steps (roadmap)

Pending work, roughly in priority order:

1. **Restore** counterpart for backups (currently backup-only; pg_restore/mysql/mongorestore paths
   and a Restore section in the Server panel).
2. **User management actions** — create/drop user, change password (Server panel lists users
   read-only today).
3. **Per-dialect syntax highlighting** via `AvaloniaEdit.TextMate` (current highlighting is the
   built-in generic SQL definition; Mongo/Redis tabs get no highlighting).
4. **EXPLAIN for SQL Server and Oracle** (need `SHOWPLAN`/`PLAN_TABLE` flows, not a statement
   prefix; `SupportsExplain=false` today).
5. **Window state persistence** (size/position/splitter widths in `AppSettings`) and a
   light-theme toggle.
6. **Cross-OS publishes**: `dotnet publish -r win-x64 / osx-arm64` + packaging; today only the
   Linux framework-dependent build in `dist/` is exercised.
7. **CSV import for ClickHouse** (fails today — no transactions; needs a batch INSERT path).
8. **Result grid virtualization / paging beyond the 10k row cap** for very wide result sets.
9. **CI**: script the Docker matrix (`--selftest` loop) so regressions are caught automatically;
   Oracle gated behind a flag because of its size.
