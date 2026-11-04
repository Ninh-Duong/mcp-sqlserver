# Engineering standards

How code, tools and tests are written in this repository. The hub-side rules are in `mcp-hub/docs/child-mcp-compatibility.md`; the invariants are in [RULES.md](RULES.md).

## Runtime and layout

- C# on .NET 10, `Nullable` and `ImplicitUsings` on. Packages: `Microsoft.Data.SqlClient`, `Microsoft.SqlServer.TransactSql.ScriptDom`, `Microsoft.Data.Sqlite` (offline catalog), `ModelContextProtocol` + `Microsoft.Extensions.Hosting` (server).
- Folders and their allowed dependencies: [ARCHITECTURE.md](ARCHITECTURE.md). Namespaces: the root `McpSqlServer`, plus `.Security`, `.Investigation`, `.Mcp`, `.Configuration`.
- Entry points (`Program.cs`): `serve` (MCP over stdio, what the hub starts), `scan` (offline snapshot), `menu` (interactive, default), `--preflight` (dependency check and self-tests).

## Tools

- **Name:** `snake_case` verb phrase (`list_*`, `describe_*`, `find_*`, `lookup_*`, `get_*`, `run_*`, `explain_*`, `check_*`, `search_*`). No prefix: the hub adds `sql__`.
- **Annotations:** `ReadOnly = true, Destructive = false, Idempotent = true`; `OpenWorld = true` when the tool reaches the database, `false` for the offline snapshot. See rule 2.
- **Arguments:** `snake_case` C# parameter names (they are the MCP argument names), simple JSON types with defaults, bounded numbers (`limit` 1-N), `required` only for what is truly required. Describe every parameter with `[Description]`; the text is for an agent that has never seen the code.
- **Description:** what it answers, when to use it instead of another tool, the limits. No marketing.
- **Output:** one text block of compact JSON, `snake_case`, nulls omitted, built from a record in `Investigation/` or `Models/` and serialised by `ToolResults.From`. Lists are capped and carry `truncated`. Never return a stack trace.
- **Errors:** `ToolResults.From(result, success: false)` or `ToolResults.Error(...)` sets the MCP error flag. Missing required argument: `Parameter 'x' is required.` Missing configuration: text containing `not configured` (the hub smoke check turns that into a warning). Error text names the problem and the fix, and never contains credentials, SQL text or row data.
- **Server instructions** (`McpHost.Instructions`) stay under 1,500 characters and describe the workflow, not the schema.

## Code

- Short async methods, `CancellationToken` through every I/O call. No interface with a single implementation, no ORM, no speculative abstraction.
- Exceptions are caught at the tool boundary only to produce a safe message; `OperationCanceledException` is rethrown.
- Logging: `Logger.Process/Info/Warn/Error` to `stderr` and `./logs/`. Tool name, error type, counts and timings only (rule 6).
- Fixed SQL lives in `internal const string` fields next to the code that uses it, so `AllFixedSql` can prove in a test that each one passes the validator.

## Tests

- Framework: xUnit. Run `dotnet test McpSqlServer.slnx` (or `scripts/build.ps1`).
- **No database needed** for everything except tests marked `[FactIfSqlServer]`, which skip themselves unless `MSSQL_SERVER`, `MSSQL_USERNAME`, `MSSQL_PASSWORD` are set. Use a disposable server for those; they only read.
- Layers of tests: validator bypass tests, executor pre-connection tests, SQL-builder tests, config loader tests, `McpStdioEndToEndTests` (starts the real server and speaks JSON-RPC), `ArchitectureRulesTests`, `DocsConsistencyTests`.
- A bug fix starts with a failing test. Bypass tests are never deleted.

## Docs

- A change in behaviour updates the docs in the same pull request: [TOOLS.md](TOOLS.md) for tools, [CONFIGURATION.md](CONFIGURATION.md) for keys, [SECURITY.md](SECURITY.md) for protection, the hub contract for anything the hub sees.
- Docs are written in English, for the engineers and agents that operate the hub. Use repo-relative paths and key names, never values or personal paths.

## Versioning

`Version` in `McpSqlServer.csproj` is reported as the MCP server version. Increase the minor version when a tool or key is added, the major version when one is removed or changes meaning, and mention it in the pull request.
