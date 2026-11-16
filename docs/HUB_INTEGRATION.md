# Connecting to mcp-hub

`mcp-sqlserver` is a hub **child MCP**: the hub starts it as a local stdio process and exposes its tools as `sql__<tool>`. Everything below follows the hub's own runbook (`mcp-hub/docs/adding-a-child-mcp.md`) and compatibility contract (`mcp-hub/docs/child-mcp-compatibility.md`). The hub-side files already ship in `mcp-hub`; this page tells you what they contain and how to turn the child on.

## What the hub needs from this repository

| Hub requirement | Where it is satisfied |
|---|---|
| Starts over stdio, lists tools **without credentials** | `McpSqlServer.exe serve`; missing config only produces `not configured` errors from DB tools (RULES #7) |
| Reads its section from `CENTRAL_CONFIG_PATH`, falls back to local files, reports the source | `Configuration/ConfigLoader.cs`, section `mssql`; `check_connection` returns `config_source`; `config_status` returns `{"groups": {"mssql_login": "<source>"}}` without connecting, for the Hub's config check (group `id` in `hub-requirements.json`) |
| Read-only tools carry `readOnlyHint = true` | every tool in `Mcp/SqlTools.cs`; enforced by `ArchitectureRulesTests` |
| Failures use the MCP error flag, never a message prefix | `ToolResults`; offline tools return `SearchContextResult.Success` / `ObjectContextResult.Found` |
| stdout only for the protocol, diagnostics on stderr | `Logger`; checked by `ArchitectureRulesTests` |
| A read-only health tool with a recognisable success shape | `check_connection` -> `{"connected": true, ...}`; unset account -> text containing `not configured` |
| Contract document and smoke check | `mcp-hub/docs/mcps/mcp-sqlserver.md`, `CHECKS["mcp-sqlserver"]` in `mcp-hub/scripts/smoke.py` |

## Files in `mcp-hub`

1. **`config/mcps/mcp-sqlserver.example.json`** is the template of the private child file:

   ```json
   {
     "transport": "stdio",
     "command": "publish/McpSqlServer.exe",
     "args": ["serve"],
     "cwd": "../mcp-sqlserver",
     "inheritEnv": ["PATH", "PATHEXT", "SYSTEMROOT", "WINDIR", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)"],
     "timeoutSeconds": 60,
     "connectTimeoutSeconds": 300,
     "env": { "CENTRAL_CONFIG_PATH": "../mcp-hub/config/private/mcps/mcp-sqlserver.json" },
     "mssql": { "server": "", "port": 1433, "username": "", "password": "", "database": "master",
                "encrypt": true, "trust_server_certificate": false, "allowed_databases": [],
                "isolation": "read_uncommitted", "max_rows": 100 }
   }
   ```

   `cwd` must be the repository root (a relative `CENTRAL_CONFIG_PATH` and `dbconfig.json` resolve from there). `command` is the published executable; `connectTimeoutSeconds` is generous because the first start can follow a build. NuGet restore fails without a `ProgramFiles` variable, so `inheritEnv` passes `ProgramFiles` and `ProgramFiles(x86)`.

2. **`config/hub.example.json`** registers the server (disabled) with a `prepare` step (`scripts/prepare.ps1`, needs the .NET 10 SDK; it publishes only when `publish/McpSqlServer.exe` is missing or older than `src/`) and the feature `sql` with an allowlist of the 13 agent tools (not `config_status`, which the Hub calls itself), `exposeAs: sql__<tool>`. Data tools (`lookup_rows`, `get_related_rows`, `run_select`, `explain_select`) have no `cacheSeconds` on purpose.

3. **`docs/mcps/mcp-sqlserver.md`** is the contract the hub serves to AI clients.

4. **`scripts/setup.ps1`** has the one-line account hint; **`scripts/smoke.py`** has the check.

## Turn it on (step by step)

1. Make sure `mcp-sqlserver` is a sibling of `mcp-hub` (`Setup.cmd` clones it; or `git clone` it next to the hub).
2. Create the private child file from the template (AI agents must not open it afterwards; inspect it only with `private_config.py keys`):

   ```powershell
   Copy-Item config/mcps/mcp-sqlserver.example.json config/private/mcps/mcp-sqlserver.json
   uv run python scripts/private_config.py keys mcp-sqlserver
   ```
3. Fill in the account. The hub refuses to set real values for sensitive keys (`password`, ...), so the **user** edits `mssql.server`, `mssql.username`, `mssql.password` (and optionally `allowed_databases`) in that file, or keeps the account in `../mcp-sqlserver/dbconfig.json` instead. Never paste a password into a chat. A read-only login is recommended: [readonly-login.sql](readonly-login.sql).
4. Register in `config/private/hub.json`: copy the `mcp-sqlserver` entry under `servers` and the `sql` entry under `features` from `config/hub.example.json`, then set `"enabled": true` on both.
5. Restart the client that runs the hub (`scripts/start.ps1`). The hub runs `prepare` (`scripts/prepare.ps1`) before starting the child. The executable is not rebuilt while the hub runs: after a `git pull` here, run `hubctl restart` in the hub (a stale build logs a warning on stderr at start).
6. Verify:

   ```powershell
   uv run python scripts/smoke.py
   ```

   Expected: `mcp-sqlserver  PASS` once the account is filled, or `WARN` (not configured) before. Then ask the client to call `sql__check_connection`.

## Troubleshooting

| You see | Meaning | Fix |
|---|---|---|
| Smoke `WARN`, text `not configured` | Account not filled in | Step 3 |
| `server.connect.failed` / timeout in `logs/mcp-hub.jsonl` | The exe is missing or `cwd` is wrong | Run `scripts/prepare.ps1` in the child; check `cwd` and `command` |
| `prepare` says the exe is running | A client starts `McpSqlServer.exe` directly, outside the hub | Close it (or remove that client entry) and run `hubctl restart`; the existing build is kept |
| `prepare` fails with `Value cannot be null (path1)` | `ProgramFiles` is not passed to the child | Add `ProgramFiles` and `ProgramFiles(x86)` to `inheritEnv` |
| `prepare` fails | .NET 10 SDK not installed | Install it (`winget install Microsoft.DotNet.SDK.10`) |
| `check_connection`: `Login failed`, `Cannot connect`, TLS error | Wrong credentials, address, firewall, self-signed certificate | Fix `server`/`username`/`password`; set `trust_server_certificate` |
| `check_connection`: `write_capable: true` | The login can write | Safe to use (the MCP cannot write) but use [readonly-login.sql](readonly-login.sql) when you can |
| `Database 'x' is not allowed` | Not in `allowed_databases`, or a system database | Add it to `mssql.allowed_databases` |
| `config_source` shows `local` but you set the hub file | The hub file has no `mssql` section or is unreadable | See warnings in `../mcp-sqlserver/logs/process.log` |
| Tool missing in the client | Not in the `sql` feature allowlist | Add it to `hub.json` `features.sql.tools` |

## Keeping both repositories in sync

A tool or key change touches: `Mcp/SqlTools.cs`, [TOOLS.md](TOOLS.md) or [CONFIGURATION.md](CONFIGURATION.md), `mcp-hub/docs/mcps/mcp-sqlserver.md`, `mcp-hub/config/hub.example.json`, and `mcp-hub/scripts/smoke.py` when the health tool changes (RULES #8). Run `dotnet test McpSqlServer.slnx` here and `uv run python scripts/package_qc.py` in the hub.
