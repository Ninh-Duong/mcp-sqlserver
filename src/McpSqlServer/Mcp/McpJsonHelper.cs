using System.Text.Json;

namespace McpSqlServer;

public static class McpJsonHelper
{
    public static string? GetString(this JsonElement element, string propertyName, string? defaultValue = null)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
        }
        return defaultValue;
    }

    public static int GetInt32(this JsonElement element, string propertyName, int defaultValue = 0)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val))
                return val;
        }
        return defaultValue;
    }

    public static bool GetBool(this JsonElement element, string propertyName, bool defaultValue = false)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
        }
        return defaultValue;
    }
}
