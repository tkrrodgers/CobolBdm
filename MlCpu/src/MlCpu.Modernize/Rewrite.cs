namespace MlCpu.Modernize;

/// <summary>
/// Rewrites on lifted bodies after structuring: JRE emulation → JDK, leftover J3S parts → Java, simplifications.
/// </summary>
public sealed class Rewrite(Model m, FunctionDef? fn = null)
{
    readonly Model M = m;
    /// <summary>The function rewritten (its class names an unbound class literal: getLogger(X.class) in X).</summary>
    readonly FunctionDef? Fn = fn;

    public List<St> Body(List<St> body)
    {
        foreach (var s in Passes.All(body)) Statement(s);
        return body;
    }

    /// <summary>
    /// After the loop passes: what is left of J3S's stream protocol is a list's own API (fence = size(), at(i) = get(i)); the
    /// modification probes have no Java counterpart (the JDK iterators check by themselves).
    /// </summary>
    public static List<St> StreamLeftovers(List<St> list)
    {
        var res = new List<St>();
        foreach (var s in list)
        {
            if (s is SExpr { E: ECall { Name: "j3sModCount" } }) continue;
            switch (s)
            {
                case SIf i: i.Then = StreamLeftovers(i.Then); i.Else = StreamLeftovers(i.Else); break;
                case SLoop l: l.Body = StreamLeftovers(l.Body); break;
                case SBlock b: b.Body = StreamLeftovers(b.Body); break;
                case SSwitch sw:
                    for (int k = 0; k < sw.Cases.Count; k++) sw.Cases[k] = (sw.Cases[k].Labels, StreamLeftovers(sw.Cases[k].Body));
                    if (sw.Default is not null) sw.Default = StreamLeftovers(sw.Default);
                    break;
                case STry t: t.Body = StreamLeftovers(t.Body); break;
            }
            foreach (var _ in Fold.Exprs(s)) { }
            Leftover(s);
            res.Add(s);
        }
        return res;
    }

