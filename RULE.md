# Repository Governance & Engineering Rules

This document specifies mandatory engineering and governance rules for developing, maintaining, and contributing to the `mcp-sqlserver` repository.

---

## 1. Security & Zero-Leak Credentials
1. **Never commit secrets:** Never commit real usernames, passwords, tokens, connection strings, or sensitive configuration files to Git.
2. **Data Sanitization:** All error logs, exception messages, and traces written to stderr or log files must automatically redact or replace passwords with `***`.
3. **CLI Password Masking:** Password input in terminal must mask characters (`*`), be stored only in session memory, and never be persisted in plain text files.

---

## 2. Relative Path Rule (100% Relative Paths)
1. **No hardcoded absolute paths:** Absolutely do not use hardcoded absolute drive paths like `C:\...`, `D:\...`, or `/home/...` in source code, tests, scripts, log file paths, or configurations.
2. **Standard path resolution:** All local file/directory paths (logs, publish, test assets) must be computed relative to the working directory or `AppContext.BaseDirectory`.

---

## 3. Run Gate & Unit Tests
1. **100% Feature Test Coverage:** All process features (Pre-flight checks, ConnectionOptions builder/validation, SQL parser/validator, Logger sanitizer, CLI masking, MCP tool contracts) must have corresponding unit tests.
2. **Mandatory Passing Tests on Every Run:**
   - When executing via scripts (`./scripts/run.ps1`), all unit tests run first. If any test fails, execution stops immediately.
   - When the application starts (including standalone binary), `PreflightChecker.RunCoreSelfTests()` executes an in-process self-test suite. If it fails, the application refuses to run.

---

## 4. Strict STDIO Separation for MCP
1. **`stdout` stream rule:** In MCP server mode (`mcp-sqlserver serve`), the `stdout` stream is strictly reserved for the JSON-RPC protocol. Never call `Console.WriteLine` or output log messages to `stdout`.
2. **`stderr` stream rule:** All diagnostic logs, connection progress, and error messages must be routed to `Console.Error` (`stderr`) or written to the relative log file `./logs/process.log`.

---

## 5. Ponytail Principles (Minimalism & High Performance)
1. **No unrequested abstractions:** No interfaces for classes with a single implementation; no Factory patterns when direct instantiation suffices; no heavy ORM when direct catalog queries solve the problem cleanly.
2. **Async & Cancellation:** Short, single-purpose methods, fully asynchronous with `CancellationToken` support across all I/O and SQL queries.
3. **Deletion over addition:** Keep the codebase clean and lean, eliminating speculative code and boilerplate.
