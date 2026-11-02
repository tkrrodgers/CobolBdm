namespace MlCpu.Modernize;

/// <summary>
/// J3S's expansion of equals on a final JDK value class (Boolean, String, a boxed number), back to the call:
/// <c>r = false; if (b !=C a) return r; if (a.equals(b)) r = true; return r;</c> (<c>!=C</c>: b null or of another class)
/// with <c>a</c> a literal's variable is <c>return a.equals(b);</c>: the final class's equals checks the same class itself.
/// </summary>
public static class ValueEquals
{
    public static void Run(List<St> body)
    {
        var literals = new HashSet<Var>();
        var defs = new Dictionary<Var, int>();
        foreach (var s in Passes.All(body))
            if (s is SAssign { Lhs: EVar { V: var v } } a)
            {
                defs[v] = defs.GetValueOrDefault(v) + 1;
                if (a.Rhs is ELit { Value: bool or string or long }) literals.Add(v);
            }
        literals.RemoveWhere(v => defs[v] != 1);
        if (literals.Count > 0) Rewrite(body, body, literals);
    }

    static void Rewrite(List<St> list, List<St> body, HashSet<Var> literals)
    {
        foreach (var s in list)
            switch (s)
            {
                case SIf f: Rewrite(f.Then, body, literals); Rewrite(f.Else, body, literals); break;
                case SLoop l: Rewrite(l.Body, body, literals); break;
                case SBlock b: Rewrite(b.Body, body, literals); break;
                case STry t: Rewrite(t.Body, body, literals); foreach (var c in t.Catches) Rewrite(c.Body, body, literals); break;
                case SSwitch sw: foreach (var c in sw.Cases) Rewrite(c.Body, body, literals); if (sw.Default is not null) Rewrite(sw.Default, body, literals); break;
            }
        for (int i = 0; i + 3 < list.Count; i++)
        {
            if (list[i] is not SAssign { Lhs: EVar { V: var r }, Rhs: ELit { Value: false } }) continue;
            if (list[i + 1] is not SIf { Else.Count: 0, Then: [SReturn { E: EVar { V: var r1 } }], C: EBinary { Op: "!=C", L: EVar { V: var b0 }, R: EVar { V: var a1 } } }) continue;
            var b1 = b0;
            if (list[i + 2] is not SIf { Else.Count: 0, Then: [SAssign { Lhs: EVar { V: var r2 }, Rhs: ELit { Value: true } }], C: ECall { Name: "equals", Recv: EVar { V: var a2 }, Args: [EVar { V: var b2 }] } eq }) continue;
            if (list[i + 3] is not SReturn { E: EVar { V: var r3 } } ret) continue;
            if (r1 != r || r2 != r || r3 != r || b1 != b0 || b2 != b0 || a2 != a1 || !literals.Contains(a1)) continue;
            // the result variable is these four statements' only
            if (Passes.All(body).Count(x => Fold.Exprs(x).Any(e => Fold.Mentions(e, r)) || x is SAssign { Lhs: EVar { V: var lv } } && lv == r) != 4) continue;
            list.RemoveRange(i, 4);
            list.Insert(i, new SReturn(new ECall { Kind = eq.Kind, Owner = eq.Owner, Name = "equals", Recv = new EVar(a1), Args = [new EVar(b0)], Type = JType.Bool }) { Line = ret.Line, Site = ret.Site });
        }
    }
}
