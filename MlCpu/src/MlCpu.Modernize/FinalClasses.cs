using System.Text.Json;
using System.Text.Json.Serialization;

namespace MlCpu.Modernize;

/// <summary>
/// Phase M rule R3 (final-classes, opt-in): a class the estate cannot extend is declared <c>final</c>. It runs only when
/// requested (--rule final-classes) AND the estate owner's deployment profile declares closedWorld: true
/// (<see cref="DeploymentProfile"/>; never inferred). The decision is
/// generic: it sees opaque class identities, the printed form (class, record, enum, enum constant body, abstract,
/// interface), the known direct subclasses (opaque ids), whether the hierarchy around the class is fully evidenced, and
/// the keep obligations (reflection or provider registration, proxies, model edits). It never sees a Java or COBOL name:
/// <see cref="Candidate.Display"/>, <see cref="Candidate.SourceIds"/> and <see cref="Candidate.SourceProof"/> only go to
/// the ledger.
/// <list type="bullet">
/// <item>records and enums are implicitly final, an enum constant body is an anonymous class, abstract classes and
/// interfaces are extension points by kind: skipped (never marked);</item>
/// <item>a known subclass: kept (a final class cannot be extended);</item>
/// <item>a keep obligation (reflection/provider/proxy target, model edits, an unknown reflective lookup): kept;</item>
/// <item>a hierarchy that is not fully evidenced (unbound class ids or unlinked classes in its heap pool, instance
/// members without an allocated class id, a class whose superclass is undetermined): kept (fail closed);</item>
/// <item>otherwise: final.</item>
/// </list>
/// </summary>
public static class FinalClasses
{
    public const string RuleName = "final-classes";

    public enum Form { Class, Record, Enum, EnumBody, Abstract, Interface }

    public sealed class Candidate
    {
        /// <summary>Opaque, stable identity (ordered by COBOL-side keys).</summary>
        public required string Id { get; init; }
        public required Form Form { get; init; }
        public bool Nested { get; init; }
        /// <summary>Opaque ids of the classes whose superclass link names this class.</summary>
        public List<string> Subclasses { get; init; } = [];
        /// <summary>Why the hierarchy around the class is not fully evidenced (any entry: fail closed).</summary>
        public List<string> HierarchyUnknown { get; init; } = [];
        /// <summary>Keep obligations: reflection, provider, proxy, model edits.</summary>
        public List<string> Obligations { get; init; } = [];
        /// <summary>For the ledger only: the generated Java class, the COBOL source ids and the structural facts seen.</summary>
        public string Display { get; init; } = "";
        public List<string> SourceIds { get; init; } = [];
        public List<string> SourceProof { get; init; } = [];
    }

    public sealed record Decision(Candidate C, string Outcome, string Reason)
    {
        public bool Final => Outcome == "applied";
    }

    public static List<Decision> Decide(IEnumerable<Candidate> candidates) =>
        candidates.OrderBy(c => c.Id, StringComparer.Ordinal).Select(One).ToList();

    static Decision One(Candidate c) => c.Form switch
    {
        Form.Record => new(c, "skipped", "implicitly final: a record"),
        Form.Enum => new(c, "skipped", "implicitly final: an enum"),
        Form.EnumBody => new(c, "skipped", "an enum constant body: an anonymous class (cannot be declared final)"),
        Form.Interface => new(c, "skipped", "an interface: an extension point by kind"),
        Form.Abstract => new(c, "skipped", "abstract: an extension point by kind (cannot be final)"),
        _ when c.Subclasses.Count > 0 => new(c, "kept", "known subclass: " + string.Join(", ", c.Subclasses.Order(StringComparer.Ordinal))),
        _ when c.Obligations.Count > 0 => new(c, "kept", "keep obligation: " + string.Join("; ", c.Obligations.Order(StringComparer.Ordinal))),
        _ when c.HierarchyUnknown.Count > 0 => new(c, "kept", "unknown hierarchy: " + string.Join("; ", c.HierarchyUnknown.Order(StringComparer.Ordinal))),
        _ => new(c, "applied", c.Nested ? "final: a nested class with no subclass in the estate's evidenced hierarchy" : "final: no subclass in the estate's evidenced hierarchy"),
    };

