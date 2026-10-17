using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

/// <summary>An object allocated by JA-NEW and not yet constructed (the constructor call turns it into new C(...)).</summary>
public sealed class EAlloc(Var v, int id) : Expr
{
    public Var V { get; } = v;
    public int Id { get; } = id;
    public override IEnumerable<Expr> Children => [];
    public override Expr Map(Func<Expr, Expr> f) => this;
}

public sealed partial class FunctionLifter
{
    Var? resultVar;
    Var ResultVar() => resultVar ??= NewNamed("result", Fn.Ret?.Kind ?? VKind.Unknown);

    Var NewNamed(string name, VKind kind)
    {
        var v = new Var { Name = name, Kind = kind };
        R.Vars.Add(v);
        return v;
    }

    // ================================================================== reads

    Expr Read(Loc loc, string part, Node? n = null)
    {
        switch (loc)
        {
            case LVar lv:
            {
                var key = Key(loc);
                if (pending.TryGetValue(key, out var pe))
                {
                    if (part.Length > 0 && pe.Parts.TryGetValue(part, out var pv)) return pv;
                    Commit(key);
                }
                // the own result slot read after a recursive self-call holds that call's result
                if (lv.V.Slot?.Role == SlotRole.Ret && results.TryGetValue(lv.V.Slot.CobolName, out var selfRes)) return MakePart(selfRes, part, Fn.Ret!.Kind);
                if (lv.V.Slot?.Role == SlotRole.Ret) return MakePart(new EVar(ResultVar()), part, Fn.Ret!.Kind);
                if (thisAlias.Contains(lv.V)) return MakePart(new EThis(Fn.Owner), part, VKind.Ref);
                Expr whole = lv.V.IsThis ? new EThis(Fn.Owner) : new EVar(lv.V);
                if (allocated.ContainsKey(lv.V) && part is "C" or "I") return new EPart(whole, part);
                return MakePart(whole, part, KindOfLoc(loc));
            }
            case LField lf:
            {
                var key = Key(loc);
                if (pending.TryGetValue(key, out var pe))
                {
                    if (part.Length > 0 && pe.Parts.TryGetValue(part, out var pv)) return pv;
                    Commit(key);
                }
                if (lf.Obj is EVar ov && closures.TryGetValue(ov.V, out var lam) && lam.Captures.TryGetValue(lf.F, out var cap))
                    return MakePart(cap, part, lf.F.Kind);
                if (lf.Obj is EThis && Fn.IsLambda) return MakePart(new EVar(Captured(lf.F)), part, lf.F.Kind);
                return MakePart(new EField(lf.Obj, lf.F), part, lf.F.Kind);
            }
            case LStatic ls:
            {
                var key = Key(loc);
                if (pending.TryGetValue(key, out var pe))
                {
                    if (part.Length > 0 && pe.Parts.TryGetValue(part, out var pv)) return pv;
                    Commit(key);
                }
                if (M.Literals.TryGetValue(ls.F.CobolName, out var text)) return MakePart(ELit.Str(text), part, VKind.Ref);
                return MakePart(new EStatic(ls.F), part, ls.F.Kind);
            }
            case LForeign f:
            {
                if (f.Slot.Role == SlotRole.Ret)
                {
                    if (results.TryGetValue(f.Slot.CobolName, out var res)) return MakePart(res, part, f.Slot.Kind);
                    Residue("result of " + f.Fn.Id + " read before a call");
                    return new EOpaque("ret " + f.Fn.Id);
                }
                return SymRead(Key(loc), part, f.Slot.Kind);
            }
            case LFa fa:
                return SymRead(Key(loc), part, FaKind(fa.View));
            case LReg r:
                return SymRead(Key(loc), part, VKind.Unknown, r.Name);
            case LRecord rec:
                return rec.RefStart > 0 ? new ERecordField(rec.Item) { Type = JType.String, RefStart = rec.RefStart, RefLen = rec.RefLen } : RecordRead(rec.Item, n);
            default:
                Residue("read of " + loc);
                return new EOpaque(loc.ToString()!);
        }
    }

