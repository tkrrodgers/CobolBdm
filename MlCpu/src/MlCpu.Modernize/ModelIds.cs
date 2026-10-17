using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

public sealed partial class Model
{
    /// <summary>Statements of a function's paragraph range, in order (top level of each paragraph).</summary>
    public IEnumerable<(Paragraph Para, Stmt Stmt)> TopStatements(FunctionDef fn)
    {
        var paras = fn.Program.Model.Paragraphs;
        for (int i = fn.FirstParagraph; i <= fn.LastParagraph; i++)
            foreach (var s in paras[i].Statements) yield return (paras[i], s);
    }

    public static IEnumerable<Stmt> Deep(IEnumerable<Stmt> list)
    {
        foreach (var s in list)
        {
            yield return s;
            if (s.Blocks is null) continue;
            foreach (var b in s.Blocks.Values) foreach (var c in Deep(b)) yield return c;
        }
    }

    public static string? DstName(Stmt s) => s.L?.GetValueOrDefault("dst") is [{ K: "ref" } d] ? d.V : null;
    public static Node? Dst(Stmt s) => s.L?.GetValueOrDefault("dst") is [{ K: "ref" } d] ? d : null;

    /// <summary>The class ids: allocations followed by their constructor, receiver checks, and dispatch to methods.</summary>
    void BuildClassIds()
    {
        var thisChecks = new Dictionary<ClassDef, SortedSet<int>>();
        var dispatch = new List<(int Id, FunctionDef Target)>();
        foreach (var fn in Functions.Values)
        {
            var p = fn.Program;
            // linear simulation over every statement list of the function
            void Seq(List<Stmt> list)
            {
                int? jaC = null;
                string? jaPool = null;
                int? lastAlloc = null;
                var allocVar = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int? faEntry = null;
                foreach (var s in list)
                {
                    switch (s.Verb)
                    {
                        case "MOVE":
                        {
                            var src = s.Get("src");
                            var dst = Dst(s);
                            if (dst is null || src is null) break;
                            var dn = dst.V!.ToUpperInvariant();
                            if (dn == "JA-C" && src is { K: "num" }) jaC = int.Parse(src.V!);
                            else if (dn == "FA-ENTRY" && src is { K: "num" }) faEntry = int.Parse(src.V!);
                            else if (src is { K: "ref" } && src.V!.Equals("JA-C", StringComparison.OrdinalIgnoreCase) && lastAlloc is int n1)
                                allocVar[SlotKey(dst)] = n1;
                            else if (src is { K: "ref" } && src.V!.EndsWith("-C", StringComparison.OrdinalIgnoreCase) && allocVar.TryGetValue(SlotKey(src), out var n2))
                                allocVar[SlotKey(dst)] = n2;
                            break;
                        }
                        case "PERFORM":
                        {
                            var t = s.Str("target") ?? "";
                            if (t.StartsWith("JA-NEW-", StringComparison.OrdinalIgnoreCase) && jaC is int id)
                            {
                                jaPool = t[7..].ToUpperInvariant();
                                if (Pools.TryGetValue(jaPool, out var pd)) pd.Ids.Add(id);
                                lastAlloc = id;
                            }
                            else if (FunctionOfParagraph(t) is { IsCtor: true, This: not null } ctor && allocVar.TryGetValue(ctor.This.CobolName, out var cid))
                                Bind(cid, ctor.Owner);
                            break;
                        }
                        case "CALL":
                        {
                            var prog = s.Get("target")?.V;
                            if (prog is not null && faEntry is int e && ResolveEntry(prog, e) is { IsCtor: true } ctor && allocVar.TryGetValue("FA#1", out var cid))
                                Bind(cid, ctor.Owner);
                            break;
                        }
                        case "IF":
                        {
                            var c = s.Get("cond");
                            // receiver check: IF F-THIS-C NOT = n (in a method of the class)
                            if (c is { K: "rel", Op: "ne", L: { K: "ref" } l, R: { K: "num" } r } && fn.This is not null && fn.Owner is not null
                                && l.V!.Equals(fn.This.CobolName + "-C", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!thisChecks.TryGetValue(fn.Owner, out var set)) thisChecks[fn.Owner] = set = [];
                                set.Add(int.Parse(r.V!));
                            }
                            // dispatch: IF x-C = n GO TO label
                            if (c is { K: "rel", Op: "eq", L: { K: "ref" } l2, R: { K: "num" } r2 } && l2.V!.EndsWith("-C", StringComparison.OrdinalIgnoreCase)
                                && s.Blocks?.GetValueOrDefault("TRUE") is [{ Verb: "GO TO" } g] && g.L?["targets"] is [{ V: var lbl }]
                                && p.ParagraphIndex.TryGetValue(lbl!, out var li))
                            {
                                var target = FirstCall(p, p.Model.Paragraphs[li].Statements);
                                if (target is not null) dispatch.Add((int.Parse(r2.V!), target));
                            }
                            break;
                        }
                        case "EVALUATE":
                        {
                            if (s.L?["subjects"] is [{ K: "ref" } subj] && subj.V!.EndsWith("-C", StringComparison.OrdinalIgnoreCase))
                                foreach (var w in s.Blocks?.GetValueOrDefault("WHEN") ?? [])
                                    if (w.Get("cond") is { K: "rel", Op: "eq", R: { K: "num" } wn } && FirstCall(p, w.Blocks?.GetValueOrDefault("TRUE") ?? []) is { } tf)
                                        dispatch.Add((int.Parse(wn.V!), tf));
                            break;
                        }
                    }
                    if (s.Blocks is not null) foreach (var b in s.Blocks.Values) Seq(b);
                }
            }
            var paras = p.Model.Paragraphs;
            for (int i = fn.FirstParagraph; i <= fn.LastParagraph; i++) Seq(paras[i].Statements);
        }
        // entry paragraphs and other code outside functions (job programs) also allocate: scan whole programs once for pools
        foreach (var (owner, ids) in thisChecks)
            if (ids.Count == 1 && !ClassById.ContainsKey(ids.Min)) Bind(ids.Min, owner);
        foreach (var (id, target) in dispatch)
        {
            if (ClassById.ContainsKey(id)) continue;
            if (target.IsLambda) { BindClosure(id, target); continue; }
            if (target.Owner is not null && !target.IsStatic && target.Owner.Ids.Count == 0) Bind(id, target.Owner);
        }
        // closures dispatch did not reach: the lambda whose receiver reads the closure pool's fields
        foreach (var pd in Pools.Values.Where(x => x.Prefix.StartsWith("HL") && x.Prefix[2..].All(char.IsDigit)))
            foreach (var id in pd.Ids.Where(i => !ClassById.ContainsKey(i)))
            {
                var lam = Functions.Values.FirstOrDefault(f => f.IsLambda && f.Owner is null && f.This is not null
                    && Deep(TopStatements(f).Select(x => x.Stmt)).SelectMany(Refs).Any(r => r.V!.StartsWith(pd.Prefix + "-", StringComparison.OrdinalIgnoreCase)));
                lam ??= Functions.Values.Where(f => f.IsLambda && f.Owner is null).FirstOrDefault(f => pd.Fields.Count == 0);
                if (lam is not null) BindClosure(id, lam);
            }
        // closures: a closure pool's class id is the lambda reached by dispatch; pools of closures
        foreach (var pd in Pools.Values.Where(x => x.Prefix.StartsWith("HL") && x.Prefix[2..].All(char.IsDigit)))
            foreach (var id in pd.Ids)
                if (ClassById.TryGetValue(id, out var cc)) cc.Pool = pd;
        // an id never allocated in a pool of anonymous classes of one class (an enum's constant bodies): that class
        // (J3S puts a hierarchy in one pool; the enum itself is abstract, so no allocation names its id)
        foreach (var pd in Pools.Values)
        {
            var bound = pd.Ids.Where(ClassById.ContainsKey).Select(i => ClassById[i]).Distinct().ToList();
            if (bound.Count == 0 || !bound.All(b => b.SimpleName.StartsWith('$') && b.Outer is not null) || bound.Select(b => b.Outer).Distinct().Count() != 1) continue;
            var outer = bound[0].Outer!;
            if (outer.Ids.Count > 0) continue;
            foreach (var id in pd.Ids.Where(i => !ClassById.ContainsKey(i)).Take(1)) Bind(id, outer);
        }
        // pools of bound classes
        foreach (var pd in Pools.Values)
            foreach (var id in pd.Ids)
                if (ClassById.TryGetValue(id, out var c) && c.Pool is null) c.Pool = pd;
        // exception names from the job program's JO-SIMPLE-NAME table
        foreach (var jp in JobPrograms)
        {
            var prog = Estate.ById[jp];
            var para = prog.Model.Paragraphs.FirstOrDefault(x => x.Name.Equals("JO-SIMPLE-NAME", StringComparison.OrdinalIgnoreCase));
            var ev = para?.Statements.FirstOrDefault(x => x.Verb == "EVALUATE");
            foreach (var w in ev?.Blocks?.GetValueOrDefault("WHEN") ?? [])
                if (w.Get("cond") is { K: "rel", R: { K: "num" } n } && w.Blocks?.GetValueOrDefault("TRUE") is [{ Verb: "MOVE" } mv] && mv.Get("src") is { K: "str" } nm)
                    ExceptionNames[int.Parse(n.V!)] = nm.V!;
        }
        // array classes: the allocator picks a class id by length (MOVE n TO JA-C in JA-NEW-A and JA-NEW-I)
        foreach (var prog in Estate.Programs)
            foreach (var para in prog.Model.Paragraphs.Where(x => x.Name is "JA-NEW-A" or "JA-NEW-I"))
                foreach (var mv in Deep(para.Statements).Where(x => x.Verb == "MOVE" && DstName(x)?.Equals("JA-C", StringComparison.OrdinalIgnoreCase) == true))
                    if (mv.Get("src") is { K: "num" } an && int.TryParse(an.V, out var aid))
                        ArrayIds[aid] = JType.Of(para.Name == "JA-NEW-A" ? "java.lang.Object[]" : "int[]");
    }

