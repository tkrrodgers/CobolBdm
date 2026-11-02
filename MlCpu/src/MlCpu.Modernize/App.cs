using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

/// <summary>
/// A record class: a typed view of a fixed-width record image (a copybook record, or a program's own group that is
/// redefined, subscripted or moved as a whole). The adapters of the application layer read and write through it.
/// </summary>
public sealed class RecordClass
{
    public RecordClass()
    {
        var names = new AliasComparer(Alias);
        DateViews = new(names);
        Flags = new(names);
        Codes = new(names);
        Initializers = new(names);
        Edited = new(names);
    }

    /// <summary>Numeric-edited fields the code moves a number to, by item name: a setter that edits it (the PICTURE in the codec).</summary>
    public HashSet<string> Edited { get; }

    public required DataItem Item { get; init; }
    public required string Name { get; init; }
    /// <summary>Private to one program (a nested class), or shared by the programs that copy it (a top-level class).</summary>
    public bool Nested { get; init; }
    public string? Copybook { get; init; }
    /// <summary>Accessor names by item name (the programs that copy a record have their own items of it).</summary>
    public Dictionary<string, string> Accessors { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Item names of another copy of the same layout (COPY ... REPLACING ==:M:== BY ==MA== and BY ==LM==) → the name of
    /// the field they are: one record class serves every copy, so a caller's record is the parameter its callee declares.
    /// </summary>
    public Dictionary<string, string> Alias { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The record's fields, from the first program that uses it (offsets relative to the record).</summary>
    public List<DataItem> Fields { get; } = [];
    /// <summary>Fields the code reads or writes as a date (yyyyMMdd), by item name.</summary>
    public HashSet<string> DateViews { get; }
    /// <summary>Y/N indicators the code only tests for 'Y' (and validates as Y or N), by item name: a boolean accessor.</summary>
    public HashSet<string> Flags { get; }
    /// <summary>Named views of the image's characters the code reads or writes (a number's digits, a sign, adjacent fields): name → bytes.</summary>
    public Dictionary<string, (int Offset, int Length, string Doc)> Views { get; } = new(StringComparer.Ordinal);
    /// <summary>Code fields read as enums (a closed set of codes the code compares and switches on): item name → enum name and codes.</summary>
    public Dictionary<string, (string Enum, SortedSet<string> Codes, Dictionary<string, string> Names)> Codes { get; }
    /// <summary>Groups the code INITIALIZEs (the record itself, or a group of it), by item name: their initialize method.</summary>
    public Dictionary<string, string> Initializers { get; }
    public int Length => Item.Length;

    /// <summary>The name of the field an item of this layout is (its own name, or the first copy's).</summary>
    public string Canon(string name) => Alias.TryGetValue(name, out var c) ? c : name;

    /// <summary>Item names compared through <see cref="Alias"/>: two copies' names of one field are equal.</summary>
    sealed class AliasComparer(Dictionary<string, string> alias) : IEqualityComparer<string>
    {
        string C(string s) => alias.TryGetValue(s, out var c) ? c : s;
        public bool Equals(string? a, string? b) => a is null || b is null ? a == b : string.Equals(C(a), C(b), StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(string s) => StringComparer.OrdinalIgnoreCase.GetHashCode(C(s));
    }

    /// <summary>
    /// The named view of the image's bytes [offset, offset + length): a text field (its Text accessor), a number's
    /// characters, a SIGN LEADING SEPARATE field's sign or digits, a group, or adjacent fields; null when the bytes are
    /// no such part (they stay a positional read).
    /// </summary>
    public string? View(int offset, int length)
    {
        foreach (var (name, v) in Views) if (v.Offset == offset && v.Length == length) return name;
        static int Off(DataItem d) => d.Offset - d.Record.Offset;
        var leaves = Fields.Where(f => f.Dims.Count == 0 && f.Occurs == 0).ToList();
        string? Add(string name, string doc)
        {
            var textAccessors = Fields.Where(f => f.Category != Category.Numeric && Accessors.ContainsKey(f.Name)).Select(f => Accessors[f.Name] + "Text");
            if (Accessors.Values.Contains(name) || textAccessors.Contains(name) || Views.ContainsKey(name) || name is "text" or "raw" or "bytes") return null;
            Views[name] = (offset, length, doc);
            return name;
        }
        if (leaves.FirstOrDefault(f => Off(f) == offset && f.Length == length) is { } leaf && Accessors.TryGetValue(leaf.Name, out var acc))
            return leaf.Category == Category.Numeric ? Add(acc + "Text", $"{leaf.Name}: its characters") : acc + "Text";
        foreach (var f in leaves.Where(f => f.Category == Category.Numeric && f.Signed && f.SignSeparate))
        {
            if (!Accessors.TryGetValue(f.Name, out var sa)) continue;
            if (Off(f) == offset && length == 1) return Add(sa + "Sign", $"{f.Name}: its sign character");
            if (Off(f) + 1 == offset && f.Length - 1 == length) return Add(sa + "Digits", $"{f.Name}: its digits");
        }
        var group = Descendants(Item).FirstOrDefault(d => !d.IsLeaf && !d.IsFiller && d.Dims.Count == 0 && Off(d) == offset && d.Length == length);
        if (group is not null)
        {
            var words = group.Name.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (words.Count > 1 && words[0].Length <= 3) words.RemoveAt(0);
            return Add(Naming.Camel(string.Join('-', words)) + "Text", $"{group.Name}: its characters");
        }
        // adjacent fields of the record's own layout (not a REDEFINES alternative), at most four
        var own = leaves.Where(f => !Ancestry(f).Any(a => a.RedefinesItem is not null) && !f.IsFiller).OrderBy(Off).ToList();
        var run = new List<DataItem>();
        int at = offset;
        while (at < offset + length && run.Count < 4 && own.FirstOrDefault(f => Off(f) == at) is { } next && Accessors.ContainsKey(next.Name)) { run.Add(next); at += next.Length; }
        if (run.Count >= 2 && at == offset + length)
            return Add(Accessors[run[0].Name] + string.Concat(run.Skip(1).Select(r => Naming.UpperFirst(Accessors[r.Name]))) + "Text", string.Join(" and ", run.Select(r => r.Name)) + ": their characters");
        return null;
    }

    static IEnumerable<DataItem> Descendants(DataItem d)
    {
        foreach (var c in d.Children)
        {
            yield return c;
            foreach (var x in Descendants(c)) yield return x;
        }
    }

    static IEnumerable<DataItem> Ancestry(DataItem d) { for (var x = d; x is not null; x = x.Parent) yield return x; }
}

/// <summary>A plain Java field for a program's own elementary item (a work field never seen as bytes); a boolean is a Y/N flag ('Y' true).</summary>
public sealed record PromotedField(string Name, JType Type, DataItem Item);

/// <summary>A program of the application layer: a batch driver (files) or a job program (the J3S job adapter).</summary>
public sealed class AppProgram
{
    public required SourceProgram P { get; init; }
    public required string ClassName { get; set; }
    /// <summary>A subprogram whose CALL interface is a known JDK contract (Kernels): its class is the JDK's, the lifted code its test reference.</summary>
    public KernelContract? Kernel { get; set; }
    /// <summary>The header evidence the class is named from (Q2); null: named with its program id.</summary>
    public HeaderNames.Evidence? NameEvidence { get; set; }
    public bool IsJob { get; init; }
    /// <summary>A CALLed subprogram (PROCEDURE DIVISION USING, no datasets): a class whose public entry takes the caller's records.</summary>
    public bool IsSubprogram { get; init; }
    /// <summary>A subprogram whose performed paragraphs end the CALL (GOBACK outside the entry paragraph): call() catches their CallEnded.</summary>
    public bool EndsCallInParagraph { get; set; }
    /// <summary>A batch step (main(), datasets by DD name).</summary>
    public bool IsStep => !IsJob && !IsSubprogram;
    /// <summary>A subprogram's LINKAGE records as parameters: record (and a record that REDEFINES it) → its variable.</summary>
    public Dictionary<DataItem, Var> LinkageVars { get; } = [];
    /// <summary>A subprogram's USING records in order (the entry method's parameters).</summary>
    public List<DataItem> LinkageOrder { get; } = [];
    /// <summary>The LINKAGE records each paragraph reads or writes, the paragraphs it PERFORMs included (USING order).</summary>
    public Dictionary<string, List<DataItem>> ParagraphLinkage { get; } = new(StringComparer.OrdinalIgnoreCase);
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
    /// <summary>The restructured data flow of a batch step (ClassicFlow): the fields that remain, its nested types, its report.</summary>
    public FlowResult? Flow { get; set; }

    /// <summary>The names of the program class's fields (locals of its methods take other names).</summary>
    public IEnumerable<string> FieldNames() => Flow is { } f
        ? Instances.Values.Where(i => !f.DroppedInstances.Contains(i)).Concat(f.Fields.Select(x => x.Name)).Concat(FileFields.Values).Concat(Callees.Values).Concat(f.ReportField is null ? [] : [f.ReportField]).Concat(f.Nested.Select(n => n.Field))
        : Instances.Where(kv => !LinkageVars.ContainsKey(kv.Key)).Select(kv => kv.Value).Concat(Promoted.Values.Select(p => p.Name)).Concat(FileFields.Values).Concat(Callees.Values);
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
    /// <summary>An abend routine reports its abend with the lines it displays (Abend's display constructor).</summary>
    public bool DisplayedAbends { get; set; }

    static readonly HashSet<string> RuntimeRecords = new(StringComparer.OrdinalIgnoreCase) { "FS-HEAP", "FS-CTX", "FS-FRAMES", "JB", "DT-PARMS", "JO", "JO-TEXTS", "FS-STATE" };

    /// <summary>
    /// Records named as J3S runtime storage in an estate that has no J3S runtime (a Tier-1 estate's DT-PARMS is the
    /// date kernel's own LINKAGE record, a record of the application like any other).
    /// </summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataItem, object> PlainRecords = new();

    /// <summary>J3S runtime storage: the heap, the context and its registers, the frames, the BigDecimal and date interfaces.</summary>
    public static bool IsRuntime(DataItem it) =>
        RuntimeRecords.Contains(it.Record.Name) && !PlainRecords.TryGetValue(it.Record, out _)
        || System.Text.RegularExpressions.Regex.IsMatch(it.Record.Name, @"^F\d{4}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>A J3S estate: a program keeps the J3S context, heap or frames.</summary>
    static bool IsJ3s(Model m) => m.Functions.Count > 0 || m.Estate.Programs.Any(p => p.Model.Items.Any(i => i.Level == 1
        && (i.Name.Equals("FS-CTX", StringComparison.OrdinalIgnoreCase) || i.Name.Equals("FS-HEAP", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(i.Name, @"^F\d{4}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))));

    public static AppModel Build(Model m)
    {
        var app = new AppModel { M = m, BasePackage = BaseOf(m) };
        if (!IsJ3s(m))
            foreach (var p in m.Estate.Programs)
                foreach (var it in p.Model.Items.Where(i => i.Level == 1 && RuntimeRecords.Contains(i.Name)))
                    PlainRecords.AddOrUpdate(it, true);
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
        foreach (var ap in app.Programs.ToList()) app.Analyse(ap);
        app.Subprograms();
        // a decimal library its callers CALL (recognized once every caller is known): the JDK, proven by its gate
        foreach (var ap in app.Programs.Where(p => p.IsSubprogram && p.Kernel is null).ToList())
            if (DecimalKernels.Recognize(app, ap) is { } dk) { ap.Kernel = dk; ap.ClassName = dk.ClassName; }
        app.NameSubprograms();
        foreach (var ap in app.Programs) app.Wire(ap);
        app.Flags();
        return app;
    }

    /// <summary>
    /// The subprograms the application layer CALLs (statically, by literal name), and theirs: a program without datasets
    /// or J3S functions whose PROCEDURE DIVISION USING names records of the application (not J3S runtime storage), CALLed
    /// with arguments that are such records.
    /// </summary>
    void Subprograms()
    {
        var queue = new Queue<AppProgram>(Programs);
        while (queue.Count > 0)
        {
            var caller = queue.Dequeue();
            foreach (var s in caller.Classic.SelectMany(x => Model.Deep(x.Statements)).Where(s => s.Verb == "CALL"))
            {
                if (s.Get("target") is not { K: "str", V: { } target } || ById.ContainsKey(target) || !M.Estate.ById.TryGetValue(target, out var sp)) continue;
                var args = (s.L?.GetValueOrDefault("args") ?? []).Select(a => a.L ?? a).ToList();
                if (args.Count == 0 || args.All(a => caller.P.ItemsById.TryGetValue(a.Id ?? -1, out var ai) && IsRuntime(ai))) continue;
                if (sp.Model.Files.Count > 0 || M.Functions.Values.Any(f => f.Program == sp)) continue;
                var using_ = sp.Model.Using.Select(u => sp.ItemsByName.GetValueOrDefault(u)).ToList();
                if (using_.Count == 0 || using_.Any(u => u is null || IsRuntime(u))) continue;
                var ap = new AppProgram { P = sp, IsSubprogram = true, ClassName = Naming.Pascal(sp.Id) };
                ap.Description = Describe(sp);
                Programs.Add(ap);
                ById[sp.Id] = ap;
                Analyse(ap);
                // a known kernel contract (by its LINKAGE layout and function codes): the JDK, proven by its gate
                if (Kernels.Recognize(ap) is { } kernel) { ap.Kernel = kernel; ap.ClassName = kernel.ClassName; }
                queue.Enqueue(ap);
            }
        }
    }

    /// <summary>
    /// The subprograms' class names from their headers (Q2), where the name is unique among the application's classes;
    /// a kernel the JDK replaces is named by its contract, a program whose header names nothing keeps its program id.
    /// </summary>
    void NameSubprograms()
    {
        var named = Programs.Where(p => p.IsSubprogram && p.Kernel is null).Select(p => (P: p, E: HeaderNames.FromHeader(p.P))).Where(x => x.E is not null).ToList();
        var taken = new HashSet<string>(Programs.Where(p => !named.Any(n => n.P == p)).Select(p => p.ClassName), StringComparer.OrdinalIgnoreCase);
        foreach (var group in named.GroupBy(n => n.E!.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() > 1 || taken.Contains(group.Key) || group.Key.EndsWith("Record", StringComparison.Ordinal)) continue;
            var (p, e) = group.First();
            p.ClassName = e!.Name;
            p.NameEvidence = e;
        }
    }

    /// <summary>The programs a program CALLs: one instance each (a job program keeps its state across calls).</summary>
    void Wire(AppProgram ap)
    {
        foreach (var s in ap.Classic.SelectMany(x => Model.Deep(x.Statements)).Where(s => s.Verb == "CALL"))
            if (s.Get("target") is { K: "str", V: var callee } && callee is not null && !ap.Callees.ContainsKey(callee) && ById.TryGetValue(callee, out var cp))
                ap.Callees[callee] = Unique(cp.Kernel is not null || cp.NameEvidence is not null ? Naming.LowerFirst(cp.ClassName) : Naming.Camel(callee), ap);
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
        // the data the program's code touches: references of its paragraphs and of its job functions (a condition
        // name's reference is its field's)
        var refs = new List<(Node N, Stmt S)>();
        foreach (var para in ap.Classic)
            foreach (var s in Model.Deep(para.Statements))
                foreach (var n in Model.Refs(s).Concat(StmtNodes(s).Where(x => x.K == "c88"))) refs.Add((n, s));
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
            if (!p.ItemsById.TryGetValue(n.Id ?? -1, out var it)) continue;
            // a condition name of a table's field: the table is bytes of a record
            if (it.Level == 88) { if (it.Parent is { } cp && (cp.Dims.Count > 0 || cp.RedefinesItem is not null)) asBytes.Add(cp.Record); continue; }
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
                    ap.Promoted[leaf] = new PromotedField(Unique(Naming.Field(leaf.Name), ap), FieldType(leaf), leaf);
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
            ap.Instances[rec] = Unique(ap.IsSubprogram && linkage ? ParamName(rec.Name) : Naming.LowerFirst(rc.Name), ap);
        }
        // several records of one class (copies of a layout: PRINCIPAL-VALUE, PAYMENT-VALUE): each instance is named by its
        // own record (principalValue, paymentValue), not numbered after the class
        foreach (var group in ap.Records.Where(kv => kv.Key.Section != DataSection.Linkage && kv.Key.RedefinesItem is null).GroupBy(kv => kv.Value).Where(g => g.Count() > 1).ToList())
        {
            foreach (var (rec, _) in group) ap.Instances.Remove(rec);
            foreach (var (rec, _) in group.OrderBy(kv => kv.Key.Line)) ap.Instances[rec] = Unique(Naming.Camel(rec.Name), ap);
        }
        if (ap.IsSubprogram) Linkage(ap);
    }

    /// <summary>A LINKAGE record's parameter name: its words without a short prefix or a REC suffix (LK-MODEL-QUERY → modelQuery).</summary>
    static string ParamName(string cobol)
    {
        var words = cobol.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0].Length <= 3) words.RemoveAt(0);
        if (words.Count > 1 && words[^1] is "REC" or "RECORD" or "PARMS" or "PARM") words.RemoveAt(words.Count - 1);
        return Naming.Camel(string.Join('-', words));
    }

    /// <summary>
    /// A subprogram's LINKAGE records as parameters: of its entry, and of every paragraph that reads or writes them (or
    /// PERFORMs one that does), so that no paragraph keeps the caller's storage in a field.
    /// </summary>
    void Linkage(AppProgram ap)
    {
        var p = ap.P;
        foreach (var name in p.Model.Using)
        {
            if (!p.ItemsByName.TryGetValue(name, out var u) || !ap.Records.TryGetValue(u.Record, out var rc)) continue;
            var v = new Var { Name = ap.Instances[u.Record], Type = JType.Of(RecordPackage + "." + rc.Name), IsParam = true, Fixed = true };
            ap.LinkageVars[u.Record] = v;
            ap.LinkageOrder.Add(u.Record);
        }
        foreach (var rec in ap.Records.Keys.Where(r => r.RedefinesItem?.Record is { } b && ap.LinkageVars.ContainsKey(b)))
            ap.LinkageVars.TryAdd(rec, ap.LinkageVars[rec.RedefinesItem!.Record]);
        var paras = p.Model.Paragraphs;
        var direct = new Dictionary<int, HashSet<DataItem>>();
        var performs = new Dictionary<int, HashSet<int>>();
        for (int i = 0; i < paras.Count; i++)
        {
            var d = direct[i] = [];
            var calls = performs[i] = [];
            foreach (var s in Model.Deep(paras[i].Statements))
            {
                foreach (var n in StmtNodes(s))
                    if (n.K is "ref" or "c88" && p.ItemsById.TryGetValue(n.Id ?? -1, out var it) && ap.LinkageVars.TryGetValue(it.Record, out var lv))
                        d.Add(ap.LinkageOrder.First(r => ap.LinkageVars[r] == lv));
                if (s.Verb == "PERFORM" && s.Str("inline") != "true" && s.Str("target") is { } t && p.ParagraphIndex.TryGetValue(t, out var from))
                {
                    int to = s.Str("thru") is { } thru && p.ParagraphIndex.TryGetValue(thru, out var th) ? th : from;
                    for (int k = from; k <= to; k++) calls.Add(k);
                }
            }
        }
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var (i, calls) in performs)
                foreach (var k in calls)
                    foreach (var r in direct[k])
                        if (direct[i].Add(r)) changed = true;
        }
        for (int i = 0; i < paras.Count; i++)
            ap.ParagraphLinkage[paras[i].Name] = ap.LinkageOrder.Where(direct[i].Contains).ToList();
        // the entry takes every USING record (the caller passes them all)
        if (ap.Classic.Count > 0) ap.ParagraphLinkage[ap.Classic[0].Name] = [.. ap.LinkageOrder];
    }

    /// <summary>Every node of a statement's operands (not of its nested statements).</summary>
    internal static IEnumerable<Node> StmtNodes(Stmt s) =>
        (s.N?.Values ?? Enumerable.Empty<Node>()).Concat(s.L?.Values.SelectMany(l => l) ?? []).SelectMany(Nodes);

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

    static IEnumerable<DataItem> Descendants(DataItem d)
    {
        foreach (var c in d.Children)
        {
            yield return c;
            foreach (var x in Descendants(c)) yield return x;
        }
    }

    RecordClass RecordFor(DataItem rec, AppProgram ap, bool copybook)
    {
        var key = (copybook ? rec.Origin : ap.P.Id) + "/" + rec.Name;
        if (copybook && Shared.TryGetValue(key, out var shared))
        {
            // the same record name with other item names (COPY FNSTATX REPLACING ==:P:== BY ==XT==): the names are aliases
            var (sd, rd) = (Descendants(shared.Item).ToList(), Descendants(rec).ToList());
            if (sd.Count == rd.Count)
                foreach (var (a, b) in sd.Zip(rd))
                {
                    if (a.Level == 88 || a.IsFiller || a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    shared.Alias[b.Name] = a.Name;
                    if (shared.Accessors.TryGetValue(a.Name, out var sacc)) shared.Accessors[b.Name] = sacc;
                }
            return shared;
        }
        // another copy of a copybook's record (COPY ... REPLACING a prefix): the same class, its names aliases of the first's
        if (copybook && Shared.Values.FirstOrDefault(r => r.Copybook == rec.Origin && SameLayout(r.Item, rec)) is { } same)
        {
            foreach (var (a, b) in Descendants(same.Item).Prepend(same.Item).Zip(Descendants(rec).Prepend(rec)))
            {
                if (a.Level == 88 || a.IsFiller || a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase)) continue;
                same.Alias[b.Name] = a.Name;
                if (same.Accessors.TryGetValue(a.Name, out var acc)) same.Accessors[b.Name] = acc;
            }
            Shared[key] = same;
            return same;
        }
        // a program's own record is named with the program (Lcle0100DateRecord): all records are classes of the record package
        var name = copybook ? RecordName(rec.Name) : Naming.Pascal(ap.P.Id) + RecordName(rec.Name);
        // two copybooks with a record of the same name (FSOUT and FSEOUT both define FS-OUT): the copybook names the second
        if (copybook && Shared.Values.Any(r => r.Name == name)) name = Naming.Pascal(rec.Origin) + "Record";
        var rc = new RecordClass { Item = rec, Name = name, Nested = !copybook, Copybook = copybook ? rec.Origin : null };
        foreach (var leaf in Leaves(rec).Where(l => !l.IsFiller)) AddField(rc, leaf);
        if (copybook) Shared[key] = rc;
        return rc;
    }

    // a record's own members, and Object's (a field named EQUALS must not overload equals(Object))
    static readonly HashSet<string> RecordMembers = new(StringComparer.Ordinal)
    {
        "text", "bytes", "raw", "clear", "image", "length",
        "equals", "hashCode", "toString", "getClass", "wait", "notify", "notifyAll", "clone", "finalize",
    };

    /// <summary>Two copies of one copybook record: the same items at the same offsets, their names the same past their first word.</summary>
    static bool SameLayout(DataItem a, DataItem b)
    {
        static string Tail(string n) => n.IndexOf('-') is var k and > 0 ? n[(k + 1)..] : n;
        var da = Descendants(a).Prepend(a).ToList();
        var db = Descendants(b).Prepend(b).ToList();
        if (da.Count != db.Count) return false;
        for (int i = 0; i < da.Count; i++)
        {
            var (x, y) = (da[i], db[i]);
            if (x.Level != y.Level || x.Offset - x.Record.Offset != y.Offset - y.Record.Offset || x.Length != y.Length || x.Usage != y.Usage
                || x.Pic != y.Pic || x.Occurs != y.Occurs || x.IsFiller != y.IsFiller || !Tail(x.Name).Equals(Tail(y.Name), StringComparison.OrdinalIgnoreCase)) return false;
            if (x.Level == 88 && !x.Values.Select(v => v.Low.Text).SequenceEqual(y.Values.Select(v => v.Low.Text))) return false;
        }
        return !a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase);
    }

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
        /// <summary>Evidence of a Y/N flag (a test for, or a MOVE or SET of, 'Y' or 'N'); a field only copied into a record has none.</summary>
        public bool Yn, RecordCopy;
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
        var recordYn = new HashSet<(RecordClass, string)>();
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
                                u.Yn = true;
                                if (lo.Text == "N") u.N = true;
                            }
                            // SET flag-88 TO TRUE: the MOVE of its one value, 'Y' or 'N'
                            else if (it.Level == 88 && s.Verb == "SET" && s.Str("mode") == "TRUE" && it.Values.Count == 1 && it.Values[0].High is null
                                && it.Values[0].Low is { Quoted: true, Text: "Y" or "N" } && n.Start is null && n.Args is null or { Count: 0 })
                            { u.Ok++; u.Yn = true; }
                        }
                        if (YesNo(ap, n) is (DataItem yi, string yl, _))
                        {
                            U(yi).Ok++;
                            U(yi).Yn = true;
                            if (yl == "N" && !validated.Contains(n)) U(yi).N = true;
                        }
                    }
                // MOVE 'Y' / 'N' TO the field
                if (s.Verb == "MOVE" && s.Get("src") is { K: "str", V: "Y" or "N" })
                    foreach (var d in s.L?.GetValueOrDefault("dst") ?? [])
                        if (d.Id is int did && d.Start is null && ap.P.ItemsById.TryGetValue(did, out var dit) && dit.Level != 88) { U(dit).Ok++; U(dit).Yn = true; }
                // MOVE a work flag TO a work flag
                if (s.Verb == "MOVE" && s.Get("src") is { K: "ref", Start: null, Id: int sid } && ap.P.ItemsById.TryGetValue(sid, out var sit) && sit.Level != 88 && ap.Promoted.ContainsKey(sit) && sit.Length == 1)
                    foreach (var d in s.L?.GetValueOrDefault("dst") ?? [])
                        if (d.Id is int did2 && d.Start is null && ap.P.ItemsById.TryGetValue(did2, out var dit2) && dit2.Level != 88 && ap.Promoted.ContainsKey(dit2) && dit2.Length == 1)
                        {
                            U(sit).Ok++;
                            U(dit2).Ok++;
                            copies.Add((sit, dit2));
                        }
                // MOVE a one-character field TO a record's one-character field: the record keeps the character (a flag
                // field's boolean accessor reads it as 'Y' is true, a work flag is written as 'Y' or 'N')
                if (s.Verb == "MOVE" && s.Get("src") is { K: "ref", Start: null, Id: int rsid } && ap.P.ItemsById.TryGetValue(rsid, out var rsit) && OneChar(rsit))
                    foreach (var d in s.L?.GetValueOrDefault("dst") ?? [])
                        if (d.Id is int rdid && d.Start is null && (d.Args is null || d.Args.Count == 0) && ap.P.ItemsById.TryGetValue(rdid, out var rdit) && OneChar(rdit)
                            && !ap.Promoted.ContainsKey(rdit) && RecordOf(ap, rdit) is not null && (ap.Promoted.ContainsKey(rsit) || RecordOf(ap, rsit) is not null))
                        {
                            U(rsit).Ok++;
                            U(rdit).Ok++;
                            U(rsit).RecordCopy = true;
                        }
            }
            // work flags tested for 'N' whose initial value is not Y or N: safe when every read follows an assignment
            var unset = uses.Where(kv => ap.Promoted.ContainsKey(kv.Key) && kv.Value.N && !kv.Value.InJob
                && !(kv.Key.Values.Count == 1 && kv.Key.Values[0].Low is { Quoted: true, Text: "Y" or "N" })).Select(kv => kv.Key).ToHashSet();
            var setFirst = unset.Count == 0 ? [] : AssignedBeforeRead(ap, unset);
            bool Shape(DataItem it, FlagUse u) => it.IsLeaf && it.Length == 1 && it.Category is not (Category.Numeric or Category.NumericEdited) && u.All == u.Ok;
            var work = uses.Where(kv => ap.Promoted.ContainsKey(kv.Key) && Shape(kv.Key, kv.Value) && !kv.Value.InJob && (kv.Value.Yn || !kv.Value.RecordCopy)
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
                if (u.Yn) recordYn.Add(key);
            }
        }
        foreach (var ((rc, name), ok) in recordOk) if (ok && recordYn.Contains((rc, name))) rc.Flags.Add(name);
    }

    /// <summary>A one-character alphanumeric elementary item (a flag's shape).</summary>
    static bool OneChar(DataItem it) => it.Level != 88 && it.IsLeaf && it.Length == 1 && it.Category is not (Category.Numeric or Category.NumericEdited);

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
                    case "SET" when s.Str("mode") == "TRUE":
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

    /// <summary>
    /// The Java type of an elementary item. A packed or binary number past 18 digits is a BigDecimal of scale 0 (long
    /// cannot hold it, and the arithmetic is BigDecimal's); a zoned one keeps its BigInteger view.
    /// </summary>
    public static JType FieldType(DataItem it)
    {
        if (it.Category is Category.Numeric)
        {
            if (it.Scale > 0) return JType.BigDecimal;
            if (it.Digits > 18) return it.Usage == Usage.Display ? JType.Of("java.math.BigInteger") : JType.BigDecimal;
            return it.Digits > 9 ? JType.Long : JType.Int;
        }
        return JType.String;
    }

    /// <summary>The record class that holds an item in a program, and the instance (field) name.</summary>
    public (RecordClass Rc, string Instance)? RecordOf(AppProgram ap, DataItem it) =>
        ap.Records.TryGetValue(it.Record, out var rc) ? (rc, ap.Instances[it.Record]) : null;
}
