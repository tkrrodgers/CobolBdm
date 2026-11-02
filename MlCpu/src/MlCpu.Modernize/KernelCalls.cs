using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

/// <summary>
/// Calls of a kernel the JDK replaces (Q1): where the function code the caller moved into the interface record is proven
/// (a literal reaching the CALL on every path, its paragraphs and the CALL's helper paragraph followed), the CALL becomes
/// the kernel's typed method, and its return-code check the handler of the exception the typed method throws. The record
/// keeps the operands and the result the caller reads; the code moves that only selected the function go. A CALL whose
/// code is not proven keeps the record interface and is residue: a code chosen at run time must not reach modern code.
/// </summary>
sealed class KernelCalls
{
    readonly AppModel A;
    readonly AppProgram ap;
    readonly List<AppMethod> methods;
    readonly List<string> residue;
    /// <summary>Kernel instance fields of the program → the kernel's program.</summary>
    readonly Dictionary<string, AppProgram> kernels = new(StringComparer.Ordinal);
    int temps;

    KernelCalls(AppModel a, AppProgram p, List<AppMethod> m, List<string> r)
    {
        A = a; ap = p; methods = m; residue = r;
        foreach (var (id, field) in p.Callees)
            // a decimal library keeps its record interface at the call (its JDK class takes the record)
            if (a.ById.TryGetValue(id, out var k) && k.Kernel is not null && k.Kernel.Kind != DecimalKernels.Kind) kernels[field] = k;
    }

    public static void Run(AppModel a, AppProgram p, List<AppMethod> methods, List<string> residue)
    {
        var kc = new KernelCalls(a, p, methods, residue);
        if (kc.kernels.Count == 0) return;
        kc.InlineHelpers();
        kc.Rewrite();
        kc.DropUnusedKernels();
    }

