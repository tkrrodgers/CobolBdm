namespace MlCpu.Modernize;

/// <summary>Clean-ups on structured statements: from the structurer's loops and blocks to the code a person writes.</summary>
public static class Passes
{
    // ================================================================== control flow

    /// <summary>The structurer's loops exit when their body ends (as WebAssembly loops); Java loops repeat: add the break.</summary>
    public static void FixLoops(List<St> list)
    {
        foreach (var s in list) Children(s, FixLoops);
        foreach (var l in list.OfType<SLoop>())
            if (CanComplete(l.Body)) l.Body.Add(new SBreak(l.Label));
    }

    static void Children(St s, Action<List<St>> f)
    {
        switch (s)
        {
            case SIf i: f(i.Then); f(i.Else); break;
            case SLoop l: f(l.Body); break;
            case SBlock b: f(b.Body); break;
            case SSwitch sw: foreach (var c in sw.Cases) f(c.Body); if (sw.Default is not null) f(sw.Default); break;
            case STry t: f(t.Body); foreach (var c in t.Catches) f(c.Body); break;
        }
    }

    public static bool CanComplete(List<St> list)
    {
        if (list.Count == 0) return true;
        return CanComplete(list[^1]) && list.Take(list.Count - 1).All(CanCompleteOrJumps);
    }

    static bool CanCompleteOrJumps(St s) => true;

    public static bool CanComplete(St s) => s switch
    {
        SReturn or SThrow or SBreak or SContinue => false,
        SIf i => CanComplete(i.Then) || CanComplete(i.Else),
        SBlock b => CanComplete(b.Body) || Targets(b.Body, b.Label),
        SLoop l => l.C is not null || l.Each is not null || Targets(l.Body, l.Label),
        SSwitch sw => sw.Default is null || sw.Cases.Any(c => CanComplete(c.Body)) || CanComplete(sw.Default) || sw.Cases.Any(c => HasPlainBreak(c.Body)),
        STry t => CanComplete(t.Body) || t.Catches.Any(c => CanComplete(c.Body)),
        _ => true,
    };

    static bool HasPlainBreak(List<St> body) => All(body).Any(s => s is SBreak { Label: null });

    /// <summary>Whether a break to <paramref name="label"/> occurs inside the list.</summary>
    public static bool Targets(List<St> list, string label) => All(list).Any(s => s is SBreak b && b.Label == label);

    /// <summary>Every statement of the list and of the lists nested in it, in source order (an explicit stack: deep trees stay linear).</summary>
    public static IEnumerable<St> All(List<St> list)
    {
        var stack = new Stack<(List<St> L, int I)>();
        stack.Push((list, 0));
        while (stack.Count > 0)
        {
            var (l, i) = stack.Pop();
            if (i >= l.Count) continue;
            stack.Push((l, i + 1));
            var s = l[i];
            yield return s;
            var kids = new List<List<St>>();
            switch (s)
            {
                case SIf f: kids.Add(f.Then); kids.Add(f.Else); break;
                case SLoop lo:
                    if (lo.Init is not null) kids.Add([lo.Init]);
                    kids.Add(lo.Body);
                    if (lo.Update is not null) kids.Add([lo.Update]);
                    break;
                case SBlock b: kids.Add(b.Body); break;
                case SSwitch sw: foreach (var c in sw.Cases) kids.Add(c.Body); if (sw.Default is not null) kids.Add(sw.Default); break;
                case STry t: kids.Add(t.Body); foreach (var c in t.Catches) kids.Add(c.Body); break;
            }
            for (int k = kids.Count - 1; k >= 0; k--) stack.Push((kids[k], 0));
        }
    }

    /// <summary>Repeats the control-flow clean-ups until nothing changes.</summary>
    public static List<St> Tidy(List<St> list)
    {
        for (int round = 0; round < 12; round++)
        {
            bool changed = false;
            list = TidyList(list, null, null, ref changed);
            if (!changed) break;
        }
        return list;
    }

