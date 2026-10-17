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
                Flush();
                Emit(new SThrow(new ENew { JdkClass = M.PlatformPackage + ".Abend", Args = [ELit.Int(38), ELit.Str(P.Id + " job adapter failure")] }));
                return;
            case "JS-FIELD":
                // the characters of a String object (JA) into JS-BYTES
                SetReg("JS-BYTES", Materialize(Assemble(VKind.Ref, RegParts("JA"), "JA")));
                return;
            case "JX-INTERNAL": case "JX-DEAD": case "JX-UNSUPPORTED": case "JX-INEXACT":
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
        if (r is not null) SetReg("JB-R", Materialized(r, VKind.BigDec, JType.BigDecimal));
        if (n is not null) SetReg("JB-N", Materialized(n, VKind.Int, JType.Int));
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
