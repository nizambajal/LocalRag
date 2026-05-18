using LocalRag.Application.Contracts;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace LocalRag.Infrastructure.Services;

/// <summary>
/// Extracts text from PDF files using PdfPig.
/// Uses ContentOrderTextExtractor which preserves natural reading order
/// across multi-column layouts and handles headers/footers gracefully.
/// </summary>
public sealed class PdfPigReader : IPdfReader
{
    private readonly ILogger<PdfPigReader> _logger;

    public PdfPigReader(ILogger<PdfPigReader> logger) => _logger = logger;

    /// <inheritdoc />
    public IEnumerable<(int Page, string Text)> ExtractPages(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"PDF not found: {filePath}");

        _logger.LogDebug("Opening PDF: {File}", Path.GetFileName(filePath));

        using var doc = PdfDocument.Open(filePath);

        foreach (Page page in doc.GetPages())
        {
            string text;
            try
            {
                // ContentOrderTextExtractor respects reading order better than
                // simple word enumeration for multi-column / complex layouts.
                text = ContentOrderTextExtractor.GetText(page, true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to extract page {Page} from {File} — skipping",
                    page.Number, Path.GetFileName(filePath));
                continue;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogDebug("Page {Page} in {File} is empty — skipping",
                    page.Number, Path.GetFileName(filePath));
                continue;
            }

            yield return (page.Number, NormalizeWhitespace(text));
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Collapse runs of whitespace/newlines that PdfPig sometimes emits
    /// while preserving paragraph breaks (double newline).
    /// </summary>
    private static string NormalizeWhitespace(string raw)
    {
        // Preserve paragraph breaks then collapse everything else
        var withParaBreaks = System.Text.RegularExpressions.Regex
            .Replace(raw, @"(\r?\n){2,}", "\n\n");

        var collapsed = System.Text.RegularExpressions.Regex
            .Replace(withParaBreaks, @"[ \t]+", " ");

        return collapsed.Trim();
    }
}
