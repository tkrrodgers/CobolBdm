namespace MlCpu.Modernize;

/// <summary>
/// Low IR (labels, gotos) → structured statements. A control-flow graph, its dominator tree, then the recursive
/// translation of N. Ramsey, "Beyond Relooper" (ICFP 2022): loops at loop headers, labelled blocks before merge nodes,
/// branches as continue / break / inline code. Irreducible graphs are reported, not guessed.
/// </summary>
public sealed class Structurer
{
    sealed class Block
    {
        public int Id;
        public string? Label;
        public List<St> Body = [];
        public St? Term; // LGoto, LCondGoto (then falls to Next), LSwitchGoto, SReturn, SThrow
        public int Next = -1; // fall-through
        public List<int> Succ = [];
        public List<int> Pred = [];
        public int Rpo = -1;
        public int Idom = -1;
        public List<int> DomKids = [];
        public bool Loop;
        public bool Merge;
    }

    readonly List<Block> blocks = [];
    readonly Dictionary<string, int> byLabel = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> problems;
    int labelNo;

    Structurer(List<string> problems) { this.problems = problems; }

    public static List<St> Run(List<St> low, List<string> problems, string fn)
    {
        var s = new Structurer(problems);
        return s.Build(low, fn);
    }

    List<St> Build(List<St> low, string fn)
    {
        Split(low);
        if (blocks.Count == 0) return [];
        Edges();
        Order();
        Dominators();
        if (!Reducible()) { problems.Add(fn + ": irreducible control flow"); }
        var code = DoTree(0, []);
        return code;
    }

    // ------------------------------------------------------------------ blocks

    void Split(List<St> low)
    {
        Block cur = NewBlock(null);
        foreach (var s in low)
        {
            if (s is LLabel l)
            {
                if (cur.Body.Count > 0 || cur.Term is not null || cur.Label is not null)
                {
                    var nb = NewBlock(l.Name);
                    if (cur.Term is null || cur.Term is LCondGoto) cur.Next = nb.Id;
                    cur = nb;
                }
                else cur.Label = l.Name;
                byLabel[l.Name] = cur.Id;
                continue;
            }
            if (cur.Term is not null && cur.Term is not LCondGoto)
            {
                // code after an unconditional transfer is unreachable until the next label
                cur = NewBlock(null);
            }
            else if (cur.Term is LCondGoto)
            {
                var nb = NewBlock(null);
                cur.Next = nb.Id;
                cur = nb;
            }
            if (s is LGoto or LCondGoto or LSwitchGoto or SReturn or SThrow) cur.Term = s;
            else cur.Body.Add(s);
        }
    }

    Block NewBlock(string? label)
    {
        var b = new Block { Id = blocks.Count, Label = label };
        blocks.Add(b);
        if (label is not null) byLabel[label] = b.Id;
        return b;
    }

    int Target(string label)
    {
        if (byLabel.TryGetValue(label, out var id)) return id;
        problems.Add("goto to unknown label " + label);
        return -1;
    }

    void Edges()
    {
        foreach (var b in blocks)
        {
            switch (b.Term)
            {
                case LGoto g: Add(b, Target(g.Target)); break;
                case LCondGoto cg: Add(b, Target(cg.Target)); Add(b, b.Next); break;
                case LSwitchGoto sw:
                    foreach (var (_, t) in sw.Cases) Add(b, Target(t));
                    Add(b, Target(sw.Default));
                    break;
                case SReturn or SThrow: break;
                default: if (b.Next >= 0) Add(b, b.Next); break;
            }
        }
    }

    void Add(Block from, int to)
    {
        if (to < 0) return;
        if (!from.Succ.Contains(to)) from.Succ.Add(to);
        if (!blocks[to].Pred.Contains(from.Id)) blocks[to].Pred.Add(from.Id);
    }

    readonly List<int> rpo = [];

    void Order()
    {
        var seen = new bool[blocks.Count];
        var post = new List<int>();
        // iterative DFS
        var stack = new Stack<(int Node, int Next)>();
        stack.Push((0, 0));
        seen[0] = true;
        while (stack.Count > 0)
        {
            var (n, i) = stack.Pop();
            var succ = blocks[n].Succ;
            if (i < succ.Count)
            {
                stack.Push((n, i + 1));
                var m = succ[i];
                if (!seen[m]) { seen[m] = true; stack.Push((m, 0)); }
            }
            else post.Add(n);
        }
        post.Reverse();
        rpo.AddRange(post);
        for (int k = 0; k < rpo.Count; k++) blocks[rpo[k]].Rpo = k;
        // drop unreachable predecessors
        foreach (var b in blocks) b.Pred.RemoveAll(p => blocks[p].Rpo < 0);
    }

