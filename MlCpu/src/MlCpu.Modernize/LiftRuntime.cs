using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

/// <summary>The J3S runtime paragraphs as operations on symbolic registers (docs/J3S.md §8-§10, Intrinsics).</summary>
public sealed partial class FunctionLifter
{
    Expr Reg(string name, string part = "") => SymRead("R#" + name, part, VKind.Unknown, name);
    void SetReg(string name, Expr v) => SymWrite("R#" + name, "", v);

    Expr Materialized(Expr e, VKind kind, JType? type)
    {
        // results of operations that may throw are bound to a temporary where the COBOL computes them (exception order)
        Flush();
        var t = NewTemp(kind, type);
        Emit(new SAssign(new EVar(t), e));
        return new EVar(t);
    }

    void Runtime(string name, Stmt s)
    {
        switch (name)
        {
            case "JX-RAISE":
            {
                Flush();
                var id = Reg("JX-C");
                if (id is ELit { Value: long cid } && TypeOfClassId((int)cid) is { } t)
                    Emit(new SThrow(new ENew { JdkClass = t.Class is null ? t.Name : null, Class = t.Class, Type = t }));
                else { Residue("raise of an unknown class"); Emit(new SThrow(new EOpaque("raise"))); }
                return;
            }
            case "JX-RAISE-OBJ":
            {
                Flush();
                var o = Reg("JX-O");
                Emit(new SThrow(Materialize(o)));
                return;
            }
            case "JX-NPE":
                Flush();
                Emit(new SThrow(new ENew { JdkClass = "java.lang.NullPointerException" }));
                return;
            case "JX-JOBFAIL":
            {
                // the job adapter cannot pass a value (a field's range, an unknown step): the legacy run displays the
                // failure and abends 38 at what JX-AT names; the modern code stops the same way
                Flush();
                var at = jobFailAt ?? "";
                M.JobAdapterSites.TryAdd($"{P.Id} {Fn.Id} JX-JOBFAIL {at}", "JX-JOBFAIL");
                Emit(new SThrow(new ENew { JdkClass = M.PlatformPackage + ".Abend", Args = [ELit.Int(38), ELit.Str("job adapter failure"), ELit.Str("CEE3ABD"), ELit.Str(at), ELit.Str("JOB ADAPTER FAILURE")] }));
                return;
            }
            case "JS-FIELD":
            {
                // the characters of a String object (JA) into JS-BYTES: the job adapter writes printable ASCII only
                var str = Materialize(Assemble(VKind.Ref, RegParts("JA"), "JA"));
                if (Fn.IsJob)
                {
                    M.JobAdapterSites.TryAdd($"{P.Id} {Fn.Id} JS-FIELD", "JS-FIELD");
                    str = JdkStatic(M.PlatformPackage + ".JobAdapter", "field", JType.String, str);
                }
                SetReg("JS-BYTES", str);
                return;
            }
            case "JX-UNSUPPORTED":
            {
                // a construct J3S cannot reproduce exactly (the emulation's trap, a catch clause of the target profile):
                // the legacy run stops with abend 31 at the statement's site; the modern code stops the same way instead
                // of doing what the JDK would (no widening past the legacy's frontier)
                Flush();
                var at = site ?? "";
                M.UnsupportedSites.TryAdd(at, P.Id);
                Emit(new SThrow(new ENew { JdkClass = M.PlatformPackage + ".Abend", Args = [ELit.Int(31), ELit.Str("unsupported construct"), ELit.Str("CEE3ABD"), ELit.Str(at), ELit.Str("UNSUPPORTED CONSTRUCT REACHED")] }));
                return;
            }
            case "JX-INTERNAL": case "JX-DEAD": case "JX-INEXACT":
                Flush();
                Emit(new SThrow(new ENew { JdkClass = "java.lang.IllegalStateException", Args = [ELit.Str(name)] }));
                return;
            case "JX-PROP": case "JB-ERR": case "JX-SIZE": case "JX-DEPTH": case "JX-ABEND":
                return;
            case "JB-CALL": BigDecimalOp(); return;
            case "JW-WRAP": SetReg("JW-L", new ECast(JType.Int, Reg("JW-L")) { Type = JType.Int }); return;
            case "JW-LONGHASH": SetReg("JW-L", JdkStatic("java.lang.Long", "hashCode", JType.Int, Reg("JW-L"))); return;
            case "JH-IDENTITY":
                SetReg("JW-L", JdkStatic("java.lang.System", "identityHashCode", JType.Int, Materialize(Assemble(VKind.Ref, RegParts("JA"), "JA"))));
                return;
            case "JH-JENUM":
                SetReg("JW-L", Jdk(Reg("JA-K"), "hashCode", JType.Int));
                return;
            case "JA-LENGTH":
                SetReg("JA-N", new EArrayLength(Assemble(VKind.Ref, RegParts("JA"), "JA")));
                return;
            case "JA-GETA": case "JA-GETI":
            {
                var arr = Assemble(VKind.Ref, RegParts("JA"), "JA");
                var e = new EIndex(arr, Reg("JA-K"));
                SetReg(name == "JA-GETA" ? "JA-EA" : "JA-EI", Materialized(e, name == "JA-GETA" ? VKind.Any : VKind.Int, null));
                return;
            }
            case "JA-SETA": case "JA-SETI":
            {
                Flush();
                var arr = Assemble(VKind.Ref, RegParts("JA"), "JA");
                var v = name == "JA-SETA" ? SymReadWhole("R#JA-EA", VKind.Any) : Reg("JA-EI");
                Emit(new SAssign(new EIndex(arr, Reg("JA-K")), Materialize(v)));
                return;
            }
            case "JA-NEW-A": case "JA-NEW-I":
            {
                var t = NewTemp(VKind.Ref, name == "JA-NEW-I" ? JType.IntArray : JType.ObjectArray);
                Flush();
                Emit(new SAssign(new EVar(t), new EArrayNew(name == "JA-NEW-I" ? JType.Int : JType.Object, Reg("JA-N"))));
                SetPartsJa(new EVar(t));
                return;
            }
            case "JS-CHARS":
            {
                var bytes = Reg("JS-BYTES");
                // a record field's bytes: its characters, trailing spaces included
                if (bytes is ERecordField { Padded: false } brf && brf.Type?.Name == "java.lang.String") bytes = new ERecordField(brf.Item) { Index = brf.Index, Padded = true, Type = JType.String };
                // JS-BYTES (1:JS-LEN): a literal is cut or padded to its length
                if (bytes is ELit { Value: string lit } && SymReadOr("R#JS-LEN", ELit.Null) is ELit { Value: long len })
                    bytes = ELit.Str(lit.Length >= len ? lit[..(int)len] : lit.PadRight((int)len));
                // a record field's bytes: the job adapter reads printable ASCII only (else abend 38 at JS-CHARS)
                if (Fn.IsJob && bytes is not ELit && DeepOf(bytes).Any(x => x is ERecordField))
                {
                    M.JobAdapterSites.TryAdd($"{P.Id} {Fn.Id} JS-CHARS", "JS-CHARS");
                    bytes = JdkStatic(M.PlatformPackage + ".JobAdapter", "chars", JType.String, bytes);
                }
                SetPartsJa(Jdk(bytes, "toCharArray", JType.CharArray));
                return;
            }
            case "JS-RTRIM":
            {
                var tb = Reg("JS-BYTES");
                if (tb is ERecordField { Padded: false } trf && trf.Type?.Name == "java.lang.String") tb = new ERecordField(trf.Item) { Index = trf.Index, Padded = true, Type = JType.String };
                SetReg("JS-BYTES", Jdk(tb, "stripTrailing", JType.String));
            }
                return;
            case "JD-PLUS": DatePlus(); return;
            case "JD-UNIT": SetReg("JD-U", Reg("JD-UO") is ELit { Value: long uo } && JdkEnums.Constant(8, uo) is { } cu ? cu : Reg("JD-UO")); return;
            case "JD-COMPARE": SetReg("JD-N", Jdk(Reg("JD-DAY"), "compareTo", JType.Int, Reg("JD-DAY2"))); return;
            case "JD-YMD":
            {
                var d = Reg("JD-DAY");
                SetReg("JD-Y", Jdk(d, "getYear", JType.Int));
                SetReg("JD-M", Jdk(d, "getMonthValue", JType.Int));
                SetReg("JD-D", Jdk(d, "getDayOfMonth", JType.Int));
                return;
            }
            case "JD-OF":
                SetReg("JD-DAY", Materialized(JdkStatic("java.time.LocalDate", "of", JType.LocalDate, Reg("JD-Y"), Reg("JD-M"), Reg("JD-D")), VKind.Date, JType.LocalDate));
                return;
            case "JD-WITH-DAY":
                SetReg("JD-DAY", Materialized(Jdk(Reg("JD-DAY"), "withDayOfMonth", JType.LocalDate, Reg("JD-N")), VKind.Date, JType.LocalDate));
                return;
            case "JD-HASH": SetReg("JW-L", Jdk(Reg("JD-DAY"), "hashCode", JType.Int)); return;
            case "JD-LEAP": SetReg("JD-FLAG", JdkStatic("java.time.Year", "isLeap", JType.Bool, Reg("JD-L"))); return;
            case "JD-KERNEL": DateKernel(); return;
            case "JG-PUSH": case "JG-MARK": case "JG-COLLECT": case "JG-POP": case "JA-INIT": case "JA-MARK0": case "JA-RESET": case "JA-STATS": case "JD-TABLES": case "JW-TABLES":
                return;
        }
        if (name.StartsWith("JA-NEW-", StringComparison.Ordinal))
        {
            Allocate(name[7..]);
            return;
        }
        if (name.StartsWith("JW-", StringComparison.Ordinal)) { BitOp(name); return; }
        Residue("runtime paragraph " + name);
    }

