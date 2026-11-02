namespace MlCpu.Modernize;

/// <summary>
/// Domain shape: which classes are emitted and where, enums, statics, final fields, accessors, method names and
/// visibility, interfaces the legacy dispatches through.
/// </summary>
public sealed class Shapes
{
    public sealed class Shape
    {
        public bool IsEnum;
        public bool IsAbstract;
        public List<(string Name, List<Expr> Args)> EnumValues = [];
        public HashSet<FieldDef> EnumFields = [];
        /// <summary>Enum constants with a body of their own (an anonymous subclass): the constant and its class.</summary>
        public Dictionary<string, ClassDef> EnumBodies = [];
        /// <summary>The class is the body of an enum constant (printed inside the constant, not as a class).</summary>
        public bool IsEnumBody;
        /// <summary>Methods every subclass implements and a virtual call reaches through this class: declared abstract.</summary>
        public List<FunctionDef> AbstractMethods = [];
        public List<FunctionDef> Methods = [];
        public HashSet<FunctionDef> Private = [];
        public HashSet<FieldDef> FinalFields = [];
        public HashSet<FieldDef> FinalStatics = [];
        public Dictionary<FieldDef, Expr> StaticInit = [];
        public List<FieldDef> Getters = [];
        public Dictionary<FieldDef, string> GetterName = [];
        /// <summary>More getters of a field under other names (a virtual method J3S inlined as that field).</summary>
        public List<(string Name, FieldDef Field)> ExtraGetters = [];
        public Func<FunctionDef, string> MethodName = f => f.MethodName;
        /// <summary>A value class printed as a Java record: its components in canonical-constructor order, that constructor.</summary>
        public List<FieldDef>? Components;
        public FunctionDef? CanonicalCtor;
        /// <summary>The record defines no toString: one with Object's format keeps the output (Class@hash).</summary>
        public bool IdentityToString;
    }

    public sealed class Iface
    {
        public required string FullName;
        public string SimpleName => FullName[(FullName.LastIndexOf('.') + 1)..];
        public string Package => FullName.LastIndexOf('.') is var d && d > 0 ? FullName[..d] : "";
        public Dictionary<string, FunctionDef> Methods = [];
    }

    readonly Model M;
    readonly Dictionary<FunctionDef, Method> methods;
    readonly Dictionary<ClassDef, Shape> shapes = [];
    public readonly List<ClassDef> TopLevel = [];
    readonly Dictionary<ClassDef, List<ClassDef>> nested = [];
    public readonly Dictionary<string, Iface> Interfaces = new(StringComparer.Ordinal);
    public readonly Dictionary<ClassDef, List<Iface>> Implements = [];
    /// <summary>R1 api-visibility (opt-in): the access of methods, constructors and statics; null: the lift's own rules print.</summary>
    public Dictionary<FunctionDef, Access>? MemberAccess;
    public Dictionary<FieldDef, Access>? StaticAccess;
    /// <summary>R3 final-classes (opt-in): the classes printed final; null: the lift's own rules print.</summary>
    public HashSet<ClassDef>? FinalClasses;
    /// <summary>R5 program-interfaces (opt-in): declarations printed with an interface type; null: the lift's own types print.</summary>
    public ProgramInterfaces.Widened? Widened;

    public Shapes(Model m, Dictionary<FunctionDef, Method> methods)
    {
        M = m; this.methods = methods;
    }

    public Shape Of(ClassDef c) => shapes.TryGetValue(c, out var s) ? s : shapes[c] = new Shape();

    public IEnumerable<ClassDef> NestedOf(ClassDef c) => nested.TryGetValue(c, out var l) ? l.OrderBy(x => x.FullName, StringComparer.Ordinal) : [];

    public string MethodName(FunctionDef fn) => fn.Owner is null ? fn.MethodName : Of(fn.Owner).MethodName(fn);

    public static bool Emitted(ClassDef c) => c.Kind is ClassKind.Domain;

    public void Build()
    {
        AssignStatics();
        foreach (var c in M.Classes.Values.Where(Emitted))
        {
            if (c.Outer is not null && Emitted(c.Outer)) (nested.TryGetValue(c.Outer, out var l) ? l : nested[c.Outer] = []).Add(c);
            else TopLevel.Add(c);
            var s = Of(c);
            s.Methods = c.Functions.Where(f => methods.ContainsKey(f) && !f.IsLambda).OrderBy(Order).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        }
        foreach (var c in M.Classes.Values.Where(Emitted)) Enum(c);
        foreach (var c in M.Classes.Values.Where(Emitted)) FinalAndPrivate(c);
        Abstracts();
    }

