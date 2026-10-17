using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

/// <summary>A lifted function: variables, the low IR (labels and gotos) and, after structuring, the body.</summary>
public sealed class Lifted
{
    public required FunctionDef Fn { get; init; }
    public List<Var> Params { get; } = [];
    public List<Var> Vars { get; } = [];
    public List<St> Low { get; } = [];
    public List<St>? Body { get; set; }
    public List<string> Residue { get; } = [];
    public JType? ReturnType { get; set; }
    /// <summary>For a lambda: the captured fields of the closure, as variables.</summary>
    public Dictionary<FieldDef, Var> Captured { get; } = [];
}

/// <summary>Where a COBOL data reference points, seen through the J3S idiom pack.</summary>
abstract record Loc;
sealed record LVar(Var V) : Loc;
sealed record LForeign(FunctionDef Fn, FrameSlot Slot) : Loc;
sealed record LFa(int K, string View) : Loc;
sealed record LField(Expr Obj, FieldDef F) : Loc;
sealed record LStatic(FieldDef F) : Loc;
sealed record LReg(string Name) : Loc;
/// <summary>A field of a job or driver record; with a reference modification (start, length; 0 when none).</summary>
sealed record LRecord(DataItem Item, int RefStart = 0, int RefLen = 0) : Loc;
sealed record LUnknown(string Why) : Loc;

/// <summary>
/// Lifts one J3S function into low IR. A symbolic machine: the runtime interfaces (argument slots FA, the BigDecimal
/// interface JB, the date interface JD, allocation JA, ...) hold expressions, not bytes; writes to real storage
/// (frame variables, object fields, statics) become assignments; guards that only re-check what Java checks itself
/// (null, receiver class, exception status, size) are dropped.
/// </summary>
public sealed partial class FunctionLifter
{
    readonly Model M;
    readonly FunctionDef Fn;
    readonly SourceProgram P;
    readonly Lifted R;
    readonly Dictionary<FrameSlot, Var> slotVar = [];
    /// <summary>Symbolic store of non-storage locations (argument slots, registers, foreign frames): key → part → value.</summary>
    Dictionary<string, Dictionary<string, Expr>> sym = new(StringComparer.Ordinal);
    /// <summary>Partial writes to real storage waiting for their other parts.</summary>
    Dictionary<string, (Loc Loc, VKind Kind, Dictionary<string, Expr> Parts)> pending = new(StringComparer.Ordinal);
    /// <summary>Result variables of calls, by callee RET slot.</summary>
    readonly Dictionary<string, Expr> results = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Objects allocated and not yet constructed: var → (class id, class).</summary>
    readonly Dictionary<Var, (int Id, ClassDef? Cls, PoolDef? Pool)> allocated = [];
    /// <summary>Closure objects being captured.</summary>
    readonly Dictionary<Var, ELambda> closures = [];
    string? site;
    int line;
    int tempNo;

    public FunctionLifter(Model m, FunctionDef fn)
    {
        M = m; Fn = fn; P = fn.Program;
        R = new Lifted { Fn = fn };
    }

    public static Lifted Lift(Model m, FunctionDef fn)
    {
        var l = new FunctionLifter(m, fn);
        l.Run();
        return l.R;
    }

    // ================================================================== driver

