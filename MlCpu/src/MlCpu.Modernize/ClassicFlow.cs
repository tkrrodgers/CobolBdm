using System.Globalization;
using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

/// <summary>What <see cref="ClassicFlow"/> made of a batch step's class: the fields left, its nested types, its report port.</summary>
public sealed class FlowResult
{
    /// <summary>Work fields that are still fields of the class (the rest became parameters, locals and return values).</summary>
    public HashSet<PromotedField> Fields { get; } = [];
    /// <summary>Record instances that are no longer fields (a method's own record, or no use left).</summary>
    public HashSet<string> DroppedInstances { get; } = new(StringComparer.Ordinal);
    /// <summary>Nested types of the class (a counters value), as Java text at member indentation.</summary>
    public List<string> Members { get; } = [];
    /// <summary>The report port's field and class (the platform package), when the step reports.</summary>
    public string? ReportField { get; set; }
    public string? ReportClass { get; set; }
    /// <summary>The report formatter's edited values: method, PICTURE, legacy item.</summary>
    public List<(string Method, string Picture, string Legacy)> Edits { get; } = [];
    /// <summary>What was restructured (MODERNIZATION.md, the log).</summary>
    public List<string> Notes { get; } = [];
    /// <summary>Imports the nested members need.</summary>
    public HashSet<string> Imports { get; } = new(StringComparer.Ordinal);
    /// <summary>A nested class that owns state and its operations (the record sequence): doc, name, field lines, methods.</summary>
    public List<(string Doc, string Name, string Field, List<string> Fields, List<AppMethod> Methods)> Nested { get; } = [];
    /// <summary>The step's entry method when renamed (runStep).</summary>
    public string? EntryName { get; set; }
    /// <summary>Fields of the nested classes (not the program class's own).</summary>
    public HashSet<PromotedField> NestedFields { get; } = [];
}

/// <summary>
/// The data flow of a batch step's paragraphs, restructured on the lifted IR (JOBOL: methods with inputs and results,
/// not paragraphs sharing WORKING-STORAGE). Each step is a proof obligation checked on the IR; a step whose conditions do
/// not hold leaves the code as it was (fail closed). The steps:
/// <list type="bullet">
/// <item>datasets: a FILE STATUS checked after every operation becomes the adapter's exception (a try around the
/// operation, the check's handler in the catch); the priming read loop becomes a for-each over the dataset's records;</item>
/// <item>the report port: DISPLAY is a line of the step's report, a numeric edit is the report formatter's (compat lane);</item>
/// <item>validation flags computed from a record only (x = true; if (bad) x = false) become predicates of the record;</item>
/// <item>the record a loop iterates is a parameter of the methods the loop calls;</item>
/// <item>work fields become parameters (read before written), return values (written and read by the caller after the
/// call) and locals; counters only ever incremented become an immutable totals value passed and returned;</item>
/// <item>a record a method fills from scratch is its local (and a parameter of the methods it calls).</item>
/// </list>
/// </summary>
sealed class ClassicFlow
{
    sealed class Meth
    {
        public required string Name { get; set; }
        public required Method M { get; init; }
        public required string Doc { get; init; }
        public JType? Ret { get; set; }
        public bool Static { get; init; }
        public bool Predicate { get; init; }
        public List<St> Body { get => M.Body; set => M.Body = value; }
        public List<Var> Params => M.L.Params;
    }

    /// <summary>A state variable threaded through the methods: a work field, or a group of counters (one value).</summary>
    sealed class Sv
    {
        public required string Key { get; init; }
        public required string Name { get; init; }
        public required JType Type { get; init; }
        public required Expr Init { get; init; }
        public PromotedField? Field { get; init; }
        public List<PromotedField>? Members { get; init; }
        public int Order { get; init; }
    }

    sealed class Sum
    {
        public HashSet<string> Use { get; } = [];
        public HashSet<string> MayDef { get; } = [];
        public HashSet<string> MustDef { get; set; } = [];
    }

    readonly AppModel A;
    readonly AppProgram ap;
    readonly List<Meth> ms = [];
    readonly Dictionary<string, PromotedField> fields = new(StringComparer.Ordinal);
    readonly Dictionary<string, RecordClass> instances = new(StringComparer.Ordinal);
    readonly HashSet<string> calleeFields;
    readonly HashSet<string> fileFields;
    readonly FlowResult R = new();
    string entryName;
    string RecordsOwner => A.PlatformPackage + ".Records";
    string? loopInstance;
    Var? loopVar;
    string? groupType;
    readonly HashSet<string> groupComponents = new(StringComparer.Ordinal);

    ClassicFlow(AppModel a, AppProgram p, List<AppMethod> methods)
    {
        A = a; ap = p;
        foreach (var am in methods)
            ms.Add(new Meth { Name = am.Head.Split(' ')[^1], M = am.M, Doc = am.Doc });
        foreach (var pf in p.Promoted.Values) fields[pf.Name] = pf;
        foreach (var (item, inst) in p.Instances) if (p.Records.TryGetValue(item, out var rc)) instances.TryAdd(inst, rc);
        calleeFields = new HashSet<string>(p.Callees.Values, StringComparer.Ordinal);
        fileFields = new HashSet<string>(p.FileFields.Values, StringComparer.Ordinal);
        entryName = p.MethodNames[p.Classic[0].Name];
    }

    Meth? Entry => ms.FirstOrDefault(m => m.Name == entryName);
    Meth? ByName(string name) => ms.FirstOrDefault(m => m.Name == name);

    /// <summary>Restructures a batch step's methods; a job program's are left as lifted.</summary>
    public static List<AppMethod> Run(AppModel a, AppProgram p, List<AppMethod> methods)
    {
        if (p.IsJob || p.Classic.Count == 0 || !p.MethodNames.ContainsKey(p.Classic[0].Name)) return methods;
        var f = new ClassicFlow(a, p, methods);
        if (f.Entry is null) return methods;
        f.InlineConstants();
        f.Datasets();
        f.Report();
        foreach (var m in f.ms) f.FoldFlags(m.Body);
        f.Predicates();
        f.LoopRecord();
        f.Thread();
        f.OwnLimits();
        f.ScratchRecords();
        foreach (var m in f.ms) f.Cleanup(m);
        foreach (var m in f.ms) f.CodeChains(m);
        f.StateObject();
        f.InlineGuards();
        f.RejectCodes();
        f.Validate();
        f.TwoValued();
        f.Names();
        f.Finish();
        p.Flow = f.R;
        return f.ms.Select(m => new AppMethod($"private {(m.Static ? "static " : "")}{(m.Ret is null ? "void" : JavaName(m.Ret))} {m.Name}", m.M, m.Doc)).ToList();
    }

    static string JavaName(JType t) => t.Name switch { "java.lang.String" => "String", _ => t.Name.Contains('.') ? t.Name[(t.Name.LastIndexOf('.') + 1)..] : t.Name };

    // ================================================================== IR helpers

    static IEnumerable<Expr> Deep(Expr e)
    {
        yield return e;
        foreach (var c in e.Children)
            foreach (var d in Deep(c)) yield return d;
    }

    /// <summary>The expressions of a statement (an assignment's target included), not those of nested statements.</summary>
    static IEnumerable<Expr> Exprs(St s) => s switch
    {
        SAssign a => [a.Lhs, a.Rhs],
        SExpr x => [x.E],
        SReturn { E: { } r } => [r],
        SThrow t => [t.E],
        SIf i => [i.C],
        SLoop l => (l.C is null ? Array.Empty<Expr>() : [l.C]).Concat(l.Iterable is null ? [] : [l.Iterable]),
        SSwitch w => new[] { w.Subject }.Concat(w.Cases.SelectMany(c => c.Labels)),
        _ => [],
    };

    static IEnumerable<Expr> AllExprs(List<St> body) => Passes.All(body).SelectMany(Exprs).SelectMany(Deep);
    static IEnumerable<Expr> StExprs(St s) => Passes.All([s]).SelectMany(Exprs).SelectMany(Deep);

    /// <summary>The expressions a body reads (an assignment's target is not read, its subscripts and record are).</summary>
    static IEnumerable<Expr> Reads(List<St> body) => Passes.All(body)
        .SelectMany(s => s is SAssign a ? a.Lhs.Children.Append(a.Rhs) : Exprs(s)).SelectMany(Deep);

    static IEnumerable<List<St>> Lists(List<St> body)
    {
        yield return body;
        foreach (var s in body)
            foreach (var child in Children(s))
                foreach (var l in Lists(child)) yield return l;
    }

    static IEnumerable<List<St>> Children(St s) => s switch
    {
        SIf i => [i.Then, i.Else],
        SLoop l => [l.Body],
        SBlock b => [b.Body],
        SSwitch sw => sw.Cases.Select(c => c.Body).Concat(sw.Default is null ? [] : [sw.Default]),
        STry t => new[] { t.Body }.Concat(t.Catches.Select(c => c.Body)),
        _ => [],
    };

    static Expr Rw(Expr e, Func<Expr, Expr?> f) => f(e) ?? e.Map(c => Rw(c, f));

    static void MapSt(St s, Func<Expr, Expr> g)
    {
        switch (s)
        {
            case SAssign a: a.Lhs = g(a.Lhs); a.Rhs = g(a.Rhs); break;
            case SExpr x: x.E = g(x.E); break;
            case SReturn { E: not null } r: r.E = g(r.E!); break;
            case SThrow t: t.E = g(t.E); break;
            case SIf i: i.C = g(i.C); break;
            case SLoop l:
                if (l.C is not null) l.C = g(l.C);
                if (l.Iterable is not null) l.Iterable = g(l.Iterable);
                break;
            case SSwitch w:
                w.Subject = g(w.Subject);
                foreach (var (labels, _) in w.Cases) for (int k = 0; k < labels.Count; k++) labels[k] = g(labels[k]);
                break;
        }
    }

    static void MapBody(List<St> body, Func<Expr, Expr?> f)
    {
        foreach (var s in Passes.All(body).ToList()) MapSt(s, e => Rw(e, f));
    }

    void MapAll(Func<Expr, Expr?> f) { foreach (var m in ms) MapBody(m.Body, f); }

    Meth? Callee(Expr e) => e is ECall { Kind: CallKind.Instance, Recv: null, Fn: null } c ? ByName(c.Name) : null;

    IEnumerable<Meth> CallsIn(List<St> body) => AllExprs(body).Select(Callee).Where(m => m is not null).Select(m => m!);

    static bool IsField(Expr e, string name) => e is EProgField f && f.Name == name;

    static ECall Jdk(Expr recv, string name, JType type, params Expr[] args) => new() { Kind = CallKind.Jdk, Name = name, Recv = recv, Args = [.. args], Type = type };

    static ECall Call(string name, JType? type, params Expr[] args) => new() { Kind = CallKind.Instance, Name = name, Args = [.. args], Type = type };

    /// <summary>The record instance an expression reads or writes directly (the program's field, not a parameter).</summary>
    string? InstanceOf(Expr e) => e switch
    {
        ERecRaw { Via: null } r => r.Instance,
        ERecordField { Via: null } f => A.RecordOf(ap, f.Item)?.Instance,
        EProgField p when instances.ContainsKey(p.Name) => p.Name,
        _ => null,
    };

    bool Touches(List<St> body, string inst) => AllExprs(body).Any(e => InstanceOf(e) == inst);

    /// <summary>An expression without effects that cannot fail: it may be evaluated earlier, later, or not at all.</summary>
    bool Stable(Expr e) => e switch
    {
        ELit or EVar or EProgField => true,
        ERecRaw r => (r.Offset is null || Stable(r.Offset)) && (r.Via is null || Stable(r.Via)),
        ERecordField f => f.Type?.Name != "java.time.LocalDate" && f.Index is null && (f.Via is null || Stable(f.Via)),
        EUnary u => Stable(u.E),
        EBinary b => (b.Op is not ("/" or "%") || b.R is ELit { Value: long d } && d != 0) && Stable(b.L) && Stable(b.R),
        ECond c => Stable(c.C) && Stable(c.A) && Stable(c.B),
        ECall { Kind: CallKind.JdkStatic, Name: "isNumeric" or "isYesNo" or "compare" or "fit" or "pad" or "fits" } c => c.Owner == RecordsOwner && c.Args.All(Stable),
        ECall { Kind: CallKind.Jdk, Name: "equals" or "isEmpty" or "equalsIgnoreCase", Recv: { } r } c => Stable(r) && c.Args.All(Stable),
        ECall { Kind: CallKind.Jdk, Recv: EProgField rp } c when rp.Name == R.ReportField && c.Name.StartsWith("format", StringComparison.Ordinal) => c.Args.All(Stable),
        ECall { Kind: CallKind.Jdk, Recv: EVar gv, Args.Count: 0 } c when groupType is not null && gv.V.Type?.Name == groupType && groupComponents.Contains(c.Name) => true,
        ECall { Kind: CallKind.Instance } c when Callee(c) is { Predicate: true } => c.Args.All(Stable),
        ECall { Kind: CallKind.Jdk, Name: "status", Args.Count: 0, Recv: EProgField fp } => fileFields.Contains(fp.Name),
        ECall { Kind: CallKind.Jdk, Args.Count: 0, Recv: EProgField sp } c when nestedField is { } nf && sp.Name == nf.Field && nestedQueries.Contains(c.Name) => true,
        _ => false,
    };

    // ================================================================== control flow facts

    HashSet<Meth> noReturn = [];

