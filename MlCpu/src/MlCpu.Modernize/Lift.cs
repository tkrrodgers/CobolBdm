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
    /// <summary>What the last MOVE to JX-AT named (a field or a step of the job adapter): the site of JX-JOBFAIL.</summary>
    string? jobFailAt;
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
        // parameters in order (one a caller passes a stored decimal to is checked: the model's taint sets)
        foreach (var ps in Fn.Params)
        {
            var v = VarOf(ps);
            if (M.LimitedParams.ContainsKey(FnKey + "#" + R.Params.Count)) Taint(v);
            R.Params.Add(v);
        }
        var paras = P.Model.Paragraphs;
        // a job function's reject paragraph is the handler the application layer wraps around it
        if (Fn.IsJob) skipParagraphs.Add("F" + Fn.Id[1..] + "-REJECT");
        var tries = FindTries();
        foreach (var t in tries) tryHandlers.Add(paras[t.HandlerPara].Name);
        LiftParagraphs(Fn.FirstParagraph, 0, Fn.LastParagraph, tries);
        Flush();
        Fold.Returns(R.Low, Fn.ExitParagraph, Fn.Ret is null ? null : ResultVar());
        Fold.DuplicateExits(R.Low);
        Fold.Webs(R.Low, R.Vars);
    }

    /// <summary>
    /// Lifts paragraphs first..last (the first from its statement firstStmt on); a try statement among them (one of
    /// <paramref name="tries"/> that starts here and ends before last) becomes an STry, its body lifted the same way, so
    /// nested try statements nest.
    /// </summary>
    void LiftParagraphs(int first, int firstStmt, int last, List<TryRegion> tries)
    {
        var paras = P.Model.Paragraphs;
        for (int i = first; i <= last; i++)
        {
            var para = paras[i];
            int from = i == first ? firstStmt : 0;
            Flush();
            // a try statement: its body from the start statement to the handler, each catch clause from its paragraph on
            if (tries.Where(t => t.StartPara == i && t.StartStmt >= from && t.HandlerPara <= last + 1)
                    .OrderBy(t => t.StartStmt).ThenByDescending(t => t.HandlerPara).FirstOrDefault() is { } tr)
            {
                if (i > Fn.FirstParagraph && from == 0) Emit(new LLabel(para.Name));
                Block(para.Statements.Skip(from).Take(tr.StartStmt - from).ToList());
                Flush();
                var inner = tries.Where(t => !ReferenceEquals(t, tr)).ToList();
                int start = i;
                if (tr.Clauses.Count == 0 && tr.Trap is null)
                {
                    // a handler that never catches: the body in place, the handler and its dead catch bodies skipped
                    LiftParagraphs(start, tr.StartStmt, tr.HandlerPara - 1, inner);
                    i = tr.JoinPara - 1;
                    continue;
                }
                var tryBody = LiftRegion(() => LiftParagraphs(start, tr.StartStmt, tr.HandlerPara - 1, inner), tr.Join);
                var siteAfterBody = site;
                var st = new STry(tryBody) { Line = line };
                foreach (var c in tr.Clauses)
                {
                    var e = NewNamed("e", VKind.Ref);
                    e.Type = c.Type;
                    var catchBody = LiftRegion(() =>
                    {
                        for (int k = c.FirstPara; k < c.EndPara; k++) { Flush(); if (k > c.FirstPara) Emit(new LLabel(paras[k].Name)); Block(paras[k].Statements); }
                    }, tr.Join);
                    st.Catches.Add((c.Type, e, catchBody));
                }
                if (tr.Trap is { } trap)
                {
                    // a try-with-resources handler: J3S cannot close the resource and keep the exception (suppressed), so
                    // an exception with the resource open stops the run (abend 31 at the trap's site); else it propagates
                    var e = NewNamed("e", VKind.Ref);
                    e.Type = JType.Of("java.lang.Exception");
                    var catchBody = LiftRegion(() => { Block([trap]); Flush(); Emit(new SThrow(new EVar(e))); }, tr.Join);
                    st.Catches.Add((e.Type, e, catchBody));
                }
                // clauses with the same body, the later catching a superclass of the earlier: the later alone (a
                // RuntimeException handler followed by an Exception handler that both stop the run)
                for (int ci = 0; ci + 1 < st.Catches.Count; ci++)
                    if (CatchSuper(st.Catches[ci].Type.Name, st.Catches[ci + 1].Type.Name)
                        && IrText.Show(st.Catches[ci].Body, 0) == IrText.Show(st.Catches[ci + 1].Body, 0)
                        && !Passes.All(st.Catches[ci].Body).SelectMany(Fold.Exprs).Any(x => Fold.Mentions(x, st.Catches[ci].V)))
                    {
                        st.Catches.RemoveAt(ci);
                        ci--;
                    }
                // the statements after the try continue the try body's site (a handler's trap site is its own)
                site = siteAfterBody;
                Low.Add(st);
                i = tr.JoinPara - 1;
                continue;
            }
            if (skipParagraphs.Contains(para.Name)) { Emit(new LLabel(para.Name)); Emit(new SThrow(new ENew { JdkClass = "java.lang.IllegalStateException", Args = [ELit.Str("lifted elsewhere")] })); continue; }
            if (i > Fn.FirstParagraph && from == 0) Emit(new LLabel(para.Name));
            if (IsExit(para.Name))
            {
                Emit(new SReturn(Fn.Ret is null ? null : new EVar(ResultVar())));
                continue;
            }
            Block(para.Statements.Skip(from).ToList());
        }
    }

    /// <summary>Whether the JDK exception class <paramref name="super"/> is a superclass of <paramref name="sub"/> (java.lang's hierarchy).</summary>
    static bool CatchSuper(string sub, string super)
    {
        var up = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["java.lang.RuntimeException"] = "java.lang.Exception", ["java.lang.Exception"] = "java.lang.Throwable", ["java.lang.Error"] = "java.lang.Throwable",
        };
        for (var x = up.GetValueOrDefault(sub); x is not null; x = up.GetValueOrDefault(x)) if (x == super) return true;
        return false;
    }

    bool IsExit(string para) => para.Equals(Fn.ExitParagraph, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where statements go: the function's low IR, or the region being lifted (a try body, a catch body).</summary>
    List<St>? sink;
    List<St> Low => sink ?? R.Low;
    /// <summary>The regions being lifted, outermost first.</summary>
    readonly List<List<St>> regionStack = [];
    /// <summary>The handler paragraphs of the function's try statements: a GO TO one after a raise is the throw itself.</summary>
    readonly HashSet<string> tryHandlers = new(StringComparer.OrdinalIgnoreCase);

    sealed record CatchClause(JType Type, int FirstPara, int EndPara);

    /// <summary>
    /// A try statement: its body from StartPara/StartStmt to the handler paragraph; the catch clauses (each a typed catch
    /// body from its paragraph on); or the trap of a try-with-resources handler (Trap: the IF that stops the run when the
    /// resource is open); the join is where the statement completes normally.
    /// </summary>
    sealed record TryRegion(int StartPara, int StartStmt, int HandlerPara, int JoinPara, string Join, List<CatchClause> Clauses, Stmt? Trap, string Next);

    /// <summary>
    /// J3S's try/catch: the guards of the try body GO TO a handler paragraph. A catch handler tests the exception's class
    /// clause by clause (PERFORM JX-ISA-n, IF JX-ISA = 'Y' GO TO the catch body) and goes on to the outer handler or the
    /// exit; a try-with-resources handler stops the run if the resource is open (the J3S trap) and goes on the same way.
    /// The try body before the handler ends with GO TO the join; a catch body runs from its paragraph to the next one or
    /// the join. JX-ISA-n names the caught class (FSJXP's comment) and lists the estate's classes that are one; a clause no
    /// class of the estate matches never catches (its body is dead). An outer try starts where its first inner try does.
    /// </summary>
    List<TryRegion> FindTries()
    {
        var paras = P.Model.Paragraphs;
        const StringComparison ic = StringComparison.OrdinalIgnoreCase;
        var found = new List<(int H, int JoinPara, string Join, List<(string Isa, int K)> Isas, Stmt? Trap, string Next)>();
        for (int h = Fn.FirstParagraph + 1; h <= Fn.LastParagraph; h++)
        {
            var hs = paras[h].Statements;
            var before = paras[h - 1].Statements.LastOrDefault();
            if (before is not { Verb: "GO TO" } || before.L?.GetValueOrDefault("targets") is not [{ V: { } join }] || !P.ParagraphIndex.TryGetValue(join, out var joinPara) || joinPara <= h) continue;
            int idx = 0;
            var isas = new List<(string, int)>();
            while (idx + 1 < hs.Count && hs[idx] is { Verb: "PERFORM" } isa && isa.Str("target") is { } isaName && isaName.StartsWith("JX-ISA-", ic)
                   && hs[idx + 1] is { Verb: "IF" } test && test.Blocks?.GetValueOrDefault("TRUE") is [{ Verb: "GO TO" } toCatch]
                   && toCatch.L?.GetValueOrDefault("targets") is [{ V: { } catchName }] && P.ParagraphIndex.TryGetValue(catchName, out var k) && k > h && k < joinPara)
            {
                isas.Add((isaName, k));
                idx += 2;
            }
            Stmt? trap = null;
            if (isas.Count == 0 && idx < hs.Count && hs[idx] is { Verb: "IF" } tif && tif.Blocks?.GetValueOrDefault("FALSE") is null or { Count: 0 }
                && tif.Blocks?.GetValueOrDefault("TRUE") is { Count: > 0 } tb && tb.Any(b => b.Verb == "PERFORM" && b.Str("target")?.Equals("JX-UNSUPPORTED", ic) == true)
                && tb.All(b => b.Verb == "PERFORM" || b.Verb == "MOVE" && Model.DstName(b)?.Equals("JX-AT", ic) == true))
            {
                trap = tif;
                idx++;
            }
            if (isas.Count == 0 && trap is null) continue;
            if (idx != hs.Count - 1 || hs[idx] is not { Verb: "GO TO" } nx || nx.L?.GetValueOrDefault("targets") is not [{ V: { } next }]) continue;
            found.Add((h, joinPara, join, isas, trap, next));
        }
        if (found.Count == 0) return [];
        // the paragraphs of handlers and catch bodies: their GO TOs are the handlers' own, not guards of a try body
        var handlerParas = new HashSet<int>();
        foreach (var f in found) { handlerParas.Add(f.H); foreach (var (_, k) in f.Isas) for (int x = k; x < f.JoinPara && !found.Any(o => o.H == x); x++) handlerParas.Add(x); }
        var starts = new Dictionary<int, (int P, int S)>();
        foreach (var f in found)
        {
            for (int p = Fn.FirstParagraph; p < f.H && !starts.ContainsKey(f.H); p++)
            {
                if (handlerParas.Contains(p)) continue;
                for (int k = 0; k < paras[p].Statements.Count; k++)
                    if (Model.Deep([paras[p].Statements[k]]).Any(x => x.Verb == "GO TO" && x.L?.GetValueOrDefault("targets") is [{ V: { } gt }] && gt.Equals(paras[f.H].Name, ic)))
                    {
                        int ss = k;
                        // the call before a propagation guard is in the try body
                        if (paras[p].Statements[k].Get("cond") is { K: "rel", L: { K: "ref", V: { } cv } } && cv.Equals("JX-C", ic))
                            for (int b = k - 1; b >= 0; b--)
                                if (paras[p].Statements[b].Verb is "CALL" or "PERFORM") { ss = b; break; }
                        starts[f.H] = (p, ss);
                        break;
                    }
            }
        }
        // an outer handler (the one an inner handler goes on to) starts no later than the inner try
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var f in found)
                if (starts.TryGetValue(f.H, out var si) && P.ParagraphIndex.TryGetValue(f.Next, out var outer) && found.Any(o => o.H == outer)
                    && (!starts.TryGetValue(outer, out var so) || (si.P, si.S).CompareTo((so.P, so.S)) < 0))
                {
                    starts[outer] = si;
                    changed = true;
                }
        }
        var res = new List<TryRegion>();
        foreach (var f in found)
        {
            if (!starts.TryGetValue(f.H, out var s)) continue;
            var clauses = new List<CatchClause>();
            var ks = f.Isas.Select(x => x.K).Order().ToList();
            foreach (var (isaName, k) in f.Isas)
            {
                var end = ks.FirstOrDefault(x => x > k, f.JoinPara);
                var ids = new List<JType>();
                // WHEN 39 WHEN 36 ... (one body): an OR of the class ids
                static IEnumerable<string> ClassIds(Node? c) => c switch
                {
                    { K: "or" } o => ClassIds(o.L).Concat(ClassIds(o.R)),
                    { K: "rel", R: { K: "num", V: { } v } } => [v],
                    _ => [],
                };
                var listed = 0;
                if (paras.FirstOrDefault(x => x.Name.Equals(isaName, ic)) is { } isaPara)
                    foreach (var w in Model.Deep(isaPara.Statements).Where(x => x.Verb == "WHEN"))
                        foreach (var idText in ClassIds(w.Get("cond")))
                        {
                            listed++;
                            if (M.ClassIdType(int.Parse(idText, System.Globalization.CultureInfo.InvariantCulture)) is { } ct) ids.Add(ct);
                        }
                // a clause that lists classes the model does not know cannot be lifted (fail closed)
                if (listed > 0 && ids.Count < listed) Residue($"catch clause {isaName}: {listed - ids.Count} unknown class ids");
                // no class of the estate is one: the clause never catches
                if (ids.Count == 0) continue;
                var type = M.CatchTypes.TryGetValue(isaName, out var named) ? JType.Of(named)
                    : ids.Count == 1 ? ids[0] : JType.Of(string.Join(" | ", ids.Select(t => t.Simple)));
                clauses.Add(new CatchClause(type, k, end));
            }
            // no clause catches and no trap: a handler that only goes on (its catch bodies are dead); the body is lifted in place
            res.Add(new TryRegion(s.P, s.S, f.H, f.JoinPara, f.Join, clauses, f.Trap, f.Next));
        }
        return res;
    }

    /// <summary>Lifts a region into a low IR of its own and structures it: GO TO the join leaves it, GO TO the exit returns.</summary>
    List<St> LiftRegion(Action lift, string join)
    {
        var saved = sink;
        var buf = new List<St>();
        sink = buf;
        regionStack.Add(buf);
        lift();
        Flush();
        regionStack.RemoveAt(regionStack.Count - 1);
        sink = saved;
        // the region's temporaries split into webs as the function's are (Fold.Webs over the low IR, which sees a structured
        // try as one statement): J3S reuses a temporary slot for values of other types on either side of a try's boundary,
        // and a statement that began before the try (an allocation whose constructor is guarded) ends inside it
        var outside = new HashSet<Var>();
        foreach (var s in Passes.All(R.Low).Concat(regionStack.SelectMany(Passes.All)))
        {
            if (s is SAssign { Lhs: EVar { V: var dv } }) outside.Add(dv);
            foreach (var e in Fold.Exprs(s)) foreach (var d in DeepOf(e)) if (d is EVar ev) outside.Add(ev.V);
        }
        Fold.Webs(buf, R.Vars, outside, region: true);
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
        // a copy of a capacity-checked decimal result is one too; it flows into parameters, fields and results (the model's
        // taint sets); any other way out of the function is recorded (DecimalLimitEscapes)
        NoteLimited(s);
        Low.Add(s);
    }

    void NoteLimited(St s)
    {
        // the estate's calls and constructors: an argument that may be a stored decimal taints the callee's parameter
        foreach (var e in Fold.Exprs(s).SelectMany(DeepOf))
        {
            var (fn, args) = e switch { ECall { Fn: { } cf, Kind: not CallKind.Jdk and not CallKind.JdkStatic } c => (cf, c.Args), ENew { Ctor: { } ct } n => (ct, n.Args), _ => ((FunctionDef?)null, (List<Expr>?)null) };
            if (fn is null || args is null) continue;
            for (int i = 0; i < args.Count; i++)
                if (Limited(args[i])) M.LimitedParams.TryAdd(KeyOf(fn) + "#" + i, 0);
        }
        switch (s)
        {
            case SAssign { Lhs: EVar lv, Rhs: var rv } when TaintedValue(rv): Taint(lv.V); return;
            // the operation itself (a BigDecimal method on the value): BigDecimalOp checks it
            case SAssign { Lhs: EVar ov, Rhs: ECall { Kind: CallKind.Jdk } jc } when jc.Type == JType.BigDecimal || jc.Type?.Name == JType.BigDecimal.Name: limitVars.Remove(ov.V); return;
            // a temporary given a value that is not one is not one any more (straight-line code; Taint)
            case SAssign { Lhs: EVar kv, Rhs: var kr } when !Limited(kr): limitVars.Remove(kv.V); return;
            case SAssign { Lhs: EField lf, Rhs: var fr } when Limited(fr): M.LimitedFields.TryAdd(FieldKey(lf.F), 0); M.CheckedFunctions.TryAdd(FnKey, 0); return;
            case SAssign { Lhs: EStatic ls, Rhs: var sr } when Limited(sr): M.LimitedFields.TryAdd(FieldKey(ls.F), 0); M.CheckedFunctions.TryAdd(FnKey, 0); return;
            case SReturn { E: { } re } when Limited(re): M.LimitedReturns.TryAdd(FnKey, 0); M.CheckedFunctions.TryAdd(FnKey, 0); return;
            case SAssign { Lhs: EVar, Rhs: ECall { Fn: not null } or ENew }: return;
            case SExpr { E: ECall { Fn: not null } or ENew }: return;
            case SAssign { Lhs: EVar, Rhs: var rhs } when Limited(rhs): M.DecimalLimitEscapes.TryAdd($"{P.Id} {Fn.Id} {site}: an argument or operand of {IrText.Show(rhs)}", 0); return;
            case SAssign { Rhs: var srhs } when Limited(srhs): M.DecimalLimitEscapes.TryAdd($"{P.Id} {Fn.Id} {site}: stored, {IrText.Show(srhs)}", 0); return;
            case SExpr { E: var se } when Limited(se): M.DecimalLimitEscapes.TryAdd($"{P.Id} {Fn.Id} {site}: an argument of {IrText.Show(se)}", 0); return;
        }
    }

    static IEnumerable<Expr> DeepOf(Expr e)
    {
        yield return e;
        foreach (var c in e.Children)
            foreach (var d in DeepOf(c)) yield return d;
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
