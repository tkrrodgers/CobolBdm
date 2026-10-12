using System.Collections;

namespace MlCpu.Cobol.Analysis;

public sealed record ParagraphEdge(string From, string To, string Kind);
public sealed record CtrlDepEdge(string PredicateStepId, string StepId, string Branch);
public sealed record DataDepEdge(string FromStepId, string ToStepId, string Field);
public sealed record Frontier(string Kind, string Subject, string Detail);
public sealed record CallSite(string StepId, string Paragraph, string Target, string Kind, IReadOnlyList<string> ResolvedTargets);

/// <summary>Program dependence graph (§5.4.4): CFG, paragraph call graph, control and data dependence.</summary>
public sealed class ProgramGraph
{
    public string ProgramId { get; init; } = "";
    public List<ParagraphEdge> ParagraphEdges { get; } = [];
    public List<CtrlDepEdge> CtrlDeps { get; } = [];
    public List<DataDepEdge> DataDeps { get; } = [];
    public List<string> DeadParagraphs { get; } = [];
    public List<CallSite> Calls { get; } = [];
    public List<Frontier> Frontiers { get; } = [];
    /// <summary>Statement CFG: stepId → successor stepIds (paragraph entry/exit pseudo nodes are elided).</summary>
    public Dictionary<string, List<string>> Cfg { get; } = [];
    public int DataflowIterations { get; set; }
}

/// <summary>
/// Builds the PDG over the semantic tree. Storage is modelled as byte ranges per area, so a definition of a group,
/// a REDEFINES overlay or an FD record reaches every overlapping use. Known approximation (as in the spec): PERFORM
/// returns are context-insensitive and a paragraph end both returns and falls through.
/// </summary>
public static class ProgramGraphBuilder
{
    const int MaxIterations = 50;

    sealed class CfgNode
    {
        public int Id;
        public Stmt? Stmt;
        public string Label = "";
        public readonly List<int> Succ = [];
        public readonly List<Access> Defs = [];
        public readonly List<Access> Uses = [];
    }

    /// <summary>A byte-range access. Exact definitions kill what they cover; partial (subscripted, ref-modified, INITIALIZE) do not.</summary>
    readonly record struct Access(string Area, int Start, int End, string Field, bool Exact);

    sealed class Ctx
    {
        public int SentenceFollow;
        public int ParaExit;
        public int LoopHeader = -1;
        public int LoopFollow = -1;
    }

    public static ProgramGraph Build(CobolProgram p) => Build(p, dataDeps: true);

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CobolProgram, IReadOnlyList<CallSite>> CallCache = new();

    /// <summary>A program's CALL sites (dynamic ones resolved), computed once per model: CALL closures ask for every program again and again.</summary>
    public static IReadOnlyList<CallSite> Calls(CobolProgram p) => CallCache.GetValue(p, m => Build(m, dataDeps: false).Calls);

    /// <summary>
    /// The graph without the def→use edges when <paramref name="dataDeps"/> is false: reaching definitions then run only
    /// when a dynamic CALL needs them to resolve its targets. Paragraph edges, CALLs, control dependences, dead paragraphs
    /// and the CFG are the same. The data dependences of a large program grow with its shared storage: a J3S program whose
    /// every CALL passes the heap and context records BY REFERENCE has 5.2 million, which only the DDG needs.
    /// </summary>
    public static ProgramGraph Build(CobolProgram p, bool dataDeps)
    {
        var g = new ProgramGraph { ProgramId = p.ProgramId };
        var nodes = new List<CfgNode>();
        int New(string label, Stmt? s = null) { var n = new CfgNode { Id = nodes.Count, Label = label, Stmt = s }; nodes.Add(n); return n.Id; }

        var items = p.Items.ToDictionary(i => i.Id);
        var files = p.Files.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var paraIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < p.Paragraphs.Count; i++) paraIndex.TryAdd(p.Paragraphs[i].Name, i);