    static int Order(FunctionDef f) => f.IsClinit ? 0 : f.IsCtor ? 1 : 2;

    // ------------------------------------------------------------------ statics

    /// <summary>Static field groups (S + class abbreviation in FS-CTX) belong to the class of that abbreviation.</summary>
    void AssignStatics()
    {
        foreach (var (fn, m) in methods)
        {
            if (!fn.IsClinit || fn.Owner is null) continue;
            foreach (var st in Passes.All(m.Body))
                if (st is SAssign { Lhs: EStatic es } && es.F.Owner is null && es.F.Group is not ("SLIT" or "SJOB"))
                {
                    foreach (var f in M.StaticsByItem.Values.Where(x => x.Group == es.F.Group && x.Owner is null))
                    {
                        f.Owner = fn.Owner;
                        if (!fn.Owner.Statics.Contains(f)) fn.Owner.Statics.Add(f);
                    }
                }
        }
        foreach (var f in M.StaticsByItem.Values.DistinctBy(f => f.CobolName))
        {
            if (f.Group is null || f.Group is "SLIT" or "SJOB") continue;
            var abbr = f.Group[1..];
            if (M.ClassByAbbr.TryGetValue(abbr, out var c) && !c.Statics.Contains(f))
            {
                f.Owner = c;
                c.Statics.Add(f);
            }
        }
    }

    // ------------------------------------------------------------------ enums

    /// <summary>
    /// A class whose instances are all created by its own class initializer and stored in its own statics is an enum:
    /// the statics are the constants in allocation order (J3S: the ordinal is the pool index - 1).
    /// </summary>
    void Enum(ClassDef c)
    {
        var clinit = c.Functions.FirstOrDefault(f => f.IsClinit);
        if (clinit is null || !methods.TryGetValue(clinit, out var cm)) return;
        // every new C(...) of this class in the estate, and of its anonymous subclasses (constants with a body)
        bool Mine(ClassDef? k) => k == c || k is not null && k.Super == c && k.SimpleName.StartsWith('$');
        var news = methods.Values.SelectMany(m => Passes.All(m.Body).Select(s => (m, s))).Where(x => Fold.Exprs(x.s).Any(e => Has(e, y => y is ENew n0 && Mine(n0.Class)))).ToList();
        if (news.Count == 0 || news.Any(x => x.m != cm)) return;
        var s = Of(c);
        var constants = new List<(FieldDef F, ENew New)>();
        foreach (var st in cm.Body)
            if (st is SAssign { Lhs: EStatic es, Rhs: ENew n } && Mine(n.Class)) constants.Add((es.F, n));
        if (constants.Count == 0) return;
        s.IsEnum = true;
        var names = EnumNames(c, constants.Select(x => x.F).ToList());
        foreach (var (f, n) in constants)
        {
            s.EnumFields.Add(f);
            f.Name = names[f];
            // an enum constant is of the enum's type (its creation leaves the initializer)
            f.Type = JType.Of(c);
            s.EnumValues.Add((f.Name, n.Args));
            if (n.Class != c && n.Class is { } body) { s.EnumBodies[f.Name] = body; Of(body).IsEnumBody = true; }
        }
        // the initializer's constant creation and the implicit valueOf/values are the enum's own: drop them
        cm.Body = cm.Body.Where(st => !(st is SAssign { Lhs: EStatic es2 } && s.EnumFields.Contains(es2.F))).ToList();
        if (cm.Body.Count == 0 || cm.Body.All(st => st is SReturn { E: null })) s.Methods.Remove(clinit);
        s.Methods.RemoveAll(f => f.MethodName is "valueOf" or "values" && f.IsStatic);
    }

    static bool Creates(St s, ClassDef c) => Fold.Exprs(s).Any(e => Has(e, x => x is ENew n && n.Class == c));

    static bool Has(Expr e, Func<Expr, bool> p) => p(e) || e.Children.Any(ch => Has(ch, p));

