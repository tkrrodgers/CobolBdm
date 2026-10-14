using System.Text;
using System.Text.RegularExpressions;

namespace MlCpu.DesignModel.Forward;

/// <summary>
/// The leak checker (ForwardEngineeringFineract.md section 4.3, decision F3): no text of the source system may reach a prompt.
/// The dictionary (forward.json "leakDictionary", built by the package's sme/tools/build_leak_dictionary.py) lists every
/// identifier of the source system's classes in scope with a status; only "leak" names are flagged (the names a real estate's
/// COBOL could not give, of 8 characters or more, of two words or more). The source system's name words are flagged as
/// substrings, in any case. Package files are scanned when the package loads; every prompt is scanned before it is sent, and
/// a hit stops the step (<see cref="LeakException"/>). Model output (designs, code) is measured, not blocked: <see cref="Overlap"/>.
/// </summary>
public sealed class LeakChecker
{
    /// <summary>The source system's name words, matched as substrings in any case.</summary>
    public static readonly string[] NameWords = ["fineract", "mifos"];

    readonly HashSet<string> leaks;
    readonly HashSet<string> all;

    public string Path { get; }
    public int LeakNames => leaks.Count;

    public LeakChecker(string dictionaryPath)
    {
        Path = dictionaryPath;
        leaks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(dictionaryPath))
        {
            if (line.StartsWith('#') || line.Trim().Length == 0) continue;
            var parts = line.Split('\t');
            all.Add(parts[0]);
            if (parts.Length > 1 && parts[1] == "leak") leaks.Add(parts[0]);
        }
    }

    public sealed record Hit(string Token, int Line, string Where);

    static readonly Regex Identifier = new(@"[A-Za-z_][A-Za-z0-9_]*");
    static readonly Regex Heading = new(@"^#{1,6} .*$", RegexOptions.Multiline);

    /// <summary>The leaks in a text: flagged identifiers and the name words, each with its line and the heading above it.</summary>
    public List<Hit> Scan(string text)
    {
        var hits = new List<Hit>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        string where = "";
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (Heading.IsMatch(line)) where = line.Trim();
            foreach (Match m in Identifier.Matches(line))
                if (leaks.Contains(m.Value)) hits.Add(new Hit(m.Value, i + 1, where));
            foreach (var w in NameWords)
                if (line.Contains(w, StringComparison.OrdinalIgnoreCase)) hits.Add(new Hit(w, i + 1, where));
        }
        return hits;
    }

    /// <summary>Throws when the prompt carries a leak: the step is blocked, naming each token and where it is.</summary>
    public void Check(string prompt, string what)
    {
        var hits = Scan(prompt);
        if (hits.Count == 0) return;
        throw new LeakException(what, hits);
    }

    /// <summary>
    /// The source system's identifiers in a model's text (contamination, section 10.3): the distinct dictionary names it uses
    /// (any status, 8 characters or more, two words or more) and the ones a prompt could not have given it ("leak" names).
    /// </summary>
    public (IReadOnlyList<string> All, IReadOnlyList<string> Leaks, IReadOnlyList<string> NameWords) Overlap(string text)
    {
        var tokens = Identifier.Matches(text).Select(m => m.Value).Where(t => t.Length >= 8 && Regex.IsMatch(t, "[a-z][A-Z]|^[A-Z][a-z]+[A-Z]"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inAll = tokens.Where(all.Contains).OrderBy(t => t, StringComparer.Ordinal).ToList();
        var inLeaks = tokens.Where(leaks.Contains).OrderBy(t => t, StringComparer.Ordinal).ToList();
        var words = NameWords.Where(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)).ToList();
        return (inAll, inLeaks, words);
    }

    /// <summary>Scans the files of a package that can reach a prompt (or must pass the gate); the SME's tools are excluded.</summary>
    public List<(string File, Hit Hit)> ScanPackage(string packageDir)
    {
        var found = new List<(string, Hit)>();
        foreach (var f in Directory.EnumerateFiles(packageDir, "*", SearchOption.AllDirectories))
        {
            var rel = System.IO.Path.GetRelativePath(packageDir, f).Replace('\\', '/');
            if (rel.StartsWith("sme/", StringComparison.Ordinal) || rel.Contains("/build/", StringComparison.Ordinal)
                || rel.Contains("/.gradle/", StringComparison.Ordinal) || rel.StartsWith(".", StringComparison.Ordinal)) continue;
            if (!(rel.EndsWith(".md") || rel.EndsWith(".json") || rel.EndsWith(".java") || rel.EndsWith(".kts"))) continue;
            foreach (var h in Scan(File.ReadAllText(f))) found.Add((rel, h));
        }
        return found;
    }
}

/// <summary>A prompt that would carry the source system's text: the step stops.</summary>
public sealed class LeakException(string what, IReadOnlyList<LeakChecker.Hit> hits) : Exception(Describe(what, hits))
{
    public IReadOnlyList<LeakChecker.Hit> Hits { get; } = hits;

    static string Describe(string what, IReadOnlyList<LeakChecker.Hit> hits)
    {
        var sb = new StringBuilder($"LEAK: the {what} carries {hits.Count} source-system name(s); it is not sent:");
        foreach (var h in hits.Take(20)) sb.Append($"\n  {h.Token} (line {h.Line}{(h.Where.Length > 0 ? ", under " + h.Where : "")})");
        if (hits.Count > 20) sb.Append($"\n  ... {hits.Count - 20} more");
        return sb.ToString();
    }
}
