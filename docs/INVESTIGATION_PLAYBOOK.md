# Investigation playbook

How to use the tools, together with the log, source, ticket and pull-request MCPs, to get from a symptom to a root cause. Written for the agent that is about to call them. The short form is `McpHost.Instructions`.

## Principles

1. **Read-only, so be curious.** Nothing you run here can change data. Still: results are real business data; quote only what the finding needs and never paste personal data into tickets or pull requests.
2. **Schema first, data second.** Learn where a value lives (`search_context`, `find_columns`, `describe_table`) before reading rows.
3. **Small and keyed.** Filter by an id, a reference or a time window. Whole-table reads are truncated and slow on production. Check `truncated`.
4. **Prefer tools without SQL.** `lookup_rows` and `get_related_rows` build safe, parameterised queries. Use `run_select` for joins and aggregates; use `explain_select` before a query that might scan a big table.
5. **Facts, then explanation.** State what you saw (table, key, columns, values), then what you infer. Say what you could not check.

## The loop

```
symptom  ->  identifiers (log / ticket / PR)  ->  where they live  ->  the record  ->  its neighbours  ->  the logic that wrote it  ->  hypothesis  ->  confirm
```

| Step | Tool | Typical question |
|---|---|---|
| 0. Orient | `sql__check_connection`, `sql__list_databases` | Am I connected, to which environment, which databases are allowed? |
| 1. Get identifiers | `logs__*`, `jira__*`, `pr__*` | Which order id, user id, correlation id, time window, error text? |
| 2. Locate | `sql__search_context`, `sql__find_columns` | Which tables hold `OrderId`? Which database? (`search_context` is instant and offline; `find_columns` is live.) |
| 3. Understand | `sql__describe_table` | Columns, keys, foreign keys both ways, indexes, triggers. |
| 4. Read the record | `sql__lookup_rows` | The row with that id; the latest rows around the incident time. |
| 5. See the neighbours | `sql__get_related_rows` | Parent and child rows connected by foreign keys. |
| 6. Read the logic | `sql__get_object_definition`, `source__search` | Which procedure/trigger/view sets this column? Which code calls it? |
| 7. Aggregate / compare | `sql__run_select` | Counts, duplicates, orphans, differences between two environments. |
| 8. Performance | `sql__explain_select` | Why is this query slow? Scans, missing indexes, estimated cost. |
| 9. Is the snapshot stale? | `sql__check_schema_drift` | Can I still trust `search_context` / `get_object_context`? |

## Examples

**A. "Order 123 shows the wrong total."**
1. `logs__*`: find the request that touched order 123; note the timestamp and user.
2. `sql__find_columns {name_like: "OrderId"}` -> `Crm.dbo.Orders`, `Crm.dbo.OrderLines`.
3. `sql__lookup_rows {database: "Crm", table: "dbo.Orders", filters: [{column: "OrderId", op: "=", value: 123}]}` -> the order.
4. `sql__get_related_rows {database: "Crm", table: "dbo.Orders", key_value: 123}` -> lines, payments, discounts.
5. If `Total` disagrees with the sum of lines: `sql__run_select` with the `SUM`, then `sql__describe_table` for triggers/computed columns and `sql__get_object_definition` for the procedure that recalculates it. Compare with `source__search` for the caller.

**B. "A nightly job created duplicates."**
1. `sql__describe_table` to find the natural key and unique indexes (is there one?).
2. `sql__run_select` `SELECT BusinessKey, COUNT(*) FROM ... GROUP BY BusinessKey HAVING COUNT(*) > 1` (with a date filter).
3. `sql__lookup_rows` ordered by `CreatedDate` for two duplicates; compare columns that differ.
4. `sql__get_object_definition` of the job's procedure; look for a missing `NOT EXISTS`/`MERGE` guard.

**C. "The list page times out."**
1. Get the query from the source or logs.
2. `sql__explain_select`: look at `warnings` (scans), `missing_indexes`, the most expensive operator.
3. `sql__describe_table` (`indexes`) and a `COUNT` with the same filter (`sql__run_select`) to see selectivity.

## Reading results

- `truncated: true` -> add filters or a smaller `limit`; never conclude "only N rows exist".
- Values `***` are masked by configuration (`masked_column_patterns`). Do not try to bypass the mask.
- `approx_rows` is an estimate from metadata, not a count.
- `READ UNCOMMITTED` can show data that is being written right now. For a disputed value, read it again after a moment, or ask the owner to switch `isolation` to `read_committed`.
- `error_message` containing `not configured` means the account is missing; `is not allowed` means the database is outside `allowed_databases`.
- Dates come back as `yyyy-MM-dd HH:mm:ss.fff` in the server's time zone.

## Do not

- Do not try to make the MCP write, create temporary tables, or call procedures. It will be rejected, and the attempt is a sign the question belongs to a human with a write path.
- Do not loop on the same query. Results of data tools are not cached by the hub, so each call reaches the server.
- Do not put identifiers or values from production into prompts for external services unless the user agreed.
