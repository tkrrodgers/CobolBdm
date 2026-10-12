using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MlCpu.Cobol.Analysis;
using MlCpu.Cobol.Analysis.JavaBridge;
using MlCpu.Core;

// Headless CLI (§5.8): model → IR → sidecar. Stops at transpilation; project assembly runs in the host.
var opts = ParseArgs(args);
if (opts.ContainsKey("help") || args.Length == 0)
{
    Console.WriteLine("""
        MlCpu.JavaBridge
          --file <path> [--ir-out <file>] [--sidecar <url>] [--spill-dir <dir>] [--transpile-only]
          --batch-dir <dir> --spill-dir <dir> --report <path> [--sidecar <url>]
          --analyze <path>        print parse diagnostics and frontiers only
          --kb <siteDir> --estate <app root>                     kb/jobs.jsonl, programs.jsonl, meta.json
          --graph <dir> --estate <code root>                     program graphs + estate call graph
          --dd-binding-plan <job> --estate <app root> --out <dir> [--as-of yyyy-MM-dd]
          --replay-plan <job> --estate <app root> --out <dir> [--as-of yyyy-MM-dd] [--encoding CP037]
          --ast <file|dir|project.json> [--out <dir>] [--timeout <s>] [--errors <n>]   COBOL-85 parse (ANTLR): AST JSON + outline per program
        """);
    return 0;
}

var sidecar = opts.GetValueOrDefault("sidecar") ?? Environment.GetEnvironmentVariable("MLCPU_JAVA_SIDECAR") ?? "http://127.0.0.1:8079";
using var http = new HttpClient { BaseAddress = new Uri(sidecar), Timeout = TimeSpan.FromSeconds(120) };

if (opts.TryGetValue("ast", out var astPath))
{
    // --ast <file|dir|project.json> [--out <dir>] [--timeout <s>] [--errors <n>]: grammars-v4 COBOL-85 parse;
    // <PID>.ast.json and <PID>.outline.txt when --out is given. A project.json parses that project's programs with its
    // configured copybook roots (layout.copybookRoots), as the host's graph stage does.
    var outDir = opts.GetValueOrDefault("out");
    if (outDir is not null) Directory.CreateDirectory(outDir);
    TimeSpan? timeout = opts.TryGetValue("timeout", out var ts) && double.TryParse(ts, out var sec) ? TimeSpan.FromSeconds(sec) : null;
    int showErrors = opts.TryGetValue("errors", out var se) && int.TryParse(se, out var n) ? n : 1;
    IReadOnlyList<string> files;
    if (astPath!.EndsWith("project.json", StringComparison.OrdinalIgnoreCase))
    {
        var proj = JsonNode.Parse(File.ReadAllText(astPath))!;
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(astPath))!, proj["layout"]?["sourceDir"]?.GetValue<string>() ?? "source"));
        var roots = (proj["layout"]?["copybookRoots"]?.AsArray() ?? []).Select(r => Path.GetFullPath(Path.Combine(srcDir, r!.GetValue<string>()))).Where(Directory.Exists);
        CobolModelBuilder.ConfigureCopybookRoots(srcDir, roots);
        files = proj["programs"]!.AsArray().Select(x => Path.GetFullPath(Path.Combine(srcDir, x!["path"]!.GetValue<string>()))).ToList();
    }
    else files = Directory.Exists(astPath) ? CobolFiles.Programs(astPath!) : [astPath!];
    int ok = 0;
    foreach (var f in files)
    {
        // heap = managed memory the parse result holds (tree, tokens, preprocessed text) plus the grammar's shared DFA growth
        long baseHeap = GC.GetTotalMemory(true);
        var p = MlCpu.Cobol.Antlr.CobolAntlr.Parse(f, timeout);
        long retained = GC.GetTotalMemory(true) - baseHeap;
        if (p.Ok) ok++;
        var json = MlCpu.Cobol.Antlr.AstExport.ToJsonText(p);
        var outline = MlCpu.Cobol.Antlr.AstExport.Outline(p);
        Console.WriteLine($"{p.ProgramId,-10} {(p.Ok ? "OK " : p.TimedOut ? "TIMEOUT" : "ERR")} {p.Mode,-3} {p.Elapsed.TotalMilliseconds,9:N0} ms  " +
            $"lines={p.Source.ProgramLines.Count,7:N0} tokens={p.Tokens.Size,9:N0} heap={retained / (1 << 20),5:N0} MB ast={json.Length / 1024,7:N0} KB outline={outline.Length / 1024,6:N0} KB  errors={p.Errors.Count}" +
            string.Concat(p.Errors.Take(showErrors).Select(e => "\n    " + (e.Length > 240 ? e[..240] + "…" : e))));
        if (outDir is not null)
        {
            File.WriteAllText(Path.Combine(outDir, p.ProgramId + ".ast.json"), json);
            File.WriteAllText(Path.Combine(outDir, p.ProgramId + ".outline.txt"), outline);
        }
    }
    Console.WriteLine($"{ok}/{files.Count} parsed without errors (peak working set {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / (1 << 20):N0} MB)");
    return ok == files.Count ? 0 : 1;
}

