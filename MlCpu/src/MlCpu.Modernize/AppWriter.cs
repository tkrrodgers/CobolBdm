using System.Globalization;
using System.Text;
using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

/// <summary>A method of a program class: its head ("private void runControl()"), its lifted body, its documentation.</summary>
public sealed record AppMethod(string Head, Method M, string Doc);

/// <summary>
/// Prints the application layer: a record class per record (a typed view of the fixed-width image), a class per program
/// (a batch step with main(), or a job service with call()), the platform runtime, and the legacy sites of the
/// statements (the reject mapping's source map).
/// </summary>
public sealed class AppWriter
{
    readonly AppModel A;
    readonly JavaWriter W;
    readonly Dictionary<AppProgram, List<AppMethod>> methods;
    /// <summary>Job objects (SJOB statics) each job program's functions use.</summary>
    readonly Dictionary<AppProgram, List<FieldDef>> jobObjects;
    public readonly List<(string File, int Line, string Site)> Sites = [];
    public readonly List<string> Residue = [];

    public AppWriter(AppModel a, JavaWriter w, Dictionary<AppProgram, List<AppMethod>> methods, Dictionary<AppProgram, List<FieldDef>> jobObjects)
    {
        A = a; W = w; this.methods = methods; this.jobObjects = jobObjects;
    }

    /// <summary>The test sources (src/test/java): the lifted kernels a JDK substitute replaces, and their differential gates.</summary>
    public readonly Dictionary<string, string> TestFiles = new(StringComparer.Ordinal);

