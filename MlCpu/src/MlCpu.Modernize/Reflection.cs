using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

/// <summary>
/// The reflection and class-path resources of a J3S target (docs/J3S.md section 17: profile keys `reflection` and
/// `resources`), after the lift:
/// <list type="bullet">
/// <item>class ids no allocation names (an abstract class) bound from the emulation's assignability table
/// (JClassMembers.asSubclass) and application-class list (getClassLoader), so a class literal names the right class;</item>
/// <item>X.class.getClassLoader().loadClass("C").asSubclass(S.class).getDeclaredConstructor().newInstance() with a literal
/// name the profile lists, C a subclass of S: new C() (the emulation loads only the listed classes and runs their
/// no-argument constructor, an exception of which traps; javac then checks what the reflective form checked at run time);</item>
/// <item>every class-path resource name the code reads is one the profile declares, absolute for Class.getResourceAsStream
/// (the emulation traps on any other: the modern code would read the JVM's class path instead), else residue.</item>
/// </list>
/// </summary>
static class Reflection
{
    public static void Run(Model m, Dictionary<FunctionDef, Method> methods, List<string> residue)
    {
        BindAbstractIds(m, residue);
        foreach (var meth in methods.Values) meth.Body = ClassLiterals(m, meth.Body);
        foreach (var meth in methods.Values) Fold(m, meth, residue);
        Resources(m, methods, residue);
        if (m.Target is not null) Lifetime(m, methods, residue);
    }

    // ------------------------------------------------------------------ initializer lifetime

    static readonly HashSet<string> Mutators = new(StringComparer.Ordinal)
    {
        "put", "putIfAbsent", "putAll", "remove", "clear", "add", "addAll", "set", "replace", "replaceAll", "compute", "computeIfAbsent",
        "computeIfPresent", "merge", "removeIf", "retainAll", "removeAll", "sort",
    };

    /// <summary>
    /// J3S builds what class initialization allocates once and resets its heap to that state for every case; Java keeps
    /// static state for the whole run. They agree as long as no case changes static state: every write to a static field
    /// of the estate's classes, and every mutating call on one, in the code a case runs (the job's functions and what they
    /// call, class initializers excluded) is residue. The functions checked and what they reach are reported.
    /// </summary>
    static void Lifetime(Model m, Dictionary<FunctionDef, Method> methods, List<string> residue)
    {
        var byName = methods.Keys.Where(f => !f.IsStatic && !f.IsCtor).GroupBy(f => (f.MethodName, f.Params.Count)).ToDictionary(g => g.Key, g => g.ToList());
        var seen = new HashSet<FunctionDef>();
        var work = new Stack<FunctionDef>(methods.Keys.Where(f => f.IsJob));
        while (work.Count > 0)
        {
            var f = work.Pop();
            if (f.IsClinit || !seen.Add(f) || !methods.TryGetValue(f, out var meth)) continue;
            foreach (var st in Passes.All(meth.Body))
            {
                if (st is SAssign { Lhs: EStatic { F: { Owner: { Kind: ClassKind.Domain } } sf } })
                    residue.Add($"{f.Program.Id} {f.Id}: a case writes static {sf.Owner!.SimpleName}.{sf.Name} (J3S resets it per case, Java keeps it)");
                foreach (var e in MlCpu.Modernize.Fold.Exprs(st).SelectMany(Deep))
                {
                    if (e is ECall { Kind: CallKind.Jdk, Name: var mn, Recv: EStatic { F: { Owner: { Kind: ClassKind.Domain } } rf } } && Mutators.Contains(mn))
                        residue.Add($"{f.Program.Id} {f.Id}: a case calls {mn} on static {rf.Owner!.SimpleName}.{rf.Name} (J3S resets it per case, Java keeps it)");
                    if (e is ECall { Fn: { } cf } c && c.Kind is not CallKind.Jdk and not CallKind.JdkStatic)
                        foreach (var g in c.Virtual && byName.TryGetValue((cf.MethodName, cf.Params.Count), out var o) ? o : [cf]) work.Push(g);
                    if (e is ENew { Ctor: { } ct }) work.Push(ct);
                }
            }
        }
        m.CaseReach = seen.Count;
        m.Target!.CaseFunctions = seen.Count;
    }