    void Dominators()
    {
        // Cooper, Harvey, Kennedy
        foreach (var b in blocks) b.Idom = -1;
        blocks[0].Idom = 0;
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var n in rpo.Skip(1))
            {
                int newIdom = -1;
                foreach (var p in blocks[n].Pred)
                {
                    if (blocks[p].Idom < 0) continue;
                    newIdom = newIdom < 0 ? p : Intersect(p, newIdom);
                }
                if (newIdom >= 0 && blocks[n].Idom != newIdom) { blocks[n].Idom = newIdom; changed = true; }
            }
        }
        foreach (var n in rpo.Skip(1)) if (blocks[n].Idom >= 0) blocks[blocks[n].Idom].DomKids.Add(n);
        foreach (var n in rpo)
        {
            var b = blocks[n];
            int forward = 0;
            foreach (var p in b.Pred)
            {
                if (blocks[p].Rpo >= b.Rpo) b.Loop = true; // back edge
                else forward++;
            }
            b.Merge = forward >= 2;
        }
    }

    int Intersect(int a, int b)
    {
        while (a != b)
        {
            while (blocks[a].Rpo > blocks[b].Rpo) a = blocks[a].Idom;
            while (blocks[b].Rpo > blocks[a].Rpo) b = blocks[b].Idom;
        }
        return a;
    }

    bool Dominates(int a, int b)
    {
        while (true)
        {
            if (a == b) return true;
            if (b == 0) return false;
            var d = blocks[b].Idom;
            if (d == b || d < 0) return false;
            b = d;
        }
    }

    bool Reducible()
    {
        foreach (var n in rpo)
            foreach (var s in blocks[n].Succ)
                if (blocks[s].Rpo <= blocks[n].Rpo && !Dominates(s, n)) return false;
        return true;
    }

    // ------------------------------------------------------------------ translation

    abstract record Ctx;
    sealed record LoopCtx(int Header, string Label) : Ctx;
    sealed record BlockCtx(int Follow, string Label) : Ctx;
    sealed record IfCtx : Ctx;

    List<St> DoTree(int x, List<Ctx> ctx)
    {
        var b = blocks[x];
        var merges = b.DomKids.Where(k => blocks[k].Merge).OrderByDescending(k => blocks[k].Rpo).ToList();
        if (b.Loop)
        {
            var label = "loop" + (++labelNo);
            var inner = new List<Ctx>(ctx) { new LoopCtx(x, label) };
            var body = NodeWithin(x, merges, inner);
            return [new SLoop(label, null, body) { Line = FirstLine(b) }];
        }
        return NodeWithin(x, merges, ctx);
    }

    List<St> NodeWithin(int x, List<int> ys, List<Ctx> ctx)
    {
        if (ys.Count == 0)
        {
            var b = blocks[x];
            var code = new List<St>(b.Body);
            code.AddRange(Terminator(b, ctx));
            return code;
        }
        var y = ys[0];
        var rest = ys.Skip(1).ToList();
        var label = "block" + (++labelNo);
        var inner = new List<Ctx>(ctx) { new BlockCtx(y, label) };
        var res = new List<St> { new SBlock(label, NodeWithin(x, rest, inner)) };
        res.AddRange(DoTree(y, ctx));
        return res;
    }

    List<St> Terminator(Block b, List<Ctx> ctx)
    {
        switch (b.Term)
        {
            case SReturn r: return [r];
            case SThrow t: return [t];
            case LGoto g: return Branch(b.Id, Target(g.Target), ctx);
            case LCondGoto cg:
            {
                if (Target(cg.Target) == b.Next) return Branch(b.Id, b.Next, ctx); // both ways to the same place
                var then = Branch(b.Id, Target(cg.Target), ctx);
                var els = Branch(b.Id, b.Next, ctx);
                return [new SIf(cg.C, then, els) { Line = cg.Line, Site = cg.Site }];
            }
            case LSwitchGoto sw:
            {
                var s = new SSwitch(sw.Subject) { Line = sw.Line };
                foreach (var (vals, t) in sw.Cases) s.Cases.Add((vals, Branch(b.Id, Target(t), ctx)));
                s.Default = Branch(b.Id, Target(sw.Default), ctx);
                return [s];
            }
            default:
                if (b.Next >= 0) return Branch(b.Id, b.Next, ctx);
                return [];
        }
    }

    List<St> Branch(int from, int to, List<Ctx> ctx)
    {
        if (to < 0) return [new SThrow(new ENew { JdkClass = "java.lang.IllegalStateException", Args = [ELit.Str("unknown jump")] })];
        if (blocks[to].Rpo <= blocks[from].Rpo)
        {
            var lc = ctx.OfType<LoopCtx>().LastOrDefault(c => c.Header == to);
            if (lc is null) { problems.Add($"backward jump to {blocks[to].Label} without a loop"); return []; }
            return [new SContinue(lc.Label)];
        }
        if (blocks[to].Merge)
        {
            var bc = ctx.OfType<BlockCtx>().LastOrDefault(c => c.Follow == to);
            if (bc is null) { problems.Add($"jump to merge node {blocks[to].Label} outside its block"); return []; }
            return [new SBreak(bc.Label)];
        }
        return DoTree(to, ctx);
    }

    static int FirstLine(Block b) => b.Body.FirstOrDefault()?.Line ?? b.Term?.Line ?? 0;
}
