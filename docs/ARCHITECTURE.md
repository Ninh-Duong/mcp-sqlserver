# Architecture

`mcp-sqlserver` is a **read-only SQL Server investigation MCP**. It lets an AI agent look at real data to find the root cause of an issue, next to the log, repository, ticket and pull-request MCPs of the same hub. It can read; it cannot change anything.

```
mcp-hub  --stdio-->  McpSqlServer.exe serve
                       |
   Mcp/            SqlTools (13 tools, ReadOnly = true)  +  McpHost (SDK, stdio)
                       | thin adapters: validate arguments, serialise JSON
   Investigation/  InvestigationService     SqlServerService        Context/ (offline snapshot)
                       | fixed catalog SQL + a parameterised lookup builder
   Security/       ReadOnlyQueryExecutor  --uses-->  SelectOnlyValidator (ScriptDom AST)
                       | the ONLY code that opens a connection for a tool
                   SQL Server  (ApplicationIntent=ReadOnly, rolled-back transaction)

   Scanning/       offline CLI scan (`McpSqlServer.exe scan`) -> ai-context/   (not reachable from Mcp/ or Investigation/)
   Configuration/  ConfigLoader: env > hub section > local file > defaults
```

Dependencies point **down only**. `Security/` knows nothing about tools. `Mcp/` and `Investigation/` never see SqlClient. `Scanning/` is a separate branch used by the command line; the server never calls it. These directions are checked by `ArchitectureRulesTests`.

## Folders

| Folder / file | Responsibility | May depend on |
|---|---|---|
| `Mcp/` | `SqlTools` declares the 13 tools; `McpHost` is the composition root (hosting, SDK, stdio, server instructions); `ToolResults` formats results (compact snake_case JSON, MCP error flag) | `Investigation/`, `Security/` (types), `Context/`, `SqlServerService` |
| `Investigation/` | Tool logic: list/describe/find/lookup/related/explain/definition. Fixed catalog SQL and the `lookup_rows` SQL builder. `SqlIdentifier` quoting | `Security/` (executor, parameter type) |
| `Security/` | `SelectOnlyValidator` (AST allowlist) and `ReadOnlyQueryExecutor` (validation, rollback, guards, limits, masking, allow-list) | `ConnectionOptions`, `Logger` |
| `SqlServerService.cs` | Connection check (with `write_capable`), database list, table list, schema-drift check; wrappers used by the CLI menu | `Security/`, `Scanning/` (static formatting helpers) |
| `Configuration/` | `ConfigLoader`: layered loading and source reporting | `ConnectionOptions` |
| `Context/` | Offline snapshot: `AiContextCatalog` (SQLite search), `ObjectContextReader` (path-safe reads), `ServerRegistryReader` | none of the DB layers |
| `Scanning/` | `DatabaseSchemaScanner`, `ScanCheckpoint`: read catalog views and write `ai-context/` (CLI only) | `Security/` (error formatting) |
| `AiContextRenderer.cs`, `CliMenu.cs`, `Program.cs`, `PreflightChecker.cs` | CLI: render snapshot, interactive menu, entry point, self-tests | everything below the CLI |

## Life of a `run_select` call

1. The hub forwards `sql__run_select` over stdio. The SDK deserialises the arguments and calls `SqlTools.RunSelect`.
2. `SqlTools` checks required arguments, then calls `ReadOnlyQueryExecutor.RunSelectAsync`.
3. **Configured?** Missing server/user/password returns `not configured` (no connection attempt).
4. **Allowed database?** The target database and every database named inside the query must pass `allowed_databases` (or must not be `master`, `msdb`, `model`, `tempdb`, `rdsadmin` when the list is empty).
5. **Validate.** `SelectOnlyValidator` parses the text with ScriptDom. Anything but one batch holding one `SELECT` (CTEs allowed) is rejected, as are `SELECT INTO`, `OPENROWSET/OPENQUERY/OPENDATASOURCE`, linked-server names, `xp_`/`sp_` functions, `NEXT VALUE FOR`, locking hints and similar. A rejected query never reaches the network.
6. **Connect** with `ApplicationIntent=ReadOnly`, `ApplicationName=mcp-sqlserver-readonly`.
7. **Guard** the session: `NOCOUNT`, `XACT_ABORT`, `DEADLOCK_PRIORITY LOW`, `LOCK_TIMEOUT`, `ROWCOUNT = max_rows + 1`; begin a transaction at the configured isolation (default `READ UNCOMMITTED`).
8. **Run the validated text** with a command timeout; read at most `max_rows`, cut long cells, stop at the response-size budget, mask sensitive columns.
9. **Roll back** the transaction unconditionally, reset `ROWCOUNT`, return the rows as JSON.

`lookup_rows`, `get_related_rows`, `describe_table` and the other tools do steps 3-9 through the same executor; they only differ in how the SELECT is produced.

## Why these choices

- **C# and ScriptDom.** The configured login may be an administrator (a dedicated read-only login is not always available), so the code must not rely on database permissions to stay read-only. Only .NET has Microsoft's own T-SQL parser. See [ADR 0001](adr/0001-keep-csharp-scriptdom.md).
- **Defence in depth.** The validator is the main control; the rolled-back transaction, session guards and read-only intent exist so that a parser/server disagreement still cannot persist a change. See [ADR 0002](adr/0002-select-only-defense-in-depth.md) and [SECURITY.md](SECURITY.md).
- **`READ UNCOMMITTED` by default.** Investigation must never block the application's writers on production. [ADR 0003](adr/0003-read-uncommitted-default.md).
- **Official MCP SDK.** Protocol framing, cancellation, error codes and annotations come from the SDK. [ADR 0004](adr/0004-official-mcp-sdk-and-hub-startup.md).
- **Offline snapshot kept, scan removed from MCP.** `search_context` and `get_object_context` answer instantly from `ai-context/`. Scanning writes files and takes minutes, so it is a CLI command, not an agent tool.