    /// <summary>
    /// J3S dispatches a call of Object's methods over every class the receiver can hold: an instanceof chain ending in
    /// JX-INTERNAL, each branch the class's own method, or identity where the class does not override it (x == y,
    /// System.identityHashCode(x)). It is one virtual call: x.equals(y), x.hashCode(), x.toString().
    /// </summary>
    public static List<St> ObjectDispatch(List<St> list)
    {
        var res = new List<St>();
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            switch (s)
            {
                case SIf f: f.Then = ObjectDispatch(f.Then); f.Else = ObjectDispatch(f.Else); break;
                case SLoop l: l.Body = ObjectDispatch(l.Body); break;
                case SBlock b: b.Body = ObjectDispatch(b.Body); break;
                case STry t: t.Body = ObjectDispatch(t.Body); break;
                case SSwitch sw:
                    for (int k = 0; k < sw.Cases.Count; k++) sw.Cases[k] = (sw.Cases[k].Labels, ObjectDispatch(sw.Cases[k].Body));
                    if (sw.Default is not null) sw.Default = ObjectDispatch(sw.Default);
                    break;
            }
            if (s is SIf && MatchDispatch(list, i, out int used) is { } d && used > 0)
            {
                res.Add(new SAssign(d.Target, new ECall { Kind = CallKind.Jdk, Name = d.Method, Recv = d.Recv, Args = d.Args,
                    Type = d.Method switch { "equals" => JType.Bool, "hashCode" => JType.Int, _ => JType.String } }));
                i += used - 1;
                continue;
            }
            res.Add(s);
        }
        return res;
    }

    sealed record Dispatch(Expr Target, string Method, Expr Recv, List<Expr> Args, string Key);

    static Dispatch? MatchDispatch(List<St> l, int at, out int used)
    {
        used = 0;
        if (at >= l.Count || l[at] is not SIf f) return null;
        // if (!(x instanceof T)) { throw } ; v = e
        if (f.C is EUnary { Op: "!", E: EInstanceOf } && f.Then is [SThrow { E: ENew { Args: [ELit { Value: "JX-INTERNAL" or "unreachable" }] } }] && f.Else.Count == 0
            && at + 1 < l.Count && l[at + 1] is SAssign last && Branch(last, ((EInstanceOf)((EUnary)f.C).E).E) is { } d0)
        {
            used = 2;
            return d0;
        }
        // if (!(x instanceof T)) { rest } else { v = e }   or   if (x instanceof T) { v = e } else { rest }
        var (inst, rest, one) = f.C switch
        {
            EUnary { Op: "!", E: EInstanceOf i1 } => (i1, f.Then, f.Else),
            EInstanceOf i2 => (i2, f.Else, f.Then),
            _ => ((EInstanceOf?)null, f.Then, f.Else),
        };
        if (inst is null || one is not [SAssign a] || Branch(a, inst.E) is not { } d) return null;
        // the rest already collapsed (the inner chain is rewritten first)
        if (rest is [SAssign ra] && Branch(ra, inst.E) is { } dr && dr.Key == d.Key) { used = 1; return d; }
        if (MatchDispatch(rest, 0, out int u2) is not { } inner || u2 != rest.Count || inner.Key != d.Key) return null;
        used = 1;
        return inner;
    }

    /// <summary>One branch: target = the receiver's equals/hashCode/toString, or the identity it falls back to.</summary>
    static Dispatch? Branch(SAssign a, Expr x)
    {
        static Expr Strip(Expr e) => e is ECast c ? Strip(c.E) : e;
        var xs = IrText.Show(Strip(x));
        (string M, List<Expr> Args)? m = a.Rhs switch
        {
            ECall { Name: "equals" or "hashCode" or "toString", Recv: { } r } c when IrText.Show(Strip(r)) == xs => (c.Name, c.Args),
            EBinary { Op: "==", L: var bl, R: var br } when IrText.Show(Strip(bl)) == xs => ("equals", [br]),
            ECall { Kind: CallKind.JdkStatic, Name: "identityHashCode", Args: [var ia] } when IrText.Show(Strip(ia)) == xs => ("hashCode", []),
            _ => null,
        };
        if (m is null) return null;
        var key = IrText.Show(a.Lhs) + "=" + xs + "." + m.Value.M + "(" + string.Join(",", m.Value.Args.Select(e => IrText.Show(Strip(e)))) + ")";
        return new Dispatch(a.Lhs, m.Value.M, Strip(x), m.Value.Args.Select(Strip).ToList(), key);
    }

    static void Leftover(St s)
    {
        static Expr R(Expr e)
        {
            e = e.Map(R);
            return e switch
            {
                ECall { Name: "j3sFence" } c => new ECall { Kind = CallKind.Jdk, Name = "size", Recv = c.Recv, Type = JType.Int },
                ECall { Name: "j3sAt" } c => new ECall { Kind = CallKind.Jdk, Name = "get", Recv = c.Recv, Args = c.Args },
                ECall { Name: "j3sModCount" } => ELit.Int(0),
                _ => e,
            };
        }
        switch (s)
        {
            case SAssign a: if (a.Lhs is not EVar) a.Lhs = R(a.Lhs); a.Rhs = R(a.Rhs); break;
            case SExpr x: x.E = R(x.E); break;
            case SReturn { E: not null } r: r.E = R(r.E!); break;
            case SIf i: i.C = R(i.C); break;
            case SLoop l: if (l.C is not null) l.C = R(l.C); if (l.Iterable is not null) l.Iterable = R(l.Iterable); break;
        }
    }

    void Statement(St s)
    {
        switch (s)
        {
            case SAssign a: a.Lhs = a.Lhs is EVar ? a.Lhs : E(a.Lhs); a.Rhs = E(a.Rhs); break;
            case SExpr e: e.E = E(e.E); break;
            case SReturn r when r.E is not null: r.E = E(r.E); break;
            case SThrow t: t.E = E(t.E); break;
            case SIf i: i.C = Cond(E(i.C)); break;
            case SLoop l: if (l.C is not null) l.C = Cond(E(l.C)); if (l.Iterable is not null) l.Iterable = E(l.Iterable); break;
            case SSwitch sw: sw.Subject = E(sw.Subject); break;
        }
    }

    public Expr E(Expr e)
    {
        // an enum constant's pool index compared with a number: its ordinal is the index - 1
        if (e is EBinary { Op: "==" or "!=", L: EPart { Part: "I" } ip, R: ELit { Value: long k } } eb)
            return new EBinary(eb.Op, new ECall { Kind = CallKind.Jdk, Name = "ordinal", Recv = E(ip.Whole), Type = JType.Int }, ELit.Int(k - 1)) { Type = JType.Bool };
        e = e.Map(E);
        switch (e)
        {
            // new JString("x".toCharArray()) → "x"; new String(chars) otherwise
            case ENew { Class: { Kind: ClassKind.Emulation } c } n when JdkMap.EmulationName(c) == "JString":
                if (n.Args is [ECall { Name: "toCharArray", Recv: { } src }]) return src;
                return new ENew { JdkClass = "java.lang.String", Args = n.Args, Type = JType.String };
            case ENew { Class: { Kind: ClassKind.Emulation } c2 } n2:
            {
                var name = JdkMap.EmulationName(c2);
                var concrete = JdkMap.Concrete.GetValueOrDefault(name) ?? JdkMap.Types.GetValueOrDefault(name) ?? "java.lang.Object";
                return new ENew { JdkClass = concrete, Args = n2.Args, Type = JType.Of(JdkMap.Types.GetValueOrDefault(name) ?? concrete) };
            }
            case ECall { Fn.Owner: { Kind: ClassKind.Emulation } oc } call:
            {
                var name = JdkMap.EmulationName(oc);
                // getClass() as J3S lowers it: Class.of(the class id of x) is x.getClass(); Class.of(id) is a class literal
                if (name == "JClass" && call.Name == "of" && call.Args is [var ca])
                {
                    if (ca is EPart { Part: "C" } cp) return new ECall { Kind = CallKind.Jdk, Name = "getClass", Recv = cp.Whole, Type = JType.Of("java.lang.Class") };
                    if (ca is ELit { Value: long cid } && M.ClassIdType((int)cid) is { } ct) return new EClassLit(ct) { Type = JType.Of("java.lang.Class") };
                    // an id no allocation binds (an abstract class) in the class's own code: its own literal
                    if (ca is ELit && Fn?.Owner is { Kind: ClassKind.Domain } own) return new EClassLit(JType.Of(own)) { Type = JType.Of("java.lang.Class") };
                }
                // SLF4J (a no-op logger in the estate): the JDK's System.Logger, {} placeholders as {0}, {1}
                if (name.EndsWith("JLoggerFactory") && call.Name == "getLogger" && call.Args is [var lc])
                    return new ECall { Kind = CallKind.JdkStatic, Owner = "java.lang.System", Name = "getLogger", Type = JType.Of("java.lang.System.Logger"),
                        Args = [new ECall { Kind = CallKind.Jdk, Name = "getName", Recv = lc, Type = JType.String }] };
                if (name.EndsWith("JNopLogger") && call.Recv is not null && call.Name is "debug" or "info" or "warn" or "error" or "trace")
                {
                    var largs = new List<Expr> { new EStaticJdk("java.lang.System.Logger.Level", call.Name.ToUpperInvariant(), JType.Of("java.lang.System.Logger.Level")) };
                    for (int ai = 0; ai < call.Args.Count; ai++)
                        largs.Add(ai == 0 && call.Args[0] is ELit { Value: string fmt } ? ELit.Str(Placeholders(fmt)) : call.Args[ai]);
                    return new ECall { Kind = CallKind.Jdk, Name = "log", Recv = call.Recv, Args = largs, Type = JType.Void };
                }
                if (call.Name == "fromStream" && call.Args.Count == 1)
                    return new ECall { Kind = CallKind.JdkStatic, Owner = "java.util.Collections", Name = "unmodifiableList", Args = call.Args };
                if (call.Kind == CallKind.Static || call.Recv is null)
                {
                    var owner = JdkMap.Statics.GetValueOrDefault(name.Split('.')[0]) ?? JdkMap.Types.GetValueOrDefault(name) ?? name;
                    return new ECall { Kind = CallKind.JdkStatic, Owner = owner, Name = call.Name, Args = call.Args, Type = call.Type };
                }
                return new ECall { Kind = CallKind.Jdk, Owner = JdkMap.Types.GetValueOrDefault(name), Name = call.Name, Recv = call.Recv, Args = call.Args, Type = call.Type, };
            }
            // an emulation field read inlined by J3S (a trivial getter): the JDK accessor
            case EField { F.Owner: { Kind: ClassKind.Emulation } fo } f when f.Obj is not EThis:
                return new ECall { Kind = CallKind.Jdk, Name = AccessorOf(fo, f.F), Recv = f.Obj, Type = f.Type };
            case EStatic { F.Group: var g } st when g is not null && EmulationStatic(st.F) is { } js:
                return js;
            case EPart { Part: "I" } p:
                return p.Whole;
            case ECast { T.Name: "int" } c when IsInt(c.E):
                return c.E;
            case EBinary { Op: "<" or "<=" or ">" or ">=" or "==" or "!=" } b when Ternary01(b) is { } t01:
                return t01;
        }
        return e;
    }

    /// <summary>SLF4J's {} placeholders as MessageFormat's {0}, {1}.</summary>
    static string Placeholders(string fmt)
    {
        var sb = new System.Text.StringBuilder();
        int k = 0;
        for (int i = 0; i < fmt.Length; i++)
        {
            if (fmt[i] == '{' && i + 1 < fmt.Length && fmt[i + 1] == '}') { sb.Append('{').Append(k++).Append('}'); i++; }
            else if (fmt[i] == '\'') sb.Append("''");
            else sb.Append(fmt[i]);
        }
        return sb.ToString();
    }

    static bool IsInt(Expr e) => e.Type?.Name == "int" || e is ELit { Value: long v } && v is >= int.MinValue and <= int.MaxValue
        || e is EBinary { Op: "+" or "-" or "*" } b && IsInt(b.L) && IsInt(b.R) || e is EVar { V.Kind: VKind.Int } || e is EField { F.Kind: VKind.Int }
        || e is ECall { Type.Name: "int" } || e is ECond c && IsInt(c.A) && IsInt(c.B);

    /// <summary>0 &lt; (c ? 1 : 0) and similar comparisons of a 0/1 ternary with a constant: the condition.</summary>
    static Expr? Ternary01(EBinary b)
    {
        if (b.R is ECond { A: ELit { Value: long a }, B: ELit { Value: long z } } c && b.L is ELit { Value: long k } && (a, z) is (1, 0) or (0, 1))
        {
            bool whenTrue = Eval(b.Op, k, a), whenFalse = Eval(b.Op, k, z);
            if (whenTrue && !whenFalse) return c.C;
            if (!whenTrue && whenFalse) return FunctionLifter.Not(c.C);
        }
        return null;
    }

    static bool Eval(string op, long l, long r) => op switch { "<" => l < r, "<=" => l <= r, ">" => l > r, ">=" => l >= r, "==" => l == r, _ => l != r };

    static Expr Cond(Expr c) => c;

    static string AccessorOf(ClassDef emu, FieldDef f) => (JdkMap.EmulationName(emu), f.Name) switch
    {
        (_, "size") => "size",
        ("JMapEntry", "key") => "getKey",
        ("JMapEntry", "value") => "getValue",
        (_, "value") => "get",
        (_, var n) => n,
    };

    /// <summary>Static fields of the emulation (Optional.EMPTY, Collections.reverseOrder()).</summary>
    Expr? EmulationStatic(FieldDef f)
    {
        var owner = M.Classes.Values.FirstOrDefault(c => c.Statics.Contains(f));
        return f.Group switch
        {
            "SJO" => new ECall { Kind = CallKind.JdkStatic, Owner = "java.util.Optional", Name = "empty" },
            "SJC" => new ECall { Kind = CallKind.JdkStatic, Owner = "java.util.Collections", Name = "reverseOrder" },
            _ => null,
        };
    }
}
