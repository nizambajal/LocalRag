using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LocalRag.Infrastructure.Services;

public sealed class BertTokenizer
{
    private const long ClsTokenId = 101;
    private const long SepTokenId = 102;
    private const long UnkTokenId = 100;
    private const long PadTokenId = 0;

    private readonly Dictionary<string, long> _vocab;
    private readonly ILogger<BertTokenizer> _logger;
    private readonly int _maxLength;

    private BertTokenizer(Dictionary<string, long> vocab, ILogger<BertTokenizer> logger, int maxLength)
    {
        _vocab = vocab;
        _logger = logger;
        _maxLength = maxLength;
    }

    public static async Task<BertTokenizer> LoadAsync(
        string tokenizerJsonPath,
        ILogger<BertTokenizer> logger,
        int maxLength = 512,
        CancellationToken ct = default)
    {
        if (!File.Exists(tokenizerJsonPath))
            throw new FileNotFoundException(
                $"tokenizer.json not found: {tokenizerJsonPath}");

        logger.LogInformation("Loading tokenizer from {Path}", tokenizerJsonPath);

        await using var fs = File.OpenRead(tokenizerJsonPath);
        using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct);
        var vocab = new Dictionary<string, long>(StringComparer.Ordinal);

        if (doc.RootElement.TryGetProperty("model", out var model) &&
            model.TryGetProperty("vocab", out var vocabEl))
        {
            foreach (var entry in vocabEl.EnumerateObject())
                vocab[entry.Name] = entry.Value.GetInt64();
        }

        if (vocab.Count == 0)
            throw new InvalidOperationException("Tokenizer vocab is empty.");

        logger.LogInformation("Tokenizer loaded: {Count} tokens", vocab.Count);
        return new BertTokenizer(vocab, logger, maxLength);
    }

    public TokenizerOutput Encode(string text, int? sequenceLength = null)
    {
        int seqLen = sequenceLength ?? _maxLength;
        text = text.Trim().ToLowerInvariant();

        var tokenIds = WordPieceTokenize(text);
        int maxContent = seqLen - 2;
        if (tokenIds.Count > maxContent) tokenIds = tokenIds[..maxContent];

        var ids = new List<long>(seqLen) { ClsTokenId };
        ids.AddRange(tokenIds);
        ids.Add(SepTokenId);

        int actualLen = ids.Count;
        while (ids.Count < seqLen) ids.Add(PadTokenId);

        var mask = new long[seqLen];
        var typeIds = new long[seqLen];
        for (int i = 0; i < actualLen; i++) mask[i] = 1;

        return new TokenizerOutput(ids.ToArray(), mask, typeIds, actualLen);
    }

    public IReadOnlyList<TokenizerOutput> EncodeBatch(IReadOnlyList<string> texts)
    {
        var encoded = texts.Select(t => Encode(t, _maxLength)).ToList();
        int maxActual = Math.Min(encoded.Max(e => e.ActualLength), _maxLength);
        return texts.Select(t => Encode(t, maxActual)).ToList();
    }

    private List<long> WordPieceTokenize(string text)
    {
        var result = new List<long>();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = Tokenize(word);
            result.AddRange(pieces.Select(p =>
                _vocab.TryGetValue(p, out long id) ? id : UnkTokenId));
        }
        return result;
    }

    private List<string> Tokenize(string word)
    {
        if (_vocab.ContainsKey(word)) return [word];

        var subTokens = new List<string>();
        int start = 0;
        bool isBad = false;

        while (start < word.Length)
        {
            int end = word.Length;
            string? curSubStr = null;

            while (start < end)
            {
                string substr = (start == 0 ? "" : "##") + word[start..end];
                if (_vocab.ContainsKey(substr)) { curSubStr = substr; break; }
                end--;
            }

            if (curSubStr is null) { isBad = true; break; }
            subTokens.Add(curSubStr);
            start = end;
        }

        return isBad ? ["[UNK]"] : subTokens;
    }
}

public sealed record TokenizerOutput(
    long[] InputIds,
    long[] AttentionMask,
    long[] TokenTypeIds,
    int ActualLength);