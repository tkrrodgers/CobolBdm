using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

/// <summary>
/// Whether a CALL of a J3S program's entry can return with a pending exception (JX-C not 0): the paragraphs the entry
/// reaches (PERFORM ranges, GO TO targets, fall-through) and the entries they call, searched for a write of JX-C other than
/// 0 (JX-RAISE, JX-NPE, a propagated exception). An abend (CEE3ABD) ends the step and raises nothing. Unknown shapes (a
/// CALL whose entry is not a literal, a program without an entry dispatch, a recursion in progress) count as raising, so
/// "cannot raise" is a proof over the code, never an observation.
/// </summary>
public static class RaiseAnalysis
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(Model, string), bool> Memo = new();

    /// <summary>True when the entry of the program provably returns with JX-C = 0 (or ends the step).</summary>
    public static bool CannotRaise(Model m, string program, int entry) => !MayRaise(m, program, entry, []);

    static bool MayRaise(Model m, string program, int entry, HashSet<string> active)
    {
        var key = program.ToUpperInvariant() + "#" + entry;
        if (Memo.TryGetValue((m, key), out var known)) return known;
        if (!active.Add(key)) return true;
        bool r = Compute(m, program, entry, active);
        active.Remove(key);
        Memo[(m, key)] = r;
        return r;
    }

    static bool Compute(Model m, string program, int entry, HashSet<string> active)
    {
        var p = m.Estate.Programs.FirstOrDefault(x => x.Id.Equals(program, StringComparison.OrdinalIgnoreCase));
        if (p is null) return true;
        var paras = p.Model.Paragraphs;
        // the dispatch: GO TO E0001 E0002 ... DEPENDING ON FA-ENTRY (or RT-FN) in the first paragraph
        var go = paras.Count == 0 ? null : paras[0].Statements.FirstOrDefault(s => s.Verb == "GO TO" && s.Get("depending") is not null);
        if (go?.L?.GetValueOrDefault("targets") is not { } targets || entry < 1 || entry > targets.Count) return true;
        if (!p.ParagraphIndex.TryGetValue(targets[entry - 1].V!, out var start)) return true;
        // (paragraph, the last paragraph of the PERFORM range it runs in, or -1 for the entry's flow up to GOBACK)
        var seen = new HashSet<(int, int)>();
        var work = new Stack<(int, int)>();
        work.Push((start, -1));
        while (work.Count > 0)
        {
            var (i, limit) = work.Pop();
            if (i < 0 || i >= paras.Count || !seen.Add((i, limit))) continue;
            string? faEntry = null, rtFn = null;
            foreach (var s in Model.Deep(paras[i].Statements))
            {
                switch (s.Verb)
                {
                    case "MOVE":
                    {
                        var dst = Model.DstName(s);
                        var src = s.Get("src");
                        if (dst is null) break;
                        if (dst.Equals("JX-C", StringComparison.OrdinalIgnoreCase) && src is not { K: "num", V: "0" }) return true;
                        if (dst.Equals("FA-ENTRY", StringComparison.OrdinalIgnoreCase)) faEntry = src is { K: "num" } ? src.V : null;
                        if (dst.Equals("RT-FN", StringComparison.OrdinalIgnoreCase)) rtFn = src is { K: "num" } ? src.V : null;
                        break;
                    }
                    case "COMPUTE": case "ADD": case "SUBTRACT": case "MULTIPLY": case "DIVIDE": case "INITIALIZE": case "SET":
                        if ((s.L?.Values.SelectMany(x => x) ?? []).Any(n => n.V?.Equals("JX-C", StringComparison.OrdinalIgnoreCase) == true)) return true;
                        break;
                    case "PERFORM":
                    {
                        if (s.Str("target") is not { } t) break;
                        if (!p.ParagraphIndex.TryGetValue(t, out var from)) return true;
                        int to = s.Str("thru") is { } thru && p.ParagraphIndex.TryGetValue(thru, out var th) ? th : from;
                        work.Push((from, to));
                        break;
                    }
                    case "GO TO":
                        foreach (var g in s.L?.GetValueOrDefault("targets") ?? [])
                        {
                            if (!p.ParagraphIndex.TryGetValue(g.V!, out var gi)) return true;
                            work.Push((gi, limit));
                        }
                        break;
                    case "CALL":
                    {
                        var target = s.Get("target") is { K: "str", V: { } tv } ? tv : null;
                        if (target is null) return true;
                        if (target.Equals("CEE3ABD", StringComparison.OrdinalIgnoreCase)) break;
                        // the callee's entry: the literal its dispatch reads (FA-ENTRY for classes, RT-FN for runtime programs)
                        var callee = m.Estate.Programs.FirstOrDefault(x => x.Id.Equals(target, StringComparison.OrdinalIgnoreCase));
                        if (callee is null) return true;
                        var cgo = callee.Model.Paragraphs.Count == 0 ? null : callee.Model.Paragraphs[0].Statements.FirstOrDefault(x => x.Verb == "GO TO" && x.Get("depending") is not null);
                        var dvar = cgo?.Get("depending")?.V;
                        var lit = dvar?.Equals("RT-FN", StringComparison.OrdinalIgnoreCase) == true ? rtFn : dvar?.Equals("FA-ENTRY", StringComparison.OrdinalIgnoreCase) == true ? faEntry : null;
                        if (lit is null || !int.TryParse(lit, out var ce) || MayRaise(m, callee.Id, ce, active)) return true;
                        break;
                    }
                }
            }
            // fall-through into the next paragraph unless the paragraph ends the flow
            var last = paras[i].Statements.LastOrDefault();
            if ((last is null || !(last.Verb is "GOBACK" or "STOP" or "EXIT PROGRAM" || last.Verb == "GO TO" && last.Get("depending") is null)) && i != limit) work.Push((i + 1, limit));
        }
        return false;
    }

    /// <summary>
    /// The job adapter's failures after a call or a decimal operation (IF JX-C NOT = 0 / IF JB-RC NOT = 0 ... JX-JOBFAIL,
    /// directly or through a GO TO of a paragraph that performs it), over every program the lift reads: each is either
    /// proved unreachable (the call before it cannot raise) or behavior the modern code does not preserve (the legacy run
    /// abends 38; Java lets the exception propagate, with another return code and SYSOUT). Startup marks those of the
    /// once-per-run initialization (the job's -INIT paragraph, what it performs, the programs it calls).
    /// </summary>
    public static List<(string Where, string Status, bool Startup)> JobFailLedger(Model m)
    {
        var res = new List<(string, string, bool)>();
        var startupPrograms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var startupParas = new HashSet<(string, int)>();
        foreach (var p in m.Estate.Programs.Where(x => m.JobPrograms.Contains(x.Id)))
            foreach (var init in p.Model.Paragraphs.Where(x => x.Name.EndsWith("-INIT", StringComparison.OrdinalIgnoreCase) && x.Name.StartsWith("0")))
            {
                startupParas.Add((p.Id, p.ParagraphIndex[init.Name]));
                foreach (var s in Model.Deep(init.Statements))
                {
                    if (s.Verb == "CALL" && s.Get("target") is { K: "str", V: { } ct }) startupPrograms.Add(ct);
                    if (s.Verb == "PERFORM" && s.Str("target") is { } t && p.ParagraphIndex.TryGetValue(t, out var f))
                    {
                        int to = s.Str("thru") is { } th && p.ParagraphIndex.TryGetValue(th, out var ti) ? ti : f;
                        for (int k = f; k <= to; k++) startupParas.Add((p.Id, k));
                    }
                }
            }
        foreach (var p in m.Estate.Programs.Where(x => !m.RuntimePrograms.Contains(x.Id) && !m.DriverPrograms.Contains(x.Id)))
        {
            var paras = p.Model.Paragraphs;
            bool FailPara(string name) => p.ParagraphIndex.TryGetValue(name, out var gi)
                && Model.Deep(paras[gi].Statements).Any(b => b.Verb == "PERFORM" && b.Str("target")?.Equals("JX-JOBFAIL", StringComparison.OrdinalIgnoreCase) == true);
            for (int i = 0; i < paras.Count; i++)
            {
                (string Prog, int Entry)? lastCall = null;
                string? faEntry = null;
                foreach (var s in Model.Deep(paras[i].Statements))
                {
                    if (s.Verb == "MOVE" && Model.DstName(s)?.Equals("FA-ENTRY", StringComparison.OrdinalIgnoreCase) == true) faEntry = s.Get("src") is { K: "num" } fe ? fe.V : null;
                    if (s.Verb == "CALL" && s.Get("target") is { K: "str", V: { } ct })
                        lastCall = !ct.Equals("FSJBDEC", StringComparison.OrdinalIgnoreCase) && int.TryParse(faEntry, out var e) ? (ct, e) : null;
                    if (s.Verb != "IF" || s.Get("cond") is not { K: "rel", L: { K: "ref", V: { } cv } }) continue;
                    var body = s.Blocks?.GetValueOrDefault("TRUE") ?? [];
                    bool fails = Model.Deep(body).Any(b => b.Verb == "PERFORM" && b.Str("target")?.Equals("JX-JOBFAIL", StringComparison.OrdinalIgnoreCase) == true
                        || b.Verb == "GO TO" && (b.L?.GetValueOrDefault("targets") ?? []).Any(g => FailPara(g.V!)));
                    if (!fails) continue;
                    var where = $"{p.Id} {paras[i].Name} line {s.Line}";
                    bool startup = startupPrograms.Contains(p.Id) || startupParas.Contains((p.Id, i));
                    if (cv.Equals("JX-C", StringComparison.OrdinalIgnoreCase))
                    {
                        if (lastCall is { } lc && CannotRaise(m, lc.Prog, lc.Entry))
                            res.Add((where, $"proved unreachable: {lc.Prog} entry {lc.Entry} cannot raise", startup));
                        else
                            res.Add((where, $"UNPRESERVED: the legacy run abends 38 when {(lastCall is { } l2 ? $"{l2.Prog} entry {l2.Entry}" : "the call")} raises; Java lets the exception propagate (another return code and SYSOUT)", startup));
                    }
                    else if (cv.Equals("JB-RC", StringComparison.OrdinalIgnoreCase))
                        res.Add((where, "UNPRESERVED: the legacy run abends 38 when the decimal conversion fails (RC 1 inexact, RC 20 capacity); Java's ArithmeticException propagates, RC 20 is not checked", startup));
                }
            }
        }
        return res;
    }
}