    void ComputeNoReturn()
    {
        noReturn = [];
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var m in ms)
                if (!noReturn.Contains(m) && !Passes.All(m.Body).Any(s => s is SReturn) && !Completes(m.Body)) { noReturn.Add(m); changed = true; }
        }
    }

    bool Completes(List<St> list) => list.All(Completes);

    bool Completes(St s) => s switch
    {
        SThrow or SReturn or SBreak or SContinue => false,
        SExpr or SAssign => !Exprs(s).SelectMany(Deep).Any(e => Callee(e) is { } c && noReturn.Contains(c)),
        SIf i => Completes(i.Then) || Completes(i.Else),
        SSwitch w => w.Default is null || w.Cases.Any(c => Completes(c.Body)) || Completes(w.Default),
        SLoop l => l.C is not null || l.Each is not null || Passes.All(l.Body).Any(x => x is SBreak),
        STry t => Completes(t.Body) || t.Catches.Any(c => Completes(c.Body)),
        _ => true,
    };

    /// <summary>Whether javac sees the end of the list as reachable (it does not know a call never returns).</summary>
    static bool JavaCompletes(List<St> list) => list.Count == 0 || JavaCompletes(list[^1]);

    static bool JavaCompletes(St s) => s switch
    {
        SReturn or SThrow or SBreak or SContinue => false,
        SIf i => i.Else.Count == 0 || JavaCompletes(i.Then) || JavaCompletes(i.Else),
        SSwitch w => w.Default is null || w.Cases.Any(c => JavaCompletes(c.Body)) || JavaCompletes(w.Default),
        SLoop l => l.C is not null || l.Each is not null || l.Init is not null || Passes.All(l.Body).Any(x => x is SBreak),
        STry t => JavaCompletes(t.Body) || t.Catches.Any(c => JavaCompletes(c.Body)),
        _ => true,
    };

    /// <summary>The methods in call order (callers first); null when the paragraphs call each other recursively.</summary>
    List<Meth>? CallOrder()
    {
        var order = new List<Meth>();
        var state = new Dictionary<Meth, int>();
        bool cyclic = false;
        void Visit(Meth m)
        {
            if (state.TryGetValue(m, out var st)) { if (st == 1) cyclic = true; return; }
            state[m] = 1;
            foreach (var c in CallsIn(m.Body).Distinct()) Visit(c);
            state[m] = 2;
            order.Add(m);
        }
        Visit(Entry!);
        foreach (var m in ms) Visit(m);
        if (cyclic) return null;
        order.Reverse();
        return order;
    }

    // ================================================================== values

    /// <summary>A work field's initial value (its VALUE clause) as the Java field had it; null when not a simple value.</summary>
    static Expr? Initial(PromotedField pf)
    {
        var it = pf.Item;
        var v = it.Values.Count == 1 ? it.Values[0].Low : null;
        switch (pf.Type.Name)
        {
            case "boolean": return ELit.Bool(v is { Quoted: true, Text: "Y" });
            case "java.lang.String":
            {
                if (v is null) return ELit.Str("");
                var text = v.Quoted ? v.Text : v.Text.ToUpperInvariant() switch { "SPACE" or "SPACES" => "", "ZERO" or "ZEROS" or "ZEROES" => new string('0', it.Length), _ => v.Text };
                if (text.Length > it.Length) text = text[..it.Length];
                return ELit.Str(text.TrimEnd());
            }
            case "int" or "long":
            {
                long n = 0;
                if (v is not null && !long.TryParse(v.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                {
                    if (v.Quoted || !v.Text.StartsWith("ZERO", StringComparison.OrdinalIgnoreCase)) return null;
                    n = 0;
                }
                return pf.Type.Name == "long" ? new ELit(n, JType.Long) { Type = JType.Long } : ELit.Int(n);
            }
        }
        return null;
    }

    HashSet<string> Written() => ms.SelectMany(m => Passes.All(m.Body)).OfType<SAssign>().Select(a => a.Lhs).OfType<EProgField>().Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>A text field never written keeps its VALUE: its reads are the literal.</summary>
    void InlineConstants()
    {
        var written = Written();
        foreach (var pf in fields.Values)
        {
            if (pf.Type.Name != "java.lang.String" || written.Contains(pf.Name) || pf.Item.Values.Count != 1 || Initial(pf) is not ELit lit) continue;
            MapAll(e => IsField(e, pf.Name) ? ELit.Str((string)lit.Value!) : null);
        }
    }

    // ================================================================== datasets

    static bool StatusStore(St s, string file, string status) =>
        s is SAssign { Lhs: EProgField { Name: var n }, Rhs: ECall { Kind: CallKind.Jdk, Name: "status", Recv: EProgField { Name: var fn } } } && n == status && fn == file;

    /// <summary>!status.equals("code")</summary>
    static bool NotStatus(Expr c, string status, string code) =>
        c is EUnary { Op: "!", E: ECall { Name: "equals", Recv: EProgField { Name: var n }, Args: [ELit { Value: string v }] } } && n == status && v == code;

    static ECall? FileOp(St s, string file, params string[] ops) => s switch
    {
        SExpr { E: ECall { Kind: CallKind.Jdk, Recv: EProgField { Name: var f } } c } when f == file && ops.Contains(c.Name) => c,
        SAssign { Lhs: EVar, Rhs: ECall { Kind: CallKind.Jdk, Recv: EProgField { Name: var f2 } } c2 } when f2 == file && ops.Contains(c2.Name) => c2,
        _ => null,
    };

    Var NewVar(string name, JType type) => new() { Name = name, Type = type, Fixed = true };

    JType DatasetException => JType.Of(A.PlatformPackage + ".DatasetException");

    /// <summary>The handler of a status check, the status read from the exception: catch (DatasetException e) { ... e.status() ... }.</summary>
    STry Guard(List<St> body, List<St> handler, string status, int line)
    {
        var e = NewVar("e", DatasetException);
        MapBody(handler, x => IsField(x, status) ? Jdk(new EVar(e), "status", JType.String) : null);
        var t = new STry(body) { Line = line };
        t.Catches.Add((DatasetException, e, handler));
        return t;
    }

    /// <summary>
    /// FILE STATUS to the adapter: every operation whose status the program checks ("00", or "00"/"10" after a READ) throws
    /// the adapter's DatasetException instead, the check's statements its handler; the status field goes when no read of
    /// it is left. The priming read (READ; PERFORM UNTIL EOF ... READ) becomes a for-each over the dataset's records.
    /// </summary>
    void Datasets()
    {
        foreach (var fd in ap.P.Model.Files)
        {
            if (fd.FileStatus is null || !ap.P.ItemsByName.TryGetValue(fd.FileStatus, out var stItem) || !ap.Promoted.TryGetValue(stItem, out var stf)) continue;
            bool indexed = fd.Organization == "INDEXED";
            string file = ap.FileFields[fd.Name], status = stf.Name;
            var covered = new HashSet<Expr>(ReferenceEqualityComparer.Instance);
            var apply = new List<Action>();
            foreach (var m in ms)
                foreach (var list in Lists(m.Body).ToList())
                    for (int i = 0; i + 2 < list.Count; i++)
                    {
                        if (indexed && KeyedRead(m, list, i, file, status, covered) is { } keyed) { apply.Add(keyed); continue; }
                        if (FileOp(list[i], file, indexed ? ["openInput"] : ["openInput", "openOutput", "write"]) is not { } op || list[i] is not SExpr || !StatusStore(list[i + 1], file, status)
                            || list[i + 2] is not SIf { Else.Count: 0 } chk || !NotStatus(chk.C, status, "00")) continue;
                        foreach (var x in Deep(chk.C).Concat(AllExprs(chk.Then))) if (IsField(x, status)) covered.Add(x);
                        if (Passes.All(chk.Then).Any(s => s is SAssign { Lhs: EProgField { Name: var w } } && w == status)) continue;
                        var (l, opSt, storeSt) = (list, list[i], list[i + 1]);
                        var name = op.Name switch { "openInput" => "openForReading", "openOutput" => "openForWriting", _ => "append" };
                        apply.Add(() =>
                        {
                            int k = l.IndexOf(opSt);
                            var call = new ECall { Kind = CallKind.Jdk, Name = name, Recv = op.Recv, Args = op.Args, Type = JType.Void };
                            var guard = Guard([new SExpr(call) { Line = opSt.Line }], chk.Then, status, opSt.Line);
                            l[k] = guard;
                            l.Remove(storeSt);
                            l.Remove(chk);
                        });
                    }
            var loop = indexed ? null : PrimingRead(file, status, covered);
            if (loop is not null) apply.Add(loop);
            // every read of the status is a check that goes: the status field goes with it
            bool all = ms.All(m => Reads(m.Body).All(x => !IsField(x, status) || covered.Contains(x))
                && Passes.All(m.Body).OfType<SAssign>().All(a => !IsField(a.Lhs, status) || a.Rhs is ECall { Name: "status" }));
            if (!all || apply.Count == 0) continue;
            foreach (var a in apply) a();
            foreach (var m in ms)
                foreach (var list in Lists(m.Body).ToList())
                    list.RemoveAll(s => s is SAssign a && IsField(a.Lhs, status));
            R.Notes.Add($"{fd.Name}: FILE STATUS {fd.FileStatus} checks are the dataset adapter's exceptions");
        }
    }

    /// <summary>
    /// A keyed READ whose status the program evaluates: "00" found, "23" not found, any other a failure. The adapter's find
    /// answers found or not and throws for the rest: try { if (file.find(key, record)) A else B } catch (DatasetException e) { other }.
    /// </summary>
    Action? KeyedRead(Meth m, List<St> list, int i, string file, string status, HashSet<Expr> covered)
    {
        if (FileOp(list[i], file, "read") is not { Args.Count: 2 } rd || !StatusStore(list[i + 1], file, status)) return null;
        if (list[i + 2] is not SSwitch { Subject: EProgField { Name: var sn }, Default: { } other } sw || sn != status || sw.Cases.Count != 2) return null;
        List<St>? Case(string code) => sw.Cases.FirstOrDefault(c => c.Labels is [ELit { Value: string v }] && v == code).Body;
        if (Case("00") is not { } found || Case("23") is not { } missing) return null;
        // the found flag is not read; the branches neither call nor touch datasets (they run inside the try)
        if (list[i] is SAssign { Lhs: EVar { V: var fv } } && Reads(m.Body).Any(e => e is EVar ev && ev.V == fv)) return null;
        bool Quiet(List<St> b) => !AllExprs(b).Any(e => Callee(e) is not null || e is ECall { Recv: EProgField rp } && (fileFields.Contains(rp.Name) || calleeFields.Contains(rp.Name)) || IsField(e, status))
            && !Passes.All(b).Any(s => s is SAssign a && IsField(a.Lhs, status) || s is SReturn or SThrow);
        // branches that call or touch datasets run after the try, when the failure handler never completes (an abend)
        bool Untouched(List<St> b) => !AllExprs(b).Any(e => IsField(e, status)) && !Passes.All(b).Any(s => s is SAssign a && IsField(a.Lhs, status));
        bool Ends(List<St> b) => b.Count > 0 && (b[^1] is SThrow || b[^1] is SExpr { E: var last } && Callee(last) is { Body: [.., SThrow] } c && !Passes.All(c.Body).Any(s => s is SReturn or SBreak));
        bool after = !(Quiet(found) && Quiet(missing));
        if (after && !(Untouched(found) && Untouched(missing) && Ends(other))) return null;
        if (Passes.All(other).Any(s => s is SAssign a && IsField(a.Lhs, status))) return null;
        covered.Add(sw.Subject);
        foreach (var x in AllExprs(other)) if (IsField(x, status)) covered.Add(x);
        var (opSt, storeSt) = (list[i], list[i + 1]);
        return () =>
        {
            var find = new ECall { Kind = CallKind.Jdk, Name = "find", Recv = rd.Recv, Args = rd.Args, Type = JType.Bool };
            int k = list.IndexOf(opSt);
            if (after)
            {
                // boolean found = false; try { found = file.find(key, record); } catch (DatasetException e) { abend } if (found) A else B
                var fv = NewVar("found", JType.Bool);
                m.M.L.Vars.Add(fv);
                St branch = found.Count == 0 ? new SIf(FunctionLifter.Not(new EVar(fv)), missing, []) : new SIf(new EVar(fv), found, missing);
                branch.Line = sw.Line;
                list[k] = new SAssign(new EVar(fv), ELit.Bool(false)) { Line = opSt.Line };
                list.Insert(k + 1, Guard([new SAssign(new EVar(fv), find) { Line = opSt.Line }], other, status, opSt.Line));
                list.Remove(storeSt);
                list[list.IndexOf(sw)] = branch;
                return;
            }
            St test = found.Count == 0 ? new SIf(FunctionLifter.Not(find), missing, []) : new SIf(find, found, missing);
            test.Line = sw.Line;
            list[k] = Guard([test], other, status, opSt.Line);
            list.Remove(storeSt);
            list.Remove(sw);
        };
    }

    /// <summary>
    /// The priming read of the entry paragraph: READ; PERFORM UNTIL EOF (body; READ). The read paragraph: the READ, its status,
    /// AT END the end flag, NOT AT END a part A, then the status check ("00" or "10") and parts B under NOT EOF. The loop
    /// becomes for (record : file.records(buffer)) { A B; body }, a read failure the status check's handler.
    /// </summary>
    Action? PrimingRead(string file, string status, HashSet<Expr> covered)
    {
        var entry = Entry!;
        for (int k = 0; k + 1 < entry.Body.Count; k++)
        {
            if (entry.Body[k] is not SExpr { E: var first } || Callee(first) is not { } read || read == entry) continue;
            if (entry.Body[k + 1] is not SLoop { Each: null, Init: null, C: EUnary { Op: "!", E: EProgField eofRef } } loop) continue;
            if (loop.Body.Count == 0 || loop.Body[^1] is not SExpr { E: var again } || Callee(again) != read) continue;
            string eof = eofRef.Name;
            if (!fields.TryGetValue(eof, out var eofField) || eofField.Type.Name != "boolean" || Initial(eofField) is not ELit { Value: false }) continue;
            var b = read.Body;
            if (b.Count < 4) continue;
            if (b[0] is not SAssign { Lhs: EVar { V: var found }, Rhs: ECall { Kind: CallKind.Jdk, Name: "read", Recv: EProgField { Name: var rf }, Args: [EProgField { Name: var inst }] } }
                || rf != file || !instances.ContainsKey(inst)) continue;
            if (!StatusStore(b[1], file, status)) continue;
            if (b[2] is not SIf { C: EUnary { Op: "!", E: EVar { V: var fv } }, Then: [SAssign { Lhs: EProgField { Name: var eofSet }, Rhs: ELit { Value: true } }] } atEnd || fv != found || eofSet != eof) continue;
            var parts = new List<St>(atEnd.Else);
            SIf? check = null;
            bool ok = true;
            foreach (var s in b.Skip(3))
            {
                if (s is SIf { Else.Count: 0 } c && c.C is EBinary { Op: "&&", L: var l, R: var r }
                    && (NotStatus(l, status, "00") && NotStatus(r, status, "10") || NotStatus(l, status, "10") && NotStatus(r, status, "00")) && check is null)
                    check = c;
                else if (s is SIf { C: EUnary { Op: "!", E: EProgField { Name: var e2 } }, Else.Count: 0 } g && e2 == eof) parts.AddRange(g.Then);
                else { ok = false; break; }
            }
            if (!ok || check is null) continue;
            // the flag and the found variable live in the pattern only; the read paragraph is called only here
            bool Mentions(IEnumerable<St> sts, Func<Expr, bool> p) => sts.SelectMany(s => StExprs(s)).Any(p);
            if (Mentions(parts, x => IsField(x, eof) || x is EVar { V: var v } && v == found || IsField(x, status))) continue;
            if (Mentions(check.Then, x => IsField(x, eof) || x is EVar { V: var v2 } && v2 == found)) continue;
            if (Passes.All(check.Then).Any(s => s is SAssign { Lhs: EProgField { Name: var w } } && (w == eof || w == status))) continue;
            if (ms.Any(m => m != read && m != entry && Mentions(m.Body, x => IsField(x, eof)))) continue;
            if (Mentions(entry.Body.Where(s => s != loop), x => IsField(x, eof)) || Mentions(loop.Body, x => IsField(x, eof))) continue;
            if (ms.Sum(m => CallsIn(m.Body).Count(c => c == read)) != 2) continue;
            foreach (var x in Deep(check.C).Concat(AllExprs(check.Then))) if (IsField(x, status)) covered.Add(x);
            var rc = instances[inst];
            var primer = entry.Body[k];
            return () =>
            {
                int at = entry.Body.IndexOf(primer);
                var accept = read;
                if (accept.Name.StartsWith("read", StringComparison.Ordinal) && accept.Name.Length > 4)
                {
                    var name = "accept" + accept.Name[4..];
                    if (ByName(name) is null) accept.Name = name;
                }
                accept.Body = parts;
                var each = NewVar(RecordVarName(rc), JType.Of(A.RecordPackage + "." + rc.Name));
                var body = new List<St>();
                if (parts.Count > 0) body.Add(new SExpr(Call(accept.Name, null)) { Line = entry.Body[at].Line });
                body.AddRange(loop.Body.Take(loop.Body.Count - 1));
                if (parts.Count == 0) ms.Remove(accept);
                var forEach = new SLoop(loop.Label, null, body)
                {
                    Line = loop.Line,
                    Each = each,
                    Iterable = Jdk(new EProgField(file, JType.Of(A.PlatformPackage + ".Datasets.Sequential")), "records", JType.Of("java.lang.Iterable"), new EProgField(inst, JType.Of(rc.Name))),
                };
                var guard = Guard([forEach], check.Then, status, loop.Line);
                entry.Body[at] = guard;
                entry.Body.Remove(loop);
                loopInstance = inst;
                loopVar = each;
                R.Notes.Add($"priming read of {inst}: for-each over the dataset's records ({accept.Name} per record)");
            };
        }
        return null;
    }

    /// <summary>A record's variable name: its class without the program prefix and the Record suffix (EventRecord → event).</summary>
    string RecordVarName(RecordClass rc)
    {
        var n = rc.Name;
        var prog = Naming.Pascal(ap.P.Id);
        if (n.StartsWith(prog, StringComparison.Ordinal) && n.Length > prog.Length) n = n[prog.Length..];
        if (n.EndsWith("Record", StringComparison.Ordinal) && n.Length > 6) n = n[..^6];
        return Naming.Safe(Naming.LowerFirst(n));
    }

    // ================================================================== the report port

    /// <summary>DISPLAY: a line of the step's report; a numeric edit for it: the report formatter's (the PICTURE stays in the compat lane).</summary>
    void Report()
    {
        var taken = ap.FieldNames().ToHashSet(StringComparer.Ordinal);
        var name = taken.Contains("report") ? "runReport" : "report";
        R.ReportField = name;
        var edits = new List<(SAssign A, PromotedField F, Expr V, string Pic)>();
        foreach (var m in ms)
            foreach (var s in Passes.All(m.Body).OfType<SAssign>())
                if (s.Lhs is EProgField lf && fields.TryGetValue(lf.Name, out var pf) && pf.Item.Category == Category.NumericEdited
                    && s.Rhs is ECall { Kind: CallKind.JdkStatic, Name: "edit", Args: [var v, ELit { Value: string pic }] } ec && ec.Owner == RecordsOwner)
                    edits.Add((s, pf, v, pic));
        R.ReportClass = edits.Count > 0 ? Naming.Pascal(ap.P.Id) + "Report" : "RunReport";
        var type = JType.Of(A.PlatformPackage + "." + R.ReportClass);
        Expr Port() => new EProgField(name, type) { Type = type };
        foreach (var (s, pf, v, pic) in edits)
        {
            var method = "format" + Naming.UpperFirst(pf.Name);
            if (R.Edits.FirstOrDefault(e => e.Method == method) is { Method: not null } prev && prev.Picture != pic) continue;
            if (!R.Edits.Any(e => e.Method == method)) R.Edits.Add((method, pic, pf.Item.Name));
            s.Rhs = Jdk(Port(), method, JType.String, v);
        }
        MapAll(e => e is ECall { Kind: CallKind.Jdk, Name: "println", Recv: EStaticJdk { Owner: "java.lang.System", Name: "out" }, Args.Count: 1 } c
            ? Jdk(Port(), "line", JType.Void, c.Args[0]) : null);
        R.Notes.Add($"DISPLAY: the run report ({R.ReportClass})");
    }

    // ================================================================== flags

    static bool SameTarget(Expr a, Expr b) => a is EVar va && b is EVar vb && va.V == vb.V || a is EProgField fa && b is EProgField fb && fa.Name == fb.Name;

    static bool MentionsTarget(Expr e, Expr x) => Deep(e).Any(d => SameTarget(d, x));

    /// <summary>x = true; if (c1) x = false; if (c2) x = false; ... → x = !c1 &amp;&amp; !c2 (and the mirror), the conditions stable.</summary>
    void FoldFlags(List<St> body)
    {
        foreach (var list in Lists(body).ToList())
            for (int i = 0; i + 1 < list.Count; i++)
            {
                if (list[i] is not SAssign { Rhs: ELit { Value: bool init } } a || a.Lhs is not (EVar or EProgField)) continue;
                var conds = new List<Expr>();
                int j = i + 1;
                while (j < list.Count && list[j] is SIf { Else.Count: 0, Then: [SAssign { Rhs: ELit { Value: bool set } } t] } f
                       && set == !init && SameTarget(t.Lhs, a.Lhs) && Stable(f.C) && !MentionsTarget(f.C, a.Lhs))
                {
                    conds.Add(f.C);
                    j++;
                }
                if (conds.Count == 0) continue;
                Expr acc = init ? FunctionLifter.Not(conds[0]) : conds[0];
                foreach (var c in conds.Skip(1)) acc = init ? FunctionLifter.And(acc, FunctionLifter.Not(c)) : FunctionLifter.Or(acc, c);
                a.Rhs = acc;
                list.RemoveRange(i + 1, conds.Count);
            }
    }

    /// <summary>
    /// A paragraph that only computes flags from records (DW-NUM-n = the field is numeric): each flag a predicate of the record
    /// (isAmountNumeric(event)), evaluated where the callers read the flag. Sound when each caller reads the flags after its
    /// one call and before anything can change the records they test.
    /// </summary>
    void Predicates()
    {
        foreach (var n in ms.ToList())
        {
            if (n == Entry || n.Params.Count > 0 || n.Body.Count == 0) continue;
            var flags = new List<(PromotedField F, Expr C)>();
            bool ok = true;
            foreach (var s in n.Body)
                if (s is SAssign { Lhs: EProgField pf, Rhs: var c } && fields.TryGetValue(pf.Name, out var f) && f.Type.Name == "boolean" && Stable(c)
                    && !Deep(c).Any(x => x is EVar || x is EProgField p && !instances.ContainsKey(p.Name)) && flags.All(x => x.F != f))
                    flags.Add((f, c));
                else { ok = false; break; }
            if (!ok || flags.Count == 0) continue;
            var names = flags.Select(x => x.F.Name).ToHashSet(StringComparer.Ordinal);
            var callers = ms.Where(m => m != n && CallsIn(m.Body).Contains(n)).ToList();
            if (callers.Count == 0) continue;
            if (ms.Any(m => m != n && !callers.Contains(m) && AllExprs(m.Body).Any(e => e is EProgField p && names.Contains(p.Name)))) continue;
            if (callers.Any(m => Passes.All(m.Body).OfType<SAssign>().Any(a => a.Lhs is EProgField p && names.Contains(p.Name)))) continue;
            var tested = flags.SelectMany(x => Deep(x.C)).Select(InstanceOf).Where(i => i is not null).Select(i => i!).ToHashSet(StringComparer.Ordinal);
            bool Reads(St s) => StExprs(s).Any(e => e is EProgField p && names.Contains(p.Name));
            foreach (var c in callers)
            {
                int idx = c.Body.FindIndex(s => s is SExpr { E: var e } && Callee(e) == n);
                if (idx < 0 || CallsIn(c.Body).Count(x => x == n) != 1) { ok = false; break; }
                int last = idx;
                for (int k = 0; k < c.Body.Count; k++)
                    if (k != idx && Reads(c.Body[k])) { if (k < idx) ok = false; last = k; }
                for (int k = idx + 1; ok && k <= last; k++)
                {
                    var s = c.Body[k];
                    // the head is evaluated first; the nested statements of the last one run after its reads
                    var head = Exprs(s).ToList();
                    if (k == last && s is SIf or SAssign or SExpr && !Children(s).Any(ch => Passes.All(ch).Any(Reads)))
                    {
                        if (head.SelectMany(Deep).Any(e => Mutates(e, tested))) ok = false;
                    }
                    else if (Passes.All([s]).Any(x => Exprs(x).SelectMany(Deep).Any(e => Mutates(e, tested)) || x is SAssign { Lhs: var lhs } && InstanceOf(lhs) is { } li && tested.Contains(li)))
                        ok = false;
                }
                if (!ok) break;
            }
            if (!ok) continue;
            // the predicates: named for the record field they test
            var preds = new Dictionary<string, (string Name, List<string> Insts)>(StringComparer.Ordinal);
            var made = new List<Meth>();
            foreach (var (f, c) in flags)
            {
                if (!callers.Any(m => AllExprs(m.Body).Any(e => IsField(e, f.Name)))) continue;
                var name = PredicateName(c);
                if (name is null) { ok = false; break; }
                while (ms.Any(m => m.Name == name) || made.Any(m => m.Name == name)) name += "Field";
                var insts = Deep(c).Select(InstanceOf).Where(i => i is not null).Select(i => i!).Distinct().ToList();
                var ps = insts.Select(i => NewVar(RecordVarName(instances[i]), JType.Of(A.RecordPackage + "." + instances[i].Name))).ToList();
                var body = Rw(c, e => Rebind(e, insts, ps));
                var fn = new FunctionDef { Id = ap.P.Id + "/" + name, Program = ap.P, Paragraph = n.Doc, MethodName = name };
                var l = new Lifted { Fn = fn };
                l.Params.AddRange(ps);
                made.Add(new Meth { Name = name, M = new Method { Fn = fn, L = l, Body = [new SReturn(body)] }, Doc = $"{f.Item.Name} of {n.Doc}: {Describe(c)}", Ret = JType.Bool, Static = true, Predicate = true });
                preds[f.Name] = (name, insts);
            }
            if (!ok) continue;
            foreach (var c in callers) c.Body.RemoveAll(s => s is SExpr { E: var e } && Callee(e) == n);
            int at = ms.IndexOf(n);
            ms.Remove(n);
            ms.InsertRange(Math.Min(at, ms.Count), made);
            foreach (var c in callers)
            {
                MapBody(c.Body, e => e is EProgField p && preds.TryGetValue(p.Name, out var pr)
                    ? Call(pr.Name, JType.Bool, [.. pr.Insts.Select(i => (Expr)new EProgField(i, JType.Of(instances[i].Name)))]) : null);
            }
            R.Notes.Add($"{n.Doc}: {made.Count} predicates of the record ({string.Join(", ", made.Select(m => m.Name))})");
        }
    }

    /// <summary>An expression that may change a record the predicates test: a write into it, a call that gets it, a paragraph.</summary>
    bool Mutates(Expr e, HashSet<string> tested) => e switch
    {
        ECall c when Callee(c) is { Predicate: false } => true,
        ECall { Kind: CallKind.Jdk, Recv: EProgField rp } when calleeFields.Contains(rp.Name) || fileFields.Contains(rp.Name) => true,
        ECall c => c.Args.Any(a => a is EProgField p && tested.Contains(p.Name)),
        _ => false,
    };

    Expr? Rebind(Expr e, List<string> insts, List<Var> ps)
    {
        var i = InstanceOf(e);
        int k = i is null ? -1 : insts.IndexOf(i);
        if (k < 0) return null;
        var v = new EVar(ps[k]);
        return e switch
        {
            ERecRaw r => new ERecRaw(r.Instance, r.Offset is null ? null : Rw(r.Offset, x => Rebind(x, insts, ps)), r.Length) { Type = r.Type, Item = r.Item, Via = v, Len = r.Len is null ? null : Rw(r.Len, x => Rebind(x, insts, ps)) },
            ERecordField f => new ERecordField(f.Item) { Index = f.Index, More = f.More, Padded = f.Padded, Type = f.Type, RefStart = f.RefStart, RefLen = f.RefLen, Via = v },
            EProgField => v,
            _ => null,
        };
    }

    /// <summary>is + the tested field + Numeric/YesNo/Valid: the one record field every part of the condition is a part of.</summary>
    string? PredicateName(Expr c)
    {
        var parts = Deep(c).OfType<ERecRaw>().ToList();
        var accessors = Deep(c).OfType<ERecordField>().ToList();
        DataItem? item = null;
        if (parts.Count > 0 && parts.All(p => p.Item is not null) && parts.Select(p => p.Item).Distinct().Count() == 1 && accessors.Count == 0) item = parts[0].Item;
        else if (parts.Count == 0 && accessors.Select(a => a.Item).Distinct().Count() == 1) item = accessors[0].Item;
        if (item is null || A.RecordOf(ap, item) is not { } r) return null;
        var field = r.Rc.Accessors.TryGetValue(item.Name, out var acc) ? acc : Naming.Field(item.Name);
        var calls = Deep(c).OfType<ECall>().Select(x => x.Name).ToHashSet();
        var what = calls.Contains("isNumeric") ? "Numeric" : calls.Contains("isYesNo") ? "YesNo" : "Valid";
        return "is" + Naming.UpperFirst(field) + what;
    }

    static string Describe(Expr c)
    {
        var calls = Deep(c).OfType<ECall>().Select(x => x.Name).ToHashSet();
        return calls.Contains("isNumeric") ? (calls.Contains("compare") ? "a sign and digits" : "digits only") : "valid";
    }

    // ================================================================== the loop's record

    /// <summary>The record the for-each yields is a parameter of the methods the loop calls that use it (the same buffer).</summary>
    void LoopRecord()
    {
        if (loopInstance is null || loopVar is null) return;
        var entry = Entry!;
        var loop = Passes.All(entry.Body).OfType<SLoop>().FirstOrDefault(l => l.Each == loopVar);
        if (loop is null) return;
        var reach = new HashSet<Meth>();
        var stack = new Stack<Meth>(CallsIn(loop.Body));
        while (stack.Count > 0) { var m = stack.Pop(); if (m != entry && reach.Add(m)) foreach (var c in CallsIn(m.Body)) stack.Push(c); }
        var needs = new HashSet<Meth>();
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var m in reach)
                if (!needs.Contains(m) && !m.Predicate && (Touches(m.Body, loopInstance) || CallsIn(m.Body).Any(needs.Contains))) { needs.Add(m); changed = true; }
        }
        if (needs.Count == 0) return;
        var rc = instances[loopInstance];
        var type = JType.Of(A.RecordPackage + "." + rc.Name);
        var param = new Dictionary<Meth, Var>();
        foreach (var m in needs)
        {
            var p = NewVar(loopVar.Name, type);
            param[m] = p;
            m.Params.Insert(0, p);
        }
        Expr? Bind(Expr e, Var v)
        {
            if (e is ECall c && Callee(c) is { } t && param.ContainsKey(t))
                return new ECall { Kind = c.Kind, Name = c.Name, Args = [new EVar(v), .. c.Args.Select(a => Rw(a, x => Bind(x, v)))], Type = c.Type };
            return Rebind(e, [loopInstance], [v]);
        }
        foreach (var m in needs) MapBody(m.Body, e => Bind(e, param[m]));
        MapBody(loop.Body, e => Bind(e, loopVar));
        // elsewhere the methods get the program's buffer (the same object the loop yields)
        foreach (var m in ms.Where(m => !needs.Contains(m)))
            MapBody(m.Body, e => e is ECall c && Callee(c) is { } t && param.ContainsKey(t) && !(c.Args.Count > 0 && c.Args[0] is EVar ev && ev.V.Type?.Name == type.Name)
                ? new ECall { Kind = c.Kind, Name = c.Name, Args = [new EProgField(loopInstance, JType.Of(rc.Name)), .. c.Args], Type = c.Type } : null);
        R.Notes.Add($"the loop's record {loopInstance} is a parameter of {string.Join(", ", needs.Select(m => m.Name).Order(StringComparer.Ordinal))}");
    }

    // ================================================================== threading the work fields

    readonly Dictionary<string, Sv> svs = new(StringComparer.Ordinal);
    /// <summary>Field name → the state variable it is (a work field) or is part of (a counter).</summary>
    readonly Dictionary<string, Sv> svOf = new(StringComparer.Ordinal);
    Dictionary<Meth, Sum> sums = [];

    string? KeyOf(Expr e) => e is EProgField f && svOf.TryGetValue(f.Name, out var sv) ? sv.Key : null;

    bool IsMember(Expr e) => e is EProgField f && svOf.TryGetValue(f.Name, out var sv) && sv.Members is not null;

    /// <summary>
    /// Work fields to parameters, return values and locals. Per method: the fields it reads before writing them (with the
    /// callees') are parameters; the fields it may write that a caller reads after the call are its result (a parameter too
    /// when not always written, unless every caller passes the same constant); the rest are locals. Counters (only ever
    /// incremented by one) are one immutable value. A method that would return two values keeps the less local field as a
    /// field; a recursive call graph or a BigDecimal field leaves everything as it is.
    /// </summary>
    void Thread()
    {
        ComputeNoReturn();
        var order = CallOrder();
        if (order is null) { R.Notes.Add("data flow: recursive paragraphs, fields kept"); return; }
        var used = ms.SelectMany(m => AllExprs(m.Body)).OfType<EProgField>().Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        int ord = 0;
        // counters: incremented by one (in an ON SIZE ERROR guard or not) and read; an initial value
        var counters = new List<PromotedField>();
        foreach (var pf in fields.Values.OrderBy(p => p.Item.Line))
        {
            if (!used.Contains(pf.Name) || pf.Type.Name is not ("int" or "long") || Initial(pf) is null) continue;
            var writes = ms.SelectMany(m => Passes.All(m.Body)).OfType<SAssign>().Where(a => IsField(a.Lhs, pf.Name)).ToList();
            if (writes.Count == 0 || !writes.All(a => a.Rhs is EBinary { Op: "+", L: EProgField l, R: ELit { Value: long one } } && l.Name == pf.Name && one == 1)) continue;
            // a counter is not passed by reference (a CALL argument) nor written as text
            counters.Add(pf);
        }
        if (counters.Count >= 2)
        {
            groupType = "RunTotals";
            var key = "#totals";
            var sv = new Sv { Key = key, Name = "totals", Type = JType.Of(groupType), Init = new ENew { JdkClass = groupType, Args = [.. counters.Select(c => Initial(c)!)] }, Members = counters, Order = int.MaxValue };
            svs[key] = sv;
            foreach (var c in counters) { svOf[c.Name] = sv; groupComponents.Add(c.Name); }
        }
        foreach (var pf in fields.Values.OrderBy(p => p.Item.Line))
        {
            if (!used.Contains(pf.Name) || svOf.ContainsKey(pf.Name) || Initial(pf) is not { } init) continue;
            var sv = new Sv { Key = pf.Name, Name = pf.Name, Type = pf.Type, Init = init, Field = pf, Order = ord++ };
            svs[sv.Key] = sv;
            svOf[pf.Name] = sv;
        }
        if (svs.Count == 0) return;
        // a field passed by reference to another program keeps its identity: not threaded
        foreach (var m in ms)
            foreach (var e in AllExprs(m.Body))
                if (e is ECall { Kind: CallKind.Jdk, Recv: EProgField rp } c && calleeFields.Contains(rp.Name))
                    foreach (var a in c.Args.SelectMany(Deep)) if (KeyOf(a) is { } k) Drop(k);
        Summaries(order);
        var liveOut = LiveOuts(order);
        var constEntry = ConstEntries();
        Dictionary<Meth, List<string>> ps = [], rets = [];
        for (bool again = true; again;)
        {
            again = false;
            ps.Clear(); rets.Clear();
            foreach (var m in ms)
            {
                var s = sums[m];
                var lo = liveOut.GetValueOrDefault(m) ?? [];
                var r = s.MayDef.Where(k => svs.ContainsKey(k) && lo.Contains(k)).ToList();
                var p = s.Use.Where(svs.ContainsKey).ToHashSet();
                foreach (var k in r) if (!s.MustDef.Contains(k) && !constEntry.ContainsKey((m, k))) p.Add(k);
                if (m == Entry && p.Count > 0)
                {
                    // the entry's reads before writes are the fields' initial values: locals initialized
                    p.Clear();
                }
                ps[m] = p.OrderBy(k => svs[k].Order).ToList();
                rets[m] = r.OrderBy(k => svs[k].Order).ToList();
                // a result is assigned where the call is a statement of its own
                if (r.Count == 1 && ms.Any(x => Passes.All(x.Body).Any(s => Exprs(s).Any(e => Deep(e).Any(d => Callee(d) == m) && !(s is SExpr { E: var se } && Callee(se) == m)))))
                {
                    Drop(r[0]);
                    again = true;
                    break;
                }
                if (r.Count > 1)
                {
                    // keep the counters, else the field the fewest methods touch
                    var keep = r.OrderByDescending(k => svs[k].Members is not null).ThenBy(k => ms.Count(x => sums[x].Use.Contains(k) || sums[x].MayDef.Contains(k))).ThenBy(k => svs[k].Order).First();
                    foreach (var k in r.Where(k => k != keep)) Drop(k);
                    again = true;
                    break;
                }
            }
        }
        if (svs.Count == 0) return;
        Rewrite(ps, rets, constEntry);
        R.Notes.Add($"work fields to parameters, results and locals: {string.Join(", ", svs.Values.OrderBy(v => v.Order).Select(v => v.Name))}");
    }

    void Drop(string key)
    {
        if (!svs.Remove(key, out var sv)) return;
        foreach (var k in svOf.Where(kv => kv.Value == sv).Select(kv => kv.Key).ToList()) svOf.Remove(k);
        if (sv.Members is not null) { groupType = null; groupComponents.Clear(); }
    }

    // ------------------------------------------------------------------ summaries: used before written, written, always written

    void Summaries(List<Meth> order)
    {
        sums = ms.ToDictionary(m => m, _ => new Sum());
        for (int i = order.Count - 1; i >= 0; i--)
        {
            var m = order[i];
            var s = sums[m];
            var exits = new List<HashSet<string>>();
            var end = Fwd(m.Body, [], s, exits);
            if (end is not null) exits.Add(end);
            s.MustDef = exits.Count == 0 ? [] : exits.Skip(1).Aggregate(new HashSet<string>(exits[0]), (acc, x) => { acc.IntersectWith(x); return acc; });
            s.MustDef.RemoveWhere(k => !svs.ContainsKey(k) || svs[k].Members is not null);
        }
    }

    HashSet<string>? Fwd(List<St> list, HashSet<string>? a, Sum s, List<HashSet<string>> exits)
    {
        foreach (var st in list)
        {
            if (a is null) return null;
            a = Fwd(st, a, s, exits);
        }
        return a;
    }

    static HashSet<string>? Meet(IEnumerable<HashSet<string>?> states)
    {
        HashSet<string>? acc = null;
        foreach (var st in states)
        {
            if (st is null) continue;
            if (acc is null) acc = new HashSet<string>(st);
            else acc.IntersectWith(st);
        }
        return acc;
    }

    HashSet<string>? Fwd(St st, HashSet<string> a, Sum s, List<HashSet<string>> exits)
    {
        switch (st)
        {
            case SAssign x:
            {
                bool done = Eval(x.Rhs, a, s);
                foreach (var c in x.Lhs.Children) done &= Eval(c, a, s);
                if (KeyOf(x.Lhs) is { } k)
                {
                    s.MayDef.Add(k);
                    if (IsMember(x.Lhs)) { if (!a.Contains(k)) s.Use.Add(k); }
                    else a = new HashSet<string>(a) { k };
                }
                return done ? a : null;
            }
            case SExpr x: return Eval(x.E, a, s) ? a : null;
            case SReturn r:
                if (r.E is not null) Eval(r.E, a, s);
                exits.Add(a);
                return null;
            case SThrow t: Eval(t.E, a, s); return null;
            case SIf i:
                if (!Eval(i.C, a, s)) return null;
                return Meet([Fwd(i.Then, new HashSet<string>(a), s, exits), Fwd(i.Else, new HashSet<string>(a), s, exits)]);
            case SSwitch w:
            {
                if (!Eval(w.Subject, a, s)) return null;
                var outs = w.Cases.Select(c => Fwd(c.Body, new HashSet<string>(a), s, exits)).ToList();
                outs.Add(w.Default is null ? a : Fwd(w.Default, new HashSet<string>(a), s, exits));
                return Meet(outs);
            }
            case SLoop l:
            {
                var b = a;
                if (l.Init is not null) b = Fwd(l.Init, b, s, exits) ?? b;
                if (l.C is not null) Eval(l.C, b, s);
                if (l.Iterable is not null) Eval(l.Iterable, b, s);
                var after = Fwd(l.Body, new HashSet<string>(b), s, exits);
                if (after is not null && l.Update is not null) Fwd(l.Update, after, s, exits);
                return b;
            }
            case STry t:
            {
                var outs = new List<HashSet<string>?> { Fwd(t.Body, new HashSet<string>(a), s, exits) };
                foreach (var c in t.Catches) outs.Add(Fwd(c.Body, new HashSet<string>(a), s, exits));
                return Meet(outs);
            }
            case SBlock bl:
                Fwd(bl.Body, new HashSet<string>(a), s, exits);
                return a;
            case SBreak or SContinue: return null;
            default: return a;
        }
    }

    /// <summary>The reads of an expression (and the calls' effects), in evaluation order; false when a call never returns.</summary>
    bool Eval(Expr e, HashSet<string> a, Sum s)
    {
        bool done = true;
        foreach (var c in e.Children) done &= Eval(c, a, s);
        if (KeyOf(e) is { } k && !a.Contains(k)) s.Use.Add(k);
        if (Callee(e) is { } m && sums.TryGetValue(m, out var cs))
        {
            foreach (var u in cs.Use) if (!a.Contains(u)) s.Use.Add(u);
            s.MayDef.UnionWith(cs.MayDef);
            a.UnionWith(cs.MustDef);
            if (noReturn.Contains(m)) done = false;
        }
        return done;
    }

    // ------------------------------------------------------------------ liveness: the fields a caller reads after a call

    Dictionary<Meth, HashSet<string>> LiveOuts(List<Meth> order)
    {
        var liveOut = ms.ToDictionary(m => m, _ => new HashSet<string>());
        foreach (var m in order)
        {
            var exit = liveOut[m];
            Bwd(m.Body, new HashSet<string>(exit), exit, [], liveOut);
        }
        return liveOut;
    }

    HashSet<string> Bwd(List<St> list, HashSet<string> live, HashSet<string> exit, HashSet<string> handler, Dictionary<Meth, HashSet<string>> calls)
    {
        for (int i = list.Count - 1; i >= 0; i--) live = Bwd(list[i], live, exit, handler, calls);
        return live;
    }

    HashSet<string> Bwd(St st, HashSet<string> L, HashSet<string> exit, HashSet<string> handler, Dictionary<Meth, HashSet<string>> calls)
    {
        HashSet<string> r;
        switch (st)
        {
            case SAssign x:
            {
                r = new HashSet<string>(L);
                if (KeyOf(x.Lhs) is { } k) { if (IsMember(x.Lhs)) r.Add(k); else r.Remove(k); }
                foreach (var c in x.Lhs.Children.Reverse()) r = BwdExpr(c, r, calls);
                r = BwdExpr(x.Rhs, r, calls);
                break;
            }
            case SExpr x: r = BwdExpr(x.E, new HashSet<string>(L), calls); break;
            case SReturn ret: r = ret.E is null ? new HashSet<string>(exit) : BwdExpr(ret.E, new HashSet<string>(exit), calls); break;
            case SThrow t: r = BwdExpr(t.E, new HashSet<string>(handler), calls); break;
            case SIf i:
                r = Bwd(i.Then, new HashSet<string>(L), exit, handler, calls);
                r.UnionWith(Bwd(i.Else, new HashSet<string>(L), exit, handler, calls));
                r = BwdExpr(i.C, r, calls);
                break;
            case SSwitch w:
                r = w.Default is null ? new HashSet<string>(L) : Bwd(w.Default, new HashSet<string>(L), exit, handler, calls);
                foreach (var c in w.Cases) r.UnionWith(Bwd(c.Body, new HashSet<string>(L), exit, handler, calls));
                r = BwdExpr(w.Subject, r, calls);
                break;
            case SLoop l:
            {
                var x = new HashSet<string>(L);
                for (int round = 0; round < 50; round++)
                {
                    var head = new HashSet<string>(x);
                    if (l.C is not null) head = BwdExpr(l.C, head, calls);
                    if (l.Iterable is not null) head = BwdExpr(l.Iterable, head, calls);
                    var bodyOut = new HashSet<string>(head);
                    if (l.Update is not null) bodyOut = Bwd(l.Update, bodyOut, exit, handler, calls);
                    var b = Bwd(l.Body, bodyOut, exit, handler, calls);
                    var next = new HashSet<string>(L);
                    next.UnionWith(head);
                    next.UnionWith(b);
                    if (next.SetEquals(x)) break;
                    x = next;
                }
                r = l.Init is null ? x : Bwd(l.Init, x, exit, handler, calls);
                break;
            }
            case STry t:
            {
                var c = new HashSet<string>();
                foreach (var cc in t.Catches) c.UnionWith(Bwd(cc.Body, new HashSet<string>(L), exit, handler, calls));
                var inner = new HashSet<string>(handler);
                inner.UnionWith(c);
                r = Bwd(t.Body, new HashSet<string>(L), exit, inner, calls);
                r.UnionWith(c);
                break;
            }
            case SBlock or SBreak or SContinue:
                r = new HashSet<string>(svs.Keys.Concat(L));
                break;
            default: r = new HashSet<string>(L); break;
        }
        r.UnionWith(handler);
        return r;
    }

    HashSet<string> BwdExpr(Expr e, HashSet<string> L, Dictionary<Meth, HashSet<string>> calls)
    {
        // evaluation order: the children, then the node; backwards: the node, then the children in reverse
        if (Callee(e) is { } m && sums.TryGetValue(m, out var cs))
        {
            calls[m].UnionWith(L);
            L = new HashSet<string>(L);
            L.ExceptWith(cs.MustDef);
            L.UnionWith(cs.Use);
        }
        if (KeyOf(e) is { } k) L.Add(k);
        foreach (var c in e.Children.Reverse()) L = BwdExpr(c, L, calls);
        return L;
    }

    // ------------------------------------------------------------------ constants at every call (x = ""; validate())

    Dictionary<(Meth, string), ELit> ConstEntries()
    {
        var seen = new Dictionary<(Meth, string), List<ELit?>>();
        foreach (var m in ms) CFwd(m.Body, new Dictionary<string, ELit>(StringComparer.Ordinal), seen);
        var res = new Dictionary<(Meth, string), ELit>();
        foreach (var (key, vals) in seen)
            if (vals.Count > 0 && vals.All(v => v is not null) && vals.Select(v => v!.Value).Distinct().Count() == 1) res[key] = vals[0]!;
        return res;
    }

    Dictionary<string, ELit>? CFwd(List<St> list, Dictionary<string, ELit>? s, Dictionary<(Meth, string), List<ELit?>> seen)
    {
        foreach (var st in list)
        {
            if (s is null) return null;
            s = CFwd(st, s, seen);
        }
        return s;
    }

    static Dictionary<string, ELit>? CMeet(IEnumerable<Dictionary<string, ELit>?> states)
    {
        Dictionary<string, ELit>? acc = null;
        foreach (var st in states)
        {
            if (st is null) continue;
            if (acc is null) { acc = new Dictionary<string, ELit>(st, StringComparer.Ordinal); continue; }
            foreach (var k in acc.Keys.ToList()) if (!st.TryGetValue(k, out var v) || !Equals(v.Value, acc[k].Value)) acc.Remove(k);
        }
        return acc;
    }

    void CCalls(Expr e, Dictionary<string, ELit> s, Dictionary<(Meth, string), List<ELit?>> seen)
    {
        foreach (var c in e.Children) CCalls(c, s, seen);
        if (Callee(e) is not { } m) return;
        foreach (var k in svs.Keys)
        {
            if (!seen.TryGetValue((m, k), out var l)) seen[(m, k)] = l = [];
            l.Add(s.GetValueOrDefault(k));
        }
        foreach (var k in sums[m].MayDef) s.Remove(k);
    }

    Dictionary<string, ELit>? CFwd(St st, Dictionary<string, ELit> s, Dictionary<(Meth, string), List<ELit?>> seen)
    {
        switch (st)
        {
            case SAssign x:
                CCalls(x.Rhs, s, seen);
                if (KeyOf(x.Lhs) is { } k)
                {
                    if (x.Rhs is ELit lit && !IsMember(x.Lhs)) s[k] = lit;
                    else s.Remove(k);
                }
                return s;
            case SExpr x: CCalls(x.E, s, seen); return s;
            case SReturn or SThrow: foreach (var e in Exprs(st)) CCalls(e, s, seen); return null;
            case SIf i:
                CCalls(i.C, s, seen);
                return CMeet([CFwd(i.Then, new(s), seen), CFwd(i.Else, new(s), seen)]);
            case SSwitch w:
            {
                CCalls(w.Subject, s, seen);
                var outs = w.Cases.Select(c => CFwd(c.Body, new(s), seen)).ToList();
                outs.Add(w.Default is null ? s : CFwd(w.Default, new(s), seen));
                return CMeet(outs);
            }
            default:
            {
                // a loop, a try: what their statements may write is unknown before, during and after them
                foreach (var x in Passes.All([st]))
                {
                    if (x is SAssign { Lhs: var lhs } && KeyOf(lhs) is { } k2) s.Remove(k2);
                    foreach (var e in Exprs(x).SelectMany(Deep)) if (Callee(e) is { } m) foreach (var k3 in sums[m].MayDef) s.Remove(k3);
                }
                foreach (var e in Passes.All([st]).SelectMany(Exprs)) CCalls(e, s, seen);
                foreach (var x in Passes.All([st]))
                {
                    if (x is SAssign { Lhs: var lhs2 } && KeyOf(lhs2) is { } k4) s.Remove(k4);
                }
                return st is SBreak or SContinue ? null : s;
            }
        }
    }

    // ------------------------------------------------------------------ the rewrite

    void Rewrite(Dictionary<Meth, List<string>> ps, Dictionary<Meth, List<string>> rets, Dictionary<(Meth, string), ELit> constEntry)
    {
        // what each method touches: directly, or through the parameters and results of its calls
        var touch = ms.ToDictionary(m => m, m =>
        {
            var t = AllExprs(m.Body).Select(KeyOf).Where(k => k is not null && svs.ContainsKey(k)).Select(k => k!).ToHashSet(StringComparer.Ordinal);
            foreach (var c in CallsIn(m.Body)) { t.UnionWith(ps[c]); t.UnionWith(rets[c]); }
            t.UnionWith(ps[m]);
            t.UnionWith(rets[m]);
            return t;
        });
        var baseCount = ms.ToDictionary(m => m, m => m.Params.Count);
        foreach (var m in ms)
        {
            var vars = new Dictionary<string, Var>(StringComparer.Ordinal);
            foreach (var k in ps[m])
            {
                var p = NewVar(svs[k].Name, svs[k].Type);
                vars[k] = p;
                m.Params.Add(p);
            }
            var inits = new List<(string Key, Expr Value)>();
            foreach (var k in touch[m].Where(k => !vars.ContainsKey(k)).OrderBy(k => svs[k].Order))
            {
                vars[k] = NewVar(svs[k].Name, svs[k].Type);
                if (m == Entry && sums[m].Use.Contains(k)) inits.Add((k, svs[k].Init));
                else if (constEntry.TryGetValue((m, k), out var lit) && rets[m].Contains(k) && !sums[m].MustDef.Contains(k)) inits.Add((k, new ELit(lit.Value, lit.Type) { Type = lit.Type }));
            }
            // calls: the parameters passed, the result assigned
            foreach (var list in Lists(m.Body).ToList())
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is SExpr { E: ECall c } && Callee(c) is { } t && rets[t].Count == 1)
                    {
                        var call = WithArgs(c, t, ps, vars);
                        list[i] = new SAssign(new EVar(vars[rets[t][0]]), call) { Line = list[i].Line, Site = list[i].Site };
                    }
                }
            MapBody(m.Body, e => e is ECall c && Callee(c) is { } t && ps[t].Count > 0 && c.Args.Count == baseCount[t] ? WithArgs(c, t, ps, vars) : null);
            // counters: one more → the value's next
            foreach (var list in Lists(m.Body).ToList())
                for (int i = 0; i < list.Count; i++)
                    if (list[i] is SAssign { Lhs: EProgField lf } a && svOf.TryGetValue(lf.Name, out var g) && g.Members is not null)
                        list[i] = new SAssign(new EVar(vars[g.Key]), Jdk(new EVar(vars[g.Key]), "count" + Naming.UpperFirst(lf.Name), g.Type)) { Line = a.Line, Site = a.Site };
            MapBody(m.Body, e =>
            {
                if (e is not EProgField f || !svOf.TryGetValue(f.Name, out var sv)) return null;
                if (!vars.TryGetValue(sv.Key, out var v)) return null;
                return sv.Members is null ? new EVar(v) : Jdk(new EVar(v), f.Name, f.FieldType);
            });
            // initial values where first needed
            foreach (var (k, value) in inits)
            {
                var v = vars[k];
                int at = m.Body.FindIndex(s => StExprs(s).Any(x => x is EVar ev && ev.V == v));
                if (at < 0) at = 0;
                m.Body.Insert(at, new SAssign(new EVar(v), value));
            }
            // the result
            if (rets[m].Count == 1)
            {
                var v = vars[rets[m][0]];
                m.Ret = svs[rets[m][0]].Type;
                foreach (var r in Passes.All(m.Body).OfType<SReturn>()) r.E ??= new EVar(v);
                if (JavaCompletes(m.Body)) m.Body.Add(new SReturn(new EVar(v)));
            }
        }
        groupSv = groupType is not null ? svs.Values.FirstOrDefault(s => s.Members is not null) : null;
    }

    Sv? groupSv;
    /// <summary>The counters' limits (their PIC 9(n) ON SIZE ERROR) when the totals value owns them, and its exception.</summary>
    readonly Dictionary<string, long> limits = new(StringComparer.Ordinal);
    string? overflowClass;

    /// <summary>
    /// The PIC 9(n) ON SIZE ERROR guard of every count (if (!fits(totals.x() + 1, n)) overflow(); else totals = totals.countX())
    /// into the totals value: countX() throws past 10^n - 1 and the entry paragraph's handler runs the guard's paragraph. The
    /// same: the guard's paragraph never returns, so the run ends where the count is, and nothing between catches the
    /// exception. Every count of a counter guarded, by one paragraph and one size each, or nothing changes.
    /// </summary>
    void OwnLimits()
    {
        if (groupSv is null || groupType is null || Entry is not { } entry) return;
        var guards = new List<(List<St> L, SIf G, SAssign Count)>();
        string? handler = null;
        bool ok = true;
        int counts = 0;
        foreach (var m in ms)
            foreach (var list in Lists(m.Body).ToList())
                foreach (var s in list)
                {
                    if (s is SAssign { Lhs: EVar lv, Rhs: ECall { Kind: CallKind.Jdk, Recv: EVar rv, Args.Count: 0 } } && lv.V.Type?.Name == groupType && rv.V == lv.V) counts++;
                    if (s is not SIf { C: EUnary { Op: "!", E: ECall { Kind: CallKind.JdkStatic, Name: "fits", Args: [EBinary { Op: "+", L: ECall { Kind: CallKind.Jdk, Recv: EVar gv, Name: var comp, Args.Count: 0 }, R: ELit { Value: 1L } }, ELit { Value: long digits }] } fits }, Then: [SExpr { E: var h }], Else: [SAssign { Lhs: EVar cv, Rhs: ECall { Kind: CallKind.Jdk, Recv: EVar cr, Name: var cn, Args.Count: 0 } } cnt] } g) continue;
                    if (fits.Owner != RecordsOwner || gv.V.Type?.Name != groupType || cv.V != gv.V || cr.V != gv.V || cn != "count" + Naming.UpperFirst(comp)) continue;
                    if (Callee(h) is not { } hm || ((ECall)h).Args.Count > 0 || !noReturn.Contains(hm) || handler is not null && handler != hm.Name) { ok = false; continue; }
                    handler = hm.Name;
                    var limit = (long)Math.Pow(10, digits) - 1;
                    if (limits.TryGetValue(comp, out var prev) && prev != limit) ok = false;
                    limits[comp] = limit;
                    guards.Add((list, g, cnt));
                }
        if (!ok || handler is null || guards.Count != counts || guards.Count == 0) { limits.Clear(); return; }
        overflowClass = Naming.UpperFirst(handler);
        if (overflowClass == groupType || ms.Any(m => Naming.UpperFirst(m.Name) == overflowClass && m.Name != handler)) overflowClass = "CountOverflow";
        foreach (var (list, g, cnt) in guards) list[list.IndexOf(g)] = new SAssign(cnt.Lhs, cnt.Rhs) { Line = g.Line, Site = g.Site };
        var e = NewVar("e", JType.Of(A.AppPackage + "." + ap.ClassName + "." + overflowClass));
        var tr = new STry(entry.Body) { Line = entry.Body.FirstOrDefault()?.Line ?? 0 };
        tr.Catches.Add((e.Type!, e, [new SExpr(Call(handler, null))]));
        entry.Body = [tr];
        R.Notes.Add($"the counts' PIC 9(n) limits belong to {groupType} ({guards.Count} guards gone); {handler} runs once, at the entry");
    }


    ECall WithArgs(ECall c, Meth t, Dictionary<Meth, List<string>> ps, Dictionary<string, Var> vars) =>
        new() { Kind = c.Kind, Name = c.Name, Args = [.. c.Args, .. ps[t].Select(k => (Expr)new EVar(vars[k]))], Type = t.Ret ?? c.Type };

    string GroupText(Sv g)
    {
        var comps = g.Members!.Select(m => (Type: JavaName(m.Type), m.Name, m.Item)).ToList();
        var sb = new System.Text.StringBuilder();
        sb.Append($"    /** The run's counts (legacy {string.Join(", ", comps.Select(c => c.Item.Name))}): a value; counting one more makes a new one. */\n");
        sb.Append($"    private record {groupType}({string.Join(", ", comps.Select(c => c.Type + " " + c.Name))}) {{\n");
        for (int i = 0; i < comps.Count; i++)
        {
            var args = string.Join(", ", comps.Select((c, j) => j == i ? c.Name + " + 1" : c.Name));
            var limited = limits.TryGetValue(comps[i].Name, out var max);
            var maxText = max.ToString("N0", CultureInfo.InvariantCulture);
            sb.Append($"        /** The counts with one more {comps[i].Name} ({comps[i].Item.Name}{(limited ? ", at most " + maxText : "")}). */\n");
            sb.Append($"        {groupType} count{Naming.UpperFirst(comps[i].Name)}() {{\n");
            if (limited) sb.Append($"            if ({comps[i].Name} >= {maxText.Replace(",", "_")}) throw new {overflowClass}(\"{comps[i].Item.Name}\");\n");
            sb.Append($"            return new {groupType}({args});\n");
            sb.Append("        }\n");
            if (i < comps.Count - 1) sb.Append('\n');
        }
        sb.Append("    }\n");
        if (overflowClass is not null && limits.Count > 0)
        {
            sb.Append("\n    /** A count past its PIC 9(n) limit (the legacy ON SIZE ERROR of the counter named in the message). */\n");
            sb.Append($"    private static final class {overflowClass} extends RuntimeException {{\n");
            sb.Append($"        {overflowClass}(String counter) {{\n");
            sb.Append("            super(counter);\n");
            sb.Append("        }\n");
            sb.Append("    }\n");
        }
        return sb.ToString();
    }

    // ================================================================== a method's own records

    /// <summary>
    /// A record one method fills from scratch before anything else reads it (a reject record, a date to check): that
    /// method's local, a parameter of the methods it calls with it. Records another program gets by reference keep their identity.
    /// </summary>
    void ScratchRecords()
    {
        foreach (var (inst, rc) in instances)
        {
            if (inst == loopInstance) continue;
            var users = ms.Where(m => Touches(m.Body, inst)).ToList();
            if (users.Count == 0) continue;
            // passed to another program, or read from a dataset into: its identity or its bytes come from outside
            if (ms.Any(m => AllExprs(m.Body).Any(e => e is ECall c && Callee(c) is null && c.Args.Any(a => IsField(a, inst))))) continue;
            Meth? owner = null;
            List<St>? home = null;
            int at = -1;
            foreach (var m in users)
            {
                // the first access in the method's order is a whole write; every other access follows it in its list
                var firstSt = Passes.All(m.Body).FirstOrDefault(x => x is not (SIf or SLoop or SSwitch or STry or SBlock) && StExprs(x).Any(e => InstanceOf(e) == inst)
                    || Exprs(x).SelectMany(Deep).Any(e => InstanceOf(e) == inst));
                if (firstSt is null || !WholeWrite(firstSt, inst, rc)) continue;
                var list = Lists(m.Body).First(l => l.Contains(firstSt));
                int k = list.IndexOf(firstSt);
                int inside = list.Skip(k).Sum(s => Passes.All([s]).Sum(x => Exprs(x).SelectMany(Deep).Count(e => InstanceOf(e) == inst)));
                int total = AllExprs(m.Body).Count(e => InstanceOf(e) == inst);
                if (inside != total) continue;
                if (owner is not null) { owner = null; break; }
                (owner, home, at) = (m, list, k);
            }
            if (owner is null || home is null || owner == Entry) continue;
            var readers = users.Where(m => m != owner).ToList();
            bool ok = readers.All(n => n != Entry && !n.Predicate && ms.Where(m => CallsIn(m.Body).Contains(n)).All(m => m == owner)
                && !CallsIn(n.Body).Any(x => users.Contains(x)));
            // the owner calls the readers only after the write, in the same list
            foreach (var n in readers)
            {
                int inside = home.Skip(at + 1).Sum(s => Passes.All([s]).Sum(x => Exprs(x).SelectMany(Deep).Count(e => Callee(e) == n)));
                if (inside != CallsIn(owner.Body).Count(c => c == n)) ok = false;
            }
            if (!ok) continue;
            var type = JType.Of(A.RecordPackage + "." + rc.Name);
            var name = RecordVarName(rc) + (RecordVarName(rc).EndsWith("Record", StringComparison.Ordinal) ? "" : "Record");
            var local = NewVar(name, type);
            var write = (SAssign)home[at];
            // MOVE SPACES to the record is its creation only when a new record is spaces (no VALUE, no binary zero)
            bool blank = write.Rhs is ELit { Value: string sp } && sp.Trim().Length == 0 && AppWriter.InitialImage(rc, A.PlatformPackage, [], []).Count == 0;
            var create = new SAssign(new EVar(local), new ENew { JdkClass = A.RecordPackage + "." + rc.Name }) { Line = write.Line };
            if (blank) home[at] = create;
            else home.Insert(at, create);
            var param = readers.ToDictionary(n => n, n => NewVar(name, type));
            foreach (var n in readers) n.Params.Insert(0, param[n]);
            Expr? Bind(Expr e, Var v) => e is ECall c && Callee(c) is { } t && param.ContainsKey(t)
                ? new ECall { Kind = c.Kind, Name = c.Name, Args = [new EVar(v), .. c.Args.Select(a => Rw(a, x => Bind(x, v)))], Type = c.Type }
                : Rebind(e, [inst], [v]);
            MapBody(owner.Body, e => Bind(e, local));
            foreach (var n in readers) MapBody(n.Body, e => Bind(e, param[n]));
            R.Notes.Add($"{inst}: a record of {owner.Name}{(readers.Count > 0 ? ", passed to " + string.Join(", ", readers.Select(r => r.Name)) : "")}");
        }
    }

    /// <summary>The statement replaces the whole record image (text(...), or its one field of the record's length) from a value not read from it.</summary>
    bool WholeWrite(St s, string inst, RecordClass rc)
    {
        if (s is not SAssign a || StExprs(new SExpr(a.Rhs)).Any(e => InstanceOf(e) == inst)) return false;
        return a.Lhs switch
        {
            ERecRaw { Via: null, Offset: null } r => r.Instance == inst && r.Length == rc.Length,
            ERecordField { Via: null, Index: null, RefStart: 0 } f => A.RecordOf(ap, f.Item)?.Instance == inst && f.Item.Offset == f.Item.Record.Offset && f.Item.Length == rc.Length,
            _ => false,
        };
    }

    // ================================================================== the month length idiom

    static bool Same(Expr a, Expr b) => IrText.Show(a) == IrText.Show(b) && a.GetType() == b.GetType();

    static bool IsInt(Expr e, long v) => e is ELit { Value: long x } && x == v;

    /// <summary>
    /// The Gregorian month length COBOL computes by hand: r4 = y % 4, r100 = y % 100, r400 = y % 400; EVALUATE m: 2 → 29
    /// when (r400 = 0 or r4 = 0 and r100 not = 0) else 28; 4, 6, 9, 11 → 30; other → 31. Under a guard 0 &lt; m &lt; 13
    /// this is YearMonth.of(y, m).lengthOfMonth() (the ISO leap rule is the same for every year).
    /// </summary>
    void LeapYears(Meth m)
    {
        foreach (var guard in Passes.All(m.Body).OfType<SIf>().ToList())
        {
            var list = guard.Then;
            int w = list.FindIndex(s => s is SSwitch);
            if (w < 0 || list[w] is not SSwitch sw || !Stable(sw.Subject)) continue;
            var month = sw.Subject;
            // the guard: m > 0 (or >= 1) and m < 13 (or <= 12) among its conjuncts
            var conj = new List<Expr>();
            void Split(Expr e) { if (e is EBinary { Op: "&&" } b) { Split(b.L); Split(b.R); } else conj.Add(e); }
            Split(guard.C);
            bool low = conj.Any(c => c is EBinary { Op: ">", R: var r } g && Same(g.L, month) && IsInt(r, 0) || c is EBinary { Op: ">=", R: var r2 } g2 && Same(g2.L, month) && IsInt(r2, 1));
            bool high = conj.Any(c => c is EBinary { Op: "<", R: var r } g && Same(g.L, month) && IsInt(r, 13) || c is EBinary { Op: "<=", R: var r2 } g2 && Same(g2.L, month) && IsInt(r2, 12));
            if (!low || !high) continue;
            // everything before the switch in the guard: stable assignments of locals
            if (!list.Take(w).All(s => s is SAssign { Lhs: EVar } a && Stable(a.Rhs))) continue;
            Var? len = null;
            bool Sets(List<St> body, long v) => body is [SAssign { Lhs: EVar { V: var x }, Rhs: var r }] && IsInt(r, v) && (len ??= x) == x;
            if (sw.Cases.Count != 2 || sw.Default is null || !Sets(sw.Default, 31)) continue;
            var (feb, thirty) = sw.Cases[0].Labels is [var l2] && IsInt(l2, 2) ? (sw.Cases[0], sw.Cases[1]) : (sw.Cases[1], sw.Cases[0]);
            if (feb.Labels is not [var fl] || !IsInt(fl, 2)) continue;
            if (thirty.Labels.Count != 4 || !new long[] { 4, 6, 9, 11 }.All(v => thirty.Labels.Any(l => IsInt(l, v))) || !Sets(thirty.Body, 30)) continue;
            if (feb.Body is not [SIf { C: var leap, Then: var t29, Else: var t28 }] || !Sets(t29, 29) || !Sets(t28, 28)) continue;
            // leap: r400 == 0 || r4 == 0 && r100 != 0, each r the year modulo its number
            Expr? year = null;
            bool Rem(Expr e, long n, string op)
            {
                if (e is not EBinary { Op: var o, L: EVar { V: var r }, R: var z } || o != op || !IsInt(z, 0)) return false;
                var def = list.Take(w).OfType<SAssign>().LastOrDefault(a => a.Lhs is EVar ev && ev.V == r);
                if (def?.Rhs is not EBinary { Op: "%", L: var y, R: var d } || !IsInt(d, n)) return false;
                if (year is not null && !Same(year, y)) return false;
                year ??= y;
                // the remainder's variable is not assigned again before the switch
                return list.Take(w).OfType<SAssign>().Count(a => a.Lhs is EVar ev2 && ev2.V == r) == 1;
            }
            if (leap is not EBinary { Op: "||", L: var p400, R: EBinary { Op: "&&", L: var p4, R: var p100 } }
                || !Rem(p400, 400, "==") || !Rem(p4, 4, "==") || !Rem(p100, 100, "!=") || year is null || len is null) continue;
            if (Deep(year).Any(e => e is EVar ev && list.Take(w).OfType<SAssign>().Any(a => a.Lhs is EVar lv && lv.V == ev.V))) continue;
            var yearMonth = new ECall { Kind = CallKind.JdkStatic, Owner = "java.time.YearMonth", Name = "of", Args = [year, month], Type = JType.Of("java.time.YearMonth") };
            list[w] = new SAssign(new EVar(len), Jdk(yearMonth, "lengthOfMonth", JType.Int)) { Line = sw.Line, Site = sw.Site };
            R.Notes.Add($"{m.Name}: the month length is YearMonth's (java.time)");
        }
    }

    // ================================================================== Y/N locals

    static bool Letter(Expr e, out bool yes)
    {
        yes = e is ELit { Value: "Y" };
        return e is ELit { Value: "Y" or "N" };
    }

    /// <summary>
    /// A local text that only ever holds 'Y' or 'N' (a work flag that is now a method's own) and is only compared with them:
    /// a boolean. Its values: 'Y' true, 'N' false, c ? 'Y' : 'N' the condition.
    /// </summary>
    bool YesNoLocals(Meth m)
    {
        bool any = false;
        var cands = Passes.All(m.Body).OfType<SAssign>().Select(a => a.Lhs).OfType<EVar>().Select(v => v.V).Distinct()
            .Where(v => v.Fixed && v.Type?.Name == "java.lang.String" && IsLocal(m, v)).ToList();
        foreach (var v in cands)
        {
            bool IsV(Expr e) => e is EVar ev && ev.V == v;
            bool Value(Expr e) => Letter(e, out _) || e is ECond { A: var a, B: var b } && Letter(a, out var ya) && Letter(b, out var yb) && ya != yb;
            if (!Passes.All(m.Body).OfType<SAssign>().Where(a => IsV(a.Lhs)).All(a => Value(a.Rhs))) continue;
            int reads = Reads(m.Body).Count(IsV);
            int tests = Reads(m.Body).Count(e => e is ECall { Name: "equals", Recv: var r, Args: [var x] } && (IsV(r) && Letter(x, out _) || Letter(r, out _) && IsV(x)));
            if (reads != tests) continue;
            Expr Bool(Expr e)
            {
                if (Letter(e, out var y)) return ELit.Bool(y);
                var c = (ECond)e;
                Letter(c.A, out var ya);
                return ya ? c.C : FunctionLifter.Not(c.C);
            }
            foreach (var a in Passes.All(m.Body).OfType<SAssign>().Where(a => IsV(a.Lhs))) a.Rhs = Bool(a.Rhs);
            MapBody(m.Body, e => e is ECall { Name: "equals", Recv: var r, Args: [var x] } && (IsV(r) || IsV(x))
                ? (Letter(IsV(r) ? x : r, out var y) && y ? new EVar(v) { Type = JType.Bool } : FunctionLifter.Not(new EVar(v) { Type = JType.Bool })) : null);
            v.Type = JType.Bool;
            any = true;
        }
        return any;
    }

    // ================================================================== the state the records leave behind

    /// <summary>
    /// The work fields left after the data flow (each record's place in the sequence: the previous key, the current group,
    /// what it has seen, whether its rest is skipped) are one concept: a nested object owns them and the method that keeps
    /// them (the per-record method, now its accept); the other methods only set a flag of it or test one, through its
    /// methods. Mutable state inside one cohesive object replaces fields shared across paragraphs. Applies when one method
    /// touches every field and the others only set a boolean of them to true or read it.
    /// </summary>
    void StateObject()
    {
        var progFields = ms.SelectMany(m => AllExprs(m.Body)).OfType<EProgField>().Select(f => f.Name).Where(fields.ContainsKey).ToHashSet(StringComparer.Ordinal);
        var state = progFields.Select(n => fields[n]).Where(pf => Initial(pf) is not null).OrderBy(pf => pf.Item.Line).ToList();
        if (state.Count < 2 || state.Count != progFields.Count) return;
        var names = state.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var owner = ms.Where(m => m != Entry && !m.Static).OrderByDescending(m => AllExprs(m.Body).OfType<EProgField>().Select(f => f.Name).Where(names.Contains).Distinct().Count()).FirstOrDefault();
        if (owner is null || !names.All(n => AllExprs(owner.Body).Any(e => IsField(e, n)))) return;
        // elsewhere: only "flag = true" statements and reads of a boolean flag
        var outside = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in ms.Where(m => m != owner))
            foreach (var s in Passes.All(m.Body))
            {
                if (s is SAssign { Lhs: EProgField lf } a && names.Contains(lf.Name))
                {
                    if (fields[lf.Name].Type.Name != "boolean" || a.Rhs is not ELit { Value: true }) return;
                    outside.Add(lf.Name);
                }
                foreach (var e in s is SAssign sa ? sa.Lhs.Children.Append(sa.Rhs).SelectMany(Deep) : Exprs(s).SelectMany(Deep))
                    if (e is EProgField rf && names.Contains(rf.Name))
                    {
                        if (fields[rf.Name].Type.Name != "boolean") return;
                        outside.Add(rf.Name);
                    }
            }
        if (CallsIn(owner.Body).Any()) { if (CallsIn(owner.Body).Any(c => AllExprs(c.Body).OfType<EProgField>().Any(f => names.Contains(f.Name)))) return; }
        var className = loopVar is not null && owner.Params.Any(p => p.Type?.Name == loopVar.Type?.Name) ? Naming.UpperFirst(loopVar.Name) + "Sequence" : Naming.UpperFirst(owner.Name) + "State";
        var taken = ap.FieldNames().ToHashSet(StringComparer.Ordinal);
        var field = taken.Contains("sequence") || !className.EndsWith("Sequence", StringComparison.Ordinal) ? Naming.LowerFirst(className) : "sequence";
        if (taken.Contains(field) || ms.Any(m => Naming.UpperFirst(m.Name) == className)) return;
        var type = JType.Of(className);
        Expr Obj() => new EProgField(field, type) { Type = type };
        // the record sequence keeps track of each record; another state object accepts it
        var newName = className.EndsWith("Sequence", StringComparison.Ordinal) ? "track" : owner.Name.StartsWith("accept", StringComparison.Ordinal) ? "accept" : owner.Name;
        // the outside operations: set a flag, ask it; a flag every setter sets beside a reject (a reject written, counted)
        // is the rejection of the current group: reject() and rejected()
        var setName = new Dictionary<string, string>(StringComparer.Ordinal);
        var askName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in outside)
        {
            var sites = ms.Where(m => m != owner).SelectMany(m => Lists(m.Body)).Where(l => l.Any(s => s is SAssign { Lhs: EProgField lf } && lf.Name == n)).ToList();
            bool rejects = sites.Count > 0 && sites.All(l => l.SelectMany(s => StExprs(s)).Any(e => e is ECall c && c.Name.Contains("reject", StringComparison.OrdinalIgnoreCase)));
            if (rejects && !setName.ContainsValue("reject")) { setName[n] = "reject"; askName[n] = "rejected"; }
            else { setName[n] = n; askName[n] = "should" + Naming.UpperFirst(n); }
        }
        nestedQueries.UnionWith(askName.Values);
        var methods = new List<AppMethod>();
        foreach (var n in outside.OrderBy(n => fields[n].Item.Line))
        {
            var pf = fields[n];
            var fnSet = new FunctionDef { Id = ap.P.Id + "/" + className + "." + setName[n], Program = ap.P, Paragraph = n, MethodName = setName[n] };
            methods.Add(new AppMethod($"void {setName[n]}", new Method { Fn = fnSet, L = new Lifted { Fn = fnSet }, Body = [new SAssign(new EProgField(n, JType.Bool) { Type = JType.Bool }, ELit.Bool(true))] }, setName[n] == n ? $"{pf.Item.Name} on" : $"The current group is rejected: its other records are skipped ({pf.Item.Name} on)"));
            var ask = askName[n];
            var fnAsk = new FunctionDef { Id = ap.P.Id + "/" + className + "." + ask, Program = ap.P, Paragraph = n, MethodName = ask };
            methods.Add(new AppMethod($"boolean {ask}", new Method { Fn = fnAsk, L = new Lifted { Fn = fnAsk }, Body = [new SReturn(new EProgField(n, JType.Bool) { Type = JType.Bool })] }, setName[n] == n ? $"Whether {pf.Item.Name} is on" : $"Whether the current group is rejected ({pf.Item.Name})"));
        }
        foreach (var m in ms.Where(m => m != owner))
        {
            foreach (var list in Lists(m.Body).ToList())
                for (int i = 0; i < list.Count; i++)
                    if (list[i] is SAssign { Lhs: EProgField lf } a && names.Contains(lf.Name))
                        list[i] = new SExpr(Jdk(Obj(), setName[lf.Name], JType.Void)) { Line = a.Line, Site = a.Site };
            MapBody(m.Body, e => e is EProgField rf && names.Contains(rf.Name) ? Jdk(Obj(), askName[rf.Name], JType.Bool)
                : e is ECall { Kind: CallKind.Instance, Recv: null } c && c.Name == owner.Name ? new ECall { Kind = CallKind.Jdk, Name = newName, Recv = Obj(), Args = c.Args, Type = c.Type } : null);
        }
        ms.Remove(owner);
        owner.Name = newName;
        methods.Insert(0, new AppMethod($"{(owner.Ret is null ? "void" : JavaName(owner.Ret))} {newName}", owner.M, owner.Doc));
        var lines = state.Select(pf => $"private {JavaName(pf.Type)} {pf.Name}{InitialText(pf)};").ToList();
        R.Nested.Add(($"Where the run is in the sequence of {(loopVar is null ? "records" : loopVar.Name + " records")} (legacy {string.Join(", ", state.Select(s => s.Item.Name))}): the state the records leave behind and the method that keeps it.", className, field, lines, methods));
        R.NestedFields.UnionWith(state);
        R.Notes.Add($"{string.Join(", ", names)}: one {className} object ({newName}{(outside.Count > 0 ? ", " + string.Join(", ", outside) : "")})");

        nestedField = (field, className);
    }

    (string Field, string Class)? nestedField;
    readonly HashSet<string> nestedQueries = new(StringComparer.Ordinal);

    static string InitialText(PromotedField pf) => Initial(pf) switch
    {
        ELit { Value: bool b } => b ? " = true" : "",
        ELit { Value: string s } => " = \"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        ELit { Value: long n } => n == 0 ? "" : " = " + n.ToString(CultureInfo.InvariantCulture),
        _ => "",
    };

    // ================================================================== trivial paragraphs

    /// <summary>
    /// A paragraph that only guards one call (if (c) x = call(...); return x;), called once: its guard moves to the caller,
    /// with the paragraph's name as a comment (2000-PROCESS-RECORD). The guard is stable and the parameters are the
    /// caller's variables, so evaluating them at the call site is the same.
    /// </summary>
    void InlineGuards()
    {
        foreach (var m in ms.ToList())
        {
            if (m == Entry || m.Static) continue;
            if (m.Body is not ([SIf { Else.Count: 0, Then.Count: 1 }] or [SIf { Else.Count: 0, Then.Count: 1 }, SReturn { E: EVar }])) continue;
            var guard = (SIf)m.Body[0];
            var only = guard.Then[0];
            var ret = m.Body.Count == 2 ? ((EVar)((SReturn)m.Body[1]).E!).V : null;
            if (!Stable(guard.C) || only is not (SExpr { E: ECall } or SAssign { Lhs: EVar, Rhs: ECall })) continue;
            if (ret is not null && (only is not SAssign { Lhs: EVar lv } || lv.V != ret || !m.Params.Contains(ret))) continue;
            // the one call site, a statement of its own
            var sites = new List<(List<St> L, int I, Meth Caller)>();
            foreach (var c in ms.Where(c => c != m))
                foreach (var list in Lists(c.Body))
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] is SExpr { E: ECall ce } && Callee(ce) == m || list[i] is SAssign { Rhs: ECall ca } && Callee(ca) == m) sites.Add((list, i, c));
            if (sites.Count != 1 || ms.Sum(c => CallsIn(c.Body).Count(x => x == m)) != 1) continue;
            var (L, I, caller) = sites[0];
            var call = (ECall)(L[I] is SExpr se ? se.E : ((SAssign)L[I]).Rhs);
            if (call.Args.Count != m.Params.Count || call.Args.Any(a => a is not (EVar or EProgField or ELit))) continue;
            // the result is the variable passed for the returned parameter
            if (ret is not null && (L[I] is not SAssign { Lhs: EVar target } || call.Args[m.Params.IndexOf(ret)] is not EVar passed || passed.V != target.V)) continue;
            if (ret is null && L[I] is not SExpr) continue;
            var map = m.Params.Select((p, k) => (p, a: call.Args[k])).ToDictionary(x => x.p, x => x.a);
            Expr Sub(Expr e) => Rw(e, x => x is EVar v && map.TryGetValue(v.V, out var a) ? a : null);
            var body = only switch
            {
                SAssign a => (St)new SAssign(Sub(a.Lhs), Sub(a.Rhs)) { Line = a.Line, Site = a.Site },
                SExpr x => new SExpr(Sub(x.E)) { Line = x.Line, Site = x.Site },
                _ => only,
            };
            L[I] = new SIf(Sub(guard.C), [body], []) { Line = L[I].Line };
            L.Insert(I, new SComment(m.Doc) { Line = L[I].Line });
            ms.Remove(m);
            R.Notes.Add($"{m.Name} ({m.Doc}): only a guard; in {caller.Name}");
        }
    }

    // ================================================================== codes with an empty sentinel

    /// <summary>
    /// A text code whose empty value means "none" (DW-CODE: SPACES, or RJEVT001): the paragraphs that compute it return
    /// Optional of an enum of its codes, the caller tests isEmpty() as before and hands the present code on; the method that
    /// gets it compares enum constants and writes its code() into the record. A code mapped to a text in one branch per
    /// code (the reject texts) carries its text, so the mapping is the enum's. Applies when every value is a literal, every
    /// use is one of those, and the code is handed on only where it is present.
    /// </summary>
    void RejectCodes()
    {
        bool IsText(Var v) => v.Fixed && v.Type?.Name == "java.lang.String";
        // the methods that compute a code: text results, every value a literal, "" among them
        var producers = new Dictionary<Meth, Var>();
        foreach (var m in ms.Where(m => m.Ret?.Name == "java.lang.String"))
        {
            var rets = Passes.All(m.Body).OfType<SReturn>().ToList();
            if (rets.Count == 0 || rets.Any(r => r.E is not EVar) || rets.Select(r => ((EVar)r.E!).V).Distinct().Count() != 1) continue;
            var c = ((EVar)rets[0].E!).V;
            var vals = Passes.All(m.Body).OfType<SAssign>().Where(a => a.Lhs is EVar v && v.V == c).Select(a => a.Rhs).ToList();
            if (!IsText(c) || m.Params.Contains(c) || vals.Count == 0 || vals.Any(x => x is not ELit { Value: string }) || !vals.Any(x => x is ELit { Value: "" })) continue;
            if (Reads(m.Body).Count(e => e is EVar ev && ev.V == c) != rets.Count) continue;
            producers[m] = c;
        }
        if (producers.Count == 0) return;
        // the callers' variables: values "" or a producer's result; read only by isEmpty() and as an argument of one consumer
        var holders = new Dictionary<Meth, Var>();
        Meth? consumer = null;
        int argAt = -1;
        foreach (var m in ms)
        {
            var assigned = Passes.All(m.Body).OfType<SAssign>().Where(a => a.Rhs is ECall c && Callee(c) is { } p && producers.ContainsKey(p)).ToList();
            if (assigned.Count == 0) continue;
            if (assigned.Any(a => a.Lhs is not EVar)) return;
            var x = ((EVar)assigned[0].Lhs).V;
            if (assigned.Any(a => ((EVar)a.Lhs).V != x) || !IsText(x) || m.Params.Contains(x)) return;
            if (Passes.All(m.Body).OfType<SAssign>().Where(a => a.Lhs is EVar v && v.V == x).Any(a => !(a.Rhs is ELit { Value: "" } || a.Rhs is ECall c && Callee(c) is { } p && producers.ContainsKey(p)))) return;
            // each read: x.isEmpty(), or an argument of the consumer under !x.isEmpty()
            foreach (var e in Reads(m.Body).Where(e => e is EVar ev && ev.V == x).ToList())
            {
                bool ok = Reads(m.Body).Any(r => r is ECall { Name: "isEmpty", Recv: EVar rv, Args.Count: 0 } && ReferenceEquals(rv, e));
                if (!ok)
                {
                    var call = Reads(m.Body).OfType<ECall>().FirstOrDefault(c => c.Args.Any(a => ReferenceEquals(a, e)));
                    if (call is null || Callee(call) is not { } w || consumer is not null && consumer != w) return;
                    consumer = w;
                    argAt = call.Args.FindIndex(a => ReferenceEquals(a, e));
                    if (!Guarded(m.Body, call, x)) return;
                }
            }
            holders[m] = x;
        }
        if (consumer is null || argAt < 0 || producers.ContainsKey(consumer)) return;
        var p = consumer.Params[argAt];
        if (!IsText(p)) return;
        // the consumer: p.equals("CODE") and writes of p into the record
        var pReads = Reads(consumer.Body).Where(e => e is EVar ev && ev.V == p).ToList();
        foreach (var e in pReads)
        {
            bool cmp = Reads(consumer.Body).Any(r => r is ECall { Name: "equals", Recv: EVar rv, Args: [ELit { Value: string }] } && ReferenceEquals(rv, e));
            bool store = Passes.All(consumer.Body).OfType<SAssign>().Any(a => a.Lhs is ERecordField or ERecRaw && ReferenceEquals(a.Rhs, e));
            if (!cmp && !store) return;
        }
        if (Passes.All(consumer.Body).OfType<SAssign>().Any(a => a.Lhs is EVar v && v.V == p)) return;
        // every consumer call hands over a holder
        foreach (var m in ms)
            foreach (var c in Reads(m.Body).OfType<ECall>().Where(c => Callee(c) == consumer))
                if (c.Args[argAt] is not EVar av || !holders.TryGetValue(m, out var h) || h != av.V) return;
        // the codes, and their texts where the consumer maps each to one
        var codes = producers.Keys.SelectMany(m => Passes.All(m.Body).OfType<SAssign>().Where(a => a.Lhs is EVar v && v.V == producers[m]).Select(a => (string)((ELit)a.Rhs).Value!))
            .Where(s => s.Length > 0).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (codes.Count == 0 || codes.Any(c => !System.Text.RegularExpressions.Regex.IsMatch(c, "^[A-Za-z][A-Za-z0-9_]*$"))) return;
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        Expr? textTarget = null;
        foreach (var s in Passes.All(consumer.Body).OfType<SIf>())
            if (s.C is ECall { Name: "equals", Recv: EVar cv, Args: [ELit { Value: string code }] } && cv.V == p
                && s.Then is [SAssign { Lhs: ERecordField or ERecRaw, Rhs: ELit { Value: string text } } ta] && (textTarget is null || IrText.Show(textTarget) == IrText.Show(ta.Lhs)))
            { texts[code] = text; textTarget = ta.Lhs; }
        bool named = codes.All(texts.ContainsKey) && codes.Select(c => Naming.Constant(texts[c])).Distinct().Count() == codes.Count;
        var words = System.Text.RegularExpressions.Regex.Split(consumer.Name, "(?=[A-Z])").Where(w => w.Length > 0).ToList();
        var enumName = words.Count > 1 ? Naming.UpperFirst(words[^1]) + Naming.UpperFirst(p.Name) : Naming.UpperFirst(p.Name) + "Value";
        if (ms.Any(m => Naming.UpperFirst(m.Name) == enumName) || enumName == groupType) return;
        string Const(string code) => named ? Naming.Constant(texts[code]) : code.ToUpperInvariant();
        var enumType = JType.Of(enumName);
        var optional = JType.Of("java.util.Optional", enumType);
        Expr Of(string code) => code.Length == 0
            ? new ECall { Kind = CallKind.JdkStatic, Owner = "java.util.Optional", Name = "empty", Type = optional }
            : new ECall { Kind = CallKind.JdkStatic, Owner = "java.util.Optional", Name = "of", Args = [new EEnum(enumName, Const(code), false)], Type = optional };
        // producers and holders: Optional values
        foreach (var (m, c) in producers)
        {
            c.Type = optional;
            foreach (var a in Passes.All(m.Body).OfType<SAssign>().Where(a => a.Lhs is EVar v && v.V == c)) a.Rhs = Of((string)((ELit)a.Rhs).Value!);
            m.Ret = JType.Of($"Optional<{enumName}>");
        }
        foreach (var (m, x) in holders)
        {
            x.Type = optional;
            foreach (var a in Passes.All(m.Body).OfType<SAssign>().Where(a => a.Lhs is EVar v && v.V == x && a.Rhs is ELit)) a.Rhs = Of("");
            MapBody(m.Body, e => e is ECall c && Callee(c) == consumer
                ? new ECall { Kind = c.Kind, Name = c.Name, Args = c.Args.Select((a, k) => k == argAt ? Jdk(a, "orElseThrow", enumType) : a).ToList(), Type = c.Type } : null);
        }
        // the consumer: an enum parameter, its comparisons, its code() into the record, its text when every code has one
        p.Type = enumType;
        foreach (var list in Lists(consumer.Body).ToList())
            for (int i = 0; i < list.Count; i++)
                if (named && textTarget is not null && list[i] is SIf chain && MapsTexts(chain, p, texts, codes))
                    list[i] = new SAssign(textTarget, Jdk(new EVar(p), "text", JType.String)) { Line = chain.Line, Site = chain.Site };
        MapBody(consumer.Body, e => e switch
        {
            ECall { Name: "equals", Recv: EVar cv, Args: [ELit { Value: string code }] } when cv.V == p => new EBinary("==", new EVar(p), new EEnum(enumName, Const(code), false)) { Type = JType.Bool },
            _ => null,
        });
        foreach (var a in Passes.All(consumer.Body).OfType<SAssign>().Where(a => a.Rhs is EVar v && v.V == p && a.Lhs is ERecordField or ERecRaw))
            a.Rhs = Jdk(new EVar(p), "code", JType.String);
        R.Imports.Add("java.util.Optional");
        var sb = new System.Text.StringBuilder();
        sb.Append($"    /** The codes {string.Join(", ", producers.Keys.Select(m => m.Name))} return (legacy {p.Name}, SPACES for none){(named ? ", with the text the reject record carries" : "")}. */\n");
        sb.Append($"    private enum {enumName} {{\n");
        for (int i = 0; i < codes.Count; i++)
            sb.Append($"        {Const(codes[i])}(\"{codes[i]}\"{(named ? ", \"" + texts[codes[i]].Replace("\"", "\\\"") + "\"" : "")}){(i < codes.Count - 1 ? "," : ";")}\n");
        sb.Append("\n        private final String code;\n");
        if (named) sb.Append("        private final String text;\n");
        sb.Append($"\n        {enumName}(String code{(named ? ", String text" : "")}) {{\n            this.code = code;\n");
        if (named) sb.Append("            this.text = text;\n");
        sb.Append("        }\n\n        /** The code as the record carries it. */\n        String code() {\n            return code;\n        }\n");
        if (named) sb.Append("\n        /** The code's text. */\n        String text() {\n            return text;\n        }\n");
        sb.Append("    }\n");
        R.Members.Add(sb.ToString());
        R.Notes.Add($"{p.Name}: Optional<{enumName}> from {string.Join(", ", producers.Keys.Select(m => m.Name))} to {consumer.Name}");
        codeProducers = producers.Keys.ToHashSet();
        codeConsumer = consumer;
        codeType = producers.Keys.First().Ret;
    }

    /// <summary>if (p.equals(A)) target = textA; else if ... else other: one text per code, every code mapped (the else is unreachable for the enum).</summary>
    static bool MapsTexts(SIf chain, Var p, Dictionary<string, string> texts, List<string> codes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        St? s = chain;
        while (s is SIf { C: ECall { Name: "equals", Recv: EVar cv, Args: [ELit { Value: string code }] }, Then: [SAssign { Rhs: ELit { Value: string text } }] } i && cv.V == p && texts.TryGetValue(code, out var t) && t == text)
        {
            seen.Add(code);
            s = i.Else is [SIf next] ? next : null;
        }
        return seen.SetEquals(codes);
    }

    /// <summary>The call happens only where the variable holds a code: inside if (!x.isEmpty()) or the else of if (x.isEmpty()).</summary>
    static bool Guarded(List<St> body, ECall call, Var x)
    {
        foreach (var s in Passes.All(body).OfType<SIf>())
        {
            bool notEmpty = s.C is EUnary { Op: "!", E: ECall { Name: "isEmpty", Recv: EVar a } } && a.V == x;
            bool empty = s.C is ECall { Name: "isEmpty", Recv: EVar b } && b.V == x;
            var branch = notEmpty ? s.Then : empty ? s.Else : null;
            if (branch is not null && Passes.All(branch).Any(st => Exprs(st).SelectMany(Deep).Any(e => ReferenceEquals(e, call)))) return true;
        }
        return false;
    }

    // ================================================================== one validation, its order kept

    HashSet<Meth> codeProducers = [];
    Meth? codeConsumer;
    JType? codeType;

    /// <summary>
    /// A switch whose every branch first asks a check for its reject code (code = checkHeader(event)) and then does
    /// something else (sets the job function): the checks become one validate(event) with the same switch, and the
    /// branches keep their other statements in a second switch right after it. The order is the same: each branch ran
    /// its check before its other statements, the checks neither read nor write anything those statements touch (no
    /// field, no record of the program, no dataset), and the switch's subject is a parameter's record they do not change.
    /// </summary>
    void Validate()
    {
        if (codeProducers.Count == 0 || codeType is null) return;
        // the checks and what they call: only their parameters and their own locals
        var closure = new HashSet<Meth>();
        var stack = new Stack<Meth>(codeProducers);
        while (stack.Count > 0) { var m = stack.Pop(); if (closure.Add(m)) foreach (var c in CallsIn(m.Body)) stack.Push(c); }
        // (an immutable code set constant is no state)
        bool State(Expr e) => e is EProgField pf && !codeSets.Contains(pf.Name) || InstanceOf(e) is not null || e is ECall { Recv: EProgField rp } && !codeSets.Contains(rp.Name);
        if (closure.Any(m => AllExprs(m.Body).Any(State))) return;
        foreach (var c in ms.ToList())
            foreach (var list in Lists(c.Body).ToList())
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is not SSwitch sw || sw.Default is not { Count: > 0 } || !Stable(sw.Subject)) continue;
                    var branches = sw.Cases.Select(x => x.Body).Append(sw.Default).ToList();
                    Var? x0 = null;
                    bool ok = true;
                    foreach (var b in branches)
                    {
                        if (b.Count == 0 || b[0] is not SAssign { Lhs: EVar { V: var xv }, Rhs: ECall call } || Callee(call) is not { } p || !codeProducers.Contains(p)
                            || x0 is not null && x0 != xv || call.Args.Any(a => a is not EVar av || !c.Params.Contains(av.V))) { ok = false; break; }
                        x0 = xv;
                        if (b.Skip(1).Any(s => Passes.All([s]).SelectMany(StExprs).Any(e => e is EVar ev && ev.V == xv))) { ok = false; break; }
                    }
                    if (!ok || x0 is null) continue;
                    // the subject reads the method's parameters only (the record the checks get)
                    if (Deep(sw.Subject).Any(e => e is EVar ev && !c.Params.Contains(ev.V) || e is EProgField || InstanceOf(e) is not null)) continue;
                    var used = c.Params.Where(pv => Deep(sw.Subject).Concat(branches.SelectMany(b => Deep(((SAssign)b[0]).Rhs))).Any(e => e is EVar ev && ev.V == pv)).ToList();
                    var name = "validate";
                    for (int k = 2; ms.Any(m => m.Name == name); k++) name = "validate" + k;
                    var ps = used.Select(pv => NewVar(pv.Name, pv.Type!)).ToList();
                    Expr Sub(Expr e) => Rw(e, y => y is EVar ev && used.IndexOf(ev.V) is var k2 && k2 >= 0 ? new EVar(ps[k2]) : null);
                    var vsw = new SSwitch(Sub(sw.Subject)) { Line = sw.Line, Site = sw.Site };
                    foreach (var (labels, body) in sw.Cases) vsw.Cases.Add((labels.ToList(), [new SReturn(Sub(((SAssign)body[0]).Rhs)) { Line = body[0].Line }]));
                    vsw.Default = [new SReturn(Sub(((SAssign)sw.Default[0]).Rhs)) { Line = sw.Default[0].Line }];
                    var fn = new FunctionDef { Id = ap.P.Id + "/" + name, Program = ap.P, Paragraph = c.Doc, MethodName = name };
                    var l = new Lifted { Fn = fn };
                    l.Params.AddRange(ps);
                    var v = new Meth { Name = name, M = new Method { Fn = fn, L = l, Body = [vsw] }, Doc = $"The record's reject code, if any: the check of its kind ({c.Doc})", Ret = codeType };
                    ms.Insert(ms.IndexOf(c) + 1, v);
                    var rest = new SSwitch(sw.Subject) { Line = sw.Line, Site = sw.Site };
                    foreach (var (labels, body) in sw.Cases) rest.Cases.Add((labels, body.Skip(1).ToList()));
                    rest.Default = sw.Default.Skip(1).ToList();
                    var replacement = new List<St> { new SAssign(new EVar(x0), Call(name, codeType, [.. used.Select(pv => (Expr)new EVar(pv))])) { Line = sw.Line } };
                    if (rest.Cases.Any(cs => cs.Body.Count > 0) || rest.Default.Count > 0) replacement.Add(rest);
                    list.RemoveAt(i);
                    list.InsertRange(i, replacement);
                    // the code's empty first value right before is overwritten now
                    if (i > 0 && list[i - 1] is SAssign { Lhs: EVar { V: var iv }, Rhs: ECall { Name: "empty" } } && iv == x0) list.RemoveAt(i - 1);
                    R.Notes.Add($"{c.Name}: the checks behind {name}(...), the other statements of its branches after it");
                    return;
                }
    }

    /// <summary>
    /// A text code a check returns with two values, "Y" and one other ("F": fixed length outside the envelope), that its
    /// callers only compare with those: a boolean, "Y" true. Every value a literal, every read a comparison.
    /// </summary>
    void TwoValued()
    {
        foreach (var m in ms.Where(m => m.Ret?.Name == "java.lang.String").ToList())
        {
            var rets = Passes.All(m.Body).OfType<SReturn>().ToList();
            if (rets.Count == 0 || rets.Any(r => r.E is not EVar) || rets.Select(r => ((EVar)r.E!).V).Distinct().Count() != 1) continue;
            var rv = ((EVar)rets[0].E!).V;
            if (!rv.Fixed || m.Params.Contains(rv)) continue;
            var vals = Passes.All(m.Body).OfType<SAssign>().Where(a => a.Lhs is EVar v && v.V == rv).Select(a => a.Rhs).ToList();
            if (vals.Any(x => x is not ELit { Value: string })) continue;
            var codes = vals.Select(x => (string)((ELit)x).Value!).Distinct().ToList();
            if (codes.Count != 2 || !codes.Contains("Y")) continue;
            var other = codes.First(x => x != "Y");
            if (Reads(m.Body).Count(e => e is EVar ev && ev.V == rv) != rets.Count) continue;
            // the callers: a local assigned from the call (or the two codes), read only by comparisons with them
            var holders = new List<(Meth M, Var X)>();
            bool ok = true;
            foreach (var c in ms.Where(c => c != m))
            {
                var assigns = Passes.All(c.Body).OfType<SAssign>().Where(a => a.Rhs is ECall cc && Callee(cc) == m).ToList();
                if (CallsIn(c.Body).Count(x => x == m) != assigns.Count) { ok = false; break; }
                foreach (var a in assigns)
                {
                    if (a.Lhs is not EVar { V: var x } || !x.Fixed || c.Params.Contains(x)) { ok = false; break; }
                    if (Passes.All(c.Body).OfType<SAssign>().Where(s => s.Lhs is EVar sv && sv.V == x).Any(s => !(s.Rhs is ECall sc && Callee(sc) == m || s.Rhs is ELit { Value: string sl } && codes.Contains(sl)))) { ok = false; break; }
                    var reads = Reads(c.Body).Count(e => e is EVar ev && ev.V == x);
                    var tests = Reads(c.Body).Count(e => e is ECall { Name: "equals", Recv: EVar r, Args: [ELit { Value: string lv }] } && r.V == x && codes.Contains(lv));
                    if (reads != tests) { ok = false; break; }
                    holders.Add((c, x));
                }
                if (!ok) break;
            }
            if (!ok || holders.Count == 0) continue;
            Expr Bool(Expr e) => e is ELit { Value: string s } ? ELit.Bool(s == "Y") : e;
            void Convert(Meth owner, Var x)
            {
                x.Type = JType.Bool;
                foreach (var a in Passes.All(owner.Body).OfType<SAssign>().Where(a => a.Lhs is EVar v && v.V == x)) a.Rhs = Bool(a.Rhs);
                MapBody(owner.Body, e => e is ECall { Name: "equals", Recv: EVar r, Args: [ELit { Value: string lv }] } && r.V == x
                    ? (lv == "Y" ? new EVar(x) { Type = JType.Bool } : FunctionLifter.Not(new EVar(x) { Type = JType.Bool })) : null);
            }
            Convert(m, rv);
            m.Ret = JType.Bool;
            foreach (var (c, x) in holders.Distinct()) Convert(c, x);
            R.Notes.Add($"{m.Name}: a boolean ('Y' true, '{other}' false)");
        }
    }

    /// <summary>
    /// Business names where the code establishes them: the step's entry runs the step (runStep); the method that writes
    /// a reject record to a dataset for a reject code writes the reject (writeReject).
    /// </summary>
    void Names()
    {
        void Rename(Meth m, string name)
        {
            if (ms.Any(x => x.Name == name) || name == m.Name) return;
            var old = m.Name;
            foreach (var x in ms) MapBody(x.Body, e => e is ECall { Kind: CallKind.Instance, Recv: null } c && c.Name == old && Callee(c) == m
                ? new ECall { Kind = c.Kind, Name = name, Args = c.Args, Type = c.Type } : null);
            foreach (var n in R.Nested) foreach (var am in n.Methods) MapBody(am.M.Body, e => e is ECall { Kind: CallKind.Instance, Recv: null } c && c.Name == old
                ? new ECall { Kind = c.Kind, Name = name, Args = c.Args, Type = c.Type } : null);
            m.Name = name;
            R.Notes.Add($"{old} is {name}");
        }
        if (codeConsumer is not null && ms.Contains(codeConsumer) && AllExprs(codeConsumer.Body).Any(e => e is ECall { Name: "append", Recv: EProgField rp } && fileFields.Contains(rp.Name)))
        {
            var words = System.Text.RegularExpressions.Regex.Split(codeConsumer.Name, "(?=[A-Z])").Where(w => w.Length > 0).ToList();
            if (words.Count > 1 && words[^1].Equals("Reject", StringComparison.Ordinal)) Rename(codeConsumer, "writeReject");
        }
        if (Entry is { } entry && ms.All(m => m.Name != "runStep"))
        {
            Rename(entry, "runStep");
            if (entry.Name == "runStep") { R.EntryName = "runStep"; entryName = "runStep"; }
        }
    }

    // ================================================================== code sets

    static readonly string[] RoundingModes = ["UP", "DOWN", "CEILING", "FLOOR", "HALF_UP", "HALF_DOWN", "HALF_EVEN", "UNNECESSARY"];

    /// <summary>
    /// A condition that compares one text field with three or more codes (x = A or x = B or x = C; x not = A and ...): a
    /// membership test of a named set. The names of java.math.RoundingMode: an EnumSet of exactly those modes, matched by
    /// exact name (no case folding, no trimming beyond the field's; UNNECESSARY only when the program lists it). Other
    /// codes: an immutable Set of the texts. The terms are stable, so their order and short-circuit do not matter.
    /// </summary>
    void CodeChains(Meth m)
    {
        MapBody(m.Body, e => Chain(e));
    }

    Expr? Chain(Expr e)
    {
        if (e is not EBinary { Op: "&&" or "||" } top) return null;
        var op = top.Op;
        var terms = new List<Expr>();
        void Flat(Expr x) { if (x is EBinary b && b.Op == op) { Flat(b.L); Flat(b.R); } else terms.Add(x); }
        Flat(top);
        // field.equals("CODE"), negated under &&
        (Expr? Field, string? Code) Term(Expr t)
        {
            var inner = op == "&&" ? (t is EUnary { Op: "!", E: var n } ? n : null) : t;
            return inner is ECall { Kind: CallKind.Jdk, Name: "equals", Recv: ERecordField { Padded: false, Index: null } f, Args: [ELit { Value: string c }] } && c == c.TrimEnd() && c.Length > 0
                ? (f, c) : (null, null);
        }
        var groups = terms.Select((t, i) => (I: i, T: Term(t))).Where(x => x.T.Field is not null).GroupBy(x => IrText.Show(x.T.Field!)).Where(g => g.Count() >= 3).ToList();
        if (groups.Count == 0) return terms.Count == 2 ? null : null;
        var result = new List<Expr>();
        var replaced = new Dictionary<int, Expr>();
        var drop = new HashSet<int>();
        foreach (var g in groups)
        {
            var field = (ERecordField)g.First().T.Field!;
            var codes = g.Select(x => x.T.Code!).Distinct().ToList();
            if (A.RecordOf(ap, field.Item) is not { } r || !r.Rc.Accessors.TryGetValue(field.Item.Name, out var acc)) return null;
            Expr test = Membership(acc, field.Item.Name, codes, field);
            replaced[g.First().I] = op == "&&" ? FunctionLifter.Not(test) : test;
            foreach (var x in g.Skip(1)) drop.Add(x.I);
        }
        for (int i = 0; i < terms.Count; i++)
        {
            if (drop.Contains(i)) continue;
            var t = replaced.TryGetValue(i, out var nt) ? nt : Rw(terms[i], Chain);
            result.Add(t);
        }
        return result.Skip(1).Aggregate(result[0], (a, b) => op == "&&" ? FunctionLifter.And(a, b) : FunctionLifter.Or(a, b));
    }

    readonly HashSet<string> codeSets = new(StringComparer.Ordinal);

    Expr Membership(string acc, string legacy, List<string> codes, Expr field)
    {
        var words = legacy.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0].Length <= 3) words.RemoveAt(0);
        var constant = Naming.Constant(string.Join('-', words));
        if (codes.All(c => RoundingModes.Contains(c)))
        {
            var set = constant.EndsWith("_MODE", StringComparison.Ordinal) ? constant + "S" : constant + "_MODES";
            var test = "is" + Naming.UpperFirst(acc);
            if (codeSets.Add(set))
            {
                R.Imports.UnionWith(["java.math.RoundingMode", "java.util.EnumSet", "java.util.Set"]);
                R.Members.Add($"    /** The rounding modes {legacy} may name: java.math's, as the legacy check lists them. */\n"
                    + $"    private static final Set<RoundingMode> {set} = EnumSet.of({string.Join(", ", codes.Select(c => "RoundingMode." + c))});\n\n"
                    + $"    /** Whether the text is the exact name of one of {set} (case and spelling as written, as the legacy comparison). */\n"
                    + $"    private static boolean {test}(String text) {{\n"
                    + $"        return {set}.stream().anyMatch(mode -> mode.name().equals(text));\n"
                    + "    }\n");
            }
            return new ECall { Kind = CallKind.Instance, Name = test, Args = [field], Type = JType.Bool };
        }
        var name = constant + "_CODES";
        if (codeSets.Add(name))
        {
            R.Imports.Add("java.util.Set");
            R.Members.Add($"    /** The codes {legacy} may hold. */\n"
                + $"    private static final Set<String> {name} = Set.of({string.Join(", ", codes.Select(c => "\"" + c + "\""))});\n");
        }
        var setType = JType.Of("java.util.Set");
        return Jdk(new EProgField(name, setType) { Type = setType }, "contains", JType.Bool, field);
    }

    // ================================================================== cleanup

    void Cleanup(Meth m)
    {
        LeapYears(m);
        if (YesNoLocals(m)) FoldFlags(m.Body);
        for (int round = 0; round < 8; round++)
        {
            bool changed = DeadStores(m) | Substitute(m);
            if (!changed) break;
        }
        Passes.Counting(m.Body);
        m.Body = Passes.Tidy(m.Body);
        if (m.Ret is null) while (m.Body.Count > 0 && m.Body[^1] is SReturn { E: null }) m.Body.RemoveAt(m.Body.Count - 1);
    }

    /// <summary>Local liveness: the variables read after each statement.</summary>
    Dictionary<St, HashSet<Var>> LocalLiveness(Meth m)
    {
        var after = new Dictionary<St, HashSet<Var>>(ReferenceEqualityComparer.Instance);
        HashSet<Var> All() => Passes.All(m.Body).SelectMany(s => StExprs(s)).OfType<EVar>().Select(v => v.V).ToHashSet();
        HashSet<Var> Uses(Expr e) => Deep(e).OfType<EVar>().Select(v => v.V).ToHashSet();
        HashSet<Var> Block(List<St> list, HashSet<Var> live, HashSet<Var> handler)
        {
            for (int i = list.Count - 1; i >= 0; i--) live = Stmt(list[i], live, handler);
            return live;
        }
        HashSet<Var> Stmt(St s, HashSet<Var> L, HashSet<Var> handler)
        {
            after[s] = new HashSet<Var>(L);
            HashSet<Var> r;
            switch (s)
            {
                case SAssign a:
                    r = new HashSet<Var>(L);
                    if (a.Lhs is EVar lv) r.Remove(lv.V);
                    else r.UnionWith(Uses(a.Lhs));
                    r.UnionWith(Uses(a.Rhs));
                    break;
                case SReturn or SThrow: r = Exprs(s).SelectMany(Uses).ToHashSet(); break;
                case SIf i: r = Block(i.Then, new(L), handler); r.UnionWith(Block(i.Else, new(L), handler)); r.UnionWith(Uses(i.C)); break;
                case SSwitch w:
                    r = w.Default is null ? new(L) : Block(w.Default, new(L), handler);
                    foreach (var c in w.Cases) r.UnionWith(Block(c.Body, new(L), handler));
                    r.UnionWith(Uses(w.Subject));
                    break;
                case SLoop l:
                {
                    var x = new HashSet<Var>(L);
                    for (int k = 0; k < 50; k++)
                    {
                        var head = new HashSet<Var>(x);
                        foreach (var e in Exprs(l)) head.UnionWith(Uses(e));
                        var bodyOut = l.Update is null ? head : Stmt(l.Update, head, handler);
                        var b = Block(l.Body, new(bodyOut), handler);
                        var next = new HashSet<Var>(L);
                        next.UnionWith(head);
                        next.UnionWith(b);
                        if (next.SetEquals(x)) break;
                        x = next;
                    }
                    r = l.Init is null ? x : Stmt(l.Init, x, handler);
                    break;
                }
                case STry t:
                {
                    var c = new HashSet<Var>();
                    foreach (var cc in t.Catches) c.UnionWith(Block(cc.Body, new(L), handler));
                    r = Block(t.Body, new(L), new HashSet<Var>(handler.Concat(c)));
                    r.UnionWith(c);
                    break;
                }
                case SBlock or SBreak or SContinue: r = All(); r.UnionWith(L); break;
                default: r = new(L); foreach (var e in Exprs(s)) r.UnionWith(Uses(e)); break;
            }
            r.UnionWith(handler);
            return r;
        }
        Block(m.Body, [], []);
        return after;
    }

    bool IsLocal(Meth m, Var v) => !m.Params.Contains(v) && v != loopVar && !Passes.All(m.Body).Any(s => s is SLoop { Each: var ev } && ev == v || s is STry t && t.Catches.Any(c => c.V == v));

    /// <summary>An assignment to a local nobody reads: gone when its value is stable, the call kept otherwise.</summary>
    bool DeadStores(Meth m)
    {
        var after = LocalLiveness(m);
        bool changed = false;
        foreach (var list in Lists(m.Body).ToList())
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] is not SAssign { Lhs: EVar { V: var v } } a || !IsLocal(m, v) || after[list[i]].Contains(v)) continue;
                // a literal first value a later branch replaces keeps the declaration readable (x = ""; switch ... x = ...)
                if (a.Rhs is ELit && !list.Skip(i + 1).Any(s => s is SAssign { Lhs: EVar lv } && lv.V == v)
                    && !Passes.All(m.Body).TakeWhile(s => s != list[i]).Any(s => StExprs(s).Any(e => e is EVar ev && ev.V == v))
                    && Passes.All(m.Body).SkipWhile(s => s != list[i]).Skip(1).Any(s => StExprs(s).Any(e => e is EVar ev2 && ev2.V == v))) continue;
                if (Stable(a.Rhs)) { list.RemoveAt(i); changed = true; }
                else if (a.Rhs is ECall c) { list[i] = new SExpr(c) { Line = a.Line, Site = a.Site }; changed = true; }
            }
        return changed;
    }

    /// <summary>x = e; next(x) → next(e), x read nowhere else: e stable, or next reads x before anything else happens.</summary>
    bool Substitute(Meth m)
    {
        var after = LocalLiveness(m);
        foreach (var list in Lists(m.Body).ToList())
            for (int i = 0; i + 1 < list.Count; i++)
            {
                if (list[i] is not SAssign { Lhs: EVar { V: var v } } a || !IsLocal(m, v)) continue;
                var next = list[i + 1];
                if (after[next].Contains(v)) continue;
                var head = Exprs(next).Where(e => !(next is SAssign na && ReferenceEquals(e, na.Lhs))).ToList();
                if (next is SAssign { Lhs: EVar lv } && lv.V == v) continue;
                int uses = head.SelectMany(Deep).Count(e => e is EVar ev && ev.V == v);
                int all = Passes.All([next]).SelectMany(s => StExprs(s)).Count(e => e is EVar ev && ev.V == v);
                if (uses != 1 || all != 1) continue;
                if (next is SLoop) continue;
                bool first = next switch
                {
                    SIf { C: EVar c } => c.V == v,
                    SIf { C: EUnary { Op: "!", E: EVar c2 } } => c2.V == v,
                    SAssign { Rhs: EVar r, Lhs: EVar or EProgField } => r.V == v,
                    SReturn { E: EVar r2 } => r2.V == v,
                    _ => false,
                };
                bool calm = Stable(a.Rhs) && !head.SelectMany(Deep).Any(e => e is ECall c && !Stable(c) && !(c.Name == "line" && c.Recv is EProgField p && p.Name == R.ReportField))
                    && !(next is SAssign { Lhs: EVar w } && Deep(a.Rhs).Any(e => e is EVar ev && ev.V == w.V));
                bool constant = a.Rhs is ELit;
                if (!first && !calm && !constant) continue;
                var value = a.Rhs;
                MapSt(next, e => Rw(e, x => x is EVar ev && ev.V == v ? value : null));
                list.RemoveAt(i);
                return true;
            }
        return false;
    }

    // ================================================================== what the class keeps

    void Finish()
    {
        if (groupSv is not null && groupType is not null) R.Members.Insert(0, GroupText(groupSv));
        var refs = ms.SelectMany(m => AllExprs(m.Body)).ToList();
        var progFields = refs.OfType<EProgField>().Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var pf in fields.Values) if (progFields.Contains(pf.Name)) R.Fields.Add(pf);
        var usedInst = refs.Select(InstanceOf).Where(i => i is not null).Select(i => i!).ToHashSet(StringComparer.Ordinal);
        foreach (var inst in ap.Instances.Values) if (!usedInst.Contains(inst)) R.DroppedInstances.Add(inst);
        if (!progFields.Contains(R.ReportField!)) R.Notes.Add("no report lines");
    }
}
