namespace MlCpu.Modernize;

/// <summary>
/// Whole-estate type inference: every variable, field, parameter and result is a slot holding the set of classes its
/// values can have. Values flow along assignments, arguments and returns (inclusion constraints, solved to a fixpoint);
/// collections carry element, key and value slots, unified across assignments, so List&lt;RepaymentPeriod&gt; comes out.
/// Declared types (the J3S signature comments) win where they exist.
/// </summary>
public sealed class TypeInference
{
    public sealed class Slot
    {
        public readonly HashSet<ClassDef> C = [];
        /// <summary>Requirements: classes whose methods or fields are used on this slot's values.</summary>
        public readonly HashSet<ClassDef> Req = [];
        public readonly HashSet<string> J = [];
        /// <summary>JDK types whose methods are called on this slot's values (the type when no value says more).</summary>
        public readonly HashSet<string> JReq = [];
        public JType? Decl;
        /// <summary>A declared simple name that is not a class of the estate (an interface the COBOL never shows).</summary>
        public string? DeclName;
        /// <summary>The package of the function that declared DeclName (where a synthesized interface goes).</summary>
        public string? DeclFrom;
        public readonly List<Slot> Out = [];
        Slot? elem, key, val;
        Slot? parent;
        public string Name = "";
        public Slot Rep { get { var s = this; while (s.parent is not null) s = s.parent; return s; } }
        public Slot Elem => Rep.elem ??= new Slot { Name = Name + ".elem", Element = true };
        public Slot Key => Rep.key ??= new Slot { Name = Name + ".key", Element = true };
        public Slot Val => Rep.val ??= new Slot { Name = Name + ".val", Element = true };
        /// <summary>The slot is a container's element, key or value: primitives are boxed (a missing map value is null).</summary>
        public bool Element;
        /// <summary>Declared with a type variable (Supplier&lt;? extends T&gt;): each use has its own type, the values do not merge.</summary>
        public bool Generic;
        /// <summary>The class a signature declares: what flows on from this slot is of that class, whatever reaches it.</summary>
        public ClassDef? DeclClass;
        public bool HasElem => Rep.elem is not null;
        public bool HasKey => Rep.key is not null;

        public static void Unify(Slot a, Slot b)
        {
            a = a.Rep; b = b.Rep;
            if (a == b) return;
            b.parent = a;
            a.C.UnionWith(b.C); a.J.UnionWith(b.J); a.Req.UnionWith(b.Req); a.JReq.UnionWith(b.JReq); a.Out.AddRange(b.Out);
            a.Decl ??= b.Decl;
            a.Element |= b.Element;
            a.Generic |= b.Generic;
            a.DeclClass ??= b.DeclClass;
            a.DeclName ??= b.DeclName;
            a.DeclFrom ??= b.DeclFrom;
            if (b.elem is not null) { if (a.elem is null) a.elem = b.elem; else Unify(a.elem, b.elem); }
            if (b.key is not null) { if (a.key is null) a.key = b.key; else Unify(a.key, b.key); }
            if (b.val is not null) { if (a.val is null) a.val = b.val; else Unify(a.val, b.val); }
        }
    }

    readonly Model M;
    public readonly Dictionary<Var, Slot> Vars = [];
    public readonly Dictionary<FieldDef, Slot> Fields = [];
    public readonly Dictionary<FunctionDef, Slot> Returns = [];
    public readonly Dictionary<(FunctionDef, int), Slot> Params = [];
    readonly List<(Slot From, Slot To)> edges = [];

    public TypeInference(Model m) { M = m; }

    // ------------------------------------------------------------------ slots

    /// <summary>The estate's class of a fully qualified name, or null.</summary>
    public ClassDef? ClassNamed(string name) => M.Classes.TryGetValue(name, out var c) && c.Kind == ClassKind.Domain ? c : null;

    public Slot Of(Var v)
    {
        if (Vars.TryGetValue(v, out var s)) return s.Rep;
        s = new Slot { Name = v.Name };
        if (Declared(v.Kind) is { } d) s.Decl = d;
        if (v.Type is not null && !v.Type.IsObject) s.Decl ??= v.Type.Class is null && !IsContainer(v.Type.Name) ? v.Type : null;
        if (v.Type?.Class is { } vc) s.C.Add(vc);
        Vars[v] = s;
        return s;
    }

