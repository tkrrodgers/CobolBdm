using System.Text;
using System.Text.Json.Nodes;
using MlCpu.Kit;

namespace MlCpu.DesignModel;

/// <summary>
/// CALL-level shadow feedback (kit feature shadow-feedback) for a subprogram capability. The shadow run answers every CALL
/// of a failing scenario twice, faithful program and model each on its own copy of the arguments, and goes on with the
/// faithful results. This names, for each kind of difference (which result items differ), the first CALL that shows it: what
/// was passed in that matters (selectors: items with 88-level names, short codes; small input records in full), every result
/// item that differs (its value before the CALL, the faithful program's, the model's), and the packet rules that write those
/// items. A difference the outputs never show (a field the caller ignores) is reported like any other: the model sees every
/// way its program parts from the faithful one, not only the one the first differing output byte happens to reveal.
/// Values are in the worked examples' form.
/// </summary>
public sealed class ShadowFeedback
{
    readonly string programId;
    readonly KitLayouts? layouts;
    readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> conditions;
    readonly List<(string Id, string Paragraph, List<string> When, List<string> Writes, string Cobol)> rules = [];

    /// <summary>The programs it CALLs, traced inside each shadowed CALL (kit feature subprogram-calls); empty otherwise.</summary>
    public IReadOnlyList<string> Callees { get; }
    /// <summary>Callee → the record names of its USING arguments (the first CALL statement to it), to show its arguments as values.</summary>
    readonly Dictionary<string, List<string>> usingNames = new(StringComparer.Ordinal);

    /// <summary>The callees with a verified contract (callee-contracts.md next to the packet): a model may compute their results itself.</summary>
    readonly HashSet<string> contracted = new(StringComparer.Ordinal);