    void Run()
    {
        // parameters in order
        foreach (var ps in Fn.Params)
        {
            var v = VarOf(ps);
            R.Params.Add(v);
        }
        var paras = P.Model.Paragraphs;
        // a job function's reject paragraph is the handler the application layer wraps around it
        if (Fn.IsJob) skipParagraphs.Add("F" + Fn.Id[1..] + "-REJECT");
        var tries = FindTries();
        for (int i = Fn.FirstParagraph; i <= Fn.LastParagraph; i++)
        {
            var para = paras[i];
            Flush();
            // a try statement: its body from the start statement to the handler, the catch body up to the join
            if (tries.FirstOrDefault(t => t.StartPara == i) is { } tr)
            {
                if (i > Fn.FirstParagraph) Emit(new LLabel(para.Name));
                Block(para.Statements.Take(tr.StartStmt).ToList());
                Flush();
                var tryBody = LiftRegion(() =>
                {
                    Block(para.Statements.Skip(tr.StartStmt).ToList());
                    for (int k = i + 1; k < tr.HandlerPara; k++) { Flush(); Emit(new LLabel(paras[k].Name)); Block(paras[k].Statements); }
                }, tr.Join);
                var e = NewNamed("e", VKind.Ref);
                e.Type = tr.Types.Count == 1 ? tr.Types[0] : JType.Of(string.Join(" | ", tr.Types.Select(t => t.Simple)));
                var catchBody = LiftRegion(() =>
                {
                    for (int k = tr.CatchPara; k < tr.JoinPara; k++) { Flush(); if (k > tr.CatchPara) Emit(new LLabel(paras[k].Name)); Block(paras[k].Statements); }
                }, tr.Join);
                var st = new STry(tryBody) { Line = line };
                st.Catches.Add((e.Type, e, catchBody));
                Low.Add(st);
                i = tr.JoinPara - 1;
                continue;
            }
            if (skipParagraphs.Contains(para.Name)) { Emit(new LLabel(para.Name)); Emit(new SThrow(new ENew { JdkClass = "java.lang.IllegalStateException", Args = [ELit.Str("lifted elsewhere")] })); continue; }
            if (i > Fn.FirstParagraph) Emit(new LLabel(para.Name));
            if (IsExit(para.Name))
            {
                Emit(new SReturn(Fn.Ret is null ? null : new EVar(ResultVar())));
                continue;
            }
            Block(para.Statements);
        }
        Flush();
        Fold.Returns(R.Low, Fn.ExitParagraph, Fn.Ret is null ? null : ResultVar());
        Fold.DuplicateExits(R.Low);
        Fold.Webs(R.Low, R.Vars);
    }