    public Slot Of(FieldDef f)
    {
        if (Fields.TryGetValue(f, out var s)) return s.Rep;
        s = new Slot { Name = f.ToString() };
        if (Declared(f.Kind) is { } d) s.Decl = d;
        if (f.Type?.Class is { } fc) s.C.Add(fc);
        Fields[f] = s;
        return s;
    }

    public Slot Ret(FunctionDef fn)
    {
        if (Returns.TryGetValue(fn, out var s)) return s.Rep;
        s = new Slot { Name = fn.Id + ".ret" };
        if (fn.Ret is not null && Declared(fn.Ret.Kind) is { } d) s.Decl = d;
        Returns[fn] = s;
        return s;
    }

    public Slot Param(FunctionDef fn, int i)
    {
        if (Params.TryGetValue((fn, i), out var s)) return s.Rep;
        s = new Slot { Name = fn.Id + ".p" + i };
        var ps = fn.Params[i];
        if (Declared(ps.Kind) is { } d) s.Decl = d;
        if (i < fn.ParamTypes.Count) DeclaredName(s, fn.ParamTypes[i]);
        Params[(fn, i)] = s;
        return s;
    }

    static JType? Declared(VKind k) => k switch
    {
        VKind.Bool => JType.Bool, VKind.Int => JType.Int, VKind.Long => JType.Long, VKind.Dbl => JType.Double,
        VKind.BigDec => JType.BigDecimal, VKind.Date => JType.LocalDate, VKind.Mc => JType.MathContext,
        VKind.BoxInt => JType.Integer, VKind.BoxLong => JType.LongBox, VKind.BoxBool => JType.BooleanBox,
        _ => null,
    };

    static readonly Dictionary<string, string> KnownNames = new(StringComparer.Ordinal)
    {
        ["String"] = "java.lang.String", ["Object"] = "java.lang.Object", ["Integer"] = "java.lang.Integer", ["Long"] = "java.lang.Long", ["Boolean"] = "java.lang.Boolean",
        ["BigDecimal"] = "java.math.BigDecimal", ["MathContext"] = "java.math.MathContext", ["RoundingMode"] = "java.math.RoundingMode",
        ["LocalDate"] = "java.time.LocalDate", ["ChronoUnit"] = "java.time.temporal.ChronoUnit", ["ChronoField"] = "java.time.temporal.ChronoField",
        ["TemporalUnit"] = "java.time.temporal.TemporalUnit", ["Year"] = "java.time.Year", ["YearMonth"] = "java.time.YearMonth",
        ["List"] = "java.util.List", ["Map"] = "java.util.Map", ["Set"] = "java.util.Set", ["Collection"] = "java.util.Collection", ["Optional"] = "java.util.Optional",
        ["Iterator"] = "java.util.Iterator", ["Comparator"] = "java.util.Comparator", ["Function"] = "java.util.function.Function",
        ["Consumer"] = "java.util.function.Consumer", ["Predicate"] = "java.util.function.Predicate", ["Supplier"] = "java.util.function.Supplier",
        ["BiFunction"] = "java.util.function.BiFunction", ["BiConsumer"] = "java.util.function.BiConsumer", ["UnaryOperator"] = "java.util.function.UnaryOperator",
        ["BinaryOperator"] = "java.util.function.BinaryOperator",
        ["int"] = "int", ["long"] = "long", ["boolean"] = "boolean", ["double"] = "double", ["char"] = "char",
    };

    public static bool IsContainer(string jdk) => jdk is "java.util.List" or "java.util.Map" or "java.util.Set" or "java.util.NavigableSet" or "java.util.Collection" or "java.util.Optional"
        or "java.util.Iterator" or "java.util.Map.Entry" or "java.util.concurrent.atomic.AtomicReference";

    /// <summary>A declared parameter type from the signature comment (a simple name; generic arguments are cut by J3S).</summary>
    static readonly System.Text.RegularExpressions.Regex TypeVar = new(@"(^|[<\s,?])[A-Z][A-Z0-9]?($|[>\s,\[])");

