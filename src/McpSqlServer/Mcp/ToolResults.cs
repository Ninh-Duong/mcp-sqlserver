using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;

namespace McpSqlServer.Mcp;

/// <summary>Hub output standard: one text block of compact snake_case JSON, null fields omitted; failures set the MCP error flag.</summary>
public static class ToolResults
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static CallToolResult From<T>(T value, bool success) where T : notnull => new()
    {
        IsError = !success,
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, value.GetType(), Json) }],
    };

    public static CallToolResult Error(string message) => From(new { success = false, error_message = message }, false);

    public static CallToolResult Text(string text, bool isError = false) => new()
    {
        IsError = isError,
        Content = [new TextContentBlock { Text = text }],
    };
}