    Dictionary<string, Expr> RegParts(string prefix)
    {
        var d = new Dictionary<string, Expr>();
        if (sym.TryGetValue("R#" + prefix + "-C", out var c) && c.TryGetValue("", out var cv)) d["C"] = cv;
        if (sym.TryGetValue("R#" + prefix + "-I", out var i) && i.TryGetValue("", out var iv)) d["I"] = iv;
        return d;
    }

    void SetPartsJa(Expr obj)
    {
        SetReg("JA-C", new EPart(obj, "C"));
        SetReg("JA-I", new EPart(obj, "I"));
    }

    Expr SymReadWhole(string key, VKind kind)
    {
        if (sym.TryGetValue(key, out var parts))
        {
            if (parts.TryGetValue("", out var w)) return w;
            if (parts.Count > 0) return Assemble(kind, parts, key);
        }
        Residue("read of unset " + key);
        return new EOpaque(key);
    }

    /// <summary>JA-NEW-pool with the class id in JA-C: a new object (constructed by the next constructor call on it), or a closure.</summary>
    void Allocate(string pool)
    {
        var idExpr = Reg("JA-C");
        if (idExpr is not ELit { Value: long id }) { Residue("allocation without a class id"); return; }
        var cls = M.ClassById.GetValueOrDefault((int)id);
        var v = NewTemp(VKind.Ref, cls is null ? null : JType.Of(cls));
        allocated[v] = ((int)id, cls, M.Pools.GetValueOrDefault(pool));
        if (cls is { Kind: ClassKind.Closure }) closures[v] = new ELambda { Closure = cls };
        SetPartsJa(new EVar(v));
    }

