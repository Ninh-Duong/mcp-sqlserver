# 0002. SELECT-only enforced by layered defence, not by the login

Status: Accepted

## Context
The user cannot always create a read-only SQL login; the configured login may be an administrator. Relying on permissions would make the "never write" guarantee depend on something outside the code.

## Decision
Enforce it in the code with independent layers (see [SECURITY.md](../SECURITY.md)): AST allowlist, always-rolled-back transaction, session guards, `ApplicationIntent=ReadOnly`, database scope, output limits. `check_connection` reports `write_capable` and warns, but does not refuse to run. A read-only login script is provided as a recommendation. All SQL goes through one class, `ReadOnlyQueryExecutor`; this is enforced by architecture tests.

## Consequences
- A bypass of the validator is still undone by the rollback; effects outside a transaction (extended stored procedures called from a user function) remain a documented residual risk.
- Every new feature must reuse the gateway; a second place that opens a connection fails the build.
- Slight cost: each call is a short transaction.
