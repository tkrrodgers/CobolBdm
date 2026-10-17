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
    public int Length => Item.Length;
}

/// <summary>A plain Java field for a program's own elementary item (a work field never seen as bytes).</summary>
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
        var name = RecordName(rec.Name);
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
