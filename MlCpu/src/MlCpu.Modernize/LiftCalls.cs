using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

/// <summary>A field of a record of a job or driver program (a linkage record, FS-OUT): typed by its PICTURE.</summary>
public sealed class ERecordField(DataItem item) : Expr
{
    public DataItem Item { get; } = item;
    /// <summary>The OCCURS subscript (1-based), or null.</summary>
    public Expr? Index { get; init; }
    /// <summary>The field's characters with trailing spaces (J3S copies the bytes of an alphanumeric field).</summary>
    public bool Padded { get; init; }
    /// <summary>A reference modification (1-based start, length): the characters of that part of the field.</summary>
    public int RefStart { get; init; }
    public int RefLen { get; init; }
    /// <summary>The record as a parameter or local of a restructured program method (ClassicFlow), in place of its instance field.</summary>
    public Expr? Via { get; init; }
    /// <summary>The subscripts past the first (a table inside a table: MA-IP-RATE (k, j)), 1-based.</summary>
    public IReadOnlyList<Expr> More { get; init; } = [];
    public override IEnumerable<Expr> Children => (Index is null ? [] : new[] { Index }).Concat(More).Concat(Via is null ? [] : [Via]);
    public override Expr Map(Func<Expr, Expr> f) => Index is null && Via is null ? this
        : new ERecordField(Item) { Index = Index is null ? null : f(Index), More = More.Select(f).ToList(), Padded = Padded, Type = Type, RefStart = RefStart, RefLen = RefLen, Via = Via is null ? null : f(Via) };
}

public sealed partial class FunctionLifter
{
    // ================================================================== PERFORM

    void Perform(Stmt s)
    {
        if (s.Str("inline") == "true") { InlineLoop(s); return; }
        var t = s.Str("target") ?? "";
        if (M.FunctionOfParagraph(t) is { } fn && fn.Paragraph.Equals(t, StringComparison.OrdinalIgnoreCase))
        {
            InvokeLocal(fn);
            return;
        }
        // a paragraph of the job program lifted as a method of its class (JO-EMIT)
        if (M.ClassicMethods.TryGetValue(P.Id + "/" + t, out var classic))
        {
            Flush();
            Emit(new SExpr(new ECall { Kind = CallKind.Instance, Name = classic }));
            return;
        }
        // the recursion protocol inside a function: MOVE k TO JR-SEL, PERFORM Sn-RUN THRU Sn-RUN-X runs member k of the component
        if (t.EndsWith("-RUN", StringComparison.OrdinalIgnoreCase) && jrSel is int sel
            && P.Model.Paragraphs.FirstOrDefault(x => x.Name.Equals(t, StringComparison.OrdinalIgnoreCase)) is { } run
            && run.Statements.FirstOrDefault(x => x.Verb == "GO TO" && x.Get("depending") is not null)?.L?.GetValueOrDefault("targets") is { } members
            && sel >= 1 && sel <= members.Count && M.FunctionOfParagraph(members[sel - 1].V!) is { } member)
        {
            InvokeLocal(member);
            return;
        }
        Runtime(t.ToUpperInvariant(), s);
    }

    void InlineLoop(Stmt s)
    {
        var body = s.Blocks?.GetValueOrDefault("LOOP") ?? [];
        Flush();
        var top = NewLabel("loop");
        var end = NewLabel("endloop");
        if (s.Get("var") is { } v)
        {
            var loc = Resolve(v);
            var it = P.ItemsById.GetValueOrDefault(v.Id ?? -1);
            var part = it is null ? "" : PartOf(loc, it);
            Write(loc, part, Operand(s.Get("from")!));
            Flush();
            Emit(new LLabel(top));
            Emit(new LCondGoto(Cond(s.Get("until")!), end));
            Block(body);
            Flush();
            Write(loc, part, Arith("+", Read(loc, part, v), Operand(s.Get("by")!)));
            Flush();
            Emit(new LGoto(top));
            Emit(new LLabel(end));
            return;
        }
        if (s.Get("until") is { } until)
        {
            Emit(new LLabel(top));
            Emit(new LCondGoto(Cond(until), end));
            Block(body);
            Flush();
            Emit(new LGoto(top));
            Emit(new LLabel(end));
            return;
        }
        if (s.Get("times") is { } times)
        {
            var i = NewTemp(VKind.Int, JType.Int);
            Emit(new SAssign(new EVar(i), ELit.Int(0)));
            Emit(new LLabel(top));
            Emit(new LCondGoto(new EBinary(">=", new EVar(i), Operand(times)), end));
            Block(body);
            Flush();
            Emit(new SAssign(new EVar(i), new EBinary("+", new EVar(i), ELit.Int(1))));
            Emit(new LGoto(top));
            Emit(new LLabel(end));
            return;
        }
        Residue("inline PERFORM form");
    }

