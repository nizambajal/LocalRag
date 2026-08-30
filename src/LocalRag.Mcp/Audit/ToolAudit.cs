using Microsoft.Extensions.Logging;

namespace LocalRag.Mcp.Audit;

/// <summary>
/// Set <see cref="VerboseEnabled"/> (via RagOptions.VerboseToolLogging) to
/// log FULL request/response content instead, for local debugging only.
/// Every log line still goes through the standard ILogger pipeline, which
/// Program.cs wires to both console and a rolling file
/// (logs/localrag-mcp-.log) — see Program.cs for the Serilog config.
/// </summary>
internal static class ToolAudit
{
    private const int MaxSummaryLength = 120;

    /// <summary>
    /// Global switch, set once at startup from RagOptions.VerboseToolLogging.
    /// Deliberately a static bool rather than DI-injected per-call state —
    /// this is a debug utility, not a service with meaningful per-request
    /// configuration, so the extra ceremony isn't worth it here.
    /// </summary>
    public static bool VerboseEnabled { get; set; } = false;

    public static void LogCall(ILogger logger, string toolName, string fullInput)
    {
        var shown = VerboseEnabled ? fullInput : Truncate(fullInput);
        logger.LogInformation(
            "[audit] tool={Tool} input={Input}",
            toolName, shown);
    }

    public static void LogResult(ILogger logger, string toolName, string fullOutput)
    {
        if (VerboseEnabled)
        {
            logger.LogInformation(
                "[audit] tool={Tool} status=ok outputLength={Length} output={Output}",
                toolName, fullOutput.Length, fullOutput);
        }
        else
        {
            logger.LogInformation(
                "[audit] tool={Tool} status=ok outputLength={Length}",
                toolName, fullOutput.Length);
        }
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