        int progEntry = New("<entry>"), progExit = New("<exit>");
        var paraEntry = p.Paragraphs.Select(x => New("<para " + x.Name + ">")).ToArray();
        var paraExit = p.Paragraphs.Select(x => New("<end " + x.Name + ">")).ToArray();
        var returnSites = new List<(int RangeEnd, int Site)>();
        var performs = new Dictionary<int, (int Lo, int Hi, int After)>();
        var alters = new List<(string From, string To)>();
        // The program starts after DECLARATIVES; a declarative section runs only on an I/O exception (modelled as
        // reachable from the entry) and ends the program flow if fallen out of.
        int startPara = p.Paragraphs.FindIndex(x => !x.Declarative);
        nodes[progEntry].Succ.Add(startPara >= 0 ? paraEntry[startPara] : progExit);
        for (int di = 0; di < p.Paragraphs.Count; di++)
            if (p.Paragraphs[di].Declarative && p.Paragraphs[di].IsSection) nodes[progEntry].Succ.Add(paraEntry[di]);

        int RangeEnd(string name)
        {
            if (!paraIndex.TryGetValue(name, out var i)) return -1;
            if (!p.Paragraphs[i].IsSection) return i;
            while (i + 1 < p.Paragraphs.Count && string.Equals(p.Paragraphs[i + 1].Section, name, StringComparison.OrdinalIgnoreCase) && !p.Paragraphs[i + 1].IsSection) i++;
            return i;
        }

        int BuildBlock(List<Stmt>? block, int follow, Ctx ctx, string para)
        {
            if (block is null || block.Count == 0) return follow;
            int next = follow;
            for (int i = block.Count - 1; i >= 0; i--) next = BuildStmt(block[i], next, ctx, para);
            return next;
        }