    public ShadowFeedback(string programId, string? layoutsPath, string packetPath, bool nested = false)
    {
        this.programId = programId;
        var contracts = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(packetPath))!, "callee-contracts.md");
        if (nested && File.Exists(contracts))
            foreach (var line in File.ReadLines(contracts))
                if (line.StartsWith("## ", StringComparison.Ordinal)) contracted.Add(line[3..].Trim());
        Callees = nested && File.Exists(packetPath) ? ImplementStep.CalledPrograms(packetPath) : [];
        if (Callees.Count > 0)
        {
            // The USING lists, from the packet's CALL statements and from the AST outline (which has the ones the packet leaves out).
            var outline = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(packetPath))!, "ast-outline.txt");
            var calls = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(packetPath), "\"cobol\":\\s*\"CALL '([A-Z0-9#@$-]+)' USING ([^\"]*)\"")
                .Concat(File.Exists(outline)
                    ? System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(outline), @"\bCALL '([A-Z0-9#@$-]+)' USING ([A-Z0-9 -]+)")
                    : Enumerable.Empty<System.Text.RegularExpressions.Match>());
            foreach (var m in calls)
                usingNames.TryAdd(m.Groups[1].Value, m.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w is not ("BY" or "REFERENCE" or "CONTENT" or "VALUE")).ToList());
        }
        layouts = layoutsPath is not null && File.Exists(layoutsPath) ? KitLayouts.Parse(File.ReadAllText(layoutsPath)) : null;
        conditions = KitUpgrade.Conditions(packetPath);
        if (File.Exists(packetPath))
            foreach (var r in JsonNode.Parse(File.ReadAllText(packetPath))!["rules"]?.AsArray().OfType<JsonObject>() ?? [])
                rules.Add((r["id"]!.GetValue<string>(), r["paragraph"]?.GetValue<string>() ?? "",
                    r["when"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [],
                    r["writes"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [],
                    string.Join("; ", r["statements"]?.AsArray().OfType<JsonObject>().Select(s => s["cobol"]?.GetValue<string>() ?? "").Take(3) ?? [])));
    }

    const int Budget = 7000;

    public string Render(IReadOnlyList<ShadowScenario> scenarios)
    {
        if (scenarios.Count == 0) return "";
        var sb = new StringBuilder($"## CALL-level comparison: where your {programId} leaves different results\n");
        sb.Append($"Each CALL of the failing scenarios below was answered twice, by the faithful {programId} and by yours, each on its own copy of the ");
        sb.Append("caller's arguments; the run went on with the faithful results, so every CALL had the faithful run's inputs.\n");
        foreach (var s in scenarios)
            sb.Append($"- '{s.Name}': {s.Calls} CALL(s), {(s.Differing == 0 ? "all with the faithful results" : $"{s.Differing} with different results (first: CALL {s.First[0].Seq})")}.\n");
        if (!scenarios.Any(s => s.First.Count > 0))
        {
            sb.Append("Every CALL left exactly the faithful results: the scenarios fail for another reason (see below).\n");
            return sb.ToString();
        }
        // Kinds of difference: the items that differ (and whether a CALL ended abnormally), over the CALLs kept in full.
        var kept = scenarios.SelectMany(s => s.First.Select(c => (Scenario: s.Name, Call: c))).ToList();
        var kinds = kept.GroupBy(x => string.Join(",", Differing(x.Call)) + (x.Call.FaithfulEnd is null) + (x.Call.YoursEnd is null))
            .Select(g => g.ToList()).ToList();
        sb.Append(kinds.Count == 1
            ? "Your results differ in one way:\n"
            : $"Your results differ in {kinds.Count} different ways (the items that differ); the first CALL of each is shown:\n");
        foreach (var g in kinds)
            sb.Append($"- {Clip(string.Join(", ", Differing(g[0].Call)), 200)}{(g[0].Call.YoursEnd is not null ? " (yours ended abnormally)" : "")}: " +
                      $"{string.Join("; ", g.GroupBy(x => x.Scenario).Select(s => $"CALL {string.Join(", ", s.Select(x => x.Call.Seq))} of '{s.Key}'"))}\n");
        int n = 0;
        foreach (var g in kinds)
        {
            if (sb.Length > Budget) { sb.Append($"(the other {kinds.Count - n} kind(s) are not shown in detail: feedback budget reached)\n"); break; }
            n++;
            sb.Append(Detail(g[0].Scenario, g[0].Call));
        }
        return sb.ToString();
    }

    List<RecordView?> Views(ShadowCall c) =>
        Enumerable.Range(0, c.Before.Count).Select(i => layouts?.Argument(i, c.Before[i].Length) is { } l ? new RecordView(l) : null).ToList();

    List<string> Names(ShadowCall c) =>
        Enumerable.Range(0, c.Before.Count).Select(i => layouts is not null && i < layouts.LinkageNames.Count ? layouts.LinkageNames[i] : $"argument {i + 1}").ToList();

    /// <summary>The names of the items whose results differ (a raw argument by its name).</summary>
    List<string> Differing(ShadowCall c)
    {
        var views = Views(c);
        var names = Names(c);
        return Enumerable.Range(0, Math.Min(c.Faithful.Count, c.Yours.Count)).Where(i => !c.Faithful[i].AsSpan().SequenceEqual(c.Yours[i]))
            .SelectMany(i => views[i]?.Changes(c.Faithful[i], c.Yours[i]).Select(d => d.Field.Name).Distinct() ?? [names[i]]).ToList();
    }

    string Detail(string scenario, ShadowCall c)
    {
        var sb = new StringBuilder($"\n### CALL {c.Seq} of '{scenario}'\n");
        if (c.FaithfulEnd is not null || c.YoursEnd is not null)
            sb.Append($"The faithful CALL {(c.FaithfulEnd is null ? "returned normally" : "ended with " + c.FaithfulEnd)}; yours {(c.YoursEnd is null ? "returned normally" : "ended with " + Clip(c.YoursEnd, 300))}.\n");
        var views = Views(c);
        var names = Names(c);
        sb.Append("Passed in (the items that select what the CALL does; small input records in full):\n");
        for (int i = 0; i < c.Before.Count; i++)
        {
            bool output = !c.Before[i].AsSpan().SequenceEqual(c.Faithful[i]) || !c.Before[i].AsSpan().SequenceEqual(c.Yours[i]);
            sb.Append($"- {names[i]}: ").Append(PassedIn(views[i], c.Before[i], output)).Append('\n');
        }
        sb.Append("Results that differ (the value before the CALL; the faithful program's; yours):\n");
        var differing = new List<string>();
        for (int i = 0; i < c.Before.Count; i++)
        {
            if (c.Faithful[i].AsSpan().SequenceEqual(c.Yours[i])) continue;
            if (views[i] is not { } v) { sb.Append($"- {names[i]}: faithful «{Clip(KitValues.Escape(c.Faithful[i]), 200)}», yours «{Clip(KitValues.Escape(c.Yours[i]), 200)}»\n"); continue; }
            var diffs = v.Changes(c.Faithful[i], c.Yours[i]);
            differing.AddRange(diffs.Select(d => d.Field.Name));
            sb.Append($"- {names[i]}:\n");
            int shown = 0;
            foreach (var d in diffs)
            {
                if (shown++ == 16) { sb.Append($"  … and {diffs.Count - 16} more differing item(s): {string.Join(", ", diffs.Skip(16).Select(x => x.Field.Name).Distinct().Take(12))}\n"); break; }
                sb.Append($"  {d.Name}: before {KitValues.Show(d.Field, c.Before[i], d.Offset)}; faithful {d.Before}; yours {d.After}\n");
            }
            var agreedChanges = v.Changes(c.Before[i], c.Faithful[i]).Where(x => !diffs.Any(d => d.Offset == x.Offset)).ToList();
            var agreed = agreedChanges.Count <= 6
                ? agreedChanges.Select(x => $"{x.Name} ({x.Before} → {x.After})").ToList()
                : agreedChanges.Select(x => x.Field.Name).Distinct().ToList();
            if (agreed.Count > 0) sb.Append($"  (the faithful CALL also changed {Clip(string.Join(", ", agreed), 400)}, and yours did the same)\n");
        }
        var bases = differing.Distinct().ToHashSet(StringComparer.Ordinal);
        var writers = rules.Where(r => r.Writes.Any(bases.Contains)).Take(5).ToList();
        if (writers.Count > 0)
        {
            sb.Append($"Rules of {programId} that write these items (requirements packet; the AST outline has their statements):\n");
            foreach (var r in writers)
                sb.Append($"- {r.Id} in {r.Paragraph}{(r.When.Count > 0 ? $", when {string.Join(" AND ", r.When.Select(w => Clip(w, 100)))}" : "")}: " +
                          $"{Clip(r.Cobol, 160)} (writes {string.Join(", ", r.Writes.Where(bases.Contains))})\n");
        }
        if (c.Nested is { } nested) sb.Append(NestedDetail(nested));
        return sb.ToString();
    }

    /// <summary>
    /// What the CALL did through its own CALLs, faithful against yours. Each of your CALLs is paired with the next faithful CALL
    /// to the same program with the same selectors (short codes of its first argument, such as MY-FUNCTION or DF-OP), so the
    /// CALLs you replace by your own code are skipped, not misaligned; inputs are compared on the items the faithful CALL does
    /// not write (a result left over from an earlier CALL is not an input). Shown: the first of your CALLs without a faithful
    /// counterpart, or with other inputs, or with other results; when all of them match, the last one and what it returned, so
    /// that the difference is seen to come from your own code around the CALLs.
    /// </summary>
    string NestedDetail(ShadowNested n, string? header = null)
    {
        static string Times(int k) => k == 1 ? "once" : $"{k} times";
        static string Counts(IEnumerable<ShadowNestedCall> calls) =>
            string.Join(", ", calls.GroupBy(c => c.Program).Select(g => $"{g.Key} {Times(g.Count())}")) is { Length: > 0 } s ? s : "nothing";
        var sb = new StringBuilder(header ?? $"Inside this CALL (the CALLs it makes; the faithful {programId}'s against yours):\n");
        sb.Append($"- the faithful CALLed {Counts(n.Faithful)}; yours CALLed {Counts(n.Yours)}");
        var computed = n.Faithful.Select(c => c.Program).Distinct().Where(p => n.Yours.All(y => y.Program != p)).ToList();
        if (computed.Count > 0) sb.Append($" (what {string.Join(", ", computed)} did, you compute yourself)");
        if (n.Truncated || n.FaithfulTotal > n.Faithful.Count || n.YoursTotal > n.Yours.Count) sb.Append($" (the first {n.Faithful.Count} and {n.Yours.Count} compared)");
        sb.Append(".\n");
        int fi = 0;
        var nth = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var y in n.Yours)
        {
            int k = nth[y.Program] = nth.GetValueOrDefault(y.Program) + 1;
            var key = Key(y);
            int j = -1;
            for (int t = fi; t < n.Faithful.Count; t++)
                if (Key(n.Faithful[t]) == key) { j = t; break; }
            var label = $"your CALL {k} to {y.Program}{(Selectors(y) is { Length: > 0 } sel ? $" ({sel})" : "")}";
            if (j < 0)
            {
                // The faithful log was cut short (its first CALLs only): past its end there is nothing to pair with, which says
                // nothing about yours. FNLS0100's run made fewer CALLs per loan, so its first 300 reached further into the job.
                if (n.FaithfulTotal > n.Faithful.Count)
                {
                    sb.Append($"- your CALLs got the same results as the faithful program's as far as its {n.Faithful.Count:N0} logged CALLs go (your CALL {k} to {y.Program} lies past them).\n");
                    return sb.ToString();
                }
                sb.Append($"- {label} has no counterpart: after the CALLs matched so far the faithful program made no CALL to {y.Program} with these codes. It passed: {NestedArgs(y, y.Before)}\n");
                return sb.ToString();
            }
            var f = n.Faithful[j];
            fi = j + 1;
            // Inputs that differ only in form (30 passed as 3E1 to FNDKDFO) or in items the callee does not use (DT-N to an 'LY'
            // CALL of FNDTKRN) do not matter while the results are the same: such a CALL is matched, and the comparison goes on.
            var inputs = InputDiff(f, y);
            var outputs = ResultDiff(f, y);
            if (outputs.Count == 0 && f.End == y.End) continue;
            if (inputs.Count > 0)
            {
                sb.Append($"- {label} is the first that gets other results than the faithful program's matching CALL: {Clip(string.Join("; ", outputs), 500)}\n");
                sb.Append($"  It passed other inputs: {Clip(string.Join("; ", inputs), 600)}\n");
                sb.Append($"  The faithful CALL returned: {NestedChanges(f)}\n");
                return sb.ToString();
            }
            {
                sb.Append($"- {label} passed the same inputs as the faithful program's matching CALL but got other results: {Clip(string.Join("; ", outputs), 600)}");
                if (f.End != y.End) sb.Append($"; the faithful CALL {(f.End is null ? "returned normally" : "ended with " + Clip(f.End, 160))}, yours {(y.End is null ? "returned normally" : "ended with " + Clip(y.End, 160))}");
                sb.Append(" (the callee keeps WORKING-STORAGE between CALLs: an earlier CALL differed)\n");
                return sb.ToString();
            }
        }
        if (n.Yours.Count > 0)
        {
            var last = n.Yours[^1];
            sb.Append($"- each of your CALLs got the same results as the faithful program's matching CALL. The last, CALL {n.Yours.Count(c => c.Program == last.Program)} to {last.Program}");
            sb.Append($"{(Selectors(last) is { Length: > 0 } s ? $" ({s})" : "")}, returned: {NestedChanges(last)}\n");
            sb.Append("  So your results differ because of your own code around these CALLs: what you compute yourself, or which result items you use.\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// A step's CALLs (kit feature subprogram-calls): the job ran twice, with the faithful step and with yours, every CALL to the
    /// programs it CALLs logged; shown for the first scenario whose CALLs part, the first of yours without a faithful counterpart,
    /// with other inputs or other results, or the faithful CALL yours never made (a step that ends early: FNLS0100 abended in
    /// every scenario and was told only "normalized ABEND 999").
    /// </summary>
    public string RenderStepCalls(IReadOnlyList<StepCallsScenario> scenarios)
    {
        if (scenarios.Count == 0) return "";
        var sb = new StringBuilder($"## The CALLs of your {programId} against the faithful program's\n");
        sb.Append("Each scenario below ran twice, with the faithful step and with yours; every CALL to the programs it CALLs was logged in both runs.\n");
        foreach (var s in scenarios)
            sb.Append($"- '{s.Name}': the faithful step made {s.Nested.FaithfulTotal} CALL(s) and ended with return code {s.FaithfulRc}; " +
                      $"yours made {s.Nested.YoursTotal} and ended with return code {s.YoursRc}{(s.YoursRc != s.FaithfulRc && s.YoursDetail is { Length: > 0 } d ? $" ({Clip(d, 160)})" : "")}.\n");
        // The scenario shown is the first whose CALLs part (FNLS0100: base matched throughout, while in mini-p03-ceiling the
        // faithful step rejected loan 929 on a callee's status and yours went on); otherwise the first.
        var first = scenarios.FirstOrDefault(s => s.Nested.YoursTotal == 0 && s.Nested.FaithfulTotal > 0)
                    ?? scenarios.FirstOrDefault(s => AlignedEnd(s.Nested) < 0)
                    ?? scenarios[0];
        if (first.Nested.YoursTotal == 0 && first.Nested.Faithful.Count > 0)
        {
            // No CALL at all: the step never reached its CALLs (it read no records, or ended before them). Name the faithful
            // step's first CALLs, and the first to a program without a verified contract, which the step has to CALL.
            sb.Append($"In '{first.Name}' your step made no CALL at all. The faithful step's first CALLs:\n");
            foreach (var (c, i) in first.Nested.Faithful.Take(3).Select((c, i) => (c, i)))
                sb.Append($"- CALL {i + 1} to {c.Program}{(Selectors(c) is { Length: > 0 } sel ? $" ({sel})" : "")}{(contracted.Contains(c.Program) ? " (a program with a verified contract: you may compute it yourself)" : "")}: {NestedArgs(c, c.Before)}\n");
            int k = first.Nested.Faithful.ToList().FindIndex(c => !contracted.Contains(c.Program));
            if (k >= 3)
            {
                var c = first.Nested.Faithful[k];
                sb.Append($"- the first to a program you must CALL is CALL {k + 1}, to {c.Program}{(Selectors(c) is { Length: > 0 } sel ? $" ({sel})" : "")}: {NestedArgs(c, c.Before)}\n");
            }
            sb.Append("Find why your step does not get to these CALLs: which records it reads, and the conditions before them.\n");
            return sb.ToString();
        }
        sb.Append(NestedDetail(first.Nested, $"In '{first.Name}' (the faithful step's CALLs against yours):\n"));
        // A step that stops early (an abend, an early return) shows no differing CALL of its own: name the faithful CALL it never made.
        // The next faithful CALL to a program yours CALLs too (one it computes itself, such as FNDTKRN, says nothing).
        var called = first.Nested.Yours.Select(c => c.Program).ToHashSet(StringComparer.Ordinal);
        if (AlignedEnd(first.Nested) is int aligned and >= 0
            && first.Nested.Faithful.Skip(aligned).Select((c, i) => (c, i)).FirstOrDefault(x => called.Contains(x.c.Program)) is { c: not null } hit)
        {
            int end = aligned + hit.i;
            var next = hit.c;
            sb.Append($"- after your last CALL the faithful step went on: its CALL {end + 1} was to {next.Program}" +
                      $"{(Selectors(next) is { Length: > 0 } sel ? $" ({sel})" : "")}, passing: {NestedArgs(next, next.Before)}\n");
            if (first.Nested.Yours.Count > 0)
            {
                var last = first.Nested.Yours[^1];
                sb.Append($"  Your last CALL, to {last.Program}, returned: {NestedChanges(last)}; then your step {(first.YoursRc != first.FaithfulRc ? $"ended with return code {first.YoursRc}" : "made no further CALL")}.\n");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Where the faithful CALLs stand once each of yours is paired (NestedDetail's pairing): the index after the last faithful
    /// CALL matched; -1 when one of yours has no counterpart or other inputs or results (NestedDetail reports that one).
    /// </summary>
    int AlignedEnd(ShadowNested n)
    {
        int fi = 0;
        foreach (var y in n.Yours)
        {
            var key = Key(y);
            int j = -1;
            for (int t = fi; t < n.Faithful.Count; t++)
                if (Key(n.Faithful[t]) == key) { j = t; break; }
            if (j < 0) return -1;
            var f = n.Faithful[j];
            if (ResultDiff(f, y).Count > 0 || f.End != y.End) return -1;
            fi = j + 1;
        }
        return fi;
    }

    /// <summary>The program and the short codes of a CALL's first argument (text items of at most 4 characters that are set).</summary>
    string Key(ShadowNestedCall c) => c.Program + "|" + FunctionCode(c);

    /// <summary>
    /// The CALL's function code: the first short code of its first argument (DF-OP, MY-FUNCTION, DT-FUNCTION, QY-FUNCTION), which
    /// the caller sets before every CALL. The other short codes can be left over from an earlier CALL (DF-MODE stays 'E' through
    /// FNEM0300's normalize CALLs), so pairing on all of them called matching CALLs unmatched.
    /// </summary>
    string FunctionCode(ShadowNestedCall c)
    {
        var views = NestedViews(c);
        if (views.Count == 0 || views[0] is not { } v || c.Before[0].Bytes is not { } b) return "";
        return v.Plain.Where(f => !f.Numeric && f.Length <= 4 && !KitValues.Initial(KitValues.Slice(b, f.Offset, f.Length)))
            .OrderBy(f => f.Offset).Select(f => $"{f.Name} = {KitValues.Show(f, b, f.Offset)}").FirstOrDefault() ?? "";
    }

    string Selectors(ShadowNestedCall c)
    {
        var views = NestedViews(c);
        if (views.Count == 0 || views[0] is not { } v || c.Before[0].Bytes is not { } b) return "";
        return string.Join(", ", v.Plain.Where(f => !f.Numeric && f.Length <= 4 && !KitValues.Initial(KitValues.Slice(b, f.Offset, f.Length)))
            .Select(f => $"{f.Name} = {KitValues.Show(f, b, f.Offset)}"));
    }

    /// <summary>The inputs that differ: per argument, the items the faithful CALL does not write, faithful against yours.</summary>
    List<string> InputDiff(ShadowNestedCall f, ShadowNestedCall y)
    {
        var views = NestedViews(f);
        var parts = new List<string>();
        for (int i = 0; i < Math.Min(f.Before.Count, y.Before.Count); i++)
        {
            if (f.Before[i].SameAs(y.Before[i])) continue;
            if (views[i] is { } v && f.Before[i].Bytes is { } fb && y.Before[i].Bytes is { } yb && f.After[i].Bytes is { } fa)
            {
                var written = v.Changes(fb, fa).Select(x => x.Offset).ToHashSet();
                var diffs = v.Changes(fb, yb).Where(x => !written.Contains(x.Offset)).ToList();
                if (diffs.Count > 0) parts.Add(string.Join(", ", diffs.Take(8).Select(d => $"{d.Name}: faithful {d.Before}, yours {d.After}")));
            }
            else parts.Add($"{NestedName(f, i)} differs{(f.Before[i].Bytes is null ? $" (a {f.Before[i].Length:N0}-byte record, compared whole)" : "")}");
        }
        return parts;
    }

    /// <summary>
    /// The results that differ: per argument, the items either CALL wrote (changed from before to after), faithful against
    /// yours; a large record (hash only) when either CALL changed it and the two differ after.
    /// </summary>
    List<string> ResultDiff(ShadowNestedCall f, ShadowNestedCall y)
    {
        var views = NestedViews(f);
        var parts = new List<string>();
        for (int i = 0; i < Math.Min(f.After.Count, y.After.Count); i++)
        {
            if (f.After[i].SameAs(y.After[i])) continue;
            bool changed = !f.Before[i].SameAs(f.After[i]) || (i < y.Before.Count && !y.Before[i].SameAs(y.After[i]));
            if (!changed) continue;
            if (views[i] is { } v && f.Before[i].Bytes is { } fb && f.After[i].Bytes is { } fa && y.Before[i].Bytes is { } yb && y.After[i].Bytes is { } ya)
            {
                var written = v.Changes(fb, fa).Select(x => x.Offset).Concat(v.Changes(yb, ya).Select(x => x.Offset)).ToHashSet();
                var diffs = v.Changes(fa, ya).Where(x => written.Contains(x.Offset)).ToList();
                if (diffs.Count > 0) parts.Add(string.Join(", ", diffs.Take(8).Select(d => $"{d.Name}: faithful {d.Before}, yours {d.After}")));
            }
            else parts.Add($"{NestedName(f, i)} differs{(f.After[i].Bytes is null ? $" (a {f.After[i].Length:N0}-byte record, compared whole)" : "")}");
        }
        return parts;
    }

    /// <summary>The results that differ, faithful against yours (a large record by its hash).</summary>
    List<string> Diff(ShadowNestedCall c, IReadOnlyList<ShadowArg> faithful, IReadOnlyList<ShadowArg> yours)
    {
        var views = NestedViews(c);
        var parts = new List<string>();
        for (int i = 0; i < Math.Min(faithful.Count, yours.Count); i++)
        {
            if (faithful[i].SameAs(yours[i])) continue;
            parts.Add(views[i] is { } v && faithful[i].Bytes is { } a && yours[i].Bytes is { } b
                ? string.Join(", ", v.Changes(a, b).Take(8).Select(d => $"{d.Name}: faithful {d.Before}, yours {d.After}"))
                : $"{NestedName(c, i)} differs");
        }
        return parts;
    }

    /// <summary>The layouts of a callee's arguments, by the record names of the CALL's USING list.</summary>
    List<RecordView?> NestedViews(ShadowNestedCall c)
    {
        var names = usingNames.GetValueOrDefault(c.Program) ?? [];
        return Enumerable.Range(0, c.Before.Count).Select(i =>
            i < names.Count && layouts?.All.FirstOrDefault(l => l.Name == names[i] && l.Length == c.Before[i].Length) is { } l ? new RecordView(l) : null).ToList();
    }

    string NestedName(ShadowNestedCall c, int i) => usingNames.GetValueOrDefault(c.Program) is { } n && i < n.Count ? n[i] : $"argument {i + 1}";

    string NestedArgs(ShadowNestedCall c, IReadOnlyList<ShadowArg> args)
    {
        var views = NestedViews(c);
        return string.Join("; ", Enumerable.Range(0, args.Count).Select(i =>
            $"{NestedName(c, i)}: {(args[i].Bytes is { } b ? Clip(PassedIn(views[i], b, false), 300) : $"({args[i].Length:N0} bytes)")}"));
    }

    /// <summary>What a CALL changed in its arguments: item, value before, value after.</summary>
    string NestedChanges(ShadowNestedCall c)
    {
        var views = NestedViews(c);
        var parts = new List<string>();
        for (int i = 0; i < Math.Min(c.Before.Count, c.After.Count); i++)
        {
            if (c.Before[i].SameAs(c.After[i])) continue;
            parts.Add(views[i] is { } v && c.Before[i].Bytes is { } b && c.After[i].Bytes is { } a
                ? string.Join(", ", v.Changes(b, a).Take(8).Select(d => $"{d.Name} {d.Before} → {d.After}"))
                : $"{NestedName(c, i)} changed");
        }
        return parts.Count == 0 ? "(no argument changed)" : Clip(string.Join("; ", parts), 600);
    }

    /// <summary>
    /// An argument as passed in: a small input record in full; otherwise only its selectors (88-level items, short codes),
    /// with a count of the other items that are set.
    /// </summary>
    string PassedIn(RecordView? v, byte[] b, bool output)
    {
        if (v is null) return "«" + Clip(KitValues.Escape(b), 300) + "»";
        var set = v.Plain.Where(f => !KitValues.Initial(KitValues.Slice(b, f.Offset, f.Length))).ToList();
        bool tables = v.Tables.Any(t => RecordView.Rows(t).Any(x => t.Columns.Any(col => !KitValues.Initial(KitValues.Slice(b, col.Offset + x.Delta, col.Length)))));
        if (set.Count == 0 && !tables) return "(blank)";
        if (!output && set.Count <= 30 && !tables) return string.Join(", ", set.Select(f => $"{f.Name} = {KitValues.Show(f, b, f.Offset)}"));
        var selectors = set.Where(f => conditions.ContainsKey(f.Name) || (f.Kind == "text" && f.Length <= 12) || (f.Kind == "binary" && f.Digits <= 4)).ToList();
        var text = string.Join(", ", selectors.Select(f => $"{f.Name} = {KitValues.Show(f, b, f.Offset)}"));
        int rest = set.Count - selectors.Count;
        var note = output ? "an argument the CALL writes" : "a larger input";
        return (text.Length == 0 ? "" : text + " ") + $"({note}{(rest > 0 ? $"; {rest} other item(s) set" : "")}{(tables ? "; tables in use" : "")})";
    }

    static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