    bool IsExit(string para) => para.Equals(Fn.ExitParagraph, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where statements go: the function's low IR, or the region being lifted (a try body, a catch body).</summary>
    List<St>? sink;
    List<St> Low => sink ?? R.Low;

    sealed record TryRegion(int StartPara, int StartStmt, int HandlerPara, int CatchPara, int JoinPara, string Join, List<JType> Types);

    /// <summary>
    /// J3S's try/catch: the guards of the try body GO TO a handler paragraph (PERFORM JX-ISA-n, IF JX-ISA = 'Y' GO TO the
    /// catch body, else on to the outer handler or the exit); the try body before the handler ends with GO TO the join;
    /// the catch body runs from its paragraph to the join. JX-ISA-n names the caught classes.
    /// </summary>
    List<TryRegion> FindTries()
    {
        var res = new List<TryRegion>();
        var paras = P.Model.Paragraphs;
        const StringComparison ic = StringComparison.OrdinalIgnoreCase;
        for (int h = Fn.FirstParagraph + 1; h <= Fn.LastParagraph; h++)
        {
            var hs = paras[h].Statements;
            if (hs is not [{ Verb: "PERFORM" } isa, { Verb: "IF" } test, ..] || isa.Str("target") is not { } isaName || !isaName.StartsWith("JX-ISA-", ic)) continue;
            if (test.Blocks?.GetValueOrDefault("TRUE") is not [{ Verb: "GO TO" } toCatch] || toCatch.L?.GetValueOrDefault("targets") is not [{ V: { } catchName }]) continue;
            if (!P.ParagraphIndex.TryGetValue(catchName, out var catchPara)) continue;
            var before = paras[h - 1].Statements.LastOrDefault();
            if (before is not { Verb: "GO TO" } || before.L?.GetValueOrDefault("targets") is not [{ V: { } join }] || !P.ParagraphIndex.TryGetValue(join, out var joinPara) || joinPara <= catchPara) continue;
            var types = new List<JType>();
            if (paras.FirstOrDefault(x => x.Name.Equals(isaName, ic)) is { } isaPara)
                foreach (var w in Model.Deep(isaPara.Statements).Where(x => x.Verb == "WHEN"))
                    if (w.Get("cond") is { K: "rel", R: { K: "num", V: { } idText } } && M.ClassIdType(int.Parse(idText, System.Globalization.CultureInfo.InvariantCulture)) is { } ct) types.Add(ct);
            if (types.Count == 0) continue;
            // the start: the first statement whose guard goes to the handler (the call before it, for a propagation guard)
            int sp = -1, ss = -1;
            for (int p = Fn.FirstParagraph; p < h && sp < 0; p++)
                for (int k = 0; k < paras[p].Statements.Count && sp < 0; k++)
                    if (Model.Deep([paras[p].Statements[k]]).Any(x => x.Verb == "GO TO" && x.L?.GetValueOrDefault("targets") is [{ V: { } gt }] && gt.Equals(paras[h].Name, ic)))
                    {
                        sp = p;
                        ss = k;
                        if (paras[p].Statements[k].Get("cond") is { K: "rel", L: { K: "ref", V: { } cv } } && cv.Equals("JX-C", ic))
                            for (int b = k - 1; b >= 0; b--)
                                if (paras[p].Statements[b].Verb is "CALL" or "PERFORM") { ss = b; break; }
                    }
            if (sp < 0) continue;
            res.Add(new TryRegion(sp, ss, h, catchPara, joinPara, join, types));
        }
        return res;
    }

    /// <summary>Lifts a region into a low IR of its own and structures it: GO TO the join leaves it, GO TO the exit returns.</summary>
    List<St> LiftRegion(Action lift, string join)
    {
        var saved = sink;
        var buf = new List<St>();
        sink = buf;
        lift();
        Flush();
        sink = saved;
        for (int k = 0; k < buf.Count; k++)
        {
            Expr? ret = Fn.Ret is null ? null : new EVar(ResultVar());
            if (buf[k] is LGoto { Target: var t } && IsExit(t)) buf[k] = new SReturn(ret) { Line = buf[k].Line };
            else if (buf[k] is LCondGoto { Target: var ct } cg && IsExit(ct)) buf[k] = new SIf(cg.C, [new SReturn(ret)], []) { Line = cg.Line };
        }
        buf.Add(new LLabel(join));
        var probs = new List<string>();
        var body = Structurer.Run(buf, probs, Fn.Id);
        foreach (var p in probs) Residue("try region: " + p);
        Passes.FixLoops(body);
        return body;
    }

    void Emit(St s)
    {
        if (s.Line == 0) s.Line = line;
        if (s is SAssign or SExpr or SThrow or SReturn) s.Site ??= site;
        Low.Add(s);
    }

    void Residue(string what)
    {
        R.Residue.Add($"{P.Id}:{line} {Fn.Id} {what}");
    }

    Var NewTemp(VKind kind, JType? type = null)
    {
        var v = new Var { Name = "t" + (++tempNo), Kind = kind, Type = type, IsTemp = true };
        R.Vars.Add(v);
        return v;
    }

    Var VarOf(FrameSlot s)
    {
        if (slotVar.TryGetValue(s, out var v)) return v;
        string name = s.Role switch
        {
            SlotRole.Param => s.Hint is null ? "p" + s.No : Naming.Camel(s.Hint),
            SlotRole.Local => Naming.Camel(s.Hint!),
            SlotRole.Temp => "t" + s.No + "_",
            SlotRole.Index => "x" + s.No,
            _ => "this",
        };
        v = new Var { Name = name, Kind = s.Kind, IsParam = s.Role == SlotRole.Param, IsThis = s.Role == SlotRole.This, IsTemp = s.Role is SlotRole.Temp or SlotRole.Index, Slot = s };
        slotVar[s] = v;
        if (s.Role != SlotRole.This) R.Vars.Add(v);
        return v;
    }

    // ================================================================== locations

    static LRecord RecordLoc(DataItem it, Node n)
    {
        if (n.Start is not { K: "num" } s) return new LRecord(it);
        int start = int.Parse(s.V!, System.Globalization.CultureInfo.InvariantCulture);
        int len = n.Len is { K: "num" } l ? int.Parse(l.V!, System.Globalization.CultureInfo.InvariantCulture) : it.Length - start + 1;
        return new LRecord(it, start, len);
    }

    Loc Resolve(Node n)
    {
        if (n.K != "ref") return new LUnknown("not a ref: " + n.K);
        var it = n.Id is int id ? P.ItemsById.GetValueOrDefault(id) : null;
        if (it is null) return new LUnknown("unknown item " + n.V);
        var name = it.Name.ToUpperInvariant();
        // argument area
        if (name.StartsWith("FA-") && name != "FA-ENTRY" && n.Args is [{ K: "num" } k])
        {
            var view = it;
            while (view.Parent is not null && !view.Parent.Name.Equals("FA-E", StringComparison.OrdinalIgnoreCase)) view = view.Parent;
            return new LFa(int.Parse(k.V!), view.Name.ToUpperInvariant());
        }
        // frame slots (own or foreign)
        if (M.FrameSlotOf(P, it) is { } fs)
        {
            // (Fnnnn-SP + 1): the next stacked frame of the same function, the arguments of a recursive self-call
            if (ReferenceEquals(fs.Fn, Fn) && n.Args is [{ K: "bin", Op: "+" }]) return new LForeign(Fn, fs.Slot);
            if (ReferenceEquals(fs.Fn, Fn)) return fs.Slot.Role == SlotRole.This ? new LVar(ThisVar()) : new LVar(VarOf(fs.Slot));
            return new LForeign(fs.Fn, fs.Slot);
        }
        // J3S machinery inside objects (a throwable's recorded site)
        for (var up = it; up is not null; up = up.Parent) if (M.RuntimeFields.Contains(up.Name)) return new LReg("RT#" + up.Name);
        // heap fields
        if (M.HeapField(it) is { } hf)
        {
            if (n.Args is not [var sub]) return new LUnknown("heap field without one subscript: " + name);
            var obj = ObjectOfIndex(sub);
            return new LField(obj, hf.Field);
        }
        // statics
        if (M.StaticField(it) is { } sf) return new LStatic(sf.Field);
        // records of job programs and drivers (linkage records, FS-OUT, ...)
        var top = it;
        while (top.Parent is not null) top = top.Parent;
        var tn = top.Name.ToUpperInvariant();
        if (tn is "FS-CTX" or "JB" or "DT-PARMS" or "FS-HEAP" or "FS-FRAMES" || tn.StartsWith("JR-") || tn.StartsWith("JD-") || tn.StartsWith("JW-"))
            return new LReg(RegName(it));
        if (it.Section == DataSection.Linkage || tn.StartsWith("FN-") || tn.StartsWith("FS-OUT") || tn.StartsWith("FO-") || tn.StartsWith("EE-") || tn.StartsWith("EO-"))
            return RecordLoc(it, n);
        // a job function's records: every item of its program that is not J3S runtime storage (FS-EVENT-OUT)
        if (Fn.IsJob && !AppModel.IsRuntime(it)) return RecordLoc(it, n);
        return new LReg(RegName(it));
    }

    /// <summary>The register an item belongs to: the 05-level item under FS-CTX/JB/DT-PARMS (JB-A for JB-A-U).</summary>
    static string RegName(DataItem it)
    {
        var cur = it;
        while (cur.Parent is not null && cur.Parent.Parent is not null && cur.Level > 5) cur = cur.Parent;
        // registers are 10-level under 05 groups of FS-CTX (JX-C under JX): use the 10-level name; JB-A-U → JB-A
        var lvl10 = it;
        while (lvl10.Parent is not null && lvl10.Level > 10) lvl10 = lvl10.Parent;
        var nm = (lvl10.Level >= 10 ? lvl10 : it).Name.ToUpperInvariant();
        if (it.Parent?.Name.Equals("JB", StringComparison.OrdinalIgnoreCase) == true || it.Parent?.Parent?.Name.Equals("JB", StringComparison.OrdinalIgnoreCase) == true)
        {
            var j = it;
            while (j.Parent is not null && !j.Parent.Name.Equals("JB", StringComparison.OrdinalIgnoreCase)) j = j.Parent;
            return j.Name.ToUpperInvariant();
        }
        return nm;
    }

    static string PartOfReg(DataItem it, string reg)
    {
        var n = it.Name.ToUpperInvariant();
        return n == reg ? "" : n.StartsWith(reg + "-") ? n[(reg.Length + 1)..] : "";
    }

    Var? thisVar;
    Var ThisVar() => thisVar ??= new Var { Name = "this", IsThis = true, Kind = VKind.Ref };

    /// <summary>The object an index expression designates: X-I of a slot (the slot's object), or an index variable.</summary>
    Expr ObjectOfIndex(Node sub)
    {
        if (sub.K == "ref")
        {
            var loc = Resolve(sub);
            var it = P.ItemsById.GetValueOrDefault(sub.Id ?? -1);
            var part = it is null ? "" : PartOf(loc, it);
            var v = Read(loc, part, sub);
            return StripPart(v, "I");
        }
        if (sub.K == "bin" && sub.L is { K: "ref" } l)
            return ObjectOfIndex(l); // (F-SP + 1) style subscripts never index heap pools; defensive
        return new EOpaque("index " + sub.K);
    }

    static Expr StripPart(Expr v, string part) => v is EPart p && p.Part == part ? p.Whole : v;

    string PartOf(Loc loc, DataItem it) => loc switch
    {
        LVar lv when lv.V.Slot is { } s => Model.PartPath(s.Item, it),
        LVar lv when lv.V.IsThis && Fn.This is { } ts => Model.PartPath(ts.Item, it),
        LForeign f => Model.PartPath(f.Slot.Item, it),
        LField f => Model.PartPath(f.F.Item, it),
        LStatic s => Model.PartPath(s.F.Item, it),
        LFa fa => FaPart(it),
        LReg r => PartOfReg(it, r.Name),
        _ => "",
    };

    static string FaPart(DataItem it)
    {
        // FA-REF-C under FA-REF: part C; FA-BIGDEC-P-U → P.U
        var view = it;
        while (view.Parent is not null && !view.Parent.Name.Equals("FA-E", StringComparison.OrdinalIgnoreCase)) view = view.Parent;
        return Model.PartPath(view, it);
    }

    static VKind KindOfLoc(Loc loc) => loc switch
    {
        LVar v => v.V.IsThis ? VKind.Ref : v.V.Kind,
        LForeign f => f.Slot.Kind,
        LField f => f.F.Kind,
        LStatic s => s.F.Kind,
        LFa fa => FaKind(fa.View),
        _ => VKind.Unknown,
    };

    static VKind FaKind(string view) => view switch
    {
        "FA-ANY" => VKind.Any, "FA-BOOL" => VKind.Bool, "FA-INT" => VKind.Int, "FA-LONG" => VKind.Long, "FA-DBL" => VKind.Dbl, "FA-REF" => VKind.Ref,
        "FA-BIGDEC" => VKind.BigDec, "FA-DATE" => VKind.Date, "FA-MC" => VKind.Mc, "FA-JENUM" => VKind.JEnum, "FA-BOXINT" => VKind.BoxInt,
        "FA-BOXLONG" => VKind.BoxLong, "FA-BOXBOOL" => VKind.BoxBool, _ => VKind.Unknown,
    };

    static string Key(Loc loc) => loc switch
    {
        LVar v => "V#" + v.V.Id,
        LForeign f => "F#" + f.Fn.Id + "#" + f.Slot.CobolName,
        LFa fa => "FA#" + fa.K,
        LField f => "H#" + IrText.Show(f.Obj) + "#" + f.F.CobolName,
        LStatic s => "S#" + s.F.CobolName,
        LReg r => "R#" + r.Name,
        LRecord r => "D#" + r.Item.Name,
        _ => "?",
    };
}
