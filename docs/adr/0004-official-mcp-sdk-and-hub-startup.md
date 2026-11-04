# 0004. Official MCP SDK, hub start-up contract, scan leaves the MCP

Status: Accepted

## Context
The first version implemented JSON-RPC by hand: errors thrown while handling a message were logged but never answered (the client hung), the protocol version was hardcoded, cancellation was ignored, and requests were handled one at a time. It also exited with code 1 when credentials were missing, which the hub contract forbids, and it exposed a `scan_server_context` tool that writes and deletes files in a caller-supplied directory.

## Decision
- Use the official `ModelContextProtocol` C# SDK with the generic host and stdio transport. Tools are `[McpServerTool]` methods with annotations.
- The server starts and lists its tools without credentials; database tools answer `not configured`. Configuration is layered (env > hub section > local file > default) and reports its source.
- `serve` skips the in-process self-tests and the working-directory write probe so the hub can start it quickly; they run for `--preflight` and the interactive modes.
- `scan_server_context` is removed from the MCP. Scanning stays a command-line command (`scan`) that writes the snapshot read by `search_context`, `get_object_context` and `check_schema_drift`.
- `execute_query` becomes `run_select`; `test_connection` becomes `check_connection` (hub naming and smoke shape).

## Consequences
- Protocol behaviour (errors, cancellation, version negotiation, annotations) is the SDK's responsibility and is covered by end-to-end tests that start the real process.
- A fully malformed line without an id is dropped by the SDK (there is nobody to answer); the server keeps running.
- Clients that used `execute_query`, `test_connection` or `scan_server_context` must be updated; the hub contract and allowlist ship with this change.
