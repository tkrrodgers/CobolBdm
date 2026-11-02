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

    /// <summary>
    /// J3S dispatches a call through an interface (List.size(), List.get(i)) over every JRE emulation class the receiver can
    /// hold: an instanceof chain whose last test ends in "unreachable", each branch the same call on its own class. After the
    /// emulation → JDK mapping every branch is the same Java code: it is one virtual call. Two forms: nested
    /// (if (!(x instanceof A)) { if (!(x instanceof B)) throw; T } else { T }) and, where a branch ends the method,
    /// sequential (if (x instanceof A) { T } if (!(x instanceof B)) throw; T). Branches must be the same statements; a call
    /// of size()/isEmpty() on the receiver that one branch keeps as a statement (J3S read the other class's field inline) is
    /// kept, so nothing the original evaluated is dropped.
    /// </summary>
    public static List<St> InterfaceDispatch(List<St> list)
    {
        var res = new List<St>();
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            switch (s)
            {
                case SIf f: f.Then = InterfaceDispatch(f.Then); f.Else = InterfaceDispatch(f.Else); break;
                case SLoop l: l.Body = InterfaceDispatch(l.Body); break;
                case SBlock b: b.Body = InterfaceDispatch(b.Body); break;
                case STry t:
                    t.Body = InterfaceDispatch(t.Body);
                    for (int k = 0; k < t.Catches.Count; k++) t.Catches[k] = (t.Catches[k].Type, t.Catches[k].V, InterfaceDispatch(t.Catches[k].Body));
                    break;
                case SSwitch sw:
                    for (int k = 0; k < sw.Cases.Count; k++) sw.Cases[k] = (sw.Cases[k].Labels, InterfaceDispatch(sw.Cases[k].Body));
                    if (sw.Default is not null) sw.Default = InterfaceDispatch(sw.Default);
                    break;
            }
            if (s is SIf f2 && DispatchTest(f2.C) is { } test)
            {
                var (one, rest) = test.Negated ? (f2.Else, f2.Then) : (f2.Then, f2.Else);
                // nested: both branches present
                if (one.Count > 0 && rest.Count > 0 && Merge(StripGuard(rest, test.Recv), StripGuard(one, test.Recv), test.Recv) is { } merged)
                {
                    res.AddRange(merged);
                    continue;
                }
                // sequential: if (x instanceof A) { T (ends the method) } if (!(x instanceof B)) throw; T
                if (!test.Negated && rest.Count == 0 && Ends(one) && i + 1 < list.Count && IsGuard(list[i + 1], test.Recv))
                {
                    var tail = InterfaceDispatch(list.GetRange(i + 2, list.Count - i - 2));
                    if (Merge(one, tail, test.Recv) is { } m2)
                    {
                        res.AddRange(m2);
                        return res;
                    }
                }
            }
            res.Add(s);
        }
        return res;
    }

    sealed record DispatchTestOf(Expr Recv, bool Negated);

    /// <summary>
    /// A chain of guarded early exits that all end the same way, at the end of a method (a job function's dispatch on the
    /// event type: if (t is MP) { ...; status = S; return; } if (t is NT) { ...; status = S; return; } ...; status = S):
    /// an if / else-if chain with the shared last statement once after it. The statement runs after every branch in both
    /// forms, so the order of effects is the same. <paramref name="tail"/>: the list ends the method.
    /// </summary>
    public static List<St> TailHoist(List<St> list, bool tail = true)
    {
        if (!tail || list.Count == 0) return list;
        if (list[^1] is STry t && t.Body.Count > 0) t.Body = TailHoist(t.Body);
        int start = -1;
        for (int i = 0; i < list.Count; i++)
            if (list[i] is SIf { Else.Count: 0 } f && f.Then.Count >= 2 && f.Then[^1] is SReturn { E: null }) { start = i; break; }
        if (start < 0) return list;
        int end = start;
        while (end < list.Count && list[end] is SIf { Else.Count: 0 } g && g.Then.Count >= 2 && g.Then[^1] is SReturn { E: null }) end++;
        var rest = list.GetRange(end, list.Count - end);
        if (end - start < 2 || rest.Count == 0) return list;
        var last = Plain(rest[^1]);
        if (last is null || rest[^1] is not (SExpr or SAssign)) return list;
        for (int i = start; i < end; i++)
            if (((SIf)list[i]).Then.Count < 3 || Plain(((SIf)list[i]).Then[^2]) != last) return list;
        // the chain, innermost else first
        var elseBody = rest.GetRange(0, rest.Count - 1);
        for (int i = end - 1; i >= start; i--)
        {
            var f = (SIf)list[i];
            f.Then = f.Then.GetRange(0, f.Then.Count - 2);
            f.Else = elseBody;
            elseBody = [f];
        }
        var res = list.GetRange(0, start);
        res.AddRange(elseBody);
        res.Add(rest[^1]);
        return res;
    }

    static string? Plain(St s) => s is SExpr or SAssign ? System.Text.RegularExpressions.Regex.Replace(IrText.Show([s]), @"   // [^\r\n]*", "") : null;

    /// <summary>
    /// t = e; return t; where the temporary t is assigned and read nowhere else: return e; (the same evaluation, one
    /// name less). A named variable is kept: its name says what the value is.
    /// </summary>
    public static List<St> InlineReturnedTemps(List<St> body)
    {
        var reads = new Dictionary<Var, int>();
        var writes = new Dictionary<Var, int>();
        foreach (var st in Passes.All(body))
        {
            if (st is SAssign { Lhs: EVar { V: var wv } }) writes[wv] = writes.GetValueOrDefault(wv) + 1;
            if (st is SLoop { Each: { } ev }) writes[ev] = writes.GetValueOrDefault(ev) + 2;
            foreach (var e in Fold.Exprs(st))
                foreach (var x in DeepExprs(e))
                    if (x is EVar { V: var rv }) reads[rv] = reads.GetValueOrDefault(rv) + 1;
        }
        Inline(body);
        return body;

        void Inline(List<St> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                switch (list[i])
                {
                    case SIf f: Inline(f.Then); Inline(f.Else); break;
                    case SLoop l: Inline(l.Body); break;
                    case SBlock b: Inline(b.Body); break;
                    case STry t: Inline(t.Body); foreach (var c in t.Catches) Inline(c.Body); break;
                    case SSwitch sw: foreach (var c in sw.Cases) Inline(c.Body); if (sw.Default is not null) Inline(sw.Default); break;
                }
                if (i + 1 < list.Count && list[i] is SAssign { Lhs: EVar { V: { IsTemp: true } tv } } a && list[i + 1] is SReturn { E: EVar { V: var rv } } r
                    && rv == tv && writes.GetValueOrDefault(tv) == 1 && reads.GetValueOrDefault(tv) == 1)
                {
                    list[i] = new SReturn(a.Rhs) { Line = a.Line, Site = a.Site ?? r.Site };
                    list.RemoveAt(i + 1);
                }
            }
        }
    }

    static IEnumerable<Expr> DeepExprs(Expr e)
    {
        yield return e;
        foreach (var c in e.Children)
            foreach (var d in DeepExprs(c)) yield return d;
    }

    /// <summary>x instanceof E or !(x instanceof E), E a JRE emulation class with a JDK type, x a read without effects.</summary>
    static DispatchTestOf? DispatchTest(Expr c)
    {
        var (inst, neg) = c switch { EUnary { Op: "!", E: EInstanceOf i1 } => (i1, true), EInstanceOf i2 => (i2, false), _ => ((EInstanceOf?)null, false) };
        if (inst is null || inst.T.Class is not { Kind: ClassKind.Emulation } ec || JdkMap.JdkTypeOf(ec) is null || !PureRead(inst.E)) return null;
        return new DispatchTestOf(inst.E, neg);
    }

    static bool PureRead(Expr e) => e is EVar or EThis || e is EField f && PureRead(f.Obj) || e is ECast c && PureRead(c.E)
        || e is ECall { Args.Count: 0, Recv: { } r, Name: var n } && n.StartsWith("get", StringComparison.Ordinal) && PureRead(r);

    /// <summary>if (!(x instanceof E)) throw "unreachable" (the end of a dispatch chain).</summary>
    static bool IsGuard(St s, Expr recv) => s is SIf { C: EUnary { Op: "!", E: EInstanceOf gi }, Then: [SThrow { E: ENew { Args: [ELit { Value: "JX-INTERNAL" or "unreachable" }] } }], Else.Count: 0 }
        && gi.T.Class is { Kind: ClassKind.Emulation } ec && JdkMap.JdkTypeOf(ec) is not null && Key(gi.E) == Key(recv);

    static List<St> StripGuard(List<St> l, Expr recv) => l.Count > 0 && IsGuard(l[0], recv) ? l.GetRange(1, l.Count - 1) : l;

    static bool Ends(List<St> l) => l.Count > 0 && l[^1] is SReturn or SThrow;

    /// <summary>The branches as one when they are the same statements (sites aside), else null.</summary>
    static List<St>? Merge(List<St> a, List<St> b, Expr recv)
    {
        if (a.Count == 0 || b.Count == 0) return null;
        var ka = Shape(a, recv, true);
        if (ka != Shape(b, recv, true)) return null;
        // the branch that evaluates more of the receiver's accessors is the one that keeps what the source evaluated
        return Shape(a, recv, false).Length >= Shape(b, recv, false).Length ? a : b;
    }

    static string Shape(List<St> l, Expr recv, bool dropProbes)
    {
        var rk = Key(recv);
        var kept = dropProbes ? l.Where(s => !(s is SExpr { E: ECall { Name: "size" or "isEmpty", Args.Count: 0, Recv: { } pr } } && Key(pr) == rk)).ToList() : l;
        return System.Text.RegularExpressions.Regex.Replace(IrText.Show(kept), @"   // [^\r\n]*", "");
    }

    static string Key(Expr e) => IrText.Show(e is ECast c ? c.E : e);

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
                // System.getProperty(key, def): the job has no system properties (J3S JSystemStatics returns def; a JVM
                // property such as java.* traps there), so a literal key outside the JVM's namespaces is def
                if (name == "JSystemStatics" && call.Name == "getProperty" && call.Args is [ELit { Value: string pkey }, var pdef]
                    && JdkMap.JobPropertyKey(pkey))
                {
                    M.JobProperties.TryAdd(pkey, Fn?.Id ?? "");
                    return pdef;
                }
                // getClass() as J3S lowers it: Class.of(the class id of x) is x.getClass(); Class.of(id) is a class literal
                if (name == "JClass" && call.Name == "of" && call.Args is [var ca])
                {
                    if (ca is EPart { Part: "C" } cp) return new ECall { Kind = CallKind.Jdk, Name = "getClass", Recv = cp.Whole, Type = JType.Of("java.lang.Class") };
                    if (ca is ELit { Value: long cid } && M.ClassIdType((int)cid) is { } ct) return new EClassLit(ct) { Type = JType.Of("java.lang.Class") };
                    // an id no allocation binds (an abstract class) in the class's own code: its own literal; the id is kept, and
                    // the reflection pass resolves it from the profile's assignability table once the hierarchy is known
                    if (ca is ELit { Value: long aid } && Fn?.Owner is { Kind: ClassKind.Domain } own) return new EClassLit(JType.Of(own)) { Type = JType.Of("java.lang.Class"), ClassId = (int)aid };
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
                    // J3S's JRE pow(int, MathContext): the legacy's FSJBDEC operations at the emulation's sites (DecimalLimit, the
                    // compatibility lane), so the store and its capacity are the legacy's; without the sites it stays unmapped
                    if (call.Fn is { } pf && FunctionLifter.IsJrePow(pf) && M.JrePowSites.Count == 4)
                        return new ECall { Kind = CallKind.JdkStatic, Owner = M.PlatformPackage + ".DecimalLimit", Name = "pow", Type = JType.BigDecimal,
                            Args = [.. call.Args, new EStaticJdk(M.PlatformPackage + ".DecimalLimit", "JRE_POW", JType.Of(M.PlatformPackage + ".DecimalLimit.PowSites"))] };
                    var owner = JdkMap.Statics.GetValueOrDefault(name.Split('.')[0]) ?? JdkMap.Types.GetValueOrDefault(name) ?? oc.FullName;
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