        int BuildStmt(Stmt s, int next, Ctx ctx, string para)
        {
            int n = New(s.StepId, s);
            var succ = nodes[n].Succ;
            AccessesOf(s, nodes[n], items, files);
            switch (s.Verb)
            {
                case "IF":
                    succ.Add(BuildBlock(s.Blocks?.GetValueOrDefault("TRUE"), next, ctx, para));
                    succ.Add(BuildBlock(s.Blocks?.GetValueOrDefault("FALSE"), next, ctx, para));
                    break;
                case "EVALUATE":
                {
                    int chain = next;
                    var whens = s.Blocks?.GetValueOrDefault("WHEN") ?? [];
                    for (int i = whens.Count - 1; i >= 0; i--)
                    {
                        var w = whens[i];
                        int wn = New(w.StepId, w);
                        AccessesOf(w, nodes[wn], items, files);
                        nodes[wn].Succ.Add(BuildBlock(w.Blocks?.GetValueOrDefault("TRUE"), next, ctx, para));
                        if (w.Str("other") != "true") nodes[wn].Succ.Add(chain);
                        g.CtrlDeps.Add(new CtrlDepEdge(s.StepId, w.StepId, "EVAL"));
                        chain = wn;
                    }
                    succ.Add(chain);
                    break;
                }
                case "SEARCH" or "SEARCH ALL":
                {
                    // A loop over the table: AT END leaves it; each WHEN either runs its statements and leaves, or
                    // falls to the next WHEN; after the last one the index moves and the search repeats.
                    succ.Add(BuildBlock(s.Blocks?.GetValueOrDefault("AT-END"), next, ctx, para));
                    int chain = n;
                    var whens = s.Blocks?.GetValueOrDefault("WHEN") ?? [];
                    for (int i = whens.Count - 1; i >= 0; i--)
                    {
                        var w = whens[i];
                        int wn = New(w.StepId, w);
                        AccessesOf(w, nodes[wn], items, files);
                        nodes[wn].Succ.Add(BuildBlock(w.Blocks?.GetValueOrDefault("TRUE"), next, ctx, para));
                        nodes[wn].Succ.Add(chain);
                        g.CtrlDeps.Add(new CtrlDepEdge(s.StepId, w.StepId, "EVAL"));
                        chain = wn;
                    }
                    succ.Add(chain);
                    break;
                }
                case "SORT" or "MERGE":
                {
                    // INPUT / OUTPUT PROCEDUREs are performed ranges.
                    bool any = false;
                    foreach (var (proc, thru) in new[] { (s.Str("inputProc"), s.Str("inputThru")), (s.Str("outputProc"), s.Str("outputThru")) })
                    {
                        if (proc is null) continue;
                        if (!paraIndex.TryGetValue(proc, out var lo) || RangeEnd(thru ?? proc) < 0)
                        {
                            g.Frontiers.Add(new Frontier("perform", proc, $"unresolved SORT procedure at {s.StepId}"));
                            continue;
                        }
                        int hi = RangeEnd(thru ?? proc);
                        succ.Add(paraEntry[lo]);
                        returnSites.Add((hi, next));
                        g.ParagraphEdges.Add(new ParagraphEdge(para, p.Paragraphs[lo].Name, "perform"));
                        any = true;
                    }
                    succ.Add(next);
                    if (!any && s.Frontier is not null) g.Frontiers.Add(new Frontier("statement", s.StepId, s.Frontier));
                    break;
                }
                case "PERFORM":
                {
                    bool loop = s.Get("until") is not null || s.Get("times") is not null || s.Get("var") is not null;
                    if (s.Str("inline") == "true")
                    {
                        var inner = new Ctx { SentenceFollow = ctx.SentenceFollow, ParaExit = ctx.ParaExit, LoopHeader = n, LoopFollow = next };
                        succ.Add(BuildBlock(s.Blocks?.GetValueOrDefault("LOOP"), loop ? n : next, inner, para));
                        if (loop) succ.Add(next);
                        break;
                    }
                    var target = s.Str("target")!;
                    var thru = s.Str("thru") ?? target;
                    if (!paraIndex.TryGetValue(target, out var lo) || RangeEnd(thru) < 0)
                    {
                        g.Frontiers.Add(new Frontier("perform", target, $"unresolved PERFORM target at {s.StepId}"));
                        succ.Add(next);
                        break;
                    }
                    int hi = RangeEnd(thru);
                    succ.Add(paraEntry[lo]);
                    if (loop) succ.Add(next);
                    returnSites.Add((hi, loop ? n : next));
                    performs[n] = (lo, hi, next);
                    g.ParagraphEdges.Add(new ParagraphEdge(para, p.Paragraphs[lo].Name, s.Str("thru") is null ? "perform" : "perform-thru"));
                    if (s.Str("thru") is not null && hi != lo) g.ParagraphEdges.Add(new ParagraphEdge(para, p.Paragraphs[hi].Name, "perform-thru-end"));
                    break;
                }
                case "GO TO":
                    foreach (var t in s.L?.GetValueOrDefault("targets") ?? [])
                    {
                        if (paraIndex.TryGetValue(t.V!, out var ti)) { succ.Add(paraEntry[ti]); g.ParagraphEdges.Add(new ParagraphEdge(para, t.V!, "goto")); }
                        else g.Frontiers.Add(new Frontier("goto", t.V!, $"unresolved GO TO target at {s.StepId}"));
                    }
                    if (s.Get("depending") is not null) succ.Add(next);
                    break;
                case "GOBACK" or "STOP RUN" or "EXIT PROGRAM":
                    succ.Add(progExit);
                    if (s.Verb == "EXIT PROGRAM") succ.Add(next);
                    break;
                case "NEXT SENTENCE": succ.Add(ctx.SentenceFollow); break;
                case "EXIT PARAGRAPH": succ.Add(ctx.ParaExit); break;
                case "EXIT PERFORM": succ.Add(ctx.LoopFollow >= 0 ? ctx.LoopFollow : next); break;
                case "EXIT PERFORM CYCLE": succ.Add(ctx.LoopHeader >= 0 ? ctx.LoopHeader : next); break;
                default:
                    if (s.Blocks is not null)
                        foreach (var (_, b) in s.Blocks) succ.Add(BuildBlock(b, next, ctx, para));
                    succ.Add(next);
                    if (s.Verb == "CALL") g.Calls.Add(CallOf(s, para));
                    if (s.Verb == "ALTER") alters.AddRange(AlterPairs(s));
                    if (s.Frontier is not null) g.Frontiers.Add(new Frontier("statement", s.StepId, s.Frontier));
                    break;
            }
            if (s.Blocks is not null)
                foreach (var (branch, b) in s.Blocks)
                    if (branch != "WHEN")
                        foreach (var c in b) g.CtrlDeps.Add(new CtrlDepEdge(s.StepId, c.StepId, branch));
            return n;
        }

