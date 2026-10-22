# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**Nexus** is a lightweight, cross-OS database administrator. C# / .NET 10 + Avalonia UI 12 (MVVM via CommunityToolkit.Mvvm), dark IDE-style UI. Supports 10 engines: PostgreSQL, MySQL, MariaDB, SQL Server, Oracle, SQLite, DuckDB, ClickHouse (SQL) plus MongoDB (mongosh-style queries) and Redis (raw commands + `INSPECT <key>` pseudo-command). Extensions are permanently out of scope — the product stays lightweight.

## Commands

```bash
dotnet build -c Debug                 # build
dotnet run -c Debug                   # run the GUI
./run.sh                              # run dist/Nexus if present (falls back to dotnet run)

packaging/linux/build.sh              # .deb + .rpm (needs rpmbuild) + .tar.gz → artifacts/
packaging/macos/build.sh osx-arm64    # .app + .dmg (macOS only; osx-x64 for Intel)
pwsh packaging/windows/build.ps1      # Inno Setup setup.exe (Windows only)
python3 packaging/generate-icons.py   # regenerate every icon format from code (Pillow)
```

`.github/workflows/installers.yml` builds all installers on every push, really installs them on
Windows/macOS/Ubuntu (+ Fedora/Debian containers) and runs `--selftest sqlite|duckdb|secrets`
against the installed app. A tag `v<Version>` publishes a GitHub Release.

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

