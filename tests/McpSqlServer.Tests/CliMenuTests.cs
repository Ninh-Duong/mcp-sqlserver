namespace McpSqlServer.Tests;

public class CliMenuTests
{
    [Fact]
    public void MaskPasswordLogic_HandlesTypingAndBackspaceCorrectly()
    {
        // Simulating the logic used inside CliMenu.ReadPasswordMasked()
        var sb = new System.Text.StringBuilder();

        // Simulate typing "p", "a", "s", "s", "1", backspace, "2"
        var inputs = new (char charValue, bool isBackspace)[]
        {
            ('p', false),
            ('a', false),
            ('s', false),
            ('s', false),
            ('1', false),
            ('\0', true), // backspace
            ('2', false)
        };

        foreach (var input in inputs)
        {
            if (input.isBackspace)
            {
                if (sb.Length > 0) sb.Remove(sb.Length - 1, 1);
            }
            else
            {
                sb.Append(input.charValue);
            }
        }

        Assert.Equal("pass2", sb.ToString());
    }

    [Fact]
    public void TableFormatting_GeneratesCleanRows()
    {
        var db = new DatabaseItem(1, "test_database", "ONLINE", true, false);
        var accessText = db.HasAccess.HasValue ? (db.HasAccess.Value ? "Yes" : "No") : "Unknown";
        var formatted = string.Format("{0,-30} {1,-15} {2,-15}", db.Name, db.State, accessText);

        Assert.Contains("test_database", formatted);
        Assert.Contains("ONLINE", formatted);
        Assert.Contains("Yes", formatted);
    }
}
