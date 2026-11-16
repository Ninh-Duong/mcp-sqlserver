# mcp-sqlserver

A **read-only SQL Server MCP** for investigating issues. It lets an AI agent look at real data to find a root cause, next to the log, source, ticket and pull-request MCPs of [mcp-hub](../mcp-hub). It runs **one validated `SELECT` at a time and nothing else**: no `INSERT`, `UPDATE`, `DELETE`, `TRUNCATE`, `DROP`, `EXEC`, no `SELECT INTO`, no multi-statement batches. This holds even when the SQL login itself is allowed to write.

It works in two modes:

- **MCP server** (`serve`): for AI assistants (through mcp-hub, Claude Desktop, Cursor, Antigravity, ...).
- **Interactive CLI menu** (`menu`): for developers to explore databases and export the offline schema snapshot in the terminal.

C# / .NET 10, MCP over stdio, [official MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk), Microsoft ScriptDom for SQL validation.

## How read-only is guaranteed

Six independent layers ([docs/SECURITY.md](docs/SECURITY.md)): a T-SQL **AST allowlist** (not a keyword list), a transaction that is **always rolled back**, session guards (lock timeout, row limit, `READ UNCOMMITTED`), `ApplicationIntent=ReadOnly`, a database allow-list, and output limits with column masking. Passwords are redacted (`***`) in logs and errors. All SQL goes through one class, and tests fail the build if any other code opens a connection.

## Tools

14 tools, all `readOnlyHint = true` (behind the hub they are `sql__<tool>`; `config_status` is called by the Hub itself and not exposed). Details: [docs/TOOLS.md](docs/TOOLS.md).

| Group | Tools |
|---|---|
| Server | `check_connection`, `list_databases`, `config_status` (Hub config check, no connection) |
| Schema (live) | `list_tables`, `describe_table`, `find_columns`, `get_object_definition` |
| Data | `lookup_rows` (no SQL needed), `get_related_rows` (follow foreign keys), `run_select`, `explain_select` |
| Offline snapshot | `search_context`, `get_object_context`, `check_schema_drift` |

How an agent uses them to get from a symptom to a root cause: [docs/INVESTIGATION_PLAYBOOK.md](docs/INVESTIGATION_PLAYBOOK.md).

## Quick start (3 steps)

### 1. Prerequisites

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) (`dotnet --version`).

### 2. Configuration

Copy [`dbconfig.example.json`](dbconfig.example.json) to `dbconfig.json` in the project root and fill the first fields:

```json
{
  "server_alias": "DEV",
  "server": "sql.example.internal",
  "port": 1433,
  "username": "reader",
  "password": "YourPassword123",
  "database": "master",
  "encrypt": true,
  "trust_server_certificate": false
}
```

`dbconfig.json` is git-ignored, so credentials are never committed. Environment variables (`MSSQL_SERVER`, `MSSQL_USERNAME`, `MSSQL_PASSWORD`, ...) win over the file. All keys, the priority (environment > hub section > local file > default) and examples: [docs/CONFIGURATION.md](docs/CONFIGURATION.md).

The server starts and lists its tools even with no credentials; database tools then answer `not configured`. A read-only login is recommended but not required: [docs/readonly-login.sql](docs/readonly-login.sql).

### 3. Run

**Mode A: interactive menu (for humans)**

```powershell
powershell -File ./scripts/run.ps1 menu          # runs the unit tests first, then starts
dotnet run --project ./src/McpSqlServer -- menu
```

Menu options: `1` configure connection settings, `2` test connection, `3` check DB migrations and schema drift, `4` list databases, `5` list tables in a database, `6` scan the server and export the offline snapshot (`./ai-context/`), `0` exit.

**Mode B: MCP server (for AI tools)**

Through mcp-hub, see [docs/HUB_INTEGRATION.md](docs/HUB_INTEGRATION.md). The hub files (`config/mcps/mcp-sqlserver.example.json`, the `sql` feature in `config/hub.example.json`, `docs/mcps/mcp-sqlserver.md`) are already in `mcp-hub`: fill the `mssql` section of the private child file and enable it. `hub-requirements.json` (repo root) lists the `mssql` server, username and password; mcp-hub's desktop app shows a gap as **Config incomplete**, and the server still starts.

Directly in another MCP client (for example `claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "sqlserver": {
      "command": "dotnet",
      "args": ["run", "--project", "./src/McpSqlServer", "--", "serve"],
      "env": {
        "MSSQL_SERVER": "sql.example.internal",
        "MSSQL_USERNAME": "reader",
        "MSSQL_PASSWORD": "YourPassword123",
        "MSSQL_ALLOWED_DATABASES": "Crm,Billing"
      }
    }
  }
}
```

Use `dotnet publish src/McpSqlServer -c Release -o publish` and `publish/McpSqlServer.exe serve` for a faster start.

Other commands: `dotnet run --project src/McpSqlServer -- --preflight` (dependency check and self-tests).

## Offline snapshot

`scan` reads the catalog of every accessible database (tables, columns, keys, indexes, views, procedures, functions, triggers, cross-database dependencies) and writes compact files an agent can search without touching the server:

```powershell
dotnet run --project src/McpSqlServer -- scan --alias DEV --output ./ai-context     # all databases
dotnet run --project src/McpSqlServer -- scan --alias DEV -d CRM_Order              # one database
dotnet run --project src/McpSqlServer -- scan --alias DEV --resume                 # finish databases that failed
```

Output under `ai-context/` (git-ignored): `INDEX.md`, `servers_registry.json`, `catalog.db` (SQLite, used by `search_context`), and per server `summary.md`, `TABLES_ROUTER.compact.md`, `CROSS_DB_GRAPH.compact.md` and per database `schema.compact.md` (an index plus one file per table when a database has more than 50 tables), `dependencies.compact.md`, and `views/`, `procedures/`, `functions/`, `triggers/` with one `.sql` file each. `scan` is a command-line command only; it is not an MCP tool.

## Develop

```powershell
dotnet test McpSqlServer.slnx        # no database needed (integration tests skip themselves)
scripts/build.ps1                    # restore, build, test
dotnet publish src/McpSqlServer -c Release -o publish
```

Start with [AGENTS.md](AGENTS.md), [docs/RULES.md](docs/RULES.md) and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md); code standards in [docs/STANDARDS.md](docs/STANDARDS.md); decisions in [docs/adr/](docs/adr/).

## Documentation map

| Doc | For |
|---|---|
| [RULES](docs/RULES.md) | Invariants that keep the MCP read-only and hub-compatible |
| [ARCHITECTURE](docs/ARCHITECTURE.md) | Layers, dependencies, life of a request |
| [SECURITY](docs/SECURITY.md) | The six layers, closed bypasses, residual risk |
| [STANDARDS](docs/STANDARDS.md) | Tool, code, test and doc conventions |
| [TOOLS](docs/TOOLS.md) | Contract of every tool |
| [CONFIGURATION](docs/CONFIGURATION.md) | Keys, priority, examples |
| [HUB_INTEGRATION](docs/HUB_INTEGRATION.md) | Connecting to mcp-hub |
| [INVESTIGATION_PLAYBOOK](docs/INVESTIGATION_PLAYBOOK.md) | How agents find root causes with these tools |
| [adr/](docs/adr/) | Architecture decisions |
