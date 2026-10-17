namespace MlCpu.Modernize;

/// <summary>Loop shapes: exits after the loop, J3S's fused stream loops as for-each, optional sources as if.</summary>
public static class LoopPasses
{
    /// <summary>Loop variables made by the for-each rewrite and the element variable they feed (types unify).</summary>
    [ThreadStatic] static List<(Var Each, Var Element)>? eachVarsTs;
    static List<(Var Each, Var Element)> eachVars => eachVarsTs ??= [];
    public static List<(Var Each, Var Element)> TakeEachVars() { var l = eachVars.ToList(); eachVars.Clear(); return l; }

    public static List<St> Run(List<St> body)
    {
        for (int round = 0; round < 6; round++)
        {
            bool changed = false;
            body = Hoist(body, ref changed);
            body = ForEach(body, ref changed);
            if (!changed) break;
        }
        Reduce(body);
        return body;
    }

    /// <summary>
    /// A fused reduce: found = false; for (e : c) { ... if (found) { acc = op(acc, e) } else { acc = e; found = true } } ...
    /// Optional.of(acc). J3S reuses the accumulator's statement temporary from an earlier statement; the flag proves the earlier
    /// value is never read here, so the accumulator gets a variable of its own in the loop and its result.
    /// </summary>
    static void Reduce(List<St> list)
    {
        foreach (var s in list) foreach (var k in Kids(s)) Reduce(k);
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is not SAssign { Lhs: EVar { V: var flag }, Rhs: ELit { Value: false } } || !flag.IsTemp) continue;
            // the statement that sets the flag (a loop or an if), with the values set beside it
            int j = i + 1;
            while (j < list.Count && !Passes.All([list[j]]).Any(x => IsSet(x, flag))) j++;
            if (j >= list.Count || list[j] is not (SLoop or SIf)) continue;
            var accs = new HashSet<Var>();
            foreach (var kid in Passes.All([list[j]]).SelectMany(Kids).Prepend(Kids(list[j]).FirstOrDefault() ?? []))
                if (kid.Any(x => IsSet(x, flag)))
                    foreach (var a in kid.OfType<SAssign>())
                        if (a.Lhs is EVar { V: var av } && av != flag && av.IsTemp) accs.Add(av);
            // the uses after it must be guarded by the flag: the next statement tests it
            if (j + 1 >= list.Count || !Fold.Exprs(list[j + 1]).Any(e => Fold.Mentions(e, flag)) && !Passes.All([list[j + 1]]).SelectMany(Fold.Exprs).Any(e => Fold.Mentions(e, flag))) continue;
            foreach (var acc in accs)
            {
                var fresh = new Var { Name = acc.Name, Kind = acc.Kind, Type = acc.Type, IsTemp = true };
                Rename(list[j], acc, fresh);
                for (int k = j + 1; k < list.Count; k++)
                {
                    if (list[k] is SAssign { Lhs: EVar { V: var dv } } && dv == acc) break;
                    Rename(list[k], acc, fresh);
                }
            }
        }
    }

    static bool IsSet(St s, Var flag) => s is SAssign { Lhs: EVar { V: var v }, Rhs: ELit { Value: true } } && v == flag;

    static void Rename(St root, Var from, Var to)
    {
        foreach (var st in Passes.All([root]))
        {
            Fold.SubstituteOwn(st, from, new EVar(to));
            if (st is SAssign { Lhs: EVar { V: var lv } } la && lv == from) la.Lhs = new EVar(to);
        }
    }

    static IEnumerable<List<St>> Kids(St s) => s switch
    {
        SIf i => [i.Then, i.Else],
        SLoop l => [l.Body],
        SBlock b => [b.Body],
        SSwitch sw => sw.Cases.Select(c => c.Body).Concat(sw.Default is null ? [] : [sw.Default]),
        STry t => new[] { t.Body }.Concat(t.Catches.Select(c => c.Body)),
        _ => [],
    };

    static void Rebuild(St s, Func<List<St>, List<St>> f)
    {
        switch (s)
        {
            case SIf i: i.Then = f(i.Then); i.Else = f(i.Else); break;
            case SLoop l: l.Body = f(l.Body); break;
            case SBlock b: b.Body = f(b.Body); break;
            case SSwitch sw:
                for (int k = 0; k < sw.Cases.Count; k++) sw.Cases[k] = (sw.Cases[k].Labels, f(sw.Cases[k].Body));
                if (sw.Default is not null) sw.Default = f(sw.Default);
                break;
            case STry t:
                t.Body = f(t.Body);
                for (int k = 0; k < t.Catches.Count; k++) t.Catches[k] = (t.Catches[k].Type, t.Catches[k].V, f(t.Catches[k].Body));
                break;
        }
    }

    // ------------------------------------------------------------------ exits after the loop

    /// <summary>while (true) { if (c) { T } rest } with T leaving and no other exit → while (!c) { rest } T.</summary>
    static List<St> Hoist(List<St> list, ref bool changed)
    {
        bool ch = changed;
        foreach (var s in list) Rebuild(s, l => Hoist(l, ref ch));
        changed = ch;
        var res = new List<St>();
        foreach (var s in list)
        {
            if (s is SLoop { C: null, Each: null } l && l.Body.Count > 1 && l.Body[0] is SIf { Else.Count: 0 } head
                && !Passes.CanComplete(head.Then) && !TargetsLoop(head.Then, l.Label) && !Passes.Targets(l.Body.Skip(1).ToList(), l.Label))
            {
                var tail = head.Then;
                // a trailing break out of this very loop in T is just the end of T
                if (tail is [.., SBreak { Label: var bl }] && bl == l.Label) tail = tail.Take(tail.Count - 1).ToList();
                l.C = FunctionLifter.Not(head.C);
                l.Body.RemoveAt(0);
                res.Add(l);
                res.AddRange(tail);
                changed = true;
                continue;
            }
            // while (true) { if (c) { S } T } with T leaving: S runs while c holds and leaves the loop when it completes
            if (s is SLoop { C: null, Each: null } l2 && l2.Body.Count > 1 && l2.Body[0] is SIf { Else.Count: 0 } head2
                && !Passes.CanComplete(l2.Body.Skip(1).ToList()) && !Passes.Targets(l2.Body.Skip(1).ToList(), l2.Label) && !TargetsLoop(l2.Body.Skip(1).ToList(), l2.Label))
            {
                var tail = l2.Body.Skip(1).ToList();
                var inner = head2.Then;
                if (Passes.CanComplete(inner)) inner.Add(new SBreak(l2.Label));
                l2.C = head2.C;
                l2.Body = inner;
                res.Add(l2);
                res.AddRange(tail);
                changed = true;
                continue;
            }
            res.Add(s);
        }
        return res;
    }

    static bool TargetsLoop(List<St> list, string label) => Passes.All(list).Any(x => x is SContinue { Label: var c } && c == label);

    // ------------------------------------------------------------------ fused streams

    /// <summary>
    /// fence = c.j3sFence(); mc = c.j3sModCount(); i = 0; while (i &lt; fence) { e = c.j3sAt(i); i = i + 1; body } [CME check]
    ///   → for (e : c) { body }
    /// fence = opt.isPresent() ? 1 : 0; ...; while (i &lt; fence) { e = opt.get(); i = i + 1; body } → if (opt.isPresent()) { e = opt.get(); body }
    /// </summary>
    static List<St> ForEach(List<St> list, ref bool changed)
    {
        bool ch = changed;
        foreach (var s in list) Rebuild(s, l => ForEach(l, ref ch));
        changed = ch;
        for (int k = 0; k < list.Count; k++)
        {
            if (list[k] is not SLoop { Each: null, C: EBinary { Op: "<", L: EVar { V: var iv }, R: var fenceE } } loop) continue;
            if (loop.Body.Count < 2) continue;
            if (loop.Body[0] is not SAssign { Lhs: EVar { V: var ev }, Rhs: ECall { Name: "j3sAt" or "get" } at } first) continue;
            if (loop.Body[1] is not SAssign { Lhs: EVar { V: var iv2 }, Rhs: EBinary { Op: "+", L: EVar { V: var iv3 }, R: ELit { Value: 1L } } } || iv2 != iv || iv3 != iv) continue;
            // the index starts at 0 just before
            int initAt = FindDef(list, k, iv, d => d is ELit { Value: 0L });
            if (initAt < 0) continue;
            if (Passes.All(loop.Body.Skip(2).ToList()).SelectMany(Fold.Exprs).Any(e => Fold.Mentions(e, iv))) continue;
            var body = loop.Body.Skip(2).ToList();
            if (at.Name == "j3sAt" && at.Recv is { } coll)
            {
                // fence: the collection's j3sFence, before
                Var? fv = fenceE is EVar fvv ? fvv.V : null;
                int fenceAt = fv is null ? -1 : FindDef(list, k, fv, d => d is ECall { Name: "j3sFence" });
                int mcAt = FindDefAny(list, k, d => d is ECall { Name: "j3sModCount" }, out var mcVar);
                // the element used after the loop (findFirst): a loop variable of its own, the element assigned from it
                bool escapes = list.Skip(k + 1).Any(x => Passes.All([x]).SelectMany(Fold.Exprs).Any(e => Fold.Mentions(e, ev)) || x is SAssign { Lhs: EVar { V: var lv } } && lv == ev);
                if (escapes)
                {
                    var each = new Var { Name = ev.Name, Kind = ev.Kind, Type = ev.Type, IsTemp = false };
                    body.Insert(0, new SAssign(new EVar(ev), new EVar(each)) { Line = first.Line, Site = first.Site });
                    ev = each;
                    eachVars.Add((each, first.Lhs is EVar fe ? fe.V : each));
                }
                loop.Each = ev;
                loop.Iterable = coll;
                loop.C = null;
                // the per-element modification probes of short-circuiting terminals: the for-each iterator checks itself
                loop.Body = DropProbes(body);
                var remove = new HashSet<St>(ReferenceEqualityComparer.Instance) { list[initAt] };
                if (fenceAt >= 0) remove.Add(list[fenceAt]);
                if (mcAt >= 0) remove.Add(list[mcAt]);
                for (int p = k - 1; p >= 0 && p >= k - 6; p--)
                    if (list[p] is SExpr { E: ECall { Name: "j3sModCount" } }) remove.Add(list[p]);
                // the comodification check after the loop
                if (mcVar is not null && k + 1 < list.Count && list[k + 1] is SIf { C: var cc, Then: [SThrow] } && Fold.Mentions(cc, mcVar)) remove.Add(list[k + 1]);
                int before = list.Take(k).Count(x => remove.Contains(x));
                list.RemoveAll(x => remove.Contains(x));
                k -= before;
                changed = true;
                continue;
            }
            if (at.Name == "get" && at.Args.Count == 0 && at.Recv is { } opt && fenceE is EVar fv2)
            {
                int fenceAt = FindDef(list, k, fv2.V, d => d is ECond { A: ELit { Value: 1L }, B: ELit { Value: 0L } } || d is ECond { A: ELit { Value: 0L }, B: ELit { Value: 1L } });
                if (fenceAt < 0) continue;
                var fdef = (SAssign)list[fenceAt];
                var present = fdef.Rhs is ECond { A: ELit { Value: 1L } } c1 ? c1.C : FunctionLifter.Not(((ECond)fdef.Rhs).C);
                // the body runs once when the optional holds a value
                var then = new List<St> { first };
                then.AddRange(body.Where(b => !(b is SBreak sb && sb.Label == loop.Label)));
                if (Passes.Targets(then, loop.Label)) continue;
                list[k] = new SIf(present, then, []) { Line = loop.Line };
                int mcAt = FindDefAny(list, k, d => d is ELit { Value: 0L } && false, out _);
                foreach (var r in new[] { initAt, fenceAt }.OrderDescending()) { list.RemoveAt(r); if (r < k) k--; }
                changed = true;
            }
        }
        return list;
    }

    static List<St> DropProbes(List<St> list)
    {
        var res = new List<St>();
        foreach (var s in list)
        {
            if (s is SExpr { E: ECall { Name: "j3sModCount" } }) continue;
            Rebuild(s, DropProbes);
            res.Add(s);
        }
        return res;
    }

    static int FindDef(List<St> list, int before, Var v, Func<Expr, bool> rhs)
    {
        for (int i = before - 1; i >= 0; i--)
        {
            if (list[i] is SAssign { Lhs: EVar { V: var d } } a && d == v) return rhs(a.Rhs) ? i : -1;
            if (Fold.Exprs(list[i]).Any(e => Fold.Mentions(e, v))) return -1;
        }
        return -1;
    }

    static int FindDefAny(List<St> list, int before, Func<Expr, bool> rhs, out Var? v)
    {
        v = null;
        for (int i = before - 1; i >= 0 && i >= before - 6; i--)
            if (list[i] is SAssign { Lhs: EVar { V: var d } } a && rhs(a.Rhs)) { v = d; return i; }
        return -1;
    }
}