    void Bind(int id, ClassDef? c)
    {
        if (c is null) return;
        if (ClassById.TryGetValue(id, out var old))
        {
            if (!ReferenceEquals(old, c)) Problems.Add($"class id {id}: {old.FullName} and {c.FullName}");
            return;
        }
        ClassById[id] = c;
        c.Ids.Add(id);
    }

    void BindClosure(int id, FunctionDef lambda)
    {
        var outer = lambda.SrcAbbr is not null && ClassByAbbr.TryGetValue(lambda.SrcAbbr, out var o) ? o : null;
        var name = (outer?.FullName ?? "modern.lambda") + ".Lambda" + lambda.Id[1..];
        var c = GetClass(name);
        c.Kind = ClassKind.Closure;
        c.Outer = outer;
        c.Lambda = lambda;
        lambda.Owner = c;
        if (!c.Functions.Contains(lambda)) c.Functions.Add(lambda);
        Bind(id, c);
    }

    /// <summary>A stable key for a slot reference: frame slot or FA argument slot, ignoring the part.</summary>
    public static string SlotKey(Node n)
    {
        var v = n.V!.ToUpperInvariant();
        if (v.StartsWith("FA-") && n.Args is [{ K: "num" } k]) return "FA#" + k.V;
        foreach (var suf in new[] { "-C", "-I", "-V", "-P" })
            if (v.EndsWith(suf, StringComparison.Ordinal)) return v[..^suf.Length];
        return v;
    }

