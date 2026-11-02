namespace MlCpu.Modernize;

/// <summary>
/// A program's abend routine (P4): a paragraph that edits the abend code into a display field, DISPLAYs its ABENDING line
/// and CALLs CEE3ABD with the code (9999-ABEND-PROGRAM). Where every PERFORM of it follows a MOVE of a literal code the
/// display field's PICTURE (Z..Z9) holds unchanged, the routine takes the code as its parameter and throws the abend
/// with the line it reports: the run report writes it (RunReport.abend), modern code neither edits nor prints. The code,
/// display and timing work fields go. Any other use of the code or of the display field keeps the routine as it is.
/// </summary>
static class AbendRoutines
{
    public static void Run(AppModel app, AppProgram ap, List<AppMethod> methods)
    {
        foreach (var m in methods)
            if (Routine(app, m) is { } r && Rewrite(app, ap, methods, m, r))
                app.DisplayedAbends = true;
    }

    sealed record Shape(string Code, string Display, int Width, List<Expr> Line, ENew Abend);

    /// <summary>[display = edit(code, "Z..Z9"); System.out.println(literals + display); throw new Abend(code, message, routine)].</summary>
    static Shape? Routine(AppModel app, AppMethod m)
    {
        if (!m.Head.StartsWith("private void ", StringComparison.Ordinal) || m.M.L.Params.Count > 0) return null;
        if (m.M.Body is not [SAssign { Lhs: EProgField disp, Rhs: ECall { Kind: CallKind.JdkStatic, Name: "edit", Args: [EProgField code, ELit { Value: string mask }] } },
                SExpr { E: ECall { Kind: CallKind.Jdk, Name: "println", Recv: EStaticJdk { Owner: "java.lang.System", Name: "out" }, Args: [var text] } },
                SThrow { E: ENew { JdkClass: var cls, Args: [EProgField thrown, ELit { Value: string }, ELit { Value: string }] } abend }])
            return null;
        if (cls != app.PlatformPackage + ".Abend" || thrown.Name != code.Name || mask.Length == 0 || mask.Length > 9
            || mask[^1] != '9' || mask[..^1].Any(c => c != 'Z')) return null;
        var parts = new List<Expr>();
        void Flatten(Expr e)
        {
            if (e is EBinary { Op: "+" } b) { Flatten(b.L); Flatten(b.R); }
            else parts.Add(e);
        }
        Flatten(text);
        if (!parts.All(p => p is ELit { Value: string } || p is EProgField f && f.Name == disp.Name)) return null;
        return new Shape(code.Name, disp.Name, mask.Length, parts, abend);
    }

    static IEnumerable<List<St>> Lists(List<St> body)
    {
        yield return body;
        foreach (var s in Passes.All(body))
            switch (s)
            {
                case SIf f: yield return f.Then; yield return f.Else; break;
                case SLoop lo: yield return lo.Body; break;
                case SBlock b: yield return b.Body; break;
                case SSwitch sw: foreach (var c in sw.Cases) yield return c.Body; if (sw.Default is not null) yield return sw.Default; break;
                case STry t: yield return t.Body; foreach (var c in t.Catches) yield return c.Body; break;
            }
    }

    static IEnumerable<Expr> Deep(Expr e)
    {
        yield return e;
        foreach (var c in e.Children)
            foreach (var d in Deep(c)) yield return d;
    }

    static bool IsCall(St s, string name) => s is SExpr { E: ECall { Kind: CallKind.Instance, Recv: null, Name: var n, Args.Count: 0 } } && n == name;

    static bool Rewrite(AppModel app, AppProgram ap, List<AppMethod> methods, AppMethod routine, Shape r)
    {
        string name = routine.Head["private void ".Length..];
        long max = (long)Math.Pow(10, r.Width) - 1;
        // every PERFORM of the routine right after a MOVE of a literal code the display field holds unchanged
        var sites = new List<(List<St> List, int Index, long Code)>();
        var counted = new HashSet<St>(ReferenceEqualityComparer.Instance);
        foreach (var m in methods)
        {
            if (m == routine) continue;
            foreach (var list in Lists(m.M.Body))
                for (int i = 0; i < list.Count; i++)
                {
                    if (!IsCall(list[i], name)) continue;
                    if (i == 0 || list[i - 1] is not SAssign { Lhs: EProgField lf, Rhs: ELit { Value: var v } } || lf.Name != r.Code
                        || v is not (int or long) || Convert.ToInt64(v) is var code && (code < 0 || code > max)) return false;
                    sites.Add((list, i, Convert.ToInt64(v)));
                    counted.Add(list[i - 1]);
                    counted.Add(list[i]);
                }
        }
        if (sites.Count == 0) return false;
        // the code and the display field are read or written nowhere else
        foreach (var m in methods)
        {
            if (m == routine) continue;
            foreach (var s in Passes.All(m.M.Body))
                if (!counted.Contains(s) && Fold.Exprs(s).SelectMany(Deep).Any(e => e is EProgField f && (f.Name == r.Code || f.Name == r.Display)))
                    return false;
        }
        // the rewrite: the call takes the code, the routine throws the abend with its line
        foreach (var (list, i, code) in sites.OrderByDescending(x => x.Index))
        {
            ((SExpr)list[i]).E = new ECall { Kind = CallKind.Instance, Name = name, Args = [ELit.Int(code)], Type = JType.Void };
            list.RemoveAt(i - 1);
        }
        var param = new Var { Name = "code", Type = JType.Int, IsParam = true, Fixed = true };
        routine.M.L.Params.Add(param);
        Expr formatted = new ECall { Kind = CallKind.JdkStatic, Owner = "java.lang.String", Name = "format", Args = [ELit.Str("%" + r.Width + "d"), new EVar(param)], Type = JType.String };
        Expr? line = null;
        foreach (var p in r.Line)
        {
            var part = p is EProgField ? formatted : p;
            line = line is null ? part : new EBinary("+", line, part) { Type = JType.String };
        }
        var lines = new ECall { Kind = CallKind.JdkStatic, Owner = "java.util.List", Name = "of", Args = [line!], Type = JType.Of("java.util.List") };
        routine.M.Body = [new SThrow(new ENew { JdkClass = r.Abend.JdkClass, Args = [new EVar(param), r.Abend.Args[1], r.Abend.Args[2], lines], Type = r.Abend.Type })];
        // the work fields only the routine used: the code, its display, and CEE3ABD's timing argument
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in methods)
            foreach (var s in Passes.All(m.M.Body))
                foreach (var e in Fold.Exprs(s).SelectMany(Deep))
                    if (e is EProgField f) used.Add(f.Name);
        var named = new HashSet<int>();
        if (ap.Classic.FirstOrDefault(p => p.Name == routine.Doc) is { } para)
            foreach (var s in para.Statements)
                foreach (var n in AppModel.StmtNodes(s))
                    if (n.Id is int id) named.Add(id);
        foreach (var (item, pf) in ap.Promoted.ToList())
            if ((pf.Name == r.Code || pf.Name == r.Display || named.Contains(item.Id)) && !used.Contains(pf.Name))
                ap.Promoted.Remove(item);
        return true;
    }
}
