# Configuration

## Where values come from

Per value, the first one that is set wins (an empty string counts as not set):

1. **Environment** `MSSQL_*`
2. **Hub section** `mssql` in the file named by `CENTRAL_CONFIG_PATH` (what `mcp-hub` uses)
3. **Local file** `dbconfig.json` in the working directory (stand-alone use and the CLI menu)
4. **Default**

A missing or unreadable hub file is a warning, not a failure: the server falls back to the local file. A relative `CENTRAL_CONFIG_PATH` resolves against the working directory and then the repository root.

`check_connection` returns `config_source` per group (`server`, `credentials`, `limits`) with one of `env|hub|local|default|unset`. It never returns a value or a path.

## Keys

JSON keys are case-insensitive and `_`/`-` are ignored, so `trust_server_certificate`, `TrustServerCertificate` and `trust-server-certificate` are the same key.

| Key | Env variable | Default | Meaning |
|---|---|---|---|
| `server_alias` | `MSSQL_SERVER_ALIAS` | `DEV` | Label of the environment (used by the offline snapshot) |
| `server` | `MSSQL_SERVER` | - | Host or `host,port` |
| `port` | `MSSQL_PORT` | `1433` | Ignored when `server` already has a port or instance |
| `username` | `MSSQL_USERNAME` | - | SQL login |
| `password` | `MSSQL_PASSWORD` | - | Never logged; redacted in errors |
| `database` | `MSSQL_DATABASE` | `master` | Initial catalog for server-level catalog queries. Tools always pass their own `database` |
| `encrypt` | `MSSQL_ENCRYPT` | `true` | TLS |
| `trust_server_certificate` | `MSSQL_TRUST_SERVER_CERTIFICATE` (legacy `MSSQL_TRUST_CERTIFICATE`) | `false` | Needed for self-signed certificates |
| `connect_timeout` (`connect_timeout_seconds`) | `MSSQL_CONNECT_TIMEOUT` | `10` | 1-120 s |
| `query_timeout` (`query_timeout_seconds`) | `MSSQL_QUERY_TIMEOUT` | `30` | 1-120 s per statement |
| `allowed_databases` | `MSSQL_ALLOWED_DATABASES` (comma separated) | empty | Only these databases can be queried. Empty = all except `master`, `msdb`, `model`, `tempdb`, `rdsadmin` |
| `isolation` | `MSSQL_ISOLATION` | `read_uncommitted` | or `read_committed`. See [ADR 0003](adr/0003-read-uncommitted-default.md) |
| `lock_timeout_ms` | `MSSQL_LOCK_TIMEOUT_MS` | `3000` | How long a read may wait for a lock (0-60000) |
| `max_rows` | `MSSQL_MAX_ROWS` | `100` | Default row cap per call; hard cap 1000 |
| `masked_column_patterns` | `MSSQL_MASKED_COLUMN_PATTERNS` | `password\|passwd\|pwd\|token\|secret\|salt\|hash\|api_?key` | Regex on column names; matching values are returned as `***` |
| `context_directory` | `MSSQL_CONTEXT_DIRECTORY` | `./ai-context` | Offline snapshot folder |

Server, username and password are required for database tools. Everything else has a default.

## Examples

**Hub** (`config/private/mcps/mcp-sqlserver.json`, section `mssql`; secrets are filled in by the user, never in chat):

```json
{
  "mssql": {
    "server": "db.example.internal",
    "port": 1433,
    "username": "mcp_readonly",
    "password": "",
    "trust_server_certificate": true,
    "allowed_databases": ["Crm", "Billing"],
    "isolation": "read_uncommitted",
    "max_rows": 100
  }
}
```

**Stand-alone** (`dbconfig.json`, git-ignored; copy `dbconfig.example.json`) uses the same keys at the top level.

**Environment only**: `MSSQL_SERVER`, `MSSQL_USERNAME`, `MSSQL_PASSWORD` (and any other `MSSQL_*`).

## Notes

- The password is never written to disk by this program. `dbconfig.json` and `config/private/` are git-ignored.
- Warnings found while loading (bad numbers, missing hub file, unreadable file) go to `stderr` and `./logs/process.log`, without values.
- Environment overrides files. This changed from the earlier versions, where `dbconfig.json` silently won over environment variables.