    /// <summary>The first function a statement list (or the paragraph it jumps to) calls.</summary>
    FunctionDef? FirstCall(SourceProgram p, List<Stmt> list)
    {
        int? e = null;
        foreach (var s in list)
        {
            if (s.Verb == "MOVE" && DstName(s) is { } d && d.Equals("FA-ENTRY", StringComparison.OrdinalIgnoreCase) && s.Get("src") is { K: "num" } n) e = int.Parse(n.V!);
            if (s.Verb == "PERFORM" && FunctionOfParagraph(s.Str("target") ?? "") is { } f && ReferenceEquals(f.Program, p)) return f;
            if (s.Verb == "CALL" && e is int ee && s.Get("target")?.V is { } prog && ResolveEntry(prog, ee) is { } f2) return f2;
            if (s.Verb == "GO TO" && s.L?["targets"] is [{ V: var t }] && FunctionOfParagraph(t!) is { } f3 && f3.Paragraph.Equals(t, StringComparison.OrdinalIgnoreCase)) return f3;
        }
        return null;
    }

    /// <summary>Owners of pool fields: the least common superclass of the classes that use each field.</summary>
    void AssignFields()
    {
        // superclasses: a constructor that runs another class's constructor on its own receiver
        // superclasses come from the lifted super(...) calls (Model.SetSuperclasses after lifting)
        var uses = new Dictionary<FieldDef, HashSet<ClassDef>>();
        foreach (var fn in Functions.Values)
        {
            if (fn.Owner is null) continue;
            foreach (var s in Deep(TopStatements(fn).Select(x => x.Stmt)))
                foreach (var n in Refs(s))
                {
                    if (n.Args is not [{ K: "ref" } sub]) continue;
                    var it = fn.Program.ItemsById.GetValueOrDefault(n.Id ?? -1);
                    if (it is null || HeapField(it) is not { } hf) continue;
                    // the receiver is this function's THIS: the field belongs to the owner or a superclass
                    if (fn.This is not null && sub.V!.StartsWith(fn.This.CobolName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!uses.TryGetValue(hf.Field, out var set)) uses[hf.Field] = set = [];
                        set.Add(fn.Owner);
                    }
                }
        }
        foreach (var pd in Pools.Values)
        {
            var classes = pd.Ids.Select(i => ClassById.GetValueOrDefault(i)).Where(c => c is not null).Cast<ClassDef>().Distinct().ToList();
            var root = classes.Count == 0 ? null : classes.Aggregate(Lca);
            foreach (var f in pd.Fields)
            {
                ClassDef? owner = uses.TryGetValue(f, out var set) && set.Count > 0 ? set.Aggregate(Lca) : root;
                if (owner is null) continue;
                f.Owner = owner;
                owner.Fields.Add(f);
            }
        }
    }