        for (int pi = 0; pi < p.Paragraphs.Count; pi++)
        {
            var para = p.Paragraphs[pi];
            var stmts = para.Statements;
            // Build sentence by sentence (last first) so NEXT SENTENCE knows its target.
            int follow = paraExit[pi];
            var sentences = stmts.GroupBy(s => s.Sentence).Select(gr => gr.ToList()).ToList();
            for (int si = sentences.Count - 1; si >= 0; si--)
                follow = BuildBlock(sentences[si], follow, new Ctx { SentenceFollow = follow, ParaExit = paraExit[pi] }, para.Name);
            nodes[paraEntry[pi]].Succ.Add(follow);
            nodes[paraExit[pi]].Succ.Add(pi + 1 < p.Paragraphs.Count && p.Paragraphs[pi + 1].Declarative == para.Declarative ? paraEntry[pi + 1] : progExit);
        }
        // An ALTERed paragraph's GO TO may transfer to any ALTER target.
        foreach (var (from, to) in alters.Distinct())
        {
            if (!paraIndex.TryGetValue(from, out var fi) || !paraIndex.TryGetValue(to, out var ti))
            {
                g.Frontiers.Add(new Frontier("alter", from, $"unresolved ALTER {from} TO {to}"));
                continue;
            }
            nodes[paraEntry[fi]].Succ.Add(paraEntry[ti]);
            g.ParagraphEdges.Add(new ParagraphEdge(from, to, "alter"));
        }
        foreach (var (end, site) in returnSites) nodes[paraExit[end]].Succ.Add(site);

        // Paragraph reachability with PERFORM context: a performed range returns at its last paragraph instead of falling through.
        var paraOfEntry = new Dictionary<int, int>();
        var paraOfExit = new Dictionary<int, int>();
        for (int pi = 0; pi < p.Paragraphs.Count; pi++) { paraOfEntry[paraEntry[pi]] = pi; paraOfExit[paraExit[pi]] = pi; }
        var reached = new bool[p.Paragraphs.Count];
        var fallthrough = new SortedSet<int>();
        var visited = new HashSet<(int, long)>();
        var work = new Stack<(int Node, long Ctx)>([(progEntry, -1L)]);
        while (work.Count > 0)
        {
            var (x, c) = work.Pop();
            if (!visited.Add((x, c))) continue;
            if (paraOfEntry.TryGetValue(x, out var pe)) reached[pe] = true;
            if (performs.TryGetValue(x, out var pf))
            {
                work.Push((paraEntry[pf.Lo], ((long)pf.Lo << 32) | (uint)pf.Hi));
                work.Push((pf.After, c));
                continue;
            }
            if (paraOfExit.TryGetValue(x, out var px))
            {
                if (c >= 0 && px == (int)(c & 0xFFFFFFFF)) continue;
                if (px + 1 < p.Paragraphs.Count) { fallthrough.Add(px); work.Push((paraEntry[px + 1], c)); }
                continue;
            }
            foreach (var y in nodes[x].Succ) work.Push((y, c));
        }
        for (int pi = 0; pi < p.Paragraphs.Count; pi++)
        {
            if (!reached[pi]) g.DeadParagraphs.Add(p.Paragraphs[pi].Name);
            if (fallthrough.Contains(pi)) g.ParagraphEdges.Add(new ParagraphEdge(p.Paragraphs[pi].Name, p.Paragraphs[pi + 1].Name, "fallthrough"));
        }

