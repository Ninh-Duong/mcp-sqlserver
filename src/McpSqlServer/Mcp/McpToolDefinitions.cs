using System.Text.Json.Nodes;

namespace McpSqlServer;

public static class McpToolDefinitions
{
    public static JsonArray GetToolDefinitions()
    {
        return new JsonArray
        {
            new JsonObject
            {
                ["name"] = "test_connection",
                ["description"] = "Verify connectivity to SQL Server instance with current configuration.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject()
                }
            },
            new JsonObject
            {
                ["name"] = "list_databases",
                ["description"] = "Count and enumerate databases visible to current login on SQL Server.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject()
                }
            },
            new JsonObject
            {
                ["name"] = "list_tables",
                ["description"] = "Count and list tables in a specific database.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["database"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Target database name to inspect tables."
                        }
                    },
                    ["required"] = new JsonArray { "database" }
                }
            },
            new JsonObject
            {
                ["name"] = "scan_server_context",
                ["description"] = "Scan all databases on SQL Server (Tables, Columns, Data Types, PK, FK, Views, Stored Procedures, and SQL logic) and export token-optimized AI Context documentation.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["server_alias"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Server or environment alias (e.g. DEV, UAT, PROD). Defaults to current configuration."
                        },
                        ["database"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Optional single database to scan (targeted fast scan in ~2s). If omitted, scans all accessible databases."
                        },
                        ["output_directory"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Directory to export AI Context documentation (default: ./ai-context)."
                        },
                        ["include_system"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "Whether to scan system databases (master, msdb, etc.) (default: false)."
                        }
                    }
                }
            },
            new JsonObject
            {
                ["name"] = "check_schema_drift",
                ["description"] = "Check whether databases on SQL Server have newer EF Core migrations or DDL modifications compared to the cached ai-context snapshot.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["base_directory"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Base directory of ai-context (default: ./ai-context)."
                        }
                    }
                }
            },
            new JsonObject
            {
                ["name"] = "execute_query",
                ["description"] = "Execute a safe, read-only SQL query (SELECT) on the database and return results in structured JSON. Automatically blocks modifying commands (INSERT, UPDATE, DELETE, DROP, etc.).",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "SQL SELECT or WITH ... SELECT statement to execute."
                        },
                        ["database"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Target database name to execute query against (defaults to configured initial database)."
                        },
                        ["max_rows"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Maximum number of rows to return (default: 100, max: 1000)."
                        }
                    },
                    ["required"] = new JsonArray { "query" }
                }
            },
            new JsonObject
            {
                ["name"] = "search_context",
                ["description"] = "Instant, token-optimized local search across all databases for tables, columns, procedures, views, functions, and triggers without reading large files.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Keyword or name of table, column, procedure, view, function, or trigger to locate."
                        },
                        ["server_alias"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Optional server alias to restrict search (e.g. 'DEV', 'PROD')."
                        },
                        ["database"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Optional target database name to restrict search."
                        },
                        ["target"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Optional search filter: 'all' (default), 'table', 'column', 'routine', 'procedure', 'view', 'function', 'trigger'."
                        },
                        ["limit"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Maximum number of matches to return (default: 10, max: 50)."
                        },
                        ["include_details"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "Whether to include full details (columns, parameters) in results. Defaults to false for minimal token usage."
                        }
                    },
                    ["required"] = new JsonArray { "query" }
                }
            },
            new JsonObject
            {
                ["name"] = "get_object_context",
                ["description"] = "Retrieve the exact compact definition or SQL code for a specific table, view, procedure, function, or trigger without loading massive files.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["database"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Target database name containing the object."
                        },
                        ["name"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Target object name (e.g. 'Orders' or 'dbo.sp_GetCustomerSummary')."
                        },
                        ["server_alias"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Optional server alias (defaults to configured server)."
                        },
                        ["type"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Optional object type ('table', 'view', 'procedure', 'function', 'trigger', 'dependency')."
                        }
                    },
                    ["required"] = new JsonArray { "database", "name" }
                }
            }
        };
    }
}