if (opts.TryGetValue("analyze", out var analyzePath))
{
    var ir = IrExporter.Export(CobolModelBuilder.BuildGroundedFull(analyzePath!));
    Console.WriteLine($"{ir.Program.Id}: {ir.Dictionary.Count} items, {ir.Paragraphs.Count} paragraphs, {ir.Paragraphs.Sum(p => p.Statements.Count)} statements, {ir.Frontiers.Count} frontiers");
    var g = ir.Graph!;
    Console.WriteLine($"  graph: {g.ParagraphEdges.Count} paragraph edges, {g.CtrlDeps.Count} control deps, {g.DataDeps.Count} data deps, " +
        $"{g.Calls.Count} calls, dead paragraphs [{string.Join(", ", g.DeadParagraphs)}], dataflow iterations {g.DataflowIterations}");
    foreach (var f in ir.Frontiers) Console.WriteLine($"  [{f.Kind}] {f.Subject}: {f.Detail}");
    return 0;
}

if (opts.TryGetValue("dd-binding-plan", out var ddJob) | opts.TryGetValue("replay-plan", out var rpJob))
{
    var app = opts.GetValueOrDefault("estate") ?? throw new ArgumentException("--estate is required");
    var outDir = opts.GetValueOrDefault("out") ?? throw new ArgumentException("--out is required");
    var asOf = opts.GetValueOrDefault("as-of") ?? "2026-10-01";
    var jobName = ddJob ?? rpJob!;
    var job = MlCpu.CobolKb.Knowledge.JclHarvester.Harvest([Path.Combine(app, "jcl")], [Path.Combine(app, "proc")])
        .FirstOrDefault(j => string.Equals(j.Name, jobName, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"job {jobName} not found");
    var sources = CobolFiles.Programs(Path.Combine(app, "cbl")).ToDictionary(p => Path.GetFileNameWithoutExtension(p).ToUpperInvariant(), p => p, StringComparer.OrdinalIgnoreCase);
    Directory.CreateDirectory(outDir);
    var plan = MlCpu.CobolKb.Knowledge.DdBindingPlanner.Plan(job, sources, asOf, out var models, Console.WriteLine);
    File.WriteAllText(Path.Combine(outDir, "DdBindingPlan.json"), MlCpu.CobolKb.Knowledge.DdBindingPlanner.ToJson(plan));
    Console.WriteLine($"{plan.Job}: {string.Join(", ", plan.Summary.Select(kv => $"{kv.Key}={kv.Value}"))}");
    foreach (var u in plan.Unbound) Console.WriteLine("  unbound " + u);
    if (rpJob is not null)
    {
        Directory.CreateDirectory(Path.Combine(outDir, "replay"));
        var replay = MlCpu.CobolKb.Knowledge.ReplayPlanBuilder.Build(plan, models, Path.GetFileName(Path.GetFullPath(outDir)), opts.GetValueOrDefault("encoding") ?? "CP037");
        File.WriteAllText(Path.Combine(outDir, "replay", "ReplayPlan.json"), MlCpu.CobolKb.Knowledge.ReplayPlanBuilder.BuildJson(plan, models, Path.GetFileName(Path.GetFullPath(outDir)), opts.GetValueOrDefault("encoding") ?? "CP037"));
        foreach (var s in replay.Steps)
            foreach (var o in s.Outputs)
                Console.WriteLine($"  {s.Step,-16} {o.Dd,-9} layouts=[{string.Join(",", o.Layouts.Select(l => l.Role + ":" + l.Record))}] volatile=[{string.Join(",", o.VolatileFields)}]");
    }
    return plan.Unbound.Count == 0 ? 0 : 2;
}

if (opts.TryGetValue("requirements", out var reqJobs))
{
    // --requirements <JOB[,JOB]> --estate <app root> --out <dir>: normalization/<PID>/requirements.json + design.schema.json + index.json
    var app = opts.GetValueOrDefault("estate") ?? throw new ArgumentException("--estate is required");
    var outDir = opts.GetValueOrDefault("out") ?? throw new ArgumentException("--out is required");
    var jobs = MlCpu.CobolKb.Knowledge.JclHarvester.Harvest([Path.Combine(app, "jcl")], [Path.Combine(app, "proc")]);
    var sources = CobolFiles.Programs(Path.Combine(app, "cbl")).ToDictionary(p => Path.GetFileNameWithoutExtension(p).ToUpperInvariant(), p => p, StringComparer.OrdinalIgnoreCase);
    Directory.CreateDirectory(outDir);
    var index = new JsonArray();
    foreach (var jobName in reqJobs!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var job = jobs.First(j => string.Equals(j.Name, jobName, StringComparison.OrdinalIgnoreCase));
        var plan = MlCpu.CobolKb.Knowledge.DdBindingPlanner.Plan(job, sources, "2026-10-01", out var models);
        var replay = MlCpu.CobolKb.Knowledge.ReplayPlanBuilder.Build(plan, models, "normalization");
        foreach (var (pid, model) in models)
        {
            var packet = MlCpu.CobolKb.Knowledge.RequirementsPacketBuilder.Build(model, ProgramGraphBuilder.Build(model, dataDeps: false), sources[pid], plan, replay);
            var dir = Path.Combine(outDir, pid);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "requirements.json"), MlCpu.CobolKb.Knowledge.RequirementsPacketBuilder.ToJson(packet));
            index.Add(new JsonObject { ["programId"] = pid, ["job"] = job.Name, ["requirementsHash"] = packet["requirementsHash"]!.GetValue<string>(),
                ["rules"] = packet["rules"]!.AsArray().Count, ["quirks"] = packet["legacyQuirks"]!.AsArray().Count, ["path"] = $"{pid}/requirements.json" });
            Console.WriteLine($"{pid,-9} rules={packet["rules"]!.AsArray().Count,3} quirks={packet["legacyQuirks"]!.AsArray().Count} hash={packet["requirementsHash"]!.GetValue<string>()[..12]} " +
                $"bytes={new FileInfo(Path.Combine(dir, "requirements.json")).Length:N0}");
        }
    }
    File.WriteAllText(Path.Combine(outDir, "design.schema.json"), MlCpu.CobolKb.Knowledge.RequirementsPacketBuilder.DesignSchema + "\n");
    File.WriteAllText(Path.Combine(outDir, "index.json"), new JsonObject { ["schema"] = "mlcpu.normalization-index.v1", ["programs"] = index }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    return 0;
}

