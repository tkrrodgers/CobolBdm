using MlCpu.SemanticKb.Text;

namespace MlCpu.SemanticKb.Labels;

public sealed class GlossaryTerm
{
    public string Id { get; set; } = "";
    public string Term { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    public List<string> CodeTerms { get; set; } = [];
    public string Definition { get; set; } = "";
    public List<string> Domains { get; set; } = [];
    public List<string> Functions { get; set; } = [];
    public double Weight { get; set; } = 1;
    public string? Source { get; set; }
}

public sealed class GlossaryFile
{
    public string Schema { get; set; } = "";
    public bool Reviewed { get; set; }
    public string Note { get; set; } = "";
    public List<GlossaryTerm> Terms { get; set; } = [];
}

/// <summary>A glossary term found in a text: how often and by which phrase.</summary>
public sealed record TermHit(GlossaryTerm Term, int Count);

/// <summary>
/// The financial glossary (knowledge/glossary/financial-terms.json). Aliases and code terms become sub-word phrases
/// (<see cref="Terms.Words"/>), so "ANNUAL-RATE" matches LT-ANNUAL-RATE, RQ-ANNUAL-RATE and annualRate alike.
/// </summary>
public sealed class Glossary
{
    readonly Dictionary<string, List<(string[] Phrase, GlossaryTerm Term)>> _byFirstWord = new(StringComparer.Ordinal);

    public GlossaryFile File { get; }
    public IReadOnlyList<GlossaryTerm> Terms => File.Terms;

    public Glossary(GlossaryFile file)
    {
        File = file;
        foreach (var t in file.Terms)
            foreach (var phrase in t.Aliases.Concat(t.CodeTerms).Select(Words).Where(p => p.Count > 0).Select(p => p.ToArray()).DistinctBy(p => string.Join(' ', p)))
            {
                if (!_byFirstWord.TryGetValue(phrase[0], out var list)) _byFirstWord[phrase[0]] = list = [];
                list.Add((phrase, t));
            }
        // Longest phrase first, so "interest rate" wins over "interest" at the same position.
        foreach (var list in _byFirstWord.Values) list.Sort((a, b) => b.Phrase.Length.CompareTo(a.Phrase.Length));
    }

    public static Glossary Load(string path) => new(KbJson.Read<GlossaryFile>(path));

    /// <summary>Terms found in the text, each counted once per position (longest phrase wins).</summary>
    public IReadOnlyList<TermHit> Find(string text)
    {
        var words = Words(text);
        var counts = new Dictionary<GlossaryTerm, int>();
        for (int i = 0; i < words.Count; i++)
        {
            if (!_byFirstWord.TryGetValue(words[i], out var candidates)) continue;
            foreach (var (phrase, term) in candidates)
            {
                if (i + phrase.Length > words.Count) continue;
                bool match = true;
                for (int k = 1; k < phrase.Length && match; k++) match = words[i + k] == phrase[k];
                if (!match) continue;
                counts[term] = counts.GetValueOrDefault(term) + 1;
                i += phrase.Length - 1;
                break;
            }
        }
        return counts.Select(kv => new TermHit(kv.Key, kv.Value)).OrderByDescending(h => h.Count * h.Term.Weight).ToList();
    }

    /// <summary>Extra lexical query words for the terms found in a query: their aliases and code terms.</summary>
    public IReadOnlyList<string> Expand(string query)
    {
        var extra = new List<string>();
        foreach (var hit in Find(query))
            foreach (var p in hit.Term.Aliases.Concat(hit.Term.CodeTerms))
                extra.AddRange(Text.Terms.IndexTerms(p));
        return extra.Distinct().ToList();
    }

    public GlossaryTerm? Get(string id) => File.Terms.FirstOrDefault(t => t.Id == id);

    /// <summary>Sub-words with a light plural stem, applied to phrases and texts alike ("Credit Cards" matches "credit
    /// card", "currencies" matches "currency"); BM25 keeps the exact words.</summary>
    static List<string> Words(string text) => Text.Terms.Words(text).Select(Stem).ToList();

    static string Stem(string w) =>
        w.Length > 4 && w.EndsWith("ies") ? w[..^3] + "y" :
        w.Length > 3 && w[^1] == 's' && w[^2] is not ('s' or 'u' or 'i') ? w[..^1] : w;
}
