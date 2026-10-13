namespace MlCpu.SemanticKb.Labels;

/// <summary>
/// Weak <c>function</c> labels from the glossary. A chunk scores each taxonomy class by the glossary terms in its own text
/// (each term counts at most 3 times, times its weight). A label is given only when the best class scores at least 2 and
/// 1.5× the runner-up; everything else is left to the ML.NET classifier. A program summary is labeled by its prior: its
/// header Function line (weighted 2) with its purpose and Gemma design. Adding the prior to every chunk of the program
/// (<see cref="PriorTop"/>) was measured on the SME sample: 1.5 and 0.75 scored 26/40 and 0 scored 25/40 (no real
/// difference at n = 40), while 0 gave the most consistent labels (cross-validation macro 55.4% against 49.8%), so it is
/// off by default.
/// </summary>
public sealed class FunctionLabeler(Glossary glossary)
{
    public const double MinScore = 2, MinMargin = 1.5;
    /// <summary>What the program's main class adds to each chunk of the program (0 = off; see the class remarks).</summary>
    public static double PriorTop { get; set; } = 0;

    public Dictionary<string, double> Scores(string text)
    {
        var s = new Dictionary<string, double>();
        foreach (var hit in glossary.Find(text))
            foreach (var f in hit.Term.Functions)
                s[f] = s.GetValueOrDefault(f) + Math.Min(hit.Count, 3) * hit.Term.Weight;
        return s;
    }

    /// <summary>The program's prior, scaled so its main class is 1: the header Function line counts twice as much as the
    /// purpose and design text.</summary>
    public Dictionary<string, double> Prior(string function, string context)
    {
        var s = Scores(context);
        foreach (var (k, v) in Scores(function)) s[k] = s.GetValueOrDefault(k) + 2 * v;
        if (s.Count == 0) return s;
        var top = s.Values.Max();
        return s.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value / top, 3));
    }

    /// <summary>The prior's main class when it clearly leads (a program summary is labeled by its own header).</summary>
    public static KbLabel? Top(IReadOnlyDictionary<string, double> prior)
    {
        var ranked = prior.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        if (ranked.Count == 0) return null;
        var second = ranked.Count > 1 ? ranked[1].Value : 0;
        return ranked[0].Value >= MinMargin * second ? new KbLabel(ranked[0].Key, "glossary", Math.Round(ranked[0].Value / (ranked[0].Value + second), 3)) : null;
    }

    public KbLabel? Label(string text, IReadOnlyDictionary<string, double> prior)
    {
        var s = Scores(text);
        if (PriorTop > 0) foreach (var (k, v) in prior) s[k] = s.GetValueOrDefault(k) + PriorTop * v;
        if (s.Count == 0) return null;
        var ranked = s.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        var top = ranked[0];
        var second = ranked.Count > 1 ? ranked[1].Value : 0;
        if (top.Value < MinScore || top.Value < MinMargin * second) return null;
        return new KbLabel(top.Key, "glossary", Math.Round(top.Value / (top.Value + second), 3));
    }
}