    // ================================================================== BigDecimal (FSJBDEC through JB-CALL)

    void BigDecimalOp()
    {
        // a capacity-checked operation whose site no guard named: fail closed (the generation reports it)
        if (limitPending is not null) Residue("decimal capacity: a checked operation without its JB-RC site");
        limitPending = null;
        var opE = Reg("JB-OP");
        if (opE is not ELit { Value: string op }) { Residue("JB-CALL without an operation"); return; }
        Expr A() => Reg("JB-A");
        Expr B() => Reg("JB-B");
        Expr Mc() => MathContextOf(Reg("JB-P"), Mode());
        Expr Mode() => Reg("JB-M") is ELit { Value: long m } && JdkEnums.Constant(7, m) is { } rm ? rm : Reg("JB-M");
        Expr? r = null, n = null;
        switch (op)
        {
            case "AD": r = Jdk(A(), "add", JType.BigDecimal, B()); break;
            case "SB": r = Jdk(A(), "subtract", JType.BigDecimal, B()); break;
            case "MU": r = Jdk(A(), "multiply", JType.BigDecimal, B()); break;
            case "AM": r = Jdk(A(), "add", JType.BigDecimal, B(), Mc()); break;
            case "SM": r = Jdk(A(), "subtract", JType.BigDecimal, B(), Mc()); break;
            case "MM": r = Jdk(A(), "multiply", JType.BigDecimal, B(), Mc()); break;
            case "DM": r = Jdk(A(), "divide", JType.BigDecimal, B(), Mc()); break;
            case "DS": r = Jdk(A(), "divide", JType.BigDecimal, B(), Reg("JB-P"), Mode()); break;
            case "DR": r = Jdk(A(), "divide", JType.BigDecimal, B(), Mode()); break;
            case "SS": r = Jdk(A(), "setScale", JType.BigDecimal, Reg("JB-P"), Mode()); break;
            case "RD": r = Jdk(A(), "round", JType.BigDecimal, Mc()); break;
            case "PW": r = Jdk(A(), "pow", JType.BigDecimal, Reg("JB-P")); break;
            case "ST": r = Jdk(A(), "stripTrailingZeros", JType.BigDecimal); break;
            case "NG": r = Jdk(A(), "negate", JType.BigDecimal); break;
            case "AB": r = Jdk(A(), "abs", JType.BigDecimal); break;
            case "MN": r = Jdk(A(), "min", JType.BigDecimal, B()); break;
            case "MX": r = Jdk(A(), "max", JType.BigDecimal, B()); break;
            case "SG": n = Jdk(A(), "signum", JType.Int); break;
            case "PR": n = Jdk(A(), "precision", JType.Int); break;
            case "CP": n = Jdk(A(), "compareTo", JType.Int, B()); break;
            case "HC": n = Jdk(A(), "hashCode", JType.Int); break;
            case "EQ": n = new ECond(Jdk(A(), "equals", JType.Bool, B()), ELit.Int(1), ELit.Int(0)) { Type = JType.Int }; break;
            case "VD": r = JdkStatic("java.math.BigDecimal", "valueOf", JType.BigDecimal, Reg("JB-D")); break;
            case "PS": r = new ENew { JdkClass = "java.math.BigDecimal", Args = [Jdk(A(), "toPlainString", JType.String)], Type = JType.BigDecimal }; break;
            default: Residue("JB operation " + op); return;
        }
        if (r is not null)
        {
            SetReg("JB-R", Materialized(r, VKind.BigDec, JType.BigDecimal));
            if (M.DecimalInline)
            {
                // the runtime without a big-value store: every operation that can exceed 31 digits is checked at its site
                // (the JB-RC guard names it); one without a model is the frontier the generation reports
                if (InlineLimitOps.Contains(op) && Low[^1] is SAssign { Lhs: EVar } il) limitPending = il;
                else if (!InlineSafeOps.Contains(op)) M.UncheckedDecimalOps.TryAdd($"{P.Id} {Fn.Id} line {line}: {op} {IrText.Show(r)}", 0);
            }
            // an operation FSJBDEC may refuse for capacity: pow, the operations on its results (the JB-RC guard names the
            // site), and every capacity operation of a function that handles such a value (its stores counted exactly)
            else if (LimitOps.Contains(op) && (op == "PW" || Limited(r) || M.CheckedFunctions.ContainsKey(FnKey)) && Low[^1] is SAssign { Lhs: EVar } la) limitPending = la;
            else if (LimitOps.Contains(op)) M.UncheckedDecimalOps.TryAdd($"{P.Id} {Fn.Id} line {line}: {op} {IrText.Show(r)}", 0);
        }
        else if (M.DecimalInline && !InlineSafeOps.Contains(op))
            M.UncheckedDecimalOps.TryAdd($"{P.Id} {Fn.Id} line {line}: {op} {IrText.Show(n ?? A())}", 0);
        // an operation without FSJBDEC's big path on a value that may be beyond 31 digits (RC 20 there): fail closed
        if (op != "VD" && !LimitOps.Contains(op) && !BigPathOps.Contains(op) && (Limited(A()) || op is "AD" or "SB" or "MU" or "AM" or "SM" or "MM" or "DM" or "DS" or "DR" or "CP" or "MN" or "MX" or "EQ" && Limited(B())))
            Residue("decimal capacity: JB operation " + op + " on a value beyond 31 digits has no checked form");
        if (n is not null) SetReg("JB-N", Materialized(n, VKind.Int, JType.Int));
    }

