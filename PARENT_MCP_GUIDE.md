# Parent MCP / Master Agent Integration Guide

## 1. Overview & Architecture

`mcp-sqlserver` is a dedicated, secure SQL Server worker node designed to be orchestrated by a **Parent MCP**, **Master Agent**, or **LLM Orchestrator** via the **Model Context Protocol (MCP)** over **STDIO**.

```
[ Master AI Agent / Parent MCP ]
               │
               ▼  (STDIO - JSON-RPC 2.0)
     [ mcp-sqlserver (Worker) ]
        │                 │
        ▼                 ▼
 [ Local Context ]   [ SQL Server (Target) ]
  ./ai-context/       Read-Only SELECT
  ./logs/error.log
```

---

## 2. Fail-Fast Configuration Contract

Before invoking `mcp-sqlserver`, the Parent MCP **MUST** provide connection credentials. If any required credential is missing, `mcp-sqlserver` **fails immediately on startup with exit code 1**. It will not start into an indeterminate or interactive state when run in `serve` mode.

### 2.1 Recommended Invocation (Environment Variables)

Parent MCPs should spawn the worker process with the following environment variables:

```bash
MSSQL_SERVER="10.0.0.15"              # Required: Host or IP (and optional port/instance)
MSSQL_USERNAME="sa"                   # Required: SQL Login username
MSSQL_PASSWORD="StrongPassword!2026"  # Required: SQL Login password
MSSQL_DATABASE="master"               # Optional: Initial database (defaults to 'master')
MSSQL_PORT="1433"                     # Optional: Port (defaults to 1433)
MSSQL_SERVER_ALIAS="PROD"             # Optional: Environment alias (e.g., DEV, UAT, PROD)
MSSQL_ENCRYPT="true"                  # Optional: true/false (defaults to true)
MSSQL_TRUST_CERT="false"              # Optional: true/false (defaults to false)
MSSQL_CONNECT_TIMEOUT="10"            # Optional: seconds (defaults to 10)
MSSQL_QUERY_TIMEOUT="15"              # Optional: seconds (defaults to 15)
```

Command line to spawn worker:
```bash
# Executable:
./McpSqlServer.exe serve

# Or via dotnet CLI:
dotnet run --project src/McpSqlServer -c Release -- serve
```

### 2.2 Alternative: Config File
Alternatively, ensure a valid `dbconfig.json` exists in the worker's working directory.

---

## 3. STDIO Communication & Safety Rules

1. **Pure JSON-RPC on STDOUT**: `stdout` is strictly reserved for newline-delimited MCP JSON-RPC messages. Never parse stdout for logs or diagnostic text.
2. **Zero Secret Leakage**: All passwords in connection strings, queries, and logs are automatically sanitized (`***`).
3. **Diagnostics via Stderr & Local Logs**:
   - `stderr`: Real-time operational messages (safe for log streaming).
   - `./logs/process.log`: Full process event log.
   - `./logs/error.log`: Dedicated error log capturing exceptions, failed queries, and security violations.

---

## 4. MCP Tool Specifications & Calling Rules

`mcp-sqlserver` exposes 5 specialized tools:

| Tool | Purpose | Required Inputs | Fail-Fast Behavior |
|------|---------|-----------------|-------------------|
| `test_connection` | Verify connectivity & retrieve SQL Server version | None | Returns `isError: true` with human-readable error if connection fails. |
| `list_databases` | Enumerate accessible databases | None | Returns catalog listing and access flags. |
| `list_tables` | List tables in a specific database | `database` (string) | Returns `isError: true` immediately if `database` is missing or empty. |
| `scan_server_context` | Full server scan & AI Context export | None | Generates token-optimized markdown in `./ai-context/`. |
| `execute_query` | Execute safe, read-only SQL queries | `query` (string) | Rejects modifying keywords immediately with error before DB roundtrip. |

---

### 4.1 Tool: `execute_query` (Critical Contract)

