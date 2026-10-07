# MCP SQL Server

A lightweight, secure Microsoft SQL Server connector and schema explorer. It works in two modes:
- **Interactive CLI Menu**: For developers to explore databases and export schemas directly in the terminal.
- **MCP Server**: For AI assistants (Claude Desktop, Cursor, Antigravity, etc.) to understand database structures and query data safely via the Model Context Protocol (MCP).

---

## What Does This Project Do?

1. **Connects safely to SQL Server**: Automatically masks passwords and prevents secret leakage in logs.
2. **Exports Database Schemas for AI (`./ai-context`)**: Scans tables, columns, primary/foreign keys, indexes, views, stored procedures, and triggers into compact Markdown and SQLite catalog files without bloating AI token limits.
3. **Searches Schemas Instantly**: Find any table, column, or routine in milliseconds.
4. **Executes Safe Read-Only Queries**: Runs `SELECT` queries with row limits while blocking modifying statements (`INSERT`, `UPDATE`, `DELETE`, `DROP`, etc.).

---

## Quick Start (3 Steps)

### Step 1: Prerequisites
- Install [.NET 10 SDK](https://dotnet.microsoft.com/download) (`dotnet --version`).

### Step 2: Configuration
Copy [`dbconfig.example.json`](./dbconfig.example.json) to `dbconfig.json` in the project root:

```json
{
  "ServerAlias": "DEV",
  "Server": "sql.example.internal",
  "Username": "reader",
  "Password": "YourPassword123",
  "Database": "master",
  "Port": 1433,
  "Encrypt": true,
  "TrustServerCertificate": false
}
```

> `dbconfig.json` is ignored by `.gitignore` so your credentials are never committed.

### Step 3: Run

#### Mode A: Interactive Menu (For Humans)
Run from PowerShell:
```pwsh
powershell -File ./scripts/run.ps1 menu
```
Or with `dotnet`:
```pwsh
dotnet run --project ./src/McpSqlServer -- menu
```

**Menu Options:**
- `1`: Configure connection settings
- `2`: Test connection and check latency
- `3`: Check DB migration drift
- `4`: List databases
- `5`: List tables in a database
- `6`: Scan entire server & export AI documentation (`./ai-context/`)
- `0`: Exit

---

#### Mode B: Connect to AI Tools (MCP Server)
Add this to your AI client configuration (e.g., `claude_desktop_config.json` or Antigravity/Cursor MCP config):

```json
{
  "mcpServers": {
    "sqlserver": {
      "command": "dotnet",
      "args": ["run", "--project", "./src/McpSqlServer", "--", "serve"],
      "env": {
        "MSSQL_SERVER": "sql.example.internal",
        "MSSQL_USERNAME": "reader",
        "MSSQL_PASSWORD": "YourPassword123",
        "MSSQL_DATABASE": "master",
        "MSSQL_TRUST_CERT": "false"
      }
    }
  }
}
```

**Tools Available to the AI:**
- `test_connection`: Verifies server connection and measures response latency.
- `list_databases`: Lists accessible databases.
- `list_tables`: Lists tables inside a specified database.
- `scan_server_context`: Scans schemas and writes compact AI docs to `./ai-context/`.
- `search_context`: Fast search for any table, column, or stored procedure.
- `execute_query`: Runs read-only `SELECT` queries (enforces safety guards and row limits).

---

## Security & Safety Guarantees

- **Strict Read-Only Enforcement**: Blocks mutating queries (`INSERT`, `UPDATE`, `DELETE`, `DROP`, `ALTER`, `TRUNCATE`, `EXEC`) before sending them to SQL Server.
- **Zero-Leak Passwords**: Passwords in connection strings, errors, and logs are automatically redacted (`***`).
- **100% Relative Paths**: No machine-specific or absolute drive paths (`C:\...`, `D:\...`).
- **Run Gate**: Every run verifies that 100% of unit tests pass before startup.

---

## Running Tests

Run all unit tests:
```pwsh
dotnet test ./tests/McpSqlServer.Tests/McpSqlServer.Tests.csproj
```
