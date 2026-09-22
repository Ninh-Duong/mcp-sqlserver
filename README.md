# MCP SQL Server

A lightweight, secure Microsoft SQL Server connector and schema intelligence tool supporting dual modes: **Interactive CLI Menu** (for direct human operation) and **MCP Server** (for AI agents such as Claude Desktop, Antigravity, and Codex).

---

## 1. What does this repository do?

This repository provides high-performance Microsoft SQL Server connectivity and schema extraction using raw ADO.NET (.NET 10):
- **Minimalist & High Performance (Ponytail)**: No bulky ORM overhead, querying system catalogs directly.
- **Zero-Leak Security**: No passwords written to disk, automatic terminal masking (`*`), and strict redaction (`***`) across all logs and exception traces.
- **Run Gate**: Enforces 100% passing unit tests and in-process self-checks before application launch.
- **100% Relative Paths**: Seamless cross-platform portability.

---

## 2. Key Capabilities

1. **Interactive CLI Menu**:
   - Connection management (Server, Username, masked Password, Port, Database, TLS/Certificate flags).
   - Instant connection health check, latency measurement (ms), and SQL Server version retrieval.
   - Database enumeration (categorizing System vs. User databases, ONLINE/OFFLINE state, access permissions).
   - Table enumeration in any target database (with schema names: `dbo`, `sales`, etc.).
2. **Multi-Server Scan & Token-Optimized AI Context Generation (DEV, UAT, PROD...)**:
   - Automatically scans all databases: Tables, Columns (with formatted Data Types, Nullable, Identity, PK, FK, and `MS_Description`).
   - Extracts complete SQL logic for all Views and Stored Procedures.
   - **Progressive Disclosure Architecture**:
     - `INDEX.md`: Central Master Index aggregating all servers (DEV, UAT, PROD) for instant environment mapping.
     - `GLOBAL_TABLES_MAP.compact.md`: One line per table, allowing AI agents to reverse-search (`grep`) any column or table across the entire instance in 0.1s.
     - `schema.compact.md`: Ultra-dense schema lookup format (~60% token savings compared to JSON or DDL).
     - `views/*.sql` & `procedures/*.sql`: Isolated SQL files allowing AI agents to read only the specific routine needed.
3. **MCP STDIO Server**:
   - Provides 5 tools for AI agents:
     1. `test_connection`: Verify server connectivity and fetch latency/version.
     2. `list_databases`: Enumerate databases visible to current login.
     3. `list_tables`: Enumerate tables in a specific database.
     4. `scan_server_context`: Scan entire SQL instance and export token-optimized AI Context documentation.
     5. `execute_query`: Execute read-only SQL queries (`SELECT`) with automated safety guards against data mutation.
   - Strict STDIO isolation: JSON-RPC over `stdout`, diagnostics over `stderr` and `./logs/process.log`.

---

## 3. Quick Start

### Prerequisites
- **.NET 10 SDK** or higher (`dotnet --version`).

---

### Option 1: Interactive CLI Menu

Run from terminal:

```pwsh
dotnet run --project ./src/McpSqlServer -- menu
```

*(Or use the automated test gate runner: `powershell -File ./scripts/run.ps1 menu`)*

**Menu Controls:**
- Press **`1`** + Enter: Configure connection (`Server`, `Username`, `Password`, `Server Alias` like DEV/UAT/PROD).
- Press **`2`** + Enter: Test connection.
- Press **`3`** + Enter: List accessible databases.
- Press **`4`** + Enter: List tables in a database.
- Press **`5`** + Enter: **Scan entire server & export AI Context docs (DEV/UAT/PROD)**.
- Press **`0`** + Enter: Exit.

**Headless Automation (CLI Scan):**
```pwsh
dotnet run --project ./src/McpSqlServer -- scan --server-alias DEV --output ./ai-context
```

> [!TIP]
> **Automatic configuration via `dbconfig.json`:**
> Copy [`dbconfig.example.json`](file:///d:/VisualStudioCode/mcp-sqlserver/dbconfig.example.json) to `dbconfig.json` in the project root:
> ```json
> {
>   "ServerAlias": "DEV",
>   "Server": "sql.example.internal",
>   "Username": "reader",
>   "Password": "YourPassword123",
>   "Database": "master",
>   "Port": 1433,
>   "Encrypt": true,
>   "TrustServerCertificate": false
> }
> ```
> `dbconfig.json` is protected by `.gitignore` to prevent accidental credential commits.

---

### Option 2: MCP Server for AI Agents (Claude Desktop, Antigravity, Codex)

1. Set environment variables:
   ```pwsh
   $env:MSSQL_SERVER_ALIAS = "DEV"
   $env:MSSQL_SERVER = "sql.example.internal"
   $env:MSSQL_USERNAME = "your_username"
   $env:MSSQL_PASSWORD = "your_password"
   $env:MSSQL_TRUST_CERTIFICATE = "true"  # Enable if using self-signed SSL
   ```

2. Add to your AI Client's configuration file:
   ```json
   {
     "mcpServers": {
       "sqlserver": {
         "command": "dotnet",
         "args": ["run", "--project", "./src/McpSqlServer", "--", "serve"],
         "env": {
           "MSSQL_SERVER_ALIAS": "DEV",
           "MSSQL_SERVER": "sql.example.internal",
           "MSSQL_USERNAME": "your_username",
           "MSSQL_PASSWORD": "your_password",
           "MSSQL_TRUST_CERTIFICATE": "true"
         }
       }
     }
   }
   ```

---

### Option 3: Running Unit Tests

```pwsh
dotnet test ./tests/McpSqlServer.Tests
```