    /// <summary>
    /// The operations whose result FSJBDEC stores when it is beyond 31 digits (7900-BIG-RESULT; docs/J3S.md §17): the
    /// capacity checked operations. Negate and abs of a stored value store a new one too (7560).
    /// </summary>
    static readonly HashSet<string> LimitOps = new(StringComparer.Ordinal) { "AD", "SB", "MU", "PW", "DS", "SS", "NG", "AB" };
    /// <summary>Operations with a big path that store nothing (signum, precision, equals, compareTo, min and max: an operand).</summary>
    static readonly HashSet<string> BigPathOps = new(StringComparer.Ordinal) { "SG", "PR", "EQ", "CP", "MN", "MX" };
    /// <summary>
    /// The operations of the runtime without a big-value store that can refuse a value of 31 digits (RC 20), as DecimalLimit
    /// of that runtime models them: exact add, subtract and multiply, setScale, divide at a scale or the dividend's scale.
    /// </summary>
    static readonly HashSet<string> InlineLimitOps = new(StringComparer.Ordinal) { "AD", "SB", "MU", "SS", "DS", "DR" };
    /// <summary>Its operations that cannot refuse a value of at most 31 digits (no virtual zeros: every value-making operation is checked).</summary>
    static readonly HashSet<string> InlineSafeOps = new(StringComparer.Ordinal) { "SG", "PR", "EQ", "CP", "MN", "MX", "HC", "NG", "AB" };
    /// <summary>A capacity-checked decimal operation waiting for the JB-RC guard that names its legacy site.</summary>
    SAssign? limitPending;
    /// <summary>Variables holding results of capacity-checked operations: their values can exceed 31 digits.</summary>
    readonly HashSet<Var> limitVars = [];

