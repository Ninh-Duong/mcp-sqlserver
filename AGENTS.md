# Agent instructions

`mcp-sqlserver` is a **read-only** SQL Server MCP (C#, .NET 10) used by `mcp-hub` to investigate issues. It can run one validated `SELECT` at a time and nothing else. **Keeping it read-only is the single most important property of this repository.**

Read before changing anything: [docs/RULES.md](docs/RULES.md) (invariants), [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) (layers), [docs/SECURITY.md](docs/SECURITY.md).

## Non-negotiable

1. SQL reaches the server **only** through `Security/ReadOnlyQueryExecutor`. No `SqlConnection`/`SqlCommand` anywhere else except `Scanning/` (offline CLI scan).
2. Every tool is `ReadOnly = true`. Never add a tool that writes.
3. `SelectOnlyValidator` is an AST allowlist (ScriptDom). Never replace it with regex or keyword lists; never widen it without a new bypass test and an ADR.
4. Values are parameters; identifiers come from the catalog and are quoted with `SqlIdentifier.Quote`.
5. Never log SQL text, parameters or rows. Never print to stdout (it is the MCP protocol).
6. The server must start and list tools without credentials (`not configured` errors only).
7. Never commit secrets, `dbconfig.json`, `ai-context/`, `logs/` or `publish/`.

If a task seems to require breaking one of these, stop and ask the user: it probably belongs somewhere else.

## Commands

```powershell
dotnet build McpSqlServer.slnx
dotnet test McpSqlServer.slnx            # must be green before every commit
dotnet run --project src/McpSqlServer -- serve      # MCP over stdio (what the hub starts)
dotnet run --project src/McpSqlServer -- scan --alias DEV --output ./ai-context   # offline snapshot; also -d <database>, --include-system, --resume
dotnet run --project src/McpSqlServer -- --preflight                              # dependency check + self-tests
```

`scripts/build.ps1` (restore, build, test), `scripts/run.ps1` (tests then run), `scripts/publish-singlefile.ps1`. The hub publishes with `dotnet publish src/McpSqlServer -c Release -o publish`.

Integration tests marked `[FactIfSqlServer]` skip themselves unless `MSSQL_SERVER`, `MSSQL_USERNAME`, `MSSQL_PASSWORD` are set; use a disposable server.

## Where things are

`src/McpSqlServer/`: `Mcp/` (tools, host) -> `Investigation/` (tool logic) -> `Security/` (validator + executor). `Configuration/` (config loading), `Context/` (offline snapshot), `Scanning/` (CLI scan). Details and allowed dependencies: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## When you change something

- Bug fix: write the failing test first. Validator bypass tests are never deleted.
- New or changed tool: code + [docs/TOOLS.md](docs/TOOLS.md) + `mcp-hub/docs/mcps/mcp-sqlserver.md` + `mcp-hub/config/hub.example.json` (RULES #8). `DocsConsistencyTests` and `ArchitectureRulesTests` fail if you forget.
- New config key: `Configuration/ConfigLoader.cs` + [docs/CONFIGURATION.md](docs/CONFIGURATION.md) + `dbconfig.example.json` + the hub template.
- Architecture change: add an ADR in `docs/adr/`.
- Keep it small: no interface with one implementation, no ORM, no speculative code ([docs/STANDARDS.md](docs/STANDARDS.md)).

## Hub

Hub integration, file by file: [docs/HUB_INTEGRATION.md](docs/HUB_INTEGRATION.md). Investigation workflow for agents: [docs/INVESTIGATION_PLAYBOOK.md](docs/INVESTIGATION_PLAYBOOK.md).
