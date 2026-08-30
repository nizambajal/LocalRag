namespace LocalRag.Mcp.Validation;

/// <summary>
/// Basic tool-input validation. Deliberately simple bounds checks — this is a personal
/// local tool with one caller (the agent), not a public API needing
/// exhaustive sanitization.
/// </summary>
internal static class ToolInput
{
    private const int MaxTextLength = 50_000; // generous upper bound for a pasted job description

    public static string RequireNonEmpty(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"'{paramName}' must not be empty.", paramName);

        if (value.Length > MaxTextLength)
            throw new ArgumentException(
                $"'{paramName}' is too long ({value.Length} chars, max {MaxTextLength}).", paramName);

        return value;
    }
}