    /// <summary>The constant names: valueOf compares the full names with string literals; otherwise the static names.</summary>
    Dictionary<FieldDef, string> EnumNames(ClassDef c, List<FieldDef> fields)
    {
        var res = fields.ToDictionary(f => f, f => f.Name);
        var valueOf = c.Functions.FirstOrDefault(f => f.MethodName == "valueOf" && f.IsStatic && methods.ContainsKey(f));
        if (valueOf is null) return res;
        foreach (var st in Passes.All(methods[valueOf].Body))
        {
            // if ("NAME".equals(p)) return C.FIELD;  if (!"NAME".equals(p)) { ... } else { result = C.FIELD; }
            if (st is not SIf { C: var cond } sif || FindLiteral(cond) is not { } lit) continue;
            var branch = cond is EUnary { Op: "!" } ? sif.Else : sif.Then;
            // if (!"NAME".equals(p)) { throw ...; } result = C.FIELD;  (the last name of the chain)
            if (cond is EUnary { Op: "!" } && sif.Then is [SThrow] && sif.Else.Count == 0 && NextOf(methods[valueOf].Body, sif) is { } next) branch = [next];
            var f = branch switch { [SReturn { E: EStatic r }] => r.F, [SAssign { Rhs: EStatic a }] => a.F, _ => null };
            if (f is not null && res.ContainsKey(f) && System.Text.RegularExpressions.Regex.IsMatch(lit, @"^[A-Za-z_$][A-Za-z0-9_$]*$")) res[f] = lit;
        }
        return res;
    }

    /// <summary>The statement after s in the list that holds it.</summary>
    static St? NextOf(List<St> body, St s)
    {
        foreach (var list in Lists(body))
        {
            int i = list.IndexOf(s);
            if (i >= 0) return i + 1 < list.Count ? list[i + 1] : null;
        }
        return null;
    }

    static IEnumerable<List<St>> Lists(List<St> body)
    {
        yield return body;
        foreach (var st in body)
        {
            var kids = st switch
            {
                SIf f => new[] { f.Then, f.Else },
                SLoop l => new[] { l.Body },
                SBlock b => new[] { b.Body },
                STry t => new[] { t.Body },
                _ => [],
            };
            foreach (var k in kids) foreach (var l in Lists(k)) yield return l;
        }
    }

    static string? FindLiteral(Expr e) => e switch
    {
        ELit { Value: string s } => s,
        _ => e.Children.Select(FindLiteral).FirstOrDefault(x => x is not null),
    };

    // ------------------------------------------------------------------ fields, visibility

    void FinalAndPrivate(ClassDef c)
    {
        var s = Of(c);
        var assignedOutsideCtor = new HashSet<FieldDef>();
        var staticAssignedOutsideInit = new HashSet<FieldDef>();
        // a static J3S never initializes (a JDK formatter it does not lower) cannot be final
        var staticAssignedInInit = new HashSet<FieldDef>();
        foreach (var m in methods.Values)
            foreach (var st in Passes.All(m.Body))
            {
                if (st is SAssign { Lhs: EField lf } && !(m.Fn.IsCtor && m.Fn.Owner == lf.F.Owner && lf.Obj is EThis)) assignedOutsideCtor.Add(lf.F);
                if (st is SAssign { Lhs: EStatic ls } && !(m.Fn.IsClinit && m.Fn.Owner == ls.F.Owner)) staticAssignedOutsideInit.Add(ls.F);
                // final: assigned once, at the top level of the static initializer (not in its loops)
                else if (st is SAssign { Lhs: EStatic li } && (!m.Body.Contains(st) || !staticAssignedInInit.Add(li.F))) staticAssignedOutsideInit.Add(li.F);
            }
        // a final field must be assigned exactly once on every constructor path: only claim it for single simple assignments
        foreach (var f in c.Fields)
        {
            if (assignedOutsideCtor.Contains(f)) continue;
            var ctors = c.Functions.Where(fn => fn.IsCtor && methods.ContainsKey(fn)).ToList();
            if (ctors.Count == 0) continue;
            bool ok = ctors.All(ct =>
            {
                var body = methods[ct].Body;
                int top = body.Count(st => st is SAssign { Lhs: EField { Obj: EThis } fe } && fe.F == f);
                int all = Passes.All(body).Count(st => st is SAssign { Lhs: EField { Obj: EThis } fe } && fe.F == f);
                bool delegates = body.Any(st => st is SExpr { E: ECall { Kind: CallKind.Super, Name: "this" } });
                return delegates ? all == 0 : top == 1 && all == 1;
            });
            if (ok) s.FinalFields.Add(f);
        }
        foreach (var f in c.Statics)
            if (!staticAssignedOutsideInit.Contains(f) && !s.EnumFields.Contains(f) && (staticAssignedInInit.Contains(f) || s.StaticInit.ContainsKey(f))) s.FinalStatics.Add(f);
        // static constants initialised once in the class initializer: final (the initializer assigns them)
        // methods called only from their own class (no entry from another program) are private
        var calledFromOutside = new HashSet<FunctionDef>();
        foreach (var m in methods.Values)
            foreach (var st in Passes.All(m.Body))
                foreach (var e in Fold.Exprs(st))
                    Visit(e, x =>
                    {
                        if (x is ECall { Fn: { } f } && (TopOf(f.Owner) != TopOf(m.Fn.Owner) || m.Fn.IsLambda && false)) calledFromOutside.Add(f);
                        // from an enum constant's body (an anonymous subclass): not private (the body cannot reach those)
                        if (x is ECall { Fn: { } f2 } && m.Fn.Owner is { } mo && mo.SimpleName.StartsWith('$') && f2.Owner != mo) calledFromOutside.Add(f2);
                    });
        foreach (var fn in s.Methods)
            if (!fn.IsCtor && !fn.IsClinit && !calledFromOutside.Contains(fn) && !Overrides(fn) && M.Entries.Values.SelectMany(e => e.Values).All(e => e.Fn != fn))
                s.Private.Add(fn);
    }

