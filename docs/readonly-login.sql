-- Read-only login for mcp-sqlserver (recommended, not required).
--
-- Run it as an administrator. Replace the placeholders. Run PART 2 once in EACH database that
-- the MCP may read (the databases listed in `allowed_databases`).
-- Never put the real password in git: set it through the hub (scripts/private_config.py set ... mssql.password).
--
-- With this login the server itself refuses writes, in addition to the code-level protection
-- described in docs/SECURITY.md. It works on AWS RDS (no sysadmin needed).

-- ===== PART 1: once per server =====
USE master;
GO
CREATE LOGIN [mcp_readonly] WITH PASSWORD = N'<CHOOSE_A_LONG_RANDOM_PASSWORD>', CHECK_POLICY = ON, DEFAULT_DATABASE = [master];
GO
-- Needed for list_databases / HAS_DBACCESS (optional) and explain_select needs SHOWPLAN per database (part 2).
-- GRANT VIEW ANY DATABASE TO [mcp_readonly];   -- default for most logins already
GO

-- ===== PART 2: once per database (repeat with USE <db>) =====
USE [<YourDatabase>];
GO
CREATE USER [mcp_readonly] FOR LOGIN [mcp_readonly];
GO
ALTER ROLE [db_datareader] ADD MEMBER [mcp_readonly];
GO
-- Belt and braces: even if the user later lands in a writer role, these DENYs win.
DENY INSERT, UPDATE, DELETE, ALTER, CONTROL, EXECUTE, REFERENCES, TAKE OWNERSHIP TO [mcp_readonly];
DENY CREATE TABLE, CREATE VIEW, CREATE PROCEDURE, CREATE FUNCTION, CREATE SCHEMA, CREATE TYPE TO [mcp_readonly];
GO
-- Needed by get_object_definition (read procedure/view/trigger source) and explain_select (estimated plans).
GRANT VIEW DEFINITION TO [mcp_readonly];
GRANT SHOWPLAN TO [mcp_readonly];
GO

-- ===== Verify (run as the new login) =====
-- EXECUTE AS LOGIN = 'mcp_readonly';
-- SELECT IS_SRVROLEMEMBER('sysadmin') AS is_sysadmin, IS_MEMBER('db_owner') AS is_db_owner,
--        HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'INSERT') AS can_insert;   -- expect 0, 0, 0
-- REVERT;
-- Then mcp-sqlserver's check_connection must report "write_capable": false.
