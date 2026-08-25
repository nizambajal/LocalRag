using Microsoft.Extensions.Logging;

namespace LocalRag.Mcp.Audit;

/// <summary>
/// Minimal audit trail for MCP tool calls (master prompt §16: "Log important
/// agent actions" / "Make tool calls observable"). Deliberately logs only
/// truncated summaries — never full CV text or full tool output — per
/// guardrail #5 ("Never expose private CV data unnecessarily") and
/// "Do not log secrets ... or sensitive credentials."
///
/// This is a lightweight, per-call helper rather than a global MCP filter
/// pipeline: the SDK's filter API surface for tool calls specifically
/// wasn't something we could verify against real docs/behavior in the time
/// available, and guessing at an unverified API would violate §22's "do not
/// invent APIs" rule. This achieves the same audit requirement with
/// standard, unambiguous .NET (ILogger), at the cost of one call per tool
/// method rather than one central pipeline. Revisit if the SDK's tool-call
/// filter API is confirmed later.
/// </summary>
internal static class ToolAudit
{
    private const int MaxSummaryLength = 120;

    public static void LogCall(ILogger logger, string toolName, string inputSummary)
    {
        logger.LogInformation(
            "[audit] tool={Tool} input={Input}",
            toolName, Truncate(inputSummary));
    }

    public static void LogResult(ILogger logger, string toolName, int outputLength)
    {
        logger.LogInformation(
            "[audit] tool={Tool} status=ok outputLength={Length}",
            toolName, outputLength);
    }

    public static void LogError(ILogger logger, string toolName, Exception ex)
    {
        logger.LogWarning(
            "[audit] tool={Tool} status=error error={Error}",
            toolName, ex.Message);
    }

    private static string Truncate(string s) =>
        string.IsNullOrEmpty(s) ? "(empty)" :
        s.Length <= MaxSummaryLength ? s :
        s[..MaxSummaryLength] + "…";
}