    static ClassDef? TopOf(ClassDef? c) { while (c?.Outer is not null) c = c.Outer; return c; }

    static void Visit(Expr e, Action<Expr> a) { a(e); foreach (var c in e.Children) Visit(c, a); if (e is ELambda l) foreach (var v in l.Captures.Values) Visit(v, a); }

    bool Overrides(FunctionDef fn) => fn.MethodName is "equals" or "hashCode" or "toString" or "compareTo" or "canEqual"
        || fn.Owner?.Super?.Functions.Any(f => f.MethodName == fn.MethodName && f.Params.Count == fn.Params.Count) == true
        || fn.Owner?.Subs.Any(sc => sc.Functions.Any(f => f.MethodName == fn.MethodName && f.Params.Count == fn.Params.Count)) == true;

    void Abstracts()
    {
        foreach (var c in M.Classes.Values.Where(Emitted))
            if (c.Ids.Count == 0 && c.Subs.Count > 0 && !Of(c).IsEnum) Of(c).IsAbstract = true;
    }

    // ------------------------------------------------------------------ interfaces

    /// <summary>The interfaces type inference found; their methods are the calls made through them.</summary>
    public void AddInterfaces(TypeInference t)
    {
        foreach (var (_, (full, impls)) in t.Interfaces)
        {
            var i = new Iface { FullName = full };
            Interfaces[full] = i;
            foreach (var c in impls) (Implements.TryGetValue(c, out var l) ? l : Implements[c] = []).Add(i);
        }
        // the implementations J3S inlined as field reads: a getter named as the method
        foreach (var m in methods.Values)
            foreach (var st in Passes.All(m.Body))
                foreach (var e in Fold.Exprs(st))
                    Visit(e, x =>
                    {
                        if (x is not ECall { Virtual: true, Fn: { } vf, FieldImpls: { } impls }) return;
                        foreach (var (cls, fld) in impls)
                        {
                            if (Inherits(cls).Any(g => g.MethodName == vf.MethodName && g.Params.Count == 0)) continue;
                            var s = Of(fld.Owner ?? cls);
                            var gname = MethodName(vf);
                            if (s.GetterName.TryGetValue(fld, out var existing) && existing != gname)
                            {
                                if (!s.ExtraGetters.Contains((gname, fld))) s.ExtraGetters.Add((gname, fld));
                                continue;
                            }
                            s.GetterName[fld] = gname;
                            if (!s.Getters.Contains(fld)) s.Getters.Add(fld);
                        }
                    });
        // a virtual call through a superclass that does not declare the method (enum constant bodies, subclasses): abstract there
        foreach (var m in methods.Values)
            foreach (var st in Passes.All(m.Body))
                foreach (var e in Fold.Exprs(st))
                    Visit(e, x =>
                    {
                        if (x is not ECall { Virtual: true, Fn: { Owner: { } fo } vf, Recv: { } vr }) return;
                        if (Static(t, vr) is not { } rtn || !M.Classes.TryGetValue(rtn, out var rc) || rc == fo || !IsSub(fo, rc)) return;
                        if (Inherits(rc).Any(g => g.MethodName == vf.MethodName && g.Params.Count == vf.Params.Count)) return;
                        var rs = Of(rc);
                        if (rs.AbstractMethods.Any(a => a.MethodName == vf.MethodName && a.Params.Count == vf.Params.Count)) return;
                        rs.AbstractMethods.Add(vf);
                        if (!rs.IsEnum) rs.IsAbstract = true;
                    });
        if (Interfaces.Count == 0) return;
        foreach (var m in methods.Values)
            foreach (var st in Passes.All(m.Body))
                foreach (var e in Fold.Exprs(st))
                    Visit(e, x =>
                    {
                        if (x is not ECall { Kind: CallKind.Instance, Fn: { } f, Recv: { } r }) return;
                        var rt = Static(t, r);
                        if (rt is null || !Interfaces.TryGetValue(rt, out var iface)) return;
                        var impls0 = t.Interfaces.Values.First(v => v.FullName == rt).Impls;
                        bool everyone = impls0.All(impl => Inherits(impl).Any(g => g.MethodName == f.MethodName && g.Params.Count == f.Params.Count)
                            || f.Params.Count == 0 && SuperChain(impl).Any(c => Of(c).GetterName.Values.Contains(MethodName(f)) || Of(c).ExtraGetters.Any(x => x.Name == MethodName(f))));
                        if (!everyone) return;
                        iface.Methods.TryAdd(f.MethodName + "/" + f.Params.Count, f);
                        // every implementation is public
                        foreach (var impl in t.Interfaces.Values.First(v => v.FullName == rt).Impls)
                            foreach (var g in impl.Functions.Where(g => g.MethodName == f.MethodName && g.Params.Count == f.Params.Count))
                                Of(impl).Private.Remove(g);
                    });
    }