    void DeclaredName(Slot s, string name)
    {
        var n = name.Trim();
        if (TypeVar.IsMatch(n)) s.Generic = true;
        if (n.EndsWith("...")) n = n[..^3] + "[]";
        if (n.EndsWith("[]"))
        {
            var el = n[..^2];
            s.Decl = JType.Of((KnownNames.TryGetValue(el, out var fq) ? fq : M.Classes.Values.FirstOrDefault(c => c.SimpleName == el && c.Kind == ClassKind.Domain)?.FullName ?? el) + "[]");
            return;
        }
        if (n.EndsWith('>'))
        {
            // "DisbursementData>" is what J3S kept of List<DisbursementData>: the element's type
            var el = n.TrimEnd('>');
            if (el.LastIndexOf('<') is var lt0 and >= 0) el = el[(lt0 + 1)..];
            if (el.Length > 0 && char.IsLetter(el[0])) DeclaredName(s.Elem, el);
            return;
        }
        // generic arguments are cut by J3S ("NotNullList<T"): keep the raw name
        if (n.IndexOf('<') is var lt and > 0) n = n[..lt];
        // a type variable (T, E, K, V) says nothing
        if (n.Length <= 2 && n.All(char.IsUpper)) return;
        if (n.Length == 0 || !char.IsLetter(n[0]) || n.Any(ch => !char.IsLetterOrDigit(ch) && ch != '_')) return;
        if (KnownNames.TryGetValue(n, out var jdk))
        {
            if (IsContainer(jdk) || jdk.StartsWith("java.util.function.") || jdk == "java.util.Comparator") s.J.Add(jdk);
            else s.Decl ??= JType.Of(jdk);
            return;
        }
        var cands = M.Classes.Values.Where(c => c.SimpleName == n && c.Kind is ClassKind.Domain).ToList();
        if (cands.Count == 1) { s.C.Add(cands[0]); s.DeclName = cands[0].FullName; s.DeclClass = cands[0]; return; }
        s.DeclName = n;
        s.DeclFrom ??= declaringPackage;
    }

    string? declaringPackage;

    /// <summary>The declared types of a function's parameters (call once per function before solving).</summary>
    public void Declare(FunctionDef fn)
    {
        declaringPackage = fn.Owner?.Package;
        for (int i = 0; i < fn.Params.Count; i++) Param(fn, i);
        declaringPackage = null;
    }

    // ------------------------------------------------------------------ interfaces the COBOL never shows

    /// <summary>Declared names that are no class of the estate, with the classes whose values reach them.</summary>
    public readonly Dictionary<string, (string FullName, HashSet<ClassDef> Impls)> Interfaces = new(StringComparer.Ordinal);

    public void BuildInterfaces()
    {
        var all = Vars.Values.Concat(Fields.Values).Concat(Returns.Values).Concat(Params.Values).Select(s => s.Rep).Distinct().ToList();
        var seen = new HashSet<Slot>();
        void Visit(Slot s)
        {
            s = s.Rep;
            if (!seen.Add(s)) return;
            if (s.DeclName is { } d && !d.Contains('.') && !KnownNames.ContainsKey(d))
            {
                var impls = s.C.Where(c => c.Kind == ClassKind.Domain).ToHashSet();
                if (!Interfaces.TryGetValue(d, out var e)) Interfaces[d] = e = ((s.DeclFrom ?? impls.FirstOrDefault()?.Package ?? "modern") + "." + d, []);
                e.Impls.UnionWith(impls);
            }
            if (s.HasElem) Visit(s.Elem);
            if (s.HasKey) { Visit(s.Key); Visit(s.Val); }
        }
        foreach (var s in all) Visit(s);
        foreach (var k in Interfaces.Where(kv => kv.Value.Impls.Count == 0).Select(kv => kv.Key).ToList()) Interfaces.Remove(k);
        // an interface lives with its implementations (the package most of them are in)
        foreach (var (k, (_, impls)) in Interfaces.ToList())
        {
            static string TopPackage(ClassDef c) { while (c.Outer is not null) c = c.Outer; return c.Package; }
            var pkg = impls.GroupBy(TopPackage).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
            Interfaces[k] = (pkg + "." + k, impls);
        }
    }

