using MlCpu.SemanticKb.Text;

namespace MlCpu.SemanticKb.Index;

/// <summary>Okapi BM25 (k1 1.2, b 0.75) over identifier-aware terms (<see cref="Terms.IndexTerms"/>) of title + text.
/// It finds exact names (WS-DF-RATE, FNEM0300, R-CBTRN02C-004) that dense vectors blur.</summary>
public sealed class Bm25
{
    const double K1 = 1.2, B = 0.75;
    readonly Dictionary<string, List<(int Doc, int Tf)>> _postings = new(StringComparer.Ordinal);
    readonly int[] _length;
    readonly double _avg;

    public Bm25(IReadOnlyList<string> docs)
    {
        _length = new int[docs.Count];
        for (int d = 0; d < docs.Count; d++)
        {
            var terms = Terms.IndexTerms(docs[d]);
            _length[d] = terms.Count;
            foreach (var g in terms.GroupBy(t => t))
            {
                if (!_postings.TryGetValue(g.Key, out var list)) _postings[g.Key] = list = [];
                list.Add((d, g.Count()));
            }
        }
        _avg = docs.Count == 0 ? 1 : Math.Max(1, _length.Average());
    }

    /// <summary>Scores of the documents matching any query term (query terms weighted, expansion terms lower).</summary>
    public Dictionary<int, double> Score(IEnumerable<(string Term, double Weight)> query)
    {
        var scores = new Dictionary<int, double>();
        int n = _length.Length;
        foreach (var (term, weight) in query.GroupBy(q => q.Term).Select(g => (g.Key, g.Max(x => x.Weight))))
        {
            if (!_postings.TryGetValue(term, out var list)) continue;
            double idf = Math.Log(1 + (n - list.Count + 0.5) / (list.Count + 0.5));
            foreach (var (doc, tf) in list)
            {
                double s = idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * _length[doc] / _avg));
                scores[doc] = scores.GetValueOrDefault(doc) + weight * s;
            }
        }
        return scores;
    }
}