# OS keyring round-trip, legacy migration, and "connections.json has no secrets"
dotnet run -- --selftest secrets
```

Self-tests must never touch the user's real data: they call `SecretStore.UseEphemeral()` (engine
suites) or `AppPaths.RedirectForTests(tempDir)` (anything that saves/loads `connections.json`).
An earlier version that backed up/restored the real file deleted a user's legacy `key.bin`.

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
- **Persistence** (`AppPaths.ConfigDir`: `~/.config/Nexus`, `~/Library/Application Support/Nexus`,
  `%APPDATA%\Nexus`): `connections.json`, `settings.json` (UI prefs, e.g. sidebar side),
  `history.json` / `snippets.json`. Old files without newer fields migrate via serializer
  defaults (e.g. `Provider` defaults to postgresql).
- **Passwords never go in `connections.json`** (it's exportable/portable). `SecretStore` keeps
  them in the OS keyring keyed by connection `Id` — Windows Credential Manager, macOS Keychain,
  Linux Secret Service via libsecret — all by direct P/Invoke (`SecretBackends.cs`, no CLI
  helpers, so secrets never hit argv). No reachable keyring → AES-GCM vault in
  `AppPaths.DataDir` (never next to the JSON). Flow: the editor sets
  `SavedConnection.PendingPassword` (memory only, so Test works before saving) and
  `ConnectionStore.SaveAll` commits it to the keyring; `GetPassword` = pending ?? keyring.
  Removing a connection calls `ForgetPassword`. Pre-0.2 `EncryptedPassword` blobs
  (`LegacyEncryptedPassword` + `key.bin`) are migrated into the keyring on `Load`.
  On Linux use libsecret's `secret_service_*` calls, never `secret_password_*`: the latter
  silently writes to a private file when `SNAP_NAME` is set (e.g. launched from the VS Code
  snap's terminal), so passwords saved there were invisible to a desktop launch.
- **Connection lifecycle**: nothing connects at startup; expanding a connection root connects it.
  Providers report every profile they actually open to `ConnectionStatus` (SQL engines via
  `SqlProviderBase.OpenAsync` — use it, not `CreateConnection`+`OpenAsync`, for real work;
  `TestConnectionAsync` stays untracked). `IDatabaseProvider.DisconnectAsync` closes them all
  (driver `ClearPool` per opened profile incl. pinned clones; Mongo unregisters its cluster —
  `MongoClient.Dispose` doesn't). The tree shows a status dot per connection
  (`TreeNodeViewModel.IsConnected`/`HasConnectError`), synced from `ConnectionStatus.Changed`.
- `SavedConnection` equality is by `Id`, so database-pinned clones match their profile in
  pickers and `IndexOf`.
- **Singletons**: `QueryProfiler.Instance` (all query logging; UI-thread marshalled unless
  `DirectDispatch`), `QueryHistoryStore.Instance`, `SchemaCache` (autocomplete words per
  connection; invalidate on tree refresh).

### Avalonia gotchas specific to this repo

- Compiled bindings are on by default; `RelativeSource`/`DataContext.*` bindings need
  `x:CompileBindings="False"` on the element (see MainWindow tree/tabs).
- AvaloniaEdit consumes Enter before window KeyBindings — Ctrl+Enter is intercepted with a
  **tunnel** handler on the editor (`QueryDocumentView`). Same technique selects tree nodes on
  right-click (`MainWindow.OnExplorerPointerPressed`) because Avalonia doesn't select on
  right-click by itself. Set `TreeView.SelectedItem` there — `TreeViewItem.IsSelected = true` no
  longer propagates to it in Avalonia 12 (the context menu then acted on a stale node).
- The explorer context menu shows items per node kind via flags on `TreeNodeViewModel`
  (`IsQueryable`, `IsTable`, `CanDrop`, `CanOpenQuery`…); add new actions the same way.
- Don't put `Padding` on a `ScrollViewer`: it isn't subtracted when measuring the content, so
  wide children overflow. Use a `Margin` on the content instead.
- A transparent `Background` still hit-tests: an empty control layered over another (e.g. the
  TreeView over the first-run hint) swallows its clicks — hide it instead.
- Results/data grids build `DataGridTextColumn`s in code-behind with reflection bindings to a
  string indexer (`Binding("[i]")`) because the schema is only known at runtime.
- Package name quirk: the editor package is `Avalonia.AvaloniaEdit` (theme at
  `avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml`); DataGrid version lags core Avalonia.
- **No emoji in the UI** — emoji fonts differ per OS and are missing on many Linux installs
  (they render as tofu). Use `Controls/Icon` (`<c:Icon Kind="table" />`, stroked 24×24 paths;
  add new kinds to its `Paths` table). Engines show a two-letter `Badge` on their `BrandColor`.
- Monospace text uses the bundled JetBrains Mono via `{StaticResource MonoFont}` (App.axaml).
- Two-way `ComboBox.SelectedItem` bindings push `null` while a tab's view re-binds; bind to a
  null-ignoring proxy (`QueryDocumentViewModel.ConnectionChoice`/`DatabaseChoice`).

### Headless testing / screenshots (scratchpad `Shot/` harness pattern)

For GUI-level verification use `Avalonia.Headless` + `UseSkia` and `SetupWithoutStarting`, then a
**synchronous** `RunJobs` pump loop. **Never `await` at top level after setup** — continuations
queue on the dispatcher nobody is pumping (deadlock); wrap async calls in
`Task.Run(...).GetAwaiter().GetResult()`. Real input can be simulated with
`win.MouseDown/MouseUp`, and `CaptureRenderedFrame()` saves PNGs (docs/ screenshots were made this
way). To show query results in a screenshot, execute on the thread pool *before* building the UI
and inject into the VM.

## Versioning

Semantic versioning starting at **0.1.0** (0.2.0 = OS keyring + installers). The single source of truth is `<Version>` in
`Nexus.csproj`; `Services/AppInfo.cs` reads it from the assembly at runtime and the top bar
renders it as "Nexus — {version}" (`MainWindowViewModel.VersionText`); the packaging scripts
read it from the csproj too. To release: bump `<Version>`, commit, push tag `v<Version>`.
Don't hardcode the version anywhere else.

## Product decisions (don't regress)

- Window/app title is just **"Nexus"** — no subtitle; the version appears next to it as
  "— 0.2.0". No "New Connection" button in the top bar (sidebar ＋ only). Sidebar side is
  user-toggleable (⋯ menu) and persisted.
- Removing a connection asks for confirmation (it also deletes the keyring entry).
- New query tabs start **empty**; the **profiler starts off** (opt-in).
- Connections **start disconnected** and connect only when the user expands them; right-click
  offers Connect/Disconnect, and a dot shows the state (gray hollow / green / red).
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
6. **Code signing**: buy/configure a Windows Authenticode cert and an Apple Developer ID so the
   installers open without SmartScreen/Gatekeeper prompts (the scripts already accept them).
7. **CSV import for ClickHouse** (fails today — no transactions; needs a batch INSERT path).
8. **Result grid virtualization / paging beyond the 10k row cap** for very wide result sets.
9. **CI for the engine matrix**: the installers workflow covers SQLite/DuckDB/secrets; add the
   Docker engines (`--selftest` loop), Oracle gated behind a flag because of its size.
