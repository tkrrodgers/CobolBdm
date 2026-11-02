using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MlCpu.Modernize;

/// <summary>The pipeline stage: COBOL estate → semantic model → modern Java (docs: JavaToCOBOLTest/SemanticModernization.md).</summary>
public static class Modernizer
{
    public sealed record Result(int Functions, int Lifted, int Residue, int Files, string OutDir);

    /// <summary>
    /// The requested rules are known and authorized: the estate owner's deployment profile (project.json
    /// deploymentProfile; a malformed one fails) must declare closedWorld: true for R3 final-classes. Returns the profile.
    /// </summary>
    public static DeploymentProfile? Authorize(string project, IReadOnlyCollection<string>? rules)
    {
        foreach (var r in rules ?? []) if (r != ApiVisibility.RuleName && r != FinalClasses.RuleName && r != ProgramInterfaces.RuleName) throw new ArgumentException($"unknown rule {r} (known: {ApiVisibility.RuleName}, {FinalClasses.RuleName}, {ProgramInterfaces.RuleName})");
        var profile = DeploymentProfile.Load(project);
        if (rules?.Contains(FinalClasses.RuleName) == true && profile is not { ClosedWorld: true })
            throw new ArgumentException($"rule {FinalClasses.RuleName} is not authorized for {project}: it needs the estate owner's deployment profile "
                + $"(project.json \"{DeploymentProfile.Key}\": {{ \"{DeploymentProfile.ClosedWorldKey}\": true }}), found "
                + (profile is null ? "no deployment profile" : $"{DeploymentProfile.ClosedWorldKey}: false") + "; the value is never inferred");
        return profile;
    }

