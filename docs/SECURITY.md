# Security model

**Goal:** an AI agent using this MCP can *look at* data and can never *change* it, even if the SQL Server login it uses is allowed to write.

The login is not trusted to enforce that (`check_connection` reports `write_capable`). The protection is in the code, in six layers. Each layer assumes the one before it failed.

## The six layers

| # | Layer | What it does | Where |
|---|---|---|---|
| 1 | **AST allowlist** | Parses the text with Microsoft ScriptDom. Accepts exactly one batch with exactly one `SELECT` (CTEs allowed). Rejects every other statement type, `SELECT INTO`, `OPENROWSET`/`OPENQUERY`/`OPENDATASOURCE`/`BULK`, four-part (linked-server) names, `xp_*`/`sp_*` calls, `NEXT VALUE FOR`, variable assignment, `FOR BROWSE`, `QUERYTRACEON`, lock-holding hints (`UPDLOCK`, `XLOCK`, `TABLOCK[X]`, `HOLDLOCK`, `PAGLOCK`, `SERIALIZABLE`, `REPEATABLEREAD`), and table functions that read files, traces or the log (`fn_xe_file_target_read_file`, `fn_get_audit_file`, `fn_trace_gettable`, `fn_dblog`, ...). The text that was parsed is the text that is sent. | `Security/SelectOnlyValidator.cs` |
| 2 | **Always-rollback transaction** | Every statement runs inside `BEGIN TRAN` and ends with `ROLLBACK`, also when it succeeds. If something slipped past layer 1, its effect is undone. | `Security/ReadOnlyQueryExecutor.cs` |
| 3 | **Session guards** | `XACT_ABORT ON`, `LOCK_TIMEOUT` (default 3 s), `DEADLOCK_PRIORITY LOW`, `ROWCOUNT = max_rows + 1`, `NOCOUNT ON`, command timeout (default 30 s), isolation `READ UNCOMMITTED` (or `READ COMMITTED`). Production writers are not blocked; a runaway query is stopped. | executor |
| 4 | **Connection** | `ApplicationIntent=ReadOnly` (routes to a read replica when one exists) and `ApplicationName=mcp-sqlserver-readonly` so a DBA can find and audit the sessions. | `ConnectionOptions.BuildConnectionString` |
| 5 | **Scope** | `allowed_databases`; with no list, `master`, `msdb`, `model`, `tempdb`, `rdsadmin` are refused. Databases named inside a query (`Other.dbo.T`) are checked as well. Object names are only used after the catalog confirmed them, quoted with `SqlIdentifier.Quote`; values are always parameters. | executor, `Investigation/` |
| 6 | **Output limits** | `max_rows` (1-1000), 2,000 characters per cell (60,000 for source code, 190,000 for a plan), 200,000 characters per response, values of columns matching `masked_column_patterns` replaced by `***`. | executor |

## Known bypasses that are closed (and tested)

The previous implementation used a regular-expression blocklist and could be bypassed. These are now regression tests in `SelectOnlyValidatorTests`:

- Comment markers inside string literals: `SELECT '--'; DROP TABLE t`, `SELECT '/*'; DELETE FROM t; SELECT '*/'`. The old code removed "comments" before strings and then sent the original text, so the server executed what was never checked.
- `SELECT ... INTO dbo.NewTable` / `INTO #tmp` (creates objects).
- Multi-statement batches that start with `SELECT`: `DISABLE TRIGGER`, `DBCC`, `BACKUP`, `RESTORE`, `KILL`, `RECONFIGURE`, `USE`, `SET IDENTITY_INSERT`, `WAITFOR`, `WRITETEXT`, `UPDATETEXT`, `BEGIN TRAN`.
- Pass-through: `OPENQUERY(linked, '...')`, `OPENROWSET(BULK ...)`, `OPENDATASOURCE`, linked-server names.
- `NEXT VALUE FOR` (advances a sequence), `SELECT @a = ...`, lock hints, `QUERYTRACEON`.
- Path traversal in the offline snapshot tools (`..`, rooted paths, wildcards) and `output_directory` / `base_directory` arguments (removed: the snapshot folder is configuration, not an argument).

## Residual risk (be honest about it)

- **Extended stored procedures through a user function.** A scalar user-defined function is allowed (investigations often need them). In SQL Server a function cannot modify tables, but it can call an extended stored procedure. Mitigation: use the read-only login below, and keep `xp_cmdshell` disabled (default).
- **Parser/server disagreement.** ScriptDom is Microsoft's parser, but it is not the engine. Layers 2-4 exist for this case; only the always-rollback transaction undoes data changes, it cannot undo effects outside the transaction.
- **The data itself.** Results contain real business data and are sent to whichever model the agent uses. Mask columns (`masked_column_patterns`), restrict `allowed_databases`, and decide which environments the hub exposes. The hub's own job specialists do not use this MCP until its role is added deliberately.
- **A shared admin login.** `check_connection` warns when the login is `write_capable`. The safest setup is a login with `db_datareader`, `DENY` on writes and execute: see [readonly-login.sql](readonly-login.sql). This is a recommendation, not a requirement, so the MCP works where such a login cannot be created.

## Reporting

Do not put exploit details or credentials in public issues. Report to the repository owner. Include the request that was wrongly accepted; the fix is a failing test in `SelectOnlyValidatorTests` first.