    /// <summary>The kind of a reason (the text before its first colon), for the summary.</summary>
    public static string ReasonClass(string reason) => reason.IndexOf(':') is var k && k > 0 ? reason[..k] : reason;

    /// <summary>The per-rule ledger (modernize/final-classes.json): every class's form, outcome, reason, subclasses, obligations and proof.</summary>
    public static string Ledger(List<Decision> decisions, object inputs, IEnumerable<object> skipped, IEnumerable<string> notes, object? authorization = null)
    {
        var doc = new
        {
            rule = "R3 " + RuleName,
            version = 1,
            authorization,
            evidence = new[]
            {
                "class objects and their superclass links (lifted super(...) constructor calls), the classes of the heap pools (J3S puts a class hierarchy in one pool) and the class ids bound to each class",
                "the printed form the shapes decided (record, enum, enum constant body, abstract by abstract methods or by an unallocated class with subclasses)",
                "keep obligations: classes the reflection pass folded or the target profile lists (loaded by name: providers), surviving reflective or proxy calls in the lifted IR, model edits",
            },
            forbidden = "never read: original Java, J3S CLASS and FRAME comment text, legacy PEC citations, Java or COBOL names (opaque ids; names only in the ledger's display fields)",
            disclosure = "pre-existing and unchanged: in J3S estates the model's class objects, packages and method names come from the J3S CLASS/FRAME program headers (Model.cs); R3 uses the class objects' identity and links only, never their names",
            inputs,
            summary = new
            {
                classes = decisions.Count,
                applied = decisions.Count(d => d.Outcome == "applied"),
                kept = decisions.Count(d => d.Outcome == "kept"),
                skipped = decisions.Count(d => d.Outcome == "skipped"),
                byOutcome = decisions.GroupBy(d => d.Outcome + ": " + ReasonClass(d.Reason)).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            },
            notes,
            classes = decisions.Select(d => new
            {
                id = d.C.Id,
                form = d.C.Form.ToString().ToLowerInvariant(),
                nested = d.C.Nested ? true : (bool?)null,
                outcome = d.Outcome,
                reason = d.Reason,
                subclasses = d.C.Subclasses.Count > 0 ? d.C.Subclasses.Order(StringComparer.Ordinal).ToList() : null,
                obligations = d.C.Obligations.Count > 0 ? d.C.Obligations.Order(StringComparer.Ordinal).ToList() : null,
                hierarchyUnknown = d.C.HierarchyUnknown.Count > 0 ? d.C.HierarchyUnknown.Order(StringComparer.Ordinal).ToList() : null,
                sourceProof = d.C.SourceProof,
                @class = d.C.Display,
                sourceIds = d.C.SourceIds,
            }).ToList(),
            skipped,
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }
}

/// <summary>
/// R3 final-classes over a lifted estate: builds the opaque candidates <see cref="FinalClasses.Decide"/> decides on and
/// applies the decisions to <see cref="Shapes.FinalClasses"/> (the writer prints <c>final</c>). Facts: the class objects
/// the model recovered (identity, superclass and nesting links, bound class ids, heap pools), the forms the shapes
/// decided, the lifted IR (constructors lifted, reflective and proxy calls), the reflection pass's folds and the target
/// profile's reflection list (shared with R1: <see cref="ApiSurface.ReflectedClasses"/>).
/// </summary>
public sealed class FinalClassesSurface
{
    /// <summary>JDK calls that look classes up by name or proxy them: their targets are unknown (every class is kept).</summary>
    static readonly HashSet<string> ProxyCalls = new(StringComparer.Ordinal) { "newProxyInstance", "getProxyClass", "isProxyClass" };

