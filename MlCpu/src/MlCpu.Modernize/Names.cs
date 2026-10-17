namespace MlCpu.Modernize;

/// <summary>
/// Names J3S shortened to fit COBOL, completed from the Java names the estate keeps whole (design §5.7). J3S cuts a local
/// or a parameter at 12 characters (a lambda's at 10), a field at what its pool prefix leaves, and abbreviates words
/// (INTEREST INT, REPAYMENT REPAY). Method names are whole: they are in the signature comments. The abbreviations are
/// learned from the estate (a paragraph name beside its method name). A name is completed from evidence tied to it: the
/// method that reads or writes a field, the call that sets a variable, the parameter or builder method it is passed to,
/// the field it is stored in, the arguments of a parameter, its type; without evidence its abbreviations are expanded.
/// </summary>
public sealed class NameRecovery
{
    static readonly string[] Verbs = ["get", "is", "has", "calculate", "calc", "compute", "find", "create", "build", "resolve", "determine", "fetch", "load", "read", "to", "new", "with", "set"];

    readonly Model m;
    readonly Dictionary<FunctionDef, Method> methods;
    readonly Shapes shapes;
    TypeInference? types;

    /// <summary>The abbreviations J3S applied (DISB: DISBURSEMENT), learned from the estate.</summary>
    public IReadOnlyDictionary<string, string> Abbreviations => abbr;
    readonly Dictionary<string, string> abbr;

    public NameRecovery(Model m, Dictionary<FunctionDef, Method> methods, Shapes shapes)
    {
        this.m = m;
        this.methods = methods;
        this.shapes = shapes;
        abbr = Learn(m);
    }

    /// <summary>Before type inference: the fields their getters, setters and builder methods name.</summary>
    public int FieldsFromMethods() => Fields(false);

    /// <summary>After type inference: the variables (their types are evidence too), the fields their completed constructor parameters name, the variables once more.</summary>
    public (int Fields, int Vars) Complete(TypeInference t)
    {
        types = t;
        int v = Vars();
        int f = Fields(true);
        if (f > 0) v += Vars();
        return (f, v);
    }