    // ------------------------------------------------------------------ class ids

    static FunctionDef? Emulated(Model m, string cls, string method) =>
        m.Functions.Values.FirstOrDefault(f => f.Owner is { Kind: ClassKind.Emulation } o && JdkMap.EmulationName(o) == cls && f.MethodName == method);

    static IEnumerable<Stmt> Statements(FunctionDef f)
    {
        var paras = f.Program.Model.Paragraphs;
        for (int i = f.FirstParagraph; i <= f.LastParagraph; i++)
            foreach (var s in Model.Deep(paras[i].Statements)) yield return s;
    }

    static int? Num(Node? n) => n is { K: "num", V: { } v } && int.TryParse(v, out var k) ? k : null;

    /// <summary>
    /// An abstract class has an id but no allocation: J3S lists it in the assignability table of asSubclass (the listed
    /// class's id with each supertype's) and, for an application class, in getClassLoader's list. A supertype id that is an
    /// application class and has exactly one unbound application superclass of the listed class as candidate is bound to it.
    /// </summary>
    static void BindAbstractIds(Model m, List<string> residue)
    {
        var asSub = Emulated(m, "JClassMembers", "asSubclass");
        var loader = Emulated(m, "JClassMembers", "getClassLoader");
        if (asSub is null || loader is null) return;
        var app = new HashSet<int>();
        foreach (var s in Statements(loader))
            if (s.Verb == "IF" && s.Get("cond") is { K: "rel", Op: "eq", R: var r } && Num(r) is int id) app.Add(id);
        var pairs = new List<(int Sub, int Sup)>();
        foreach (var s in Statements(asSub))
            if (s.Verb == "IF" && s.Get("cond") is { K: "and", L: { K: "rel", R: var a }, R: { K: "rel", R: var b } } && Num(a) is int sub && Num(b) is int sup && sub != sup)
                pairs.Add((sub, sup));
        foreach (var sup in pairs.Select(p => p.Sup).Distinct().Where(i => !m.ClassById.ContainsKey(i) && app.Contains(i)))
        {
            var candidates = new HashSet<ClassDef>();
            foreach (var (sub, _) in pairs.Where(p => p.Sup == sup))
                if (m.ClassById.TryGetValue(sub, out var c))
                    for (var x = c.Super; x is not null; x = x.Super)
                        if (x.Kind == ClassKind.Domain && x.Ids.Count == 0) candidates.Add(x);
            if (candidates.Count == 1)
            {
                var c = candidates.Single();
                m.ClassById[sup] = c;
                c.Ids.Add(sup);
            }
            else residue.Add($"class id {sup}: {candidates.Count} candidate classes in the assignability table");
        }
    }

    static List<St> ClassLiterals(Model m, List<St> body)
    {
        Expr E(Expr e) => e is EClassLit { ClassId: int id } cl && m.ClassById.TryGetValue(id, out var c) && cl.T.Class != c
            ? new EClassLit(JType.Of(c)) { Type = cl.Type, ClassId = id } : e.Map(E);
        foreach (var s in Passes.All(body)) Map(s, E);
        return body;
    }

    static void Map(St s, Func<Expr, Expr> f)
    {
        switch (s)
        {
            case SAssign a: if (a.Lhs is not EVar) a.Lhs = f(a.Lhs); a.Rhs = f(a.Rhs); break;
            case SExpr x: x.E = f(x.E); break;
            case SReturn { E: not null } r: r.E = f(r.E); break;
            case SThrow t: t.E = f(t.E); break;
            case SIf i: i.C = f(i.C); break;
            case SLoop l: if (l.C is not null) l.C = f(l.C); if (l.Iterable is not null) l.Iterable = f(l.Iterable); break;
            case SSwitch sw: sw.Subject = f(sw.Subject); break;
        }
    }

    // ------------------------------------------------------------------ reflection

    static bool EmptyArray(Expr? e, Dictionary<Var, Expr> single) => e switch
    {
        null => true,
        EArrayNew { Len: ELit { Value: 0L } } => true,
        EVar v when single.TryGetValue(v.V, out var d) => EmptyArray(d, []),
        _ => false,
    };