    bool Limited(Expr e) => e switch
    {
        EVar v => limitVars.Contains(v.V) || !TempName(v.V.Name) && M.LimitedVars.ContainsKey(FnKey + "#" + v.V.Name),
        EField f => M.LimitedFields.ContainsKey(FieldKey(f.F)),
        EStatic st => M.LimitedFields.ContainsKey(FieldKey(st.F)),
        ECast c => Limited(c.E),
        // a call of the estate's code: its result (its arguments taint its parameters, NoteLimited)
        ECall { Fn: { } cf, Kind: not CallKind.Jdk and not CallKind.JdkStatic } => M.LimitedReturns.ContainsKey(KeyOf(cf)),
        // an object is not a decimal value: its fields carry the taint
        ENew => false,
        _ => e.Children.Any(Limited),
    };

    /// <summary>A value itself (not an expression over one) that may be a stored decimal: what a copy of it taints.</summary>
    bool TaintedValue(Expr e) => e switch
    {
        EVar or EField or EStatic => Limited(e),
        ECast c => TaintedValue(c.E),
        ECall { Fn: not null, Kind: not CallKind.Jdk and not CallKind.JdkStatic } => Limited(e),
        _ => false,
    };

    /// <summary>The function's key in the model's taint sets.</summary>
    string FnKey => KeyOf(Fn);
    static string KeyOf(FunctionDef f) => f.Program.Id + "/" + f.Id;
    static string FieldKey(FieldDef f) => (f.Pool?.Prefix ?? f.Group ?? f.Owner?.FullName ?? "?") + "." + f.CobolName;