    // ================================================================== calls

    /// <summary>A call of a function of this program (PERFORM ... THRU, or the recursive protocol): arguments staged in its frame.</summary>
    void InvokeLocal(FunctionDef callee)
    {
        var args = new List<Expr>();
        Expr? recv = null;
        if (callee.This is { } ts)
        {
            recv = StagedArg(callee, ts);
        }
        foreach (var ps in callee.Params) args.Add(StagedArg(callee, ps));
        ClearStaging(callee);
        EmitCall(callee, recv, args);
    }

    Expr StagedArg(FunctionDef callee, FrameSlot slot)
    {
        var key = "F#" + callee.Id + "#" + slot.CobolName;
        if (sym.TryGetValue(key, out var parts) && parts.Count > 0)
        {
            var v = parts.TryGetValue("", out var w) ? w : Assemble(slot.Kind, parts, key);
            return v;
        }
        // a recursive self-call may leave an argument unchanged: J3S writes every parameter, so this is a gap
        Residue($"argument {slot.CobolName} of {callee.Id} not set");
        return new EOpaque("arg " + slot.CobolName);
    }

    void ClearStaging(FunctionDef callee)
    {
        foreach (var k in sym.Keys.Where(k => k.StartsWith("F#" + callee.Id + "#", StringComparison.Ordinal)).ToList()) sym.Remove(k);
    }

    /// <summary>A CALL of another program: arguments in FA slots (receiver first), the entry number in FA-ENTRY, the result in slot 1.</summary>
    void Call(Stmt s)
    {
        var prog = s.Get("target")?.V;
        if (prog is null) { Residue("CALL without a literal target"); return; }
        var entryExpr = SymReadOr("R#FA-ENTRY", ELit.Null);
        if (entryExpr is not ELit { Value: long entry })
        {
            Residue($"CALL {prog} without a known entry");
            return;
        }
        var callee = M.ResolveEntry(prog, (int)entry);
        if (callee is null) { Residue($"CALL {prog} entry {entry}: no function"); return; }
        int k = 1;
        Expr? recv = null;
        if (callee.This is not null) recv = FaArg(k++, callee.This.Kind);
        var args = new List<Expr>();
        foreach (var ps in callee.Params) args.Add(FaArg(k++, ps.Kind));
        foreach (var key in sym.Keys.Where(x => x.StartsWith("FA#", StringComparison.Ordinal)).ToList()) sym.Remove(key);
        var result = EmitCall(callee, recv, args);
        if (result is not null) sym["FA#1"] = new() { [""] = result };
    }

    Expr FaArg(int k, VKind kind)
    {
        var key = "FA#" + k;
        if (sym.TryGetValue(key, out var parts) && parts.Count > 0)
            return parts.TryGetValue("", out var w) ? w : Assemble(kind, parts, key);
        Residue($"argument slot {k} not set");
        return new EOpaque("FA " + k);
    }

    /// <summary>JBigDecimalOps.pow(BigDecimal, int, MathContext), J3S's JRE emulation of BigDecimal.pow(n, mc).</summary>
    public static bool IsJrePow(FunctionDef f) =>
        f.Owner is { Kind: ClassKind.Emulation } o && JdkMap.EmulationName(o) == "JBigDecimalOps" && f.MethodName == "pow" && f.Params.Count == 3;