    Var Captured(FieldDef f)
    {
        if (R.Captured.TryGetValue(f, out var v)) return v;
        v = new Var { Name = f.Name, Kind = f.Kind };
        R.Captured[f] = v;
        return v;
    }

    Expr SymRead(string key, string part, VKind kind, string? reg = null)
    {
        if (sym.TryGetValue(key, out var parts))
        {
            if (parts.TryGetValue(part, out var v)) return v;
            if (parts.TryGetValue("", out var whole)) return MakePart(whole, part, kind);
            if (part.Length == 0 && parts.Count > 0) return Assemble(kind, parts, key);
            // a sub-part of a stored part (P.U of P)
            foreach (var (pk, pv) in parts)
                if (part.StartsWith(pk + ".", StringComparison.Ordinal)) return MakePart(pv, part[(pk.Length + 1)..], kind);
        }
        if (reg is not null && RegisterDefault(reg, part) is { } d) return d;
        Residue($"read of unset {key}{(part.Length > 0 ? "." + part : "")}");
        return new EOpaque(key + "." + part);
    }

    static Expr? RegisterDefault(string reg, string part) => reg switch
    {
        _ => null,
    };

    /// <summary>A part of a whole value: the class id (-C) and identity (-I) stay symbolic; payload parts are the value.</summary>
    Expr MakePart(Expr whole, string part, VKind kind)
    {
        if (part.Length == 0) return whole;
        if (whole is EPart) return new EPart(whole, part);
        switch (part)
        {
            case "C":
            case "I":
                return new EPart(whole, part);
            case "V":
                if (kind == VKind.Any || Kinds.IsValueKind(kind) || kind == VKind.Unknown) return whole;
                return whole;
            case "P":
            case "P.D":
            case "D":
            case "P.O":
            case "O":
            case "P.V":
                return whole;
            case "P.PR":
            case "PR":
                return Jdk(whole, "getPrecision", JType.Int);
            case "P.RM":
            case "RM":
                return Jdk(whole, "getRoundingMode", JType.RoundingMode);
            case "P.U":
            case "U":
                return Jdk(whole, "unscaledValue", JType.Of("java.math.BigInteger"));
            case "P.S":
            case "S":
                return Jdk(whole, "scale", JType.Int);
            case "P.Z":
            case "Z":
                return ELit.Int(0);
        }
        return new EPart(whole, part);
    }

    static ECall Jdk(Expr recv, string name, JType? type, params Expr[] args) =>
        new() { Kind = CallKind.Jdk, Name = name, Recv = recv, Args = [.. args], Type = type };

    static ECall JdkStatic(string owner, string name, JType? type, params Expr[] args) =>
        new() { Kind = CallKind.JdkStatic, Owner = owner, Name = name, Args = [.. args], Type = type };

    // ================================================================== writes

    void Write(Loc loc, string part, Expr value)
    {
        switch (loc)
        {
            case LVar lv when lv.V.Slot?.Role == SlotRole.Ret:
                WriteStorage(new LVar(ResultVar()), part, value, Fn.Ret!.Kind);
                return;
            case LVar lv when lv.V.IsThis:
                Residue("write to this");
                return;
            case LVar:
            case LField:
            case LStatic:
                WriteStorage(loc, part, value, KindOfLoc(loc));
                return;
            case LForeign f:
                SymWrite(Key(loc), part, value);
                return;
            case LFa:
            case LReg:
                SymWrite(Key(loc), part, value);
                if (loc is LReg r) RegisterWritten(r.Name, part, value);
                return;
            case LRecord rec:
                if (rec.RefStart > 0)
                {
                    Flush();
                    Emit(new SAssign(new ERecordField(rec.Item) { Type = JType.String, RefStart = rec.RefStart, RefLen = rec.RefLen }, Materialize(value)));
                }
                else RecordWrite(rec.Item, value);
                return;
            default:
                Residue("write to " + loc);
                return;
        }
    }