    /// <summary>A variable that may hold a value FSJBDEC stores: checked from here on, and its function checks every operation.</summary>
    /// <summary>
    /// A variable that may hold a value FSJBDEC stores: checked from here on, and its function checks every operation. A
    /// named variable is tainted for the whole function (loops and joins: flow-insensitive, across passes); a temporary (the
    /// lift's, a frame's reused T-item) only until it is assigned again (straight-line code).
    /// </summary>
    void Taint(Var v)
    {
        limitVars.Add(v);
        if (!TempName(v.Name)) M.LimitedVars.TryAdd(FnKey + "#" + v.Name, 0);
        M.CheckedFunctions.TryAdd(FnKey, 0);
    }

    static bool TempName(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, @"^t\d+_?$");

    /// <summary>
    /// The guard after a capacity-checked operation names its site (IF JB-RC NOT = 0 ... MOVE 'APLC:225' TO JX-AT ...
    /// PERFORM JB-ERR): the operation becomes DecimalLimit's, which abends 37 at that site where FSJBDEC would.
    /// </summary>
    void ApplyDecimalLimit(Stmt guard, string? guardSite)
    {
        var pendingOp = limitPending;
        limitPending = null;
        if (pendingOp is null || guardSite is null || guard.Get("cond") is not { K: "rel", L: { K: "ref" } jr } || !jr.V!.Equals("JB-RC", StringComparison.OrdinalIgnoreCase) || pendingOp.Rhs is not ECall { Kind: CallKind.Jdk, Recv: { } })
        {
            // the runtime without a store: an operation whose guard names no decimal site is not checked (the frontier)
            if (M.DecimalInline && pendingOp is not null) M.UncheckedDecimalOps.TryAdd($"{P.Id} {Fn.Id} line {guard.Line}: {IrText.Show(pendingOp.Rhs)} (no decimal site)", 0);
            return;
        }
        if (pendingOp.Rhs is not ECall { Kind: CallKind.Jdk, Recv: { } recv } c) return;
        pendingOp.Rhs = new ECall { Kind = CallKind.JdkStatic, Owner = M.PlatformPackage + ".DecimalLimit", Name = c.Name, Args = [recv, .. c.Args, ELit.Str(guardSite)], Type = JType.BigDecimal };
        M.DecimalLimitSites.TryAdd(guardSite, 0);
        // the runtime without a store: no value of more than 31 digits exists, nothing to taint or count
        if (M.DecimalInline) return;
        // the result may be a stored decimal when an operand may be one, or for a product or a power of any operands; an
        // operation checked only because its function handles such values (an aligned add, a rounding of values that are
        // not) counts its result exactly but does not taint it
        if (pendingOp.Lhs is EVar lv && (c.Name is "multiply" or "pow" || Limited(recv) || c.Args.Any(Limited))) Taint(lv.V);
        M.DecimalLimitSites.TryAdd(guardSite, 0);
        // values a class initializer stores are below the job's store mark (BM), which the checked sites do not count: fail closed
        if (Fn.IsClinit) Residue("decimal capacity: a checked site in a class initializer at " + guardSite);
    }