    static bool IsSub(ClassDef c, ClassDef sup)
    {
        for (var x = c.Super; x is not null; x = x.Super) if (x == sup) return true;
        return false;
    }

    static IEnumerable<ClassDef> SuperChain(ClassDef c)
    {
        for (var x = c; x is not null; x = x.Super) yield return x;
    }

    static IEnumerable<FunctionDef> Inherits(ClassDef c)
    {
        for (var x = c; x is not null; x = x.Super) foreach (var f in x.Functions) yield return f;
    }

    static string? Static(TypeInference t, Expr e) => e switch
    {
        EVar v => t.JavaType(t.Of(v.V)).Name,
        EField f => t.JavaType(t.Of(f.F)).Name,
        EStatic s => t.JavaType(t.Of(s.F)).Name,
        ECall { Fn: { Ret: not null } fn } => t.JavaType(t.Ret(fn)).Name,
        _ => null,
    };

    // ------------------------------------------------------------------ accessors

    /// <summary>Registers the accessors (and setters) every cross-class field access needs, before any file is printed.</summary>
    public void PrepareAccessors()
    {
        foreach (var m in methods.Values)
        {
            var from = TopOf(m.Fn.Owner?.Kind == ClassKind.Closure ? m.Fn.Owner.Outer : m.Fn.Owner);
            foreach (var st in Passes.All(m.Body))
            {
                if (st is SAssign { Lhs: EField lf } && lf.F.Owner is { } wo && TopOf(wo) != from) Setter(wo, lf.F);
                foreach (var e in Fold.Exprs(st))
                    Visit(e, x =>
                    {
                        if (x is EField f && f.F.Owner is { } o && TopOf(o) != from && !(f.Obj is EThis && !m.Fn.IsLambda)) Accessor(o, f.F);
                    });
            }
        }
    }

    static readonly HashSet<string> RecordForbidden = new(StringComparer.Ordinal) { "clone", "finalize", "getClass", "hashCode", "notify", "notifyAll", "toString", "wait" };

