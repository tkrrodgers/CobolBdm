namespace MlCpu.Modernize;

/// <summary>
/// R5 program-interfaces over a lifted estate: builds the opaque units and uses <see cref="ProgramInterfaces.Decide"/>
/// decides on and applies the decisions to <see cref="Shapes.Widened"/> (the writer prints the interface type where the
/// declaration is printed; expression types, casts and names keep the lift's). Facts, taken once before anything else
/// (and before the self check's poison): the declarations' types as the existing type inference solved them
/// (<see cref="JavaWriter.VarTypeOf"/>, <see cref="JavaWriter.ParamTypeOf"/>, field and result slots), the printed member
/// names as equivalence classes (Java binds by name and arity), the checked exceptions, the recovered interfaces and
/// their implementations (symbols), superclass links, R1's COBOL CALL census (<see cref="ApiSurface.ExternalCallers"/>),
/// the reflection facts and the lifted IR. It never reads comment text, a signature or parameter-type string, an
/// original source or a name.
/// </summary>
public sealed class ProgramInterfacesSurface
{
    readonly Model M;
    readonly Shapes S;
    readonly TypeInference T;
    readonly Dictionary<FunctionDef, Method> methods;
    readonly AppModel app;

    // ---- the snapshot (before poisoning)
    readonly Dictionary<FieldDef, JType> fieldT = [];
    readonly Dictionary<(FunctionDef, int), JType> paramT = [];
    readonly Dictionary<(FunctionDef, int), JType> ifaceParamT = [];
    readonly Dictionary<FunctionDef, JType> retT = [];
    readonly Dictionary<Var, JType> varT = [];
    readonly Dictionary<FunctionDef, int> nameClass = [];
    readonly Dictionary<FunctionDef, string> objectMember = [];
    readonly HashSet<FunctionDef> jdkContract = [];
    readonly Dictionary<(FunctionDef, int), bool> genericParam = [];
    Dictionary<FunctionDef, List<string>> throws = [];
    Dictionary<FunctionDef, List<string>> external = [];
    HashSet<FunctionDef> entryFns = [];
    HashSet<ClassDef> reflected = [];
    Dictionary<ClassDef, string> typeId = [];
    readonly Dictionary<Shapes.Iface, string> ifaceId = [];
    readonly Dictionary<Shapes.Iface, string> ifaceDisplay = [];

    // ---- units
    readonly Dictionary<FieldDef, Build> fieldUnit = [];
    readonly Dictionary<(FunctionDef, int), Build> paramUnit = [];
    readonly Dictionary<FunctionDef, Build> returnUnit = [];
    readonly Dictionary<Var, Build> localUnit = [];
    readonly Dictionary<Var, (FunctionDef Fn, int I)> paramVar = [];
    readonly HashSet<FunctionDef> calledInIr = [];
    readonly List<string> reflectiveSites = [];
    readonly List<string> notes = [];

    sealed class Build
    {
        public required string Id;
        public required string Kind;
        public required JType Type;
        public ClassDef? Class;
        public required List<string> Options;
        public Dictionary<string, Shapes.Iface> Ifaces = [];
        public List<string> Keep = [];
        public List<ProgramInterfaces.Use> Uses = [];
        public string Display = "";
        public List<string> SourceIds = [];
        public Action<ProgramInterfaces.Widened, JType>? Apply;
    }

    ProgramInterfacesSurface(Model m, Shapes s, TypeInference t, Dictionary<FunctionDef, Method> methods, AppModel app)
    {
        M = m; S = s; T = t; this.methods = methods; this.app = app;
    }

    public static (string Ledger, List<ProgramInterfaces.Decision> Decisions) Run(Model m, Shapes s, TypeInference t, Dictionary<FunctionDef, Method> methods, AppModel app,
        ModelEdits? edits, bool poison, Action<string> log)
    {
        var a = new ProgramInterfacesSurface(m, s, t, methods, app);
        a.Snapshot();
        Action? restore = poison ? Poison(m, app, methods) : null;
        try
        {
            var units = a.Units(edits);
            var input = units.Select(b => new ProgramInterfaces.Unit
            {
                Id = b.Id, Kind = b.Kind, From = b.Class is { } c ? a.typeId.GetValueOrDefault(c, "?") : b.Type.Name, Options = b.Options, Keep = b.Keep, Uses = b.Uses,
                Display = b.Display, FromDisplay = poison ? "" : b.Type.ToString(), SourceIds = b.SourceIds,
                OptionDisplay = b.Ifaces.ToDictionary(x => x.Key, x => poison ? "" : a.ifaceDisplay[x.Value]),
            }).ToList();
            if (poison) { input.Reverse(); foreach (var u in input) u.Uses.Reverse(); }
            var decisions = ProgramInterfaces.Decide(input);
            var widened = new ProgramInterfaces.Widened();
            var byId = units.ToDictionary(b => b.Id, StringComparer.Ordinal);
            foreach (var d in decisions.Where(d => d.Applied))
            {
                var b = byId[d.U.Id];
                // the interface keeps the declaration's type arguments: never Object, never a cast
                b.Apply!(widened, b.Ifaces.TryGetValue(d.To!, out var iface) ? JType.Of(iface.FullName) : JType.Of(d.To!, [.. b.Type.Args]));
            }
            s.Widened = widened;
            log($"R5 {ProgramInterfaces.RuleName}: {decisions.Count} candidate declarations, {decisions.Count(d => d.Applied)} widened, {decisions.Count(d => d.Outcome == "kept")} kept, {decisions.Count(d => d.Outcome == "skipped")} skipped");
            var inputs = new
            {
                interfaces = a.ifaceId.Count,
                implementingClasses = s.Implements.Count(x => x.Value.Count > 0),
                declarations = new
                {
                    fields = a.fieldT.Keys.Count(f => f.Owner is { } o && Shapes.Emitted(o)),
                    candidates = units.Count,
                },
                externalOrUnknownCallers = a.external.Count,
                reflectedClasses = a.reflected.Count,
                reflectiveSites = a.reflectiveSites,
                modelEdits = edits is not null,
                poisoned = poison ? "self check: class, package, method, field and variable names, FRAME signatures and parameter type strings replaced by random tokens, J3S-style comments planted, inputs permuted" : null,
            };
            return (ProgramInterfaces.Ledger(decisions, inputs, Skipped(), a.notes), decisions);
        }
        finally { restore?.Invoke(); }
    }