    static void Fold(Model m, Method meth, List<string> residue)
    {
        if (m.Target is not { } td) return;
        // single assignments of the method (a variable assigned once): the literal class name, the empty argument arrays
        var assigns = Passes.All(meth.Body).OfType<SAssign>().Where(a => a.Lhs is EVar).GroupBy(a => ((EVar)a.Lhs).V).ToDictionary(g => g.Key, g => g.ToList());
        var single = assigns.Where(kv => kv.Value.Count == 1).ToDictionary(kv => kv.Key, kv => kv.Value[0].Rhs);
        foreach (var list in Lists(meth.Body))
            for (int k = 0; k < list.Count; k++)
            {
                if (list[k] is not SAssign { Lhs: EVar { V: var cls }, Rhs: ECall { Name: "asSubclass", Recv: ECall { Name: "loadClass", Recv: ECall { Name: "getClassLoader", Recv: EClassLit }, Args: [var nameE] }, Args: [EClassLit sup] } } a)
                    continue;
                var name = nameE is ELit { Value: string n } ? n : nameE is EVar nv && single.TryGetValue(nv.V, out var nd) && nd is ELit { Value: string n2 } ? n2 : null;
                var where = $"{meth.Fn.Program.Id} {meth.Fn.Id}";
                if (name is null) { residue.Add($"{where}: loadClass of a name that is not a literal"); continue; }
                if (!td.Reflect.Contains(name)) { residue.Add($"{where}: loadClass(\"{name}\") of a class the profile does not list (ClassNotFoundException)"); continue; }
                var c = m.Classes.Values.FirstOrDefault(x => x.FullName == name && x.Kind == ClassKind.Domain);
                var s = sup.T.Class;
                bool sub = false;
                for (var x = c; x is not null && s is not null; x = x.Super) if (x == s) sub = true;
                if (c is null || !sub) { residue.Add($"{where}: loadClass(\"{name}\").asSubclass({sup.T.Name}) not provably a subclass"); continue; }
                var ctor = c.Functions.FirstOrDefault(f => f.IsCtor && f.Params.Count == 0);
                if (ctor is null) { residue.Add($"{where}: {name} has no no-argument constructor"); continue; }
                // every use of the class is getDeclaredConstructor().newInstance() without arguments
                int uses = 0, folded = 0;
                Expr Rewrite(Expr e)
                {
                    if (e is ECall { Name: "newInstance", Recv: ECall { Name: "getDeclaredConstructor", Recv: EVar { V: var cv }, Args: var cargs }, Args: var nargs }
                        && cv == cls && cargs.Count <= 1 && nargs.Count <= 1 && EmptyArray(cargs.FirstOrDefault(), single) && EmptyArray(nargs.FirstOrDefault(), single))
                    {
                        folded++;
                        return new ENew { Class = c, Ctor = ctor, Type = JType.Of(c) };
                    }
                    return e.Map(Rewrite);
                }
                foreach (var st in Passes.All(meth.Body)) Map(st, Rewrite);
                foreach (var st in Passes.All(meth.Body)) foreach (var e in Fold_Exprs(st)) uses += Count(e, cls);
                if (folded == 0 || uses > 0) { residue.Add($"{where}: the loaded class {name} is used otherwise than to construct it"); continue; }
                list.RemoveAt(k);
                k--;
                m.ReflectionFolds.TryAdd($"{where}: {name}", c.FullName);
            }
        // the argument arrays and the class name nothing reads any more
        if (!m.ReflectionFolds.Keys.Any(k => k.StartsWith($"{meth.Fn.Program.Id} {meth.Fn.Id}:", StringComparison.Ordinal))) return;
        foreach (var list in Lists(meth.Body))
            list.RemoveAll(s => s is SAssign { Lhs: EVar { V: var v }, Rhs: EArrayNew or ELit } && single.ContainsKey(v)
                && !Passes.All(meth.Body).SelectMany(Fold_Exprs).Any(e => Count(e, v) > 0));
    }

    static IEnumerable<Expr> Fold_Exprs(St s) => s is SAssign { Lhs: EVar } a ? [a.Rhs] : MlCpu.Modernize.Fold.Exprs(s);

    static int Count(Expr e, Var v) => (e is EVar ev && ev.V == v ? 1 : 0) + e.Children.Sum(c => Count(c, v));

