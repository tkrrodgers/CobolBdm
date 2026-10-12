using System.Numerics.Tensors;
using System.Text.RegularExpressions;
using MlCpu.SemanticKb.Index;
using MlCpu.SemanticKb.Labels;
using MlCpu.SemanticKb.Text;

namespace MlCpu.SemanticKb.Search;

public sealed record SearchRequest(string Query, IReadOnlyList<string>? Projects = null, int K = 10, string Mode = "hybrid",
    string? Kind = null, string? Program = null, IReadOnlyDictionary<string, string>? Labels = null, int MaxPerAnchor = 2);

public sealed record SearchHit(KbChunk Chunk, double Score, int? DenseRank, double? Dense, int? LexicalRank, double? Lexical,
    string AbsolutePath, IReadOnlyList<string> Siblings);

public sealed record SearchResponse(string Query, string Text, IReadOnlyDictionary<string, string> Filters, IReadOnlyList<string> Expansion,
    IReadOnlyList<SearchHit> Hits, double Milliseconds);

/// <summary>
/// Hybrid search over the loaded project indexes (knowledgeBaseGroundGemma4.md §7): EmbeddingGemma cosine over every
/// chunk, BM25 over identifier-aware terms with glossary expansion, reciprocal rank fusion (k = 60), filters (also typed
/// inline as program:FNEM0300 kind:rule function:interest-calculation), and at most N hits per paragraph anchor.
/// </summary>
public sealed partial class KbSearch
{
    const int Rrf = 60, Pool = 200;
    readonly List<(KbIndex Index, int Row)> _rows = [];
    readonly Bm25 _bm25;
    readonly Glossary _glossary;
    readonly Func<string, float[]>? _embed;
    readonly Dictionary<string, List<int>> _byAnchor = new(StringComparer.Ordinal);

    public IReadOnlyList<KbIndex> Indexes { get; }
    public IEnumerable<KbChunk> Chunks => _rows.Select(r => r.Index.Chunks[r.Row]);

    public KbSearch(IReadOnlyList<KbIndex> indexes, Glossary glossary, Func<string, float[]>? embedQuery)
    {
        Indexes = indexes;
        _glossary = glossary;
        _embed = embedQuery;
        foreach (var ix in indexes)
            for (int i = 0; i < ix.Chunks.Count; i++)
            {
                _rows.Add((ix, i));
                var a = ix.Chunks[i].Anchor;
                if (!_byAnchor.TryGetValue(a, out var l)) _byAnchor[a] = l = [];
                l.Add(_rows.Count - 1);
            }
        _bm25 = new Bm25(_rows.Select(r => r.Index.Chunks[r.Row]).Select(c => c.Title + "\n" + c.Text).ToList());
    }

    public string AbsolutePath(KbIndex ix, KbChunk c) => Path.GetFullPath(Path.Combine(ix.Dir, "..", c.Path));

    public IReadOnlyList<KbChunk> Anchor(string anchor) =>
        _byAnchor.TryGetValue(anchor, out var rows) ? rows.Select(r => _rows[r].Index.Chunks[_rows[r].Row]).ToList() : [];

    public SearchResponse Search(SearchRequest req)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (text, inline) = ParseInline(req.Query);
        var filters = new Dictionary<string, string>(inline, StringComparer.OrdinalIgnoreCase);
        if (req.Kind is { Length: > 0 }) filters["kind"] = req.Kind;
        if (req.Program is { Length: > 0 }) filters["program"] = req.Program;
        foreach (var (k, v) in req.Labels ?? new Dictionary<string, string>()) filters[k] = v;
        var projects = filters.TryGetValue("project", out var pf) ? [pf] : req.Projects?.Where(p => p.Length > 0).ToList();

        var candidates = new List<int>();
        for (int i = 0; i < _rows.Count; i++)
            if (Matches(_rows[i].Index.Chunks[_rows[i].Row], projects, filters)) candidates.Add(i);

        var dense = new Dictionary<int, (int Rank, double Score)>();
        if (req.Mode != "lexical" && _embed is not null && text.Length > 0)
        {
            var q = _embed(text);
            var scored = candidates.Select(i => (i, s: (double)TensorPrimitives.Dot(q, _rows[i].Index.Vector(_rows[i].Row)))).OrderByDescending(x => x.s).Take(Pool).ToList();
            for (int r = 0; r < scored.Count; r++) dense[scored[r].i] = (r + 1, scored[r].s);
        }
        var lexical = new Dictionary<int, (int Rank, double Score)>();
        var expansion = _glossary.Expand(text).Where(t => !Stopwords.Contains(t)).ToList();
        if (req.Mode != "dense" && text.Length > 0)
        {
            var terms = Terms.IndexTerms(text).Where(t => !Stopwords.Contains(t)).Select(t => (t, 1.0)).Concat(expansion.Select(t => (t, 0.4)));
            var all = _bm25.Score(terms);
            var allowed = candidates.ToHashSet();
            var scored = all.Where(kv => allowed.Contains(kv.Key)).OrderByDescending(kv => kv.Value).Take(Pool).ToList();
            for (int r = 0; r < scored.Count; r++) lexical[scored[r].Key] = (r + 1, scored[r].Value);
        }

