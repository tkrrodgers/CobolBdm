using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MlCpu.DesignModel.Forward;

/// <summary>
/// Debug mode for forward engineering (fwd-implement --debug-prompt; ForwardEngineeringFineract.md Appendix E, the repair loop
/// proven on FNEM0300 by DebugPrompt): once the slice's code compiles, a repair turn sends a focused prompt in place of the first
/// prompt and the code. It holds the contract (the first prompt's Task section without its output request), the slice's rule
/// digests and design, the API types the code or the feedback names, the current code, the scenarios that pass and the
/// feedback. With the whole package in view each repair tended to rewrite the code and drift. When the package has walks
/// (forward.json "walks": the reference's intermediate values of each public scenario, rule by rule), the walks of up to two
/// failing public scenarios come before the feedback: the derivation, not only the expected result (the walk of FNEM0300).
/// </summary>
public static class ForwardDebugPrompt
{
    public static string Build(ForwardInputs inputs, ForwardInputs.Slice slice, string firstPrompt, JsonNode? design, string code, string feedback,
        string status, IReadOnlyCollection<string> passing, bool editsOnly, IReadOnlyList<string> tried, IReadOnlyList<string>? failingPublic = null)
    {
        // Raw strings take their source file's line ends (CRLF in a CRLF checkout); the prompt goes out with LF anyway.
        firstPrompt = firstPrompt.Replace("\r\n", "\n");
        feedback = feedback.Replace("\r\n", "\n");
        var sb = new StringBuilder($"# Debugging the slice \"{slice.Name}\"\n");
        sb.Append($"{status} Below: the contract the code must keep, the rules of the slice, the API types it uses, your current code and " +
                  "the test feedback. Repair the code: change what the feedback needs, keep the rest, and keep every scenario that passes passing.");
        if (editsOnly)
            sb.Append(" Most scenarios pass, so answer with SEARCH/REPLACE edits of the lines the feedback points at, not with whole files: " +
                      "a rewrite loses what passes.");
        sb.Append("\n\n");
        if (tried.Count > 0)
            sb.Append("Repairs already tried since your best version, none better than it (do not repeat them; look for the cause another way):\n")
              .Append(string.Concat(tried.TakeLast(8).Select(t => $"- {t}\n"))).Append('\n');
        var task = Regex.Match(firstPrompt, @"(?ms)^# Task\n.*\z");
        if (task.Success)
        {
            var t = task.Value;
            int output = t.IndexOf("\nOutput:", StringComparison.Ordinal);
            sb.Append((output > 0 ? t[..output] : t).TrimEnd()).Append("\n\n");
        }
        sb.Append(inputs.DigestSection(slice.Digests));
        if (design is not null) sb.Append($"# Slice design: {slice.Name} (mlcpu.slice-design.v1)\n").Append(design.ToJsonString(Program.Json)).Append("\n\n");
        sb.Append(ApiTypes(inputs, slice, code + "\n" + feedback));
        sb.Append("# Your current code\n").Append(code.TrimEnd()).Append("\n\n");
        if (passing.Count > 0)
            sb.Append($"Public scenarios that pass now (keep them passing): {string.Join(", ", passing.Take(40))}{(passing.Count > 40 ? $" and {passing.Count - 40} more" : "")}.\n\n");
        sb.Append(Walks(inputs, slice, failingPublic ?? []));
        if (editsOnly) feedback = feedback.Replace(ForwardImplement.Instructions(inputs).Replace("\r\n", "\n"), EditsOnlyInstructions.Replace("\r\n", "\n"));
        sb.Append("# Feedback\n").Append(feedback.TrimEnd()).Append('\n');
        return sb.ToString();
    }

    public const string EditsOnlyInstructions = """
        Answer with SEARCH/REPLACE edits only; do not return whole files:
        <<<<<<< SEARCH Name.java
        the lines to replace, copied exactly from the current file
        =======
        the lines that take their place
        >>>>>>> REPLACE
        The SEARCH lines are removed and the REPLACE lines are put in their place, so to add lines repeat a neighbouring line in
        both parts. The SEARCH lines must occur once in that file; include a few surrounding lines when a line is not unique.
        Send as many edits as the repair needs. (Missing imports of the API, of your own classes and of common java.* classes are added for you.)
        """;

    /// <summary>At most this much walk text in one prompt (about 6,700 tokens).</summary>
    const int WalkBudget = 24_000;

    /// <summary>
    /// The walks of up to two failing public scenarios within the budget, "" when there are none: the shortest walk that goes the
    /// whole way and the shortest that ends in a rejection (a rejection's walk stops early, so shortest-first alone shows only
    /// rejections), else the two shortest of one kind.
    /// </summary>
    static string Walks(ForwardInputs inputs, ForwardInputs.Slice slice, IReadOnlyList<string> failing)
    {
        var all = failing.Select(id => inputs.Walk(slice.Name, id)).OfType<string>().OrderBy(w => w.Length).ToList();
        var whole = all.Where(w => !w.Contains("**Rejected**", StringComparison.Ordinal)).ToList();
        var rejected = all.Where(w => w.Contains("**Rejected**", StringComparison.Ordinal)).ToList();
        var walks = whole.Take(1).Concat(rejected.Take(1)).ToList();
        walks.AddRange(all.Where(w => !walks.Contains(w)));
        var sb = new StringBuilder();
        int used = 0, shown = 0;
        foreach (var w in walks)
        {
            if (shown == 2 || used + w.Length > WalkBudget) break;
            // one heading level down: the walk's title becomes a section of this prompt
            sb.Append(Regex.Replace(w.Trim(), "(?m)^(#+) ", "#$1 ")).Append("\n\n");
            used += w.Length;
            shown++;
        }
        return shown == 0 ? "" : "# The walk of failing public scenarios\nThe reference implementation's intermediate values for " +
            (shown == 1 ? "a public scenario your code fails" : "two public scenarios your code fails") + ", in the order the rules compute them. " +
            "Find the first step where your code computes something else, and repair that step.\n\n" + sb;
    }

    /// <summary>The API source files whose type the code or the feedback names, and the SPI the slice implements.</summary>
    static string ApiTypes(ForwardInputs inputs, ForwardInputs.Slice slice, string used)
    {
        var sb = new StringBuilder();
        foreach (var (path, src) in inputs.Api)
        {
            var type = Path.GetFileNameWithoutExtension(path);
            var fq = path[..^5].Replace('/', '.');
            if (fq != slice.Implements && !Regex.IsMatch(used, $@"\b{Regex.Escape(type)}\b")) continue;
            sb.Append($"## {path}\n```java\n{src.TrimEnd()}\n```\n");
        }
        return sb.Length == 0 ? "" : $"# The API types your code uses ({inputs.ApiModule}; fixed)\n" + sb + "\n";
    }
}