    /// <summary>JB-P/JB-M from mc-PR and mc-RM: the MathContext itself; otherwise new MathContext(p, m).</summary>
    static Expr MathContextOf(Expr p, Expr m)
    {
        if (p is ECall { Name: "getPrecision", Recv: { } a } && m is ECall { Name: "getRoundingMode", Recv: { } b } && Same(a, b)) return a;
        return new ENew { JdkClass = "java.math.MathContext", Args = [p, m], Type = JType.MathContext };
    }

    // ================================================================== dates

    void DatePlus()
    {
        var day = Reg("JD-DAY");
        var n = Reg("JD-L");
        var unit = Reg("JD-U");
        bool minus = false;
        if (n is EBinary { Op: "-", L: ELit { Value: 0L } } neg) { minus = true; n = neg.R; }
        Expr r;
        if (unit is ELit { Value: string u })
        {
            var m = u switch { "D" => "Days", "W" => "Weeks", "M" => "Months", "Y" => "Years", _ => null };
            if (m is null) { Residue("date unit " + u); return; }
            r = Jdk(day, (minus ? "minus" : "plus") + m, JType.LocalDate, n);
        }
        else r = Jdk(day, minus ? "minus" : "plus", JType.LocalDate, n, unit);
        SetReg("JD-DAY", Materialized(r, VKind.Date, JType.LocalDate));
    }

    void DateKernel()
    {
        var fnE = Reg("DT-FUNCTION");
        if (fnE is not ELit { Value: string f }) { Residue("date kernel without a function"); return; }
        switch (f)
        {
            case "YM": SetReg("DT-YYYYMMDD", Typed(Reg("DT-DAY"), JType.LocalDate)); return;
            case "DN":
            {
                // a yyyyMMdd record field read as a date: the record's date view
                var dn = Reg("DT-YYYYMMDD");
                SetReg("DT-RESULT", dn is ERecordField drf ? new ERecordField(drf.Item) { Index = drf.Index, Type = JType.LocalDate } : Typed(dn, JType.LocalDate));
                return;
            }
            case "LM": SetReg("DT-RESULT", Jdk(Reg("DT-DAY"), "lengthOfMonth", JType.Int)); return;
            case "LY": SetReg("DT-RESULT", Jdk(Reg("DT-DAY"), "lengthOfYear", JType.Int)); return;
            case "BT":
            {
                var unit = Reg("DT-UNIT");
                if (unit is ELit { Value: string u })
                    unit = new EStaticJdk("java.time.temporal.ChronoUnit", u switch { "D" => "DAYS", "W" => "WEEKS", "M" => "MONTHS", "Y" => "YEARS", _ => "DAYS" }, JType.ChronoUnit);
                SetReg("DT-RESULT", Materialized(Jdk(unit, "between", JType.Long, Reg("DT-DAY"), Reg("DT-DAY2")), VKind.Long, JType.Long));
                return;
            }
            default:
                Residue("date kernel function " + f);
                return;
        }
    }

    // ================================================================== bit operations (JW)

    void BitOp(string name)
    {
        switch (name)
        {
            case "JW-XOR32": SetReg("JW-L", new EBinary("^", Reg("JW-L"), Reg("JW-L2")) { Type = JType.Int }); return;
            case "JW-AND32": SetReg("JW-L", new EBinary("&", Reg("JW-L"), Reg("JW-L2")) { Type = JType.Int }); return;
            case "JW-OR32": SetReg("JW-L", new EBinary("|", Reg("JW-L"), Reg("JW-L2")) { Type = JType.Int }); return;
            case "JW-XOR64": SetReg("JW-L", new EBinary("^", Reg("JW-L"), Reg("JW-L2")) { Type = JType.Long }); return;
            case "JW-SIGNED32": case "JW-SIGNED64": case "JW-U32": case "JW-U64": case "JW-K":
                Residue("bit operation " + name);
                return;
        }
        Residue("bit operation " + name);
    }
}

public sealed class EArrayLength(Expr a) : Expr
{
    public Expr A { get; set; } = a;
    public override IEnumerable<Expr> Children => [A];
    public override Expr Map(Func<Expr, Expr> f) => new EArrayLength(f(A)) { Type = Type };
}
