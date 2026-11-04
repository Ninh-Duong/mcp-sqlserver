# Architecture decision records

One short file per decision that shapes the architecture. A new layer, a reversed dependency, a new way to reach SQL Server, or a change of runtime/SDK needs a new record (RULES #11). Never rewrite history: to change a decision, add a record that supersedes the old one and mark the old one `Superseded by NNNN`.

Format: Status, Context, Decision, Consequences.

| # | Decision | Status |
|---|---|---|
| [0001](0001-keep-csharp-scriptdom.md) | Keep C#, use Microsoft ScriptDom to validate SQL | Accepted |
| [0002](0002-select-only-defense-in-depth.md) | SELECT-only enforced by layered defence, not by the login | Accepted |
| [0003](0003-read-uncommitted-default.md) | `READ UNCOMMITTED` is the default isolation | Accepted |
| [0004](0004-official-mcp-sdk-and-hub-startup.md) | Official MCP SDK, hub start-up contract, scan leaves the MCP | Accepted |