if (opts.TryGetValue("kb", out var kbSite))
{
    // --kb <siteDir> --estate <app root containing cbl/, jcl/, proc/>
    var app = opts.GetValueOrDefault("estate") ?? throw new ArgumentException("--estate is required");
    var kb = MlCpu.CobolKb.Knowledge.KnowledgeBase.Build(Path.Combine(app, "cbl"), [Path.Combine(app, "jcl")], [Path.Combine(app, "proc")], kbSite!, Console.WriteLine);
    Console.WriteLine($"KB {kb.KbDir}: {kb.Programs.Count} programs, {kb.Jobs.Count} jobs, {kb.Jobs.Sum(j => j.Steps.Count)} steps");
    foreach (var j in kb.Jobs.Where(j => j.Steps.Any(s => s.InvocationChain.Count > 1)))
        foreach (var s in j.Steps)
            Console.WriteLine($"  {j.Name,-9} {s.Order,2} {s.QualifiedStep,-18} {s.Program,-9} {string.Join(" > ", s.InvocationChain.Skip(1))} dds=[{string.Join(",", s.Datasets.Select(d => d.Dd + ":" + d.Direction))}]");
    foreach (var d in kb.Jobs.SelectMany(j => j.Diagnostics).Distinct()) Console.WriteLine($"  ! {d.Member}:{d.Line} {d.Message}");
    return 0;
}