        if (dataDeps || g.Calls.Any(c => c.Kind == "dynamic"))
        {
            var (defs, inSets) = ReachingDefinitions(nodes, g, progEntry, dataDeps);
            ResolveDynamicCalls(g, nodes, items, defs, inSets);
        }

        foreach (var n in nodes.Where(n => n.Stmt is not null))
            g.Cfg[n.Label] = Collapse(nodes, n.Id).Distinct().ToList();
        return g;
    }

    /// <summary>Successor statements of a node, skipping paragraph pseudo nodes.</summary>
    static IEnumerable<string> Collapse(List<CfgNode> nodes, int id)
    {
        var seen = new HashSet<int>();
        var work = new Stack<int>(nodes[id].Succ);
        while (work.Count > 0)
        {
            var x = work.Pop();
            if (!seen.Add(x)) continue;
            if (nodes[x].Stmt is not null || nodes[x].Label == "<exit>") yield return nodes[x].Label;
            else foreach (var y in nodes[x].Succ) work.Push(y);
        }
    }

    // ------------------------------------------------------------------ accesses

    static (string Area, int Start, int End)? RangeOf(DataItem it, Dictionary<string, FileDecl> files)
    {
        var root = it.Record ?? it;
        while (root.RedefinesItem is not null && root.RedefinesItem != root) root = root.RedefinesItem.Record ?? root.RedefinesItem;
        var area = root.FileName is not null ? "F:" + root.FileName : "R:" + root.Id;
        if (it.Dims.Count > 0)
        {
            // Any occurrence: the whole outermost table.
            var t = it;
            while (t.Parent is not null && t.Parent.Dims.Count > 0) t = t.Parent;
            var table = t.Occurs > 0 ? t : t.Parent ?? t;
            return (area, table.Offset, table.Offset + table.Length * Math.Max(1, table.Occurs));
        }
        return (area, it.Offset, it.Offset + Math.Max(it.Length, 1));
    }

    static void AccessesOf(Stmt s, CfgNode n, Dictionary<int, DataItem> items, Dictionary<string, FileDecl> files)
    {
        void Use(Node? x) => Collect(x, n.Uses, items, files, exactDef: false);
        void Def(Node? x, bool exact = true)
        {
            if (x is null) return;
            if (x.K is "ref" or "c88" && x.Id is { } id && items.TryGetValue(id, out var it))
            {
                if (it.Level == 88 && it.Parent is not null) it = it.Parent;
                if (RangeOf(it, files) is { } r)
                    n.Defs.Add(new Access(r.Area, r.Start, r.End, it.Name, exact && x.Args is null && x.Start is null && it.Dims.Count == 0));
                foreach (var a in x.Args ?? []) Use(a);
                Use(x.Start); Use(x.Len);
            }
        }
        void FileArea(string? file, bool def)
        {
            if (file is null || !files.TryGetValue(file, out var f)) return;
            var acc = new Access("F:" + f.Name, 0, Math.Max(f.MaxRecord, 1), f.Name, def);
            if (def) n.Defs.Add(acc); else n.Uses.Add(acc);
            if (f.FileStatus is { } fs)
                foreach (var it in items.Values.Where(i => i.Name == fs && i.Level != 88).Take(1))
                    if (RangeOf(it, files) is { } r) n.Defs.Add(new Access(r.Area, r.Start, r.End, it.Name, true));
        }
        IEnumerable<Node> L(string k) => s.L?.GetValueOrDefault(k) ?? [];

        switch (s.Verb)
        {
            case "MOVE": Use(s.Get("src")); foreach (var d in L("dst")) Def(d); break;
            case "COMPUTE": Use(s.Get("expr")); foreach (var d in L("dst")) Def(d); break;
            case "ADD" or "SUBTRACT" or "MULTIPLY" or "DIVIDE":
                foreach (var o in L("ops")) Use(o);
                Use(s.Get("by")); Use(s.Get("minuend"));
                foreach (var d in L("dst")) { Use(d); Def(d); }
                foreach (var d in L("giving")) Def(d);
                Def(s.Get("rem"));
                break;
            case "IF": case "WHEN": Use(s.Get("cond")); break;
            case "SEARCH" or "SEARCH ALL":
                if (s.Get("index") is { } ix) { Use(ix); Def(ix); }
                if (s.Get("varying") is { } vy) { Use(vy); Def(vy); }
                break;
            case "EVALUATE": foreach (var x in L("subjects")) Use(x); break;
            case "PERFORM":
                Use(s.Get("until")); Use(s.Get("times")); Use(s.Get("from")); Use(s.Get("by"));
                if (s.Get("var") is { } v) { Use(v); Def(v); }
                foreach (var a in L("after"))
                {
                    Use(a.Args![0]); Def(a.Args[0]);
                    Use(a.Args[1]); Use(a.Args[2]); Use(a.Args[3]);
                }
                break;
            case "GO TO": Use(s.Get("depending")); break;
            case "DISPLAY": foreach (var x in L("items")) Use(x); break;
            case "ACCEPT": Def(s.Get("dst")); break;
            case "OPEN" or "CLOSE": foreach (var f in L("files")) FileArea(f.V, def: false); break;
            case "READ":
                Use(s.Get("key"));
                if (s.Str("file") is { } rf && files.TryGetValue(rf, out var fd) && fd.RecordKey is { } key)
                    foreach (var k in items.Values.Where(i => i.Name == key && i.Record?.FileName == fd.Name).Take(1))
                        if (RangeOf(k, files) is { } kr) n.Uses.Add(new Access(kr.Area, kr.Start, kr.End, k.Name, false));
                FileArea(s.Str("file"), def: true);
                Def(s.Get("into"));
                break;
            case "WRITE" or "REWRITE":
                Use(s.Get("from"));
                if (s.Get("from") is not null) Def(s.Get("record"));
                Use(s.Get("record"));
                if (s.Get("record")?.Id is { } rid && items.TryGetValue(rid, out var rec)) FileArea(rec.Record?.FileName, def: false);
                break;
            case "DELETE" or "START": Use(s.Get("key")); FileArea(s.Str("file"), def: false); break;
            case "STRING":
                foreach (var pc in L("pieces")) { Use(pc.L); Use(pc.R); }
                Use(s.Get("into")); Def(s.Get("into"), exact: false);
                if (s.Get("pointer") is { } ptr) { Use(ptr); Def(ptr); }
                break;
            case "UNSTRING":
                Use(s.Get("src"));
                foreach (var d in L("delims")) Use(d);
                foreach (var t in L("targets")) { Def(t.L); Def(t.Start); Def(t.Len); }
                if (s.Get("pointer") is { } up) { Use(up); Def(up); }
                if (s.Get("tallying") is { } ut) { Use(ut); Def(ut); }
                break;
            case "INSPECT":
                Use(s.Get("target")); Def(s.Get("target"), exact: false);
                foreach (var c in L("clauses")) { Use(c.L); Use(c.R); Use(c.Start); Use(c.Len); foreach (var a in c.Args ?? []) { Use(a); Def(a); } }
                break;
            case "INITIALIZE": foreach (var d in L("dst")) Def(d, exact: false); break;
            case "SET":
                Use(s.Get("value"));
                foreach (var d in L("dst")) { if (s.Str("mode") is "UP" or "DOWN") Use(d); Def(d); }
                break;
            case "CALL":
                Use(s.Get("target"));
                foreach (var a in L("args"))
                {
                    Use(a.L);
                    if (a.Op == "REFERENCE") Def(a.L, exact: false);
                }
                break;
        }
    }

    static void Collect(Node? x, List<Access> into, Dictionary<int, DataItem> items, Dictionary<string, FileDecl> files, bool exactDef)
    {
        if (x is null) return;
        if (x.K is "ref" or "c88" && x.Id is { } id && items.TryGetValue(id, out var it))
        {
            if (it.Level == 88 && it.Parent is not null) it = it.Parent;
            if (RangeOf(it, files) is { } r) into.Add(new Access(r.Area, r.Start, r.End, it.Name, exactDef));
        }
        Collect(x.L, into, items, files, false);
        Collect(x.R, into, items, files, false);
        Collect(x.Start, into, items, files, false);
        Collect(x.Len, into, items, files, false);
        foreach (var a in x.Args ?? []) Collect(a, into, items, files, false);
    }

    // ------------------------------------------------------------------ reaching definitions

    static (List<(int Node, Access A)> Defs, BitArray[] In) ReachingDefinitions(List<CfgNode> nodes, ProgramGraph g, int entry, bool edges = true)
    {
        var defs = new List<(int Node, Access A)>();
        foreach (var n in nodes) foreach (var d in n.Defs) defs.Add((n.Id, d));
        int nd = defs.Count;
        var gen = new BitArray[nodes.Count];
        var kill = new BitArray[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            gen[i] = new BitArray(nd);
            kill[i] = new BitArray(nd);
        }
        for (int k = 0; k < nd; k++)
        {
            var (node, a) = defs[k];
            gen[node][k] = true;
            if (!a.Exact) continue;
            for (int j = 0; j < nd; j++)
            {
                var b = defs[j].A;
                if (j != k && b.Area == a.Area && b.Start >= a.Start && b.End <= a.End) kill[node][j] = true;
            }
        }
        var preds = new List<int>[nodes.Count];
        for (int i = 0; i < nodes.Count; i++) preds[i] = [];
        foreach (var n in nodes) foreach (var s in n.Succ) preds[s].Add(n.Id);

        var inSet = new BitArray[nodes.Count];
        var outSet = new BitArray[nodes.Count];
        for (int i = 0; i < nodes.Count; i++) { inSet[i] = new BitArray(nd); outSet[i] = new BitArray(gen[i]); }
        var order = ReversePostorder(nodes, entry);
        bool changed = true;
        int iter = 0;
        while (changed && iter < MaxIterations)
        {
            changed = false;
            iter++;
            foreach (var i in order)
            {
                var input = new BitArray(nd);
                foreach (var p in preds[i]) input.Or(outSet[p]);
                inSet[i] = input;
                var o = new BitArray(input).And(new BitArray(kill[i]).Not()).Or(gen[i]);
                if (!Same(o, outSet[i])) { outSet[i] = o; changed = true; }
            }
        }
        g.DataflowIterations = iter;
        if (changed) g.Frontiers.Add(new Frontier("dataflow", g.ProgramId, $"reaching definitions did not converge in {MaxIterations} iterations; data dependences are partial"));

        if (!edges) return (defs, inSet);
        var seen = new HashSet<(string, string, string)>();
        foreach (var n in nodes.Where(n => n.Stmt is not null))
            foreach (var u in n.Uses)
                for (int k = 0; k < nd; k++)
                {
                    if (!inSet[n.Id][k]) continue;
                    var (dn, d) = defs[k];
                    if (d.Area != u.Area || d.End <= u.Start || d.Start >= u.End) continue;
                    if (seen.Add((nodes[dn].Label, n.Label, u.Field))) g.DataDeps.Add(new DataDepEdge(nodes[dn].Label, n.Label, u.Field));
                }
        return (defs, inSet);
    }

    static List<int> ReversePostorder(List<CfgNode> nodes, int entry)
    {
        var post = new List<int>();
        var seen = new bool[nodes.Count];
        var stack = new Stack<(int Node, int Next)>();
        stack.Push((entry, 0));
        seen[entry] = true;
        while (stack.Count > 0)
        {
            var (x, k) = stack.Pop();
            if (k < nodes[x].Succ.Count)
            {
                stack.Push((x, k + 1));
                var y = nodes[x].Succ[k];
                if (!seen[y]) { seen[y] = true; stack.Push((y, 0)); }
            }
            else post.Add(x);
        }
        post.Reverse();
        for (int i = 0; i < nodes.Count; i++) if (!seen[i]) post.Add(i);
        return post;
    }

    static bool Same(BitArray a, BitArray b)
    {
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // ------------------------------------------------------------------ calls

    static IEnumerable<(string, string)> AlterPairs(Stmt s)
    {
        // ALTER p1 TO [PROCEED TO] q1 [p2 TO [PROCEED TO] q2 ...]
        var w = s.Tokens.Where(t => t.Kind is TokKind.Word or TokKind.Number).Select(t => t.Upper).Skip(1).ToList();
        int i = 0;
        while (i + 2 <= w.Count)
        {
            var from = w[i++];
            if (i >= w.Count || w[i++] != "TO") yield break;
            if (i < w.Count && w[i] == "PROCEED") { i++; if (i < w.Count && w[i] == "TO") i++; }
            if (i >= w.Count) yield break;
            yield return (from, w[i++]);
        }
    }

    static CallSite CallOf(Stmt s, string para)
    {
        var t = s.Get("target");
        return t?.K == "str"
            ? new CallSite(s.StepId, para, t.V!.Trim().ToUpperInvariant(), "static", [t.V!.Trim().ToUpperInvariant()])
            : new CallSite(s.StepId, para, t?.V ?? "?", "dynamic", []);
    }

    /// <summary>A dynamic CALL resolves when every reaching definition of its target field is a MOVE of a literal (or its VALUE).</summary>
    static void ResolveDynamicCalls(ProgramGraph g, List<CfgNode> nodes, Dictionary<int, DataItem> items, List<(int Node, Access A)> defs, BitArray[] inSets)
    {
        for (int i = 0; i < g.Calls.Count; i++)
        {
            var c = g.Calls[i];
            if (c.Kind != "dynamic") continue;
            var node = nodes.First(n => n.Label == c.StepId);
            var field = node.Stmt!.Get("target");
            var targets = new SortedSet<string>(StringComparer.Ordinal);
            bool closed = true, any = false;
            for (int k = 0; k < defs.Count; k++)
            {
                if (!inSets[node.Id][k]) continue;
                var (dn, d) = defs[k];
                if (field?.Id is not { } fid || !items.TryGetValue(fid, out var fit) || d.Field != fit.Name) continue;
                any = true;
                var src = nodes[dn].Stmt?.Verb == "MOVE" ? nodes[dn].Stmt!.Get("src") : null;
                if (src?.K == "str") targets.Add(src.V!.Trim().ToUpperInvariant()); else closed = false;
            }
            if (!any && field?.Id is { } vid && items.TryGetValue(vid, out var vit) && vit.Values.Count > 0 && vit.Values[0].Low.Quoted)
                targets.Add(vit.Values[0].Low.Text.Trim().ToUpperInvariant());
            if (closed && targets.Count > 0) g.Calls[i] = c with { Kind = "dynamic-resolved", ResolvedTargets = targets.ToList() };
            else g.Frontiers.Add(new Frontier("dynamic-call", c.StepId, $"CALL {c.Target}: targets not closed by reaching definitions"));
        }
    }
}

public sealed record EstateCallEdge(string Caller, string Callee, string Kind, string StepId, bool InEstate);

/// <summary>Inter-program call graph over an estate (static and resolved dynamic CALLs).</summary>
public static class EstateCallGraph
{
    public static readonly HashSet<string> RuntimeServices = new(StringComparer.OrdinalIgnoreCase) { "CEE3ABD", "ILBOABN0", "CEEDAYS", "CEELOCT", "CEEGMT" };

    public static List<EstateCallEdge> Build(IEnumerable<ProgramGraph> graphs, ISet<string> estatePrograms)
    {
        var edges = new List<EstateCallEdge>();
        foreach (var g in graphs.OrderBy(x => x.ProgramId, StringComparer.Ordinal))
            foreach (var c in g.Calls)
            {
                if (c.ResolvedTargets.Count == 0) { edges.Add(new EstateCallEdge(g.ProgramId, c.Target, "unresolved", c.StepId, false)); continue; }
                foreach (var t in c.ResolvedTargets)
                    edges.Add(new EstateCallEdge(g.ProgramId, t, RuntimeServices.Contains(t) ? "runtime-service" : c.Kind, c.StepId, estatePrograms.Contains(t)));
            }
        return edges;
    }
}
