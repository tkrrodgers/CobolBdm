using System.Text;
using System.Text.Json.Nodes;

namespace MlCpu.Kit;

/// <summary>One traced CALL (mlcpu.call-trace.v1): its sequence number in the run and every argument's bytes before and after.</summary>
public sealed record TracedCall(int Seq, IReadOnlyList<byte[]> Before, IReadOnlyList<byte[]> After);

public sealed record TracedScenario(string Name, long Total, IReadOnlyList<TracedCall> Calls);

/// <summary>
/// A subprogram's worked examples in values (kit feature "examples-values"): every argument decoded field by field with the
/// record kit's layouts, OCCURS tables as rows of the occurrences in use, and after the CALL only what changed (before → after).
/// The CALLs come from the faithful program's traces (CallTrace); the ones that show a behaviour not shown yet (other
/// selector values, other fields changed, other return or status codes) are taken first, from every scenario, then the
/// others spread over the scenarios, until the budget is spent.
/// </summary>
public static class CallExamples
{
    public static TracedScenario ReadTrace(string scenario, string traceJsonPath)
    {
        var j = JsonNode.Parse(File.ReadAllText(traceJsonPath))!;
        long total = j["total"]?.GetValue<long>() ?? 0;
        var calls = j["calls"]!.AsArray().OfType<JsonObject>().Select(c => new TracedCall(c["seq"]!.GetValue<int>(),
            c["before"]!.AsArray().Select(x => Encoding.Latin1.GetBytes(x!.GetValue<string>())).ToList(),
            c["after"]!.AsArray().Select(x => Encoding.Latin1.GetBytes(x!.GetValue<string>())).ToList())).ToList();
        return new TracedScenario(scenario, total, calls);
    }

