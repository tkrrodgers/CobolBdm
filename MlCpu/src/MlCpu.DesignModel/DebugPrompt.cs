using System.Text;
using System.Text.RegularExpressions;

namespace MlCpu.DesignModel;

/// <summary>
/// Debug mode (implement --debug-prompt): once there is code that compiles, a repair turn sends a focused prompt in place of
/// the whole first prompt. It holds the contract (the first prompt's Task section), the verified equivalences and callee
/// contracts, the COBOL paragraphs the feedback points at (where the first divergence is made, the rules that write the
/// differing items) with the data items they use, the record kit, the current code and the feedback. The model repairs the
/// code in place: FNEM0300's first prompt is about 78,000 tokens, of which a repair needs a slice, and with the whole prompt in
/// view each repair tended to rewrite the program and drift (5/71 to 2/71 and back).
/// </summary>
public static class DebugPrompt
{
    const int SliceBudget = 24_000, DataBudget = 8_000, KitWhole = 40_000;

    static readonly Regex ParagraphHeader = new(@"^L\d+ ([A-Z0-9][A-Z0-9-]*)\.\s*$", RegexOptions.Multiline);
    static readonly Regex DataName = new(@"\b[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)+\b");

    public static string Build(string programId, string fullPrompt, string? outline, string? kit, string? equivalences, string? callees,
        IReadOnlyList<string> paragraphs, string code, string feedback, string status, IReadOnlyCollection<string> passing, bool editsOnly = false,
        IReadOnlyList<string>? tried = null)
    {
        var sb = new StringBuilder($"# Debugging {programId}\n");
        sb.Append($"{status} Below: the contract it must keep, the COBOL logic around the first place where it parts from the faithful program, " +
                  "the record kit, your current code and the test feedback. Repair the code: change what the feedback needs, keep the rest, and " +
                  "keep every scenario that passes passing.");
        if (editsOnly)
            sb.Append(" Most scenarios pass, so answer with SEARCH/REPLACE edits of the lines the feedback points at, not with whole files: " +
                      "a rewrite loses what passes.");
        sb.Append("\n\n");
        if (tried is { Count: > 0 })
            sb.Append("Repairs already tried since your best version, none better than it (do not repeat them; look for the cause another way):\n")
              .Append(string.Concat(tried.TakeLast(8).Select(t => $"- {t}\n"))).Append('\n');
        // Muse Glimmer's FNEM0300 at 69/71 returned the whole class twice and fell to 5/71: with most scenarios passing, the
        // instructions ask for edits only.
        if (editsOnly) feedback = feedback.Replace(Edits.Instructions, EditsOnlyInstructions);
        var task = Regex.Match(fullPrompt.Replace("\r\n", "\n"), @"(?ms)^# Task\n.*\z");
        if (task.Success) sb.Append(task.Value.TrimEnd()).Append("\n\n");
        if (equivalences is not null) sb.Append(equivalences.Replace("\r\n", "\n").TrimEnd()).Append("\n\n");
        if (callees is not null) sb.Append(callees.Replace("\r\n", "\n").TrimEnd()).Append("\n\n");
        if (outline is not null && paragraphs.Count > 0)
        {
            var slice = Paragraphs(outline.Replace("\r\n", "\n"), paragraphs);
            if (slice.Length > 0)
            {
                sb.Append("# The COBOL paragraphs the feedback points at (AST outline: where the first divergence is made or the rules that write the differing items, and the paragraphs they PERFORM)\n```\n")
                  .Append(slice).Append("```\n\n");
                var data = DataItems(outline.Replace("\r\n", "\n"), slice);
                if (data.Length > 0) sb.Append("# Their data items (AST outline)\n```\n").Append(data).Append("```\n\n");
            }
        }
        if (kit is not null) sb.Append(Kit(kit, code + "\n" + feedback)).Append("\n\n");
        sb.Append("# Your current code\n").Append(code.TrimEnd()).Append("\n\n");
        if (passing.Count > 0)
            sb.Append($"Scenarios that pass now (keep them passing): {string.Join(", ", passing.Take(40))}{(passing.Count > 40 ? $" and {passing.Count - 40} more" : "")}.\n\n");
        sb.Append("# Feedback\n").Append(Focus(feedback));
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
        Send as many edits as the repair needs. (Imports of the record kit, the platform and common java.* classes are added for you.)
        """;

    const int DetailBudget = 4_000;

    /// <summary>
    /// The feedback cut to the first divergence: of the CALL-level comparison, the summary and the first CALL in detail (the other
    /// kinds stay listed in the summary); the record-level detail within a budget; everything else (acceptance, regressions,
    /// compile errors, edit instructions) as it is.
    /// </summary>
    static string Focus(string feedback)
    {
        var s = feedback.Replace("\r\n", "\n");
        var calls = Regex.Matches(s, @"(?m)^### CALL ").Select(m => m.Index).ToList();
        if (calls.Count > 1)
        {
            int end = calls[1];
            var rest = Regex.Match(s[end..], @"(?m)^(acceptance |\d+ of \d+ acceptance|## |Detail for )");
            s = s[..end] + "(the other kinds are listed above; fix this one first)\n\n" + (rest.Success ? s[(end + rest.Index)..] : "");
        }
        var detail = s.IndexOf("\nDetail for ", StringComparison.Ordinal);
        if (detail >= 0)
        {
            var tail = Regex.Match(s[(detail + 1)..], @"(?m)^(These scenarios passed|Fix the cause\.)");
            int stop = tail.Success ? detail + 1 + tail.Index : s.Length;
            if (stop - detail > DetailBudget)
                s = s[..(detail + DetailBudget)] + "\n(record-level detail cut for room)\n" + s[stop..];
        }
        return s;
    }

    static readonly Regex Performed = new(@"\bPERFORM ([A-Z0-9][A-Z0-9-]*)(?: (?:THRU|THROUGH) ([A-Z0-9][A-Z0-9-]*))?");

    /// <summary>
    /// The named paragraphs of the outline and, breadth first, the paragraphs they PERFORM, within the budget; printed in
    /// program order. The named ones are where the divergence is made or the rules that write the differing items; the logic
    /// that computes the values is often further down (FNEM0300: 1100-RATE-FACTOR-FOR-PERIOD PERFORMs 2000-RATE-FACTOR, which
    /// PERFORMs the period-ratio loop), and code that makes no CALLs gives no deeper location.
    /// </summary>
    static string Paragraphs(string outline, IReadOnlyList<string> names)
    {
        var heads = ParagraphHeader.Matches(outline).Select(m => (Name: m.Groups[1].Value, Start: m.Index)).ToList();
        var text = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < heads.Count; i++)
        {
            int end = i + 1 < heads.Count ? heads[i + 1].Start : outline.Length;
            text[heads[i].Name] = outline[heads[i].Start..end];
            order[heads[i].Name] = i;
        }
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(names.Where(text.ContainsKey));
        int size = 0;
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (chosen.Contains(name) || size + text[name].Length > SliceBudget) continue;
            chosen.Add(name);
            size += text[name].Length;
            foreach (Match m in Performed.Matches(text[name]))
            {
                if (!order.TryGetValue(m.Groups[1].Value, out var from)) continue;
                int to = m.Groups[2].Success && order.TryGetValue(m.Groups[2].Value, out var t) ? t : from;
                for (int i = from; i <= to; i++) queue.Enqueue(heads[i].Name);
            }
        }
        return string.Concat(chosen.OrderBy(n => order[n]).Select(n => text[n]));
    }

    /// <summary>The DATA DIVISION lines of the data items the slice names.</summary>
    static string DataItems(string outline, string slice)
    {
        var names = DataName.Matches(slice).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        int procedure = outline.IndexOf("PROCEDURE DIVISION", StringComparison.Ordinal);
        var data = procedure > 0 ? outline[..procedure] : outline;
        var sb = new StringBuilder();
        foreach (var line in data.Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*L\d+\s+\d+\s+([A-Z0-9][A-Z0-9-]*)");
            if (!m.Success || !names.Contains(m.Groups[1].Value)) continue;
            if (sb.Length + line.Length > DataBudget) break;
            sb.Append(line.TrimEnd()).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The record kit: whole when it is small, else its rules and the records the code or the feedback names.</summary>
    static string Kit(string kit, string used)
    {
        kit = kit.Replace("\r\n", "\n");
        if (kit.Length <= KitWhole) return kit.TrimEnd();
        var parts = Regex.Split(kit, @"(?m)^(?=### )");
        var sb = new StringBuilder(parts[0]);
        foreach (var p in parts.Skip(1))
        {
            var head = Regex.Match(p, @"^### (\w+): \d+ bytes; ([A-Z0-9-]+)");
            if (head.Success && (used.Contains(head.Groups[1].Value, StringComparison.Ordinal) || used.Contains(head.Groups[2].Value, StringComparison.Ordinal)))
                sb.Append(p);
        }
        return sb.ToString().TrimEnd();
    }
}