    // ------------------------------------------------------------------ constraints

    public void Flow(Slot from, Slot to)
    {
        from = from.Rep; to = to.Rep;
        if (from == to) return;
        edges.Add((from, to));
    }

    /// <summary>
    /// Java generics are invariant: when a container flows into one whose element type is a proper supertype, the source
    /// is declared with that element type too (List&lt;LoanScheduleModelPeriod&gt; periods = new ArrayList&lt;&gt;()).
    /// </summary>
    public void Invariance()
    {
        for (int round = 0; round < 4; round++)
        {
            bool changed = false;
            foreach (var (a0, b0) in edges)
            {
                var a = a0.Rep; var b = b0.Rep;
                if (a == b) continue;
                bool aCont = a.J.Any(IsContainer) || a.Decl is { } ad && IsContainer(ad.Name);
                if (b.HasElem && (a.HasElem || aCont)) changed |= Align(a.Elem.Rep, b.Elem.Rep);
                if (b.HasKey && (a.HasKey || aCont)) { changed |= Align(a.Key.Rep, b.Key.Rep); changed |= Align(a.Val.Rep, b.Val.Rep); }
                if (a.HasKey && !b.HasKey && b.J.Contains("java.util.Map")) { changed |= Align(a.Key.Rep, b.Key.Rep); changed |= Align(a.Val.Rep, b.Val.Rep); }
            }
            // an Object[] built for a varargs call takes the declared array type of the parameter
            foreach (var (a0, b0) in edges)
            {
                var a = a0.Rep; var b = b0.Rep;
                if (a == b || a.Decl is not null || b.Decl is not { IsArray: true } bd) continue;
                if (a.J.Contains("java.lang.Object[]") && bd.Name != "java.lang.Object[]") { a.Decl = bd; changed = true; }
            }
            if (!changed) break;
        }
    }

    /// <summary>One type argument of a container flowing into another: Java needs them equal.</summary>
    bool Align(Slot a, Slot b)
    {
        var ta = JavaType(a); var tb = JavaType(b);
        if (ta.Equals(tb)) return false;
        // nothing known about the source's elements (an empty new list): the target's
        if (a.Decl is null && ta.IsObject && !tb.IsObject) { a.Decl = tb; return true; }
        // nothing known about the target's (a result declared from what is returned): the source's
        if (b.Decl is null && tb.IsObject && !ta.IsObject && b.C.Count == 0) { b.Decl = ta; return true; }
        if (tb.IsObject || a.Decl is not null) return false;
        bool wider = tb.Class is { } bc && ta.Class is { } ac && IsSub(ac, bc)
            || Interfaces.Values.Any(i => i.FullName == tb.Name && ta.Class is { } ic && i.Impls.Contains(ic));
        if (!wider) return false;
        a.Decl = tb;
        return true;
    }

    /// <summary>
    /// Propagates value sets along the flows to a fixpoint. Element, key and value slots flow the same direction as their
    /// containers (a parameter's elements are the union of its arguments' elements), never merging unrelated lists.
    /// </summary>
    public void Solve()
    {
        var done = new HashSet<(Slot, Slot)>();
        bool changed = true;
        int rounds = 0;
        while (changed && rounds++ < 300)
        {
            changed = false;
            int n = edges.Count;
            for (int k = 0; k < n; k++)
            {
                var a = edges[k].From.Rep; var b = edges[k].To.Rep;
                if (a == b) continue;
                // a declared type is what flows on (values of other types reaching a declared parameter are a gap upstream)
                bool definite = a.Decl is { } ad0 && !ad0.IsObject && !ad0.IsArray && !IsContainer(ad0.Name);
                if (a.DeclClass is { } dc && !definite) changed |= b.C.Add(dc);
                else if (!definite) foreach (var c in a.C) changed |= b.C.Add(c);
                if (!definite && a.DeclClass is null) foreach (var j in a.J) changed |= b.J.Add(j);
                if (a.Decl is { } d && b.Decl is null) changed |= b.J.Add(d.Name);
                // a declared (interface) name travels with the values
                if (a.DeclName is { } dn && b.DeclName is null && b.Decl is null && !dn.Contains('.')) { b.DeclName = dn; b.DeclFrom ??= a.DeclFrom; changed = true; }
                if (a.HasElem && done.Add((a.Elem, b.Elem))) { edges.Add((a.Elem, b.Elem)); changed = true; }
                if (a.HasKey && done.Add((a.Key, b.Key))) { edges.Add((a.Key, b.Key)); edges.Add((a.Val, b.Val)); changed = true; }
            }
        }
    }