    /// <param name="conditions">Field name → its 88-level condition names (selectors: function codes, modes, switches).</param>
    /// <param name="budget">Characters of CALL text (the scenarios' headings and the legend come on top).</param>
    public static string Render(string pid, string host, IReadOnlyList<string> usingNames, string job, string stepId, string clock,
        KitLayouts layouts, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> conditions,
        IReadOnlyList<TracedScenario> scenarios, int budget = 70_000)
    {
        var sb = new StringBuilder($"# Worked examples: {pid} (CALLs traced in the oracle scenarios)\n\n");
        sb.Append($"{pid} is a subprogram: it runs only when {host} executes `CALL ... USING {string.Join(" ", usingNames)}` (step {stepId} of job {job}, ");
        sb.Append($"clock {clock}). These are CALLs of the faithful {pid}, traced from the translation that matches the GnuCOBOL oracle byte for ");
        sb.Append("byte: each argument as the caller passed it, then every item the CALL changed, with its value before and after. Your implementation ");
        sb.Append("gets the arguments as byte arrays in this order and must leave exactly these values in them (every other byte unchanged). The CALLs ");
        sb.Append("that show a behaviour not shown yet (other function codes or switches, other items changed, other return or status codes) are ");
        sb.Append("listed first from every scenario, then further CALLs spread over the scenarios.\n\n");
        sb.Append(KitValues.Legend).Append("\n\n");

        int args = usingNames.Count;
        var views = new RecordView?[args];
        for (int i = 0; i < args; i++)
        {
            int len = scenarios.SelectMany(s => s.Calls).Select(c => i < c.Before.Count ? c.Before[i].Length : 0).FirstOrDefault();
            views[i] = layouts.Argument(i, len) is { } l ? new RecordView(l) : null;
        }
        var selectors = Selectors(views, conditions, scenarios, before: true);
        var codes = Selectors(views, conditions, scenarios, before: false);

        // Candidates in scenario order; the text of each, rendered once.
        var all = scenarios.SelectMany((s, si) => s.Calls.Select(c => (Scenario: si, Call: c))).ToList();
        // A CALL's text depends on the CALL shown before it in its scenario (arguments it passes again are referred back to).
        var text = new Dictionary<(int, int, int), string>();
        string Text(int si, TracedCall c, TracedCall? prev)
        {
            var key = (si, c.Seq, prev?.Seq ?? -1);
            if (!text.TryGetValue(key, out var t)) text[key] = t = CallText(c, prev, usingNames, views);
            return t;
        }
        var chosen = scenarios.Select(_ => new SortedDictionary<int, TracedCall>()).ToList();
        int ScenarioSize(int si, SortedDictionary<int, TracedCall> calls)
        {
            int n = 0;
            TracedCall? prev = null;
            foreach (var c in calls.Values) { n += Text(si, c, prev).Length; prev = c; }
            return n;
        }
        var sizes = new int[scenarios.Count];
        int used = 0;
        bool full = false;
        bool TryAdd(int si, TracedCall c)
        {
            var with = new SortedDictionary<int, TracedCall>(chosen[si]) { [c.Seq] = c };
            int size = ScenarioSize(si, with);
            if (used - sizes[si] + size > budget) return false;
            used += size - sizes[si];
            sizes[si] = size;
            chosen[si] = with;
            return true;
        }
        // 1. Every CALL that shows something not shown yet (a selector value passed in, a code left behind, an argument or a
        //    table column changed), in scenario order: a greedy cover of the behaviours in the traces.
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var x in all)
        {
            var features = Features(x.Call, views, selectors, codes);
            if (features.All(covered.Contains)) continue;
            if (!TryAdd(x.Scenario, x.Call)) { full = true; continue; }
            covered.UnionWith(features);
        }
        // 2. Further CALLs, one scenario at a time in turn, each scenario's CALLs in run order.
        if (!full)
        {
            var queues = scenarios.Select((s, si) => new Queue<TracedCall>(s.Calls.Where(c => !chosen[si].ContainsKey(c.Seq)))).ToList();
            bool any = true;
            while (any && !full)
            {
                any = false;
                for (int si = 0; si < queues.Count && !full; si++)
                {
                    if (queues[si].Count == 0) continue;
                    any = true;
                    if (!TryAdd(si, queues[si].Dequeue())) full = true;
                }
            }
        }
        var notShown = new List<string>();
        for (int si = 0; si < scenarios.Count; si++)
        {
            var s = scenarios[si];
            var calls = chosen[si].Values.ToList();
            if (calls.Count == 0) { notShown.Add(s.Name); continue; }
            sb.Append($"## Scenario {s.Name}\n\n");
            sb.Append($"{pid} is CALLed {s.Total} time(s) in this scenario; {(calls.Count == 1 ? "CALL" : "CALLs")} {string.Join(", ", calls.Select(c => c.Seq))} {(calls.Count == 1 ? "is" : "are")} shown.\n\n");
            TracedCall? prev = null;
            foreach (var c in calls) { sb.Append(Text(si, c, prev)).Append('\n'); prev = c; }
        }
        int traced = all.Count, shown = chosen.Sum(c => c.Count);
        if (notShown.Count > 0 || shown < traced)
        {
            sb.Append("## Not shown\n\n");
            if (shown < traced) sb.Append($"{traced - shown} further traced CALL(s) are left out for length; they repeat behaviours shown above.\n");
            if (notShown.Count > 0) sb.Append($"Scenarios whose traced CALLs all repeat behaviours shown above: {string.Join(", ", notShown)}.\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// One CALL: each argument as passed in, then what the CALL changed. An argument that repeats the CALL shown before it
    /// (<paramref name="prev"/>, same scenario) is referred back to: "as CALL 16 left it", "as in CALL 16", or either with the
    /// few items that differ; every reference points to an earlier CALL of the same scenario section.
    /// </summary>
    public static string CallText(TracedCall c, TracedCall? prev, IReadOnlyList<string> usingNames, IReadOnlyList<RecordView?> views)
    {
        var sb = new StringBuilder($"### CALL {c.Seq}\n\n");
        for (int i = 0; i < c.Before.Count; i++)
        {
            var name = i < usingNames.Count ? usingNames[i] : $"argument {i + 1}";
            var v = i < views.Count ? views[i] : null;
            sb.Append($"- {name}: ");
            sb.Append(Reference(c, prev, i, v) ?? (v is null ? "«" + Clip(KitValues.Escape(c.Before[i]), 400) + "»" : v.Listing(c.Before[i], "  "))).Append('\n');
        }
        var changed = new StringBuilder();
        for (int i = 0; i < c.Before.Count && i < c.After.Count; i++)
        {
            if (c.Before[i].AsSpan().SequenceEqual(c.After[i])) continue;
            var name = i < usingNames.Count ? usingNames[i] : $"argument {i + 1}";
            var v = i < views.Count ? views[i] : null;
            if (v is null) { changed.Append($"- {name}: «{Clip(KitValues.Escape(c.After[i]), 400)}»\n"); continue; }
            changed.Append($"- {name}:\n").Append(RecordView.ChangesText(v.Changes(c.Before[i], c.After[i]), "  ")).Append('\n');
        }
        sb.Append(changed.Length == 0 ? "After the CALL: no argument changed.\n" : "After the CALL (before → after):\n" + changed);
        return sb.ToString();
    }

    /// <summary>The argument as a reference to the previous CALL shown, when it repeats it (exactly, or but for a few plain items).</summary>
    static string? Reference(TracedCall c, TracedCall? prev, int i, RecordView? v)
    {
        if (prev is null || i >= prev.Before.Count || i >= prev.After.Count) return null;
        var now = c.Before[i];
        if (now.AsSpan().SequenceEqual(prev.After[i])) return $"as CALL {prev.Seq} left it";
        if (now.AsSpan().SequenceEqual(prev.Before[i])) return $"as in CALL {prev.Seq}";
        if (v is null) return null;
        string? best = null;
        foreach (var (image, how) in new[] { (prev.After[i], $"as CALL {prev.Seq} left it"), (prev.Before[i], $"as in CALL {prev.Seq}") })
        {
            var diffs = v.Changes(image, now);
            if (diffs.Count == 0 || diffs.Any(d => d.Table is not null || d.Field.Name == "FILLER")) continue;
            int set = v.Plain.Count(f => !KitValues.Initial(KitValues.Slice(now, f.Offset, f.Length)));
            if (diffs.Count > Math.Max(3, set / 3)) continue;
            var text = $"{how}, except {string.Join(", ", diffs.Select(d => $"{d.Name} = {d.After}"))}";
            if (best is null || text.Length < best.Length) best = text;
        }
        return best;
    }

    /// <summary>
    /// Per argument, the plain items that tell behaviours apart: those with 88-level names, short text items and small binary
    /// codes, whose value (before the CALL for <paramref name="before"/>, after it otherwise) varies over the traced CALLs and
    /// takes at most 12 values.
    /// </summary>
    static List<(int Arg, KitField Field)> Selectors(RecordView?[] views, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> conditions,
        IReadOnlyList<TracedScenario> scenarios, bool before)
    {
        var list = new List<(int, KitField)>();
        for (int i = 0; i < views.Length; i++)
        {
            if (views[i] is not { } v) continue;
            foreach (var f in v.Plain.Where(f => conditions.ContainsKey(f.Name) || (f.Kind == "text" && f.Length <= 12) || (f.Kind == "binary" && f.Digits <= 4)))
            {
                var values = scenarios.SelectMany(s => s.Calls).Where(c => i < c.Before.Count && i < c.After.Count)
                    .Select(c => Convert.ToHexString(KitValues.Slice(before ? c.Before[i] : c.After[i], f.Offset, f.Length))).Distinct().Take(13).Count();
                if (values is >= 2 and <= 12) list.Add((i, f));
            }
        }
        return list;
    }

    /// <summary>What a CALL shows: the selector values passed in, the codes left behind, the arguments and table columns it changes.</summary>
    static HashSet<string> Features(TracedCall c, RecordView?[] views, List<(int Arg, KitField Field)> selectors, List<(int Arg, KitField Field)> codes)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (i, f) in selectors) if (i < c.Before.Count) set.Add($"in {f.Name}={KitValues.Show(f, c.Before[i], f.Offset)}");
        foreach (var (i, f) in codes) if (i < c.After.Count) set.Add($"out {f.Name}={KitValues.Show(f, c.After[i], f.Offset)}");
        bool any = false;
        for (int i = 0; i < views.Length && i < c.Before.Count && i < c.After.Count; i++)
        {
            if (c.Before[i].AsSpan().SequenceEqual(c.After[i])) continue;
            any = true;
            set.Add($"changes argument {i}");
            if (views[i] is { } v)
                foreach (var ch in v.Changes(c.Before[i], c.After[i]).Where(x => x.Table is not null).DistinctBy(x => x.Field.Name)) set.Add($"changes {ch.Field.Name}");
        }
        if (!any) set.Add("changes nothing");
        return set;
    }

    static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