    public Dictionary<string, string> Files()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var gates = new List<string>();
        // the programs first: printing them registers the record views their record classes print
        foreach (var ap in A.Programs)
        {
            var path = (A.AppPackage + "." + ap.ClassName).Replace('.', '/') + ".java";
            // a kernel the JDK replaces: its JDK class; the lifted kernel is the gate's reference (test lane)
            if (ap.Kernel is { } k && Kernels.JavaText(A, ap) is { } jdk)
            {
                files[path] = jdk;
                var refPkg = A.AppPackage + ".reference";
                var refName = Kernels.ReferenceName(ap);
                var refPath = (refPkg + "." + refName).Replace('.', '/') + ".java";
                TestFiles[refPath] = ProgramText(ap, refPath, refPkg, refName);
                TestFiles[(refPkg + "." + k.ClassName + "Gate").Replace('.', '/') + ".java"] = Kernels.GateText(A, ap);
                gates.Add(k.ClassName + "Gate");
                continue;
            }
            files[path] = ProgramText(ap, path);
        }
        // the runtime-compatibility profile's lifecycle check runs with the gates (the build's check)
        if (gates.Count > 0)
        {
            TestFiles[(A.PlatformPackage + ".RuntimeProfileCheck").Replace('.', '/') + ".java"] = Platform.RuntimeProfileCheckText(A.PlatformPackage);
            gates.Add(A.PlatformPackage + ".RuntimeProfileCheck");
        }
        if (gates.Count > 0) TestFiles[(A.AppPackage + ".reference.KernelGates").Replace('.', '/') + ".java"] = Kernels.RunnerText(A, gates);
        // the record classes: the copybooks' and the programs' own (named with the program), all in the record package
        var own = A.Programs.SelectMany(p => p.Records.Values.Where(r => r.Nested));
        foreach (var rc in A.Shared.Values.Concat(own).Distinct().OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            var imps = new HashSet<string>(StringComparer.Ordinal);
            var body = RecordClassText(rc, 0, imps);
            files[(A.RecordPackage + "." + rc.Name).Replace('.', '/') + ".java"] = Header(A.RecordPackage, imps) + body;
        }
        foreach (var (name, text) in Platform.Files(A.PlatformPackage, A.DisplayedAbends, !A.M.UnsupportedSites.IsEmpty || !A.M.JobAdapterSites.IsEmpty))
            files[A.PlatformPackage.Replace('.', '/') + "/" + name] = text;
        // a step's report formatter: the legacy PICTUREs of its report lines (compat lane)
        foreach (var ap in A.Programs)
            if (ap.Flow is { ReportClass: { } rc, Edits.Count: > 0 } fl)
                files[A.PlatformPackage.Replace('.', '/') + "/" + rc + ".java"] = Platform.ReportFormatter(A.PlatformPackage, rc, ap.P.Id, fl.Edits);
        // the strangler: the steps behind a port, the legacy job and the modern code, SHADOW (design §7)
        if (A.Programs.Any(p => p.IsStep))
            foreach (var (name, text) in Strangler.Files(A))
                files[(A.AppPackage + ".strangler").Replace('.', '/') + "/" + name] = text;
        return files;
    }

    static string Header(string pkg, HashSet<string> imps)
    {
        var sb = new StringBuilder();
        sb.Append("package ").Append(pkg).Append(";\n\n");
        var list = imps.Where(i => i.Contains('.') && JavaPkg(i) != pkg && !(i.StartsWith("java.lang.") && i.Count(c => c == '.') == 2)).Distinct().Order(StringComparer.Ordinal).ToList();
        foreach (var i in list) sb.Append("import ").Append(i).Append(";\n");
        if (list.Count > 0) sb.Append('\n');
        return sb.ToString();
    }

    static string JavaPkg(string fqcn) => fqcn.LastIndexOf('.') is var d && d > 0 ? fqcn[..d] : "";

    /// <summary>The subprograms a step's run CALLs, directly or through others: each after the ones it calls.</summary>
    List<AppProgram> RunSubprograms(AppProgram root)
    {
        var order = new List<AppProgram>();
        var seen = new HashSet<AppProgram>();
        void Visit(AppProgram p)
        {
            // a kernel the JDK replaces calls nothing (its lifted library's callees are its test reference's)
            if (p.Kernel is not null && p != root) return;
            foreach (var c in p.Callees.Keys)
                if (A.ById.TryGetValue(c, out var x) && x.IsSubprogram && seen.Add(x))
                {
                    Visit(x);
                    order.Add(x);
                }
        }
        Visit(root);
        return order;
    }

    // ================================================================== program classes

    /// <summary>
    /// How the job program reports an abend at a legacy site (J3S JX-ABEND displays JX-PROG and JX-LOAN): the program name
    /// it moves to JX-PROG and the record field it moves to JX-LOAN when a loan starts. Only when a lifted site can abend.
    /// </summary>
    (string Program, string Loan)? JobAbendReport(AppProgram ap)
    {
        if (A.M.DecimalLimitSites.IsEmpty && A.M.UnsupportedSites.IsEmpty && A.M.JobAdapterSites.IsEmpty) return null;
        string? program = null;
        DataItem? loan = null;
        foreach (var b in Model.Deep(ap.P.Model.Paragraphs.SelectMany(p => p.Statements).ToList()))
        {
            if (b.Verb != "MOVE") continue;
            var dst = Model.DstName(b);
            if (dst?.Equals("JX-PROG", StringComparison.OrdinalIgnoreCase) == true && b.Get("src") is { K: "str" } ps) program ??= ps.V;
            if (dst?.Equals("JX-LOAN", StringComparison.OrdinalIgnoreCase) == true && b.Get("src") is { K: "ref" } ls && ap.P.ItemsById.GetValueOrDefault(ls.Id ?? -1) is { } it) loan ??= it;
        }
        if (program is null || loan is null || A.RecordOf(ap, loan) is null) return null;
        return (program.Trim(), W.Expr(new ERecordField(loan) { Type = AppModel.FieldType(loan) }));
    }

    string ProgramText(AppProgram ap, string path, string? pkg = null, string? cls = null)
    {
        W.Prog = ap;
        try { return ProgramTextCore(ap, path, pkg ?? A.AppPackage, cls ?? ap.ClassName); }
        finally { W.Prog = null; }
    }

    string ProgramTextCore(AppProgram ap, string path, string pkg, string cls)
    {
        var imps = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        string T(JType t) => W.TypeNameIn(t, imps);
        string Rec(RecordClass rc) { imps.Add(A.RecordPackage + "." + rc.Name); return rc.Name; }
        if (!ap.IsSubprogram) imps.Add(A.PlatformPackage + ".Datasets");
        sb.Append("/**\n * ").Append(ap.P.Id).Append(": ").Append(Esc(ap.Description.Length > 0 ? ap.Description : "a program of the application layer")).Append('\n');
        sb.Append(" * <p>Legacy: ").Append(ap.P.Id).Append(ap.IsJob ? " (job program)" : ap.IsSubprogram ? $" (subprogram, CALL '{ap.P.Id}' USING {string.Join(" ", ap.LinkageOrder.Select(r => r.Name))})" : " (batch step)").Append(". Generated by MlCpu.Modernize: do not edit, regenerate.\n");
        if (ap.NameEvidence is { } ne && cls == ap.ClassName)
            sb.Append(" * <p>Named from the program header's ").Append(ne.Source).Append(" (").Append(Path.GetFileName(ap.P.Path)).Append(" line ").Append(ne.Line).Append("): ").Append(Esc(ne.Text)).Append('\n');
        sb.Append(" */\n");
        sb.Append("public final class ").Append(cls).Append(" {\n");
        // files
        foreach (var f in ap.P.Model.Files)
        {
            var field = ap.FileFields[f.Name];
            if (f.Organization == "INDEXED")
            {
                var key = f.RecordKey is null ? null : ap.P.ItemsByName.GetValueOrDefault(f.RecordKey);
                int keyOff = key is null ? 0 : key.Offset - key.Record.Offset, keyLen = key?.Length ?? 0;
                sb.Append($"    private final Datasets.Indexed {field} = new Datasets.Indexed(\"{f.Assign}\", {f.MaxRecord}, {keyOff}, {keyLen});\n");
            }
            else sb.Append($"    private final Datasets.Sequential {field} = new Datasets.Sequential(\"{f.Assign}\", {f.MaxRecord});\n");
        }
        // records: the call's parameters are set by call(), the program's own records live with it
        var flow = ap.Flow;
        foreach (var (item, rc) in ap.Records.OrderBy(kv => kv.Key.Line).DistinctBy(kv => ap.Instances[kv.Key]))
        {
            var inst = ap.Instances[item];
            if (flow is not null && flow.DroppedInstances.Contains(inst)) continue;
            // a subprogram's LINKAGE records are its methods' parameters
            if (ap.LinkageVars.ContainsKey(item)) continue;
            if (ap.Using.Contains(item)) sb.Append($"    private {Rec(rc)} {inst};\n");
            else sb.Append($"    private final {Rec(rc)} {inst} = new {Rec(rc)}();\n");
        }
        // the report port (the step's SYSOUT)
        if (flow is { ReportField: { } rf, ReportClass: { } rcls })
        {
            imps.Add(A.PlatformPackage + "." + rcls);
            sb.Append($"    private final {rcls} {rf} = new {rcls}();\n");
        }
        // the objects that own state (the record sequence)
        foreach (var n in flow?.Nested ?? []) sb.Append($"    private final {n.Name} {n.Field} = new {n.Name}();\n");
        // work fields
        foreach (var pf in ap.Promoted.Values.OrderBy(p => p.Item.Line))
            if (flow is null || flow.Fields.Contains(pf)) sb.Append($"    private {T(pf.Type)} {pf.Name}{Initial(pf)};\n");
        // job objects
        if (jobObjects.TryGetValue(ap, out var objs))
            foreach (var f in objs) sb.Append($"    private {T(W.JobObjectType(f))} {JavaWriter.JobObjectName(f)};\n");
        // called programs: a job program per caller; a subprogram once for the run (COBOL keeps one copy of its storage)
        foreach (var (prog, field) in ap.Callees)
            if (A.ById.TryGetValue(prog, out var callee))
            {
                // a test reference (another package) imports the classes it calls
                if (pkg != A.AppPackage) imps.Add(A.AppPackage + "." + callee.ClassName);
                sb.Append(callee.IsSubprogram ? $"    private final {callee.ClassName} {field};\n" : $"    private final {callee.ClassName} {field} = new {callee.ClassName}();\n");
            }
        sb.Append('\n');
        var subCallees = ap.Callees.Where(c => A.ById.TryGetValue(c.Key, out var x) && x.IsSubprogram).ToList();
        if (ap.IsSubprogram)
        {
            // the programs it calls are given (the run's instances)
            var ps = subCallees.Select(c => $"{A.ById[c.Key].ClassName} {c.Value}");
            sb.Append($"    /** The subprogram over the run's instances of the programs it calls{(subCallees.Count == 0 ? "" : " (" + string.Join(", ", subCallees.Select(c => c.Key)) + ")")}. */\n");
            sb.Append($"    public {cls}({string.Join(", ", ps)}) {{\n");
            foreach (var (_, field) in subCallees) sb.Append($"        this.{field} = {field};\n");
            sb.Append("    }\n\n");
        }
        else if (subCallees.Count > 0)
        {
            // the run's subprograms, each created once, the called before their callers
            sb.Append($"    /** The step with one instance of every subprogram of its run ({string.Join(", ", RunSubprograms(ap).Select(x => x.P.Id))}). */\n");
            sb.Append($"    public {cls}() {{\n");
            var local = new Dictionary<AppProgram, string>();
            foreach (var sp in RunSubprograms(ap))
            {
                var name = sp.Kernel is not null ? Naming.LowerFirst(sp.ClassName) : Naming.Camel(sp.P.Id);
                local[sp] = name;
                var args = sp.Kernel is not null ? [] : sp.Callees.Where(c => A.ById.TryGetValue(c.Key, out var x) && x.IsSubprogram).Select(c => local[A.ById[c.Key]]);
                sb.Append($"        {sp.ClassName} {name} = new {sp.ClassName}({string.Join(", ", args)});\n");
            }
            foreach (var (prog, field) in subCallees) sb.Append($"        this.{field} = {local[A.ById[prog]]};\n");
            sb.Append("    }\n\n");
        }
        var entry = ap.Classic.FirstOrDefault();
        var entryMethod = entry is null ? null : ap.MethodNames[entry.Name];
        if (ap.IsJob)
        {
            // the one-time initialization (the job's init functions) runs on the first call: it reads the call's records; an init
            // function left without statements (its only store had no effect) is not called
            var inits = ap.InitFunctions.Where(f => !EmptyInit(ap, f)).ToList();
            if (inits.Count > 0) sb.Append("    private boolean initialized;\n\n");
            var ps = ap.Using.Select(u => ap.Records.TryGetValue(u, out var rc) ? (Rec(rc) + " " + ap.Instances[u]) : null).Where(x => x is not null).ToList();
            sb.Append($"    /** One CALL of the job program (legacy {ap.P.Id} PROCEDURE DIVISION USING): the records are the caller's, passed by reference. */\n");
            sb.Append($"    public void call({string.Join(", ", ps)}) {{\n");
            foreach (var u in ap.Using) if (ap.Instances.TryGetValue(u, out var inst)) sb.Append($"        this.{inst} = {inst};\n");
            if (inits.Count > 0)
            {
                sb.Append("        if (!initialized) {\n            initialized = true;\n");
                foreach (var f in inits) sb.Append($"            {ap.MethodNames[f.Paragraph]}();\n");
                sb.Append("        }\n");
            }
            if (entryMethod is not null)
            {
                // an abend at a legacy site, reported as the job program's abend routine reports it (JX-ABEND: JX-PROG, JX-LOAN)
                if (JobAbendReport(ap) is { } report)
                {
                    imps.Add(A.PlatformPackage + ".Abend");
                    sb.Append("        try {\n");
                    sb.Append($"            {entryMethod}();\n");
                    sb.Append("        } catch (Abend e) {\n");
                    sb.Append($"            throw e.reportedBy(\"{report.Program}\", {report.Loan});\n");
                    sb.Append("        }\n");
                }
                else sb.Append($"        {entryMethod}();\n");
            }
            sb.Append("    }\n\n");
        }
        else if (ap.IsStep)
        {
            imps.Add(A.PlatformPackage + ".Abend");
            sb.Append($"    /** Runs the batch step (legacy program {ap.P.Id}) with its datasets by DD name; exits with its return code. */\n");
            sb.Append("    public static void main(String[] args) {\n");
            sb.Append($"        int rc = new {cls}().run();\n");
            sb.Append("        if (rc != 0) System.exit(rc);\n");
            sb.Append("    }\n\n");
            sb.Append($"    /** Runs the step (legacy program {ap.P.Id}); its return code (16 after an abend, as CEE3ABD ends the run). */\n");
            sb.Append("    public int run() {\n");
            sb.Append("        try {\n");
            sb.Append($"            {ap.Flow?.EntryName ?? entryMethod}();\n");
            sb.Append("            return 0;\n");
            sb.Append("        } catch (Abend e) {\n");
            sb.Append(flow?.ReportField is { } rport
                ? $"            {rport}.abend(e);\n"
                : "            System.out.println(\"ABEND \" + e.code() + \" \" + e.getMessage());\n");
            sb.Append("            return 16;\n");
            sb.Append("        }\n");
            sb.Append("    }\n\n");
        }
        foreach (var member in flow?.Members ?? []) sb.Append(member).Append('\n');
        W.ReservedNames = ap.FieldNames().Concat(jobObjects.GetValueOrDefault(ap)?.Select(JavaWriter.JobObjectName) ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var n in flow?.Nested ?? [])
        {
            sb.Append($"    /** {Esc(n.Doc)} */\n");
            sb.Append($"    private final class {n.Name} {{\n");
            foreach (var fieldLine in n.Fields) sb.Append("        ").Append(fieldLine).Append('\n');
            foreach (var am in n.Methods)
            {
                sb.Append('\n');
                var doc = am.Doc.Contains("legacy", StringComparison.OrdinalIgnoreCase) ? am.Doc : am.Doc + " (legacy " + ap.P.Id + ")";
                sb.Append("        /** ").Append(Esc(doc)).Append(" */\n");
                sb.Append(W.MethodText(am.M, am.Head, 2, imps, path, []));
            }
            sb.Append("    }\n\n");
        }
        if (ap.IsSubprogram && ap.EndsCallInParagraph)
        {
            sb.Append("    /** The end of the CALL from a performed paragraph (its GOBACK): call() catches it and returns. */\n");
            sb.Append($"    private static final class {ClassicLift.CallEndedClass} extends RuntimeException {{\n");
            sb.Append("        private static final long serialVersionUID = 1L;\n\n");
            sb.Append($"        {ClassicLift.CallEndedClass}() {{\n");
            sb.Append("            super(null, null, false, false);\n");
            sb.Append("        }\n");
            sb.Append("    }\n\n");
        }
        var head = new StringBuilder();
        var bodyText = sb.ToString();
        // methods (their text collects imports; lines are fixed once the header is known)
        var methodTexts = new List<(string Text, List<(int Line, string Site)> Sites)>();
        foreach (var am in methods.GetValueOrDefault(ap) ?? [])
        {
            if (ap.IsJob && ap.InitFunctions.Contains(am.M.Fn) && EmptyInit(ap, am.M.Fn)) continue;
            var sites = new List<(int, string)>();
            var doc = am.Doc.Contains("legacy", StringComparison.OrdinalIgnoreCase) ? am.Doc : am.Doc + " (legacy " + ap.P.Id + ")";
            var text = "    /** " + Esc(doc) + " */\n" + W.MethodText(am.M, am.Head, 1, imps, path, sites);
            methodTexts.Add((text, sites));
        }
        W.ReservedNames = null;
        // a model edit's imports, fields and methods for a program class (the class: its file's)
        var fqcn = path.EndsWith(".java") ? path[..^5].Replace('/', '.') : path;
        foreach (var e in W.Edits?.For("add-import", fqcn) ?? [])
        {
            imps.Add(System.Text.RegularExpressions.Regex.Replace(e.Text.Trim(), @"^import\s+|;$", ""));
            W.Edits!.Applied(e);
        }
        foreach (var e in (W.Edits?.For("add-field", fqcn) ?? []).Concat(W.Edits?.For("add-method", fqcn) ?? []))
        {
            methodTexts.Add((JavaWriter.EditDoc(e, 1) + string.Concat(ModelEdits.Lines(e.Text).Select(l => "    " + l + "\n")), []));
            W.Edits!.Applied(e);
        }
        if (flow is not null) imps.UnionWith(flow.Imports);
        // a work field no code of the class uses (a loop index the lift made a local): not declared
        foreach (var pf in ap.Promoted.Values)
        {
            var decl = $"    private {T(pf.Type)} {pf.Name}{Initial(pf)};\n";
            if (!bodyText.Contains(decl, StringComparison.Ordinal)) continue;
            var others = bodyText.Replace(decl, "", StringComparison.Ordinal) + string.Concat(methodTexts.Select(mt => mt.Text));
            if (!System.Text.RegularExpressions.Regex.IsMatch(others, $@"\b{System.Text.RegularExpressions.Regex.Escape(pf.Name)}\b"))
                bodyText = bodyText.Replace(decl, "", StringComparison.Ordinal);
        }
        // the class's own nested types (an exception of the counts) need no import
        imps.RemoveWhere(i => i.StartsWith(pkg + "." + cls + ".", StringComparison.Ordinal));
        var header = Header(pkg, imps);
        int line = header.Count(c => c == '\n') + bodyText.Count(c => c == '\n') + 1;
        var all = new StringBuilder(header).Append(bodyText);
        foreach (var (text, sites) in methodTexts)
        {
            // a site's line counts from the method text's first line (the doc comment is one line above the head)
            foreach (var (l, site) in sites) Sites.Add((path, line + l, site));
            all.Append(text).Append('\n');
            line += text.Count(c => c == '\n') + 1;
        }
        if (all[^1] == '\n' && all.Length > 1 && all[^2] == '\n') all.Length--;
        all.Append("}\n");
        return all.ToString();
    }

    /// <summary>An init function of a job program whose method has no statements (or only an empty try).</summary>
    bool EmptyInit(AppProgram ap, FunctionDef f) =>
        methods.GetValueOrDefault(ap)?.FirstOrDefault(am => am.M.Fn == f) is { } m && m.M.Body is [] or [STry { Body: [] }];

    static string Initial(PromotedField pf)
    {
        var it = pf.Item;
        var v = it.Values.Count == 1 ? it.Values[0].Low : null;
        // a Y/N flag: 'Y' is true
        if (pf.Type.Name == "boolean") return v is { Quoted: true, Text: "Y" } ? " = true" : " = false";
        if (pf.Type.Name == "java.lang.String")
        {
            if (v is null) return " = \"\"";
            var text = v.Quoted ? v.Text : v.Text.ToUpperInvariant() switch { "SPACE" or "SPACES" => "", "ZERO" or "ZEROS" or "ZEROES" => new string('0', it.Length), _ => v.Text };
            if (text.Length > it.Length) text = text[..it.Length];
            return " = \"" + text.TrimEnd().Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
        if (v is null || !v.Quoted && !decimal.TryParse(v.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return pf.Type.Name == "java.math.BigDecimal" ? " = BigDecimal.ZERO" : "";
        return pf.Type.Name == "java.math.BigDecimal" ? $" = new BigDecimal(\"{v.Text}\")" : pf.Type.Name == "long" ? $" = {v.Text}L" : $" = {v.Text}";
    }

    static string Esc(string s) => s.Replace("*/", "* /");

    // ================================================================== record classes

    string RecordClassText(RecordClass rc, int indent, HashSet<string> imps)
    {
        imps.Add(A.PlatformPackage + ".Records");
        var pad = new string(' ', indent * 4);
        var sb = new StringBuilder();
        void L(string s) => sb.Append(s.Length == 0 ? "" : pad).Append(s).Append('\n');
        var rec = rc.Item;
        var owner = rc.Copybook is not null ? $"copybook {rc.Copybook}" : $"program {A.Programs.FirstOrDefault(p => p.Records.ContainsValue(rc))?.P.Id ?? "?"}";
        L("/**");
        L($" * {rec.Name} (legacy {owner}): a typed view of the {rec.Length}-byte record image.");
        L(" * <p>Generated by MlCpu.Modernize from the record layout: do not edit, regenerate.");
        L(" */");
        L($"public {(indent > 0 ? "static " : "")}final class {rc.Name} implements Records.Image {{");
        L($"    /** The record length (legacy {rec.Name}). */");
        L($"    public static final int LENGTH = {rec.Length};");
        L("    private final byte[] data = Records.blank(LENGTH);");
        // WORKING-STORAGE: numbers start at zero and the VALUE clauses hold (GnuCOBOL initializes storage by type)
        var init = InitialImage(rc, A.PlatformPackage, imps, Residue);
        if (init.Count > 0)
        {
            L("");
            L($"    /** A new record: its numbers zero, its text spaces, then its VALUE clauses (legacy {rec.Name}). */");
            L($"    public {rc.Name}() {{");
            foreach (var line in init) L(line);
            L("    }");
        }
        L("");
        L("    /** The record image (a copy). */");
        L("    @Override");
        L("    public byte[] bytes() {");
        L("        return data.clone();");
        L("    }");
        L("");
        L("    /** Replaces the image (a shorter one is padded with spaces). */");
        L("    @Override");
        L("    public void bytes(byte[] image) {");
        L("        java.util.Arrays.fill(data, (byte) ' ');");
        L("        System.arraycopy(image, 0, data, 0, Math.min(LENGTH, image.length));");
        L("    }");
        L("");
        L($"    /** The whole record's characters (legacy {rec.Name}). */");
        L("    public String text() {");
        L("        return Records.text(data, 0, LENGTH);");
        L("    }");
        L("");
        L($"    /** Replaces the whole record's characters (MOVE to {rec.Name}). */");
        L("    public void text(String image) {");
        L("        Records.putText(data, 0, LENGTH, image);");
        L("    }");
        L("");
        L("    /** Characters of the image (a group, a reference modification), by 0-based offset. */");
        L("    public String raw(int offset, int length) {");
        L("        return Records.text(data, offset, length);");
        L("    }");
        L("");
        L("    /** Replaces characters of the image (a group, a reference modification), by 0-based offset. */");
        L("    public void raw(int offset, int length, String value) {");
        L("        Records.putText(data, offset, length, value);");
        L("    }");
        foreach (var it in rc.Fields.OrderBy(f => f.Offset).ThenBy(f => f.Line))
        {
            var name = rc.Accessors[it.Name];
            L("");
            foreach (var line in Accessors(rc, it, name, imps)) L("    " + line);
        }
        // INITIALIZE of the record or of a group: its numbers zero, its text spaces, each occurrence
        foreach (var (itemName, method) in rc.Initializers.OrderBy(i => i.Value, StringComparer.Ordinal))
        {
            var group = Descendants(rec).Prepend(rec).FirstOrDefault(d => d.Name.Equals(itemName, StringComparison.OrdinalIgnoreCase));
            if (group is null) { Residue.Add($"INITIALIZE {itemName}: not in {rc.Name}"); continue; }
            L("");
            L($"    /** INITIALIZE {group.Name}{(group.Dims.Count > 0 ? " (an occurrence)" : "")}: its numbers zero, its text spaces (FILLER and REDEFINES alternatives as they are). */");
            var indexes = group.Dims.Select((_, k) => "index" + (k == 0 ? "" : (k + 1).ToString(CultureInfo.InvariantCulture))).ToList();
            L($"    public void {method}({string.Join(", ", indexes.Select(i => "int " + i))}) {{");
            foreach (var line in InitializeLines(rc, group, 2, indexes, imps)) L(line);
            L("    }");
        }
        // the named views the programs read and write (a number's characters, a sign, digits, adjacent fields)
        foreach (var (name, (off, len, doc)) in rc.Views.OrderBy(v => v.Value.Offset).ThenBy(v => v.Key, StringComparer.Ordinal))
        {
            L("");
            L($"    /** {doc} (offset {off}, {len} bytes). */");
            L($"    public String {name}() {{ return Records.text(data, {off}, {len}); }}");
            L($"    /** {doc}: set (space padded). */");
            L($"    public void {name}(String value) {{ Records.putText(data, {off}, {len}, value); }}");
        }
        foreach (var line in CodeSets.Java(rc)) L(line);
        // a model edit's imports, fields and methods for a (top-level) record class
        if (indent == 0 && W.Edits is { } edits)
        {
            var fqcn = A.RecordPackage + "." + rc.Name;
            foreach (var e in edits.For("add-import", fqcn))
            {
                imps.Add(System.Text.RegularExpressions.Regex.Replace(e.Text.Trim(), @"^import\s+|;$", ""));
                edits.Applied(e);
            }
            foreach (var e in edits.For("add-field", fqcn).Concat(edits.For("add-method", fqcn)))
            {
                L("");
                L(JavaWriter.EditDoc(e, 1).TrimEnd('\n'));
                foreach (var line in ModelEdits.Lines(e.Text)) L("    " + line);
                edits.Applied(e);
            }
        }
        L("}");
        return sb.ToString();
    }

    /// <summary>The statements of a record class's constructor (its initial image); empty when a new record is all spaces.</summary>
    public static List<string> InitialImage(RecordClass rc, string pkg, HashSet<string> imps, List<string> residue)
    {
        // a record that REDEFINES another starts with the image of the storage it redefines (its VALUE clauses)
        var storage = rc.Item.Level == 1 && rc.Item.RedefinesItem is { Level: 1, Length: var bl } b && bl == rc.Item.Length ? b : rc.Item;
        return storage.Section is DataSection.WorkingStorage or DataSection.LocalStorage ? InitialLines(storage, storage, 2, [], pkg, imps, residue) : [];
    }

    /// <summary>
    /// The statements that give a WORKING-STORAGE record its initial image as GnuCOBOL allocates it: a number without
    /// VALUE is zero in its usage's encoding, text is spaces, a FILLER without VALUE low-values, a VALUE clause its value,
    /// each occurrence of a table; a REDEFINES alternative keeps the bytes of what it redefines. Empty when the image is
    /// all spaces.
    /// </summary>
    static List<string> InitialLines(DataItem rec, DataItem item, int depth, List<(string Var, int Stride)> loops, string pkg, HashSet<string> imps, List<string> residue)
    {
        var res = new List<string>();
        var pad = new string(' ', depth * 4);
        foreach (var c in item.Children)
        {
            if (c.Level == 88 || c.RedefinesItem is not null) continue;
            var inner = loops;
            var body = new List<string>();
            int d = depth;
            string? loopVar = null;
            if (c.Occurs > 0)
            {
                loopVar = "i" + (loops.Count + 1);
                inner = [.. loops, (loopVar, c.Length)];
                d++;
            }
            var ipad = new string(' ', d * 4);
            string at = (c.Offset - rec.Offset).ToString(CultureInfo.InvariantCulture) + string.Concat(inner.Select(l => $" + {l.Var} * {l.Stride}"));
            var v = c.Values.Count == 1 ? c.Values[0].Low : null;
            if (!c.IsLeaf && v is null) body.AddRange(InitialLines(rec, c, d, inner, pkg, imps, residue));
            else if (InitialValue(c, v, at, pkg, imps, residue) is { } stmt) body.Add(ipad + stmt);
            if (body.Count == 0) continue;
            if (loopVar is not null)
            {
                res.Add($"{pad}for (int {loopVar} = 0; {loopVar} < {c.Occurs}; {loopVar}++) {{");
                res.AddRange(body);
                res.Add($"{pad}}}");
            }
            else res.AddRange(body);
        }
        return res;
    }

    /// <summary>One item's initial bytes (null when spaces): its VALUE, or zero for a number.</summary>
    static string? InitialValue(DataItem it, RawWord? v, string at, string pkg, HashSet<string> imps, List<string> residue)
    {
        int len = it.Length;
        var fig = v is { Quoted: false } ? v.Text.ToUpperInvariant() : null;
        bool numeric = it.IsLeaf && it.Category == Category.Numeric;
        // GnuCOBOL's storage: a FILLER without VALUE keeps the low-values it was allocated with (INITIALIZE skips it)
        if (v is null && it.IsFiller) return $"java.util.Arrays.fill(data, {at}, {at} + {len}, (byte) 0);";
        if (!numeric)
        {
            if (v is null || fig is "SPACE" or "SPACES") return null;
            string text = fig switch
            {
                "ZERO" or "ZEROS" or "ZEROES" => new string('0', len),
                "QUOTE" or "QUOTES" => new string('"', len),
                "HIGH-VALUE" or "HIGH-VALUES" => new string('\u00FF', len),
                "LOW-VALUE" or "LOW-VALUES" => new string('\0', len),
                _ => v.Quoted ? (it.ValueAll ? string.Concat(Enumerable.Repeat(v.Text, len / Math.Max(1, v.Text.Length) + 1))[..len] : v.Text) : v.Text,
            };
            if (text.Trim().Length == 0 && !text.Contains('\0') && !text.Contains('\u00FF')) return null;
            var lit = "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\0", "\\0").Replace("\u00FF", "\\u00ff") + "\"";
            return $"Records.putText(data, {at}, {len}, {lit});";
        }
        decimal value = 0;
        if (v is not null && fig is not ("ZERO" or "ZEROS" or "ZEROES") && !decimal.TryParse(v.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            residue.Add($"record {it.Record.Name}: VALUE {v.Text} of {it.Name}");
            return null;
        }
        var big = value == 0 ? "BigDecimal.ZERO" : $"new BigDecimal(\"{value.ToString(CultureInfo.InvariantCulture)}\")";
        if (it.Usage is Usage.Binary or Usage.Comp5)
            return $"Records.putBinary(data, {at}, {len}, {decimal.Truncate(value * (decimal)Math.Pow(10, it.Scale)).ToString(CultureInfo.InvariantCulture)}L);";
        if (it.Usage == Usage.Comp3)
        {
            imps.Add(pkg + ".PackedDecimal");
            imps.Add("java.math.BigDecimal");
            return $"PackedDecimal.put(data, {at}, {len}, {it.Digits}, {it.Scale}, {(it.Signed ? "true" : "false")}, {big});";
        }
        if (it.Usage != Usage.Display) { residue.Add($"record {it.Record.Name}: initial {it.Name} usage {it.Usage}"); return null; }
        if (it.Signed && it.SignSeparate)
        {
            if (!it.SignLeading) { residue.Add($"record {it.Record.Name}: initial {it.Name} SIGN TRAILING SEPARATE"); return null; }
            imps.Add("java.math.BigDecimal");
            return $"Records.putSignLeading(data, {at}, {len}, {it.Scale}, {big});";
        }
        if (value < 0) { residue.Add($"record {it.Record.Name}: initial {it.Name} negative zoned"); return null; }
        return $"Records.putUnsigned(data, {at}, {len}, {decimal.Truncate(value * (decimal)Math.Pow(10, it.Scale)).ToString(CultureInfo.InvariantCulture)}L);";
    }

    /// <summary>A PICTURE with its repetitions written out: Z(3)9 → ZZZ9.</summary>
    static string ExpandPicture(string pic) =>
        System.Text.RegularExpressions.Regex.Replace(pic, @"(.)\((\d+)\)", m => new string(m.Groups[1].Value[0], int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));

    static IEnumerable<DataItem> Descendants(DataItem d)
    {
        foreach (var c in d.Children)
        {
            yield return c;
            foreach (var x in Descendants(c)) yield return x;
        }
    }

    /// <summary>The statements of an initialize method: a setter a field, a loop a table (its subscripts i1, i2, ...).</summary>
    IEnumerable<string> InitializeLines(RecordClass rc, DataItem item, int depth, List<string> subs, HashSet<string> imps)
    {
        var pad = new string(' ', depth * 4);
        foreach (var c in item.Children)
        {
            if (c.Level == 88 || c.RedefinesItem is not null) continue;
            var inner = subs;
            var lines = new List<string>();
            int d = depth;
            if (c.Occurs > 0)
            {
                var i = "i" + (subs.Count + 1);
                lines.Add($"{pad}for (int {i} = 1; {i} <= {c.Occurs}; {i}++) {{");
                inner = [.. subs, i];
                d++;
            }
            var ipad = new string(' ', d * 4);
            if (c.IsLeaf)
            {
                if (c.IsFiller || !rc.Accessors.TryGetValue(c.Name, out var acc)) continue;
                var zero = c.Category != Category.Numeric ? "\"\"" : AppModel.FieldType(c).Name switch
                {
                    "java.math.BigDecimal" => "BigDecimal.ZERO",
                    "long" => "0L",
                    _ => "0",
                };
                if (zero.StartsWith("BigDecimal", StringComparison.Ordinal)) imps.Add("java.math.BigDecimal");
                lines.Add($"{ipad}{acc}({string.Concat(inner.Select(s => s + ", "))}{zero});");
            }
            else lines.AddRange(InitializeLines(rc, c, d, inner, imps));
            if (c.Occurs > 0) lines.Add($"{pad}}}");
            if (lines.Count > (c.Occurs > 0 ? 2 : 0)) foreach (var l in lines) yield return l;
        }
    }

    IEnumerable<string> Accessors(RecordClass rc, DataItem it, string name, HashSet<string> imps)
    {
        int off = it.Offset - it.Record.Offset, len = it.Length;
        bool occurs = it.Dims.Count > 0;
        // a table in a table: one subscript a dimension (index, index2, ...)
        string idx = occurs ? string.Join(", ", it.Dims.Select((_, k) => "int index" + (k == 0 ? "" : (k + 1).ToString(CultureInfo.InvariantCulture)))) : "";
        string at = occurs ? off.ToString(CultureInfo.InvariantCulture) + string.Concat(it.Dims.Select((d, k) => $" + (index{(k == 0 ? "" : (k + 1).ToString(CultureInfo.InvariantCulture))} - 1) * {d.Stride}")) : off.ToString(CultureInfo.InvariantCulture);
        string sep = occurs ? ", " : "";
        var legacy = $"{it.Name} PIC {it.Pic ?? "X"}{(occurs ? string.Concat(it.Dims.Select(d => $" OCCURS {d.Count}")) : "")}";
        string Doc(string what) => $"/** {legacy}{what} (offset {off}, {len} bytes). */";
        var t = AppModel.FieldType(it);
        if (it.Category != Category.Numeric)
        {
            if (rc.Flags.Contains(it.Name))
            {
                // a Y/N indicator: 'Y' is true; its character stays readable (validation)
                yield return Doc(": 'Y' is true");
                yield return $"public boolean {name}({idx}) {{ return Records.text(data, {at}, 1).equals(\"Y\"); }}";
            }
            else
            {
                yield return Doc(", without trailing spaces");
                yield return $"public String {name}({idx}) {{ return Records.trimmed(data, {at}, {len}); }}";
            }
            yield return Doc(", its characters");
            yield return $"public String {name}Text({idx}) {{ return Records.text(data, {at}, {len}); }}";
            yield return Doc(": set (space padded)");
            yield return $"public void {name}({idx}{sep}String value) {{ Records.putText(data, {at}, {len}, value); }}";
            if (it.Category == Category.NumericEdited && rc.Edited.Contains(it.Name))
            {
                // a number moved to the edited field: the codec edits it with the field's PICTURE
                imps.Add("java.math.BigDecimal");
                imps.Add(A.PlatformPackage + ".Numbers");
                yield return Doc(": set from a number, edited");
                yield return $"public void {name}({idx}{sep}BigDecimal value) {{ Records.putText(data, {at}, {len}, Numbers.edit(value, \"{ExpandPicture(it.Pic ?? "")}\")); }}";
            }
            yield break;
        }
        string get, set;
        string javaType = t.Name switch { "java.math.BigDecimal" => "BigDecimal", "java.math.BigInteger" => "BigInteger", _ => t.Name };
        if (javaType == "BigDecimal") imps.Add("java.math.BigDecimal");
        if (javaType == "BigInteger") { imps.Add("java.math.BigInteger"); imps.Add("java.math.BigDecimal"); }
        if (it.Usage is Usage.Binary or Usage.Comp5)
        {
            get = javaType switch
            {
                "int" => $"(int) Records.binary(data, {at}, {len})",
                "BigDecimal" => $"BigDecimal.valueOf(Records.binary(data, {at}, {len}), {it.Scale})",
                _ => $"Records.binary(data, {at}, {len})",
            };
            set = javaType == "BigDecimal" ? $"Records.putBinary(data, {at}, {len}, value.movePointRight({it.Scale}).longValue())" : $"Records.putBinary(data, {at}, {len}, value)";
        }
        else if (it.Usage == Usage.Comp3)
        {
            // packed decimal: the codec keeps the field's digits and scale
            imps.Add(A.PlatformPackage + ".PackedDecimal");
            imps.Add("java.math.BigDecimal");
            get = javaType switch
            {
                "BigDecimal" => $"PackedDecimal.get(data, {at}, {len}, {it.Scale})",
                "long" => $"PackedDecimal.get(data, {at}, {len}, 0).longValue()",
                _ => $"PackedDecimal.get(data, {at}, {len}, 0).intValue()",
            };
            var signed = it.Signed ? "true" : "false";
            set = javaType == "BigDecimal" ? $"PackedDecimal.put(data, {at}, {len}, {it.Digits}, {it.Scale}, {signed}, value)"
                : $"PackedDecimal.put(data, {at}, {len}, {it.Digits}, 0, {signed}, BigDecimal.valueOf(value))";
        }
        else if (it.Usage == Usage.Display && it.Signed && it.SignSeparate)
        {
            get = javaType switch
            {
                "BigDecimal" => $"Records.signLeading(data, {at}, {len}, {it.Scale})",
                "BigInteger" => $"Records.signLeading(data, {at}, {len}, 0).toBigInteger()",
                "long" => $"Records.signLeading(data, {at}, {len}, 0).longValue()",
                _ => $"Records.signLeading(data, {at}, {len}, 0).intValue()",
            };
            set = javaType switch
            {
                "BigDecimal" => $"Records.putSignLeading(data, {at}, {len}, {it.Scale}, value)",
                "BigInteger" => $"Records.putSignLeading(data, {at}, {len}, 0, new BigDecimal(value))",
                _ => $"Records.putSignLeading(data, {at}, {len}, 0, BigDecimal.valueOf(value))",
            };
            imps.Add("java.math.BigDecimal");
        }
        else if (it.Usage == Usage.Display && it.Signed)
        {
            // the sign over the last digit (GnuCOBOL -fsign=EBCDIC): the zoned codec keeps the digits, scale and sign
            imps.Add(A.PlatformPackage + ".ZonedDecimal");
            imps.Add("java.math.BigDecimal");
            get = javaType switch
            {
                "BigDecimal" => $"ZonedDecimal.get(data, {at}, {len}, {it.Scale})",
                "BigInteger" => $"ZonedDecimal.get(data, {at}, {len}, 0).toBigInteger()",
                "long" => $"ZonedDecimal.get(data, {at}, {len}, 0).longValue()",
                _ => $"ZonedDecimal.get(data, {at}, {len}, 0).intValue()",
            };
            set = javaType switch
            {
                "BigDecimal" => $"ZonedDecimal.put(data, {at}, {len}, {it.Scale}, value)",
                "BigInteger" => $"ZonedDecimal.put(data, {at}, {len}, 0, new BigDecimal(value))",
                _ => $"ZonedDecimal.put(data, {at}, {len}, 0, BigDecimal.valueOf(value))",
            };
        }
        else if (it.Usage == Usage.Display)
        {
            get = javaType switch
            {
                "BigDecimal" => $"BigDecimal.valueOf(Records.unsigned(data, {at}, {len}), {it.Scale})",
                "long" => $"Records.unsigned(data, {at}, {len})",
                _ => $"(int) Records.unsigned(data, {at}, {len})",
            };
            set = javaType == "BigDecimal" ? $"Records.putUnsigned(data, {at}, {len}, value.movePointRight({it.Scale}).longValue())" : $"Records.putUnsigned(data, {at}, {len}, value)";
            // a yyyyMMdd field takes a date too
            if (it.Digits == 8 && it.Scale == 0 && !rc.DateViews.Contains(it.Name))
            {
                imps.Add("java.time.LocalDate");
                yield return Doc(": set from a date (yyyyMMdd)");
                yield return $"public void {name}({idx}{sep}LocalDate value) {{ Records.putDate(data, {at}, {len}, value); }}";
            }
        }
        else
        {
            Residue.Add($"record field {it.Name}: usage {it.Usage} not supported");
            get = "throw new UnsupportedOperationException(\"" + it.Name + "\")";
            set = get;
            yield return Doc(": not supported");
            yield return $"public {javaType} {name}({idx}) {{ {get}; }}";
            yield return Doc(": not supported");
            yield return $"public void {name}({idx}{sep}{javaType} value) {{ {set}; }}";
            yield break;
        }
        if (rc.DateViews.Contains(it.Name))
        {
            imps.Add("java.time.LocalDate");
            yield return Doc(", a date (yyyyMMdd)");
            yield return $"public LocalDate {name}({idx}) {{ return Records.date(data, {at}, {len}); }}";
            yield return Doc(": set from a date (yyyyMMdd)");
            yield return $"public void {name}({idx}{sep}LocalDate value) {{ Records.putDate(data, {at}, {len}, value); }}";
            yield return Doc(", its number");
            yield return $"public {javaType} {name}Number({idx}) {{ return {get}; }}";
        }
        else
        {
            yield return Doc("");
            yield return $"public {javaType} {name}({idx}) {{ return {get}; }}";
        }
        yield return Doc(": set");
        yield return $"public void {name}({idx}{sep}{javaType} value) {{ {set}; }}";
    }
}