    /// <summary>
    /// A slot no value reaches (the result of a method J3S never lowered, a lambda's unused parameter) takes the requirements
    /// of the slots it flows into: what its values must be where they are used.
    /// </summary>
    public void RequireBackward()
    {
        for (int round = 0; round < 20; round++)
        {
            bool changed = false;
            foreach (var (a0, b0) in edges.ToList()) changed |= Back(a0, b0, 0);
            if (!changed) break;
        }
    }

    bool Back(Slot a, Slot b, int depth)
    {
        a = a.Rep; b = b.Rep;
        if (a == b) return false;
        bool changed = false;
        if (a.C.Count == 0 && a.J.Count == 0 && a.Decl is null)
        {
            foreach (var r in b.Req) changed |= a.Req.Add(r);
            foreach (var j in b.JReq) changed |= a.JReq.Add(j);
            if (b.Decl is { } bd && !bd.IsPrimitive && !bd.IsObject) changed |= a.JReq.Add(bd.Name);
        }
        // the elements of what a container must be: a result of a method J3S never lowered (Optional of what)
        if (depth < 3)
        {
            if (b.HasElem && (a.HasElem || a.JReq.Any(IsContainer))) changed |= Back(a.Elem, b.Elem, depth + 1);
            if (b.HasKey && (a.HasKey || a.JReq.Contains("java.util.Map"))) { changed |= Back(a.Key, b.Key, depth + 1); changed |= Back(a.Val, b.Val, depth + 1); }
        }
        return changed;
    }

    // ------------------------------------------------------------------ results

    /// <summary>The Java type of a slot.</summary>
    public JType JavaType(Slot s, int depth = 0)
    {
        s = s.Rep;
        if (s.Element) { var inner = JavaTypeCore(s, depth); return inner.IsPrimitive ? Boxed(inner) : inner; }
        return JavaTypeCore(s, depth);
    }