    /// <summary>
    /// One pass. <paramref name="loopLabel"/>: the loop whose body this list ends (a trailing continue to it is redundant);
    /// <paramref name="blockLabel"/>: the block this list ends (a trailing break to it is redundant).
    /// </summary>
    static List<St> TidyList(List<St> list, string? loopLabel, string? blockLabel, ref bool changed)
    {
        var res = new List<St>();
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            bool last = i == list.Count - 1;
            switch (s)
            {
                case SIf f:
                {
                    var lp = last ? loopLabel : null;
                    var bl = last ? blockLabel : null;
                    f.Then = TidyList(f.Then, lp, bl, ref changed);
                    f.Else = TidyList(f.Else, lp, bl, ref changed);
                    if (f.Then.Count == 0 && f.Else.Count == 0) { changed = true; continue; }
                    if (f.Then.Count == 0) { f.C = FunctionLifter.Not(f.C); f.Then = f.Else; f.Else = []; changed = true; }
                    // if (c) { S; A } else { S; B }  →  S; if (c) { A } else { B }   (S the same, c pure and not changed by S)
                    while (f.Then.Count > 0 && f.Else.Count > 0 && f.Then[0] is SAssign { Lhs: EVar { V: var hv } } h1 && Same(h1, f.Else[0])
                        && Fold.Pure(h1.Rhs) && Fold.Pure(f.C) && !Fold.Mentions(f.C, hv))
                    {
                        res.Add(h1);
                        f.Then.RemoveAt(0);
                        f.Else.RemoveAt(0);
                        changed = true;
                    }
                    if (f.Then.Count == 0 && f.Else.Count == 0) { changed = true; continue; }
                    if (f.Then.Count == 0) { f.C = FunctionLifter.Not(f.C); f.Then = f.Else; f.Else = []; changed = true; }
                    // if (!x) { x = true; }  →  x = true;
                    if (f.Else.Count == 0 && f.C is EUnary { Op: "!", E: EVar { V: var fx } } && f.Then is [SAssign { Lhs: EVar { V: var fy }, Rhs: ELit { Value: true } } set1] && fx == fy)
                    {
                        res.Add(set1);
                        changed = true;
                        continue;
                    }
                    // if (c) { A } else { ...jump }  →  if (!c) { ...jump } A   (early exit first, then the main line)
                    if (f.Else.Count > 0 && !CanComplete(f.Else) && (CanComplete(f.Then) || All(f.Else).Count() < All(f.Then).Count()))
                    {
                        f.C = FunctionLifter.Not(f.C);
                        (f.Then, f.Else) = (f.Else, f.Then);
                        changed = true;
                    }
                    res.Add(f);
                    // if (c) { ...jump } else { B }  →  if (c) { ...jump } B
                    if (f.Else.Count > 0 && !CanComplete(f.Then))
                    {
                        res.AddRange(f.Else);
                        f.Else = [];
                        changed = true;
                    }
                    break;
                }
                case SLoop l:
                {
                    l.Body = TidyList(l.Body, l.Label, null, ref changed);
                    // if (c) { A; continue L } R, R leaving the loop and no longer than A  →  if (!c) { R } A   (the exit first, the main line flat)
                    for (int k = 0; k < l.Body.Count - 1; k++)
                    {
                        if (l.Body[k] is not SIf { Else.Count: 0 } fi || fi.Then is not [.., SContinue { Label: var cl }] || cl != l.Label) continue;
                        var rest = l.Body.Skip(k + 1).ToList();
                        var main = fi.Then.Take(fi.Then.Count - 1).ToList();
                        if (CanComplete(rest) || All(rest).Count() > All(main).Count()) continue;
                        fi.C = FunctionLifter.Not(fi.C);
                        fi.Then = rest;
                        l.Body = [.. l.Body.Take(k + 1), .. main];
                        changed = true;
                        break;
                    }
                    // while (true) { if (c) break L; rest }  →  while (!c) { rest }
                    if (l.C is null && l.Each is null && l.Body.Count > 0 && l.Body[0] is SIf { Else.Count: 0, Then: [SBreak bk] } head && bk.Label == l.Label)
                    {
                        l.C = FunctionLifter.Not(head.C);
                        l.Body.RemoveAt(0);
                        changed = true;
                    }
                    res.Add(l);
                    break;
                }
                case SBlock b:
                {
                    b.Body = TidyList(b.Body, last ? loopLabel : null, b.Label, ref changed);
                    if (!Targets(b.Body, b.Label))
                    {
                        // splice: nothing jumps to the end of this block (the body is tidied already)
                        res.AddRange(b.Body);
                        changed = true;
                    }
                    else res.Add(b);
                    break;
                }
                case SContinue c when last && c.Label == loopLabel:
                    changed = true;
                    continue;
                case SBreak br when last && br.Label == blockLabel && blockLabel is not null:
                    changed = true;
                    continue;
                case SSwitch sw:
                    for (int k = 0; k < sw.Cases.Count; k++) sw.Cases[k] = (sw.Cases[k].Labels, TidyList(sw.Cases[k].Body, null, null, ref changed));
                    if (sw.Default is not null) sw.Default = TidyList(sw.Default, null, null, ref changed);
                    res.Add(sw);
                    break;
                case STry t:
                    t.Body = TidyList(t.Body, null, null, ref changed);
                    for (int k = 0; k < t.Catches.Count; k++) t.Catches[k] = (t.Catches[k].Type, t.Catches[k].V, TidyList(t.Catches[k].Body, null, null, ref changed));
                    res.Add(t);
                    break;
                default:
                    res.Add(s);
                    break;
            }
            // if (c) { return false; } return true;  →  return !c;   (and the true/false form)
            if (res.Count >= 2 && res[^1] is SReturn { E: ELit { Value: bool rv } } last2 && res[^2] is SIf { Else.Count: 0, Then: [SReturn { E: ELit { Value: bool tv } }] } bif && rv != tv)
            {
                res.RemoveRange(res.Count - 2, 2);
                res.Add(new SReturn(tv ? bif.C : FunctionLifter.Not(bif.C)) { Line = bif.Line, Site = bif.Site });
                changed = true;
            }
            // nothing after a statement that cannot complete normally (unreachable in Java)
            if (res.Count > 0 && !CanComplete(res[^1]) && i < list.Count - 1)
            {
                changed = true;
                break;
            }
        }
        return res;
    }

    /// <summary>
    /// i = a; while (c) { body; i = i + k }, i used nowhere else and no continue to the loop  →  for (i = a; c; i = i + k) { body }.
    /// </summary>
    public static void Counting(List<St> list)
    {
        foreach (var s in list) ForChildren(s, Counting);
        for (int k = 0; k + 1 < list.Count; k++)
        {
            if (list[k] is not SAssign { Lhs: EVar { V: var v } } init || list[k + 1] is not SLoop { C: { } c, Each: null, Init: null } l) continue;
            if (l.Body is not [.., SAssign { Lhs: EVar { V: var uv }, Rhs: EBinary { Op: "+" or "-", L: EVar { V: var ul } } } upd] || uv != v || ul != v) continue;
            if (!Fold.Mentions(c, v) || !Fold.Pure(init.Rhs) || Fold.Mentions(init.Rhs, v)) continue;
            if (All(l.Body).Any(x => x is SContinue)) continue;
            var inner = l.Body.Take(l.Body.Count - 1).ToList();
            if (All(inner).Any(x => x is SAssign { Lhs: EVar { V: var w } } && w == v)) continue;
            if (list.Where((x, i) => i != k && i != k + 1).Any(x => Mentions(x, v))) continue;
            l.Init = init;
            l.Update = upd;
            l.Body = inner;
            list.RemoveAt(k);
        }
    }

    static bool Mentions(St s, Var v) => All([s]).Any(x => Fold.Exprs(x).Any(e => Fold.Mentions(e, v)) || x is SAssign { Lhs: EVar { V: var w } } && w == v
        || x is SLoop { Init.Lhs: EVar { V: var w2 } } && w2 == v || x is STry tr && tr.Catches.Any(cc => cc.V == v));

    /// <summary>
    /// J3S keeps a lambda's parameters and a loop's element in the frame slots named after the Java variables (rp = p1):
    /// the parameter or loop variable itself, under that name. A local nobody reads goes with its pure values; a local
    /// copied from a variable and copied on at once (an inlined lambda's parameter: second = e; acc = second) is the variable.
    /// </summary>
    public static void Coalesce(Lifted l, List<St> body)
    {
        if (l.Fn.IsLambda)
            while (body.Count > 0 && body[0] is SAssign { Lhs: EVar { V: var x }, Rhs: EVar { V: var p } } && l.Params.Contains(p)
                && Local(x) && Defs(body, x) == 1 && Defs(body, p) == 0)
            {
                body.RemoveAt(0);
                Rename(body, x, p);
            }
        foreach (var s in All(body).ToList())
            if (s is SLoop { Each: { } e } lp && lp.Body is [SAssign { Lhs: EVar { V: var x2 }, Rhs: EVar { V: var y } }, ..] && y == e
                && Local(x2) && Defs(body, x2) == 1 && Defs(lp.Body, e) == 0 && Reads(body, x2) == Reads(lp.Body, x2))
            {
                lp.Body.RemoveAt(0);
                Rename(lp.Body, x2, e);
            }
        DropCopies(body, body);
    }

    static void DropCopies(List<St> list, List<St> body)
    {
        foreach (var s in list) ForChildren(s, c => DropCopies(c, body));
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is not SAssign { Lhs: EVar { V: var x } } a || !Local(x)) continue;
            int reads = Reads(body, x);
            // nobody reads it: a pure value goes, a call stays for its effect
            if (reads == 0)
            {
                if (Fold.Pure(a.Rhs)) { list.RemoveAt(i); i--; }
                else if (a.Rhs is ECall or ENew) list[i] = new SExpr(a.Rhs) { Line = a.Line, Site = a.Site };
                continue;
            }
            // money = zero; for (...) money = money.plus(..); totalDueIn = money  →  the temporary is totalDueIn from the start
            // (a temporary assigned here: a loop's element or a catch's exception is bound by its statement, not renamed)
            if (Defs(body, x) == 1 && a.Rhs is EVar { V: var ty } && ty.IsTemp && !ty.IsParam && Defs(body, ty) > 0
                && !All(body).Any(s => s is SLoop { Each: var ev } && ev == ty || s is STry tr && tr.Catches.Any(cc => cc.V == ty))
                && Reads(body, ty) == Reads(list.Take(i + 1).ToList(), ty) && Defs(body, ty) == Defs(list.Take(i).ToList(), ty))
            {
                list.RemoveAt(i);
                i--;
                foreach (var s in All(body))
                {
                    Fold.SubstituteOwn(s, ty, new EVar(x));
                    if (s is SAssign { Lhs: EVar { V: var lv } } sa && lv == ty) sa.Lhs = new EVar(x) { Type = sa.Lhs.Type };
                }
                continue;
            }
            // m1 = acc; ...; acc = m1.plus(m2)  →  acc = acc.plus(m2)   (read once, later in this list, neither changed between)
            if (reads == 1 && Defs(body, x) == 1 && a.Rhs is EVar { V: var y } && !y.IsThis)
            {
                int j = i + 1;
                while (j < list.Count && !Fold.Exprs(list[j]).Any(e => Fold.Mentions(e, x))) j++;
                if (j >= list.Count || Reads([list[j]], x) != Fold.Exprs(list[j]).Count(e => Fold.Mentions(e, x))) continue;
                if (list.Skip(i + 1).Take(j - i - 1).Any(s => Defs([s], y) > 0 || Reads([s], x) > 0)) continue;
                Fold.SubstituteOwn(list[j], x, new EVar(y));
                list.RemoveAt(i);
                i--;
            }
        }
    }

    static bool Local(Var x) => !x.IsTemp && !x.IsParam && !x.IsThis;

    /// <summary>Two assignments of the same variable from the same expression (as printed).</summary>
    static bool Same(SAssign a, St b) => b is SAssign { Lhs: EVar { V: var v } } bb && a.Lhs is EVar { V: var u } && u == v && SameExpr(a.Rhs, bb.Rhs);

    /// <summary>Structural equality: the same variables, fields, functions, literals and operators.</summary>
    static bool SameExpr(Expr a, Expr b)
    {
        if (a.GetType() != b.GetType()) return false;
        bool leaf = (a, b) switch
        {
            (EVar x, EVar y) => x.V == y.V,
            (EThis, EThis) => true,
            (ELit x, ELit y) => Equals(x.Value, y.Value),
            (EField x, EField y) => x.F == y.F,
            (EStatic x, EStatic y) => x.F == y.F,
            (ECall x, ECall y) => x.Fn == y.Fn && x.Name == y.Name && x.Kind == y.Kind && x.Owner == y.Owner && (x.Recv is null) == (y.Recv is null),
            (ENew x, ENew y) => x.Class == y.Class && x.JdkClass == y.JdkClass && x.Ctor == y.Ctor,
            (EUnary x, EUnary y) => x.Op == y.Op,
            (EBinary x, EBinary y) => x.Op == y.Op,
            (ECast x, ECast y) => Equals(x.T, y.T),
            (ECond, ECond) => true,
            _ => false,
        };
        if (!leaf) return false;
        var ca = a.Children.ToList();
        var cb = b.Children.ToList();
        return ca.Count == cb.Count && ca.Zip(cb).All(p => SameExpr(p.First, p.Second));
    }

    static int Defs(List<St> body, Var v) => All(body).Count(s => s is SAssign { Lhs: EVar { V: var w } } && w == v);

    static int Reads(List<St> body, Var v) => All(body).Sum(s => Fold.Exprs(s).Count(e => Fold.Mentions(e, v)));

    static void Rename(List<St> body, Var from, Var to)
    {
        foreach (var s in All(body)) Fold.SubstituteOwn(s, from, new EVar(to));
        to.Name = from.Name;
        to.Hint = from.Hint;
        to.IsTemp = false;
    }

    /// <summary>
    /// a = new Object[n]; a[0] = x; ...; logger.log(level, msg, a)  →  logger.log(level, msg, x, ...): the varargs call
    /// builds the array. Only values that cannot throw (variables, literals, constants), so no reject site moves.
    /// </summary>
    public static void Varargs(List<St> body) => Varargs(body, body);

    static void Varargs(List<St> list, List<St> body)
    {
        foreach (var s in list) ForChildren(s, c => Varargs(c, body));
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is not SAssign { Lhs: EVar { V: var t }, Rhs: EArrayNew { Elem.Name: "java.lang.Object", Len: ELit { Value: long or int } ln } }) continue;
            int n = Convert.ToInt32(ln.Value);
            if (n < 2 || i + n + 1 >= list.Count) continue;
            var items = new List<Expr>();
            for (int k = 0; k < n; k++)
                if (list[i + 1 + k] is SAssign { Lhs: EIndex { A: EVar { V: var tv }, I: ELit { Value: long or int } kv }, Rhs: var e } && tv == t && Convert.ToInt32(kv.Value) == k && Simple(e))
                    items.Add(e);
            if (items.Count != n) continue;
            var top = list[i + n + 1] switch { SExpr se => se.E, SReturn { E: { } re } => re, SAssign { Lhs: EVar } sa => sa.Rhs, _ => null };
            if (top is not ECall call || !VarargsCall(call) || call.Args is not [.., EVar { V: var av }] || av != t || Reads(body, t) != n + 1) continue;
            call.Args.RemoveAt(call.Args.Count - 1);
            call.Args.AddRange(items);
            list.RemoveRange(i, n + 1);
        }
    }

    /// <summary>A JDK method whose last parameter is Object... (or T...): the logger's log, Objects.hash, String.format, Arrays.asList, List.of.</summary>
    static bool VarargsCall(ECall c) =>
        c.Kind == CallKind.Jdk && c.Name == "log"
        || c.Kind == CallKind.JdkStatic && (c.Owner, c.Name) is ("java.util.Objects", "hash") or ("java.lang.String", "format") or ("java.util.Arrays", "asList") or ("java.util.List", "of");

    static bool Simple(Expr e) => e switch
    {
        EVar or ELit or EStatic or EStaticJdk or EField { Obj: EThis } => true,
        ECast c => Simple(c.E),
        _ => false,
    };

    /// <summary>break L / continue L to the innermost loop with no switch between: plain break / continue (the label is then unused).</summary>
    public static void Unlabel(List<St> list) { foreach (var s in list) Unlabel(s, null); }

    static void Unlabel(St s, string? inner)
    {
        switch (s)
        {
            case SBreak b when b.Label is not null && b.Label == inner: b.Label = null; break;
            case SContinue c when c.Label is not null && c.Label == inner: c.Label = null; break;
            case SLoop l: foreach (var x in l.Body) Unlabel(x, l.Label); break;
            case SSwitch sw:
                foreach (var cs in sw.Cases) foreach (var x in cs.Body) Unlabel(x, null);
                if (sw.Default is not null) foreach (var x in sw.Default) Unlabel(x, null);
                break;
            case SIf f: foreach (var x in f.Then) Unlabel(x, inner); foreach (var x in f.Else) Unlabel(x, inner); break;
            case SBlock bl: foreach (var x in bl.Body) Unlabel(x, inner); break;
            case STry t: foreach (var x in t.Body) Unlabel(x, inner); foreach (var cc in t.Catches) foreach (var x in cc.Body) Unlabel(x, inner); break;
        }
    }

    static void ForChildren(St s, Action<List<St>> f)
    {
        switch (s)
        {
            case SIf i: f(i.Then); f(i.Else); break;
            case SLoop l: f(l.Body); break;
            case SBlock b: f(b.Body); break;
            case SSwitch sw: foreach (var cs in sw.Cases) f(cs.Body); if (sw.Default is not null) f(sw.Default); break;
            case STry t: f(t.Body); foreach (var cc in t.Catches) f(cc.Body); break;
        }
    }

    /// <summary>Removes labels nobody targets (the emitter prints a label only when used).</summary>
    public static HashSet<string> UsedLabels(List<St> body)
    {
        var used = new HashSet<string>();
        foreach (var s in All(body))
        {
            if (s is SBreak { Label: { } b }) used.Add(b);
            if (s is SContinue { Label: { } c }) used.Add(c);
        }
        return used;
    }
}
