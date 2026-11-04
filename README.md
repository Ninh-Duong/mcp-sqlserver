# mcp-sqlserver

A **read-only SQL Server MCP** for investigating issues. It lets an AI agent look at real data to find a root cause, next to the log, source, ticket and pull-request MCPs of [mcp-hub](../mcp-hub). It runs **one validated `SELECT` at a time and nothing else**: no `INSERT`, `UPDATE`, `DELETE`, `TRUNCATE`, `DROP`, `EXEC`, no `SELECT INTO`, no multi-statement batches. This holds even when the SQL login itself is allowed to write.

C# / .NET 10, MCP over stdio, [official MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk), Microsoft ScriptDom for SQL validation.

## How read-only is guaranteed

Six independent layers ([docs/SECURITY.md](docs/SECURITY.md)): a T-SQL **AST allowlist** (not regex), a transaction that is **always rolled back**, session guards (lock timeout, row limit, `READ UNCOMMITTED`), `ApplicationIntent=ReadOnly`, a database allow-list, and output limits with column masking. All SQL goes through one class, and tests fail the build if any other code opens a connection.

## Tools

13 tools, all `readOnlyHint = true` (behind the hub they are `sql__<tool>`). Details: [docs/TOOLS.md](docs/TOOLS.md).

| Group | Tools |
|---|---|
| Server | `check_connection`, `list_databases` |
| Schema (live) | `list_tables`, `describe_table`, `find_columns`, `get_object_definition` |
| Data | `lookup_rows` (no SQL needed), `get_related_rows` (follow foreign keys), `run_select`, `explain_select` |
| Offline snapshot | `search_context`, `get_object_context`, `check_schema_drift` |

How an agent uses them to get from a symptom to a root cause: [docs/INVESTIGATION_PLAYBOOK.md](docs/INVESTIGATION_PLAYBOOK.md).

## Use it with mcp-hub

The hub starts it as a child process and exposes `sql__*`. Step by step: [docs/HUB_INTEGRATION.md](docs/HUB_INTEGRATION.md). In short: the hub files (`config/mcps/mcp-sqlserver.example.json`, the `sql` feature in `config/hub.example.json`, `docs/mcps/mcp-sqlserver.md`) are already in `mcp-hub`; fill the `mssql` section of the private child file and enable it.

## Run it stand-alone

Requirements: .NET 10 SDK.

```powershell
Copy-Item dbconfig.example.json dbconfig.json      # git-ignored; fill server, username, password
dotnet run --project src/McpSqlServer -- serve     # MCP over stdio
dotnet run --project src/McpSqlServer -- menu      # interactive menu (default mode)
dotnet run --project src/McpSqlServer -- --preflight
```

Or set `MSSQL_SERVER`, `MSSQL_USERNAME`, `MSSQL_PASSWORD`. All keys, priority (environment > hub section > local file > default) and examples: [docs/CONFIGURATION.md](docs/CONFIGURATION.md).

The server starts and lists its tools even with no credentials; database tools then answer `not configured`.

A read-only login is recommended but not required: [docs/readonly-login.sql](docs/readonly-login.sql).

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