    /// <summary>Emits the call; returns the expression holding the result (a temporary), or null for void.</summary>
    Expr? EmitCall(FunctionDef callee, Expr? recv, List<Expr> args)
    {
        Flush();
        args = args.Select(Materialize).ToList();
        // constructor: on an allocated object it is new C(args); on this it is super(...) or this(...)
        if (callee.IsCtor)
        {
            if (recv is EVar rv && allocated.ContainsKey(rv.V) || recv is EAlloc)
            {
                var v = recv is EVar ev ? ev.V : ((EAlloc)recv).V;
                allocated.Remove(v);
                Emit(new SAssign(new EVar(v), new ENew { Class = callee.Owner, Ctor = callee, Args = args }));
                return null;
            }
            if (recv is EThis)
            {
                Emit(new SExpr(new ECall { Kind = CallKind.Super, Fn = callee, Name = ReferenceEquals(callee.Owner, Fn.Owner) ? "this" : "super", Args = args }));
                return null;
            }
            Residue("constructor call on " + (recv is null ? "nothing" : IrText.Show(recv)));
        }
        if (recv is not null) recv = Materialize(recv);
        // a lambda called through its functional interface (closure dispatch, or a call on a functional variable)
        if (callee.IsLambda && (samReceiver ?? recv) is { } samRecv)
        {
            var sam = new ECall { Kind = CallKind.Jdk, Name = SamName(callee), Recv = samRecv, Args = args, Type = callee.Ret is { Kind: var sk } && sk is not (VKind.Ref or VKind.Any) ? JType.ForKind(sk) : null };
            if (callee.Ret is null) { Emit(new SExpr(sam)); return null; }
            var st = NewTemp(callee.Ret.Kind);
            Emit(new SAssign(new EVar(st), sam));
            results[callee.Ret.CobolName] = new EVar(st);
            return new EVar(st);
        }
        var call = new ECall
        {
            Kind = callee.IsStatic ? CallKind.Static : CallKind.Instance,
            Fn = callee,
            Name = callee.MethodName,
            Recv = callee.IsStatic ? null : recv,
            Args = args,
            Type = callee.Ret is { Kind: var rk } && rk is not (VKind.Ref or VKind.Any or VKind.Unknown) ? JType.ForKind(rk) : null,
        };
        if (callee.Ret is null)
        {
            Emit(new SExpr(call));
            return null;
        }
        // the J3S JRE pow(int, MathContext) emulation: its result may be a stored decimal, its operations are FSJBDEC's
        // (DecimalLimit.pow at the emulation's sites, Rewrite)
        if (IsJrePow(callee))
        {
            if (M.DecimalInline) Residue("decimal capacity: the JRE pow emulation over a runtime without a big-value store has no checked form");
            if (M.JrePowSites.Count < 4) Residue("decimal capacity: the JRE pow emulation's sites (PW, MU, SS, DM) were not found");
            M.LimitedReturns.TryAdd(KeyOf(callee), 0);
            M.CheckedFunctions.TryAdd(FnKey, 0);
            foreach (var jrs in M.JrePowSites.Values) M.DecimalLimitSites.TryAdd(jrs, 0);
        }
        var t = NewTemp(callee.Ret.Kind);
        Emit(new SAssign(new EVar(t), call));
        var res = new EVar(t);
        results[callee.Ret.CobolName] = res;
        return res;
    }

    // ================================================================== virtual dispatch

