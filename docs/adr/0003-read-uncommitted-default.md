# 0003. `READ UNCOMMITTED` is the default isolation

Status: Accepted

## Context
The MCP is used to investigate issues, often on a production database that is being written. A read must never block the application. The alternative, `READ COMMITTED`, can wait on locks held by writers.

## Decision
Default `isolation = read_uncommitted`, with `LOCK_TIMEOUT` 3 s as a second guard. `read_committed` is available through configuration.

## Consequences
- The investigation never blocks writers.
- A read can return a row that is being written and later rolled back (dirty read). The playbook tells the agent to re-read a disputed value and the owner to switch to `read_committed` when exactness matters.