    public static (string Ledger, List<FinalClasses.Decision> Decisions) Run(Model m, Shapes s, Dictionary<FunctionDef, Method> methods, AppModel app,
        ModelEdits? edits, DeploymentProfile profile, bool poison, Action<string> log)
    {
        if (!profile.ClosedWorld) throw new InvalidOperationException($"{FinalClasses.RuleName} without a closedWorld: true deployment profile");
        // bound by the model's key before any poisoning
        var reflected = ApiSurface.ReflectedClasses(m);
        var exceptionRoots = m.ExceptionNames.Keys.Select(id => m.ClassById.GetValueOrDefault(id)).Where(c => c is not null)
            .Select(c => { var t = c!; while (t.Super is not null) t = t.Super; return t; }).ToHashSet();
        Action? restore = poison ? Poison(m, app) : null;
        try
        {
            var (candidates, inputs, notes, byId) = Build(m, s, methods, edits, reflected, exceptionRoots);
            if (poison) candidates.Reverse();
            var decisions = FinalClasses.Decide(candidates);
            s.FinalClasses = decisions.Where(d => d.Final).Select(d => byId[d.C.Id]).ToHashSet();
            log($"R3 {FinalClasses.RuleName}: {decisions.Count} classes, {decisions.Count(d => d.Final)} final, {decisions.Count(d => d.Outcome == "kept")} kept, {decisions.Count(d => d.Outcome == "skipped")} skipped");
            return (FinalClasses.Ledger(decisions, inputs, Skipped(app), notes, profile.Record([FinalClasses.RuleName])), decisions);
        }
        finally { restore?.Invoke(); }
    }

