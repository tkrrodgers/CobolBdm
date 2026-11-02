namespace MlCpu.Modernize;

/// <summary>Expression recovery: returns, temporaries split into webs and folded into the expressions that use them.</summary>
public static class Fold
{
    // ================================================================== returns (low IR)

    /// <summary>result = e; goto EXIT → return e. The exit paragraph returns the result variable.</summary>
    public static void Returns(List<St> low, string exitLabel, Var? result)
    {
        for (int i = 0; i < low.Count; i++)
        {
            if (low[i] is SAssign { Lhs: EVar { V: var v } } a && result is not null && v == result && i + 1 < low.Count)
            {
                if (low[i + 1] is LGoto g && g.Target.Equals(exitLabel, StringComparison.OrdinalIgnoreCase))
                {
                    low[i] = new SReturn(a.Rhs) { Line = a.Line, Site = a.Site };
                    low.RemoveAt(i + 1);
                    continue;
                }
                if (low[i + 1] is LLabel l && l.Name.Equals(exitLabel, StringComparison.OrdinalIgnoreCase))
                {
                    low[i] = new SReturn(a.Rhs) { Line = a.Line, Site = a.Site };
                    continue;
                }
            }
            if (low[i] is LGoto g2 && g2.Target.Equals(exitLabel, StringComparison.OrdinalIgnoreCase))
                low[i] = new SReturn(result is null ? null : new EVar(result)) { Line = g2.Line };
        }
    }