if (opts.TryGetValue("graph", out var graphDir))
{
    // --graph <dir> --estate <code root>: per-program graph JSON plus the estate call graph.
    var root = opts.GetValueOrDefault("estate") ?? throw new ArgumentException("--estate is required");
    Directory.CreateDirectory(graphDir!);
    var graphs = new List<ProgramGraph>();
    foreach (var path in CobolFiles.Programs(root))
    {
        var model = CobolModelBuilder.BuildGroundedFull(path);
        var g = ProgramGraphBuilder.Build(model);
        graphs.Add(g);
        File.WriteAllText(Path.Combine(graphDir!, g.ProgramId + ".graph.json"), JsonSerializer.Serialize(g, IrExporter.Json));
        Console.WriteLine($"{g.ProgramId,-10} paraEdges={g.ParagraphEdges.Count,-4} ctrl={g.CtrlDeps.Count,-4} data={g.DataDeps.Count,-5} calls={g.Calls.Count,-3} dead={g.DeadParagraphs.Count} frontiers={g.Frontiers.Count}");
    }
    var ids = graphs.Select(x => x.ProgramId).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var edges = EstateCallGraph.Build(graphs, ids);
    File.WriteAllText(Path.Combine(graphDir!, "estate-call-graph.json"), JsonSerializer.Serialize(edges, new JsonSerializerOptions(IrExporter.Json) { WriteIndented = true }));
    Console.WriteLine($"estate call graph: {edges.Count} edges ({edges.Count(e => e.InEstate)} in-estate, {edges.Count(e => e.Kind == "unresolved")} unresolved)");
    return 0;
}

if (opts.TryGetValue("file", out var file))
{
    // the programs after the first one of a source (separate or nested) are transpiled and spilled too
    foreach (var other in CobolModelBuilder.BuildAllGroundedFull(file!).Skip(1))
        Console.WriteLine(Summary(await Transpile(http, IrExporter.Export(other), opts.GetValueOrDefault("spill-dir"), opts.ContainsKey("transpile-only"))));
    var ir = IrExporter.Export(CobolModelBuilder.BuildGroundedFull(file!));
    if (opts.TryGetValue("ir-out", out var irOut))
    {
        File.WriteAllText(irOut!, IrExporter.ToJson(ir));
        Console.WriteLine($"IR written: {irOut}");
        if (!opts.ContainsKey("sidecar") && !opts.ContainsKey("spill-dir")) return 0;
    }
    var result = await Transpile(http, ir, opts.GetValueOrDefault("spill-dir"), opts.ContainsKey("transpile-only"));
    Console.WriteLine(Summary(result));
    return result?["compiled"]?.GetValue<bool>() == true ? 0 : 1;
}