    static IEnumerable<List<St>> Lists(List<St> body)
    {
        yield return body;
        foreach (var s in Passes.All(body))
            switch (s)
            {
                case SIf i: yield return i.Then; yield return i.Else; break;
                case SLoop l: yield return l.Body; break;
                case SBlock b: yield return b.Body; break;
                case STry t: yield return t.Body; foreach (var c in t.Catches) yield return c.Body; break;
                case SSwitch sw: foreach (var c in sw.Cases) yield return c.Body; if (sw.Default is not null) yield return sw.Default; break;
            }
    }

    // ------------------------------------------------------------------ resources

    /// <summary>
    /// The resource names the code reads: a literal, or a parameter every call passes a literal for. Each must be declared
    /// (the emulation traps on another: the modern code must not read whatever the JVM's class path holds), and absolute for
    /// Class.getResourceAsStream (a relative name traps).
    /// </summary>
    static void Resources(Model m, Dictionary<FunctionDef, Method> methods, List<string> residue)
    {
        var emptyResources = new HashSet<Expr>(ReferenceEqualityComparer.Instance);
        foreach (var meth in methods.Values)
            foreach (var st in Passes.All(meth.Body))
                foreach (var e in MlCpu.Modernize.Fold.Exprs(st).SelectMany(Deep))
                {
                    if (e is not ECall { Name: "getResourceAsStream" or "getResources", Args: [var arg] } call) continue;
                    var where = $"{meth.Fn.Program.Id} {meth.Fn.Id}";
                    bool viaClass = call.Recv is ECall { Name: "getClass" } or EClassLit;
                    var names = Names(arg, meth, methods);
                    if (names is null) { residue.Add($"{where}: {call.Name} of a name that is not a literal"); continue; }
                    foreach (var n in names)
                    {
                        if (viaClass && call.Name == "getResourceAsStream" && !n.StartsWith('/')) { residue.Add($"{where}: relative resource name {n} (the emulation traps)"); continue; }
                        var key = viaClass && call.Name == "getResourceAsStream" ? n[1..] : n;
                        if (m.Target is null || !m.Target.Declared(key)) { residue.Add($"{where}: resource {key} the profile does not declare (the emulation traps)"); continue; }
                        m.ResourceReads.TryAdd($"{where}: {call.Name}({n})", key);
                    }
                    // getResources of names the target has no copy of (declared, absent at build time): an empty
                    // enumeration, as the emulation's; the JVM's class path of the modern run would find any copy a jar has
                    if (call.Name == "getResources" && m.Target is { } td && names.All(n => td.Declared(n) && td.First(n) is null))
                        emptyResources.Add(call);
                }
        if (emptyResources.Count == 0) return;
        Expr Empty(Expr e) => emptyResources.Contains(e)
            ? new ECall { Kind = CallKind.JdkStatic, Owner = "java.util.Collections", Name = "emptyEnumeration", Type = JType.Of("java.util.Enumeration") }
            : e.Map(Empty);
        foreach (var meth in methods.Values) foreach (var st in Passes.All(meth.Body)) Map(st, Empty);
    }

    static List<string>? Names(Expr arg, Method meth, Dictionary<FunctionDef, Method> methods)
    {
        if (arg is ELit { Value: string s }) return [s];
        if (arg is not EVar { V: var v } || meth.L.Params.IndexOf(v) is var pi && pi < 0) return null;
        // a parameter: the literal every caller passes (a parameter the method assigns is not one)
        if (Passes.All(meth.Body).Any(x => x is SAssign { Lhs: EVar { V: var w } } && w == v)) return null;
        var res = new List<string>();
        int calls = 0;
        foreach (var other in methods.Values)
            foreach (var e in Passes.All(other.Body).SelectMany(MlCpu.Modernize.Fold.Exprs).SelectMany(Deep))
                if (e is ECall { Fn: { } f, Args: var args } && f == meth.Fn)
                {
                    calls++;
                    if (pi < args.Count && args[pi] is ELit { Value: string ls }) res.Add(ls); else return null;
                }
        return calls == 0 ? null : res;
    }

    static IEnumerable<Expr> Deep(Expr e)
    {
        yield return e;
        foreach (var c in e.Children) foreach (var d in Deep(c)) yield return d;
    }
}
