using System.Text.RegularExpressions;

namespace MlCpu.Modernize;

/// <summary>
/// The class name of a CALLed program from its header (Q2): the business nouns of its "Function :" line (Rate factors of
/// interest periods → RateFactors; Queries on an interest schedule model → InterestScheduleModelQueries; Build the
/// interest schedule model → InterestScheduleModelBuilder), or of its title ("DECIMAL KERNEL - ..." → DecimalKernel).
/// A program whose header says neither keeps its program id. The evidence (the header line and its text) is the class's
/// provenance: its Javadoc and modernize/names.json.
/// </summary>
public static partial class HeaderNames
{
    public sealed record Evidence(string Name, string Source, int Line, string Text);

    static readonly HashSet<string> Articles = new(StringComparer.OrdinalIgnoreCase) { "the", "a", "an", "one", "its", "and" };
    static readonly HashSet<string> Prepositions = new(StringComparer.OrdinalIgnoreCase) { "of", "on", "over", "from", "to", "at", "in", "for", "with", "by", "into" };
    /// <summary>A Function line that starts with an imperative: the class is its agent.</summary>
    static readonly Dictionary<string, string> Agents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["build"] = "Builder", ["apply"] = "Applier", ["compute"] = "Calculator", ["calculate"] = "Calculator", ["derive"] = "Deriver",
        ["generate"] = "Generator", ["process"] = "Processor", ["validate"] = "Validator", ["format"] = "Formatter", ["load"] = "Loader",
    };

    [GeneratedRegex(@"^Function\s*:\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex FunctionLine();
    [GeneratedRegex(@"^\s*[A-Za-z][A-Za-z ]*:\s")]
    private static partial Regex Label();
    [GeneratedRegex(@"^([A-Z][A-Z ]*[A-Z])\s+-\s+\S")]
    private static partial Regex Title();
    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9]*")]
    private static partial Regex Word();

    /// <summary>The name the program's header gives, with its evidence; null when the header names nothing.</summary>
    public static Evidence? FromHeader(SourceProgram p)
    {
        var header = p.Comments.Where(kv => kv.Key <= 30).ToList();
        for (int i = 0; i < header.Count; i++)
        {
            var (line, text) = header[i];
            if (FunctionLine().Match(text.Trim()) is not { Success: true } m) continue;
            var phrase = m.Groups[1].Value.Trim();
            // its continuation lines (indented, no label of their own, not a source reference)
            for (int j = i + 1; j < header.Count && header[j].Key == header[j - 1].Key + 1; j++)
            {
                var c = header[j].Value;
                var t = c.Trim();
                if (t.Length == 0 || !c.StartsWith("     ", StringComparison.Ordinal) || Label().IsMatch(c) || t.StartsWith("SRC", StringComparison.Ordinal) || t.StartsWith("Java", StringComparison.Ordinal)) break;
                phrase += " " + t;
            }
            return Name(phrase) is { } name ? new Evidence(name, "Function", line, phrase) : null;
        }
        foreach (var (line, text) in header)
        {
            var t = text.Trim().Trim('*').Trim();
            if (t.Length == 0) continue;
            if (Title().Match(t) is { Success: true } tm) return new Evidence(Pascal(Word().Matches(tm.Groups[1].Value).Select(w => w.Value)), "title", line, t);
            break;
        }
        return null;
    }

    /// <summary>The noun phrase of a Function line as a class name; null when it has none.</summary>
    static string? Name(string phrase)
    {
        // its first clause
        var cut = phrase.IndexOfAny([';', ':', '(', ',']);
        if (cut >= 0) phrase = phrase[..cut];
        var words = Word().Matches(phrase).Select(w => w.Value).Where(w => !Articles.Contains(w)).ToList();
        if (words.Count == 0) return null;
        List<string> Until(int from) => words.Skip(from).TakeWhile(w => !Prepositions.Contains(w)).ToList();
        if (Agents.TryGetValue(words[0], out var agent))
        {
            var obj = Until(1);
            return obj.Count == 0 ? null : Pascal(obj) + agent;
        }
        var head = Until(0);
        if (head.Count == 0) return null;
        if (head.Count == 1 && words.Count > 1)
        {
            // one generic word (Queries on ..., EMI of ...): what it is of (up to a participle: model passed by reference)
            var complement = Until(head.Count + 1);
            int participle = complement.Count > 1 ? complement.FindIndex(1, w => w.EndsWith("ed", StringComparison.OrdinalIgnoreCase)) : -1;
            if (participle > 0) complement = complement.Take(participle).ToList();
            if (complement.Count > 0) head = [.. complement, .. head];
        }
        return head.Count > 5 ? null : Pascal(head);
    }

    /// <summary>Words as a Java class name: each capitalised, an acronym (EMI) as a word (Emi), a mixed-case word (MathUtil) as written.</summary>
    static string Pascal(IEnumerable<string> words) =>
        string.Concat(words.Select(w => w.Any(char.IsLower) ? char.ToUpperInvariant(w[0]) + w[1..] : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
}