if (opts.TryGetValue("batch-dir", out var batchDir))
{
    var spill = opts.GetValueOrDefault("spill-dir") ?? throw new ArgumentException("--spill-dir is required");
    var report = opts.GetValueOrDefault("report") ?? Path.Combine(spill, "batch-report.json");
    Directory.CreateDirectory(spill);
    foreach (var old in Directory.EnumerateFiles(spill, "Prog_*.java")) File.Delete(old);
    var rows = new List<JsonObject>();
    foreach (var path in CobolFiles.Programs(batchDir!))
    {
        JsonObject row;
        try
        {
            var ir = IrExporter.Export(CobolModelBuilder.BuildGroundedFull(path));
            var r = await Transpile(http, ir, spill, true);
            row = new JsonObject
            {
                ["program"] = ir.Program.Id, ["path"] = path, ["verdict"] = r?["verdict"]?.GetValue<string>(),
                ["compiled"] = r?["compiled"]?.GetValue<bool>() ?? false,
                ["lowered"] = r?["loweredStatements"]?.GetValue<int>() ?? 0, ["abstained"] = r?["abstainedStatements"]?.GetValue<int>() ?? 0,
                ["frontiers"] = ir.Frontiers.Count, ["error"] = r?["error"]?.GetValue<string>()
            };
        }
        catch (Exception e) { row = new JsonObject { ["program"] = Path.GetFileNameWithoutExtension(path), ["path"] = path, ["verdict"] = "ERROR", ["error"] = e.Message }; }
        Console.WriteLine($"{row["program"],-10} {row["verdict"],-12} lowered={row["lowered"]} abstained={row["abstained"]} {row["error"]}");
        rows.Add(row);
    }
    File.WriteAllText(report, new JsonArray(rows.ToArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    var csv = new StringBuilder("program,verdict,compiled,lowered,abstained,frontiers\n");
    foreach (var r in rows) csv.AppendLine($"{r["program"]},{r["verdict"]},{r["compiled"]},{r["lowered"]},{r["abstained"]},{r["frontiers"]}");
    File.WriteAllText(Path.ChangeExtension(report, ".csv"), csv.ToString());
    return 0;
}

Console.Error.WriteLine("nothing to do; see --help");
return 2;

static async Task<JsonNode?> Transpile(HttpClient http, IrRoot ir, string? spillDir, bool transpileOnly)
{
    var request = new JsonObject
    {
        ["battery"] = new JsonArray(new JsonObject { ["inputs"] = new JsonObject(), ["expect"] = new JsonObject() }),
        ["useLir"] = false,
        ["loopPolicy"] = "TERMINATE",
        ["includeJavaSource"] = spillDir is null,
        ["transpileOnly"] = transpileOnly || spillDir is not null,
    };
    if (spillDir is not null) request["javaSourceDir"] = Path.GetFullPath(spillDir);
    var content = new StringContent(IrExporter.TranspileRequest(ir, request), Encoding.UTF8, "application/json");
    var resp = await http.PostAsync("/transpile-verify", content);
    var body = await resp.Content.ReadAsStringAsync();
    return JsonNode.Parse(body);
}

static string Summary(JsonNode? r) =>
    r is null ? "no response" :
    $"{r["programId"]}: verdict={r["verdict"]} compiled={r["compiled"]} lowered={r["loweredStatements"]} abstained={r["abstainedStatements"]} " +
    $"{(r["javaSourcePath"] is { } p ? "→ " + p : "")}{(r["error"] is { } e ? " error=" + e : "")}" +
    (r["diagnostics"] is JsonArray d && d.Count > 0 ? "\n  " + string.Join("\n  ", d.Take(20)) : "");

static Dictionary<string, string?> ParseArgs(string[] a)
{
    var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < a.Length; i++)
    {
        if (!a[i].StartsWith("--")) continue;
        var key = a[i][2..];
        d[key] = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : null;
    }
    return d;
}
