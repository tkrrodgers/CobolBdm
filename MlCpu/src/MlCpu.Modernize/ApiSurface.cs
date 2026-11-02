using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

/// <summary>
/// R1 api-visibility over a lifted estate: builds the opaque member graph <see cref="ApiVisibility.Decide"/> decides on, and
/// applies the decisions to <see cref="Shapes"/> (the writer prints them). The facts are the lifted IR (references by symbol:
/// <see cref="FunctionDef"/> and <see cref="FieldDef"/> objects, never names), the COBOL CALL statements (the entry census),
/// the class hierarchy and nesting the model recovered, and the printing rules of the writer (accessors, enum constant
/// bodies, records). Existing reliance, disclosed in the ledger: in a J3S estate the model's classes, packages and method
/// names come from the J3S program headers (CLASS) and function frames (FRAME); R1 uses only the class objects, the equality
/// of their packages, and printed name-and-arity equality where Java itself decides by it (overrides, interfaces, JDK
/// contracts, the writer's accessor and setter lookup). It never reads comment text, a signature string or original source.
/// </summary>
public sealed class ApiSurface
{
    readonly Model M;
    readonly Shapes S;
    readonly Dictionary<FunctionDef, Method> methods;
    readonly AppModel app;
    readonly Dictionary<AppProgram, List<AppMethod>> appMethods;
    readonly List<string> notes = [];
    readonly Dictionary<ClassDef, string> typeId = [];
    readonly Dictionary<string, string> packageId = new(StringComparer.Ordinal);
    readonly List<ApiVisibility.Ref> refs = [];
    readonly HashSet<FunctionDef> virtualTargets = [];
    readonly Dictionary<ClassDef, List<Ctx>> lambdaSites = [];
    readonly List<string> reflectionSites = [];
    readonly Dictionary<FunctionDef, List<string>> entryProof = [];
    readonly Dictionary<FunctionDef, List<string>> unknownCallers = [];
    readonly Dictionary<FunctionDef, List<string>> runtimeCallers = [];
    readonly Dictionary<string, int> census = new(StringComparer.Ordinal);

    /// <summary>Where code is printed: its top-level type, its class, its package (opaque ids), an enum constant body.</summary>
    sealed record Ctx(string Type, string Class, string Package, bool StaticAnonymousBody);

    static readonly Ctx Unknown = new(ApiVisibility.UnknownContext, ApiVisibility.UnknownContext, ApiVisibility.UnknownContext, false);

    /// <summary>JDK supertype methods (java.lang.Object, Comparable, Throwable, a launcher's main): their access is the JDK's.</summary>
    internal static readonly HashSet<(string, int)> JdkContracts =
    [
        ("equals", 1), ("hashCode", 0), ("toString", 0), ("compareTo", 1), ("canEqual", 1), ("clone", 0), ("finalize", 0),
        ("getMessage", 0), ("getLocalizedMessage", 0), ("getCause", 0), ("fillInStackTrace", 0), ("getStackTrace", 0), ("initCause", 1), ("main", 1),
    ];

    internal static readonly HashSet<string> ReflectiveCalls = new(StringComparer.Ordinal)
    {
        "forName", "loadClass", "getDeclaredConstructor", "getConstructor", "getDeclaredConstructors", "getConstructors", "getMethod", "getDeclaredMethod",
        "getMethods", "getDeclaredMethods", "getField", "getDeclaredField", "getFields", "getDeclaredFields", "newInstance", "invoke", "setAccessible",
        "findVirtual", "findStatic", "findConstructor", "findGetter", "findStaticGetter", "privateLookupIn",
    };

    ApiSurface(Model m, Shapes s, Dictionary<FunctionDef, Method> methods, AppModel app, Dictionary<AppProgram, List<AppMethod>> appMethods)
    {
        M = m; S = s; this.methods = methods; this.app = app; this.appMethods = appMethods;
    }