    /// <summary>
    /// EVALUATE x-C WHEN id1 ... WHEN idn: one branch per receiver class. When every branch makes the same call
    /// (by method name, same arguments, same result target), it is one virtual call.
    /// </summary>
    bool TryDispatch(Stmt s, Node subj, List<Stmt> whens)
    {
        var sit = P.ItemsById.GetValueOrDefault(subj.Id ?? -1);
        if (sit is null) return false;
        var sloc = Resolve(subj);
        if (PartOf(sloc, sit) != "C") return false;
        var recvExpr = Read(sloc, "", subj);
        Flush();
        // every branch a closure class: one call of the functional interface, lifted from the first branch
        var ids = whens.Where(w => w.Str("other") != "true").Select(w => w.Get("cond") is { K: "rel", R: { K: "num" } n } ? int.Parse(n.V!) : -1).ToList();
        if (ids.Count > 0 && ids.All(i => M.ClassById.TryGetValue(i, out var cc) && cc.Kind == ClassKind.Closure))
        {
            samReceiver = recvExpr;
            Block(whens[0].Blocks?.GetValueOrDefault("TRUE") ?? []);
            samReceiver = null;
            Flush();
            return true;
        }
        var before = Snapshot();
        var startLow = Low.Count;
        var shapes = new List<(List<St> Code, Dictionary<string, Dictionary<string, Expr>> State)>();
        foreach (var w in whens)
        {
            bool other = w.Str("other") == "true";
            var body = w.Blocks?.GetValueOrDefault("TRUE") ?? [];
            if (other) continue;
            Restore(before);
            int mark = Low.Count;
            Block(body);
            Flush();
            shapes.Add((Low.GetRange(mark, Low.Count - mark), Snapshot()));
            Low.RemoveRange(mark, Low.Count - mark);
        }
        if (shapes.Count == 0) { Restore(before); return false; }
        // normalise: the method name of every call; field reads of the receiver become accessor calls
        string? Sig(List<St> code)
        {
            var parts = new List<string>();
            foreach (var st in code)
            {
                switch (st)
                {
                    case SAssign { Rhs: ECall c } a when c.Fn is not null:
                        parts.Add($"{IrText.Show(a.Lhs)}=call {c.Name}({string.Join(",", c.Args.Select(IrText.Show))})");
                        break;
                    case SAssign { Rhs: EField f } a when Same(f.Obj, recvExpr):
                        parts.Add($"{IrText.Show(a.Lhs)}=call {Naming.LowerFirst(f.F.Name)}()");
                        break;
                    case SExpr { E: ECall c } when c.Fn is not null:
                        parts.Add($"call {c.Name}({string.Join(",", c.Args.Select(IrText.Show))})");
                        break;
                    case SAssign a:
                        parts.Add(IrText.Show(a.Lhs) + "=" + IrText.Show(a.Rhs));
                        break;
                    default:
                        return null;
                }
            }
            return string.Join(";", parts);
        }
        // temporaries differ per branch: compare the call names and the final assignment targets only
        var names = shapes.Select(sh => sh.Code.Select(st => st switch
        {
            SAssign { Rhs: ECall c } when c.Fn is not null => "call " + c.Name + "/" + c.Args.Count,
            SExpr { E: ECall c } when c.Fn is not null => "call " + c.Name + "/" + c.Args.Count,
            SAssign { Rhs: EField f } when Same(f.Obj, recvExpr) => "field " + f.F.Name,
            _ => "",
        }).Where(x => x.Length > 0).ToList()).ToList();
        var callNames = names.Select(n => n.FirstOrDefault(x => x.StartsWith("call ")) ?? n.FirstOrDefault()).ToList();
        string? method = callNames.FirstOrDefault(n => n is not null && n.StartsWith("call "))?[5..];
        bool uniform = method is not null && callNames.All(n => n is not null && (n == "call " + method || n.StartsWith("field ")));
        if (!uniform)
        {
            // a method of Object over every class (J3S inlines hashCode of the value classes, identity hash where no class
            // overrides it): one call, every branch storing its result in the same place
            var called = names.SelectMany(n => n).Where(x => x.StartsWith("call ")).Select(x => x[5..]).Distinct().ToList();
            var lasts = shapes.Select(sh => sh.Code.LastOrDefault() is SAssign la ? IrText.Show(la.Lhs) : null).ToList();
            if (shapes.Count >= 4 && called is ["hashCode/0" or "toString/0"] && lasts.All(l => l is not null && l == lasts[0]))
            {
                var objMethod = called[0][..called[0].IndexOf('/')];
                var target = ((SAssign)shapes[0].Code[^1]).Lhs;
                Restore(before);
                Low.Add(new SAssign(target, new ECall { Kind = CallKind.Jdk, Name = objMethod, Recv = recvExpr, Type = objMethod == "hashCode" ? JType.Int : JType.String }));
                return true;
            }
            // not one virtual call: an instanceof chain
            Restore(before);
            return false;
        }
        // use the first calling branch as the shape; its call becomes virtual on the receiver
        var proto = shapes.First(sh => sh.Code.Any(st => st is SAssign { Rhs: ECall { Fn: not null } } or SExpr { E: ECall { Fn: not null } }));
        // the classes whose implementation J3S inlined as a field read (a getter): they get the method
        var fieldImpls = new List<(ClassDef, FieldDef)>();
        for (int k = 0; k < shapes.Count && k < ids.Count; k++)
            if (shapes[k].Code.FirstOrDefault(st => st is SAssign { Rhs: EField fe } && Same(fe.Obj, recvExpr)) is SAssign { Rhs: EField fr }
                && !shapes[k].Code.Any(st => st is SAssign { Rhs: ECall { Fn: not null } }) && M.ClassById.TryGetValue(ids[k], out var fc))
                fieldImpls.Add((fc, fr.F));
        foreach (var st in proto.Code)
        {
            if (st is SAssign { Rhs: ECall c } a && c.Fn is not null) a.Rhs = new ECall { Kind = CallKind.Instance, Fn = c.Fn, Name = c.Name, Recv = recvExpr, Args = c.Args, Type = c.Type, Virtual = true, FieldImpls = fieldImpls.Count > 0 ? fieldImpls : null };
            if (st is SExpr { E: ECall c2 } e && c2.Fn is not null) e.E = new ECall { Kind = CallKind.Instance, Fn = c2.Fn, Name = c2.Name, Recv = recvExpr, Args = c2.Args, Virtual = true };
            Low.Add(st);
        }
        Restore(proto.State);
        return true;
    }

    // ================================================================== records of job programs

    Expr RecordRead(DataItem it, Node? n) => new ERecordField(it) { Type = RecordType(it) };

    static JType RecordType(DataItem it)
    {
        if (it.Category == Category.Numeric) return AppModel.FieldType(it);
        return JType.String;
    }

    void RecordWrite(DataItem it, Expr value)
    {
        Flush();
        Emit(new SAssign(new ERecordField(it) { Type = RecordType(it) }, Materialize(value)));
    }
}
