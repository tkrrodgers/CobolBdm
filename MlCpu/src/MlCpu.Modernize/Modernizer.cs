using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace MlCpu.Modernize;

/// <summary>The pipeline stage: COBOL estate → semantic model → modern Java (docs: JavaToCOBOLTest/SemanticModernization.md).</summary>
public static class Modernizer
{
    public sealed record Result(int Functions, int Lifted, int Residue, int Files, string OutDir);

    /// <param name="edits">Model edits applied while the model is printed (opt-in, <see cref="ModelEdits"/>).</param>
    public static Result Run(string project, string outDir, Action<string>? log = null, ModelEdits? edits = null)
    {
        log ??= Console.WriteLine;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var estate = Estate.Load(project);
        var m = Model.Build(estate);
        log($"estate {estate.Name}: {estate.Programs.Count} programs, {m.Functions.Count} functions, {m.Classes.Count} classes ({sw.ElapsedMilliseconds} ms)");
        Literals(m, log);
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
        Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, fn =>
        {
            try
            {
                var meth = LiftOne(m, fn, residue);
                if (meth is not null) methods[fn] = meth;
            }
            catch (Exception ex) { residue.Add($"{fn.Program.Id} {fn.Id} crash {ex.GetType().Name}: {ex.Message}"); }
        });
        // in function order: everything that iterates the methods (accessors, type flows) is deterministic
        var dict = methods.OrderBy(kv => kv.Key.Id, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value);
        var (appMethods, jobObjects, appResidue) = AppBuild.Run(app, dict);
        foreach (var r in appResidue) residue.Add(r);
        // the program classes' own methods take part in type inference (their temporaries)
        foreach (var am in appMethods.Values.SelectMany(x => x)) dict.TryAdd(am.M.Fn, am.M);
        m.SetSuperclasses(dict.Values.Select(x => (x.Fn, x.Body)));
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
        var writer = new JavaWriter(m, types, dict, shapes) { App = app, Edits = edits };
        var files = writer.Files();
        var appWriter = new AppWriter(app, writer, appMethods, jobObjects);
        foreach (var (path, text) in appWriter.Files()) files[path] = text;
        var src = Path.Combine(outDir, "src", "main", "java");
        if (Directory.Exists(src)) Directory.Delete(src, true);
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(src, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text, new UTF8Encoding(false));
        }
        var all = residue.Concat(writer.Residue).Concat(appWriter.Residue).Concat(edits?.Unapplied() ?? []).Order(StringComparer.Ordinal).ToList();
        var meta = Path.Combine(outDir, "modernize");
        Directory.CreateDirectory(meta);
        if (edits is not null) File.WriteAllText(Path.Combine(meta, "model-edits.json"), edits.Report());
        File.WriteAllText(Path.Combine(meta, "residue.json"), JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        // the legacy sites of the statements: provenance of every statement; the reject mapping reads only the calculation
        // classes' (oracle A takes the first stack frame in a calculation class, never one of the job adapter)
        var sites = writer.SourceMap.Select(s => $"{s.File}\t{s.Line}\t{s.Site}").ToList();
        File.WriteAllLines(Path.Combine(meta, "sourcemap.tsv"), sites.Concat(appWriter.Sites.Select(s => $"{s.File}\t{s.Line}\t{s.Site}")));
        var resources = Path.Combine(outDir, "src", "main", "resources");
        Directory.CreateDirectory(resources);
        File.WriteAllLines(Path.Combine(resources, "legacy-sites.tsv"), sites);
        File.WriteAllText(Path.Combine(outDir, "MODERNIZATION.md"), Readme(estate, app, files, all, (rf0 + rf, rv, recovery.Abbreviations)), new UTF8Encoding(false));
        foreach (var (name, text) in Gradle(estate, app)) File.WriteAllText(Path.Combine(outDir, name), text, new UTF8Encoding(false));
        log($"wrote {files.Count} files to {src}; residue {all.Count} ({sw.ElapsedMilliseconds} ms)");
        return new Result(todo.Count, dict.Count, all.Count, files.Count, outDir);
    }

    public static Method? LiftOne(Model m, FunctionDef fn, ConcurrentBag<string>? residue)
    {
        var l = FunctionLifter.Lift(m, fn);
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
        body = Fold.Expressions(body, chains: true);
        body = Passes.Tidy(body);
        while (body.Count > 0 && body[^1] is SReturn { E: null }) body.RemoveAt(body.Count - 1);
        Passes.Coalesce(l, body);
        Passes.Varargs(body);
        Passes.Counting(body);
        Passes.Unlabel(body);
        if (residue is not null)
        {
            foreach (var r in l.Residue) residue.Add(r);
            foreach (var p in probs) residue.Add($"{fn.Program.Id} {fn.Id} {p}");
        }
        return new Method { Fn = fn, L = l, Body = body };
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
            sb.Append($"| {ap.P.Id} | `{app.AppPackage}.{ap.ClassName}` | {(ap.IsJob ? "job service: `call(...)` per record" : "batch step: `main`, datasets by DD name")} |\n");
        sb.Append("\n## Names\n\n");
        sb.Append($"COBOL names are cut (a local at 12 characters) and abbreviated. {names.Fields} fields and {names.Vars} variables were completed from the full names the estate keeps ");
        sb.Append("(method signatures, getters, builder methods, call arguments, types); a name without such evidence keeps its COBOL form.");
        sb.Append(names.Abbreviations.Count == 0 ? " No abbreviations were found.\n"
            : " Abbreviations learned from the estate (paragraph names beside their method names): "
              + string.Join(", ", names.Abbreviations.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => $"{a.Key} {a.Value}")) + ".\n");
        sb.Append("\n## Build and run\n\n```\n");
        sb.Append("gradle build                       # javac --release 21 -Xlint:all (GN1)\n");
        foreach (var ap in app.Programs.Where(p => !p.IsJob))
        {
            sb.Append($"gradle run{Naming.Pascal(ap.P.Id)}");
            foreach (var f in ap.P.Model.Files) sb.Append($" -Pdd.{f.Assign}=<path>");
            sb.Append('\n');
        }
        sb.Append("```\n\nWithout Gradle:\n\n```\n");
        sb.Append("javac -d build/classes $(find src/main/java -name '*.java')\n");
        foreach (var ap in app.Programs.Where(p => !p.IsJob))
        {
            sb.Append($"java -XX:-OmitStackTraceInFastThrow -cp \"build/classes{Path.PathSeparator}src/main/resources\"");
            foreach (var f in ap.P.Model.Files) sb.Append($" -Ddd.{f.Assign}=<path>");
            sb.Append($" {app.AppPackage}.{ap.ClassName}\n");
        }
        sb.Append("```\n\n");
        if (app.Programs.Any(p => !p.IsJob))
        {
            var first = app.Programs.First(p => !p.IsJob).P.Id;
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

    /// <summary>The Gradle build of the generated project: javac --release 21 with -Xlint (GN1), a task per batch step and the strangler.</summary>
    static IEnumerable<(string Name, string Text)> Gradle(Estate estate, AppModel app)
    {
        yield return ("settings.gradle.kts", $"// {estate.Name}: generated by MlCpu.Modernize (CobolBdm). Do not edit: change the lift and regenerate.\nrootProject.name = \"{estate.Name}\"\n");
        var sb = new StringBuilder();
        sb.Append($"// {estate.Name}: modern Java generated by MlCpu.Modernize (CobolBdm) from the COBOL estate. Do not edit: change the lift and regenerate.\n");
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
        foreach (var ap in app.Programs.Where(p => !p.IsJob))
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
        if (app.Programs.Any(p => !p.IsJob))
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