    static (List<FinalClasses.Candidate>, object, List<string>, Dictionary<string, ClassDef>) Build(Model m, Shapes s, Dictionary<FunctionDef, Method> methods,
        ModelEdits? edits, HashSet<ClassDef> reflected, HashSet<ClassDef> exceptionRoots)
    {
        var notes = new List<string>();
        var emitted = m.Classes.Values.Where(Shapes.Emitted).ToList();
        var typeId = ApiSurface.OpaqueTypeIds(emitted, out int ties);
        if (ties > 0) notes.Add($"{ties} groups of classes share a COBOL-side key: their opaque ids are ordered by the model's class key (ids are ledger labels, not decision inputs)");
        // a class outside the emitted set (an emulated JDK class, a closure) has an opaque id of its kind and class ids
        string Id(ClassDef c) => typeId.TryGetValue(c, out var t) ? t : c.Kind.ToString().ToLowerInvariant() + ":" + (c.Ids.Count > 0 ? string.Join(",", c.Ids) : "unallocated");
        var poolId = m.Pools.Values.Where(p => p.Ids.Count > 0).OrderBy(p => p.Ids.Min).ThenBy(p => p.Prefix, StringComparer.Ordinal)
            .Select((p, i) => (p, i)).ToDictionary(x => x.p, x => "P" + (x.i + 1).ToString("D2"));

        // global keep obligations: model edits (Java text outside the graph), reflective and proxy calls with unknown targets
        var reflectiveSites = new List<string>();
        foreach (var mm in methods.Values.OrderBy(x => x.Fn.Program.Id, StringComparer.Ordinal).ThenBy(x => x.Fn.Id, StringComparer.Ordinal))
            foreach (var st in Passes.All(mm.Body))
                foreach (var e in Fold.Exprs(st))
                    Visit(e, x =>
                    {
                        if (x is ECall { Kind: CallKind.Jdk or CallKind.JdkStatic } jc && (ApiSurface.ReflectiveCalls.Contains(jc.Name) || ProxyCalls.Contains(jc.Name)))
                            reflectiveSites.Add($"{mm.Fn.Program.Id} {mm.Fn.Id}: {jc.Name}");
                    });
        var global = new List<string>();
        if (edits is not null) global.Add("model edits: their Java text may extend any class, outside the evidenced hierarchy");
        if (reflectiveSites.Count > 0) global.Add($"reflection or proxy: the code looks classes up or proxies them at run time ({reflectiveSites.Count} sites, targets unknown)");

        // the hierarchy evidence: superclass links (any kind of class), heap pools, constructors lifted
        var subs = m.Classes.Values.Where(c => c.Super is not null).GroupBy(c => c.Super!).ToDictionary(g => g.Key, g => g.ToHashSet());
        foreach (var c in m.Classes.Values) foreach (var sc in c.Subs) (subs.TryGetValue(c, out var hs) ? hs : subs[c] = []).Add(sc);
        bool CtorsLifted(ClassDef c) => c.Functions.Where(f => f.IsCtor).All(methods.ContainsKey);
        // a class whose superclass is undetermined: no link and a constructor the lift did not reach (its super call is unseen)
        var undetermined = emitted.Where(c => c.Super is null && !s.Of(c).IsEnumBody && !CtorsLifted(c)).ToList();
        var poolUnknown = new Dictionary<PoolDef, List<string>>();
        foreach (var p in m.Pools.Values.Where(p => p.Ids.Count > 0))
        {
            var why = new List<string>();
            var unbound = p.Ids.Where(i => !m.ClassById.ContainsKey(i)).ToList();
            if (unbound.Count > 0) why.Add($"heap pool {poolId[p]} has {unbound.Count} class ids no class is bound to");
            var bound = p.Ids.Where(m.ClassById.ContainsKey).Select(i => m.ClassById[i]).Distinct().ToList();
            // roots of the pool's classes the JDK does not explain (an exception root extends RuntimeException; emulated JDK classes)
            var roots = bound.Select(c => { var t = c; while (t.Super is not null) t = t.Super; return t; }).Distinct()
                .Where(r => r.Kind == ClassKind.Domain && !exceptionRoots.Contains(r)).ToList();
            if (roots.Count > 1) why.Add($"heap pool {poolId[p]} holds {roots.Count} unlinked class hierarchies (a superclass link may be missing)");
            foreach (var u in undetermined.Where(u => u.Pool == p)) why.Add($"heap pool {poolId[p]} holds {Id(u)}, whose superclass is undetermined (a constructor was not lifted)");
            if (why.Count > 0) poolUnknown[p] = why;
        }
        var unpooledUndetermined = undetermined.Where(u => u.Pool is null).ToList();
        if (unpooledUndetermined.Count > 0)
            global.Add($"unknown hierarchy: {string.Join(", ", unpooledUndetermined.Select(Id).Order(StringComparer.Ordinal))} without a heap pool and with an undetermined superclass (a constructor was not lifted): any class may be its superclass");

        var byId = new Dictionary<string, ClassDef>(StringComparer.Ordinal);
        var candidates = new List<FinalClasses.Candidate>();
        foreach (var c in emitted.OrderBy(x => typeId[x], StringComparer.Ordinal))
        {
            var sh = s.Of(c);
            var form = sh.IsEnumBody ? FinalClasses.Form.EnumBody
                : sh.IsEnum || c.IsEnum ? FinalClasses.Form.Enum
                : c.IsInterface ? FinalClasses.Form.Interface
                : sh.IsAbstract || c.IsAbstract || sh.AbstractMethods.Count > 0 ? FinalClasses.Form.Abstract
                : sh.Components is not null && !exceptionRoots.Contains(c) ? FinalClasses.Form.Record
                : FinalClasses.Form.Class;
            var unknown = new List<string>();
            if (c.Pool is { } pool && poolUnknown.TryGetValue(pool, out var pu)) unknown.AddRange(pu);
            bool instanceMembers = c.Fields.Count > 0 || c.Functions.Any(f => !f.IsStatic && !f.IsCtor && !f.IsClinit && !f.IsLambda);
            if (c.Ids.Count == 0 && instanceMembers) unknown.Add("instance members but no class id is allocated as this class (its instances' class is not evidenced)");
            if (undetermined.Contains(c)) unknown.Add("its own superclass is undetermined (a constructor was not lifted)");
            var obligations = new List<string>(global);
            if (reflected.Contains(c)) obligations.Add("reflection/provider registration: the class is loaded by name (a folded reflective construction or the target profile's list)");
            var ctors = c.Functions.Where(f => f.IsCtor).ToList();
            var proof = new List<string>
            {
                $"form {form.ToString().ToLowerInvariant()}" + (c.Outer is not null && Shapes.Emitted(c.Outer) ? $", nested in {Id(c.Outer)}" : ""),
                c.Ids.Count > 0 ? $"class ids allocated: {c.Ids.Count}" + (c.Pool is { } pp && poolId.TryGetValue(pp, out var pid) ? $" in heap pool {pid}" : ", no heap pool") : "no class id allocated",
                $"superclass link: {(c.Super is { } sup ? Id(sup) : "none")}",
                $"constructors: {ctors.Count}, lifted {ctors.Count(methods.ContainsKey)}",
                $"subclass links naming it: {subs.GetValueOrDefault(c)?.Count ?? 0}",
            };
            byId[typeId[c]] = c;
            candidates.Add(new FinalClasses.Candidate
            {
                Id = typeId[c], Form = form, Nested = c.Outer is not null && Shapes.Emitted(c.Outer),
                Subclasses = (subs.GetValueOrDefault(c) ?? []).Select(Id).Distinct().Order(StringComparer.Ordinal).ToList(),
                HierarchyUnknown = unknown, Obligations = obligations,
                Display = c.FullName, SourceIds = c.Programs.Order(StringComparer.Ordinal).Concat(c.Pool is { } p2 ? [p2.Prefix] : []).ToList(),
                SourceProof = proof,
            });
        }
        var inputs = new
        {
            classes = emitted.Count,
            pools = poolId.Count,
            poolsNotEvidenced = poolUnknown.Count,
            poolReasons = poolUnknown.Values.SelectMany(x => x).Distinct().Order(StringComparer.Ordinal).ToList(),
            undeterminedSuperclasses = undetermined.Count,
            reflectedClasses = reflected.Count(r => emitted.Contains(r)),
            reflectiveOrProxySites = reflectiveSites,
            modelEdits = edits is not null,
        };
        return (candidates, inputs, notes, byId);
    }

