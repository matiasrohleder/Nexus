# Nexus

A **lightweight, cross-OS database administrator**. Built with **C# / .NET 10 + Avalonia UI**
(native desktop on Linux, Windows and macOS — not a browser shell).

**[⬇ Download for Windows, macOS or Linux](#install)**

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
- **Passwords in the OS keyring** (Credential Manager / Keychain / Secret Service) — never in
  `connections.json`, which you can export and import between machines.
- Redesigned dialog: engine tiles, **smart defaults per engine** (port/user/database),
  **paste a URL** (`postgres://user:pass@host/db`, `mongodb://…`, ADO.NET strings) to auto-fill,
  file picker for SQLite/DuckDB, color swatches, Enter = Test.
- **Folders** and custom order in the explorer: drag and drop, or right-click › Move Up/Down /
  Move to Folder…; **Collapse all** button in the sidebar header.

### Query editor
- Multi-tab, line numbers; **F5 / Ctrl+Enter** runs (selection-aware).
- **Schema-aware highlighting** (VS Code Dark+ colors): keywords, functions, strings, numbers,
  comments, and your tables/columns in their own colors. Mongo and Redis tabs too.
- **Autocomplete as you type** (or Ctrl+Space), by context: tables after `FROM`/`JOIN`, columns
  of the tables in the query in `SELECT`/`WHERE`/`ORDER BY`, `alias.` → that table's columns,
  per-dialect keywords and functions (`OR` → `ORDER BY`).
- **Stop button** — real query cancellation.
- Numbered result rows with a row count; **font size** via Ctrl+= / Ctrl+- / Ctrl+0 or
  Ctrl+mouse wheel (editor and grids).
- **EXPLAIN** button (engines that support it), export results to **CSV/JSON**,
  open/save **.sql** files (Ctrl+O/Ctrl+S), results capped at 10 000 rows to protect memory.

### Administration
- **Edit Data** grid: insert/update/delete rows with PK detection, committed in one
  transaction (all-or-nothing); tables without a PK open read-only.
- Object explorer context menu: Select Top 1000 / Top 200 Rows, Edit Data, **View DDL**, **Import CSV**,
  Truncate, Drop (with confirmation).
- **Server panel** per connection: active sessions + **Kill**, users/roles, table sizes,
  server variables (filterable), **Backup** (runs the engine's tool if installed — pg_dump,
  mysqldump, mongodump, redis-cli, sqlite3; DuckDB uses native `EXPORT DATABASE`).

### Profiler & history
- **Profiler**: every query Nexus runs + optional **server activity** polling (queries from
  *any* client) on engines that expose it.
- **History**: last 500 queries persisted across sessions; double-click to reopen.
- **Snippets**: save (★) and reuse queries.

## Install

Download the installer for your system from the
**[latest release](https://github.com/matiasrohleder/Nexus/releases/latest)**.
Everything is bundled — you don't need .NET or any other runtime.

| System | Download |
|---|---|
| Windows 10 / 11 | `Nexus-<version>-windows-x64-setup.exe` |
| macOS 14+ — Apple Silicon (M1–M4) | `Nexus-<version>-macos-arm64.dmg` |
| macOS 14+ — Intel | `Nexus-<version>-macos-x64.dmg` |
| Ubuntu, Debian, Mint, Pop!_OS, elementary | `Nexus-<version>-linux-x64.deb` |
| Fedora, RHEL, Rocky, openSUSE | `Nexus-<version>-linux-x64.rpm` |
| Any other Linux (Arch, Manjaro, …) | `Nexus-<version>-linux-x64.tar.gz` |

### Windows

1. Run `Nexus-<version>-windows-x64-setup.exe`. It installs for your user only, without
   administrator rights (pick *Install for all users* in the first screen if you prefer).
2. If SmartScreen says **"Windows protected your PC"**, click **More info → Run anyway**. It
   shows up because the installer isn't code-signed yet; it's a one-time prompt.
3. Open **Nexus** from the Start menu. Uninstall it from *Settings → Apps → Installed apps*.

Windows on ARM runs the x64 build through the built-in emulation.

### macOS

1. Open the `.dmg` and drag **Nexus** onto **Applications**.
2. The first time you open it, macOS says it can't verify the developer (the app isn't
   notarized yet). Go to **System Settings → Privacy & Security**, scroll down and click
   **Open Anyway**. You only do this once per version.
   *Terminal alternative:* `xattr -dr com.apple.quarantine /Applications/Nexus.app`
3. When you save a connection with a password, macOS asks whether Nexus may use the Keychain.
   Choose **Always Allow**.

### Linux

**Debian / Ubuntu** — double-click the `.deb` (it opens in the software center) or run:

```bash
sudo apt install ./Nexus-<version>-linux-x64.deb
```

**Fedora / RHEL / openSUSE:**

```bash
sudo dnf install ./Nexus-<version>-linux-x64.rpm      # Fedora, RHEL, Rocky
sudo zypper install ./Nexus-<version>-linux-x64.rpm   # openSUSE
```

**Any other distribution** — this installs for your user only, without root:

```bash
tar xzf Nexus-<version>-linux-x64.tar.gz
./Nexus-<version>-linux-x64/install.sh             # uninstall: install.sh --uninstall
```

Then open **Nexus** from your applications menu, or run `nexus` in a terminal. Uninstall with
`sudo apt remove nexus-db` or `sudo dnf remove nexus-db`.

### Updates

From **0.4.0** on, Nexus updates itself. When it starts it checks GitHub for a newer release and
offers it (**Update now**, **Later** or **Skip this version**); you can also check any time from
**⋯ → Check for updates…**, and turn the startup check off in that dialog. *Update now*
downloads the installer for your system, verifies its SHA-256 checksum, installs it in place and
restarts Nexus — your connections, passwords and settings are kept:

| Installed with | How Nexus updates it |
|---|---|
| Windows setup | Runs the new setup silently over the current install, then reopens Nexus |
| macOS `.dmg` | Replaces `Nexus.app` with the new one (no drag and drop needed) |
| `.deb` / `.rpm` | Upgrades the package with `apt` / `dnf` (`zypper` on openSUSE), asking for your password |
| `.tar.gz` | Re-runs `install.sh` for your user (no password) |

Copies that weren't installed by an installer (e.g. built from source) link to the release page
instead. Versions before 0.4.0 can't update themselves — install 0.4.0 once by hand.

**Upgrading by hand on Linux:** install the new package over the old one —
`sudo apt install ./Nexus-<version>-linux-x64.deb` (or `sudo dnf install ./…rpm`). There is no
need to uninstall first. Some software centers (e.g. Ubuntu's App Center) only say *"installed"*
when a `.deb` with the same name is already present instead of offering the upgrade; use the
command above or the in-app update.

## Your data and passwords

**Passwords are never written to a file.** They are stored in your operating system's own
credential store, encrypted and tied to your user account:

| | Windows | macOS | Linux |
|---|---|---|---|
| Passwords | Credential Manager | Keychain | Secret Service (GNOME Keyring, KWallet, KeePassXC) |
| Connections (`connections.json`) | `%APPDATA%\Nexus` | `~/Library/Application Support/Nexus` | `~/.config/Nexus` |

If a Linux session has no keyring service at all, Nexus falls back to an AES-256-GCM encrypted
vault in `~/.local/share/Nexus` (key file readable only by you). That location is separate from
`connections.json`, so passwords still never travel with it.

**Moving your connections to another computer:** use the **⋯** menu in the Connections panel
→ **Export connections…**, then **Import connections…** on the other machine (or copy
`connections.json` itself). The file contains hosts, ports, users and colors but **no
passwords**: enter each password again on the new computer, where it's saved in that system's
keyring.

Uninstalling Nexus never deletes your connections or saved passwords. Upgrading from 0.1.x
moves your saved passwords into the system keyring automatically the first time 0.2 starts.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run -c Release
```

### Building the installers

| Installer | Command | Runs on |
|---|---|---|
| `.deb`, `.rpm`, `.tar.gz` | `packaging/linux/build.sh` | Linux (`rpmbuild` needed for the `.rpm`) |
| `.dmg` | `packaging/macos/build.sh osx-arm64` (or `osx-x64`) | macOS |
| `setup.exe` | `pwsh packaging/windows/build.ps1` | Windows with [Inno Setup 6](https://jrsoftware.org/isdl.php) |

Output goes to `artifacts/`. The [Installers workflow](.github/workflows/installers.yml) builds
all of them on every push, installs each on a real Windows, macOS and Linux machine (plus
Fedora and Debian containers), and runs the self-tests against the installed app. Pushing a
tag `v<version>` that matches `<Version>` in `Nexus.csproj` publishes a GitHub Release with
every installer and a `SHA256SUMS.txt`.

Code signing is optional: set `WINDOWS_CERT_PFX`/`WINDOWS_CERT_PASSWORD` (Windows) or
`MACOS_SIGN_IDENTITY` + `APPLE_ID`/`APPLE_TEAM_ID`/`APPLE_APP_PASSWORD` (macOS notarization)
and the build scripts sign the installers, which removes the SmartScreen and Gatekeeper prompts.

The app icon is generated by `python3 packaging/generate-icons.py`.

## Verify

```bash
# Full PostgreSQL suite
NEXUS_TEST_HOST=… NEXUS_TEST_PORT=… NEXUS_TEST_DB=… NEXUS_TEST_USER=… NEXUS_TEST_PW=… \
  dotnet run -- --selftest

# Any engine (mysql, mariadb, sqlserver, oracle, sqlite, duckdb, clickhouse, mongodb, redis)
dotnet run -- --selftest mysql          # network engines use NEXUS_TEST_* vars
NEXUS_TEST_FILE=/tmp/t.db dotnet run -- --selftest sqlite   # file engines

# Password storage in this OS's keyring + connections.json secrecy
dotnet run -- --selftest secrets
```

## Architecture

```
Providers/   IDatabaseProvider (capability-based contract) · SqlProviderBase (shared ADO.NET)
             Postgres · MySql/MariaDb · SqlServer · Oracle · Sqlite · DuckDb · ClickHouse
             Mongo (mongosh-style parser) · Redis (command tokenizer + INSPECT)
Services/    SecretStore (OS keyrings) · ConnectionStore · QueryProfiler · QueryHistoryStore
             SqlLanguage (lexer) · SqlCompletion · SchemaCache · CsvService · BackupService
             AppSettings · UiFonts
ViewModels/  MainWindow · ConnectionEditor · QueryDocument · TableEditor · ServerAdmin · Profiler
Views/       Avalonia XAML (dark, IDE-style; sidebar dockable left/right)
```

Extensions remain out of scope by design — Nexus stays lightweight.