    void SymWrite(string key, string part, Expr value)
    {
        if (!sym.TryGetValue(key, out var parts)) sym[key] = parts = [];
        if (part.Length == 0) { parts.Clear(); parts[""] = value; return; }
        if (parts.TryGetValue("", out var whole))
        {
            // splitting a whole value: keep the other parts as parts of the old whole
            parts.Remove("");
            foreach (var p in new[] { "C", "I", "V", "P" }) if (p != part && !parts.ContainsKey(p)) parts[p] = MakePart(whole, p, VKind.Unknown);
        }
        parts[part] = value;
    }

    void WriteStorage(Loc loc, string part, Expr value, VKind kind)
    {
        var key = Key(loc);
        if (part.Length == 0)
        {
            if (pending.ContainsKey(key)) pending.Remove(key);
            Assign(loc, value);
            return;
        }
        if (!pending.TryGetValue(key, out var pe)) pending[key] = pe = (loc, kind, []);
        pe.Parts[part] = value;
        touched.Add(key);
        if (Complete(kind, pe.Parts)) Commit(key);
    }

    readonly HashSet<string> touched = new(StringComparer.Ordinal);

    static bool Complete(VKind kind, Dictionary<string, Expr> parts)
    {
        bool c = parts.ContainsKey("C");
        return kind switch
        {
            VKind.Ref => c && parts.ContainsKey("I"),
            VKind.Any => c && parts.ContainsKey("I") && (parts.ContainsKey("V") || !(parts["I"] is ELit)),
            VKind.BigDec => c && (parts.ContainsKey("P") || parts.ContainsKey("P.U") && parts.ContainsKey("P.S") && parts.ContainsKey("P.Z")),
            VKind.Date => c && (parts.ContainsKey("P") || parts.ContainsKey("P.D")),
            VKind.Mc => c && (parts.ContainsKey("P") || parts.ContainsKey("P.PR") && parts.ContainsKey("P.RM")),
            VKind.JEnum => c && (parts.ContainsKey("P") || parts.ContainsKey("P.O")),
            VKind.BoxInt or VKind.BoxLong or VKind.BoxBool => c && (parts.ContainsKey("P") || parts.ContainsKey("P.V")),
            _ => false,
        };
    }

    void Commit(string key)
    {
        if (!pending.Remove(key, out var pe)) return;
        var v = Assemble(pe.Kind, pe.Parts, key);
        Assign(pe.Loc, v);
    }

    /// <summary>Commits partial writes not touched by the statement just translated.</summary>
    void CommitUntouched()
    {
        foreach (var k in pending.Keys.ToList())
            if (!touched.Contains(k)) Commit(k);
        touched.Clear();
    }

    /// <summary>Commits every pending write (control-flow boundaries, calls).</summary>
    void Flush()
    {
        foreach (var k in pending.Keys.ToList()) Commit(k);
        touched.Clear();
    }