    /// <summary>
    /// A jump to a small block that ends the function (return or throw, at most three statements) becomes a copy of
    /// that block: early returns instead of nested code around one shared exit.
    /// </summary>
    public static void DuplicateExits(List<St> low)
    {
        for (int round = 0; round < 4; round++)
        {
            var exits = new Dictionary<string, List<St>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < low.Count; i++)
            {
                if (low[i] is not LLabel l) continue;
                var body = new List<St>();
                int k = i + 1;
                while (k < low.Count && low[k] is not LLabel && body.Count < 4)
                {
                    body.Add(low[k]);
                    if (low[k] is SReturn or SThrow or LGoto or LCondGoto or LSwitchGoto) break;
                    k++;
                }
                if (body.Count is > 0 and <= 3 && body[^1] is SReturn or SThrow && body.All(s => s is SAssign or SExpr or SReturn or SThrow))
                    exits[l.Name] = body;
            }
            if (exits.Count == 0) return;
            bool changed = false;
            for (int i = 0; i < low.Count; i++)
            {
                if (low[i] is LGoto g && exits.TryGetValue(g.Target, out var copy))
                {
                    low.RemoveAt(i);
                    low.InsertRange(i, copy.Select(Clone));
                    i += copy.Count - 1;
                    changed = true;
                }
                else if (low[i] is LCondGoto cg && exits.TryGetValue(cg.Target, out var copy2))
                {
                    var skip = "dx_" + i + "_" + round;
                    low[i] = new LCondGoto(FunctionLifter.Not(cg.C), skip) { Line = cg.Line, Site = cg.Site };
                    low.InsertRange(i + 1, copy2.Select(Clone).Append(new LLabel(skip)));
                    i += copy2.Count + 1;
                    changed = true;
                }
            }
            if (!changed) return;
        }
    }

    static St Clone(St s) => s switch
    {
        SAssign a => new SAssign(a.Lhs, a.Rhs) { Line = a.Line, Site = a.Site },
        SExpr e => new SExpr(e.E) { Line = e.Line, Site = e.Site },
        SReturn r => new SReturn(r.E) { Line = r.Line, Site = r.Site },
        SThrow t => new SThrow(t.E) { Line = t.Line, Site = t.Site },
        _ => s,
    };

    // ================================================================== webs (low IR)

    /// <summary>
    /// Splits each temporary into webs (definitions connected through the uses they reach): J3S reuses statement
    /// temporaries, Java gets one variable per value. A structured try statement in the list is one instruction that uses
    /// the temporaries it mentions (deep). <paramref name="region"/>: the list is a try region's own; a temporary it reads
    /// before setting it there holds the value of the code before the region, so every web the region defines is a new
    /// variable (the old one is left to that code).
    /// </summary>
    public static void Webs(List<St> low, List<Var> vars, ISet<Var>? exclude = null, bool region = false)
    {
        // blocks over the low IR: index ranges; successor by labels
        var labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < low.Count; i++) if (low[i] is LLabel l) labels[l.Name] = i;
        int n = low.Count;
        var succ = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            succ[i] = [];
            switch (low[i])
            {
                case LGoto g: if (labels.TryGetValue(g.Target, out var t)) succ[i].Add(t); break;
                case LCondGoto cg: if (labels.TryGetValue(cg.Target, out var t2)) succ[i].Add(t2); if (i + 1 < n) succ[i].Add(i + 1); break;
                case LSwitchGoto sw:
                    foreach (var (_, tg) in sw.Cases) if (labels.TryGetValue(tg, out var t3)) succ[i].Add(t3);
                    if (labels.TryGetValue(sw.Default, out var t4)) succ[i].Add(t4);
                    break;
                case SReturn or SThrow: break;
                default: if (i + 1 < n) succ[i].Add(i + 1); break;
            }
        }
        var temps = vars.Where(v => v.IsTemp && (region || exclude is null || !exclude.Contains(v))).ToHashSet();
        if (temps.Count == 0) return;
        // definitions: instruction index per temp
        var defsOf = new Dictionary<Var, List<int>>();
        for (int i = 0; i < n; i++)
            if (low[i] is SAssign { Lhs: EVar { V: var dv } } && temps.Contains(dv))
                (defsOf.TryGetValue(dv, out var l) ? l : defsOf[dv] = []).Add(i);
        // reaching definitions per temp (forward dataflow over instructions, only for temps with 2+ defs)
        var uf = new Dictionary<(Var, int), (Var, int)>();
        (Var, int) Find((Var, int) x) { while (uf.TryGetValue(x, out var p) && !p.Equals(x)) x = p; return x; }
        void Union((Var, int) a, (Var, int) b) { a = Find(a); b = Find(b); if (!a.Equals(b)) uf[a] = b; }
        var useWeb = new Dictionary<(int Instr, Var V), (Var, int)>();
        // region: temporaries the code outside the region also has (exclude) or that the region reads before setting them
        var keepNone = new HashSet<Var>();
        foreach (var (v, defs) in defsOf)
        {
            if (defs.Count < 2 && !region) continue;
            // a region's webs are all new variables: the slot's variable is the code's outside the region (before or after it)
            if (region) keepNone.Add(v);
            foreach (var d in defs) uf[(v, d)] = (v, d);
            // in[i]: set of defs of v reaching instruction i
            var inSet = new HashSet<int>[n];
            for (int i = 0; i < n; i++) inSet[i] = [];
            var work = new Queue<int>();
            foreach (var d in defs) foreach (var s in succ[d]) { if (inSet[s].Add(d)) work.Enqueue(s); }
            var queued = new HashSet<int>(work);
            while (work.Count > 0)
            {
                var i = work.Dequeue();
                queued.Remove(i);
                bool kills = low[i] is SAssign { Lhs: EVar { V: var kv } } && kv == v;
                if (kills) continue;
                foreach (var s in succ[i])
                {
                    bool grew = false;
                    foreach (var d in inSet[i]) grew |= inSet[s].Add(d);
                    if (grew && queued.Add(s)) work.Enqueue(s);
                }
            }
            for (int i = 0; i < n; i++)
            {
                if (!Uses(low[i], v)) continue;
                var reach = inSet[i].ToList();
                if (reach.Count == 0) { if (region) keepNone.Add(v); continue; }
                for (int k = 1; k < reach.Count; k++) Union((v, reach[0]), (v, reach[k]));
                useWeb[(i, v)] = (v, reach[0]);
            }
        }
        // one variable per web
        var webVar = new Dictionary<(Var, int), Var>();
        foreach (var (v, defs) in defsOf)
        {
            if (defs.Count < 2 && !region) continue;
            var roots = defs.Select(d => Find((v, d))).Distinct().ToList();
            if (roots.Count < 2 && !keepNone.Contains(v)) continue;
            bool first = !keepNone.Contains(v);
            foreach (var r in roots)
            {
                if (first) { webVar[r] = v; first = false; continue; }
                var nv = new Var { Name = v.Name, Kind = v.Kind, Type = v.Type, IsTemp = true };
                vars.Add(nv);
                webVar[r] = nv;
            }
        }
        if (webVar.Count == 0) return;
        for (int i = 0; i < n; i++)
        {
            var s = low[i];
            // uses
            foreach (var v in defsOf.Keys)
            {
                if (!useWeb.TryGetValue((i, v), out var w)) continue;
                if (!webVar.TryGetValue(Find(w), out var nv) || nv == v) continue;
                ReplaceUses(s, v, nv);
            }
            // definition
            if (s is SAssign { Lhs: EVar { V: var dv } } a && defsOf.ContainsKey(dv) && webVar.TryGetValue(Find((dv, i)), out var dn) && dn != dv)
                a.Lhs = new EVar(dn);
        }
    }

    static bool Uses(St s, Var v) => s is STry t ? Passes.All([t]).Skip(1).Any(x => Exprs(x).Any(e => Mentions(e, v)) || x is SAssign { Lhs: EVar { V: var dv } } && dv == v)
        : Exprs(s).Any(e => Mentions(e, v));

    /// <summary>The expressions a statement reads (an assignment's target object counts, not the variable it assigns).</summary>
    public static IEnumerable<Expr> Exprs(St s)
    {
        switch (s)
        {
            case SAssign a:
                if (a.Lhs is not EVar) foreach (var c in a.Lhs.Children) yield return c;
                yield return a.Rhs;
                break;
            case SExpr e: yield return e.E; break;
            case SReturn { E: { } r }: yield return r; break;
            case SThrow t: yield return t.E; break;
            case LCondGoto cg: yield return cg.C; break;
            case LSwitchGoto sw: yield return sw.Subject; break;
            case SIf i: yield return i.C; break;
            case SLoop l:
                if (l.C is not null) yield return l.C;
                if (l.Iterable is not null) yield return l.Iterable;
                break;
            case SSwitch ss: yield return ss.Subject; break;
        }
    }

    public static bool Mentions(Expr e, Var v)
    {
        if (e is EVar ev && ev.V == v) return true;
        if (e is ELambda l) return l.Captures.Values.Any(c => Mentions(c, v));
        foreach (var c in e.Children) if (Mentions(c, v)) return true;
        return false;
    }

    static void ReplaceUses(St s, Var from, Var to)
    {
        Expr R(Expr e) => e is EVar ev && ev.V == from ? new EVar(to) { Type = e.Type } : e.Map(R);
        switch (s)
        {
            case SAssign a: if (a.Lhs is not EVar) a.Lhs = a.Lhs.Map(R); a.Rhs = R(a.Rhs); break;
            case SExpr e: e.E = R(e.E); break;
            case SReturn r when r.E is not null: r.E = R(r.E); break;
            case SThrow t: t.E = R(t.E); break;
            case LCondGoto cg: cg.C = R(cg.C); break;
            case LSwitchGoto sw: sw.Subject = R(sw.Subject); break;
            // a try statement uses what it mentions: every occurrence is the outer web's (Webs, region)
            case STry t:
                foreach (var x in Passes.All([t]).Skip(1))
                {
                    if (x is SAssign { Lhs: EVar { V: var dv } } xa && dv == from) xa.Lhs = new EVar(to) { Type = xa.Lhs.Type };
                    if (x is SLoop { Each: { } each } xl && each == from) xl.Each = to;
                    ReplaceUses(x, from, to);
                    switch (x)
                    {
                        case SIf i: i.C = R(i.C); break;
                        case SLoop l: if (l.C is not null) l.C = R(l.C); if (l.Iterable is not null) l.Iterable = R(l.Iterable); break;
                        case SSwitch ss: ss.Subject = R(ss.Subject); break;
                    }
                }
                break;
        }
    }

    // ================================================================== results kept in fields

    /// <summary>
    /// t = e; f = t; ... uses of t: the job program keeps a result in a job object (f, a field of the job class) and uses
    /// it; Java writes f = e and reads f (the field is not written again before those uses), without a numbered local.
    /// </summary>
    public static void ResultFields(List<St> body)
    {
        foreach (var s in Passes.All(body).ToList())
            switch (s)
            {
                case SIf i: ResultFieldsIn(i.Then); ResultFieldsIn(i.Else); break;
                case SLoop l: ResultFieldsIn(l.Body); break;
                case SBlock b: ResultFieldsIn(b.Body); break;
                case STry t: ResultFieldsIn(t.Body); foreach (var c in t.Catches) ResultFieldsIn(c.Body); break;
                case SSwitch sw: foreach (var c in sw.Cases) ResultFieldsIn(c.Body); if (sw.Default is not null) ResultFieldsIn(sw.Default); break;
            }
        ResultFieldsIn(body);
    }

    static int UseCount(Expr e, Var v) => (e is EVar ev && ev.V == v ? 1 : 0) + (e is ELambda l ? l.Captures.Values.Sum(c => UseCount(c, v)) : e.Children.Sum(c => UseCount(c, v)));

    static void ResultFieldsIn(List<St> list)
    {
        for (int k = 0; k + 1 < list.Count; k++)
        {
            if (list[k] is not SAssign { Lhs: EVar { V: var t }, Rhs: var rhs } a || t.IsParam || list[k + 1] is not SAssign { Lhs: EStatic { F.Group: "SJOB" } f, Rhs: EVar { V: var t2 } } || t2 != t) continue;
            // t is read once after, as a direct argument of the next statement's call (evaluated before anything the call
            // does to the field), and assigned nowhere else
            var rest = list.Skip(k + 2).ToList();
            if (rest.Count == 0 || rest[0] is not SExpr { E: ECall { Args: var cargs } } || cargs.Count(x => x is EVar { V: var xv } && xv == t) != 1) continue;
            if (rest.Sum(x => Passes.All([x]).Sum(y => Exprs(y).Sum(e => UseCount(e, t)))) != 1) continue;
            if (rest.Any(x => Passes.All([x]).Any(y => y is SAssign { Lhs: EVar { V: var dv } } && dv == t))) continue;
            if (list.Take(k).Any(x => Passes.All([x]).Any(y => Exprs(y).Any(e => Mentions(e, t)) || y is SAssign { Lhs: EVar { V: var dv } } && dv == t))) continue;
            list[k + 1].Line = a.Line;
            ((SAssign)list[k + 1]).Rhs = rhs;
            list[k + 1].Site ??= a.Site;
            list.RemoveAt(k);
            foreach (var x in rest) foreach (var y in Passes.All([x])) SubstituteOwn(y, t, new EStatic(f.F) { Type = f.Type });
            k--;
        }
    }

    sealed class Counts
    {
        public readonly Dictionary<Var, int> Defs = [];
        public readonly Dictionary<Var, int> Uses = [];
    }

    static Counts Count(List<St> body)
    {
        var c = new Counts();
        void E(Expr e)
        {
            if (e is EVar v) c.Uses[v.V] = c.Uses.GetValueOrDefault(v.V) + 1;
            if (e is ELambda l) foreach (var x in l.Captures.Values) E(x);
            foreach (var ch in e.Children) if (e is not ELambda) E(ch);
        }
        foreach (var s in Passes.All(body))
        {
            if (s is SAssign { Lhs: EVar { V: var dv } }) c.Defs[dv] = c.Defs.GetValueOrDefault(dv) + 1;
            if (s is SLoop { Each: { } each }) c.Defs[each] = c.Defs.GetValueOrDefault(each) + 1;
            foreach (var e in Exprs(s)) E(e);
        }
        return c;
    }

    /// <summary>Folds single-definition, single-use temporaries; turns materialized booleans into conditions. Repeats to a fixpoint.</summary>
    /// <param name="chains">The last round: a temporary at the head of a call chain folds even across sites (each call on its own line).</param>
    public static List<St> Expressions(List<St> body, bool chains = false)
    {
        Chains = chains;
        for (int round = 0; round < 30; round++)
        {
            bool changed = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (Environment.GetEnvironmentVariable("FOLD_TRACE") == "1") Console.Error.WriteLine($"start round {round}");
            var counts = Count(body);
            if (Environment.GetEnvironmentVariable("FOLD_TRACE") == "1") Console.Error.WriteLine($"counted {sw.ElapsedMilliseconds}");
            var t1 = sw.ElapsedMilliseconds;
            body = InlineList(body, counts, ref changed);
            body = CopyPropagate(body, counts, ref changed);
            var t2 = sw.ElapsedMilliseconds;
            body = Booleans(body, ref changed);
            if (Environment.GetEnvironmentVariable("FOLD_TRACE") == "1") Console.Error.WriteLine($"round {round}: count {t1} inline {t2 - t1} bool {sw.ElapsedMilliseconds - t2} stmts {Passes.All(body).Count()}");
            if (!changed) break;
        }
        return body;
    }

    [ThreadStatic] static bool Chains;
    static int traceN;
    static List<St> InlineList(List<St> list, Counts counts, ref bool changed)
    {
        if (Environment.GetEnvironmentVariable("FOLD_TRACE") == "1" && traceN++ < 400) Console.Error.WriteLine($"inline list {list.Count}");
        // children first
        foreach (var s in list)
        {
            switch (s)
            {
                case SIf i: i.Then = InlineList(i.Then, counts, ref changed); i.Else = InlineList(i.Else, counts, ref changed); break;
                case SLoop l: l.Body = InlineList(l.Body, counts, ref changed); break;
                case SBlock b: b.Body = InlineList(b.Body, counts, ref changed); break;
                case SSwitch sw:
                    for (int k = 0; k < sw.Cases.Count; k++) { var bb = sw.Cases[k].Body; sw.Cases[k] = (sw.Cases[k].Labels, InlineList(bb, counts, ref changed)); }
                    if (sw.Default is not null) sw.Default = InlineList(sw.Default, counts, ref changed);
                    break;
                case STry t:
                    t.Body = InlineList(t.Body, counts, ref changed);
                    for (int k = 0; k < t.Catches.Count; k++) t.Catches[k] = (t.Catches[k].Type, t.Catches[k].V, InlineList(t.Catches[k].Body, counts, ref changed));
                    break;
            }
        }
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is not SAssign { Lhs: EVar { V: var t } } def || !t.IsTemp) continue;
            int defs = counts.Defs.GetValueOrDefault(t), uses = counts.Uses.GetValueOrDefault(t);
            if (defs != 1) continue;
            if (uses == 0)
            {
                // dead temporary: keep the call (its effect), drop a pure value
                if (Pure(def.Rhs)) { list.RemoveAt(i); i--; changed = true; }
                else if (def.Rhs is ECall or ENew) { list[i] = new SExpr(def.Rhs) { Line = def.Line, Site = def.Site }; changed = true; }
                continue;
            }
            if (uses != 1) continue;
            // the statement that uses it
            int j = i + 1;
            while (j < list.Count && !Exprs(list[j]).Concat(Nested(list[j])).Any(e => Mentions(e, t))) j++;
            if (j >= list.Count) continue;
            bool effects = !Pure(def.Rhs);
            bool ok = true;
            for (int k = i + 1; k < j && ok; k++)
            {
                if (effects) ok = list[k] is SAssign { Rhs: var r } && Pure(r) && r is not ECall;
                ok = ok && !Writes(list[k], def.Rhs);
            }
            if (!ok) continue;
            var target = list[j];
            // where the use sits in the target: in its own expressions (evaluated now) or nested (conditional, repeated)
            bool inOwn = Exprs(target).Any(e => Mentions(e, t));
            if (!inOwn && (effects || !StablePure(def.Rhs))) continue;
            if (!inOwn && target is SLoop) continue;
            if (effects && inOwn && !FirstEffect(target, t)) continue;
            // a copy into a local (v = t) cannot throw: its site is only the last one set, and the folded statement takes the value's
            bool copy = target is SAssign { Lhs: EVar, Rhs: EVar { V: var cv } } && cv == t;
            if (effects && def.Site is not null && target.Site is not null && def.Site != target.Site && !copy && !Total(def.Rhs) && CheckedSite(def.Rhs) != target.Site)
            {
                // t.add(x) ... with t = a.subtract(b) of another site: a.subtract(b) then .add(x) on the next line, which keeps its site
                // (javac gives each call the line of its parenthesis)
                if (!Chains || ChainHead(target, t) is not { } head) continue;
                head.LineSite = target.Site;
                target.Site = def.Site;
            }
            if (effects && def.Site is not null && (target.Site is null || copy) && target is not SReturn) target.Site = def.Site;
            Substitute(target, t, def.Rhs);
            list.RemoveAt(i);
            i--;
            changed = true;
        }
        return list;
    }

    /// <summary>A call that cannot throw (BigDecimal.valueOf of an int or a long): where it is evaluated has no site of its own.</summary>
    static bool Total(Expr e) => e is ECall { Kind: CallKind.JdkStatic, Owner: "java.math.BigDecimal", Name: "valueOf" } c
        && c.Args.All(a => Pure(a) && (a is EVar v ? v.V.Kind is VKind.Int or VKind.Long || v.V.Type?.Name is "int" or "long" : a.Type?.Name is "int" or "long"));

    /// <summary>
    /// The site a checked decimal operation names (DecimalLimit.op(..., "site")): the legacy guard of the operation itself,
    /// lifted before the guard set the statement's site.
    /// </summary>
    static string? CheckedSite(Expr e) => e is ECall { Kind: CallKind.JdkStatic, Owner: { } o, Args: [.., ELit { Value: string s }] }
        && o.EndsWith(".DecimalLimit", StringComparison.Ordinal) ? s : null;

    /// <summary>The call whose receiver is t at the head of the statement's call chain (x = t.m(a).n(b), return t.m(), t.m()), or null.</summary>
    static ECall? ChainHead(St target, Var t)
    {
        var top = target switch { SAssign { Lhs: EVar } a => a.Rhs, SExpr e => e.E, SReturn { E: { } r } => r, _ => null };
        for (var x = top; x is ECall { Recv: { } recv } c; x = recv)
            if (recv is EVar ev && ev.V == t) return c.Kind is CallKind.Instance or CallKind.Jdk && c.LineSite is null ? c : null;
        return null;
    }

    /// <summary>t = u (t a temporary assigned once, u never reassigned): every use of t is u.</summary>
    static List<St> CopyPropagate(List<St> body, Counts counts, ref bool changed)
    {
        var map = new Dictionary<Var, Var>();
        var eachVars = Passes.All(body).OfType<SLoop>().Where(l => l.Each is not null).Select(l => l.Each!).ToHashSet();
        foreach (var s in Passes.All(body))
            if (s is SAssign { Lhs: EVar { V: var t }, Rhs: EVar { V: var u } } && t != u && t.IsTemp && counts.Defs.GetValueOrDefault(t) == 1 && counts.Defs.GetValueOrDefault(u) <= 1 && !u.IsThis && !eachVars.Contains(u))
                map[t] = u;
        if (map.Count == 0) return body;
        Var Final(Var v) { int n = 0; while (map.TryGetValue(v, out var w) && n++ < 50) v = w; return v; }
        foreach (var s in Passes.All(body))
            foreach (var t in map.Keys)
                SubstituteOwn(s, t, new EVar(Final(t)));
        changed = true;
        return Remove(body, map);
    }

    static List<St> Remove(List<St> list, Dictionary<Var, Var> map)
    {
        var res = new List<St>();
        foreach (var s in list)
        {
            if (s is SAssign { Lhs: EVar { V: var t } } && map.ContainsKey(t)) continue;
            switch (s)
            {
                case SIf i: i.Then = Remove(i.Then, map); i.Else = Remove(i.Else, map); break;
                case SLoop l: l.Body = Remove(l.Body, map); if (l.Each is not null && map.ContainsKey(l.Each)) { } break;
                case SBlock b: b.Body = Remove(b.Body, map); break;
                case SSwitch sw:
                    for (int k = 0; k < sw.Cases.Count; k++) sw.Cases[k] = (sw.Cases[k].Labels, Remove(sw.Cases[k].Body, map));
                    if (sw.Default is not null) sw.Default = Remove(sw.Default, map);
                    break;
                case STry tr: tr.Body = Remove(tr.Body, map); break;
            }
            res.Add(s);
        }
        return res;
    }

    static IEnumerable<Expr> Nested(St s) => s switch
    {
        SIf i => Passes.All(i.Then).Concat(Passes.All(i.Else)).SelectMany(Exprs),
        SLoop l => Passes.All(l.Body).SelectMany(Exprs).Concat(l.Iterable is null ? [] : [l.Iterable]),
        SBlock b => Passes.All(b.Body).SelectMany(Exprs),
        SSwitch sw => sw.Cases.SelectMany(c => Passes.All(c.Body)).Concat(sw.Default is null ? [] : Passes.All(sw.Default)).SelectMany(Exprs),
        STry t => Passes.All(t.Body).SelectMany(Exprs),
        _ => [],
    };

    /// <summary>No side effect and no exception beyond a null dereference Java raises at the same place.</summary>
    public static bool Pure(Expr e) => e switch
    {
        // creating a lambda has no effect and cannot throw; it reads its captures
        ELambda l => l.Captures.Values.All(Pure),
        ECall or ENew or EArrayNew or EOpaque => false,
        EBinary { Op: "/" or "%" } => false,
        _ => e.Children.All(Pure),
    };

    /// <summary>Pure and independent of mutable state (literals, this, parameters and locals not written).</summary>
    static bool StablePure(Expr e) => e switch
    {
        ELit or EThis or EStaticJdk => true,
        EVar => true,
        EField => false,
        EStatic => false,
        _ => Pure(e) && e.Children.All(StablePure),
    };

    /// <summary>Whether statement s writes a variable or field that e reads.</summary>
    static bool Writes(St s, Expr e)
    {
        foreach (var w in Passes.All([s]))
        {
            if (w is SAssign a)
            {
                if (a.Lhs is EVar lv && Mentions(e, lv.V)) return true;
                if (a.Lhs is EField lf && Reads(e, x => x is EField f && f.F == lf.F)) return true;
                if (a.Lhs is EStatic ls && Reads(e, x => x is EStatic st && st.F == ls.F)) return true;
                if (a.Lhs is EIndex && Reads(e, x => x is EIndex)) return true;
            }
            if (w is SExpr or SAssign { Rhs: ECall } && Reads(e, x => x is EField or EStatic or EIndex)) return true;
        }
        return false;
    }

    static bool Reads(Expr e, Func<Expr, bool> p) => p(e) || e.Children.Any(c => Reads(c, p));

    /// <summary>In evaluation order, the use of t comes before any other call or allocation in the statement.</summary>
    static bool FirstEffect(St s, Var t)
    {
        var order = new List<Expr>();
        foreach (var e in Exprs(s)) Order(e, order);
        foreach (var e in order)
        {
            if (e is EVar v && v.V == t) return true;
            if (e is ECall or ENew or EArrayNew || e is EBinary { Op: "/" or "%" }) return false;
        }
        return false;
    }

    /// <summary>Java evaluation order: operands left to right, then the operation (post-order).</summary>
    static void Order(Expr e, List<Expr> acc)
    {
        if (e is ECond c) { Order(c.C, acc); acc.Add(e); return; } // branches are conditional
        if (e is EBinary { Op: "&&" or "||" } b) { Order(b.L, acc); acc.Add(e); return; }
        foreach (var ch in e.Children) Order(ch, acc);
        acc.Add(e);
    }

    /// <summary>Replaces the variable by the value in the statement and every statement nested in it (each visited once).</summary>
    static void Substitute(St s, Var t, Expr value)
    {
        foreach (var x in Passes.All([s])) SubstituteOwn(x, t, value);
    }

    public static void SubstituteOwn(St s, Var t, Expr value)
    {
        Expr R(Expr e) => e is EVar ev && ev.V == t ? value : Mentions(e, t) ? e.Map(R) : e;
        switch (s)
        {
            case SAssign a: if (a.Lhs is not EVar) a.Lhs = R(a.Lhs); a.Rhs = R(a.Rhs); break;
            case SExpr e: e.E = R(e.E); break;
            case SReturn r when r.E is not null: r.E = R(r.E); break;
            case SThrow th: th.E = R(th.E); break;
            case SIf i: i.C = R(i.C); break;
            case SLoop l:
                if (l.C is not null) l.C = R(l.C);
                if (l.Iterable is not null) l.Iterable = R(l.Iterable);
                break;
            case SSwitch sw: sw.Subject = R(sw.Subject); break;
        }
    }

    // ================================================================== materialized booleans and ternaries

    static List<St> Booleans(List<St> list, ref bool changed)
    {
        for (int i = 0; i < list.Count; i++)
        {
            // if (c) { if (d) { S } }  →  if (c && d) { S }
            if (list[i] is SIf { Else.Count: 0, Then: [SIf { Else.Count: 0 } inner] } outer)
            {
                outer.C = FunctionLifter.And(outer.C, inner.C);
                outer.Then = inner.Then;
                changed = true;
            }
            // t = false; if (c) { t = v }  →  t = c && v   (and the true / || form)
            if (i + 1 < list.Count && list[i] is SAssign { Lhs: EVar { V: var bt }, Rhs: ELit { Value: bool b0 } } init && bt.IsTemp
                && list[i + 1] is SIf { Else.Count: 0, Then: [SAssign { Lhs: EVar { V: var bt2 } } setv] } cond && bt2 == bt && !Mentions(cond.C, bt))
            {
                var v = b0 ? FunctionLifter.Or(FunctionLifter.Not(cond.C), setv.Rhs) : FunctionLifter.And(cond.C, setv.Rhs);
                list[i] = new SAssign(new EVar(bt), v) { Line = init.Line, Site = setv.Site ?? init.Site };
                list.RemoveAt(i + 1);
                changed = true;
            }
            switch (list[i])
            {
                case SIf f:
                    f.Then = Booleans(f.Then, ref changed);
                    f.Else = Booleans(f.Else, ref changed);
                    if (Materialized(f) is { } m) { list[i] = m; changed = true; }
                    break;
                case SLoop l: l.Body = Booleans(l.Body, ref changed); break;
                case SBlock b: b.Body = Booleans(b.Body, ref changed); break;
                case SSwitch sw:
                    for (int k = 0; k < sw.Cases.Count; k++) { var bb = sw.Cases[k].Body; sw.Cases[k] = (sw.Cases[k].Labels, Booleans(bb, ref changed)); }
                    if (sw.Default is not null) sw.Default = Booleans(sw.Default, ref changed);
                    break;
                case STry t: t.Body = Booleans(t.Body, ref changed); break;
            }
        }
        return list;
    }

    /// <summary>if (c) { t = a } else { t = b } (each branch one assignment to the same temporary) → t = (c ? a : b), with && and || forms.</summary>
    static SAssign? Materialized(SIf f)
    {
        if (f.Then is not [SAssign { Lhs: EVar { V: var t1 } } a] || f.Else is not [SAssign { Lhs: EVar { V: var t2 } } b] || t1 != t2 || !t1.IsTemp) return null;
        Expr v;
        if (a.Rhs is ELit { Value: true } && b.Rhs is ELit { Value: false }) v = f.C;
        else if (a.Rhs is ELit { Value: false } && b.Rhs is ELit { Value: true }) v = FunctionLifter.Not(f.C);
        else if (b.Rhs is ELit { Value: false } && IsBool(a.Rhs)) v = FunctionLifter.And(f.C, a.Rhs);
        else if (a.Rhs is ELit { Value: true } && IsBool(b.Rhs)) v = FunctionLifter.Or(f.C, b.Rhs);
        else if (a.Rhs is ELit { Value: false } && IsBool(b.Rhs)) v = FunctionLifter.And(FunctionLifter.Not(f.C), b.Rhs);
        else if (b.Rhs is ELit { Value: true } && IsBool(a.Rhs)) v = FunctionLifter.Or(FunctionLifter.Not(f.C), a.Rhs);
        else v = new ECond(f.C, a.Rhs, b.Rhs);
        return new SAssign(new EVar(t1), v) { Line = f.Line, Site = a.Site ?? b.Site ?? f.Site };
    }

    static bool IsBool(Expr e) => e.Type?.Name == "boolean" || e is EBinary { Op: "==" or "!=" or "<" or "<=" or ">" or ">=" or "&&" or "||" } || e is EUnary { Op: "!" } || e is EInstanceOf
        || e is EVar { V.Kind: VKind.Bool } || e is ECall { Type.Name: "boolean" };
}