    /// <summary>A constructor whose body calls another class's constructor on its own receiver extends that class.</summary>
    public void SetSuperclasses(IEnumerable<(FunctionDef Fn, List<St> Body)> ctors)
    {
        foreach (var (fn, body) in ctors)
        {
            if (!fn.IsCtor || fn.Owner is null || fn.Owner.Super is not null) continue;
            foreach (var s in body)
                if (s is SExpr { E: ECall { Kind: CallKind.Super, Name: "super", Fn: { Owner: { } sup } } } && sup != fn.Owner)
                {
                    fn.Owner.Super = sup;
                    if (!sup.Subs.Contains(fn.Owner)) sup.Subs.Add(fn.Owner);
                    break;
                }
        }
        // field owners depend on the hierarchy: assign them again
        foreach (var c in Classes.Values) c.Fields.Clear();
        foreach (var pd in Pools.Values) foreach (var f in pd.Fields) f.Owner = null;
        AssignFields();
    }

    HashSet<ClassDef>? calculation;

    /// <summary>
    /// A calculation class (the reject site's class): J3S writes a site when an exception passes through its functions
    /// (MOVE 'ABBR:line' TO JX-AT before PERFORM JX-PROP); other classes let the caller's site stand.
    /// </summary>
    public bool IsCalculationClass(ClassDef c)
    {
        if (calculation is null)
        {
            calculation = [];
            foreach (var fn in Functions.Values)
            {
                if (fn.Owner is null) continue;
                foreach (var s in Deep(TopStatements(fn).Select(x => x.Stmt)))
                {
                    if (s.Verb != "IF" || s.Blocks?.GetValueOrDefault("TRUE") is not { } body) continue;
                    bool site = body.Any(b => b.Verb == "MOVE" && DstName(b)?.Equals("JX-AT", StringComparison.OrdinalIgnoreCase) == true && b.Get("src") is { K: "str" } st && st.V!.Contains(':'));
                    bool prop = body.Any(b => b.Verb == "PERFORM" && b.Str("target")?.Equals("JX-PROP", StringComparison.OrdinalIgnoreCase) == true);
                    if (site && prop) { calculation.Add(fn.Owner.Kind == ClassKind.Closure && fn.Owner.Outer is { } o ? o : fn.Owner); break; }
                }
            }
        }
        for (var x = c; x is not null; x = x.Outer) if (calculation.Contains(x)) return true;
        return false;
    }

    /// <summary>The Java type of a J3S class id: a value class, a class of the estate, an exception, an array.</summary>
    public JType? ClassIdType(int id)
    {
        if (JType.ValueClass(id) is { } vt) return vt;
        if (ClassById.TryGetValue(id, out var c)) return JType.Of(c);
        if (ExceptionNames.TryGetValue(id, out var n)) return JType.Of(FunctionLifter.ExceptionClass(n));
        if (ArrayIds.TryGetValue(id, out var at)) return at;
        return null;
    }

    /// <summary>The nearest class every one of the classes extends (null when there is none, or no classes).</summary>
    public static ClassDef? LcaAll(IEnumerable<ClassDef> classes)
    {
        ClassDef? acc = null;
        bool first = true;
        foreach (var c in classes)
        {
            if (first) { acc = c; first = false; continue; }
            acc = Lca(acc, c);
            if (acc is null) return null;
        }
        return acc;
    }

    public static ClassDef? Lca(ClassDef? a, ClassDef? b)
    {
        if (a is null || b is null) return a ?? b;
        var chain = new List<ClassDef>();
        for (var x = a; x is not null; x = x.Super) chain.Add(x);
        for (var y = b; y is not null; y = y.Super) if (chain.Contains(y)) return y;
        return null;
    }

    public static IEnumerable<Node> Refs(Stmt s)
    {
        IEnumerable<Node> Walk(Node? n)
        {
            if (n is null) yield break;
            if (n.K == "ref") yield return n;
            foreach (var c in new[] { n.L, n.R, n.Start, n.Len }) foreach (var x in Walk(c)) yield return x;
            foreach (var a in n.Args ?? []) foreach (var x in Walk(a)) yield return x;
        }
        foreach (var n in s.N?.Values ?? Enumerable.Empty<Node>()) foreach (var x in Walk(n)) yield return x;
        foreach (var l in s.L?.Values ?? Enumerable.Empty<List<Node>>()) foreach (var n in l) foreach (var x in Walk(n)) yield return x;
    }
}