    void Assign(Loc loc, Expr value)
    {
        // a variable assigned anew no longer stands for the object it aliased (J3S reuses index variables)
        if (loc is LVar fresh)
        {
            var keepAlloc = value is EAlloc;
            if (!keepAlloc) { allocated.Remove(fresh.V); closures.Remove(fresh.V); }
            aliasOf.Remove(fresh.V);
            thisAlias.Remove(fresh.V);
        }
        // an allocation lands in a variable: remember it, the constructor call creates the object
        // a closure copied into another variable (each branch of a switch picks one): the variable holds the lambda
        if (value is EAlloc ca && loc is LVar cv && ca.V != cv.V && cv is not LVar { V.Slot.Role: SlotRole.Index } && closures.TryGetValue(ca.V, out var clam))
        {
            if (cv.V.Kind is VKind.Unknown) cv.V.Kind = VKind.Ref;
            // captures written through the copy still land in the lambda (it is not 'allocated': uses read the variable)
            closures[cv.V] = clam;
            Emit(new SAssign(new EVar(cv.V), clam));
            return;
        }
        if (value is EAlloc al && loc is LVar lv)
        {
            allocated[lv.V] = allocated.TryGetValue(al.V, out var a) ? a : (al.Id, M.ClassById.GetValueOrDefault(al.Id), null);
            if (closures.TryGetValue(al.V, out var lam)) closures[lv.V] = lam;
            if (lv.V.Kind is VKind.Unknown) lv.V.Kind = VKind.Ref;
            return;
        }
        // an index variable that copies the receiver is the receiver
        if (loc is LVar { V.Slot.Role: SlotRole.Index } tv && StripPart(value, "I") is EThis)
        {
            thisAlias.Add(tv.V);
            return;
        }
        // an index variable copying an object under construction (or a closure) is an alias of it
        if (loc is LVar { V.Slot.Role: SlotRole.Index } iv && StripPart(value, "I") is EVar src && (allocated.ContainsKey(src.V) || closures.ContainsKey(src.V)))
        {
            if (allocated.TryGetValue(src.V, out var a2)) allocated[iv.V] = a2;
            if (closures.TryGetValue(src.V, out var l2)) closures[iv.V] = l2;
            aliasOf[iv.V] = src.V;
            return;
        }
        // writes into a closure being built are its captures
        if (loc is LField lf && lf.Obj is EVar ov && closures.TryGetValue(ov.V, out var cl))
        {
            cl.Captures[lf.F] = value;
            return;
        }
        // an allocated object that was never constructed: materialize it before it escapes
        value = Materialize(value);
        // an index variable (Fnnnn-Xk) holds the object whose slot index it copies
        if (loc is LVar { V.Slot.Role: SlotRole.Index }) value = StripPart(value, "I");
        var target = loc switch
        {
            LVar v => (Expr)new EVar(v.V),
            LField f => new EField(f.Obj, f.F),
            LStatic s => new EStatic(s.F),
            _ => new EOpaque(loc.ToString()!),
        };
        Emit(new SAssign(target, value));
    }

    /// <summary>
    /// A use of an object allocated but not constructed: a closure becomes its lambda; another object gets its no-argument constructor.
    /// </summary>
    readonly Dictionary<Var, Var> aliasOf = [];
    readonly HashSet<Var> thisAlias = [];

    Expr Materialize(Expr e)
    {
        if (e is EVar av0 && aliasOf.TryGetValue(av0.V, out var orig)) e = new EVar(orig);
        if (e is EVar v && allocated.TryGetValue(v.V, out var a))
        {
            if (closures.TryGetValue(v.V, out var lam)) return lam;
            allocated.Remove(v.V);
            if (a.Cls is not null) Emit(new SAssign(new EVar(v.V), new ENew { Class = a.Cls }));
            else if (M.ExceptionNames.TryGetValue(a.Id, out var exn)) Emit(new SAssign(new EVar(v.V), new ENew { JdkClass = ExceptionClass(exn) }));
            else Residue("object of class id " + a.Id + " used before construction");
            return e;
        }
        if (e is EAlloc al) return Materialize(new EVar(al.V));
        return e;
    }

