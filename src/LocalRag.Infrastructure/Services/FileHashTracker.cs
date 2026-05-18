using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LocalRag.Infrastructure.Services;

/// <summary>
/// Tracks SHA-256 hashes of every PDF that has been successfully indexed.
/// Persisted to a JSON sidecar file next to the FAISS index so it survives restarts.
///
/// Usage pattern (in the background worker):
///   if (!_hashTracker.IsAlreadyIndexed(filePath)) { ...index... }
///   _hashTracker.MarkIndexed(filePath);
///   await _hashTracker.SaveAsync();
/// </summary>
public sealed class FileHashTracker
{
    private readonly string _persistPath;
    private readonly ILogger<FileHashTracker> _logger;
    private Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);

    public FileHashTracker(string persistPath, ILogger<FileHashTracker> logger)
    {
        _persistPath = persistPath;
        _logger = logger;
    }

    /// <summary>Load previously persisted hashes from disk (call once on startup).</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_persistPath)) return;

        try
        {
            await using var fs = File.OpenRead(_persistPath);
            _hashes = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(fs, cancellationToken: ct)
                      ?? new(StringComparer.OrdinalIgnoreCase);
            _logger.LogInformation("Loaded {Count} file hashes from {Path}", _hashes.Count, _persistPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load hash tracker from {Path} — starting fresh", _persistPath);
            _hashes = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Persist current hashes to disk.</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_persistPath)!);
        await using var fs = File.Create(_persistPath);
        await JsonSerializer.SerializeAsync(fs, _hashes, cancellationToken: ct);
    }

    /// <summary>
    /// Returns true if the file's current SHA-256 hash matches the stored hash,
    /// meaning the file has not changed since it was last indexed.
    /// </summary>
    public bool IsAlreadyIndexed(string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        if (!_hashes.TryGetValue(fileName, out string? stored)) return false;
        return stored == ComputeHash(filePath);
    }

    /// <summary>Record the file as indexed using its current content hash.</summary>
    public void MarkIndexed(string filePath)
    {
        _hashes[Path.GetFileName(filePath)] = ComputeHash(filePath);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string ComputeHash(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        byte[] hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }
}
