# 0001. Keep C#, use Microsoft ScriptDom to validate SQL

Status: Accepted

## Context
The hub's other children are written in Python or Node. This one is C# (.NET 10). The requirement is absolute: the MCP must only ever run a read (`SELECT`), never `DELETE`, `TRUNCATE`, `UPDATE` or anything else, and it may have to run with an existing administrator login because a read-only login is not always available. The first implementation checked SQL with regular expressions and could be bypassed (comment markers inside string literals, `SELECT INTO`, multi-statement batches, `OPENQUERY`, ...).

## Decision
Keep C#. Validate SQL by parsing it with `Microsoft.SqlServer.TransactSql.ScriptDom` (Microsoft's own T-SQL parser) and accept only a single batch holding a single `SelectStatement`. Python and Node have no equivalent parser: they would fall back to regexes or third-party grammars that do not parse T-SQL exactly as the server does.

## Consequences
- The safety decision is made on an AST, not on text patterns; the validated text is the text that is sent.
- A .NET 10 SDK is needed to build (the hub's `prepare` step runs `dotnet publish`).
- The MCP is a different language than its siblings; the contract with the hub is protocol-level (stdio, tools, config section), so this is invisible to it.
- ScriptDom is not the engine: see [0002](0002-select-only-defense-in-depth.md).
