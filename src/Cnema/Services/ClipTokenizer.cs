using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cnema.Services;

/// <summary>
/// CLIP's byte-level BPE tokenizer (the same rules as OpenAI's simple_tokenizer.py), reading the
/// vocab.json and merges.txt that come with the model.
/// </summary>
public sealed partial class ClipTokenizer
{
    public const int Length = 77;
    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<(string, string), int> _ranks = new();
    private readonly string[] _byteChars = new string[256];
    private readonly Dictionary<string, string[]> _cache = new();
    private readonly int _start, _end;

    public ClipTokenizer(string vocabJson, string mergesTxt)
    {
        _vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(vocabJson))
                 ?? throw new InvalidDataException("vocab.json is empty");
        var rank = 0;
        foreach (var line in File.ReadLines(mergesTxt))
        {
            if (line.StartsWith("#version") || line.Length == 0) continue;
            var parts = line.Split(' ');
            if (parts.Length == 2) _ranks[(parts[0], parts[1])] = rank++;
        }
        // GPT-2's reversible byte → printable character table.
        var bs = new List<int>();
        for (var b = '!'; b <= '~'; b++) bs.Add(b);
        for (var b = '¡'; b <= '¬'; b++) bs.Add(b);
        for (var b = '®'; b <= 'ÿ'; b++) bs.Add(b);
        var n = 0;
        for (var b = 0; b < 256; b++)
            _byteChars[b] = bs.Contains(b) ? ((char)b).ToString() : ((char)(256 + n++)).ToString();
        _start = _vocab["<|startoftext|>"];
        _end = _vocab["<|endoftext|>"];
    }

    /// <summary>Start, the text's tokens (cut to fit), end, then zeros up to 77.</summary>
    public long[] Encode(string text)
    {
        var ids = new List<long> { _start };
        var clean = Spaces().Replace(text.Normalize(NormalizationForm.FormC), " ").Trim().ToLowerInvariant();
        foreach (Match m in Pattern().Matches(clean))
        {
            var word = string.Concat(Encoding.UTF8.GetBytes(m.Value).Select(b => _byteChars[b]));
            foreach (var piece in Bpe(word))
                if (_vocab.TryGetValue(piece, out var id)) ids.Add(id);
            if (ids.Count >= Length - 1) break;
        }
        if (ids.Count > Length - 1) ids.RemoveRange(Length - 1, ids.Count - (Length - 1));
        ids.Add(_end);
        var result = new long[Length];
        ids.CopyTo(0, result, 0, ids.Count);
        return result;
    }

    private string[] Bpe(string token)
    {
        if (_cache.TryGetValue(token, out var hit)) return hit;
        var word = token.Select(c => c.ToString()).ToList();
        word[^1] += "</w>";
        while (word.Count > 1)
        {
            var best = -1;
            var bestRank = int.MaxValue;
            for (var i = 0; i < word.Count - 1; i++)
                if (_ranks.TryGetValue((word[i], word[i + 1]), out var r) && r < bestRank) { bestRank = r; best = i; }
            if (best < 0) break;
            var (a, b) = (word[best], word[best + 1]);
            var merged = new List<string>(word.Count);
            for (var i = 0; i < word.Count; i++)
            {
                if (i < word.Count - 1 && word[i] == a && word[i + 1] == b) { merged.Add(a + b); i++; }
                else merged.Add(word[i]);
            }
            word = merged;
        }
        var result = word.ToArray();
        if (_cache.Count < 10_000) _cache[token] = result;
        return result;
    }

    [GeneratedRegex(@"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