    /// <param name="edits">Model edits applied while the model is printed (opt-in, <see cref="ModelEdits"/>).</param>
    /// <param name="rules">Opt-in Phase M rules (<see cref="ApiVisibility.RuleName"/>, <see cref="FinalClasses.RuleName"/>, <see cref="ProgramInterfaces.RuleName"/>); none: the generation is unchanged.</param>
    /// <param name="r1Poison">The R1 self check only: decide with poisoned names and comments (<see cref="ApiSurface.Run"/>).</param>
    /// <param name="r3Poison">The R3 self check only: decide with poisoned names, comments and permuted inputs (<see cref="FinalClassesSurface.Run"/>).</param>
    /// <param name="r5Poison">The R5 self check only: decide with poisoned names, comments, signatures and permuted inputs (<see cref="ProgramInterfacesSurface.Run"/>).</param>
    public static Result Run(string project, string outDir, Action<string>? log = null, ModelEdits? edits = null, IReadOnlyCollection<string>? rules = null, bool r1Poison = false, bool r3Poison = false, bool r5Poison = false)
    {
        log ??= Console.WriteLine;
        var profile = Authorize(project, rules);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var estate = Estate.Load(project);
        var m = Model.Build(estate);
        log($"estate {estate.Name}: {estate.Programs.Count} programs, {m.Functions.Count} functions, {m.Classes.Count} classes ({sw.ElapsedMilliseconds} ms)");
        Literals(m, log);
        CatchTypes(m);
        // the application layer (drivers, job programs): known before lifting, so job functions call its paragraphs
        var app = AppModel.Build(m);
        m.PlatformPackage = app.PlatformPackage;
        foreach (var ap in app.Programs)
            foreach (var para in ap.Classic) m.ClassicMethods[ap.P.Id + "/" + para.Name] = ap.MethodNames[para.Name];
        log($"application layer: {string.Join(", ", app.Programs.Select(p => p.P.Id + " " + p.ClassName))}");
        var residue = new ConcurrentBag<string>();
        var methods = new ConcurrentDictionary<FunctionDef, Method>();
        var todo = m.Functions.Values.Where(f => f.Owner is not null && f.Owner.Kind is ClassKind.Domain or ClassKind.Closure or ClassKind.Job
            && !(f.IsJob && f.MethodName.Contains("STRING-LITERALS", StringComparison.OrdinalIgnoreCase))
            && !(f.IsJob && app.ById.TryGetValue(f.Program.Id, out var fap) && fap.RejectFunctions.Contains(f))).ToList();
        JrePowSites(m);
        // the target's build-time data (class-path resources, the classes it loads by name), from the table J3S compiled in
        m.Target = TargetData.Load(m);
        if (m.Target is { } tdl)
            log($"target data: {tdl.Names.Count} declared resources, {tdl.Resources.Count} stored, reflection [{string.Join(", ", tdl.Reflect)}]"
                + (tdl.Problems.Count > 0 ? "; problems: " + string.Join("; ", tdl.Problems) : ""));
        // the lift to a fixed point of the decimal taint (values FSJBDEC may store, across calls, fields and results): a pass
        // that leaves the taint sets as they were is the generation; every pass lifts every function again
        int passes = 0, size;
        do
        {
            size = m.TaintSize;
            residue.Clear();
            methods.Clear();
            m.DecimalLimitSites.Clear();
            m.DecimalLimitEscapes.Clear();
            m.UncheckedDecimalOps.Clear();
            Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, fn =>
            {
                try
                {
                    var meth = LiftOne(m, fn, residue);
                    if (meth is not null) methods[fn] = meth;
                }
                catch (Exception ex) { residue.Add($"{fn.Program.Id} {fn.Id} crash {ex.GetType().Name}: {ex.Message}"); }
            });
            passes++;
        }
        while (m.TaintSize != size && passes < 16);
        if (m.TaintSize != size) residue.Add($"decimal capacity: the taint did not reach a fixed point in {passes} passes");
        if (m.DecimalInline && InlineRuntimeMismatch(m) is { } mismatch) residue.Add("decimal capacity: " + mismatch);
        if (passes > 1) log($"decimal taint: {passes} passes, {m.LimitedReturns.Count} results, {m.LimitedParams.Count} parameters, {m.LimitedFields.Count} fields, {m.CheckedFunctions.Count} checked functions");
        // in function order: everything that iterates the methods (accessors, type flows) is deterministic
        var dict = methods.OrderBy(kv => kv.Key.Id, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value);
        var (appMethods, jobObjects, appResidue) = AppBuild.Run(app, dict);
        foreach (var r in appResidue) residue.Add(r);
        // the program classes' own methods take part in type inference (their temporaries)
        foreach (var am in appMethods.Values.SelectMany(x => x)) dict.TryAdd(am.M.Fn, am.M);
        m.SetSuperclasses(dict.Values.Select(x => (x.Fn, x.Body)));
        // reflection and class-path resources of the target profile, once the class hierarchy is known
        var reflectionResidue = new List<string>();
        Reflection.Run(m, dict, reflectionResidue);
        foreach (var r in reflectionResidue) residue.Add(r);
        log($"lifted {dict.Count}/{todo.Count} functions ({sw.ElapsedMilliseconds} ms), residue {residue.Count}");
        var shapes = new Shapes(m, dict);
        shapes.Build();
        // names J3S cut to fit COBOL, completed before the accessors are named after the fields
        var recovery = new NameRecovery(m, dict, shapes);
        int rf0 = recovery.FieldsFromMethods();
        var types = new TypeInference(m);
        new TypeInferenceCollector(types, dict).Run();
        var (rf, rv) = recovery.Complete(types);
        log($"names completed: {rf0 + rf} fields, {rv} variables; abbreviations learned: {string.Join(", ", recovery.Abbreviations.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => a.Key + "=" + a.Value))}");
        shapes.AddInterfaces(types);
        shapes.PrepareAccessors();
        // value classes as records: not an exception (J3S raises them; they extend RuntimeException)
        var exceptionClasses = m.ExceptionNames.Keys.Select(id => m.ClassById.GetValueOrDefault(id)).Where(c => c is not null).Select(c => { var r = c!; while (r.Super is not null) r = r.Super; return r; }).ToHashSet();
        shapes.Records(types, exceptionClasses.Contains);
        if (Environment.GetEnvironmentVariable("TYPE_FN") is { } tfn && dict.Values.FirstOrDefault(mm => mm.Fn.Id == tfn) is { } tm)
        {
            string D(TypeInference.Slot x) { x = x.Rep; return $"C[{string.Join(",", x.C.Select(c => c.SimpleName))}] J[{string.Join(",", x.J)}] Req[{string.Join(",", x.Req.Select(c => c.SimpleName))}] DN {x.DeclName}"; }
            foreach (var v in tm.L.Params.Concat(tm.L.Vars))
                if (types.Vars.TryGetValue(v, out var sl))
                    log($"FN {v.Name}: {D(sl)}{(sl.Rep.HasElem ? " elem " + D(sl.Rep.Elem) : "")} -> {types.JavaType(sl)}");
            if (tm.Fn.Ret is not null) log($"FN ret: {D(types.Ret(tm.Fn))}{(types.Ret(tm.Fn).HasElem ? " elem " + D(types.Ret(tm.Fn).Elem) : "")}");
        }
        if (Environment.GetEnvironmentVariable("TYPE_MIXED") == "1")
            foreach (var (v, sl) in types.Vars.Where(kv => kv.Value.Rep.C.Any(c => c.Kind == ClassKind.Domain) && kv.Value.Rep.J.Any(TypeInference.IsContainer)).Take(25))
            {
                var r = sl.Rep;
                log($"MIXED {v.Name} in {dict.Values.FirstOrDefault(mm => mm.L.Vars.Contains(v) || mm.L.Params.Contains(v))?.Fn.Id}: C[{string.Join(",", r.C.Select(c => c.SimpleName))}] J[{string.Join(",", r.J)}]");
            }
        if (Environment.GetEnvironmentVariable("TYPE_FIELD") is { } fdbg)
            foreach (var (f, sl) in types.Fields.Where(kv => kv.Key.Name == fdbg))
            {
                var r = sl.Rep;
                string D(TypeInference.Slot x) { x = x.Rep; return $"C[{string.Join(",", x.C.Select(c => c.SimpleName))}] J[{string.Join(",", x.J)}] Req[{string.Join(",", x.Req.Select(c => c.SimpleName))}] Decl {x.Decl} DN {x.DeclName}"; }
                log($"FIELD {f}: {D(r)} elem {(r.HasElem ? D(r.Elem) : "-")} key {(r.HasKey ? D(r.Key) + " val " + D(r.Val) : "-")} -> {types.JavaType(r)}");
            }
        if (Environment.GetEnvironmentVariable("TYPE_DEBUG") is { } dbg)
            foreach (var (v, sl) in types.Vars.Where(kv => kv.Key.Name == dbg).Take(6))
            {
                var r = sl.Rep;
                log($"TYPE {v.Name} in {dict.Values.FirstOrDefault(mm => mm.L.Vars.Contains(v))?.Fn.Id}: C[{string.Join(",", r.C.Select(c => c.SimpleName))}] J[{string.Join(",", r.J)}] Req[{string.Join(",", r.Req.Select(c => c.SimpleName))}] Decl {r.Decl} DeclName {r.DeclName} -> {types.JavaType(r)}");
            }
        log($"types solved ({sw.ElapsedMilliseconds} ms)");
        // Phase M R1 (opt-in): the least access each member's references need, decided on the final graph before printing
        string? apiVisibility = rules?.Contains(ApiVisibility.RuleName) == true ? ApiSurface.Run(m, shapes, dict, app, appMethods, edits, r1Poison, log).Ledger : null;
        // Phase M R3 (opt-in): final on the classes the estate's evidenced hierarchy cannot extend
        string? finalClasses = rules?.Contains(FinalClasses.RuleName) == true ? FinalClassesSurface.Run(m, shapes, dict, app, edits, profile!, r3Poison, log).Ledger : null;
        // Phase M R5 (opt-in, no deployment profile): declarations whose uses are one interface's members declared with it
        string? programInterfaces = rules?.Contains(ProgramInterfaces.RuleName) == true ? ProgramInterfacesSurface.Run(m, shapes, types, dict, app, edits, r5Poison, log).Ledger : null;
        var writer = new JavaWriter(m, types, dict, shapes) { App = app, Edits = edits, Throws = CheckedExceptions.Compute(dict) };
        var files = writer.Files();
        var appWriter = new AppWriter(app, writer, appMethods, jobObjects);
        foreach (var (path, text) in appWriter.Files()) files[path] = text;
        // the decimal runtime's capacity (compat lane): emitted when a lifted site checks it
        if (files.Values.Any(t => t.Contains("import " + app.PlatformPackage + ".DecimalLimit;", StringComparison.Ordinal)))
            files[app.PlatformPackage.Replace('.', '/') + "/DecimalLimit.java"] = Platform.DecimalLimitText(app.PlatformPackage,
                files.Values.Any(t => t.Contains("DecimalLimit.JRE_POW", StringComparison.Ordinal)) ? m.JrePowSites : null,
                files.Values.Any(t => t.Contains("DecimalLimit.negate(", StringComparison.Ordinal) || t.Contains("DecimalLimit.abs(", StringComparison.Ordinal) || t.Contains("DecimalLimit.zone(", StringComparison.Ordinal)),
                m.DecimalInline);
        // the packed-decimal codec and the numeric rules of a store: emitted when a class uses them
        if (files.Values.Any(t => t.Contains("import " + app.PlatformPackage + ".PackedDecimal;", StringComparison.Ordinal)))
            files[app.PlatformPackage.Replace('.', '/') + "/PackedDecimal.java"] = Platform.PackedDecimalText(app.PlatformPackage);
        if (files.Values.Any(t => t.Contains("import " + app.PlatformPackage + ".ZonedDecimal;", StringComparison.Ordinal)))
            files[app.PlatformPackage.Replace('.', '/') + "/ZonedDecimal.java"] = Platform.ZonedDecimalText(app.PlatformPackage);
        if (files.Values.Any(t => t.Contains("import " + app.PlatformPackage + ".Numbers;", StringComparison.Ordinal)))
            files[app.PlatformPackage.Replace('.', '/') + "/Numbers.java"] = Platform.NumbersText(app.PlatformPackage);
        if (files.Values.Any(t => t.Contains("import " + app.PlatformPackage + ".JobAdapter;", StringComparison.Ordinal)))
            files[app.PlatformPackage.Replace('.', '/') + "/JobAdapter.java"] = Platform.JobAdapterText(app.PlatformPackage,
                files.Values.Any(t => t.Contains("JobAdapter.within(", StringComparison.Ordinal) || t.Contains("JobAdapter.unscaledWithin(", StringComparison.Ordinal)));
        var src = Path.Combine(outDir, "src", "main", "java");
        if (Directory.Exists(src)) Directory.Delete(src, true);
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(src, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text, new UTF8Encoding(false));
        }
        // the test sources: the lifted kernels the JDK replaces and their differential gates (run by check); only the
        // generated reference package is replaced, other tests of the project are kept
        var testSrc = Path.Combine(outDir, "src", "test", "java");
        var refDir = Path.Combine(testSrc, (app.AppPackage + ".reference").Replace('.', Path.DirectorySeparatorChar));
        if (Directory.Exists(refDir)) Directory.Delete(refDir, true);
        foreach (var (path, text) in appWriter.TestFiles)
        {
            var full = Path.Combine(testSrc, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text, new UTF8Encoding(false));
        }
        // the target's class-path resources (the bytes J3S stored at build time, one copy per name: a module has one)
        var targetResidue = new List<string>();
        if (m.Target is { } tdr)
        {
            foreach (var p in tdr.Problems) targetResidue.Add("target data: " + p);
            foreach (var g in tdr.Resources.GroupBy(r => r.Name).Where(g => g.Count() > 1))
                targetResidue.Add($"target data: resource {g.Key} has {g.Count()} class-path copies (the modern build has one class path entry)");
        }
        var all = residue.Concat(writer.Residue).Concat(appWriter.Residue).Concat(edits?.Unapplied() ?? []).Concat(Unmapped(files)).Concat(targetResidue).Order(StringComparer.Ordinal).ToList();
        var meta = Path.Combine(outDir, "modernize");
        Directory.CreateDirectory(meta);
        if (edits is not null) File.WriteAllText(Path.Combine(meta, "model-edits.json"), edits.Report());
        if (apiVisibility is not null) File.WriteAllText(Path.Combine(meta, ApiVisibility.RuleName + ".json"), apiVisibility, new UTF8Encoding(false));
        if (finalClasses is not null) File.WriteAllText(Path.Combine(meta, FinalClasses.RuleName + ".json"), finalClasses, new UTF8Encoding(false));
        if (programInterfaces is not null) File.WriteAllText(Path.Combine(meta, ProgramInterfaces.RuleName + ".json"), programInterfaces, new UTF8Encoding(false));
        // the declared deployment profile and the rules it authorized in this run (only estates that declare one)
        if (profile is not null)
            File.WriteAllText(Path.Combine(meta, "deployment-profile.json"), JsonSerializer.Serialize(profile.Record(finalClasses is not null ? [FinalClasses.RuleName] : []),
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(meta, "residue.json"), JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        // the legacy control flow structured before the lift (Loop 2): every rewrite with its program and line
        if (estate.FlowLedger.Count > 0)
            File.WriteAllText(Path.Combine(meta, "legacy-flow.json"), JsonSerializer.Serialize(estate.FlowLedger.Select(e => new { program = e.Program, line = e.Line, kind = e.Kind, detail = e.Detail, lines = e.Lines }).ToList(),
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        // the class names of the CALLed programs and their evidence (Q2): the header line, a kernel contract, the program id
        var subs = app.Programs.Where(p => p.IsSubprogram).ToList();
        if (subs.Count > 0)
            File.WriteAllText(Path.Combine(meta, "names.json"), JsonSerializer.Serialize(subs.OrderBy(p => p.P.Id, StringComparer.Ordinal).Select(p => new
            {
                program = p.P.Id,
                @class = app.AppPackage + "." + p.ClassName,
                source = p.Kernel is not null ? "kernel contract" : p.NameEvidence is { } e ? "header " + e.Source : "program id",
                file = Path.GetFileName(p.P.Path),
                line = p.NameEvidence?.Line,
                text = p.Kernel is { } k ? k.Kind + " kernel (JDK, differential gate " + k.ClassName + "Gate)" : p.NameEvidence?.Text,
            }).ToList(), new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        // the decimal capacity sites (DecimalLimit) and where a checked value leaves its function: the frontier of the check
        if (!m.DecimalLimitSites.IsEmpty || !m.DecimalLimitEscapes.IsEmpty || m.DecimalInline && !m.UncheckedDecimalOps.IsEmpty)
            File.WriteAllText(Path.Combine(meta, "decimal-limit.json"), JsonSerializer.Serialize(new
            {
                runtime = m.DecimalInline ? "FSJBDEC without a big-value store: every value at most 31 digits, every value-making operation checked at its site" : null,
                sites = m.DecimalLimitSites.Keys.Order(StringComparer.Ordinal).ToList(),
                escapes = m.DecimalLimitEscapes.Keys.Order(StringComparer.Ordinal).ToList(),
                uncheckedOperations = m.DecimalInline ? m.UncheckedDecimalOps.Keys.Order(StringComparer.Ordinal).ToList() : null,
                jrePow = m.JrePowSites.Count == 0 ? null : m.JrePowSites.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value),
                taint = m.TaintSize == 0 ? null : new
                {
                    results = m.LimitedReturns.Keys.Order(StringComparer.Ordinal).ToList(),
                    parameters = m.LimitedParams.Keys.Order(StringComparer.Ordinal).ToList(),
                    fields = m.LimitedFields.Keys.Order(StringComparer.Ordinal).ToList(),
                    checkedFunctions = m.CheckedFunctions.Keys.Order(StringComparer.Ordinal).ToList(),
                    uncheckedOperations = m.UncheckedDecimalOps.Keys.Order(StringComparer.Ordinal).ToList(),
                },
            }, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        // the job adapter's checks the code keeps (JS-CHARS, JS-FIELD, JX-JOBFAIL that survived as code) and its failures
        // after a call or a decimal conversion: proved unreachable, or legacy behavior the modern code does not preserve
        var jobFails = RaiseAnalysis.JobFailLedger(m);
        if (!m.JobAdapterSites.IsEmpty || jobFails.Count > 0)
            File.WriteAllText(Path.Combine(meta, "job-adapter.json"), JsonSerializer.Serialize(new
            {
                checks = m.JobAdapterSites.Where(kv => kv.Value != "JX-JOBFAIL"
                        || files.Values.Any(t => t.Contains("\"" + kv.Key[(kv.Key.LastIndexOf(' ') + 1)..] + "\", \"JOB ADAPTER FAILURE\")", StringComparison.Ordinal)))
                    .Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList(),
                failuresAfterCallOrDecimal = new
                {
                    provedUnreachable = jobFails.Count(f => f.Status.StartsWith("proved", StringComparison.Ordinal)),
                    unpreserved = jobFails.Count(f => f.Status.StartsWith("UNPRESERVED", StringComparison.Ordinal)),
                    unpreservedAtStartup = jobFails.Count(f => f.Startup && f.Status.StartsWith("UNPRESERVED", StringComparison.Ordinal)),
                    entries = jobFails.OrderBy(f => f.Where, StringComparer.Ordinal).Select(f => new { at = f.Where, status = f.Status, startup = f.Startup }).ToList(),
                },
            }, new JsonSerializerOptions { WriteIndented = true }));
        // the legacy sites of the statements: provenance of every statement; the reject mapping reads only the calculation
        // classes' (oracle A takes the first stack frame in a calculation class, never one of the job adapter)
        var sites = writer.SourceMap.Select(s => $"{s.File}\t{s.Line}\t{s.Site}").ToList();
        File.WriteAllLines(Path.Combine(meta, "sourcemap.tsv"), sites.Concat(appWriter.Sites.Select(s => $"{s.File}\t{s.Line}\t{s.Site}")));
        var resources = Path.Combine(outDir, "src", "main", "resources");
        Directory.CreateDirectory(resources);
        File.WriteAllLines(Path.Combine(resources, "legacy-sites.tsv"), sites);
        if (m.Target is { } tdw)
        {
            var written = new List<string>();
            foreach (var r in tdw.Resources.GroupBy(r => r.Name).Where(g => g.Count() == 1).Select(g => g.Single()))
            {
                var full = Path.Combine(resources, r.Name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, r.Bytes);
                written.Add(r.Name);
            }
            File.WriteAllText(Path.Combine(meta, "target-data.json"), tdw.Report(written, m.ResourceReads, m.ReflectionFolds, m.JobProperties, m.UnsupportedSites));
        }
        File.WriteAllText(Path.Combine(outDir, "MODERNIZATION.md"), Readme(estate, app, files, all, (rf0 + rf, rv, recovery.Abbreviations)), new UTF8Encoding(false));
        foreach (var (name, text) in Gradle(estate, app)) File.WriteAllText(Path.Combine(outDir, name), text, new UTF8Encoding(false));
        log($"wrote {files.Count} files to {src}; residue {all.Count} ({sw.ElapsedMilliseconds} ms)");
        return new Result(todo.Count, dict.Count, all.Count, files.Count, outDir);
    }

    /// <summary>
    /// The sites of J3S's JRE pow(int, MathContext) emulation (the program of class JBigDecimalOps): each FSJBDEC operation
    /// (MOVE 'xx' TO JB-OP) and the site its JB-RC guard names next (MOVE 'ABBR:line' TO JX-AT); the first of each op.
    /// </summary>
    static void JrePowSites(Model m)
    {
        var fn = m.Functions.Values.FirstOrDefault(FunctionLifter.IsJrePow);
        if (fn is null || !File.Exists(fn.Program.Path)) return;
        var lines = File.ReadAllLines(fn.Program.Path);
        for (int i = 0; i < lines.Length; i++)
        {
            var op = Regex.Match(lines[i], @"MOVE '([A-Z]{2})' TO JB-OP");
            if (!op.Success) continue;
            for (int j = i + 1; j < Math.Min(lines.Length, i + 16); j++)
            {
                if (Regex.IsMatch(lines[j], @"TO JB-OP")) break;
                var at = Regex.Match(lines[j], @"MOVE '([A-Z0-9]+:\d+)' TO JX-AT");
                if (at.Success) { m.JrePowSites.TryAdd(op.Groups[1].Value, at.Groups[1].Value); break; }
            }
        }
        foreach (var k in m.JrePowSites.Keys.Where(k => k is not ("PW" or "MU" or "SS" or "DM")).ToList()) m.JrePowSites.Remove(k);
    }

    /// <summary>
    /// A J3S JRE emulation class (fncobolgen.jre) left in the generated code has no JDK counterpart in JdkMap: the estate
    /// would not compile, so it is residue rather than a silent build failure.
    /// </summary>
    static IEnumerable<string> Unmapped(Dictionary<string, string> files) =>
        files.SelectMany(f => Regex.Matches(f.Value, @"\bfncobolgen\.jre\.[A-Za-z0-9_]+").Select(x => x.Value).Distinct()
            .Select(n => $"{f.Key}: unmapped JRE emulation {n}"));

    public static Method? LiftOne(Model m, FunctionDef fn, ConcurrentBag<string>? residue)
    {
        var l = FunctionLifter.Lift(m, fn);
        if (Environment.GetEnvironmentVariable("SHOW_LOW") is { } showLow && showLow == fn.Id) Console.Write(IrText.Show(l.Low, 1));
        var probs = new List<string>();
        var body = Structurer.Run(l.Low, probs, fn.Id);
        Passes.FixLoops(body);
        body = Passes.Tidy(body);
        body = Fold.Expressions(body);
        body = Passes.Tidy(body);
        body = new Rewrite(m, fn).Body(body);
        body = LoopPasses.Run(body);
        body = Rewrite.StreamLeftovers(body);
        body = Fold.Expressions(body);
        body = Passes.Tidy(body);
        body = Rewrite.ObjectDispatch(body);
        body = Rewrite.InterfaceDispatch(body);
        if (Environment.GetEnvironmentVariable("SHOW_FN") is { } showFn && showFn == fn.Id) Console.Write(IrText.Show(body, 1));
        ValueEquals.Run(body);
        body = Fold.Expressions(body, chains: true);
        if (fn.IsJob) Fold.ResultFields(body);
        body = Passes.Tidy(body);
        Passes.DropTailReturns(body);
        Passes.Coalesce(l, body);
        Passes.Varargs(body);
        Passes.Counting(body);
        Passes.Unlabel(body);
        body = Rewrite.TailHoist(body);
        body = Rewrite.InlineReturnedTemps(body);
        if (residue is not null)
        {
            foreach (var r in l.Residue) residue.Add(r);
            foreach (var p in probs) residue.Add($"{fn.Program.Id} {fn.Id} {p}");
        }
        return new Method { Fn = fn, L = l, Body = body };
    }

    /// <summary>
    /// J3S's catch types: the exception copybook (FSJXP) names the class of each JX-ISA-n paragraph in the comment before it
    /// ("* CATCH TYPE 2: java.lang.RuntimeException"); the paragraph itself lists only the estate's classes it matches.
    /// </summary>
    static void CatchTypes(Model m)
    {
        foreach (var dir in m.Estate.CopybookDirs.Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal))
            {
                string? pending = null;
                foreach (var line in File.ReadLines(f))
                {
                    var c = Regex.Match(line, @"^.{6}\*\s*CATCH TYPE (\d+): ([A-Za-z0-9_.$]+)\s*$");
                    if (c.Success) { pending = c.Groups[1].Value + "\t" + c.Groups[2].Value; continue; }
                    var p = Regex.Match(line, @"^.{7}(JX-ISA-(\d+))\.\s*$");
                    if (p.Success && pending is not null && pending.Split('\t')[0] == p.Groups[2].Value) m.CatchTypes.TryAdd(p.Groups[1].Value, pending.Split('\t')[1]);
                    if (line.Length > 6 && line[6] != '*') pending = null;
                }
            }
    }

    /// <summary>MODERNIZATION.md: what this generation is, its parts, how to build and run it (deterministic: no dates).</summary>
    static string Readme(Estate estate, AppModel app, Dictionary<string, string> files, List<string> residue,
        (int Fields, int Vars, IReadOnlyDictionary<string, string> Abbreviations) names)
    {
        var sb = new StringBuilder();
        int lines = files.Values.Sum(t => t.Count(c => c == '\n'));
        int appLines = files.Where(f => f.Key.StartsWith(app.AppPackage.Replace('.', '/') + "/", StringComparison.Ordinal)).Sum(f => f.Value.Count(c => c == '\n'));
        sb.Append("# ").Append(estate.Name).Append(": modern Java\n\n");
        sb.Append("Generated by `MlCpu.Modernize` (CobolBdm) from the COBOL estate; the design is `JavaToCOBOLTest/SemanticModernization.md`. ");
        sb.Append("Do not edit the code: change the lift and regenerate (CobolBdm `tools/modernize/regen.sh <estate>`).\n\n");
        sb.Append("| Part | Size |\n| --- | --- |\n");
        sb.Append($"| COBOL programs lifted | {estate.Programs.Count} |\n");
        sb.Append($"| Java files | {files.Count} ({lines} lines) |\n");
        sb.Append($"| Domain (recovered classes) | {files.Count(f => !f.Key.StartsWith(app.AppPackage.Replace('.', '/') + "/", StringComparison.Ordinal))} files ({lines - appLines} lines) |\n");
        sb.Append($"| Application layer (`{app.AppPackage}`) | {files.Count(f => f.Key.StartsWith(app.AppPackage.Replace('.', '/') + "/", StringComparison.Ordinal))} files ({appLines} lines) |\n");
        sb.Append($"| Residue (`modernize/residue.json`) | {residue.Count} |\n\n");
        sb.Append("## Programs\n\n| COBOL | Java | Role |\n| --- | --- | --- |\n");
        foreach (var ap in app.Programs)
            sb.Append($"| {ap.P.Id} | `{app.AppPackage}.{ap.ClassName}` | {(ap.IsJob ? "job service: `call(...)` per record" : ap.IsSubprogram ? "subprogram: `call(...)` with the caller's records" : "batch step: `main`, datasets by DD name")} |\n");
        sb.Append("\n## Names\n\n");
        sb.Append($"COBOL names are cut (a local at 12 characters) and abbreviated. {names.Fields} fields and {names.Vars} variables were completed from the full names the estate keeps ");
        sb.Append("(method signatures, getters, builder methods, call arguments, types); a name without such evidence keeps its COBOL form.");
        sb.Append(names.Abbreviations.Count == 0 ? " No abbreviations were found.\n"
            : " Abbreviations learned from the estate (paragraph names beside their method names): "
              + string.Join(", ", names.Abbreviations.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => $"{a.Key} {a.Value}")) + ".\n");
        sb.Append("\n## Build and run\n\n```\n");
        sb.Append("gradle build                       # javac --release 21 -Xlint:all (GN1)\n");
        foreach (var ap in app.Programs.Where(p => p.IsStep))
        {
            sb.Append($"gradle run{Naming.Pascal(ap.P.Id)}");
            foreach (var f in ap.P.Model.Files) sb.Append($" -Pdd.{f.Assign}=<path>");
            sb.Append('\n');
        }
        sb.Append("```\n\nWithout Gradle:\n\n```\n");
        sb.Append("javac -d build/classes $(find src/main/java -name '*.java')\n");
        foreach (var ap in app.Programs.Where(p => p.IsStep))
        {
            sb.Append($"java -XX:-OmitStackTraceInFastThrow -cp \"build/classes{Path.PathSeparator}src/main/resources\"");
            foreach (var f in ap.P.Model.Files) sb.Append($" -Ddd.{f.Assign}=<path>");
            sb.Append($" {app.AppPackage}.{ap.ClassName}\n");
        }
        sb.Append("```\n\n");
        if (app.Programs.Any(p => p.IsStep))
        {
            var first = app.Programs.First(p => p.IsStep).P.Id;
            sb.Append("## The strangler\n\n");
            sb.Append($"`{app.AppPackage}.strangler.Strangler STEP MODE [DD=path ...]` runs a batch step behind its port: LEGACY (the legacy job), MODERN (this code) ");
            sb.Append("or SHADOW (both on the same input; the legacy outputs stay the step's, the modern ones are written as `NAME.modern` and compared record by record, ");
            sb.Append("every difference reported with its request and field in `shadow-report.json`). The legacy job is a command: `-Dlegacy.STEP=<command>` ");
            sb.Append("(the GnuCOBOL executable of the program), with `-Dlegacy.path=<runtime dir>`; it gets its datasets as `DD_NAME` environment variables.\n\n```\n");
            sb.Append($"gradle strangler -Pstep={first} -Pmode=SHADOW -Plegacy.{first}=<legacy executable> -Plegacy.path=<GnuCOBOL bin> -Pdd.NAME=<path> ...\n```\n\n");
            sb.Append("GN4 (CobolBdm `tools/modernize/gn4.sh`) runs SHADOW over the scenarios and passes with no difference.\n\n");
        }
        sb.Append("A dataset is a path given by `-Ddd.NAME`, else the environment variable `DD_NAME`, else `NAME` in the working directory; ");
        sb.Append("an input with line ends is read a line at a time, outputs are fixed-length records. ");
        sb.Append("`-XX:-OmitStackTraceInFastThrow` keeps every exception's stack trace, which the reject mapping reads (`LegacySites`). ");
        sb.Append("`-Dlegacy.trace=true` prints the exception behind every reject.\n");
        return sb.ToString();
    }

    /// <summary>
    /// The inline decimal runtime DecimalLimitInline models (FSJBDEC and FNDKRND byte for byte, FSJRBC's JB-CALL simple cases):
    /// another version is not a runtime the model was derived from, so its capacity is not claimed (null when it is this one).
    /// </summary>
    static string? InlineRuntimeMismatch(Model m)
    {
        static string Sha(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s.Replace("\r\n", "\n")))).ToLowerInvariant();
        string? Text(string id) => m.Estate.Programs.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } p && File.Exists(p.Path) ? File.ReadAllText(p.Path, Encoding.Latin1) : null;
        var problems = new List<string>();
        if (Text("FSJBDEC") is not { } dec || Sha(dec) != "ec22fbd30f6cdc6b330ff890889610404165a13e0c3e8d1dfc8571ed1dc154fe") problems.Add("FSJBDEC");
        if (Text("FNDKRND") is not { } rnd || Sha(rnd) != "246c20006c9350ff95e7f1e01323e7f9dcdc8d45cd50e3eed71d924c525320a2") problems.Add("FNDKRND");
        var rbc = Text("FSJRBC");
        int i = rbc?.IndexOf("       JB-CALL.", StringComparison.Ordinal) ?? -1, j = rbc?.IndexOf("       JB-ERR.", StringComparison.Ordinal) ?? -1;
        if (rbc is null || i < 0 || j < i || Sha(rbc[i..j]) != "c39bcc704a0cba5b235d3bb0d000175e4b95a111c11da1516774fff189dbe854") problems.Add("FSJRBC JB-CALL");
        return problems.Count == 0 ? null : $"the runtime without a big-value store differs from the modeled one ({string.Join(", ", problems)}): its capacity has no checked form";
    }

    /// <summary>The Gradle build of the generated project: javac --release 21 with -Xlint (GN1), a task per batch step and the strangler.</summary>
    static IEnumerable<(string Name, string Text)> Gradle(Estate estate, AppModel app)
    {
        yield return ("settings.gradle.kts", $"// {estate.Name}: generated by MlCpu.Modernize (CobolBdm). Do not edit: change the lift and regenerate.\nrootProject.name = \"{estate.Name}\"\n");
        var sb = new StringBuilder();
        sb.Append($"// {estate.Name}: modern Java generated by MlCpu.Modernize (CobolBdm) from the COBOL estate. Do not edit: change the lift and regenerate.\n");
        // the resource pins (verifyTargetData) hash in the build script: java.* is the java extension there, so imported
        if (app.M.Target is { Resources.Count: > 0 }) sb.Append("import java.security.MessageDigest\n\n");
        sb.Append("plugins {\n    java\n}\n\n");
        sb.Append($"group = \"{app.BasePackage}\"\n\n");
        sb.Append("tasks.withType<JavaCompile>().configureEach {\n");
        sb.Append("    options.release.set(21)\n");
        sb.Append("    options.encoding = \"UTF-8\"\n");
        sb.Append("    // GN1: what javac warns about is reported\n");
        sb.Append("    options.compilerArgs.addAll(listOf(\"-Xlint:all\", \"-Xlint:-serial\", \"-Xlint:-this-escape\"))\n");
        sb.Append("}\n\n");
        sb.Append("// every step keeps the stack traces the reject mapping reads (LegacySites)\n");
        sb.Append("val stepJvmArgs = listOf(\"-XX:-OmitStackTraceInFastThrow\")\n\n");
        sb.Append("// datasets by DD name (-Pdd.NAME=path), the legacy job and the shadow report (-Plegacy.STEP=..., -Pshadow.report=...)\n");
        sb.Append("fun JavaExec.passProperties() {\n");
        sb.Append("    systemProperties(project.properties.filterKeys { it.startsWith(\"dd.\") || it.startsWith(\"legacy.\") || it.startsWith(\"shadow.\") })\n");
        sb.Append("}\n");
        foreach (var ap in app.Programs.Where(p => p.IsStep))
        {
            sb.Append($"\ntasks.register<JavaExec>(\"run{Naming.Pascal(ap.P.Id)}\") {{\n");
            sb.Append("    group = \"batch\"\n");
            sb.Append($"    description = \"Batch step {ap.P.Id} (legacy program {ap.P.Id}): {string.Join(" ", ap.P.Model.Files.Select(f => $"-Pdd.{f.Assign}=<path>"))}\"\n");
            sb.Append("    classpath = sourceSets[\"main\"].runtimeClasspath\n");
            sb.Append($"    mainClass.set(\"{app.AppPackage}.{ap.ClassName}\")\n");
            sb.Append("    jvmArgs(stepJvmArgs)\n");
            sb.Append("    passProperties()\n");
            sb.Append("}\n");
        }
        if (app.Programs.Any(p => p.IsStep))
        {
            sb.Append("\ntasks.register<JavaExec>(\"strangler\") {\n");
            sb.Append("    group = \"batch\"\n");
            sb.Append("    description = \"The strangler: -Pstep=STEP -Pmode=LEGACY|SHADOW|MODERN, -Pdd.NAME=path, -Plegacy.STEP=<legacy command>, -Plegacy.path=<runtime dir>\"\n");
            sb.Append("    classpath = sourceSets[\"main\"].runtimeClasspath\n");
            sb.Append($"    mainClass.set(\"{app.AppPackage}.strangler.Strangler\")\n");
            sb.Append("    jvmArgs(stepJvmArgs)\n");
            sb.Append("    passProperties()\n");
            sb.Append("    args(listOfNotNull(project.findProperty(\"step\")?.toString(), project.findProperty(\"mode\")?.toString()))\n");
            sb.Append("}\n");
        }
        if (app.Programs.Any(p => p.Kernel is not null))
        {
            sb.Append("\n// the kernels' differential gates: the JDK substitutes against the lifted COBOL kernels (src/test/java), part of check\n");
            sb.Append("val kernelGates by tasks.registering(JavaExec::class) {\n");
            sb.Append("    group = \"verification\"\n");
            sb.Append("    description = \"Runs every JDK substitute and its lifted kernel on the same cases; fails on a difference or an undetected mutant\"\n");
            sb.Append("    classpath = sourceSets[\"test\"].runtimeClasspath\n");
            sb.Append($"    mainClass.set(\"{app.AppPackage}.reference.KernelGates\")\n");
            sb.Append("    jvmArgs(stepJvmArgs)\n");
            sb.Append("}\n");
            sb.Append("tasks.named(\"check\") {\n");
            sb.Append("    dependsOn(kernelGates)\n");
            sb.Append("}\n");
            sb.Append("// the test sources are the gates' (no test framework): no tests to discover\n");
            sb.Append("tasks.named<Test>(\"test\") {\n");
            sb.Append("    failOnNoDiscoveredTests.set(false)\n");
            sb.Append("}\n");
        }
        if (app.M.Target is { } td && td.Resources.GroupBy(r => r.Name).Where(g => g.Count() == 1).Select(g => g.Single()).ToList() is { Count: > 0 } pinned)
        {
            var absent = td.Names.Where(n => td.Resources.All(r => r.Name != n)).ToList();
            sb.Append("\n// the class-path resources the code reads, pinned to the bytes J3S stored at build time (modernize/target-data.json):\n");
            sb.Append("// a changed or missing resource, or one declared absent that exists, fails the build (no other registry is run)\n");
            sb.Append("val verifyTargetData by tasks.registering {\n");
            sb.Append("    group = \"verification\"\n");
            sb.Append("    description = \"Checks the class-path resources against their pinned SHA-256 and the declared-absent names\"\n");
            sb.Append("    dependsOn(tasks.named(\"processResources\"))\n");
            sb.Append("    val dir = layout.buildDirectory.dir(\"resources/main\")\n");
            sb.Append("    doLast {\n");
            sb.Append("        val pinned = mapOf(\n");
            foreach (var r in pinned.OrderBy(r => r.Name, StringComparer.Ordinal)) sb.Append($"            \"{r.Name}\" to \"{r.Sha256}\",\n");
            sb.Append("        )\n");
            sb.Append($"        val absent = listOf({string.Join(", ", absent.Order(StringComparer.Ordinal).Select(n => "\"" + n + "\""))})\n");
            sb.Append("        for ((name, sha) in pinned) {\n");
            sb.Append("            val f = dir.get().file(name).asFile\n");
            sb.Append("            val actual = if (f.isFile) MessageDigest.getInstance(\"SHA-256\").digest(f.readBytes()).joinToString(\"\") { b -> \"%02x\".format(b) } else \"missing\"\n");
            sb.Append("            if (actual != sha) throw GradleException(\"class-path resource $name: sha256 $actual, pinned $sha\")\n");
            sb.Append("        }\n");
            sb.Append("        for (name in absent) if (dir.get().file(name).asFile.exists()) throw GradleException(\"class-path resource $name exists, declared absent\")\n");
            sb.Append("    }\n");
            sb.Append("}\n");
            sb.Append("tasks.named(\"classes\") {\n");
            sb.Append("    dependsOn(verifyTargetData)\n");
            sb.Append("}\n");
        }
        yield return ("build.gradle.kts", sb.ToString());
    }

    /// <summary>The string literal table (statics SLIT-n) from the job function that builds it.</summary>
    static void Literals(Model m, Action<string> log)
    {
        var fn = m.Functions.Values.FirstOrDefault(f => f.IsJob && f.MethodName.Contains("STRING-LITERALS", StringComparison.OrdinalIgnoreCase));
        if (fn is null) return;
        var meth = LiftOne(m, fn, null);
        if (meth is null) return;
        // a literal COBOL cannot spell ({, }) is built a character code at a time in an array: t = new int[n]; t[k] = c;
        var arrays = new Dictionary<Var, int[]>();
        foreach (var s in Passes.All(meth.Body))
        {
            if (s is SAssign { Lhs: EStatic st, Rhs: ELit { Value: string text } }) m.Literals[st.F.CobolName] = text;
            else if (s is SAssign { Lhs: EVar av, Rhs: EArrayNew { Len: ELit { Value: long n } } } && n is >= 0 and < 100000) arrays[av.V] = new int[n];
            else if (s is SAssign { Lhs: EIndex { A: EVar iv, I: ELit { Value: long k } }, Rhs: ELit { Value: long code } } && arrays.TryGetValue(iv.V, out var codes) && k >= 0 && k < codes.Length) codes[k] = (int)code;
            else if (s is SAssign { Lhs: EStatic st2, Rhs: ENew { Args: [EVar src] } } && arrays.TryGetValue(src.V, out var chars))
                m.Literals[st2.F.CobolName] = new string(chars.Select(c => (char)c).ToArray());
        }
        log($"literals {m.Literals.Count}");
    }
}