    JType JavaTypeCore(Slot s, int depth)
    {
        if (s.Decl is not null) return s.Decl;
        // what the uses require: the most derived required class, when the requirements are one line of descent
        if (s.Req.Count > 0 && !s.J.Any(IsContainer))
        {
            var req = s.Req.ToList();
            var most = req.FirstOrDefault(r => req.All(o => o == r || IsSub(r, o)));
            var values = s.C.Where(c => c.Kind == ClassKind.Domain).ToList();
            // the requirement names the type unless values of other classes reach the slot and share a supertype
            bool compatible = values.All(c => c == most || most is not null && IsSub(c, most));
            bool valuesShareType = values.Count > 1 && (Model.LcaAll(values) is not null
                || Interfaces.Values.Any(i => values.All(i.Impls.Contains)));
            if (most is not null && (compatible || !valuesShareType)) return JType.Of(most);
        }
        var jdkContainers = s.J.Where(IsContainer).Distinct().ToList();
        var other = s.J.Where(j => !IsContainer(j) && j != "null" && !(j == "java.lang.Object" && (s.C.Count > 0 || s.J.Count > 1))).ToList();
        // no value says what the slot holds: the JDK type its uses call
        if (jdkContainers.Count == 0 && other.All(o => o == "java.lang.Object") && s.C.Count == 0 && s.Req.Count == 0 && s.JReq.Count > 0)
        {
            jdkContainers = s.JReq.Where(IsContainer).Distinct().ToList();
            if (jdkContainers.Count == 0 && s.JReq.Count == 1) other = [s.JReq.First()];
        }
        // a container and values of another kind (a list or an array of dependencies): Object
        if (jdkContainers.Count > 0 && other.Any(o => o != "java.lang.Object")) return JType.Object;
        if (jdkContainers.Count > 0)
        {
            var cont = jdkContainers.Contains("java.util.List") ? "java.util.List" : jdkContainers.Contains("java.util.Set") ? "java.util.Set" : jdkContainers.Contains("java.util.Map") ? "java.util.Map"
                : jdkContainers.Contains("java.util.Optional") ? "java.util.Optional" : jdkContainers[0];
            if (jdkContainers.Count > 1 && jdkContainers.All(c => c is "java.util.List" or "java.util.Set" or "java.util.NavigableSet" or "java.util.Collection") && jdkContainers.Contains("java.util.List")) cont = "java.util.Collection";
            if (depth > 3) return JType.Of(cont);
            if (cont is "java.util.Map" or "java.util.Map.Entry") return JType.Of(cont, Boxed(JavaType(s.Key, depth + 1)), Boxed(JavaType(s.Val, depth + 1)));
            return JType.Of(cont, Boxed(JavaType(s.Elem, depth + 1)));
        }
        if (s.C.Count > 0 && other.Count == 0 && jdkContainers.Count == 0)
        {
            var domain = s.C.Where(c => c.Kind != ClassKind.Closure).OrderBy(c => c.FullName, StringComparer.Ordinal).ToList();
            var closures = s.C.Where(c => c.Kind == ClassKind.Closure).OrderBy(c => c.FullName, StringComparer.Ordinal).ToList();
            if (domain.Count == 0 && closures.Count > 0) return FunctionalType(closures, s, depth);
            if (closures.Count == 0)
            {
                var lub = Model.LcaAll(domain);
                if (lub is not null && !(s.DeclName is { } dn0 && Interfaces.ContainsKey(dn0))) return JType.Of(lub);
                if (s.DeclName is { } dn && Interfaces.TryGetValue(dn, out var ifc)) return JType.Of(ifc.FullName);
                var common = Interfaces.Values.Where(i => domain.All(i.Impls.Contains)).OrderBy(i => i.Impls.Count).FirstOrDefault();
                if (common.FullName is not null) return JType.Of(common.FullName);
                if (lub is not null) return JType.Of(lub);
            }
        }
        if (other.Count == 1 && s.C.Count == 0) return JType.Of(other[0]);
        if (s.DeclName is { } dn2 && Interfaces.TryGetValue(dn2, out var ifc2)) return JType.Of(ifc2.FullName);
        if (s.DeclName is { } dn3 && dn3.Contains('.')) return JType.Of(dn3);
        return JType.Object;
    }

    static bool IsSub(ClassDef c, ClassDef sup)
    {
        for (var x = c.Super; x is not null; x = x.Super) if (x == sup) return true;
        return false;
    }

    bool ImplementsSame(ClassDef value, ClassDef required) => Interfaces.Values.Any(i => i.Impls.Contains(value) && i.Impls.Contains(required));

    static JType Boxed(JType t) => t.Name switch
    {
        "int" => JType.Integer, "long" => JType.LongBox, "boolean" => JType.BooleanBox, "double" => JType.Of("java.lang.Double"), _ => t,
    };

    /// <summary>A closure's functional interface from the lambda's shape, with its type arguments.</summary>
    JType FunctionalType(List<ClassDef> closures, Slot s, int depth = 0)
    {
        var fn = closures[0].Lambda;
        if (fn is null) return JType.Object;
        JType P(int i) => i < fn.Params.Count && depth < 3 ? Boxed(JavaType(Param(fn, i), depth + 1)) : JType.Object;
        JType R() => fn.Ret is not null && depth < 3 ? Boxed(JavaType(Ret(fn), depth + 1)) : JType.Object;
        return (fn.Params.Count, fn.Ret?.Kind) switch
        {
            (0, null) => JType.Of("java.lang.Runnable"),
            (0, _) => JType.Of("java.util.function.Supplier", R()),
            (1, null) => JType.Of("java.util.function.Consumer", P(0)),
            (1, VKind.Bool) => JType.Of("java.util.function.Predicate", P(0)),
            (1, _) => JType.Of("java.util.function.Function", P(0), R()),
            (2, null) => JType.Of("java.util.function.BiConsumer", P(0), P(1)),
            (2, VKind.Int) => JType.Of("java.util.Comparator", P(0)),
            _ => JType.Of("java.util.function.BiFunction", P(0), P(1), R()),
        };
    }
}
