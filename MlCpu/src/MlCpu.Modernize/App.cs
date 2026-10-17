using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

/// <summary>
/// A record class: a typed view of a fixed-width record image (a copybook record, or a program's own group that is
/// redefined, subscripted or moved as a whole). The adapters of the application layer read and write through it.
/// </summary>
public sealed class RecordClass
{
    public required DataItem Item { get; init; }
    public required string Name { get; init; }
    /// <summary>Private to one program (a nested class), or shared by the programs that copy it (a top-level class).</summary>
    public bool Nested { get; init; }
    public string? Copybook { get; init; }
    /// <summary>Accessor names by item name (the programs that copy a record have their own items of it).</summary>
    public Dictionary<string, string> Accessors { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The record's fields, from the first program that uses it (offsets relative to the record).</summary>
    public List<DataItem> Fields { get; } = [];
    /// <summary>Fields the code reads or writes as a date (yyyyMMdd), by item name.</summary>
    public HashSet<string> DateViews { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Y/N indicators the code only tests for 'Y' (and validates as Y or N), by item name: a boolean accessor.</summary>
    public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Length => Item.Length;
}

/// <summary>A plain Java field for a program's own elementary item (a work field never seen as bytes); a boolean is a Y/N flag ('Y' true).</summary>
public sealed record PromotedField(string Name, JType Type, DataItem Item);

/// <summary>A program of the application layer: a batch driver (files) or a job program (the J3S job adapter).</summary>
public sealed class AppProgram
{
    public required SourceProgram P { get; init; }
    public required string ClassName { get; init; }
    public bool IsJob { get; init; }
    public List<FunctionDef> JobFunctions { get; } = [];
    /// <summary>Job functions that map an exception to a reject (they read JX-C and JX-SITE): classic code with the exception as parameter.</summary>
    public HashSet<FunctionDef> RejectFunctions { get; } = [];
    /// <summary>Job functions run once, before the first call (PERFORMed by the job's initialization).</summary>
    public List<FunctionDef> InitFunctions { get; } = [];
    public Dictionary<DataItem, RecordClass> Records { get; } = [];
    public Dictionary<DataItem, string> Instances { get; } = [];
    public Dictionary<DataItem, PromotedField> Promoted { get; } = [];
    public Dictionary<string, string> MethodNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Paragraphs lifted as methods of the program class (not the J3S runtime, not inside job functions).</summary>
    public List<Paragraph> Classic { get; } = [];
    /// <summary>The PROCEDURE DIVISION USING records (the J3S heap left out): the parameters of call().</summary>
    public List<DataItem> Using { get; } = [];
    public Dictionary<string, string> FileFields { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Programs this one CALLs: program id → the field holding its instance.</summary>
    public Dictionary<string, string> Callees { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Description { get; set; } = "";
}

/// <summary>
/// The application layer of the estate (SemanticModernization.md, ports and adapters): the programs around the
/// recovered domain code (the batch drivers and the J3S job programs), their records and their own data.
/// </summary>
public sealed class AppModel
{
    public required Model M { get; init; }
    public required string BasePackage { get; init; }
    public List<AppProgram> Programs { get; } = [];
    public Dictionary<string, AppProgram> ById { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Shared record classes by copybook and record name.</summary>
    public Dictionary<string, RecordClass> Shared { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string AppPackage => BasePackage + ".batch";
    public string RecordPackage => BasePackage + ".batch.record";
    public string PlatformPackage => BasePackage + ".batch.platform";

    static readonly HashSet<string> RuntimeRecords = new(StringComparer.OrdinalIgnoreCase) { "FS-HEAP", "FS-CTX", "FS-FRAMES", "JB", "DT-PARMS", "JO", "JO-TEXTS", "FS-STATE" };

    /// <summary>J3S runtime storage: the heap, the context and its registers, the frames, the BigDecimal and date interfaces.</summary>
    public static bool IsRuntime(DataItem it) =>
        RuntimeRecords.Contains(it.Record.Name) || System.Text.RegularExpressions.Regex.IsMatch(it.Record.Name, @"^F\d{4}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static AppModel Build(Model m)
    {
        var app = new AppModel { M = m, BasePackage = BaseOf(m) };
        foreach (var p in m.Estate.Programs)
        {
            var jobFns = m.Functions.Values.Where(f => f.IsJob && f.Program == p && !f.MethodName.Contains("STRING-LITERALS", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
            bool driver = p.Model.Files.Count > 0;
            if (jobFns.Count == 0 && !driver) continue;
            var ap = new AppProgram { P = p, IsJob = jobFns.Count > 0, ClassName = Naming.Pascal(p.Id) + (jobFns.Count > 0 ? "Job" : "Batch") };
            ap.JobFunctions.AddRange(jobFns);
            ap.Description = Describe(p);
            app.Programs.Add(ap);
            app.ById[p.Id] = ap;
        }
        foreach (var ap in app.Programs) app.Analyse(ap);
        app.Flags();
        return app;
    }

    /// <summary>The common package root of the domain classes (org.apache.fineract).</summary>
    static string BaseOf(Model m)
    {
        var pkgs = m.Classes.Values.Where(c => c.Kind == ClassKind.Domain && c.Package.Length > 0).Select(c => c.Package.Split('.')).ToList();
        if (pkgs.Count == 0) return "modern";
        var common = pkgs[0].ToList();
        foreach (var p in pkgs.Skip(1))
        {
            int k = 0;
            while (k < common.Count && k < p.Length && common[k] == p[k]) k++;
            common = common.Take(k).ToList();
        }
        return common.Count == 0 ? "modern" : string.Join('.', common);
    }

    static string Describe(SourceProgram p)
    {
        var lines = p.Comments.Values.Take(12).Select(c => c.Trim().Trim('*').Trim()).Where(c => c.Length > 0 && !c.All(ch => ch == '*')).ToList();
        return string.Join(" ", lines);
    }

    void Analyse(AppProgram ap)
    {
        var p = ap.P;
        // the paragraphs of job functions are lifted with them; the J3S runtime paragraphs are not code of the program
        var inFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in M.Functions.Values.Where(f => f.Program == p && f.FirstParagraph >= 0))
            for (int i = f.FirstParagraph; i <= f.LastParagraph && i < p.Model.Paragraphs.Count; i++) inFunctions.Add(p.Model.Paragraphs[i].Name);
        foreach (var para in p.Model.Paragraphs)
        {
            if (inFunctions.Contains(para.Name)) continue;
            if (ap.IsJob && IsRuntimeParagraph(para)) continue;
            ap.Classic.Add(para);
        }
        foreach (var para in ap.Classic) ap.MethodNames[para.Name] = MethodName(para.Name, ap.MethodNames.Values);
        foreach (var f in ap.JobFunctions) ap.MethodNames[f.Paragraph] = MethodName(f.MethodName, ap.MethodNames.Values);
        // job functions PERFORMed by the initialization paragraph run once (the constructor)
        if (ap.IsJob && p.Model.Paragraphs.FirstOrDefault(x => x.Name.EndsWith("-INIT", StringComparison.OrdinalIgnoreCase)) is { } init)
            foreach (var s in Model.Deep(init.Statements).Where(s => s.Verb == "PERFORM"))
                if (ap.JobFunctions.FirstOrDefault(f => f.Paragraph.Equals(s.Str("target"), StringComparison.OrdinalIgnoreCase)) is { } jf) ap.InitFunctions.Add(jf);
        // the initialization paragraph is the constructor (it runs the init functions; the rest is J3S runtime)
        if (ap.InitFunctions.Count > 0) ap.Classic.RemoveAll(x => x.Name.EndsWith("-INIT", StringComparison.OrdinalIgnoreCase) && !ap.JobFunctions.Any(f => f.Paragraph == x.Name));
        // the reject mapping: a job function that copies JX-SITE (the exception's site) into the reject record
        foreach (var f in ap.JobFunctions)
            if (f.FirstParagraph >= 0 && Model.Deep(p.Model.Paragraphs[f.FirstParagraph].Statements)
                    .Any(s => s.Verb == "MOVE" && s.Get("src") is { K: "ref", V: { } sv } && sv.Equals("JX-SITE", StringComparison.OrdinalIgnoreCase)))
                ap.RejectFunctions.Add(f);
        foreach (var name in p.Model.Using)
            if (p.ItemsByName.TryGetValue(name, out var u) && !IsRuntime(u)) ap.Using.Add(u);
        foreach (var fd in p.Model.Files) ap.FileFields[fd.Name] = Naming.Camel(fd.Name);
        // the data the program's code touches: references of its paragraphs and of its job functions
        var refs = new List<(Node N, Stmt S)>();
        foreach (var para in ap.Classic)
            foreach (var s in Model.Deep(para.Statements))
                foreach (var n in Model.Refs(s)) refs.Add((n, s));
        foreach (var f in ap.JobFunctions)
            for (int i = f.FirstParagraph; i >= 0 && i <= f.LastParagraph && i < p.Model.Paragraphs.Count; i++)
                foreach (var s in Model.Deep(p.Model.Paragraphs[i].Statements))
                    foreach (var n in Model.Refs(s)) refs.Add((n, s));
        var used = new HashSet<DataItem>();
        foreach (var (n, _) in refs)
            if (p.ItemsById.TryGetValue(n.Id ?? -1, out var it) && !IsRuntime(it) && it.Level != 88) used.Add(it);
            else if (p.ItemsById.TryGetValue(n.Id ?? -1, out var c88) && c88.Level == 88 && c88.Parent is { } par && !IsRuntime(par)) used.Add(par);
        foreach (var u in ap.Using) used.Add(u);
        var records = used.Select(i => i.Record).Distinct().ToList();
        // a record seen only through its elementary items, never as bytes, is plain fields of the program
        var asBytes = new HashSet<DataItem>();
        foreach (var (n, s) in refs)
        {
            if (!p.ItemsById.TryGetValue(n.Id ?? -1, out var it) || it.Level == 88) continue;
            if (!it.IsLeaf || n.Start is not null || it.Occurs > 0 || it.Dims.Count > 0 || it.RedefinesItem is not null) asBytes.Add(it.Record);
            if (s.Verb is "READ" or "WRITE" or "CALL" && !it.IsLeaf) asBytes.Add(it.Record);
        }
        foreach (var it in p.Model.Items.Where(i => i.RedefinesItem is not null)) { asBytes.Add(it.Record); asBytes.Add(it.RedefinesItem!.Record); }
        foreach (var rec in records.OrderBy(r => r.Offset).ThenBy(r => r.Line))
        {
            bool copybook = rec.Origin.Length > 0;
            bool linkage = rec.Section == DataSection.Linkage;
            if (!copybook && !linkage && !asBytes.Contains(rec) && rec.Section is DataSection.WorkingStorage or DataSection.File)
            {
                foreach (var leaf in Leaves(rec).Where(used.Contains))
                    ap.Promoted[leaf] = new PromotedField(Unique(Naming.Camel(leaf.Name), ap), FieldType(leaf), leaf);
                continue;
            }
            // FD records are buffers: READ INTO and WRITE FROM go through the target and the source
            if (rec.Section == DataSection.File && !asBytes.Contains(rec) && !Leaves(rec).Any(used.Contains)) continue;
            // a record that REDEFINES another is a second view of the same bytes: the same class and instance
            if (rec.RedefinesItem?.Record is { } baseRec && baseRec != rec && ap.Records.TryGetValue(baseRec, out var baseRc))
            {
                foreach (var leaf in Leaves(rec).Where(l => !l.IsFiller)) AddField(baseRc, leaf);
                ap.Records[rec] = baseRc;
                ap.Instances[rec] = ap.Instances[baseRec];
                continue;
            }
            var rc = RecordFor(rec, ap, copybook);
            ap.Records[rec] = rc;
            // the instance is named with its class (currencyRecord), apart from the domain objects of the same name
            ap.Instances[rec] = Unique(Naming.LowerFirst(rc.Name), ap);
        }
        // CALLs of other programs of the application layer: one instance each (a job program keeps its state across calls)
        foreach (var s in ap.Classic.SelectMany(x => Model.Deep(x.Statements)).Where(s => s.Verb == "CALL"))
            if (s.Get("target") is { K: "str", V: var callee } && callee is not null && !ap.Callees.ContainsKey(callee) && ById.ContainsKey(callee))
                ap.Callees[callee] = Unique(Naming.Camel(callee), ap);
    }

    readonly HashSet<string> takenNames = [];

    static string Unique(string name, AppProgram ap)
    {
        var taken = new HashSet<string>(ap.Promoted.Values.Select(x => x.Name).Concat(ap.Instances.Values).Concat(ap.FileFields.Values).Concat(ap.Callees.Values), StringComparer.Ordinal);
        if (taken.Add(name)) return name;
        for (int k = 2; ; k++) if (!taken.Contains(name + k)) return name + k;
    }

    static string MethodName(string cobol, IEnumerable<string> taken)
    {
        // 0100-RUN-CONTROL → runControl; job JOB-LOAN → jobLoan
        var words = cobol.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0].All(char.IsDigit)) words.RemoveAt(0);
        var n = Naming.Camel(string.Join('-', words));
        if (n is "main") n = "mainline";
        var set = taken.ToHashSet(StringComparer.Ordinal);
        if (!set.Contains(n)) return n;
        for (int k = 2; ; k++) if (!set.Contains(n + k)) return n + k;
    }

    /// <summary>A paragraph of a job program that only maintains the J3S runtime (pools, heap, tables, statistics).</summary>
    bool IsRuntimeParagraph(Paragraph para)
    {
        var name = para.Name.ToUpperInvariant();
        if (name.StartsWith("JA-") || name.StartsWith("JB-") || name.StartsWith("JD-") || name.StartsWith("JS-") || name.StartsWith("JW-") || name.StartsWith("JH-")
            || name.StartsWith("JX-") || name.StartsWith("JG-") || name.StartsWith("JR-") || name == "JO-SIMPLE-NAME") return true;
        return false;
    }

    public static IEnumerable<DataItem> Leaves(DataItem rec)
    {
        if (rec.IsLeaf) { yield return rec; yield break; }
        foreach (var c in rec.Children)
            foreach (var l in Leaves(c)) if (l.Level != 88) yield return l;
    }

    RecordClass RecordFor(DataItem rec, AppProgram ap, bool copybook)
    {
        var key = (copybook ? rec.Origin : ap.P.Id) + "/" + rec.Name;
        if (copybook && Shared.TryGetValue(key, out var shared)) return shared;
        // a program's own record is named with the program (Lcle0100DateRecord): all records are classes of the record package
        var name = copybook ? RecordName(rec.Name) : Naming.Pascal(ap.P.Id) + RecordName(rec.Name);
        // two copybooks with a record of the same name (FSOUT and FSEOUT both define FS-OUT): the copybook names the second
        if (copybook && Shared.Values.Any(r => r.Name == name)) name = Naming.Pascal(rec.Origin) + "Record";
        var rc = new RecordClass { Item = rec, Name = name, Nested = !copybook, Copybook = copybook ? rec.Origin : null };
        foreach (var leaf in Leaves(rec).Where(l => !l.IsFiller)) AddField(rc, leaf);
        if (copybook) Shared[key] = rc;
        return rc;
    }

    static readonly HashSet<string> RecordMembers = new(StringComparer.Ordinal) { "text", "bytes", "raw", "clear", "image", "length" };

    static void AddField(RecordClass rc, DataItem leaf)
    {
        if (rc.Accessors.ContainsKey(leaf.Name)) return;
        var names = new HashSet<string>(rc.Accessors.Values.Concat(RecordMembers), StringComparer.Ordinal);
        var words = leaf.Name.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0].Length <= 3) words.RemoveAt(0);
        var n = Naming.Camel(string.Join('-', words));
        if (names.Contains(n)) n = Naming.Camel(leaf.Name);
        rc.Accessors[leaf.Name] = n;
        rc.Fields.Add(leaf);
    }

    static string RecordName(string cobol)
    {
        var words = cobol.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0].Length <= 3) words.RemoveAt(0);
        if (words.Count > 1 && words[^1] is "REC" or "RECORD") words.RemoveAt(words.Count - 1);
        return Naming.Pascal(string.Join('-', words)) + "Record";
    }

    // ------------------------------------------------------------------ Y/N flags

    sealed class FlagUse
    {
        public int All, Ok;
        public bool N, InJob;
    }

    /// <summary>
    /// Y/N flags: a one-character field only set to 'Y' or 'N' and only compared with them (directly or through an 88).
    /// A work field becomes a boolean ('Y' true); a record field gets a boolean accessor (its character stays readable).
    /// A test for 'N' needs a field that holds nothing else: a work field whose initial value is 'Y' or 'N', or a record
    /// field's validation (x NOT = 'Y' AND x NOT = 'N', read as text).
    /// </summary>
    void Flags()
    {
        var recordOk = new Dictionary<(RecordClass, string), bool>();
        foreach (var ap in Programs)
        {
            var uses = new Dictionary<DataItem, FlagUse>();
            // MOVE flag TO flag between work fields: both booleans, or neither
            var copies = new List<(DataItem From, DataItem To)>();
            FlagUse U(DataItem it) => uses.TryGetValue(it, out var u) ? u : uses[it] = new FlagUse();
            foreach (var (s, inJob) in StatementsOf(ap))
            {
                var tops = (s.N?.Values ?? Enumerable.Empty<Node>()).Concat(s.L?.Values.SelectMany(l => l) ?? []).ToList();
                // validations (x NOT = 'Y' AND x NOT = 'N', x = 'Y' OR x = 'N'): their tests for 'N' are text
                var validated = new HashSet<Node>(ReferenceEqualityComparer.Instance);
                foreach (var t in tops)
                    foreach (var n in Nodes(t))
                        if (n is { K: "and" or "or", L: { } a, R: { } b } && YesNo(ap, a) is (DataItem ia, string la, string oa) && YesNo(ap, b) is (DataItem ib, string lb, string ob)
                            && ia == ib && la != lb && oa == ob && oa == (n.K == "and" ? "ne" : "eq"))
                        { validated.Add(a); validated.Add(b); }
                foreach (var t in tops)
                    foreach (var n in Nodes(t))
                    {
                        if (n.K is "ref" or "c88" && n.Id is int id && ap.P.ItemsById.TryGetValue(id, out var it))
                        {
                            var target = it.Level == 88 ? it.Parent : it;
                            if (target is null) continue;
                            var u = U(target);
                            u.All++;
                            if (inJob) u.InJob = true;
                            // an 88 of one value, 'Y' or 'N', in a condition
                            if (it.Level == 88 && s.Verb != "SET" && it.Values.Count == 1 && it.Values[0].High is null
                                && it.Values[0].Low is { Quoted: true, Text: "Y" or "N" } lo && n.Start is null)
                            {
                                u.Ok++;
                                if (lo.Text == "N") u.N = true;
                            }
                        }
                        if (YesNo(ap, n) is (DataItem yi, string yl, _))
                        {
                            U(yi).Ok++;
                            if (yl == "N" && !validated.Contains(n)) U(yi).N = true;
                        }
                    }
                // MOVE 'Y' / 'N' TO the field
                if (s.Verb == "MOVE" && s.Get("src") is { K: "str", V: "Y" or "N" })
                    foreach (var d in s.L?.GetValueOrDefault("dst") ?? [])
                        if (d.Id is int did && d.Start is null && ap.P.ItemsById.TryGetValue(did, out var dit) && dit.Level != 88) U(dit).Ok++;
                // MOVE a work flag TO a work flag
                if (s.Verb == "MOVE" && s.Get("src") is { K: "ref", Start: null, Id: int sid } && ap.P.ItemsById.TryGetValue(sid, out var sit) && sit.Level != 88 && ap.Promoted.ContainsKey(sit) && sit.Length == 1)
                    foreach (var d in s.L?.GetValueOrDefault("dst") ?? [])
                        if (d.Id is int did2 && d.Start is null && ap.P.ItemsById.TryGetValue(did2, out var dit2) && dit2.Level != 88 && ap.Promoted.ContainsKey(dit2) && dit2.Length == 1)
                        {
                            U(sit).Ok++;
                            U(dit2).Ok++;
                            copies.Add((sit, dit2));
                        }
            }
            // work flags tested for 'N' whose initial value is not Y or N: safe when every read follows an assignment
            var unset = uses.Where(kv => ap.Promoted.ContainsKey(kv.Key) && kv.Value.N && !kv.Value.InJob
                && !(kv.Key.Values.Count == 1 && kv.Key.Values[0].Low is { Quoted: true, Text: "Y" or "N" })).Select(kv => kv.Key).ToHashSet();
            var setFirst = unset.Count == 0 ? [] : AssignedBeforeRead(ap, unset);
            bool Shape(DataItem it, FlagUse u) => it.IsLeaf && it.Length == 1 && it.Category is not (Category.Numeric or Category.NumericEdited) && u.All == u.Ok;
            var work = uses.Where(kv => ap.Promoted.ContainsKey(kv.Key) && Shape(kv.Key, kv.Value) && !kv.Value.InJob
                && (kv.Key.Values.Count == 1 && kv.Key.Values[0].Low is { Quoted: true, Text: "Y" or "N" } || !kv.Value.N || setFirst.Contains(kv.Key))).Select(kv => kv.Key).ToHashSet();
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (var (from, to) in copies)
                    // a flag copied into a code is converted (dwOk = dwDateOk ? "Y" : "N"); a code copied into a field makes it a code
                    if (!work.Contains(from) && work.Remove(to)) changed = true;
            }
            foreach (var it in work) ap.Promoted[it] = ap.Promoted[it] with { Type = JType.Bool };
            foreach (var (it, u) in uses.OrderBy(kv => kv.Key.Line))
            {
                bool flag = Shape(it, u);
                if (ap.Promoted.ContainsKey(it)) continue;
                if (RecordOf(ap, it) is not { } r) continue;
                var key = (r.Rc, it.Name);
                recordOk[key] = (recordOk.TryGetValue(key, out var prev) ? prev : true) && flag && !u.N;
            }
        }
        foreach (var ((rc, name), ok) in recordOk) if (ok) rc.Flags.Add(name);
    }

    /// <summary>
    /// The flags whose every read follows an assignment on every path from the program's entry (their initial value is
    /// never seen): a definite-assignment walk of the classic paragraphs from the first, PERFORMs followed, a loop's body
    /// possibly never run, an IF or EVALUATE assigning only what all its branches assign. A GO TO, a recursive PERFORM or
    /// an entry paragraph that falls through make the answer empty.
    /// </summary>
    HashSet<DataItem> AssignedBeforeRead(AppProgram ap, HashSet<DataItem> flags)
    {
        var paras = ap.P.Model.Paragraphs;
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < paras.Count; i++) index.TryAdd(paras[i].Name, i);
        var bad = new HashSet<DataItem>();
        var active = new HashSet<int>();
        bool unknown = false;
        DataItem? Flag(Node n)
        {
            if (n.K is not ("ref" or "c88") || n.Id is not int id || !ap.P.ItemsById.TryGetValue(id, out var it)) return null;
            var f = it.Level == 88 ? it.Parent : it;
            return f is not null && flags.Contains(f) ? f : null;
        }
        void Reads(Stmt s, HashSet<DataItem> a)
        {
            var dst = s.Verb == "MOVE" ? s.L?.GetValueOrDefault("dst") ?? [] : [];
            foreach (var t in (s.N?.Values ?? Enumerable.Empty<Node>()).Concat(s.L?.Where(kv => kv.Key != "dst" || s.Verb != "MOVE").SelectMany(kv => kv.Value) ?? []))
                foreach (var n in Nodes(t))
                    if (Flag(n) is { } f && !a.Contains(f)) bad.Add(f);
        }
        static HashSet<DataItem>? Meet(IEnumerable<HashSet<DataItem>?> states)
        {
            HashSet<DataItem>? acc = null;
            bool any = false;
            foreach (var st in states)
            {
                if (st is null) continue; // a path that does not complete
                acc = acc is null ? new HashSet<DataItem>(st) : acc.Intersect(st).ToHashSet();
                any = true;
            }
            return any ? acc : null;
        }
        // the state after the statements (null: they do not complete)
        HashSet<DataItem>? Walk(IEnumerable<Stmt> stmts, HashSet<DataItem>? a)
        {
            foreach (var s in stmts)
            {
                if (a is null || unknown) return a;
                switch (s.Verb)
                {
                    case "MOVE":
                        Reads(s, a);
                        a = new HashSet<DataItem>(a);
                        foreach (var d in s.L?.GetValueOrDefault("dst") ?? []) if (Flag(d) is { } f) a.Add(f);
                        break;
                    case "IF":
                        Reads(s, a);
                        a = Meet([Walk(s.Blocks?.GetValueOrDefault("TRUE") ?? [], a), Walk(s.Blocks?.GetValueOrDefault("FALSE") ?? [], a)]);
                        break;
                    case "EVALUATE":
                    {
                        Reads(s, a);
                        var whens = s.Blocks?.GetValueOrDefault("WHEN") ?? [];
                        var outs = new List<HashSet<DataItem>?>();
                        foreach (var w in whens)
                        {
                            Reads(w, a);
                            outs.Add(Walk(w.Blocks?.GetValueOrDefault("TRUE") ?? [], a));
                        }
                        if (!whens.Any(w => w.Str("other") == "true")) outs.Add(a);
                        a = Meet(outs);
                        break;
                    }
                    case "READ":
                        Reads(s, a);
                        a = Meet([Walk(s.Blocks?.GetValueOrDefault("AT-END") ?? s.Blocks?.GetValueOrDefault("INVALID") ?? [], a),
                                  Walk(s.Blocks?.GetValueOrDefault("NOT-AT-END") ?? s.Blocks?.GetValueOrDefault("NOT-INVALID") ?? [], a)]);
                        break;
                    case "PERFORM":
                    {
                        Reads(s, a);
                        bool loop = s.N?.Count > 0 || s.L?.Count > 0;
                        HashSet<DataItem>? after;
                        if (s.Str("inline") == "true") after = Walk(s.Blocks?.GetValueOrDefault("LOOP") ?? [], a);
                        else
                        {
                            int from = index.GetValueOrDefault(s.Str("target") ?? "", -1);
                            int to = s.Str("thru") is { } thru ? index.GetValueOrDefault(thru, from) : from;
                            if (from < 0) { unknown = true; return a; }
                            after = a;
                            for (int i = from; i <= to && after is not null; i++)
                            {
                                if (!active.Add(i)) { unknown = true; return a; }
                                after = Walk(paras[i].Statements, after);
                                active.Remove(i);
                            }
                        }
                        // a loop's body may not run at all; a plain PERFORM returns unless its paragraphs stop the run
                        a = loop ? a : after ?? null;
                        break;
                    }
                    case "GO TO":
                        unknown = true;
                        return a;
                    case "GOBACK": case "STOP RUN": case "EXIT PROGRAM":
                        return null;
                    default:
                        Reads(s, a);
                        break;
                }
            }
            return a;
        }
        if (ap.Classic.FirstOrDefault() is not { } entry || !index.TryGetValue(entry.Name, out var e0)) return [];
        active.Add(e0);
        var end = Walk(entry.Statements, []);
        // the entry paragraph falls through to the next one: not followed here
        if (unknown || end is not null) return [];
        return flags.Where(f => !bad.Contains(f)).ToHashSet();
    }