    static void Visit(Expr e, Action<Expr> a)
    {
        a(e);
        foreach (var c in e.Children) Visit(c, a);
        if (e is ELambda l) foreach (var v in l.Captures.Values) Visit(v, a);
    }

    /// <summary>The application layer's classes (job, step and subprogram classes, layout records, platform): not R3's.</summary>
    static IEnumerable<object> Skipped(AppModel app)
    {
        foreach (var ap in app.Programs.OrderBy(p => p.P.Id, StringComparer.Ordinal))
            yield return new
            {
                scope = "application class",
                program = ap.P.Id,
                kind = ap.IsJob ? "job" : ap.IsSubprogram ? "subprogram" : "step",
                reason = "printed by the application writer (already final where it has no subclass); a strangler port: not decided by R3",
            };
        yield return new { scope = "layout records and platform classes", reason = "printed by the application and platform writers (final, or a declared extension point such as RunReport): not decided by R3" };
    }

    /// <summary>
    /// The self check's poison: R1's (random class and package names, planted J3S-style CLASS/FRAME/PEC comments, poisoned
    /// FRAME signatures) plus random method and field names and reversed subclass lists; returns the restore.
    /// </summary>
    static Action Poison(Model m, AppModel app)
    {
        var restoreR1 = ApiSurface.Poison(m, app);
        var rnd = new Random(20261102);
        var fnNames = m.Functions.Values.ToDictionary(f => f, f => f.MethodName);
        foreach (var f in m.Functions.Values) f.MethodName = "final" + rnd.Next(100000, 999999);
        var fields = m.Classes.Values.SelectMany(c => c.Fields.Concat(c.Statics)).Distinct().ToDictionary(f => f, f => f.Name);
        foreach (var f in fields.Keys) f.Name = "extendsX" + rnd.Next(100000, 999999);
        foreach (var c in m.Classes.Values) c.Subs.Reverse();
        return () =>
        {
            foreach (var c in m.Classes.Values) c.Subs.Reverse();
            foreach (var (f, n) in fields) f.Name = n;
            foreach (var (f, n) in fnNames) f.MethodName = n;
            restoreR1();
        };
    }
}