    /// <summary>
    /// The abbreviations: a function's paragraph name (M0693-PEC-ADD-DISB) word by word against its method name
    /// (addDisbursement); a word that differs and has the Java word's initial and letters in order is its abbreviation.
    /// The last word of a paragraph name that may be cut proves nothing; the most frequent reading wins.
    /// </summary>
    static Dictionary<string, string> Learn(Model m)
    {
        var counts = new Dictionary<(string Abbr, string Word), int>();
        foreach (var fn in m.Functions.Values)
        {
            if (fn.Owner is null || fn.IsLambda || fn.IsJob || fn.IsClinit || fn.IsCtor || fn.MethodName.Length == 0) continue;
            var parts = fn.Paragraph.ToUpperInvariant().Split('-');
            if (parts.Length < 3 || parts[0].Length != 5 || parts[0][0] != 'M') continue;
            var pw = parts[2..];
            var jw = JavaWords(fn.MethodName);
            bool cut = fn.Paragraph.Length >= 26;
            if (pw.Length > jw.Length || !cut && pw.Length != jw.Length) continue;
            var found = new List<(string, string)>();
            bool ok = true;
            for (int i = 0; i < pw.Length && ok; i++)
            {
                if (pw[i] == jw[i]) continue;
                if (i == pw.Length - 1 && cut) break;
                if (Subsequence(pw[i], jw[i])) found.Add((pw[i], jw[i]));
                else ok = false;
            }
            if (ok) foreach (var p in found) counts[p] = counts.GetValueOrDefault(p) + 1;
        }
        return counts.GroupBy(kv => kv.Key.Abbr)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Word, StringComparer.Ordinal).First().Key.Word);
    }

    /// <summary>Completes the instance fields of the domain classes; returns how many were renamed.</summary>
    int Fields(bool fromParams)
    {
        var all = m.Classes.Values.Where(c => c.Kind == ClassKind.Domain).SelectMany(c => c.Fields).Where(f => f.Pool is not null && !f.IsStatic).ToList();
        if (all.Count == 0) return 0;
        int max = all.Max(f => f.CobolName.Length);
        var ev = new Dictionary<FieldDef, SortedSet<string>>();
        void Add(FieldDef f, string name)
        {
            if (!ev.TryGetValue(f, out var s)) ev[f] = s = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var n in Forms(name)) s.Add(n);
        }
        foreach (var (fn, meth) in methods.OrderBy(kv => kv.Key.Id, StringComparer.Ordinal))
        {
            if (fn.Owner is null || fn.IsLambda || fn.IsStatic) continue;
            var nm = shapes.MethodName(fn);
            // a getter (return this.f) or a setter or builder method (this.f = p)
            if (meth.Body is [SReturn { E: EField { Obj: EThis } rf }]) Add(rf.F, nm);
            if (meth.L.Params.Count == 1 && !fn.IsCtor)
                foreach (var st in meth.Body)
                    if (st is SAssign { Lhs: EField { Obj: EThis } lf, Rhs: EVar { V: var pv } } && pv == meth.L.Params[0]) Add(lf.F, nm);
            // a constructor or setter parameter stored in the field, once the parameters are completed
            if (fromParams)
                foreach (var st in meth.Body)
                    if (st is SAssign { Lhs: EField { Obj: EThis } pf, Rhs: EVar { V: { IsParam: true } pp } }) Add(pf.F, pp.Name);
            // a virtual getter J3S inlined as a field read
            foreach (var st in Passes.All(meth.Body))
                foreach (var e in Fold.Exprs(st))
                    Visit(e, x => { if (x is ECall { Virtual: true, Fn: { } vf, FieldImpls: { } impls }) foreach (var (_, fld) in impls) Add(fld, shapes.MethodName(vf)); });
        }
        // a field without evidence of its own takes the completion of the fields of the same name (a class and its builder)
        var byName = all.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.SelectMany(f => ev.GetValueOrDefault(f) ?? []).ToHashSet());
        int n = 0;
        foreach (var f in all.OrderBy(f => f.CobolName, StringComparer.Ordinal))
        {
            bool cut = f.CobolName.Length >= max - 1;
            var words = JavaWords(f.Name);
            var best = Spell(words, Best(words, cut, ev.TryGetValue(f, out var own) && own.Count > 0 ? own : byName[f.Name])) ?? Expand(words);
            if (best is null || best == f.Name) continue;
            if (f.Owner is { } owner && owner.Fields.Any(o => o != f && o.Name == best)) continue;
            f.Name = best;
            n++;
        }
        return n;
    }

    /// <summary>Completes the parameters and locals of the lifted methods; returns how many were renamed.</summary>
    int Vars()
    {
        int n = 0;
        // to a fixed point: a completed argument completes the parameter it is passed to, and the other way round
        for (int round = 0, before = -1; round < 8 && n != before; round++)
        {
            before = n;
            var ev = new Dictionary<Var, SortedSet<string>>();
            void Add(Var v, IEnumerable<string> names)
            {
                if (v.IsThis || v.IsTemp) return;
                if (!ev.TryGetValue(v, out var s)) ev[v] = s = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var nm in names) s.Add(nm);
            }
            foreach (var (fn, meth) in methods.OrderBy(kv => kv.Key.Id, StringComparer.Ordinal))
            {
                foreach (var st in Passes.All(meth.Body))
                {
                    if (st is SAssign { Lhs: EVar lv } a) Add(lv.V, Hints(a.Rhs));
                    if (st is SAssign { Lhs: EField lf, Rhs: EVar rv }) Add(rv.V, [lf.F.Name]);
                    if (st is SReturn { E: EVar rv2 } && !fn.IsLambda && fn.Owner is not null) Add(rv2.V, Forms(shapes.MethodName(fn)));
                    if (st is SLoop { Each: { } each, Iterable: { } it }) Add(each, Hints(it).Select(Singular).Where(s => s.Length > 0));
                    foreach (var e in Fold.Exprs(st))
                        Visit(e, x =>
                        {
                            var (fnc, args) = x switch { ECall c0 => (c0.Fn, c0.Args), ENew n0 => (n0.Ctor, n0.Args), _ => (null, null) };
                            if (args is null) return;
                            var callee = fnc is not null && methods.TryGetValue(fnc, out var cm) ? cm : null;
                            for (int i = 0; i < args.Count; i++)
                            {
                                // the parameter an argument is passed to, and the argument a parameter receives
                                if (callee is not null && i < callee.L.Params.Count)
                                {
                                    if (args[i] is EVar av) Add(av.V, [callee.L.Params[i].Name]);
                                    Add(callee.L.Params[i], Hints(args[i]));
                                }
                                // a builder method or a setter: x.amount(v), x.setAmount(v)
                                if (x is ECall c && args.Count == 1 && args[i] is EVar bv) Add(bv.V, Forms(c.Fn is null ? c.Name : shapes.MethodName(c.Fn)));
                            }
                        });
                }
                // the type: an EarlyPayment is an earlyPayment, a List<MonthlyPayment> monthlyPayments
                if (types is not null)
                    foreach (var v in meth.L.Params.Concat(meth.L.Vars))
                        if (!v.IsThis && !v.IsTemp && types.Vars.ContainsKey(v)) Add(v, TypeNames(types.JavaType(types.Of(v))));
            }
            foreach (var (fn, meth) in methods.OrderBy(kv => kv.Key.Id, StringComparer.Ordinal))
                foreach (var v in meth.L.Params.Concat(meth.L.Vars))
                {
                    if (v.IsThis || v.IsTemp || v.Hint is not { } hint) continue;
                    int limit = fn.IsLambda ? 10 : 12;
                    var h = hint.ToUpperInvariant();
                    // a numbered name (MONTHLY-INTE2): J3S made it unique after the cut
                    if (h.Length > limit && char.IsDigit(h[^1])) h = h.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                    bool cut = h.Length >= limit - 1;
                    var words = h.Split('-', StringSplitOptions.RemoveEmptyEntries);
                    // the name it has: the hint's, or a completion (the writer numbers two of the same name)
                    var best = Spell(words, ev.TryGetValue(v, out var cands) ? Best(words, cut, cands) : null) ?? Expand(words);
                    if (best is null || best == v.Name) continue;
                    v.Name = best;
                    n++;
                }
        }
        return n;
    }

    /// <summary>The name with every learned abbreviation spelled out (annualNominalIntRate: annualNominalInterestRate), or null.</summary>
    string? Expand(string[] words)
    {
        if (!words.Any(abbr.ContainsKey)) return null;
        var full = words.Select(w => abbr.GetValueOrDefault(w, w)).ToArray();
        return Naming.Safe(Naming.Camel(string.Join('-', full)));
    }

    /// <summary>A completion that kept the COBOL's abbreviations (crIntAmount for CR-INT-AMOUN): spelled out (creditedInterestAmount).</summary>
    string? Spell(string[] words, string? name)
    {
        if (name is null) return null;
        var jw = JavaWords(name);
        bool any = false;
        for (int i = 0; i < Math.Min(words.Length, jw.Length); i++)
            if (jw[i] == words[i] && abbr.TryGetValue(words[i], out var full)) { jw[i] = full; any = true; }
        return any ? Naming.Safe(Naming.Camel(string.Join('-', jw))) : name;
    }

    /// <summary>The names an expression suggests for what it is assigned to or passed as.</summary>
    IEnumerable<string> Hints(Expr e) => e switch
    {
        ECall c => Forms(c.Fn is null ? c.Name : shapes.MethodName(c.Fn)),
        EField f => [f.F.Name],
        EStatic s when !s.F.Name.Contains('_') && s.F.Name != s.F.Name.ToUpperInvariant() => [s.F.Name],
        EVar { V: { IsTemp: false, IsThis: false } v } => [v.Name],
        ECast c => Hints(c.E),
        ENew { Class: { } k } => [Naming.LowerFirst(k.SimpleName.TrimStart('$'))],
        _ => [],
    };

    /// <summary>The names a type suggests: its simple name, and for a collection, a map or an array the plural of its element's.</summary>
    static IEnumerable<string> TypeNames(JType t)
    {
        if (t.IsPrimitive || t.IsObject) yield break;
        if (t.IsArray) { foreach (var s in TypeNames(JType.Of(t.Name[..^2]))) yield return Plural(s); yield break; }
        if (t.Args is [var elem] && t.Simple is "List" or "Set" or "Collection" or "NavigableSet" or "SortedSet" or "Deque" or "Queue" or "Iterable")
        {
            foreach (var s in TypeNames(elem)) yield return Plural(s);
            yield break;
        }
        if (t.Args is [_, var val] && t.Simple is "Map" or "HashMap" or "TreeMap" or "LinkedHashMap" or "NavigableMap" or "SortedMap")
        {
            foreach (var s in TypeNames(val)) yield return Plural(s);
            yield break;
        }
        var simple = t.Simple;
        int dollar = simple.LastIndexOf('$');
        if (dollar >= 0) simple = simple[(dollar + 1)..];
        if (simple.Length > 0 && char.IsLetter(simple[0]) && simple != "String") yield return Naming.LowerFirst(simple);
    }

    static string Plural(string s) =>
        s.EndsWith('s') ? s : s.EndsWith('y') && s.Length > 1 && !"aeiou".Contains(s[^2]) ? s[..^1] + "ies" : s.EndsWith('x') ? s + "es" : s + "s";

    /// <summary>A method name and its noun: getMonthlyInterestRate, monthlyInterestRate.</summary>
    static IEnumerable<string> Forms(string name)
    {
        if (name.Length == 0 || name.StartsWith('<')) yield break;
        yield return name;
        foreach (var v in Verbs)
            if (name.Length > v.Length && name.StartsWith(v, StringComparison.Ordinal) && char.IsUpper(name[v.Length]))
            {
                yield return Naming.LowerFirst(name[v.Length..]);
                yield break;
            }
    }

    static string Singular(string plural) =>
        plural.EndsWith("ies", StringComparison.Ordinal) ? plural[..^3] + "y" : plural.EndsWith('s') && !plural.EndsWith("ss", StringComparison.Ordinal) ? plural[..^1] : "";

    /// <summary>
    /// The best completion of the COBOL words among the candidates: fewest abbreviated words, most words exactly equal,
    /// fewest words, shortest. A candidate that is a prefix of another (a getter named after a cut field) gives way to it.
    /// </summary>
    string? Best(string[] words, bool cut, IEnumerable<string> cands)
    {
        string? best = null;
        (int Abbr, int Exact, int Words, int Len) score = default;
        var list = cands.Distinct().ToList();
        foreach (var c in list)
        {
            if (list.Any(o => o.Length > c.Length && o.StartsWith(c, StringComparison.Ordinal))) continue;
            if (!Naming.Safe(c).Equals(c, StringComparison.Ordinal)) continue;
            var jw = JavaWords(c);
            if (!Fits(words, jw, cut, out int exact)) continue;
            var sc = (-jw.Count(abbr.ContainsKey), exact, -jw.Length, -c.Length);
            if (best is null || sc.CompareTo(score) > 0 || sc.CompareTo(score) == 0 && string.CompareOrdinal(c, best) < 0) { best = c; score = sc; }
        }
        return best;
    }

    /// <summary>
    /// The COBOL words (MONTHLY-INTE) fit the Java words (MONTHLY INTEREST RATE): each is the Java word or its learned
    /// abbreviation; the last may be cut short (a prefix of the word or of its abbreviation); words may follow only a
    /// visibly cut word (TOTAL-AMOUNT, cut on a word boundary, reads as a whole name).
    /// </summary>
    bool Fits(string[] cw, string[] jw, bool cut, out int exact)
    {
        exact = 0;
        if (cw.Length == 0 || jw.Length < cw.Length || jw.Length > cw.Length && !cut) return false;
        if (jw.Length > cw.Length && (cw[^1] == jw[cw.Length - 1] || abbr.GetValueOrDefault(cw[^1]) == jw[cw.Length - 1])) return false;
        for (int i = 0; i < cw.Length; i++)
        {
            var c = cw[i];
            var j = jw[i];
            if (c == j) { exact++; continue; }
            if (abbr.GetValueOrDefault(c) == j) continue;
            if (i == cw.Length - 1 && cut && (j.StartsWith(c, StringComparison.Ordinal) || abbr.Any(a => a.Value == j && a.Key.StartsWith(c, StringComparison.Ordinal)))) continue;
            return false;
        }
        return true;
    }

    /// <summary>An abbreviation's shape: shorter, the same initial, the letters in order (PCT, PERCENTAGE).</summary>
    static bool Subsequence(string c, string j)
    {
        if (c.Length < 2 || c.Length >= j.Length || c[0] != j[0] || c.Any(char.IsDigit)) return false;
        int k = 0;
        foreach (var ch in j) if (k < c.Length && ch == c[k]) k++;
        return k == c.Length;
    }

    /// <summary>The words of a Java name as J3S splits them (calculateRateFactor: CALCULATE RATE FACTOR; getEMIAmount: GET EMI AMOUNT).</summary>
    public static string[] JavaWords(string s)
    {
        var words = new List<string>();
        var w = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool boundary = c == '_' || char.IsUpper(c) && w.Length > 0 && (!char.IsUpper(s[i - 1]) || i + 1 < s.Length && char.IsLower(s[i + 1]));
            if (boundary && w.Length > 0) { words.Add(w.ToString()); w.Clear(); }
            if (c != '_') w.Append(char.ToUpperInvariant(c));
        }
        if (w.Length > 0) words.Add(w.ToString());
        return [.. words];
    }

    static void Visit(Expr e, Action<Expr> a) { a(e); foreach (var c in e.Children) Visit(c, a); }
}