        // Weighted reciprocal rank fusion. BM25 earns full weight only when the query names code (a program id, a COBOL
        // data name, a rule id, a camelCase identifier); for plain questions EmbeddingGemma leads (§12: dense alone beat
        // equal-weight fusion on the gold questions).
        double lexicalWeight = req.Mode == "lexical" || HasIdentifier(text) ? 1.0 : LexicalWeightForProse;
        var fused = dense.Keys.Union(lexical.Keys).Select(i =>
        {
            double s = 0;
            if (dense.TryGetValue(i, out var d)) s += 1.0 / (Rrf + d.Rank);
            if (lexical.TryGetValue(i, out var l)) s += lexicalWeight / (Rrf + l.Rank);
            return (i, s);
        }).OrderByDescending(x => x.s).ThenBy(x => x.i);
        if (text.Length == 0) fused = candidates.Select(i => (i, 0.0)).OrderBy(x => x.i);

        var hits = new List<SearchHit>();
        var perAnchor = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (i, s) in fused)
        {
            var (ix, row) = _rows[i];
            var c = ix.Chunks[row];
            var n = perAnchor.GetValueOrDefault(c.Anchor);
            if (n >= req.MaxPerAnchor) continue;
            perAnchor[c.Anchor] = n + 1;
            var siblings = _byAnchor[c.Anchor].Where(r => r != i).Take(8).Select(r => _rows[r].Index.Chunks[_rows[r].Row].Id).ToList();
            hits.Add(new SearchHit(c, Math.Round(s, 5),
                dense.TryGetValue(i, out var d) ? d.Rank : null, dense.TryGetValue(i, out d) ? Math.Round(d.Score, 4) : null,
                lexical.TryGetValue(i, out var l) ? l.Rank : null, lexical.TryGetValue(i, out l) ? Math.Round(l.Score, 3) : null,
                AbsolutePath(ix, c), siblings));
            if (hits.Count >= req.K) break;
        }
        return new SearchResponse(req.Query, text, filters, expansion, hits, Math.Round(sw.Elapsed.TotalMilliseconds, 1));
    }

    static bool Matches(KbChunk c, IReadOnlyList<string>? projects, IReadOnlyDictionary<string, string> f)
    {
        if (projects is { Count: > 0 } && !projects.Contains(c.Project, StringComparer.OrdinalIgnoreCase)) return false;
        foreach (var (k, v) in f)
        {
            var ok = k.ToLowerInvariant() switch
            {
                "project" => c.Project.Equals(v, StringComparison.OrdinalIgnoreCase),
                "program" => c.Program.Equals(v, StringComparison.OrdinalIgnoreCase),
                "kind" => c.Kind.Equals(v, StringComparison.OrdinalIgnoreCase),
                "role" => c.Label(Families.Role)?.Equals(v, StringComparison.OrdinalIgnoreCase) == true,
                "function" => c.Label(Families.Function)?.Equals(v, StringComparison.OrdinalIgnoreCase) == true,
                "rulekind" => c.Label(Families.RuleKind)?.Equals(v, StringComparison.OrdinalIgnoreCase) == true,
                _ => true
            };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>BM25's weight in the fusion for queries that name no code (1.0 when they do). Chosen on the gold
    /// questions (`kb eval --prose-lexical w` sweeps it).</summary>
    public static double LexicalWeightForProse { get; set; } = 0.1;

    /// <summary>A token that looks like code: letters and digits mixed (FNEM0300, CBTRN02C, R-SAM1-004), an upper-case
    /// hyphenated name (WS-DF-RATE), an underscore name (HALF_EVEN) or camelCase (rateFactor).</summary>
    public static bool HasIdentifier(string text)
    {
        foreach (var raw in text.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim('?', '.', ',', ';', ':', '(', ')', '"', '\'', '!');
            if (t.Length < 3) continue;
            int letters = t.Count(char.IsLetter);
            if (letters > 0 && t.Any(char.IsDigit) && t.Length >= 4) return true;
            if (t.Contains('-') && letters >= 3 && t.Where(char.IsLetter).All(char.IsUpper)) return true;
            if (t.Contains('_') && letters >= 3) return true;
            if (char.IsLower(t[0]) && t.Skip(1).Any(char.IsUpper)) return true;
        }
        return false;
    }

    static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "has", "have", "how", "if", "in", "into", "is", "it",
        "its", "of", "on", "or", "s", "so", "that", "the", "their", "then", "there", "this", "to", "up", "was", "what", "when", "where", "which",
        "who", "why", "will", "with", "would", "yet", "get", "gets", "done", "used", "use", "happens", "happen", "set", "sets", "given"
    };

    /// <summary>Splits "rounding program:FNMY0100 kind:rule" into the query text and its inline filters.</summary>
    public static (string Text, Dictionary<string, string> Filters) ParseInline(string query)
    {
        var filters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = InlineFilter().Replace(query, m =>
        {
            filters[m.Groups[1].Value] = m.Groups[2].Value;
            return " ";
        });
        return (Regex.Replace(text, @"\s+", " ").Trim(), filters);
    }

    [GeneratedRegex(@"(?<![\w-])(project|program|kind|role|function|ruleKind|rulekind):([\w.-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex InlineFilter();
}
