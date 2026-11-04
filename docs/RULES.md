# Rules

These rules are **invariants**. They exist so that this MCP can never drift from its purpose: a SQL Server connector that can **only read**. Most of them are enforced by tests in [ArchitectureRulesTests.cs](../tests/McpSqlServer.Tests/ArchitectureRulesTests.cs); a failing test means the change breaks the architecture. Fix the change. If the decision is deliberate, write an [ADR](adr/) and update this file and the test in the same pull request.

Keywords: **MUST** / **MUST NOT** are absolute.

## A. Read-only invariants

1. **One gateway.** `SqlConnection`, `SqlCommand` and `SqlDataReader` MUST NOT appear anywhere except `src/McpSqlServer/Security/` (the read-only gateway) and `src/McpSqlServer/Scanning/` (the offline CLI scan). Every MCP tool reaches SQL Server only through `ReadOnlyQueryExecutor`. `Investigation/`, `Mcp/`, `Context/`, `Configuration/` and `SqlServerService.cs` MUST NOT reference `Microsoft.Data.SqlClient`.
2. **Tools are read-only forever.** Every `[McpServerTool]` MUST declare `ReadOnly = true`. A tool with `ReadOnly = false` or `Destructive = true` MUST NOT be added. A capability that writes (to the database, or files from a tool) does not belong in this MCP; the CLI `scan` is the only writer and it only writes local snapshot files.
3. **The validator is an AST allowlist.** `SelectOnlyValidator` MUST stay based on the ScriptDom parser and accept only one batch with one `SelectStatement`. Keyword lists and regular expressions MUST NOT be used to decide whether SQL is safe. A change to the validator MAY only narrow what is accepted; it MUST come with a new bypass test and an ADR if it widens anything. Existing tests in `SelectOnlyValidatorTests` MUST NOT be deleted.
4. **The executor keeps all its layers.** `ReadOnlyQueryExecutor` MUST: validate every statement and execute the validated text; run it inside a transaction that is always rolled back (never committed); set `SET LOCK_TIMEOUT`, `SET ROWCOUNT`, `SET XACT_ABORT`, the isolation level, a command timeout; connect with `ApplicationIntent=ReadOnly` and the application name `mcp-sqlserver-readonly`; cap rows, cell size and response size; mask sensitive columns; enforce `allowed_databases`.
5. **No string-built SQL.** Values MUST be parameters. Identifiers (table, column) MUST come from the catalog and be quoted with `SqlIdentifier.Quote`; user text MUST NOT be concatenated into SQL. The one interpolated statement is the `lookup_rows` builder.
6. **Never log SQL or data.** `Logger` calls MUST NOT include SQL text, parameters, filters, key values or rows (counts are fine). `stdout` is reserved for the MCP protocol; everything else goes to `stderr` or `./logs/`. Only `CliMenu`, `Program` and `PreflightChecker` may use `Console.Out`.

## B. Hub contract

7. **Start without credentials.** The server MUST start and list every tool when the account is not filled in. DB tools then return an error containing `not configured`. It MUST NOT call `Environment.Exit` because of missing configuration.
8. **One contract, kept in sync.** Adding, renaming or removing a tool MUST update, in the same change: the tool code, [TOOLS.md](TOOLS.md), the hub contract `mcp-hub/docs/mcps/mcp-sqlserver.md`, the tool allowlist in `mcp-hub/config/hub.example.json`, and the tests. `DocsConsistencyTests` checks code against `TOOLS.md`.
9. **Config priority and reporting.** Per value: environment `MSSQL_*` > hub section `mssql` in `CENTRAL_CONFIG_PATH` > local `dbconfig.json` > default; an empty string is unset; `check_connection` reports the source (`env|hub|local|default|unset`) and never a value.

## C. Process

10. **Test first.** Every bug fix starts with a failing test. Every new tool has unit tests for its failure paths that run without a database; the tests that need a real server are `[FactIfSqlServer]` and skip themselves.
11. **ADR for architecture changes.** A new layer, a reversed dependency, a new SQL entry point, a change of runtime or SDK MUST have a new file in [adr/](adr/).
12. **Run gate.** `scripts/run.ps1` and `scripts/build.ps1` run the unit tests first and stop on failure. The in-process self-tests (`PreflightChecker.RunCoreSelfTests`) run for `--preflight` and the interactive modes (`menu`, `scan`); `serve` skips them so the hub can start the process quickly and without a writable working directory.

## D. Inherited engineering rules

13. **No secrets in git.** Never commit real usernames, passwords, tokens, connection strings or `dbconfig.json`. Logs and exceptions MUST redact passwords (`Logger.Sanitize`; call `Logger.SetActivePassword` as soon as configuration is loaded).
14. **Relative paths only.** No hardcoded drive or home paths in source, tests, scripts or configuration. Resolve against the working directory or `AppContext.BaseDirectory`. A relative `CENTRAL_CONFIG_PATH` resolves against the repository root.
15. **Ponytail.** No abstraction without a second user, no interface for a single implementation, no ORM. Short async methods with `CancellationToken`. Delete speculative code.

## Quick checklist for a pull request

- [ ] No new `SqlCommand`/`SqlConnection` outside `Security/` and `Scanning/`.
- [ ] New tool? `ReadOnly = true`, entry in `TOOLS.md`, hub contract and allowlist updated.
- [ ] Touched the validator or executor? New bypass/limit test, and `SECURITY.md` updated.
- [ ] No SQL text or row data in any log line.
- [ ] `dotnet test McpSqlServer.slnx` is green.
