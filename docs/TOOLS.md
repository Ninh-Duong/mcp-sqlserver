# Tools

13 tools, all **read-only** (`readOnlyHint = true`). Behind `mcp-hub` they are exposed as `sql__<tool>`. Every result is one text block of compact JSON with `snake_case` keys and no null fields. A failure sets the MCP error flag (`isError`) and carries `error_message`. Without credentials every database tool answers with an error containing `not configured`.

This file is checked against the code by `DocsConsistencyTests`: every tool needs a `### <name>` section here, and no section may describe a tool that does not exist.

Common limits: rows 1-1000 (default from `max_rows`, 100), 2,000 characters per cell, 200,000 characters per response; columns matching `masked_column_patterns` return `***`; a database outside `allowed_databases` is refused.

## Server and catalog

### check_connection
Connectivity, server version and whether the login could write. Call it first.
- Input: none.
- Output: `{success, connected, elapsed_ms, server_version, write_capable, warnings[], config_source{server, credentials, limits}, error_message?}`. `config_source` values: `env|hub|local|default|unset` (never the values).
- `write_capable: true` means the login is sysadmin/db_owner or has INSERT/UPDATE/DELETE. The MCP still cannot write; see [SECURITY.md](SECURITY.md).

### list_databases
Databases this MCP may query.
- Input: none.
- Output: `{success, visible_count, system_count, other_count, scope, databases[{database_id, name, state, has_access, is_system}]}`.

### list_tables
Tables and views with approximate row counts.
- Input: `database` (required), `schema?`, `name_like?` (fragment or LIKE pattern), `limit?` (1-500, default 100).
- Output: `{success, database, count, truncated, tables[{schema, name, type: table|view, approx_rows}]}`.

### describe_table
Live structure of one table or view.
- Input: `database`, `table` (`Orders` or `dbo.Orders`).
- Output: `{success, database, schema, name, type, approx_rows, columns[{name, type, nullable, is_primary_key, is_identity, is_computed, default?}], foreign_keys[{name, direction: references|referenced_by, from_table, from_columns[], to_table, to_columns[]}], indexes[{name, type, is_unique, is_primary_key, key_columns}], triggers[{name, is_disabled, instead_of}]}`.
- An unqualified name that exists in several schemas and not in `dbo` is reported as ambiguous.

### find_columns
Which tables hold a column ("where is `OrderId` stored?").
- Input: `name_like` (fragment or pattern), `database?` (omit to search every allowed database, at most 50), `limit?` (1-200, default 50).
- Output: `{success, count, truncated, searched_databases[], matches[{database, schema, table, column, type, nullable}], errors[]}`.

## Reading data

### lookup_rows
Read rows of one table or view without writing SQL.
- Input: `database`, `table`, `filters?` = `[{column, op, value}]` combined with AND (max 10), `order_by?` = `[{column, desc}]`, `columns?`, `limit?` (1-1000, default 20).
- Operators: `=` `!=` `>` `>=` `<` `<=` `like` `contains` (escaped `%x%`) `in` (value array, max 50) `is_null` `is_not_null`. `= null` means `IS NULL`.
- Output: `{success, database, row_count, truncated, elapsed_ms, columns[], rows[], sql}`; `sql` is the generated statement with parameter placeholders (no values).
- Column and table names must exist in the catalog (case-insensitive match); values are parameters (varchar columns bind as varchar so indexes can be used).
- Example: `{"database":"Crm","table":"dbo.Orders","filters":[{"column":"OrderId","op":"=","value":123}]}`.

### get_related_rows
One row plus everything connected through foreign keys.
- Input: `database`, `table`, `key_value`, `key_column?` (default: the single-column primary key), `depth?` (1 or 2), `rows_per_relation?` (1-20, default 5).
- Output: `{success, database, table, root{}, related[{depth, direction, foreign_key, from_table, table, row_count, truncated, rows[]}], queries_used, budget_reached}`. At most 24 queries; `budget_reached: true` means more relations exist. Depth 2 expands the first two rows of each related set.

### run_select
Free-form single SELECT, when the other tools cannot express the question.
- Input: `database`, `query`, `max_rows?` (1-1000).
- Output: `{success, database, row_count, truncated, elapsed_ms, columns[], rows[]}`. Duplicate or unnamed result columns are renamed (`name_2`, `column_3`).
- Accepted: one batch, one `SELECT` (CTEs allowed). Everything else is rejected before any network call; see [SECURITY.md](SECURITY.md).

### explain_select
Estimated execution plan, the statement is **not** executed.
- Input: `database`, `query`.
- Output: `{success, database, estimated_cost, operators[{physical, logical, estimated_rows, cost, object?}] (12 most expensive), warnings[] (scans, plan warnings), missing_indexes[]}`.
- Needs the SHOWPLAN permission in that database.

### get_object_definition
Live source of a view, procedure, function or trigger.
- Input: `database`, `name` (`sp_GetOrder` or `dbo.sp_GetOrder`).
- Output: `{success, database, objects[{schema, name, type, modified, definition, truncated}]}` (up to 5 matches, 60,000 characters each). Encrypted modules say so.

## Offline snapshot (no database access)

Built by the command-line `scan`; see [README.md](../README.md#offline-snapshot).

### search_context
Instant search of the snapshot.
- Input: `query`, `server_alias?`, `database?`, `target?` (`all|table|column|routine|procedure|view|function|trigger|dependency`), `limit?` (1-50, default 10), `include_details?`.
- Output: `{query, total_matches, matches[{server_alias, database, type, name, details, relative_path}], message, success}`. No snapshot (`ai-context/catalog.db`), an empty query or an unreadable catalog sets the error flag (`success: false`); zero matches is a success.

### get_object_context
Compact definition of one object from the snapshot.
- Input: `database`, `name`, `server_alias?`, `type?`.
- Output: plain text (Markdown or SQL). Names with path separators, `..` or wildcards are rejected (`Invalid ...`), and a database or object missing from the snapshot answers `... not found ...`; both set the error flag and return `{success: false, error_message}`.

### check_schema_drift
Compare live databases with the snapshot (new EF migrations, DDL changes).
- Input: none (the snapshot folder comes from `context_directory`).
- Output: `{success, server_alias, drifted_databases[{database_name, has_drift, snapshot_migration_id, current_migration_id, snapshot_count, current_count, reason}], up_to_date_databases[]}`.