    /// <summary>
    /// Value classes as Java records (design O2): a domain class with no superclass or subclass, every field final and
    /// without setters, one constructor that only assigns each field from its own parameter of the same type (the
    /// canonical constructor), and equals and hashCode of its own (value semantics, so a record's are not a change).
    /// A zero-argument method named like a field must be its accessor; without toString the record keeps Object's.
    /// </summary>
    public void Records(TypeInference t, Func<ClassDef, bool> isException)
    {
        var withSubclasses = M.Classes.Values.Where(x => x.Super is not null).Select(x => x.Super!).ToHashSet();
        foreach (var c in M.Classes.Values.Where(Emitted).OrderBy(x => x.FullName, StringComparer.Ordinal))
        {
            var s = Of(c);
            if (c.Kind != ClassKind.Domain || s.IsEnum || s.IsAbstract || s.IsEnumBody || c.Super is not null || withSubclasses.Contains(c) || isException(c)) continue;
            if (c.Fields.Count == 0 || c.Fields.Any(f => !s.FinalFields.Contains(f) || RecordForbidden.Contains(f.Name)) || Setters.ContainsKey(c)) continue;
            var ctors = c.Functions.Where(f => f.IsCtor).ToList();
            if (ctors.Count != 1 || !methods.TryGetValue(ctors[0], out var cm) || cm.L.Params.Count != c.Fields.Count) continue;
            var order = new List<FieldDef>();
            bool canonical = cm.Body.Count == c.Fields.Count;
            for (int i = 0; canonical && i < cm.Body.Count; i++)
            {
                if (cm.Body[i] is not SAssign { Lhs: EField { Obj: EThis, F: var f }, Rhs: EVar { V: var p } } || !cm.L.Params.Contains(p) || order.Contains(f)) { canonical = false; break; }
                // the component's type is the parameter's: the constructor takes exactly what the field holds
                if (!Equals(t.JavaType(t.Of(f)), t.JavaType(t.Param(ctors[0], cm.L.Params.IndexOf(p))))) { canonical = false; break; }
                order.Add(f);
            }
            if (!canonical || order.Count != c.Fields.Count) continue;
            // components in the constructor's parameter order
            order = order.OrderBy(f => cm.L.Params.IndexOf(((EVar)((SAssign)cm.Body.First(st => st is SAssign { Lhs: EField fe } && fe.F == f)).Rhs).V)).ToList();
            bool HasMethod(string name, int arity) => c.Functions.Any(f => !f.IsStatic && f.MethodName == name && f.Params.Count == arity && methods.ContainsKey(f));
            if (!HasMethod("equals", 1) || !HasMethod("hashCode", 0)) continue;
            // a method named like a component is its accessor: no arguments, the field, public
            bool clash = c.Functions.Any(f => !f.IsStatic && !f.IsCtor && order.Any(o => o.Name == MethodName(f)) && (f.Params.Count > 0 || s.Private.Contains(f)
                || !methods.TryGetValue(f, out var fm) || fm.Body is not [SReturn { E: EField { Obj: EThis } rf }] || rf.F.Name != MethodName(f)));
            if (clash || s.ExtraGetters.Any(g => order.Any(o => o.Name == g.Name && o != g.Field))) continue;
            // a static method of a component's name would clash with its accessor
            if (c.Functions.Any(f => f.IsStatic && f.Params.Count == 0 && order.Any(o => o.Name == MethodName(f)))) continue;
            s.Components = order;
            s.CanonicalCtor = ctors[0];
            s.IdentityToString = !HasMethod("toString", 0);
        }
    }

    public readonly Dictionary<ClassDef, List<FieldDef>> Setters = [];

    public string Setter(ClassDef owner, FieldDef f)
    {
        var name = "set" + Naming.UpperFirst(f.Name);
        for (var c = owner; c is not null; c = c.Super)
            foreach (var fn in c.Functions)
                if (fn.MethodName == name && fn.Params.Count == 1) return name;
        var o = f.Owner ?? owner;
        if (!Setters.TryGetValue(o, out var l)) Setters[o] = l = [];
        if (!l.Contains(f)) l.Add(f);
        Of(o).FinalFields.Remove(f);
        return name;
    }

    /// <summary>The accessor of a field read from another class: an existing getter (return this.f;) or a synthesized one.</summary>
    public string Accessor(ClassDef owner, FieldDef f)
    {
        for (var c = owner; c is not null; c = c.Super)
            foreach (var fn in c.Functions)
                if (fn.Params.Count == 0 && !fn.IsStatic && methods.TryGetValue(fn, out var m) && m.Body is [SReturn { E: EField { Obj: EThis } rf }] && rf.F == f)
                    return MethodName(fn);
        var s = Of(f.Owner ?? owner);
        if (!s.GetterName.TryGetValue(f, out var name))
        {
            name = (f.Kind == VKind.Bool ? "is" : "get") + Naming.UpperFirst(f.Name);
            var ownerClass = f.Owner ?? owner;
            if (Inherits(ownerClass).Any(g => g.Params.Count == 0 && (g.MethodName == name || MethodName(g) == name))) name = Naming.LowerFirst(f.Name);
            s.GetterName[f] = name;
            s.Getters.Add(f);
        }
        return name;
    }
}