    /// <summary>A kernel every CALL of which became a typed method is no longer an instance the program is given.</summary>
    void DropUnusedKernels()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in methods)
            foreach (var (list, _) in Lists(m.M.Body))
                foreach (var s in list)
                    foreach (var e in Exprs(s).SelectMany(Deep))
                        if (e is EProgField f) used.Add(f.Name);
        foreach (var field in kernels.Keys)
            if (!used.Contains(field))
                foreach (var key in ap.Callees.Where(c => c.Value == field).Select(c => c.Key).ToList())
                    ap.Callees.Remove(key);
    }

    // ------------------------------------------------------------------ IR helpers

    /// <summary>A CALL of a kernel: its kernel program and the record argument.</summary>
    (AppProgram Kernel, Expr Record)? KernelCall(St s) =>
        s is SExpr { E: ECall { Kind: CallKind.Jdk, Name: "call", Recv: EProgField f, Args: [var r] } } && kernels.TryGetValue(f.Name, out var k) ? (k, r) : null;

    static IEnumerable<Expr> Deep(Expr e)
    {
        yield return e;
        foreach (var c in e.Children)
            foreach (var d in Deep(c)) yield return d;
    }

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

    static Expr CloneE(Expr e) => e.Map(CloneE);

    static List<St> Clone(List<St> list) => list.Select(Clone).ToList();

    static St Clone(St s)
    {
        St c = s switch
        {
            SAssign a => new SAssign(CloneE(a.Lhs), CloneE(a.Rhs)),
            SExpr x => new SExpr(CloneE(x.E)),
            SReturn r => new SReturn(r.E is null ? null : CloneE(r.E)),
            SThrow t => new SThrow(CloneE(t.E)),
            SIf i => new SIf(CloneE(i.C), Clone(i.Then), Clone(i.Else)),
            SLoop l => new SLoop(l.Label, l.C is null ? null : CloneE(l.C), Clone(l.Body)) { Each = l.Each, Iterable = l.Iterable is null ? null : CloneE(l.Iterable) },
            SSwitch w => CloneSwitch(w),
            SBreak b => new SBreak(b.Label),
            SContinue k => new SContinue(k.Label),
            SComment m => new SComment(m.Text),
            _ => throw new NotSupportedException(s.GetType().Name),
        };
        c.Line = s.Line;
        return c;
    }

    static SSwitch CloneSwitch(SSwitch w)
    {
        var c = new SSwitch(CloneE(w.Subject)) { Default = w.Default is null ? null : Clone(w.Default) };
        foreach (var (labels, body) in w.Cases) c.Cases.Add((labels.Select(CloneE).ToList(), Clone(body)));
        return c;
    }

    /// <summary>Every statement list of a body (the body itself, branches, loop bodies, cases), with the statement that owns it.</summary>
    static IEnumerable<(List<St> List, St? Owner)> Lists(List<St> body, St? owner = null)
    {
        yield return (body, owner);
        foreach (var s in body)
            foreach (var l in Children(s))
                foreach (var x in Lists(l, s)) yield return x;
    }

    static IEnumerable<List<St>> Children(St s) => s switch
    {
        SIf i => [i.Then, i.Else],
        SLoop l => [l.Body],
        SBlock b => [b.Body],
        SSwitch w => w.Cases.Select(c => c.Body).Concat(w.Default is null ? [] : [w.Default]),
        STry t => new[] { t.Body }.Concat(t.Catches.Select(c => c.Body)),
        _ => [],
    };

    Meth? ByName(string name) => methods.Select(m => new Meth(m)).FirstOrDefault(m => m.Name == name);

    sealed record Meth(AppMethod M)
    {
        public string Name => M.Head.Split(' ')[^1];
        public List<St> Body => M.M.Body;
    }

    static string? Called(St s) => s is SExpr { E: ECall { Kind: CallKind.Instance, Recv: null, Name: var n } } ? n : null;

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// A paragraph that only CALLs a kernel and checks its return code (8500-CALL-DATE): inlined where it is PERFORMed,
    /// so that each CALL sees the function code its caller moved; dropped when nothing calls it any more.
    /// </summary>
    void InlineHelpers()
    {
        foreach (var helper in methods.Select(m => new Meth(m)).ToList())
        {
            var body = helper.Body;
            if (body.Count is < 1 or > 2 || helper.M.M.L.Params.Count > 0 || KernelCall(body[0]) is not { } kc) continue;
            if (body.Count == 2 && RcCheck(body[1], kc.Record, kc.Kernel) is null) continue;
            bool any = false;
            foreach (var m in methods.Select(x => new Meth(x)).Where(x => x.Name != helper.Name))
                foreach (var (list, _) in Lists(m.Body).ToList())
                    for (int i = 0; i < list.Count; i++)
                        if (Called(list[i]) == helper.Name)
                        {
                            var copy = Clone(body);
                            foreach (var c in copy) if (c.Line == 0) c.Line = list[i].Line;
                            list.RemoveAt(i);
                            list.InsertRange(i, copy);
                            i += copy.Count - 1;
                            any = true;
                        }
            if (any && !methods.Where(x => x != helper.M).Any(x => Passes.All(x.M.Body).Any(s => Called(s) == helper.Name)))
                methods.Remove(helper.M);
        }
    }

    /// <summary>The return-code check after a CALL: its success branch, and its failure branches by return code (0 = any).</summary>
    sealed record Check(List<St> Success, List<(List<int> Codes, List<St> Body)> Failures, List<St>? OtherFailure);

    Check? RcCheck(St s, Expr record, AppProgram kernel)
    {
        var rc = CallerItem(record, kernel, "rc");
        if (rc is null) return null;
        bool IsRc(Expr e) => e is ERecordField f && f.Item == rc && SameRecord(f.Via, record);
        switch (s)
        {
            case SIf { C: EBinary { Op: "!=", L: var l, R: ELit { Value: 0L } } } i when IsRc(l):
                return new Check(i.Else, [], i.Then);
            case SIf { C: EBinary { Op: "==", L: var l, R: ELit { Value: 0L } } } i when IsRc(l):
                return new Check(i.Then, [], i.Else);
            // IF RC NOT = 0 AND RC NOT = k: the code k (a warning) goes on as success does
            case SIf { C: EBinary { Op: "&&", L: EBinary { Op: "!=", L: var l1, R: ELit { Value: 0L } }, R: EBinary { Op: "!=", L: var l2, R: ELit { Value: long tolerated } } } } i
                when IsRc(l1) && IsRc(l2) && tolerated != 0:
                return new Check(i.Else, [([(int)tolerated], i.Else)], i.Then);
            case SSwitch w when IsRc(w.Subject):
            {
                List<St>? success = null;
                var failures = new List<(List<int>, List<St>)>();
                foreach (var (labels, body) in w.Cases)
                {
                    if (labels.Any(x => x is not ELit { Value: long })) return null;
                    var codes = labels.Select(x => (int)(long)((ELit)x).Value!).ToList();
                    if (codes.Contains(0)) { if (codes.Count > 1) return null; success = body; }
                    else failures.Add((codes, body));
                }
                return new Check(success ?? [], failures, w.Default ?? []);
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ the caller's interface record

    /// <summary>The caller's record an argument names: its instance field's, or its LINKAGE parameter's.</summary>
    DataItem? RecordOf(Expr r) => r switch
    {
        EProgField f => ap.Instances.FirstOrDefault(kv => kv.Value == f.Name && !ap.LinkageVars.ContainsKey(kv.Key)).Key,
        EVar v => ap.LinkageVars.FirstOrDefault(kv => kv.Value == v.V).Key,
        _ => null,
    };

    /// <summary>The caller's item that plays a role of the kernel's record (the same layout: by position).</summary>
    DataItem? CallerItem(Expr record, AppProgram kernel, string role)
    {
        if (RecordOf(record) is not { } rec) return null;
        var k = kernel.Kernel!;
        var kernelLeaves = AppModel.Leaves(k.Parms).Where(l => !l.IsFiller).ToList();
        var leaves = AppModel.Leaves(rec).Where(l => !l.IsFiller).ToList();
        int at = kernelLeaves.IndexOf(k.Roles[role]);
        return leaves.Count == kernelLeaves.Count && at >= 0 ? leaves[at] : null;
    }

    static bool SameRecord(Expr? via, Expr record) => via switch
    {
        null => record is EProgField,
        EVar v => record is EVar r && r.V == v.V,
        _ => false,
    };

    ERecordField Field(Expr record, DataItem item) =>
        new(item) { Type = AppModel.FieldType(item), Via = record is EVar ? record : null };

    // ------------------------------------------------------------------ the proof of a code

    /// <summary>Whether a statement may change the item (an assignment, the record's bytes, a paragraph that does, an INITIALIZE).</summary>
    bool Writes(St s, DataItem item, Expr record, HashSet<string> writers)
    {
        foreach (var x in Passes.All([s]))
        {
            if (x is SAssign { Lhs: ERecordField f } && f.Item == item && SameRecord(f.Via, record)) return true;
            if (x is SAssign { Lhs: ERecRaw raw } && raw.Item?.Record == item.Record) return true;
            if (Called(x) is { } c && writers.Contains(c)) return true;
            foreach (var e in Exprs(x).SelectMany(Deep))
            {
                if (e is ECall { Kind: CallKind.Jdk, Recv: var rv, Name: var n } && n.StartsWith("initialize", StringComparison.Ordinal) && rv is not null && RecordOf(rv) == item.Record) return true;
                // the record passed to another program, which may change it
                if (e is ECall { Kind: CallKind.Jdk, Name: "call", Recv: EProgField cf } oc && !kernels.ContainsKey(cf.Name) && oc.Args.Any(a => RecordOf(a) == item.Record)) return true;
            }
        }
        return false;
    }

    /// <summary>The paragraph methods that may change the item, directly or through the paragraphs they call.</summary>
    HashSet<string> Writers(DataItem item, Expr record)
    {
        var w = new HashSet<string>(StringComparer.Ordinal);
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var m in methods.Select(x => new Meth(x)))
                if (!w.Contains(m.Name) && m.Body.Any(s => Writes(s, item, record, w))) { w.Add(m.Name); changed = true; }
        }
        return w;
    }

    /// <summary>
    /// The literal the item holds at a statement on every path from its method's start: the last assignment before it in
    /// its list, then before the statement that owns the list, and so on (a loop that may change it between passes, a
    /// statement or a paragraph that may change it, or the method's start make it unknown).
    /// </summary>
    static string? Proven(List<(List<St> List, int Index, St? Owner)> path, DataItem item, Expr record, Func<St, bool> writes)
    {
        for (int level = path.Count - 1; level >= 0; level--)
        {
            var (list, index, _) = path[level];
            for (int j = index - 1; j >= 0; j--)
            {
                var s = list[j];
                if (s is SAssign { Lhs: ERecordField f, Rhs: var rhs } && f.Item == item && SameRecord(f.Via, record))
                    return rhs is ELit { Value: string v } ? v.TrimEnd() : null;
                if (writes(s)) return null;
            }
            // the statement owning this list: a loop may have changed the item on an earlier pass
            if (level > 0 && path[level - 1].List[path[level - 1].Index] is SLoop loop && writes(loop)) return null;
        }
        return null;
    }

    // ------------------------------------------------------------------ the rewrite

    sealed record Site(List<(List<St> List, int Index, St? Owner)> Path, AppProgram Kernel, Expr Record, Meth Method);

    void Rewrite()
    {
        var sites = new List<Site>();
        foreach (var m in methods.Select(x => new Meth(x)))
            Collect(m, m.Body, [], sites);
        // the return-code checks after the CALLs: reading the code there is what the typed calls replace
        foreach (var s in sites)
        {
            var (list, index, _) = s.Path[^1];
            if (index + 1 < list.Count && RcCheck(list[index + 1], s.Record, s.Kernel) is not null)
                foreach (var x in Passes.All([list[index + 1]])) checks.Add(x);
        }
        int done = 0;
        // innermost and last first: a rewrite replaces statements of its own list only
        foreach (var site in sites.OrderByDescending(s => s.Path.Count).ThenByDescending(s => s.Path[^1].Index))
        {
            if (RewriteSite(site)) done++;
            else residue.Add($"{ap.P.Id}/{site.Method.Name} CALL '{site.Kernel.P.Id}' with a function code not proven: the record interface reaches modern code");
        }
        if (done > 0) DropCodeMoves();
    }

    void Collect(Meth m, List<St> list, List<(List<St>, int, St?)> path, List<Site> sites)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var here = new List<(List<St>, int, St?)>(path) { (list, i, null) };
            if (KernelCall(list[i]) is { } kc) sites.Add(new Site(here, kc.Kernel, kc.Record, m));
            foreach (var child in Children(list[i])) Collect(m, child, here, sites);
        }
    }

    bool RewriteSite(Site site)
    {
        var (list, index, _) = site.Path[^1];
        var record = site.Record;
        var kernel = site.Kernel;
        var k = kernel.Kernel!;
        var fnItem = CallerItem(record, kernel, "function");
        if (fnItem is null || index + 1 > list.Count) return false;
        var writers = Writers(fnItem, record);
        var fn = Proven(site.Path, fnItem, record, s => Writes(s, fnItem, record, writers));
        if (fn is null) return false;
        var check = index + 1 < list.Count ? RcCheck(list[index + 1], record, kernel) : null;
        if (check is null) return false;
        // the return code and (rounding) the inexact flag are read only by the check: the typed call need not set them
        var rcItem = CallerItem(record, kernel, "rc")!;
        if (ReadsElsewhere(rcItem, record, list[index + 1])) return false;
        if (k.Kind == "rounding" && CallerItem(record, kernel, "sticky") is { } st && ReadsAfterCall(st, record)) return false;
        var call = k.Kind == "dates" ? DateCall(fn, site, record, kernel) : RoundingCall(fn, site, record, kernel);
        if (call is null) return false;
        var (body, exceptions) = call.Value;
        var tr = new STry([.. body, .. check.Success]) { Line = list[index].Line };
        foreach (var (type, rcOf) in exceptions)
        {
            var e = new Var { Name = "e", Type = type, Fixed = true };
            var handler = Failure(check, record, rcItem, rcOf(new EVar(e) { Type = type }));
            tr.Catches.Add((type, e, handler));
        }
        list.RemoveAt(index + 1);
        list[index] = tr;
        return true;
    }

    /// <summary>The failure branches as one handler, the record's return code read as the exception's.</summary>
    List<St> Failure(Check check, Expr record, DataItem rcItem, Expr returnCode)
    {
        Expr Sub(Expr e) => e is ERecordField f && f.Item == rcItem && SameRecord(f.Via, record) ? returnCode : e.Map(Sub);
        List<St> Fix(List<St> body)
        {
            var c = Clone(body);
            foreach (var s in Passes.All(c))
                switch (s)
                {
                    case SAssign a: a.Rhs = Sub(a.Rhs); if (a.Lhs is not ERecordField) a.Lhs = Sub(a.Lhs); break;
                    case SExpr x: x.E = Sub(x.E); break;
                    case SIf i: i.C = Sub(i.C); break;
                    case SSwitch w: w.Subject = Sub(w.Subject); break;
                    case SThrow t: t.E = Sub(t.E); break;
                }
            return c;
        }
        if (check.Failures.Count == 0) return Fix(check.OtherFailure ?? []);
        var sw = new SSwitch(returnCode);
        foreach (var (codes, body) in check.Failures) sw.Cases.Add((codes.Select(c => (Expr)ELit.Int(c)).ToList(), Fix(body)));
        sw.Default = Fix(check.OtherFailure ?? []);
        return [sw];
    }

    readonly HashSet<St> checks = new(ReferenceEqualityComparer.Instance);

    bool ReadsElsewhere(DataItem item, Expr record, St check)
    {
        foreach (var m in methods)
            foreach (var s in Passes.All(m.M.Body))
            {
                if (s == check || checks.Contains(s)) continue;
                var reads = s is SAssign a ? Deep(a.Rhs).Concat(a.Lhs.Children.SelectMany(Deep)) : Exprs(s).SelectMany(Deep);
                if (reads.Any(e => e is ERecordField f && f.Item == item && SameRecord(f.Via, record))) return true;
            }
        return false;
    }

    /// <summary>Whether the program reads the item anywhere (an input the caller sets before each CALL is never read back).</summary>
    bool ReadsAfterCall(DataItem item, Expr record) =>
        methods.SelectMany(m => Passes.All(m.M.Body)).Any(s => (s is SAssign a ? Deep(a.Rhs).Concat(a.Lhs.Children.SelectMany(Deep)) : Exprs(s).SelectMany(Deep))
            .Any(e => e is ERecordField f && f.Item == item && SameRecord(f.Via, record)));

    ECall Static(string cls, string name, JType type, params Expr[] args) => new() { Kind = CallKind.JdkStatic, Owner = A.AppPackage + "." + cls, Name = name, Args = [.. args], Type = type };
    static ECall Jdk(Expr recv, string name, JType type, params Expr[] args) => new() { Kind = CallKind.Jdk, Name = name, Recv = recv, Args = [.. args], Type = type };

    static readonly Dictionary<string, string> Units = new() { ["D"] = "DAYS", ["W"] = "WEEKS", ["M"] = "MONTHS", ["Y"] = "YEARS" };

    (List<St>, List<(JType, Func<Expr, Expr>)>)? DateCall(string fn, Site site, Expr record, AppProgram kernel)
    {
        Expr? Read(string role) => CallerItem(record, kernel, role) is { } it ? Field(record, it) : null;
        St? Set(string role, Expr value) => CallerItem(record, kernel, role) is { } it ? new SAssign(Field(record, it), value) : null;
        Expr Date(Expr day) => Static("DateKernel", "date", JType.LocalDate, day);
        Expr? unit = null;
        if (fn is "PL" or "BT")
        {
            var unitItem = CallerItem(record, kernel, "unit")!;
            var writers = Writers(unitItem, record);
            var u = Proven(site.Path, unitItem, record, s => Writes(s, unitItem, record, writers));
            // a unit chosen at run time is a ChronoUnit decoded at the record boundary
            unit = u is not null && Units.TryGetValue(u, out var cu) ? new EStaticJdk("java.time.temporal.ChronoUnit", cu, JType.ChronoUnit) { Type = JType.ChronoUnit }
                : Static("DateKernel", "unit", JType.ChronoUnit, record);
        }
        var day = Read("day")!;
        var n = Read("n")!;
        var optional = JType.Of("java.util.OptionalInt");
        St? set = fn switch
        {
            "DN" => Set("result", Jdk(Static("DateKernel", "dayNumber", optional, Read("date")!), "orElse", JType.Int, ELit.Int(0))),
            "YM" => Set("date", Date(day)),
            "PL" => Set("result", Static("DateKernel", "plus", JType.Int, day, n, unit!)),
            "BT" => Set("result", Static("DateKernel", "units", JType.Int, unit!, day, Read("day2")!)),
            "LM" => Set("result", Jdk(Date(day), "lengthOfMonth", JType.Int)),
            "LY" => Set("result", Jdk(Date(day), "lengthOfYear", JType.Int)),
            "YR" => Set("result", Jdk(Date(day), "getYear", JType.Int)),
            "DM" => Set("result", Jdk(Date(day), "getDayOfMonth", JType.Int)),
            "WD" => Set("result", Jdk(Static("DateKernel", "withDayOfMonth", optional, day, Static("DateKernel", "dayOfMonthOperand", JType.Int, n)), "orElse", JType.Int, ELit.Int(0))),
            "YE" => Set("result", Static("DateKernel", "yearEnd", JType.Int, n)),
            _ => null,
        };
        if (set is null) return null;
        var dte = JType.Of("java.time.DateTimeException");
        var iae = JType.Of("java.lang.IllegalArgumentException");
        var exceptions = new List<(JType, Func<Expr, Expr>)> { (dte, _ => ELit.Int(20)) };
        // only a unit decoded at run time can be one the kernel does not know
        if (unit is ECall) exceptions.Add((iae, _ => ELit.Int(12)));
        return ([set], exceptions);
    }

    (List<St>, List<(JType, Func<Expr, Expr>)>)? RoundingCall(string fn, Site site, Expr record, AppProgram kernel)
    {
        Expr Read(string role) => Field(record, CallerItem(record, kernel, role)!);
        St Set(string role, Expr value) => new SAssign(Field(record, CallerItem(record, kernel, role)!), value);
        var big = JType.Of("java.math.BigInteger");
        Expr value = Jdk(Read("value"), "toBigInteger", big);
        Expr mode = Static("RoundingKernel", "mode", JType.RoundingMode, record);
        Expr inexact = Static("RoundingKernel", "inexact", JType.Bool, record);
        Expr Dec(Expr x) => new ENew { JdkClass = "java.math.BigDecimal", Args = [x], Type = JType.BigDecimal };
        var body = new List<St>();
        switch (fn)
        {
            case "S":
                body.Add(Set("value", Dec(Static("RoundingKernel", "roundSignificant", big, value, Read("parm"), mode, inexact))));
                body.Add(Set("shift", ELit.Int(0)));
                break;
            case "D":
                body.Add(Set("value", Dec(Static("RoundingKernel", "roundDecimals", big, value, Read("parm"), mode, inexact))));
                body.Add(Set("shift", ELit.Int(0)));
                break;
            case "P":
            {
                var shortenedType = JType.Of(A.AppPackage + ".RoundingKernel.Shortened");
                var v = new Var { Name = "shortened" + (temps++ == 0 ? "" : temps.ToString(System.Globalization.CultureInfo.InvariantCulture)), Type = shortenedType, Fixed = true };
                site.Method.M.M.L.Vars.Add(v);
                body.Add(new SAssign(new EVar(v) { Type = shortenedType }, Static("RoundingKernel", "shorten", shortenedType, value, Read("parm"), mode, inexact)));
                body.Add(Set("value", Dec(Jdk(new EVar(v) { Type = shortenedType }, "digits", big))));
                body.Add(Set("shift", Jdk(new EVar(v) { Type = shortenedType }, "shift", JType.Int)));
                break;
            }
            default: return null;
        }
        var failure = JType.Of(A.AppPackage + ".RoundingKernel.Failure");
        return (body, [(failure, e => Jdk(e, "returnCode", JType.Int))]);
    }

    /// <summary>
    /// The moves of a function code that only selected the kernel's function (no CALL reads the record's code any more,
    /// nothing else reads it): dropped, with a proven unit's.
    /// </summary>
    void DropCodeMoves()
    {
        var bodies = methods.Select(m => m.M.Body).ToList();
        if (bodies.SelectMany(Passes.All).Any(s => KernelCall(s) is not null)) return;
        foreach (var (field, kernel) in kernels)
            foreach (var (rec, inst) in ap.Instances.Where(kv => !ap.LinkageVars.ContainsKey(kv.Key) && kv.Key == kv.Key.Record))
            {
                var record = new EProgField(inst, JType.Of(A.RecordPackage + "." + ap.Records[rec].Name));
                if (bodies.SelectMany(Passes.All).SelectMany(Exprs).SelectMany(Deep).Any(e => e is ECall { Kind: CallKind.Jdk, Name: "call" } c && c.Args.Any(a => a is EProgField f && f.Name == inst))) continue;
                // a typed method decoding the record at its boundary (DateKernel.unit(parms)) reads its codes but the function
                bool decoded = bodies.SelectMany(Passes.All).SelectMany(Exprs).SelectMany(Deep).Any(e => e is ECall { Kind: CallKind.JdkStatic } c && c.Args.Any(a => a is EProgField f && f.Name == inst));
                foreach (var role in new[] { "function", "unit" })
                {
                    if (role != "function" && decoded) continue;
                    if (!kernel.Kernel!.Roles.ContainsKey(role) || CallerItem(record, kernel, role) is not { } item) continue;
                    if (ReadsAfterCall(item, record)) continue;
                    foreach (var (list, _) in bodies.SelectMany(b => Lists(b)).ToList())
                        list.RemoveAll(s => s is SAssign { Lhs: ERecordField f } && f.Item == item && f.Via is null);
                }
            }
    }
}