    /// <summary>
    /// Decides and applies R1; returns the ledger. <paramref name="poison"/> (the self check only) first rewrites every class
    /// and package name to random tokens and plants J3S-style comments that quote Java access words, permutes the inputs,
    /// then restores the model: the decisions must not change.
    /// </summary>
    public static (string Ledger, List<ApiVisibility.Decision> Decisions) Run(Model m, Shapes s, Dictionary<FunctionDef, Method> methods, AppModel app,
        Dictionary<AppProgram, List<AppMethod>> appMethods, ModelEdits? edits, bool poison, Action<string> log)
    {
        // the classes the existing reflection pass folded (and the profile lists): bound before any poisoning, by the model's key
        var reflected = ReflectedClasses(m);
        Action? restore = poison ? Poison(m, app) : null;
        try
        {
            var a = new ApiSurface(m, s, methods, app, appMethods) { reflected = reflected };
            var (members, decisions) = a.Build(edits, poison);
            foreach (var d in decisions)
            {
                if (d.M.Kind is "method" or "static-method" or "constructor" && a.fnById.TryGetValue(d.M.Id, out var fn)) (s.MemberAccess ??= [])[fn] = d.After;
                if (d.M.Kind is "constant" or "static-field" && a.staticById.TryGetValue(d.M.Id, out var f)) (s.StaticAccess ??= [])[f] = d.After;
            }
            int changed = decisions.Count(d => d.After != d.M.Before);
            log($"R1 {ApiVisibility.RuleName}: {decisions.Count} members, {changed} changed ({decisions.Count(d => d.After < d.M.Before)} narrowed, {decisions.Count(d => d.After > d.M.Before)} widened), {decisions.Count(d => d.Scope == "kept")} kept; census {string.Join(", ", a.census.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + " " + x.Value))}");
            var inputs = new
            {
                types = a.typeId.Count,
                packages = a.packageId.Count,
                references = a.refs.Count,
                referencesFromUnknownContext = a.refs.Count(r => r.FromType == ApiVisibility.UnknownContext),
                virtualTargets = a.virtualTargets.Count,
                reflectionSites = a.reflectionSites,
                callCensus = a.census.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value),
                poisoned = poison ? "self check: class and package names replaced by random tokens, J3S-style comments planted, inputs permuted" : null,
            };
            return (ApiVisibility.Ledger(decisions, inputs, a.Skipped(), a.notes), decisions);
        }
        finally { restore?.Invoke(); }
    }

    HashSet<ClassDef> reflected = [];
    readonly Dictionary<string, FunctionDef> fnById = new(StringComparer.Ordinal);
    readonly Dictionary<string, FieldDef> staticById = new(StringComparer.Ordinal);

    // ------------------------------------------------------------------ identities

    ClassDef Top(ClassDef c)
    {
        if (S.Of(c).IsEnumBody && c.Super is { } sup) c = sup;
        while (c.Outer is { } o && Shapes.Emitted(o)) c = o;
        return c;
    }

    string Pkg(string package) => packageId.TryGetValue(package, out var id) ? id : packageId[package] = "P" + (packageId.Count + 1).ToString("D2");

    /// <summary>Opaque type ids in an order of COBOL-side keys (class ids, function ids, programs), not of names.</summary>
    void AssignTypeIds(List<ClassDef> emitted)
    {
        var ids = OpaqueTypeIds(emitted, out int ties);
        foreach (var (c, id) in ids) typeId[c] = id;
        if (ties > 0) notes.Add($"{ties} groups of classes share a COBOL-side key: their opaque ids are ordered by the model's class key (ids are ledger labels, not decision inputs)");
        foreach (var c in emitted.OrderBy(x => typeId[x], StringComparer.Ordinal)) Pkg(Top(c).Package);
    }

    /// <summary>
    /// Opaque type ids (T001..) in an order of COBOL-side keys (class ids, function ids, programs, data item names), the
    /// model's class key only breaking ties; shared by the Phase M rules (R1, R3). Ids are ledger labels, not decision inputs.
    /// </summary>
    internal static Dictionary<ClassDef, string> OpaqueTypeIds(IEnumerable<ClassDef> classes, out int ties)
    {
        static string Key(ClassDef c) => string.Join("|",
            c.Ids.Count > 0 ? c.Ids.Min.ToString("D6") : "~",
            c.Functions.Select(f => f.Id).Order(StringComparer.Ordinal).FirstOrDefault() ?? "~",
            string.Join(",", c.Programs.Order(StringComparer.Ordinal)),
            c.Abbr ?? "~",
            c.Statics.Select(f => f.CobolName).Order(StringComparer.Ordinal).FirstOrDefault() ?? "~",
            c.Fields.Select(f => f.CobolName).Order(StringComparer.Ordinal).FirstOrDefault() ?? "~");
        var keyed = classes.Select(c => (C: c, K: Key(c))).ToList();
        ties = keyed.GroupBy(x => x.K).Count(g => g.Count() > 1);
        int n = 0;
        var ids = new Dictionary<ClassDef, string>();
        foreach (var (c, _) in keyed.OrderBy(x => x.K, StringComparer.Ordinal).ThenBy(x => x.C.FullName, StringComparer.Ordinal)) ids[c] = "T" + (++n).ToString("D3");
        return ids;
    }

    /// <summary>The classes the existing reflection pass folded and the target profile lists (loaded by name: providers).</summary>
    internal static HashSet<ClassDef> ReflectedClasses(Model m) =>
        m.ReflectionFolds.Values.Concat(m.Target?.Reflect ?? []).Distinct(StringComparer.Ordinal)
            .Select(n => m.Classes.GetValueOrDefault(n)).Where(c => c is not null).Select(c => c!).ToHashSet();

    /// <summary>
    /// The functions called from outside the lifted code (R1's COBOL CALL census, shared with R5): the external entry surface
    /// (job/replay dispatch), unknown callers and JRE emulation callbacks, with their proofs; and the functions with a COBOL
    /// ENTRY. Reads the same facts as R1 (no names, no comments); decides nothing.
    /// </summary>
    internal static (Dictionary<FunctionDef, List<string>> External, HashSet<FunctionDef> EntryFunctions) ExternalCallers(Model m, Shapes s,
        Dictionary<FunctionDef, Method> methods, AppModel app)
    {
        var a = new ApiSurface(m, s, methods, app, new Dictionary<AppProgram, List<AppMethod>>());
        a.AssignTypeIds(m.Classes.Values.Where(Shapes.Emitted).ToList());
        a.Census();
        var ext = new Dictionary<FunctionDef, List<string>>();
        foreach (var (src, label) in new[] { (a.entryProof, "external entry"), (a.unknownCallers, "unknown caller"), (a.runtimeCallers, "runtime emulation caller") })
            foreach (var (f, proofs) in src)
                foreach (var p in proofs) Add(ext, f, label + ": " + p);
        return (ext, m.Entries.Values.SelectMany(e => e.Values).Select(e => e.Fn).ToHashSet());
    }

    Ctx CtxOf(ClassDef c)
    {
        var top = Top(c);
        return new Ctx(typeId[top], typeId[c], Pkg(top.Package), S.Of(c).IsEnumBody);
    }

    Ctx AppCtx(string programId) =>
        app.ById.TryGetValue(programId, out var ap) ? new Ctx("A:" + ap.P.Id, "A:" + ap.P.Id, Pkg(app.AppPackage), false) : Unknown;

    /// <summary>The class whose file prints a method's body (lambda bodies: where their ELambda is printed, see <see cref="lambdaSites"/>).</summary>
    Ctx? CtxOf(Method m)
    {
        var fn = m.Fn;
        if (fn.IsLambda || fn.Owner?.Kind == ClassKind.Closure) return null;
        if (fn.Owner is null || fn.IsJob || fn.Owner.Kind == ClassKind.Job) return AppCtx(fn.Program.Id);
        if (fn.Owner.Kind == ClassKind.Domain && typeId.ContainsKey(fn.Owner)) return CtxOf(fn.Owner);
        return Unknown;
    }

    // ------------------------------------------------------------------ graph

    (List<ApiVisibility.Member>, List<ApiVisibility.Decision>) Build(ModelEdits? edits, bool permute)
    {
        var emitted = M.Classes.Values.Where(Shapes.Emitted).ToList();
        AssignTypeIds(emitted);
        // the writer's accessor resolution (Shapes.Accessor, Setter) needs the bodies: a getter or setter the estate declares
        // is what another type's field read or write prints
        // references of the bodies the writer prints, lambda bodies at the classes their lambdas are printed in
        var lambdaBodies = methods.Values.Where(x => x.Fn.IsLambda || x.Fn.Owner?.Kind == ClassKind.Closure).ToList();
        foreach (var mm in methods.Values.OrderBy(x => x.Fn.Id, StringComparer.Ordinal))
            if (CtxOf(mm) is { } ctx) Body(mm, ctx);
        var done = new HashSet<(ClassDef, Ctx)>();
        for (bool more = true; more;)
        {
            more = false;
            foreach (var lm in lambdaBodies.OrderBy(x => x.Fn.Id, StringComparer.Ordinal))
            {
                var cl = lm.Fn.Owner!;
                var sites = lambdaSites.GetValueOrDefault(cl)?.ToList() ?? [];
                if (sites.Count == 0) sites.Add(cl.Outer is { } o && typeId.ContainsKey(o) ? CtxOf(o) : Unknown);
                foreach (var ctx in sites)
                    if (done.Add((cl, ctx))) { Body(lm, ctx); more = true; }
            }
        }
        // what the writer prints outside method bodies: static initializers in field declarations, enum constant arguments
        foreach (var c in emitted.OrderBy(x => typeId[x], StringComparer.Ordinal))
        {
            var sh = S.Of(c);
            foreach (var (f, e) in sh.StaticInit.OrderBy(x => x.Key.CobolName, StringComparer.Ordinal)) Expr(e, CtxOf(c), "static initializer of " + f.CobolName, false);
            foreach (var (_, args) in sh.EnumValues) foreach (var e in args) Expr(e, CtxOf(c), "enum constant of " + typeId[c], false);
        }
        Census();
        var members = Members(emitted, edits);
        var input = members;
        var refInput = refs.ToList();
        if (permute) { input = [.. members.AsEnumerable().Reverse()]; refInput.Reverse(); }
        return (members, ApiVisibility.Decide(input, refInput));
    }

    void Body(Method mm, Ctx ctx)
    {
        bool lambda = mm.Fn.IsLambda;
        var site = mm.Fn.Program.Id + " " + mm.Fn.Id;
        foreach (var st in Passes.All(mm.Body))
        {
            var site2 = st.Site is { } ls ? site + " " + ls : site;
            // a write of another type's field prints its setter (an estate setter when there is one); a static write the field
            if (st is SAssign { Lhs: EField lf } && lf.F.Owner is { } wo && typeId.ContainsKey(wo) && (!Same(ctx, wo) || lambda))
                Ref(SetterOf(wo, lf.F), ctx, site2);
            if (st is SAssign { Lhs: EStatic lst }) RefStatic(lst.F, ctx, site2);
            foreach (var e in Fold.Exprs(st)) Expr(e, ctx, site2, lambda);
            if (st is SSwitch sw) foreach (var (labels, _) in sw.Cases) foreach (var e in labels) Expr(e, ctx, site2, lambda);
        }
    }

    bool Same(Ctx ctx, ClassDef owner) => typeId.ContainsKey(owner) && ctx.Type == typeId[Top(owner)];

    void Expr(Expr e, Ctx ctx, string site, bool inLambda)
    {
        switch (e)
        {
            case ECall { Fn: { } f, Kind: CallKind.Static or CallKind.Instance or CallKind.Super } c:
                Ref(f, ctx, site);
                if (c.Virtual) virtualTargets.Add(f);
                break;
            case ECall { Kind: CallKind.Jdk or CallKind.JdkStatic } jc when ReflectiveCalls.Contains(jc.Name):
                reflectionSites.Add($"{site}: {jc.Owner ?? "?"}.{jc.Name}");
                break;
            case ENew { Ctor: { } ct }:
                Ref(ct, ctx, site);
                break;
            case ENew { Class: { } k, Ctor: null }:
                foreach (var ct in k.Functions.Where(f => f.IsCtor)) Ref(ct, ctx, site);
                break;
            case EStatic es:
                RefStatic(es.F, ctx, site);
                break;
            case EField ef when ef.F.Owner is { } fo && typeId.ContainsKey(fo):
                // the writer prints another type's field read as its accessor (an estate getter when there is one)
                if (!(ef.Obj is EThis && !inLambda) && !Same(ctx, fo) && GetterOf(fo, ef.F) is { } g) Ref(g, ctx, site);
                break;
            case ELambda l:
                if (!lambdaSites.TryGetValue(l.Closure, out var ls)) lambdaSites[l.Closure] = ls = [];
                if (!ls.Contains(ctx)) ls.Add(ctx);
                foreach (var v in l.Captures.Values) Expr(v, ctx, site, inLambda);
                return;
        }
        foreach (var ch in e.Children) Expr(ch, ctx, site, inLambda);
    }

    void Ref(FunctionDef? f, Ctx ctx, string site)
    {
        if (f is null) return;
        refs.Add(new ApiVisibility.Ref(FnId(f), ctx.Type, ctx.Class, ctx.Package, site, ctx.StaticAnonymousBody));
    }

    void RefStatic(FieldDef f, Ctx ctx, string site) =>
        refs.Add(new ApiVisibility.Ref(StaticId(f), ctx.Type, ctx.Class, ctx.Package, site, ctx.StaticAnonymousBody));

    static string FnId(FunctionDef f) => "fn:" + f.Program.Id + "/" + f.Id;
    static string StaticId(FieldDef f) => "static:" + f.CobolName;

    /// <summary>Shapes.Accessor's lookup of a declared getter (return this.f;) along the superclasses.</summary>
    FunctionDef? GetterOf(ClassDef owner, FieldDef f)
    {
        for (var c = owner; c is not null; c = c.Super)
            foreach (var fn in c.Functions)
                if (fn.Params.Count == 0 && !fn.IsStatic && methods.TryGetValue(fn, out var m) && m.Body is [SReturn { E: EField { Obj: EThis } rf }] && rf.F == f)
                    return fn;
        return null;
    }

    /// <summary>Shapes.Setter's lookup of a declared setter (set + the field name, one parameter) along the superclasses.</summary>
    FunctionDef? SetterOf(ClassDef owner, FieldDef f)
    {
        var name = "set" + Naming.UpperFirst(f.Name);
        for (var c = owner; c is not null; c = c.Super)
            foreach (var fn in c.Functions)
                if (fn.MethodName == name && fn.Params.Count == 1) return fn;
        return null;
    }

    // ------------------------------------------------------------------ the COBOL CALL census (the entry surface)

    /// <summary>
    /// Every CALL of a program with entries, with the entry number MOVEd to FA-ENTRY before it on its path: the function
    /// the paragraph belongs to is the caller. A caller the lift prints is a reference of its class; a job or replay driver
    /// (no function: the program dispatches from outside the lifted code) puts the entry on the external entry surface; a
    /// JRE emulation function calls back through a JDK contract; anything else, or an entry that is not a literal, is an
    /// unknown caller (the member keeps its access).
    /// </summary>
    void Census()
    {
        var byProgram = M.Functions.Values.GroupBy(f => f.Program).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var p in M.Estate.Programs.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            var paras = p.Model.Paragraphs;
            var at = new FunctionDef?[paras.Count];
            foreach (var f in byProgram.GetValueOrDefault(p) ?? [])
            {
                int first = f.FirstParagraph >= 0 ? f.FirstParagraph : p.ParagraphIndex.GetValueOrDefault(f.Paragraph, -1);
                int last = f.LastParagraph >= 0 ? f.LastParagraph : f.ExitParagraph.Length > 0 ? p.ParagraphIndex.GetValueOrDefault(f.ExitParagraph, first) : first;
                for (int i = Math.Max(0, first); i >= 0 && i <= last && i < paras.Count; i++) at[i] ??= f;
            }
            for (int i = 0; i < paras.Count; i++) Walk(p, at[i], paras[i].Statements, null);
        }
    }

    void Walk(SourceProgram p, FunctionDef? caller, List<Stmt> list, int? entry)
    {
        int? e = entry;
        foreach (var s in list)
        {
            if (s.Verb == "MOVE" && s.L?.GetValueOrDefault("dst") is { } dst && dst.Any(d => d.V?.Equals("FA-ENTRY", StringComparison.OrdinalIgnoreCase) == true))
                e = s.Get("src") is { K: "num", V: var nv } && int.TryParse(nv, out var k) ? k : -1;
            if (s.Verb == "CALL") Call(p, caller, s, e);
            if (s.Blocks is { } blocks) foreach (var (_, b) in blocks.OrderBy(x => x.Key, StringComparer.Ordinal)) Walk(p, caller, b, e);
        }
    }

    void Count(string key) => census[key] = census.GetValueOrDefault(key) + 1;

    void Call(SourceProgram p, FunctionDef? caller, Stmt s, int? entry)
    {
        var target = s.Get("target");
        var where = $"{p.Id}:{s.Line}";
        if (target is null || target.K == "ref")
        {
            // a CALL of a program named by data: any program's entries may be the target
            Count("dynamic program");
            foreach (var (_, map) in M.Entries) foreach (var (_, ed) in map) Add(unknownCallers, ed.Fn, $"{where} CALL of a program named by data");
            return;
        }
        if (!M.Entries.TryGetValue(target.V ?? "", out var entries) || entries.Count == 0) { Count("program without entries"); return; }
        if (entry is not int en || en < 0)
        {
            Count("entry not literal");
            foreach (var (_, ed) in entries) Add(unknownCallers, ed.Fn, $"{where} CALL '{target.V}' with an entry number that is not a literal");
            return;
        }
        if (!entries.TryGetValue(en, out var def)) { Count("entry without function"); return; }
        var callee = def.Fn;
        var proof = $"{where} CALL '{target.V}' entry {en}";
        if (caller is not null && methods.TryGetValue(caller, out var cm))
        {
            Count("lifted caller");
            if (CtxOf(cm) is { } ctx) Ref(callee, ctx, proof);
            else foreach (var lc in lambdaSites.GetValueOrDefault(caller.Owner!) ?? [Unknown]) Ref(callee, lc, proof);
            return;
        }
        if (caller is not null && caller.Owner?.Kind == ClassKind.Emulation) { Count("runtime emulation caller"); Add(runtimeCallers, callee, proof + " (JRE emulation)"); return; }
        if (caller is null && (M.DriverPrograms.Contains(p.Id) || M.JobPrograms.Contains(p.Id))) { Count("job or replay dispatch"); Add(entryProof, callee, proof + " (job/replay dispatch)"); return; }
        Count("unknown caller");
        Add(unknownCallers, callee, proof + (caller is null ? " (outside any function)" : $" (function {caller.Id} is not printed)"));
    }

    static void Add(Dictionary<FunctionDef, List<string>> d, FunctionDef f, string s)
    {
        if (!d.TryGetValue(f, out var l)) d[f] = l = [];
        if (!l.Contains(s)) l.Add(s);
    }

    // ------------------------------------------------------------------ members and their obligations

    List<ApiVisibility.Member> Members(List<ClassDef> emitted, ModelEdits? edits)
    {
        string? global = edits is not null ? "model edits: their Java text may reference any member, outside the graph"
            : reflectionSites.Count > 0 ? "reflection: the code looks members up at run time (" + reflectionSites.Count + " sites)" : null;
        var entryFns = M.Entries.Values.SelectMany(e => e.Values).Select(e => e.Fn).ToHashSet();
        var subs = M.Classes.Values.Where(c => c.Super is not null).GroupBy(c => c.Super!).ToDictionary(g => g.Key, g => g.ToList());
        IEnumerable<ClassDef> Up(ClassDef c) { for (var x = c.Super; x is not null; x = x.Super) yield return x; }
        IEnumerable<ClassDef> Down(ClassDef c) { foreach (var sc in subs.GetValueOrDefault(c) ?? []) { yield return sc; foreach (var d in Down(sc)) yield return d; } }
        var members = new List<ApiVisibility.Member>();
        foreach (var c in emitted.OrderBy(x => typeId[x], StringComparer.Ordinal))
        {
            var sh = S.Of(c);
            var ctx = CtxOf(c);
            bool hasSubs = subs.ContainsKey(c);
            members.Add(new ApiVisibility.Member
            {
                Id = "type:" + typeId[c], Type = ctx.Type, Class = ctx.Class, Package = ctx.Package, Kind = "type", Before = Access.Public, Display = c.FullName,
                SourceIds = c.Programs.Order(StringComparer.Ordinal).ToList(),
                Keep = "not decided by this increment: a type is also referenced by inferred declarations (fields, parameters, casts) outside the IR reference graph",
            });
            if (sh.IsEnumBody)
            {
                foreach (var fn in sh.Methods.Where(f => !f.IsCtor && !f.IsClinit))
                    members.Add(Fn(fn, c, ctx, hasSubs, sh.Private.Contains(fn) ? Access.Private : Access.Public, "an enum constant body's method (an override of its enum)"));
                continue;
            }
            var hierarchy = Up(c).Concat(Down(c)).ToList();
            var ifaces = Up(c).Prepend(c).SelectMany(x => S.Implements.GetValueOrDefault(x) ?? []).ToList();
            foreach (var fn in sh.Methods)
            {
                if (fn.IsClinit) continue;
                if (fn.IsCtor)
                {
                    if (sh.IsEnum || fn == sh.CanonicalCtor && sh.Components is not null) continue;
                    string? keepC = global ?? (reflected.Contains(c) ? "reflection/provider registration: the class is loaded by name" : null) ?? CallerKeep(fn);
                    members.Add(Fn(fn, c, ctx, hasSubs, Access.Public, keepC));
                    continue;
                }
                var before = sh.Private.Contains(fn) ? Access.Private : Access.Public;
                var printed = S.MethodName(fn);
                int arity = fn.Params.Count;
                bool SameSig(FunctionDef g) => g.Params.Count == arity && (g.MethodName == fn.MethodName || S.MethodName(g) == printed) && g.IsStatic == fn.IsStatic && !g.IsCtor;
                string? keep = global
                    ?? (!fn.IsStatic && (JdkContracts.Contains((printed, arity)) || JdkContracts.Contains((fn.MethodName, arity))) ? "JDK contract: a public method of a JDK supertype" : null)
                    ?? (fn.IsStatic && printed == "main" && arity == 1 ? "JDK contract: a launcher's main" : null)
                    ?? (hierarchy.Any(h => h.Functions.Any(SameSig)) ? "override: the same signature in its class hierarchy (an override or a hidden static)" : null)
                    ?? (!fn.IsStatic && arity == 0 && hierarchy.Prepend(c).Any(h => S.Of(h).GetterName.Values.Contains(printed) || S.Of(h).ExtraGetters.Any(x => x.Name == printed)) ? "override: a generated accessor of the same name in its hierarchy" : null)
                    ?? (!fn.IsStatic && Up(c).Prepend(c).Any(h => S.Of(h).AbstractMethods.Any(a => a.Params.Count == arity && (a.MethodName == fn.MethodName || S.MethodName(a) == printed))) ? "override: implements an abstract method" : null)
                    ?? (!fn.IsStatic && ifaces.Any(i => i.Methods.Values.Any(im => im.Params.Count == arity && (im.MethodName == fn.MethodName || S.MethodName(im) == printed))) ? "interface: implements a method of an interface" : null)
                    ?? (virtualTargets.Contains(fn) ? "virtual dispatch target: reached through a call dispatched over classes" : null)
                    ?? (!fn.IsStatic && arity == 0 && sh.Components?.Any(f => f.Name == printed) == true ? "record accessor: public by the Java language" : null)
                    ?? CallerKeep(fn);
                members.Add(Fn(fn, c, ctx, hasSubs, before, keep));
            }
            foreach (var f in c.Statics.Where(x => !sh.EnumFields.Contains(x)).OrderBy(x => x.CobolName, StringComparer.Ordinal))
            {
                bool fin = sh.FinalStatics.Contains(f);
                var id = StaticId(f);
                staticById[id] = f;
                members.Add(new ApiVisibility.Member
                {
                    Id = id, Type = ctx.Type, Class = ctx.Class, Package = ctx.Package, Kind = fin ? "constant" : "static-field", Before = fin ? Access.Public : Access.Package,
                    Static = true, OwnerHasSubclasses = hasSubs, Keep = global, Display = $"{c.FullName}.{f.Name}", SourceIds = [f.CobolName],
                });
            }
            if (sh.Components is null)
                foreach (var f in c.Fields.OrderBy(x => x.CobolName, StringComparer.Ordinal))
                    members.Add(new ApiVisibility.Member
                    {
                        Id = "field:" + typeId[c] + "/" + f.CobolName, Type = ctx.Type, Class = ctx.Class, Package = ctx.Package, Kind = "field", Before = Access.Private,
                        OwnerHasSubclasses = hasSubs, Display = $"{c.FullName}.{f.Name}", SourceIds = [f.CobolName],
                        Keep = "instance state: other types read and write it through its accessors (the writer's rule)",
                    });
            int ak = 0;
            foreach (var (gname, g) in sh.Getters.Select(g => (sh.GetterName[g], g)).Concat(sh.ExtraGetters).OrderBy(x => x.Item1, StringComparer.Ordinal))
                members.Add(new ApiVisibility.Member
                {
                    Id = "accessor:" + typeId[c] + "/" + g.CobolName + "#" + ++ak, Type = ctx.Type, Class = ctx.Class, Package = ctx.Package, Kind = "accessor", Before = Access.Public,
                    Display = $"{c.FullName}.{gname}()", SourceIds = [g.CobolName],
                    Keep = "generated accessor: the read path of other types and of virtual implementations J3S inlined as field reads",
                });
            foreach (var st in S.Setters.GetValueOrDefault(c) ?? [])
                members.Add(new ApiVisibility.Member
                {
                    Id = "setter:" + typeId[c] + "/" + st.CobolName, Type = ctx.Type, Class = ctx.Class, Package = ctx.Package, Kind = "accessor", Before = Access.Public,
                    Display = $"{c.FullName}.set{Naming.UpperFirst(st.Name)}(..)", SourceIds = [st.CobolName],
                    Keep = "generated setter: the write path of other types",
                });
            foreach (var af in sh.AbstractMethods)
                members.Add(new ApiVisibility.Member
                {
                    Id = "abstract:" + typeId[c] + "/" + af.Program.Id + "/" + af.Id, Type = ctx.Type, Class = ctx.Class, Package = ctx.Package, Kind = "abstract-method", Before = Access.Public,
                    Display = $"{c.FullName}.{S.MethodName(af)}(..)", SourceIds = [af.Program.Id + " " + af.Id],
                    Keep = "abstract method: its implementations are public overrides",
                });
        }
        int noEntryCaller = members.Count(m => fnById.TryGetValue(m.Id, out var f) && entryFns.Contains(f) && !entryProof.ContainsKey(f) && !unknownCallers.ContainsKey(f) && !runtimeCallers.ContainsKey(f) && !refs.Any(r => r.Member == m.Id));
        if (noEntryCaller > 0) notes.Add($"{noEntryCaller} members have a COBOL ENTRY but no CALL and no reference in the estate: kept (unknown caller)");
        return members;
    }

    string? CallerKeep(FunctionDef fn) =>
        unknownCallers.TryGetValue(fn, out var u) ? "unknown caller: " + string.Join("; ", u.Order(StringComparer.Ordinal).Take(4)) + (u.Count > 4 ? $"; {u.Count - 4} more" : "")
        : runtimeCallers.TryGetValue(fn, out var r) ? "runtime callback: " + string.Join("; ", r.Order(StringComparer.Ordinal).Take(4)) + (r.Count > 4 ? $"; {r.Count - 4} more" : "")
        : null;

    ApiVisibility.Member Fn(FunctionDef fn, ClassDef c, Ctx ctx, bool hasSubs, Access before, string? keep)
    {
        var id = FnId(fn);
        fnById[id] = fn;
        var m = new ApiVisibility.Member
        {
            Id = id, Type = ctx.Type, Class = ctx.Class, Package = ctx.Package, Kind = fn.IsCtor ? "constructor" : fn.IsStatic ? "static-method" : "method",
            Before = before, Static = fn.IsStatic && !fn.IsCtor, OwnerHasSubclasses = hasSubs, Keep = keep,
            Display = fn.IsCtor ? $"{c.FullName}({fn.Params.Count} parameters)" : $"{c.FullName}.{S.MethodName(fn)}({fn.Params.Count} parameters)",
            SourceIds = [fn.Program.Id + " " + fn.Id + " " + fn.Paragraph],
        };
        if (keep is null && entryProof.TryGetValue(fn, out var proof)) m.Entry.AddRange(proof);
        return m;
    }

    /// <summary>The application layer's classes (job, step and subprogram classes, layout records): kept, with the reason.</summary>
    IEnumerable<object> Skipped()
    {
        foreach (var ap in app.Programs.OrderBy(p => p.P.Id, StringComparer.Ordinal))
        {
            var list = appMethods.GetValueOrDefault(ap) ?? [];
            yield return new
            {
                scope = "application class",
                program = ap.P.Id,
                @class = app.AppPackage + "." + ap.ClassName,
                kind = ap.IsJob ? "job" : ap.IsSubprogram ? "subprogram" : "step",
                constructor = "public, kept: a strangler port (the step registry and the calling programs construct it)",
                entry = ap.IsSubprogram ? "call(..) public, kept: the program's COBOL CALL entry (a strangler port)" : "run() public, kept: the step entry point the job and the strangler dispatch",
                privateMethods = list.Count(x => x.Head.StartsWith("private ", StringComparison.Ordinal)),
                reason = "already least access: paragraph methods are private; the entry and constructor are the external entry surface",
            };
        }
        yield return new
        {
            scope = "layout records",
            reason = "the record classes' accessors are emitted from the data layouts by the application writer, outside the IR symbol graph: kept public",
        };
    }

    // ------------------------------------------------------------------ the self check's poison

    /// <summary>Random class and package names, planted J3S-style comments quoting access words; returns the restore.</summary>
    internal static Action Poison(Model m, AppModel app)
    {
        var rnd = new Random(20261101);
        var names = m.Classes.Values.ToDictionary(c => c, c => (c.FullName, c.Package));
        var pk = new Dictionary<string, string>(StringComparer.Ordinal) { [app.AppPackage] = app.AppPackage };
        string Tok(string p) => pk.TryGetValue(p, out var t) ? t : pk[p] = "zz" + rnd.Next(100000, 999999) + ".q" + rnd.Next(1000, 9999);
        foreach (var c in m.Classes.Values.OrderBy(x => x.FullName, StringComparer.Ordinal))
        {
            var p = Tok(c.Package);
            c.Package = p;
            c.FullName = p + ".K" + rnd.Next(100000, 999999) + (c.Outer is not null ? "$" + rnd.Next(10, 99) : "");
        }
        var planted = new List<(SortedDictionary<int, string> D, int Line)>();
        foreach (var p in m.Estate.Programs)
        {
            int line = -1 - planted.Count;
            p.Comments[line] = "CLASS public final org.example.Api FRAME OF M0001-X public static void api() private protected legacy PEC:1";
            planted.Add((p.Comments, line));
        }
        var sigs = m.Functions.Values.ToDictionary(f => f, f => f.Signature);
        foreach (var f in m.Functions.Values) f.Signature = "private static void poisoned()";
        return () =>
        {
            foreach (var (c, (fn, pkg)) in names) { c.FullName = fn; c.Package = pkg; }
            foreach (var (d, line) in planted) d.Remove(line);
            foreach (var (f, s) in sigs) f.Signature = s;
        };
    }
}