#### Rules & Constraints:
- **Strictly Read-Only**: Only pure `SELECT` or CTEs (`WITH ... SELECT`) are permitted.
- **Immediate Rejection**: Any query containing modifying keywords (`INSERT`, `UPDATE`, `DELETE`, `DROP`, `ALTER`, `TRUNCATE`, `EXEC`, `MERGE`, etc.) is blocked **before sending to SQL Server**. It returns an error response immediately.
- **Row Clamping**: Parameter `max_rows` defaults to `100` and is hard-clamped between `1` and `1,000`. Responses indicate `is_truncated: true` if rows exceed the limit.

#### Example Request:
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "execute_query",
    "arguments": {
      "database": "SalesDb",
      "query": "SELECT TOP 10 OrderId, CustomerId, TotalAmount FROM Orders WHERE OrderDate >= '2026-01-01' ORDER BY OrderDate DESC;",
      "max_rows": 10
    }
  }
}
```

#### Example Success Response:
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "content": [
      {
        "type": "text",
        "text": "{\n  \"success\": true,\n  \"database\": \"SalesDb\",\n  \"rowCount\": 1,\n  \"isTruncated\": false,\n  \"elapsedMs\": 12,\n  \"columns\": [\"OrderId\", \"CustomerId\", \"TotalAmount\"],\n  \"rows\": [{\"OrderId\": 101, \"CustomerId\": 5, \"TotalAmount\": 250.00}]\n}"
      }
    ],
    "isError": false
  }
}
```

---

## 5. Token-Optimized Agent Playbook

To minimize LLM token consumption and eliminate redundant round-trips:

```
[ Master Agent Workflow ]
          │
          ├─► 1. Check if `./ai-context/INDEX.md` exists?
          │      ├─► NO  ──► Call `scan_server_context` once.
          │      └─► YES ──► Proceed to Step 2.
          │
          ├─► 2. Read `./ai-context/servers/{ALIAS}/GLOBAL_TABLES_MAP.compact.md` directly.
          │      (Locate exact Database, Schema, Table, PK/FK, and Column names in ~300 tokens)
          │
          ├─► 3. Need business logic?
          │      Read specific `./ai-context/servers/{ALIAS}/databases/{DB}/views/{VIEW}.sql`
          │      or `procedures/{SP}.sql` file directly.
          │
          └─► 4. Need actual data inspection?
                 Call `execute_query` with a precise, indexed `WHERE` filter and `TOP N`.
```

### Agent Anti-Patterns (Do NOT Do This):
- ❌ **Do NOT** call `list_tables` or query `sys.columns` repeatedly if `./ai-context/` has already been generated.
- ❌ **Do NOT** execute `SELECT * FROM BigTable` without a `WHERE` condition or `TOP` limit.
- ❌ **Do NOT** send modifying SQL statements (`UPDATE`, `DELETE`, etc.)—they will fail immediately.
- ❌ **Do NOT** retry failed queries in a tight loop without modifying the SQL statement or correcting the missing parameter.

---

## 6. Error Codes & Fail-Fast Matrix

| Situation | JSON-RPC Return | Local Log | Recommended Agent Action |
|-----------|-----------------|-----------|--------------------------|
| Missing `query` parameter | `-32602` Invalid params | `./logs/error.log` | Check tool arguments and provide `query`. Do not retry unchanged. |
| Tool not found | `-32601` Method not found | `./logs/error.log` | Use one of the 5 supported tools. |
| Modifying keyword detected | Tool Result: `isError: true` | `./logs/error.log` | Rewrite query as read-only `SELECT`. |
| Invalid database / Table not found | Tool Result: `isError: true` | `./logs/error.log` | Check `GLOBAL_TABLES_MAP.compact.md` for exact spelling. |
| Database connection timed out | Tool Result: `isError: true` | `./logs/error.log` | Check network reachability or contact admin. |