    /// <summary>Recombines parts written separately into one Java value.</summary>
    Expr Assemble(VKind kind, Dictionary<string, Expr> parts, string key)
    {
        parts.TryGetValue("C", out var c);
        parts.TryGetValue("I", out var i);
        parts.TryGetValue("V", out var v);
        parts.TryGetValue("P", out var p);
        if (c is ELit { Value: 0L or 0 }) return ELit.Null;
        // the parts of one source value
        if (c is EPart pc && pc.Part == "C")
        {
            var src = pc.Whole;
            if (i is EPart pi && pi.Part == "I" && Same(pi.Whole, src)) return src is EVar sv && allocated.ContainsKey(sv.V) ? new EAlloc(sv.V, allocated[sv.V].Id) : src;
            if (i is null or ELit && (v is not null || p is not null)) return v ?? p!;
            if (p is not null) return p;
            if (v is not null && Same(v, src)) return src;
            if (i is null && v is null && p is null) return src;
        }
        // a value with its class id: JB results, dates, constants
        if (c is ELit { Value: long id })
        {
            var vc = JType.ValueClass((int)id);
            if (vc is not null)
            {
                if (p is not null) return Typed(p, vc);
                if (v is not null) return Typed(v, vc);
                if (parts.TryGetValue("P.D", out var d)) return Typed(d, vc);
                if (parts.TryGetValue("P.O", out var o)) return o is ELit { Value: long ord } && JdkEnums.Constant(id, ord) is { } ec ? ec : o;
                if (parts.TryGetValue("P.V", out var bv)) return Typed(bv, vc);
                if (parts.TryGetValue("P.U", out var u) && parts.TryGetValue("P.S", out var s)) return BigConst(u, s);
                if (parts.TryGetValue("P.PR", out var pr) && parts.TryGetValue("P.RM", out var rm))
                    return new ENew { JdkClass = "java.math.MathContext", Args = [pr, rm], Type = JType.MathContext };
            }
            // an allocated object (C from JA-C, I from JA-I)
            if (i is EPart ai && ai.Part == "I" && ai.Whole is EVar av && allocated.ContainsKey(av.V)) return new EAlloc(av.V, (int)id);
        }
        if (c is null && i is EPart ii && ii.Part == "I") return ii.Whole;
        if (c is null && p is not null) return p;
        if (c is null && v is not null) return v;
        Residue($"cannot assemble {key} from {string.Join(",", parts.Select(kv => kv.Key + "=" + IrText.Show(kv.Value)))}");
        return new EOpaque("assemble " + key);
    }

    static Expr Typed(Expr e, JType t)
    {
        if (e.Type is null) e.Type = t;
        return e;
    }

    static Expr BigConst(Expr u, Expr s)
    {
        if (u is ELit { Value: long uv } && s is ELit { Value: long sv })
        {
            if (sv == 0 && uv == 0) return new EStaticJdk("java.math.BigDecimal", "ZERO", JType.BigDecimal);
            if (sv == 0 && uv == 1) return new EStaticJdk("java.math.BigDecimal", "ONE", JType.BigDecimal);
            if (sv == 0 && uv == 10) return new EStaticJdk("java.math.BigDecimal", "TEN", JType.BigDecimal);
            var text = sv == 0 ? uv.ToString() : FormatScaled(uv, (int)sv);
            return new ENew { JdkClass = "java.math.BigDecimal", Args = [ELit.Str(text)], Type = JType.BigDecimal };
        }
        if (s is ELit { Value: 0L }) return JdkStatic("java.math.BigDecimal", "valueOf", JType.BigDecimal, u);
        return JdkStatic("java.math.BigDecimal", "valueOf", JType.BigDecimal, u, s);
    }

    static string FormatScaled(long unscaled, int scale)
    {
        if (scale < 0) return unscaled + "E" + (-scale);
        var neg = unscaled < 0;
        var digits = Math.Abs(unscaled).ToString().PadLeft(scale + 1, '0');
        var txt = digits[..^scale] + "." + digits[^scale..];
        return (neg ? "-" : "") + txt;
    }

    static bool Same(Expr a, Expr b) => a switch
    {
        EVar va when b is EVar vb => va.V == vb.V,
        EThis when b is EThis => true,
        EField fa when b is EField fb => fa.F == fb.F && Same(fa.Obj, fb.Obj),
        EStatic sa when b is EStatic sb => sa.F == sb.F,
        ELit la when b is ELit lb => Equals(la.Value, lb.Value),
        _ => ReferenceEquals(a, b),
    };

    public static string ExceptionClass(string simple) => simple switch
    {
        "ConcurrentModificationException" or "NoSuchElementException" => "java.util." + simple,
        "DateTimeException" => "java.time." + simple,
        _ => "java.lang." + simple,
    };
}

/// <summary>A static field of a JDK class (BigDecimal.ZERO, RoundingMode.HALF_EVEN, Boolean.TRUE).</summary>
public sealed class EStaticJdk(string owner, string name, JType type) : Expr
{
    public string Owner { get; } = owner;
    public string Name { get; } = name;
    public override IEnumerable<Expr> Children => [];
    public override Expr Map(Func<Expr, Expr> f) => this;
    public JType JType { get; } = type;
}
