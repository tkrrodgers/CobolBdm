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
        paragraphs.Clear();
        OwnFrames = [];
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
            {
                sb.Append($"- {r.Id} in {r.Paragraph}{(r.When.Count > 0 ? $", when {string.Join(" AND ", r.When.Select(w => Clip(w, 100)))}" : "")}: " +
                          $"{Clip(r.Cobol, 160)} (writes {string.Join(", ", r.Writes.Where(bases.Contains))})\n");
                AddParagraph(r.Paragraph);
            }
        }
        if (c.Nested is { } nested)
        {
            sb.Append(NestedDetail(nested));
            sb.Append(Walk(nested, c));
        }
        return sb.ToString();
    }

    const int WalkBudget = 9000;

    /// <summary>Where NestedDetail found the CALLs to part: the faithful CALL's index and yours (-1 when they did not).</summary>
    (int Faithful, ShadowNestedCall? Yours) parted = (-1, null);

    /// <summary>
    /// The faithful program's way to its results, when yours computes itself what a callee did (Muse Glimmer's FNEM0300 makes no
    /// CALL, so the CALL pairing has nothing to compare and its feedback only said so): the faithful CALLs of this CALL in order,
    /// grouped by the PERFORM chain they were made from, each with the statement that made it, what it was passed and what it
    /// returned. That is the call graph flattened by the run, with every intermediate value and every precision as used, so a
    /// model can find the first step where its own arithmetic parts. A walk is cut into passes where it comes back to the
    /// statement it began from (a loop of the paragraph that writes the items: 1100-RATE-FACTOR-FOR-PERIOD once per period);
    /// when the items written by the CALL map one to one onto the passes, the pass that writes the first differing item is
    /// named and shown first, and the others follow while the budget lasts.
    /// </summary>
    string Walk(ShadowNested n, ShadowCall? call = null)
    {
        var computed = n.Faithful.Select(c => c.Program).Distinct().Where(p => n.Yours.All(y => y.Program != p)).ToList();
        // A model that makes the CALLs itself gets the walk of the pass where its CALLs part from the faithful ones, with that
        // CALL marked (Gemma's FNEM0300 was told for many attempts which date CALL got other inputs, never how the faithful
        // program came to its inputs).
        var (mark, markYours) = parted;
        bool partial = computed.Count == 0;
        if (n.Faithful.Count == 0 || Statements is null || (partial && mark < 0)) return "";
        var sb = new StringBuilder(partial
            ? $"How the faithful {programId} came to that CALL (its CALLs in the pass where yours part from them, each with what it was passed " +
              "and what it returned; values as COBOL holds them, a -COEF/-EXP pair as the number it stands for). Compare them with your CALLs:\n"
            : $"How the faithful {programId} got its results (its CALLs in order, each a step you compute yourself; values as " +
              "COBOL holds them, a -COEF/-EXP pair as the number it stands for). Compare each step with your code; the first " +
              "that differs is where yours parts:\n");
        // The items each kind of CALL (program and function code) writes, over all of them: its results, shown whole even where
        // a CALL left one as it was (a divide whose quotient has the coefficient the item already held).
        var outputs = new Dictionary<string, HashSet<(int Arg, int Offset)>>(StringComparer.Ordinal);
        foreach (var f in n.Faithful)
        {
            var views = NestedViews(f);
            if (!outputs.TryGetValue(Key(f), out var set)) outputs[Key(f)] = set = [];
            for (int i = 0; i < Math.Min(f.Before.Count, f.After.Count); i++)
                if (views[i] is { } v && f.Before[i].Bytes is { } b && f.After[i].Bytes is { } a)
                    foreach (var x in v.Changes(b, a))
                    {
                        set.Add((i, x.Offset));
                        // A -COEF/-EXP pair is one number: when a CALL changed only one half (12 / 100 left DF-R-COEF at 12
                        // and moved DF-R-EXP), the other is a result too.
                        var name = x.Field.Name;
                        var partner = name.EndsWith("-COEF", StringComparison.Ordinal) ? name[..^5] + "-EXP"
                            : name.EndsWith("-EXP", StringComparison.Ordinal) ? name[..^4] + "-COEF" : null;
                        if (partner is not null && v.Plain.FirstOrDefault(f => f.Name == partner) is { } p) set.Add((i, p.Offset));
                    }
        }
        // Each step: the PERFORM chain it was made from (outermost first; its own statements only, the frames of the programs that
        // CALLed it are the job around it), the statement that made it, and the line.
        var steps = n.Faithful.Select(f =>
        {
            var chain = Statements(f.Frames.Where(fr => fr.Class.Split('$')[0].EndsWith(".Prog_" + programId, StringComparison.Ordinal)));
            var from = chain.Skip(1).Reverse().Select(c => Short(c.Tag)).ToList();
            var here = chain.Count > 0 ? Short(chain[0].Tag) : "";
            var kind = $"{here}: {f.Program} {Selectors(f)}";
            return (From: from, Kind: kind, Line: $"  {kind}: {StepInputs(f, outputs[Key(f)])} → {StepResults(f, outputs[Key(f)])}\n");
        }).ToList();
        if (mark >= 0 && mark < steps.Count && markYours is not null)
        {
            var f = n.Faithful[mark];
            var differs = InputDiff(f, markYours).Concat(ResultDiff(f, markYours)).ToList();
            steps[mark] = steps[mark] with { Line = steps[mark].Line.TrimEnd('\n') + $"\n    ◀ yours parts here: {Clip(string.Join("; ", differs), 400)}\n" };
        }
        // The chain every step shares is said once.
        int common = steps.Count == 0 ? 0 : steps.Min(s => s.From.Count);
        for (int i = 0; i < common; i++)
            if (steps.Any(s => s.From[i] != steps[0].From[i])) { common = i; break; }
        if (common > 0) sb.Append($"All of them performed from {string.Join(" → ", steps[0].From.Take(common))}.\n");
        // A range of steps as text: a run of the same statement making the same kind of CALL from the same place (a loop:
        // 4200-RATIO-STEP adds a day 30 times) shows its first two and its last CALL.
        string Render(int start, int stop)
        {
            var text = new StringBuilder();
            string? path = null;
            for (int i = start; i < stop;)
            {
                int end = i + 1;
                while (end < stop && steps[end].Kind == steps[i].Kind && steps[end].From.SequenceEqual(steps[i].From)) end++;
                var from = string.Join(" → ", steps[i].From.Skip(common));
                if (from != path) text.Append($"- {(from.Length == 0 ? "directly" : "performed from " + from)}:\n");
                if (end - i > 4 && mark > i + 1 && mark < end - 1)
                {
                    // The marked CALL is never folded away.
                    text.Append(steps[i].Line).Append(steps[i + 1].Line);
                    if (mark - i - 2 > 0) text.Append($"  … {mark - i - 2} more like these …\n");
                    text.Append(steps[mark].Line);
                    if (end - mark - 2 > 0) text.Append($"  … {end - mark - 2} more like these …\n");
                    text.Append(steps[end - 1].Line);
                }
                else if (end - i > 4)
                    text.Append(steps[i].Line).Append(steps[i + 1].Line).Append($"  … {end - i - 3} more like these …\n").Append(steps[end - 1].Line);
                else
                    for (int k = i; k < end; k++) text.Append(steps[k].Line);
                path = from;
                i = end;
            }
            return text.ToString();
        }
        // Passes: the walk comes back to the statement it began from.
        var starts = new List<int> { 0 };
        string? First(int i) => steps[i].From.Skip(common).FirstOrDefault();
        for (int i = 1; i < steps.Count; i++)
            if (First(i) == First(0) && First(i - 1) != First(0)) starts.Add(i);
        var tail = n.FaithfulTotal > n.Faithful.Count ? $"  (… {n.FaithfulTotal - n.Faithful.Count} more CALL(s) past the log)\n" : "";
        if (starts.Count == 1)
        {
            var all = Render(0, steps.Count);
            if (sb.Length + all.Length > WalkBudget) all = all[..Math.Max(0, WalkBudget - sb.Length)] + "\n  (… the rest left out for room)\n";
            return sb.Append(all).Append(tail).ToString();
        }
        var (target, item) = call is null ? (-1, "") : Pass(call, starts.Count);
        int Stop(int p) => p + 1 < starts.Count ? starts[p + 1] : steps.Count;
        if (partial)
        {
            // Only the pass that holds the marked CALL.
            int p = Enumerable.Range(0, starts.Count).Last(q => starts[q] <= mark);
            var part = Render(starts[p], Stop(p));
            if (sb.Length + part.Length > WalkBudget) part = part[..Math.Max(0, WalkBudget - sb.Length)] + "\n  (… the rest of the pass left out for room)\n";
            sb.Append($"Pass {p + 1} of {starts.Count} (each begins at {First(0)}):\n").Append(part);
            // Yours around the CALL that parts, with the line of your code that makes each (the pairing follows program and
            // function code, so an extra or missing CALL of yours shows here).
            int at = markYours is null ? -1 : n.Yours.ToList().IndexOf(markYours);
            if (at >= 0)
            {
                foreach (var y in n.Yours)
                {
                    var views = NestedViews(y);
                    if (!outputs.TryGetValue(Key(y), out var set)) outputs[Key(y)] = set = [];
                    for (int i = 0; i < Math.Min(y.Before.Count, y.After.Count); i++)
                        if (views[i] is { } v && y.Before[i].Bytes is { } b && y.After[i].Bytes is { } a)
                            foreach (var x in v.Changes(b, a)) set.Add((i, x.Offset));
                }
                int from = Math.Max(0, at - 5), to = Math.Min(n.Yours.Count, at + 3);
                sb.Append($"Your CALLs around your CALL that parts (yours {from + 1} to {to} of {n.Yours.Count}, in order):\n");
                for (int i = from; i < to; i++)
                {
                    var y = n.Yours[i];
                    int nth = n.Yours.Take(i + 1).Count(c => c.Program == y.Program);
                    sb.Append($"  {(i == at ? "◀ " : "")}your CALL {nth} to {y.Program} {Selectors(y)}: {StepInputs(y, outputs[Key(y)])} → {StepResults(y, outputs[Key(y)])}" +
                              $"{(y.Own is { Count: > 0 } own ? $" (made at {own[0]})" : "")}\n");
                }
            }
            return sb.ToString();
        }
        var parts = Enumerable.Range(0, starts.Count).Select(p => Render(starts[p], Stop(p))).ToList();
        sb.Append($"It makes {starts.Count} passes{(First(0) is { } f0 ? $" (each begins at {f0})" : "")}" +
                  (target >= 0 ? $"; pass {target + 1} writes {item}, the first item that differs: it comes first.\n" : ".\n"));
        var order = target >= 0 ? new[] { target }.Concat(Enumerable.Range(0, starts.Count).Where(p => p != target)) : Enumerable.Range(0, starts.Count);
        foreach (var p in order)
        {
            var head = $"Pass {p + 1} of {starts.Count}{(p == target ? $" (writes {item})" : "")}:\n";
            if (sb.Length + head.Length + parts[p].Length > WalkBudget)
            {
                sb.Append($"Pass {p + 1} of {starts.Count}: {Stop(p) - starts[p]} CALL(s), left out for room.\n");
                continue;
            }
            sb.Append(head).Append(parts[p]);
        }
        return sb.Append(tail).ToString();
    }

    /// <summary>
    /// The pass that writes the first differing item: the items of its field the faithful CALL wrote (changed, or differing from
    /// yours) in subscript order, when there are as many as passes; (-1, "") otherwise.
    /// </summary>
    (int Pass, string Item) Pass(ShadowCall c, int passes)
    {
        var views = Views(c);
        for (int i = 0; i < Math.Min(c.Faithful.Count, c.Yours.Count); i++)
        {
            if (views[i] is not { } v || c.Faithful[i].AsSpan().SequenceEqual(c.Yours[i])) continue;
            var diffs = v.Changes(c.Faithful[i], c.Yours[i]);
            if (diffs.Count == 0) continue;
            var field = diffs[0].Field.Name;
            var items = v.Changes(c.Before[i], c.Faithful[i]).Concat(diffs).Where(x => x.Field.Name == field).Select(x => x.Name).Distinct()
                .OrderBy(x => x, Comparer<string>.Create(CompareSubscripts)).ToList();
            return items.Count == passes ? (items.IndexOf(diffs[0].Name), diffs[0].Name) : (-1, "");
        }
        return (-1, "");
    }

    /// <summary>Orders "NAME(2,1)" before "NAME(10,1)": by the subscripts as numbers.</summary>
    static int CompareSubscripts(string a, string b)
    {
        static int[] Of(string s) => s.IndexOf('(') is var o and >= 0
            ? s[(o + 1)..].TrimEnd(')').Split(',').Select(x => int.TryParse(x.Trim(), out var k) ? k : 0).ToArray() : [];
        var x = Of(a);
        var y = Of(b);
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
            if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        return x.Length.CompareTo(y.Length);
    }

    /// <summary>"8500-CALL-DATE:1:CALL:L599" as "8500-CALL-DATE L599".</summary>
    static string Short(string tag) => tag.Split(':') is { Length: >= 4 } p ? $"{string.Join(":", p[..^3])} {p[^1]}" : tag;

    /// <summary>What a CALL was passed: the items set that are neither its results nor its selectors.</summary>
    string StepInputs(ShadowNestedCall c, HashSet<(int Arg, int Offset)> outputs)
    {
        var views = NestedViews(c);
        var items = new List<(string Name, string Value)>();
        for (int i = 0; i < c.Before.Count; i++)
        {
            if (views[i] is not { } v || c.Before[i].Bytes is not { } b) continue;
            int arg = i;
            items.AddRange(v.Plain.Where(f => !outputs.Contains((arg, f.Offset)) && !(!f.Numeric && f.Length <= 4) && !KitValues.Initial(KitValues.Slice(b, f.Offset, f.Length)))
                .Select(f => (f.Name, KitValues.Show(f, b, f.Offset))));
        }
        return items.Count == 0 ? "(nothing else set)" : Clip(string.Join(", ", Numbers(items).Select(x => $"{x.Name} {x.Value}")), 300);
    }

    /// <summary>What a CALL returned: its result items (the ones its kind of CALL writes), with their values after it.</summary>
    string StepResults(ShadowNestedCall c, HashSet<(int Arg, int Offset)> outputs)
    {
        var views = NestedViews(c);
        var items = new List<(string Name, string Value)>();
        for (int i = 0; i < Math.Min(c.Before.Count, c.After.Count); i++)
        {
            int arg = i;
            if (views[i] is { } v && c.After[i].Bytes is { } a)
                items.AddRange(v.Plain.Where(f => outputs.Contains((arg, f.Offset))).Select(f => (f.Name, KitValues.Show(f, a, f.Offset))));
            else if (!c.Before[i].SameAs(c.After[i])) items.Add((NestedName(c, i), "changed"));
        }
        var end = c.End is null ? "" : $" (ended with {Clip(c.End, 120)})";
        return (items.Count == 0 ? "(nothing changed)" : Clip(string.Join(", ", Numbers(items).Select(x => $"{x.Name} {x.Value}")), 300)) + end;
    }

    /// <summary>A NAME-COEF and NAME-EXP pair (FNDKDFO's decimal floats) as the one number NAME = COEF × 10^EXP.</summary>
    static List<(string Name, string Value)> Numbers(List<(string Name, string Value)> items)
    {
        // An item left out as zero (an exponent of 0, a coefficient of 0) counts as 0.
        var byName = items.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        var result = new List<(string Name, string Value)>();
        foreach (var (name, value) in items)
        {
            if (name.EndsWith("-EXP", StringComparison.Ordinal))
            {
                if (byName.ContainsKey(name[..^4] + "-COEF")) continue;
                if (int.TryParse(value.TrimStart('+'), out _)) { result.Add((name[..^4], "0")); continue; }
            }
            if (name.EndsWith("-COEF", StringComparison.Ordinal) && System.Numerics.BigInteger.TryParse(value.TrimStart('+'), out var coef))
            {
                var exp = byName.GetValueOrDefault(name[..^5] + "-EXP") ?? "0";
                if (int.TryParse(exp.TrimStart('+'), out var e)) { result.Add((name[..^5], Decimal(coef, e))); continue; }
            }
            result.Add((name, value));
        }
        return result;
    }

    static string Decimal(System.Numerics.BigInteger coef, int exp)
    {
        if (exp >= 0) return (coef * System.Numerics.BigInteger.Pow(10, exp)).ToString();
        var neg = coef.Sign < 0;
        var digits = System.Numerics.BigInteger.Abs(coef).ToString().PadLeft(-exp + 1, '0');
        var s = digits[..^(-exp)] + "." + digits[^(-exp)..];
        return (neg ? "-" : "") + s;
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
        parted = (-1, null);
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
                sb.Append(Where(null, y));
                return sb.ToString();
            }
            var f = n.Faithful[j];
            fi = j + 1;
            // Inputs that differ only in form (30 passed as 3E1 to FNDKDFO) or in items the callee does not use (DT-N to an 'LY'
            // CALL of FNDTKRN) do not matter while the results are the same: such a CALL is matched, and the comparison goes on.
            var inputs = InputDiff(f, y);
            var outputs = ResultDiff(f, y);
            if (outputs.Count == 0 && f.End == y.End) continue;
            parted = (j, y);
            if (inputs.Count > 0)
            {
                sb.Append($"- {label} is the first that gets other results than the faithful program's matching CALL: {Clip(string.Join("; ", outputs), 500)}\n");
                sb.Append($"  It passed other inputs: {Clip(string.Join("; ", inputs), 600)}\n");
                sb.Append($"  The faithful CALL returned: {NestedChanges(f)}\n");
                sb.Append(Where(f, y));
                return sb.ToString();
            }
            {
                sb.Append($"- {label} passed the same inputs as the faithful program's matching CALL but got other results: {Clip(string.Join("; ", outputs), 600)}");
                if (f.End != y.End) sb.Append($"; the faithful CALL {(f.End is null ? "returned normally" : "ended with " + Clip(f.End, 160))}, yours {(y.End is null ? "returned normally" : "ended with " + Clip(y.End, 160))}");
                sb.Append(" (the callee keeps WORKING-STORAGE between CALLs: an earlier CALL differed)\n");
                sb.Append(Where(f, y));
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
        paragraphs.Clear();
        OwnFrames = [];
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

    /// <summary>Turns faithful frames into COBOL statements ("step-id statement"); set by the caller (FeedbackRenderer.StatementsAt).</summary>
    public Func<IEnumerable<ProbeFrame>, List<(string Tag, string Text)>>? Statements { get; set; }

    /// <summary>The COBOL paragraphs the last rendered feedback points at (where the divergence is made, the rules that write the items).</summary>
    public IReadOnlyList<string> Paragraphs => paragraphs;
    readonly List<string> paragraphs = [];

    /// <summary>The model's own frames at the last divergence rendered (Class.method(File.java:n)).</summary>
    public IReadOnlyList<string> OwnFrames { get; private set; } = [];

    /// <summary>
    /// Where the two runs made the CALL that parts: the faithful program's COBOL statement (and the statements that performed
    /// it), and the line of yours. The debug prompt takes these paragraphs from the outline.
    /// </summary>
    string Where(ShadowNestedCall? f, ShadowNestedCall? y)
    {
        var sb = new StringBuilder();
        if (f is { At.Count: > 0 } && Statements is not null && Statements(f.Frames) is { Count: > 0 } chain)
        {
            sb.Append($"  The faithful program makes this CALL at {chain[0].Tag} {chain[0].Text}");
            foreach (var c in chain.Skip(1).Take(3)) sb.Append($", performed from {c.Tag}");
            sb.Append(".\n");
            foreach (var c in chain.Take(4)) AddParagraph(c.Tag.Split(':') is { Length: >= 4 } p ? string.Join(":", p[..^3]) : c.Tag);
        }
        if (y?.Own is { Count: > 0 } own)
        {
            sb.Append($"  Yours makes it at {own[0]}{(own.Count > 1 ? ", called from " + string.Join(", ", own.Skip(1).Take(2)) : "")}.\n");
            OwnFrames = own;
        }
        return sb.ToString();
    }

    void AddParagraph(string p)
    {
        if (p.Length > 0 && !paragraphs.Contains(p)) paragraphs.Add(p);
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