    // ------------------------------------------------------------------ the snapshot

    static IEnumerable<ClassDef> Chain(ClassDef c) { for (var x = c; x is not null; x = x.Super) yield return x; }

    IEnumerable<ClassDef> Down(ClassDef c)
    {
        foreach (var sc in M.Classes.Values.Where(x => x.Super == c).OrderBy(x => x.FullName, StringComparer.Ordinal)) { yield return sc; foreach (var d in Down(sc)) yield return d; }
    }

    static ClassDef TopOf(ClassDef c) { while (c.Outer is not null) c = c.Outer; return c; }

    void Snapshot()
    {
        var emitted = M.Classes.Values.Where(Shapes.Emitted).ToList();
        typeId = ApiSurface.OpaqueTypeIds(emitted, out int ties);
        if (ties > 0) notes.Add($"{ties} groups of classes share a COBOL-side key: their opaque ids are ordered by the model's class key (ids are ledger labels, not decision inputs)");
        // interfaces: opaque ids in the order of their implementations' opaque class ids
        var impls = new Dictionary<Shapes.Iface, List<ClassDef>>();
        foreach (var (c, l) in S.Implements) foreach (var i in l) (impls.TryGetValue(i, out var x) ? x : impls[i] = []).Add(c);
        int k = 0;
        foreach (var i in S.Interfaces.Values.OrderBy(i => impls.GetValueOrDefault(i)?.Select(c => typeId.GetValueOrDefault(c, "~")).Min(StringComparer.Ordinal) ?? "~", StringComparer.Ordinal)
                     .ThenBy(i => i.FullName, StringComparer.Ordinal))
        {
            ifaceId[i] = "I" + (++k).ToString("D2");
            ifaceDisplay[i] = i.FullName;
        }
        var fns = M.Functions.Values.Concat(methods.Keys).Concat(S.Interfaces.Values.SelectMany(i => i.Methods.Values)).Distinct()
            .OrderBy(f => f.Program.Id, StringComparer.Ordinal).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in fns)
        {
            var printed = S.MethodName(f);
            nameClass[f] = names.TryGetValue(printed, out var n) ? n : names[printed] = names.Count;
            if (printed is "equals" or "hashCode" or "toString") objectMember[f] = printed;
            if (ApiSurface.JdkContracts.Contains((printed, f.Params.Count))) jdkContract.Add(f);
            for (int i = 0; i < f.Params.Count; i++)
            {
                paramT[(f, i)] = JavaWriter.ParamTypeOf(T, f, i);
                ifaceParamT[(f, i)] = T.JavaType(T.Param(f, i));
                genericParam[(f, i)] = T.Param(f, i).Rep.Generic;
            }
            if (f.Ret is not null) retT[f] = T.JavaType(T.Ret(f));
        }
        foreach (var c in M.Classes.Values)
            foreach (var f in c.Fields.Concat(c.Statics)) fieldT[f] = T.JavaType(T.Of(f));
        foreach (var mm in methods.Values)
        {
            foreach (var v in mm.L.Params.Concat(mm.L.Vars).Concat(VarsOf(mm.Body)).Distinct())
                varT[v] = JavaWriter.VarTypeOf(T, v);
            for (int i = 0; i < mm.L.Params.Count; i++) paramVar[mm.L.Params[i]] = (mm.Fn, i);
        }
        throws = CheckedExceptions.Compute(methods);
        (external, entryFns) = ApiSurface.ExternalCallers(M, S, methods, app);
        reflected = ApiSurface.ReflectedClasses(M);
    }

    /// <summary>For the ledger only (read after any poisoning).</summary>
    string FnDisplay(FunctionDef f) => (f.Owner?.FullName ?? f.Program.Id) + "." + S.MethodName(f);

    static IEnumerable<Var> VarsOf(List<St> body)
    {
        foreach (var st in Passes.All(body))
        {
            if (st is SAssign { Lhs: EVar lv }) yield return lv.V;
            if (st is SLoop { Each: { } ev }) yield return ev;
            if (st is STry tr) foreach (var (_, cv, _) in tr.Catches) yield return cv;
            foreach (var e in Fold.Exprs(st)) foreach (var v in ExprVars(e)) yield return v;
            if (st is SSwitch sw) foreach (var (labels, _) in sw.Cases) foreach (var e in labels) foreach (var v in ExprVars(e)) yield return v;
        }
    }

    static IEnumerable<Var> ExprVars(Expr e)
    {
        if (e is EVar v) yield return v.V;
        foreach (var c in e.Children) foreach (var x in ExprVars(c)) yield return x;
    }

    // ------------------------------------------------------------------ units

    string Site(FunctionDef fn, St? st) => fn.Program.Id + " " + fn.Id + (st?.Site is { } s ? " " + s : "");

    List<Build> Units(ModelEdits? edits)
    {
        var global = new List<string>();
        foreach (var mm in methods.Values.OrderBy(x => x.Fn.Program.Id, StringComparer.Ordinal).ThenBy(x => x.Fn.Id, StringComparer.Ordinal))
            foreach (var st in Passes.All(mm.Body))
                foreach (var e in Fold.Exprs(st))
                    Visit(e, x =>
                    {
                        if (x is ECall { Kind: CallKind.Jdk or CallKind.JdkStatic } jc && ApiSurface.ReflectiveCalls.Contains(jc.Name)) reflectiveSites.Add($"{Site(mm.Fn, st)}: {jc.Name}");
                        if (x is ECall { Fn: { } cf }) calledInIr.Add(cf);
                        if (x is ENew { Ctor: { } ct }) calledInIr.Add(ct);
                    });
        if (edits is not null) global.Add("model edits: their Java text may use any declaration, outside the IR");
        if (reflectiveSites.Count > 0) global.Add($"reflection: the code looks members up at run time ({reflectiveSites.Count} sites)");

        var all = new List<Build>();
        var emitted = M.Classes.Values.Where(Shapes.Emitted).OrderBy(c => typeId[c], StringComparer.Ordinal).ToList();
        int nf = 0, np = 0, nr = 0, nl = 0;
        foreach (var c in emitted)
        {
            var sh = S.Of(c);
            if (sh.IsEnum || c.IsEnum || sh.IsEnumBody || c.IsInterface) continue;
            var classKeep = new List<string>(global);
            if (reflected.Contains(c)) classKeep.Add("reflection/provider registration: the class is loaded by name");
            var accessors = sh.Getters.Concat(sh.ExtraGetters.Select(x => x.Field)).ToHashSet();
            var components = sh.Components is not null ? sh.Components.ToHashSet() : [];
            foreach (var f in c.Fields.Concat(c.Statics.Where(x => !sh.EnumFields.Contains(x))).OrderBy(f => f.IsStatic).ThenBy(f => f.CobolName, StringComparer.Ordinal))
            {
                if (components.Contains(f)) continue;
                var b = Candidate(f.IsStatic ? "static" : "field", fieldT[f], f.IsStatic ? "S" : "F", ref nf);
                if (b is null) continue;
                b.Keep.AddRange(classKeep);
                if (accessors.Contains(f)) b.Keep.Add("accessor: a generated getter prints the field's type");
                b.Display = (c.FullName + "." + f.Name);
                b.SourceIds = [c.Programs.FirstOrDefault() ?? "", f.CobolName];
                b.Apply = (w, to) => w.Fields[f] = to;
                fieldUnit[f] = b;
                all.Add(b);
            }
            foreach (var mm in methods.Values.Where(x => x.Fn.Owner == c && !x.Fn.IsLambda && !x.Fn.IsClinit && !x.Fn.IsJob)
                         .OrderBy(x => x.Fn.Program.Id, StringComparer.Ordinal).ThenBy(x => x.Fn.Id, StringComparer.Ordinal))
            {
                var fn = mm.Fn;
                if (fn == sh.CanonicalCtor && sh.Components is not null) continue;
                var fnKeep = new List<string>(classKeep);
                if (external.TryGetValue(fn, out var ext)) fnKeep.Add("external or unknown caller: " + string.Join("; ", ext.Take(2)) + (ext.Count > 2 ? $"; +{ext.Count - 2}" : ""));
                else if (entryFns.Contains(fn) && !calledInIr.Contains(fn)) fnKeep.Add("unknown caller: a COBOL ENTRY with no call in the lifted IR");
                fnKeep.AddRange(Obligations(fn));
                for (int i = 0; i < mm.L.Params.Count && i < fn.Params.Count; i++)
                {
                    var b = Candidate("parameter", paramT[(fn, i)], "P", ref np);
                    if (b is null) continue;
                    b.Keep.AddRange(fnKeep);
                    if (genericParam[(fn, i)]) b.Keep.Add("type variable: the parameter is generic");
                    if (Siblings(fn) is { } sib) b.Keep.Add(sib);
                    b.Display = FnDisplay(fn) + " parameter " + (i + 1);
                    b.SourceIds = [fn.Program.Id, fn.Id];
                    int pi = i;
                    b.Apply = (w, to) => w.Params[(fn, pi)] = to;
                    paramUnit[(fn, i)] = b;
                    all.Add(b);
                }
                if (fn.Ret is not null && !fn.IsCtor && retT.TryGetValue(fn, out var rt))
                {
                    var b = Candidate("return", rt, "R", ref nr);
                    if (b is not null)
                    {
                        b.Keep.AddRange(fnKeep);
                        b.Display = FnDisplay(fn) + " result";
                        b.SourceIds = [fn.Program.Id, fn.Id];
                        b.Apply = (w, to) => w.Returns[fn] = to;
                        returnUnit[fn] = b;
                        all.Add(b);
                    }
                }
                var seen = new HashSet<Var>(mm.L.Params);
                var catchVars = Passes.All(mm.Body).OfType<STry>().SelectMany(t => t.Catches.Select(x => x.V)).ToHashSet();
                foreach (var v in VarsOf(mm.Body))
                {
                    if (!seen.Add(v) || v.IsThis || catchVars.Contains(v) || mm.L.Captured.ContainsValue(v) || !varT.TryGetValue(v, out var vt)) continue;
                    var b = Candidate("local", vt, "L", ref nl);
                    if (b is null) continue;
                    b.Keep.AddRange(classKeep);
                    b.Display = FnDisplay(fn) + " local " + v.Name;
                    b.SourceIds = [fn.Program.Id, fn.Id];
                    b.Apply = (w, to) => w.Locals[v] = to;
                    localUnit[v] = b;
                    all.Add(b);
                }
            }
        }
        // the uses: every lifted body (domain, closures, the application layer's), the static initializers, the enum constants
        foreach (var mm in methods.Values.OrderBy(x => x.Fn.Program.Id, StringComparer.Ordinal).ThenBy(x => x.Fn.Id, StringComparer.Ordinal))
            Body(mm);
        foreach (var c in emitted)
        {
            var sh = S.Of(c);
            foreach (var (f, e) in sh.StaticInit.OrderBy(x => x.Key.CobolName, StringComparer.Ordinal))
                Expr(e, Flow(f, "static initializer " + f.CobolName), null, "static initializer " + f.CobolName);
            foreach (var (_, args) in sh.EnumValues) foreach (var e in args) Expr(e, Esc("an enum constant argument"), null, "enum constant");
        }
        return all;
    }

    Build? Candidate(string kind, JType t, string prefix, ref int n)
    {
        List<string> options;
        var ifaces = new Dictionary<string, Shapes.Iface>(StringComparer.Ordinal);
        ClassDef? cls = null;
        if (t.Class is { Kind: ClassKind.Domain } c && t.Args.Count == 0 && !c.IsInterface && Shapes.Emitted(c))
        {
            cls = c;
            foreach (var i in Chain(c).SelectMany(x => S.Implements.GetValueOrDefault(x) ?? []).Distinct()) ifaces[ifaceId[i]] = i;
            options = ifaces.Keys.Order(StringComparer.Ordinal).ToList();
            if (options.Count == 0) return null;
        }
        else if (ProgramInterfaces.JdkOptions.TryGetValue(t.Name, out var jo)) options = [.. jo];
        else if (ProgramInterfaces.JdkKeep.ContainsKey(t.Name)) options = [];
        else return null;
        var b = new Build { Id = prefix + (++n).ToString("D3"), Kind = kind, Type = t, Class = cls, Options = options, Ifaces = ifaces };
        if (ProgramInterfaces.JdkKeep.TryGetValue(t.Name, out var why)) b.Keep.Add("implementation contract: " + why);
        return b;
    }

    /// <summary>Override, interface, abstract and JDK-contract obligations of a method's signature.</summary>
    IEnumerable<string> Obligations(FunctionDef fn)
    {
        if (fn.IsStatic || fn.IsCtor || fn.Owner is not { } owner) yield break;
        bool Same(FunctionDef g) => g != fn && !g.IsStatic && !g.IsCtor && g.Params.Count == fn.Params.Count && nameClass.GetValueOrDefault(g, -1) == nameClass.GetValueOrDefault(fn, -3);
        if (Chain(owner).Skip(1).SelectMany(x => x.Functions).Any(Same)) yield return "override: it overrides a superclass method";
        if (Down(owner).SelectMany(x => x.Functions).Any(Same)) yield return "overridden: a subclass overrides it";
        var ifs = Chain(owner).SelectMany(x => S.Implements.GetValueOrDefault(x) ?? []).ToList();
        if (ifs.Any(i => i.Methods.Values.Any(g => g == fn || Same(g))) || S.Interfaces.Values.Any(i => i.Methods.Values.Contains(fn)))
            yield return "interface: it implements or prints an interface method";
        if (Chain(owner).Any(x => S.Of(x).AbstractMethods.Any(g => g == fn || Same(g)))) yield return "abstract: an abstract method's signature";
        if (jdkContract.Contains(fn)) yield return "JDK contract: an Object, Comparable or Throwable method";
    }

    /// <summary>Overloads of the declaring method (a sibling with the same name and arity): widening a parameter may rebind calls.</summary>
    string? Siblings(FunctionDef fn)
    {
        if (fn.Owner is not { } owner) return null;
        IEnumerable<FunctionDef> scope = fn.IsCtor ? owner.Functions.Where(g => g.IsCtor) : Chain(owner).Concat(Down(owner)).SelectMany(x => x.Functions)
            .Where(g => !g.IsCtor && g.IsStatic == fn.IsStatic && nameClass.GetValueOrDefault(g, -1) == nameClass.GetValueOrDefault(fn, -3));
        var sigs = scope.Where(g => g.Params.Count == fn.Params.Count).Select(Sig).ToList();
        return ProgramInterfaces.Overloaded(sigs) ? "overloaded: the method has a sibling with the same name and arity" : null;
    }

    string Sig(FunctionDef g) => string.Join(",", Enumerable.Range(0, g.Params.Count).Select(i => paramT.GetValueOrDefault((g, i))?.ToString() ?? "?"));

    string ClassSig(FunctionDef g) => Sig(g) + "->" + (retT.GetValueOrDefault(g)?.ToString() ?? "void") + " throws " + string.Join(",", throws.GetValueOrDefault(g) ?? []);

    /// <summary>An interface method as the interface prints it (no throws clause).</summary>
    string IfaceSig(FunctionDef g) => string.Join(",", Enumerable.Range(0, g.Params.Count).Select(i => ifaceParamT.GetValueOrDefault((g, i))?.ToString() ?? "?"))
        + "->" + (retT.GetValueOrDefault(g)?.ToString() ?? "void") + " throws ";

    // ------------------------------------------------------------------ uses

    abstract record Ctx;
    sealed record CReceiver(ECall Call) : Ctx;
    sealed record CFlow(Build? Unit, JType? Fixed, string What, string? Obstacle = null, bool Argument = false) : Ctx;
    sealed record CPlain(ProgramInterfaces.UseKind Kind) : Ctx;
    sealed record CEsc(string Why) : Ctx;

    static Ctx Esc(string why) => new CEsc(why);

    CFlow Flow(FieldDef f, string what) => new(fieldUnit.GetValueOrDefault(f), fieldT.GetValueOrDefault(f), what);

    CFlow FlowVar(Var v, string what) =>
        paramVar.TryGetValue(v, out var p) ? new(paramUnit.GetValueOrDefault(p), paramT.GetValueOrDefault(p), what) : new(localUnit.GetValueOrDefault(v), varT.GetValueOrDefault(v), what);

    void Body(Method mm)
    {
        var fn = mm.Fn;
        bool lambda = fn.IsLambda || fn.Owner?.Kind == ClassKind.Closure;
        // the class whose file prints the body (a lambda's: where its expression is printed: unknown here)
        var printedIn = !lambda && !fn.IsJob && fn.Owner is { Kind: ClassKind.Domain } o ? TopOf(o) : null;
        Stmts(mm.Body, fn, lambda, printedIn);
    }

    void Stmts(List<St> list, FunctionDef fn, bool lambda, ClassDef? printedIn)
    {
        foreach (var st in list) Stmt(st, fn, lambda, printedIn);
    }

    void Stmt(St st, FunctionDef fn, bool lambda, ClassDef? printedIn)
    {
        var site = Site(fn, st);
        switch (st)
        {
            case SAssign a:
            {
                Ctx target = Esc("a store into " + a.Lhs.GetType().Name);
                switch (a.Lhs)
                {
                    case EVar lv: target = FlowVar(lv.V, "assignment"); break;
                    case EField lf:
                        Expr(lf.Obj, Esc("a field access through the value"), printedIn, site);
                        FieldAccess(lf, printedIn, lambda, write: true);
                        target = Flow(lf.F, "field assignment");
                        break;
                    case EStatic ls: target = Flow(ls.F, "static assignment"); break;
                    default: foreach (var ch in a.Lhs.Children) Expr(ch, Esc("an index or record store"), printedIn, site); break;
                }
                Expr(a.Rhs, target, printedIn, site, lambda);
                break;
            }
            case SExpr e: Expr(e.E, new CPlain(ProgramInterfaces.UseKind.Discard), printedIn, site, lambda); break;
            case SReturn { E: { } r }:
                Expr(r, lambda ? Esc("a lambda result") : fn.Ret is null || fn.IsCtor ? Esc("a return without a result type")
                    : new CFlow(returnUnit.GetValueOrDefault(fn), retT.GetValueOrDefault(fn), "return"), printedIn, site, lambda);
                break;
            case SThrow t: Expr(t.E, Esc("thrown"), printedIn, site, lambda); break;
            case SIf i:
                Expr(i.C, Esc("a condition"), printedIn, site, lambda);
                Stmts(i.Then, fn, lambda, printedIn);
                Stmts(i.Else, fn, lambda, printedIn);
                break;
            case SLoop l:
                if (l.Init is not null) Stmt(l.Init, fn, lambda, printedIn);
                if (l.C is not null) Expr(l.C, Esc("a condition"), printedIn, site, lambda);
                if (l.Iterable is not null) Expr(l.Iterable, Esc("iterated"), printedIn, site, lambda);
                Stmts(l.Body, fn, lambda, printedIn);
                if (l.Update is not null) Stmt(l.Update, fn, lambda, printedIn);
                break;
            case SBlock b: Stmts(b.Body, fn, lambda, printedIn); break;
            case SSwitch sw:
                Expr(sw.Subject, Esc("a switch subject"), printedIn, site, lambda);
                foreach (var (labels, body) in sw.Cases)
                {
                    foreach (var e in labels) Expr(e, Esc("a case label"), printedIn, site, lambda);
                    Stmts(body, fn, lambda, printedIn);
                }
                if (sw.Default is not null) Stmts(sw.Default, fn, lambda, printedIn);
                break;
            case STry tr:
                Stmts(tr.Body, fn, lambda, printedIn);
                foreach (var (_, _, body) in tr.Catches) Stmts(body, fn, lambda, printedIn);
                break;
            case SComment or SBreak or SContinue or LLabel or LGoto: break;
            default:
                foreach (var e in Fold.Exprs(st)) Expr(e, Esc("a low-level statement"), printedIn, site, lambda);
                break;
        }
    }

    /// <summary>A field read or written outside its own top-level class (or in a lambda body) prints an accessor: the field keeps its type.</summary>
    void FieldAccess(EField f, ClassDef? printedIn, bool lambda, bool write)
    {
        if (!fieldUnit.TryGetValue(f.F, out var u) || write) return;
        bool direct = !lambda && printedIn is not null && f.F.Owner is { } o && (f.Obj is EThis || TopOf(o) == printedIn);
        if (!direct && !u.Keep.Contains("accessor: read from another class or a lambda body (the writer prints a getter typed with the field)"))
            u.Keep.Add("accessor: read from another class or a lambda body (the writer prints a getter typed with the field)");
    }

    void Expr(Expr e, Ctx ctx, ClassDef? printedIn, string site, bool lambda = false)
    {
        switch (e)
        {
            case EVar v:
                if (paramVar.TryGetValue(v.V, out var p) && paramUnit.TryGetValue(p, out var pu)) Record(pu, ctx, site);
                else if (localUnit.TryGetValue(v.V, out var lu)) Record(lu, ctx, site);
                return;
            case EField f:
                Expr(f.Obj, Esc("a field access through the value"), printedIn, site, lambda);
                FieldAccess(f, printedIn, lambda, write: false);
                if (fieldUnit.TryGetValue(f.F, out var fu)) Record(fu, ctx, site);
                return;
            case EStatic s:
                if (fieldUnit.TryGetValue(s.F, out var su)) Record(su, ctx, site);
                return;
            case ECall c:
            {
                if (c.Recv is not null) Expr(c.Recv, new CReceiver(c), printedIn, site, lambda);
                for (int i = 0; i < c.Args.Count; i++) Expr(c.Args[i], ArgCtx(c, i), printedIn, site, lambda);
                if (c.Fn is { } rf && c.Kind is CallKind.Static or CallKind.Instance or CallKind.Super && returnUnit.TryGetValue(rf, out var ru)) Record(ru, ctx, site);
                return;
            }
            case ENew n:
                for (int i = 0; i < n.Args.Count; i++)
                {
                    Ctx ac = n.Ctor is { } ct && i < ct.Params.Count
                        ? new CFlow(paramUnit.GetValueOrDefault((ct, i)), paramT.GetValueOrDefault((ct, i)), "constructor argument",
                            CtorOverloaded(ct) ? "outbound overload: the constructor has a sibling with the same arity" : genericParam.GetValueOrDefault((ct, i)) ? "a type variable parameter" : null, true)
                        : Esc("a constructor argument of a JDK or unlifted class");
                    Expr(n.Args[i], ac, printedIn, site, lambda);
                }
                return;
            case EBinary b when b.Op is "==" or "!=":
                if (b.L is ELit { IsNull: true }) Expr(b.R, new CPlain(ProgramInterfaces.UseKind.NullCompare), printedIn, site, lambda);
                else if (b.R is ELit { IsNull: true }) Expr(b.L, new CPlain(ProgramInterfaces.UseKind.NullCompare), printedIn, site, lambda);
                else { Expr(b.L, Esc("a reference comparison"), printedIn, site, lambda); Expr(b.R, Esc("a reference comparison"), printedIn, site, lambda); }
                return;
            case EBinary b when b.Op == "+":
                Expr(b.L, new CPlain(ProgramInterfaces.UseKind.StringConversion), printedIn, site, lambda);
                Expr(b.R, new CPlain(ProgramInterfaces.UseKind.StringConversion), printedIn, site, lambda);
                return;
            case ELambda l:
                foreach (var v in l.Captures.Values) Expr(v, Esc("a lambda capture"), printedIn, site, lambda);
                return;
            case EInstanceOf io: Expr(io.E, Esc("instanceof"), printedIn, site, lambda); return;
            case ECast cc: Expr(cc.E, Esc("a cast"), printedIn, site, lambda); return;
            case ECond cd:
                Expr(cd.C, Esc("a condition"), printedIn, site, lambda);
                Expr(cd.A, Esc("a conditional expression"), printedIn, site, lambda);
                Expr(cd.B, Esc("a conditional expression"), printedIn, site, lambda);
                return;
            default:
                foreach (var ch in e.Children) Expr(ch, Esc("an operand of " + e.GetType().Name), printedIn, site, lambda);
                return;
        }
    }

    bool CtorOverloaded(FunctionDef ct) => ct.Owner is { } o && ProgramInterfaces.Overloaded(o.Functions.Where(g => g.IsCtor && g.Params.Count == ct.Params.Count).Select(Sig));

    Ctx ArgCtx(ECall c, int i)
    {
        if (c.Fn is { } f && c.Kind is CallKind.Static or CallKind.Instance or CallKind.Super && i < f.Params.Count)
        {
            string? obstacle = null;
            var owner = f.Owner;
            if (owner is not null)
            {
                var scope = Chain(owner).Concat(Down(owner)).SelectMany(x => x.Functions)
                    .Where(g => !g.IsCtor && g.IsStatic == f.IsStatic && g.Params.Count == f.Params.Count && nameClass.GetValueOrDefault(g, -1) == nameClass.GetValueOrDefault(f, -2));
                if (ProgramInterfaces.Overloaded(scope.Select(Sig))) obstacle = "outbound overload: the called method has a sibling with the same name and arity";
            }
            if (obstacle is null && genericParam.GetValueOrDefault((f, i))) obstacle = "a type variable parameter";
            return new CFlow(paramUnit.GetValueOrDefault((f, i)), paramT.GetValueOrDefault((f, i)), "argument", obstacle, true);
        }
        if (c.Kind is CallKind.Jdk or CallKind.JdkStatic) return Esc("an argument of a JDK method (" + (c.Owner ?? "?") + "." + c.Name + ")");
        return Esc("an argument of an unresolved call");
    }

    void Record(Build u, Ctx ctx, string site)
    {
        switch (ctx)
        {
            case CEsc esc:
                u.Uses.Add(new ProgramInterfaces.Use { Site = site, Kind = ProgramInterfaces.UseKind.Escape, Why = esc.Why });
                break;
            case CPlain pl:
                u.Uses.Add(new ProgramInterfaces.Use { Site = site, Kind = pl.Kind });
                break;
            case CFlow fl:
            {
                // the target's declared type as printed now (a unit's too: if it stays, it accepts only what this type accepts)
                var tt = fl.Fixed;
                u.Uses.Add(new ProgramInterfaces.Use
                {
                    Site = site, Kind = fl.Argument ? ProgramInterfaces.UseKind.Argument : ProgramInterfaces.UseKind.Flow, Target = fl.Unit?.Id,
                    Accepts = tt is null ? [] : u.Options.Where(o => Accepts(u, o, tt)).ToList(), Why = fl.Obstacle,
                    Member = fl.What,
                });
                break;
            }
            case CReceiver rc:
            {
                var (covers, why, member) = Receiver(u, rc.Call);
                u.Uses.Add(new ProgramInterfaces.Use { Site = site, Kind = ProgramInterfaces.UseKind.Receiver, Covers = covers, Why = why, Member = member });
                break;
            }
        }
    }

    bool Accepts(Build u, string option, JType tt)
    {
        if (tt.IsObject) return true;
        if (u.Ifaces.TryGetValue(option, out var i)) return tt.Class is null && tt.Args.Count == 0 && S.Interfaces.TryGetValue(tt.Name, out var ti) && ti == i;
        var want = JType.Of(option, [.. u.Type.Args]);
        if (tt.Equals(want)) return true;
        return ProgramInterfaces.JdkSupers.TryGetValue(option, out var sup) && sup.Contains(tt.Name) && tt.Args.Count == u.Type.Args.Count && tt.Args.Zip(u.Type.Args).All(x => x.First.Equals(x.Second));
    }

    string FnId(FunctionDef f) => "fn:" + f.Program.Id + "/" + f.Id;

    (List<string> Covers, string? Why, string Member) Receiver(Build u, ECall c)
    {
        if (u.Class is null)
        {
            // a JDK collection: the JDK interface declares the member (implementation-specific members keep the class)
            if (c.Kind != CallKind.Jdk) return ([], "an estate call on a JDK collection", c.Name);
            var cov = u.Options.Where(o => ProgramInterfaces.JdkDeclares(o, c.Name, c.Args.Count)).ToList();
            return (cov, cov.Count == u.Options.Count ? null : ProgramInterfaces.ImplementationOnly.Contains(c.Name) ? "implementation-specific member " + c.Name : "a member the interface does not declare: " + c.Name, c.Name);
        }
        var cls = u.Class;
        if (c.Kind == CallKind.Jdk)
        {
            // Object's members through any reference type: the same virtual method (unless the class declares its own overload)
            bool objectOk = c.Name is "toString" or "hashCode" && c.Args.Count == 0
                || c.Name == "equals" && c.Args.Count == 1 && !Chain(cls).SelectMany(x => x.Functions).Any(g => objectMember.GetValueOrDefault(g) == "equals");
            return (objectOk ? [.. u.Options] : [], objectOk ? null : "a JDK call on the value: " + c.Name, c.Name);
        }
        if (c.Kind != CallKind.Instance || c.Fn is not { } f) return ([], "an unresolved call on the value", c.Name);
        var member = FnId(f);
        if (f.Owner is null || !Chain(cls).Contains(f.Owner)) return ([], "a member of another class (a downcast)", member);
        var classSigs = Chain(cls).SelectMany(x => x.Functions)
            .Where(g => !g.IsCtor && !g.IsStatic && g.Params.Count == f.Params.Count && nameClass.GetValueOrDefault(g, -1) == nameClass.GetValueOrDefault(f, -3)).Select(ClassSig).ToList();
        var covers = new List<string>();
        string? why = null;
        foreach (var o in u.Options)
        {
            var i = u.Ifaces[o];
            var im = i.Methods.Values.Where(g => g.Params.Count == f.Params.Count && nameClass.GetValueOrDefault(g, -1) == nameClass.GetValueOrDefault(f, -3)).ToList();
            if (im.Count == 0) { why ??= "a member the interface does not declare"; continue; }
            if (!ProgramInterfaces.SameBinding(classSigs, im.Select(IfaceSig))) { why ??= "a member the interface declares differently (overload, parameter, result or exceptions)"; continue; }
            covers.Add(o);
        }
        return (covers, covers.Count == u.Options.Count ? null : why, member);
    }

    static void Visit(Expr e, Action<Expr> a)
    {
        a(e);
        foreach (var c in e.Children) Visit(c, a);
    }

    /// <summary>What R5 does not decide: the application layer, layout records, platform, enums, records' components.</summary>
    static IEnumerable<object> Skipped()
    {
        yield return new { scope = "application, layout record and platform classes", reason = "printed by the application and platform writers (job objects, records, runtime): not decided by R5; a value flowing into them keeps its class" };
        yield return new { scope = "enums, enum constant bodies, record components, lambda parameters", reason = "their declarations are an enum's, a record's accessor API or inferred by Java: not decided by R5" };
        yield return new { scope = "collection elements, keys and values; arrays", reason = "generic arguments, nesting and array element types are copied unchanged (Java generics are invariant)" };
    }

    /// <summary>
    /// The self check's poison: R1's (random class and package names, planted J3S-style CLASS/FRAME/PEC comments, poisoned
    /// FRAME signatures) plus a bijective renaming of method names, random field and variable names and random FRAME
    /// parameter type strings; returns the restore.
    /// </summary>
    static Action Poison(Model m, AppModel app, Dictionary<FunctionDef, Method> methods)
    {
        var restoreR1 = ApiSurface.Poison(m, app);
        var rnd = new Random(20261105);
        var fnNames = m.Functions.Values.ToDictionary(f => f, f => f.MethodName);
        var tok = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in m.Functions.Values.OrderBy(f => f.Id, StringComparer.Ordinal))
            f.MethodName = tok.TryGetValue(f.MethodName, out var t) ? t : tok[f.MethodName] = "iface" + rnd.Next(100000, 999999) + "x" + tok.Count;
        var ptypes = m.Functions.Values.ToDictionary(f => f, f => f.ParamTypes.ToList());
        foreach (var f in m.Functions.Values) for (int i = 0; i < f.ParamTypes.Count; i++) f.ParamTypes[i] = "ArrayList<Poison" + rnd.Next(1000, 9999) + ">";
        var fields = m.Classes.Values.SelectMany(c => c.Fields.Concat(c.Statics)).Distinct().ToDictionary(f => f, f => f.Name);
        foreach (var f in fields.Keys) f.Name = "listX" + rnd.Next(100000, 999999);
        var vars = methods.Values.SelectMany(x => x.L.Params.Concat(x.L.Vars).Concat(VarsOf(x.Body))).Distinct().ToDictionary(v => v, v => v.Name);
        foreach (var v in vars.Keys) v.Name = "mapY" + rnd.Next(100000, 999999);
        return () =>
        {
            foreach (var (v, n) in vars) v.Name = n;
            foreach (var (f, n) in fields) f.Name = n;
            foreach (var (f, l) in ptypes) for (int i = 0; i < l.Count; i++) f.ParamTypes[i] = l[i];
            foreach (var (f, n) in fnNames) f.MethodName = n;
            restoreR1();
        };
    }
}