    /// <summary>A comparison of a field with 'Y' or 'N' (either side): the field, the letter and eq/ne; else nulls.</summary>
    static (DataItem? Item, string? Letter, string? Op) YesNo(AppProgram ap, Node n)
    {
        if (n is not { K: "rel", Op: "eq" or "ne" } || n.L is null || n.R is null) return (null, null, null);
        Node? f = null, l = null;
        if (n.L.K == "ref" && n.R is { K: "str", V: "Y" or "N" }) (f, l) = (n.L, n.R);
        else if (n.R.K == "ref" && n.L is { K: "str", V: "Y" or "N" }) (f, l) = (n.R, n.L);
        if (f is null || l is null || f.Start is not null || f.Id is not int id || !ap.P.ItemsById.TryGetValue(id, out var it) || it.Level == 88) return (null, null, null);
        return (it, l.V, n.Op);
    }

    static IEnumerable<Node> Nodes(Node n)
    {
        yield return n;
        foreach (var c in new[] { n.L, n.R, n.Start, n.Len }) if (c is not null) foreach (var x in Nodes(c)) yield return x;
        foreach (var a in n.Args ?? []) foreach (var x in Nodes(a)) yield return x;
    }

    /// <summary>The statements of a program's classic paragraphs, and of its job functions (true).</summary>
    IEnumerable<(Stmt S, bool InJob)> StatementsOf(AppProgram ap)
    {
        foreach (var para in ap.Classic) foreach (var s in Model.Deep(para.Statements)) yield return (s, false);
        foreach (var f in ap.JobFunctions)
            for (int i = f.FirstParagraph; i >= 0 && i <= f.LastParagraph && i < ap.P.Model.Paragraphs.Count; i++)
                foreach (var s in Model.Deep(ap.P.Model.Paragraphs[i].Statements)) yield return (s, true);
    }

    /// <summary>The Java type of an elementary item.</summary>
    public static JType FieldType(DataItem it)
    {
        if (it.Category is Category.Numeric)
        {
            if (it.Scale > 0) return JType.BigDecimal;
            if (it.Digits > 18) return JType.Of("java.math.BigInteger");
            return it.Digits > 9 ? JType.Long : JType.Int;
        }
        return JType.String;
    }

    /// <summary>The record class that holds an item in a program, and the instance (field) name.</summary>
    public (RecordClass Rc, string Instance)? RecordOf(AppProgram ap, DataItem it) =>
        ap.Records.TryGetValue(it.Record, out var rc) ? (rc, ap.Instances[it.Record]) : null;
}
