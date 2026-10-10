using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MlCpu.Cobol.Antlr;

namespace MlCpu.Host.Services.Workspace;

public sealed record TranspileRow(string Program, string Path, string Kind, string Verdict, bool Compiled, int Lowered, int Abstained,
    string? ClassName, string? Error, List<string> Frontiers, double Seconds);

public sealed record PublishResult(string ProjectRoot, int Programs, int Capabilities, IReadOnlyList<string> Jobs, IReadOnlyList<KitCapability> Kit);

/// <summary>
/// The Conversion page's actions for a project: transpile every program (COBOL → IR → Java through the sidecar, kept under
/// transpile/ and compiled with javac), publish the normalized Spring estate with the capabilities layer and the model's
/// kit, and build &amp; test it against the GnuCOBOL oracle.
/// </summary>
public sealed class ConversionService(ProjectService projects, JavaSidecarService sidecar, JavaProjectAssembler assembler,
    CapabilityKitBuilder kits, GraphService graphs, JobParityService parity, ConfigService config)
{
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, JavaTranspileResult>> _results = new();
    static readonly JsonSerializerOptions Json = new(ProjectJson.Options);

    public IReadOnlyList<TranspileRow> Report(ProjectDefinition p)
    {
        var f = Path.Combine(projects.Paths(p).Transpile, "report.json");
        try { return File.Exists(f) ? JsonSerializer.Deserialize<List<TranspileRow>>(File.ReadAllText(f), Json) ?? [] : []; }
        catch (JsonException) { return []; }
    }

    public string? JavaSource(ProjectDefinition p, TranspileRow row)
    {
        if (row.ClassName is null) return null;
        var f = Path.Combine(projects.Paths(p).Transpile, "java", SimpleName(row.ClassName) + ".java");
        return File.Exists(f) ? File.ReadAllText(f) : null;
    }

    static string SimpleName(string className) => className[(className.LastIndexOf('.') + 1)..];

    public async Task TranspileAllAsync(string projectId, Operation op, CancellationToken ct, double from = 0, double to = 1)
    {
        var p = projects.Get(projectId) ?? throw new ArgumentException($"no project {projectId}");
        if (await sidecar.HealthAsync(ct) is null) throw new InvalidOperationException($"the Java sidecar is not reachable at {config.Settings.SidecarUrl} (run tools\\Start-Sidecar.ps1)");
        var paths = projects.Paths(p);
        var javaDir = Path.Combine(paths.Transpile, "java");
        if (Directory.Exists(javaDir)) Toolchain.DeleteTree(javaDir);
        Directory.CreateDirectory(javaDir);
        var sources = OnboardingService.Sources(p, projects.SourceDir(p));
        var map = _results.GetOrAdd(p.Id, _ => new());
        map.Clear();
        var rows = new List<TranspileRow>();
        int i = 0;
        foreach (var prog in p.Programs)
        {
            ct.ThrowIfCancellationRequested();
            op.Report(from + (to - from) * 0.85 * i++ / Math.Max(1, p.Programs.Count), $"transpiling {prog.Id} ({i}/{p.Programs.Count})");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = await sidecar.TranspileAsync(sources[prog.Id], transpileOnly: true, ct);
            map[prog.Id] = r;
            if (r.JavaSource is not null && r.ClassName is not null) await File.WriteAllTextAsync(Path.Combine(javaDir, SimpleName(r.ClassName) + ".java"), r.JavaSource, ct);
            rows.Add(new TranspileRow(prog.Id, prog.Path, prog.Kind, r.Verdict, r.Compiled, r.Lowered, r.Abstained, r.ClassName, r.Error, r.AbstainedSteps, Math.Round(sw.Elapsed.TotalSeconds, 2)));
            op.Info($"{prog.Id,-9} {r.Verdict,-11} lowered {r.Lowered}, abstained {r.Abstained}{(r.Error is null ? "" : " — " + r.Error)}");
        }
        await File.WriteAllTextAsync(Path.Combine(paths.Transpile, "report.json"), JsonSerializer.Serialize(rows, Json) + "\n", ct);
        op.Report(from + (to - from) * 0.9, "javac (translation + runtime)");
        await CompileAsync(p, op, ct);
        op.Info($"transpiled {rows.Count(r => r.Compiled)}/{rows.Count} programs; {rows.Sum(r => r.Abstained)} statement(s) abstained (frontiers)");
    }

    /// <summary>javac over the translation (com.mlcpu.gen.Prog_*) against the runtime: the classes call traces and quick checks run.</summary>
    async Task CompileAsync(ProjectDefinition p, Operation op, CancellationToken ct)
    {
        var paths = projects.Paths(p);
        var java = Directory.EnumerateFiles(Path.Combine(paths.Transpile, "java"), "*.java").ToList();
        var classes = Path.Combine(paths.Transpile, "classes");
        if (Directory.Exists(classes)) Toolchain.DeleteTree(classes);
        Directory.CreateDirectory(classes);
        if (java.Count == 0) return;
        var argFile = Path.Combine(paths.Transpile, "javac.args");
        await File.WriteAllLinesAsync(argFile, java.Select(j => "\"" + j.Replace("\\", "/") + "\""), ct);
        var rt = Path.Combine(config.Settings.RepoRoot, "java-sidecar", "build", "classes", "java", "main");
        var r = await Toolchain.RunAsync(Toolchain.Javac, ["-nowarn", "-encoding", "UTF-8", "-d", classes, "-cp", rt, "@" + argFile], paths.Transpile, ct: ct);
        op.Info(r.ExitCode == 0 ? $"javac: {java.Count} class(es) compiled" : "javac failed: " + string.Join(" | ", r.Output.Split('\n').Take(6)));
    }

    async Task<Dictionary<string, JavaTranspileResult>> ResultsAsync(ProjectDefinition p, IEnumerable<string> programs, Operation op, CancellationToken ct)
    {
        var map = _results.GetOrAdd(p.Id, _ => new());
        var sources = OnboardingService.Sources(p, projects.SourceDir(p));
        var outp = new Dictionary<string, JavaTranspileResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var pid in programs)
        {
            if (!map.TryGetValue(pid, out var r) || r.Model is null)
            {
                op.Info($"transpiling {pid}");
                r = await sidecar.TranspileAsync(sources[pid], transpileOnly: true, ct);
                map[pid] = r;
            }
            outp[pid] = r;
        }
        return outp;
    }

    public string DefaultPublishDir(ProjectDefinition p) => projects.Paths(p).Publish;

    public async Task<PublishResult> PublishAsync(string projectId, string targetDir, Operation op, CancellationToken ct)
    {
        var p = projects.Get(projectId) ?? throw new ArgumentException($"no project {projectId}");
        if (p.Oracle.JobChecks.Count == 0) throw new InvalidOperationException("the GnuCOBOL oracle has not run for this project (Settings → Build & run oracle)");
        var src = projects.SourceDir(p);
        var sources = OnboardingService.Sources(p, src);
        var jobs = OnboardingService.Harvest(p, src);
        var scopes = p.Publish.Jobs.Select(j => jobs.FirstOrDefault(x => x.Name.Equals(j, StringComparison.OrdinalIgnoreCase)))
            .OfType<MlCpu.CobolKb.Knowledge.JclJob>().Select(j => EstateService.Resolve(j, sources)).ToList();
        if (scopes.Count == 0) throw new InvalidOperationException("no publish job found in the harvested JCL");
        var programs = scopes.SelectMany(s => s.Entries.Concat(s.Called)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList();

        op.Report(0.05, "graphs (AST, call graph, DDG)");
        if (graphs.BuiltAt(p) is null) await graphs.RebuildAsync(p.Id, op, ct, 0.05, 0.25);

        op.Report(0.25, $"transpile {programs.Count} program(s)");
        var results = await ResultsAsync(p, programs, op, ct);
        if (!Directory.Exists(Path.Combine(projects.Paths(p).Transpile, "classes"))) await CompileAsync(p, op, ct);

        IReadOnlyList<KitCapability> kit = [];
        string capabilitiesRoot;
        if (!string.IsNullOrWhiteSpace(p.Publish.CapabilitiesRoot))
        {
            capabilitiesRoot = Path.GetFullPath(Path.IsPathRooted(p.Publish.CapabilitiesRoot) ? p.Publish.CapabilitiesRoot : Path.Combine(config.Settings.RepoRoot, p.Publish.CapabilitiesRoot));
            op.Info($"capabilities: authored ({capabilitiesRoot})");
        }
        else
        {
            op.Report(0.4, "capabilities kit (placeholders, equivalence tests, oracle resources, worked examples)");
            kit = await kits.BuildAsync(p, op, ct);
            capabilitiesRoot = projects.Paths(p).Capabilities;
        }

        op.Report(0.6, "assembling the Spring estate");
        var runtime = await sidecar.RuntimeAsync(ct);
        var ctl = OnboardingService.Roots(src, p.Layout.ControlCardRoots).FirstOrDefault();
        var options = new AssemblyOptions(ctl, sources, DomainPartition: false, CapabilitiesRoot: capabilitiesRoot,
            RegressionNote: p.Oracle.Provider == "carddemo-script" ? null
                : "the published project's job checks: the generated JobRunner with --mlcpu.implementation=normalized against the GnuCOBOL oracle (Build & test)");
        var project = assembler.Assemble(p.Id + "-normalized", programs.Select(x => results[x]).ToList(), runtime, scopes, options);
        project = project with { Files = [.. project.Files, .. KitFiles(p, programs, project.Files)] };

        op.Report(0.85, "writing the project");
        Directory.CreateDirectory(targetDir);
        var root = await assembler.PublishAsync(project, targetDir, ct);
        var capCount = project.Files.Count(f => f.RelPath.StartsWith("normalization/", StringComparison.Ordinal) && f.RelPath.EndsWith("/requirements.json", StringComparison.Ordinal));
        projects.Update(p.Id, x => x.Publish.Last = new PublishRecord { Path = root, At = DateTime.Now.ToString("s"), Programs = programs.Count, Capabilities = capCount });
        op.Info($"published {project.Name}: {programs.Count} program(s), jobs {string.Join(", ", scopes.Select(s => s.Name))} -> {root}");
        return new PublishResult(root, programs.Count, capCount, scopes.Select(s => s.Name).ToList(), kit);
    }

    /// <summary>The graphs and the model's guide inside the published project.</summary>
    List<JavaProjectFile> KitFiles(ProjectDefinition p, IReadOnlyList<string> programs, IReadOnlyList<JavaProjectFile> existing)
    {
        var files = new List<JavaProjectFile>();
        var have = existing.Select(f => f.RelPath).ToHashSet(StringComparer.Ordinal);
        var dir = graphs.Dir(p);
        void Add(string rel, string full)
        {
            if (File.Exists(full) && !have.Contains(rel)) files.Add(new JavaProjectFile(rel, File.ReadAllText(full), "text"));
        }
        Add("graphs/callgraph.json", Path.Combine(dir, "callgraph.json"));
        Add("graphs/index.json", Path.Combine(dir, "index.json"));
        foreach (var pid in programs)
        {
            Add($"graphs/ast/{pid}.ast.json", Path.Combine(dir, "ast", pid + ".ast.json"));
            Add($"graphs/ddg/{pid}.ddg.json", Path.Combine(dir, "ddg", pid + ".ddg.json"));
            if (existing.Any(f => f.RelPath == $"normalization/{pid}/requirements.json"))
                Add($"normalization/{pid}/ast-outline.txt", Path.Combine(dir, "ast", pid + ".outline.txt"));
        }
        var caps = existing.Where(f => f.RelPath.StartsWith("normalization/") && f.RelPath.EndsWith("/requirements.json"))
            .Select(f => f.RelPath.Split('/')[1]).ToList();
        var sb = new StringBuilder($"# {p.Name}: modernization kit\n\n");
        sb.Append("Everything a model needs to re-express each program as idiomatic Java and prove it byte for byte against the original COBOL.\n\n");
        sb.Append("| Path | What it is |\n|---|---|\n");
        sb.Append("| `capability-manifest.json` | the programs to modernize: kind (step / subprogram), package, entry class, acceptance test |\n");
        sb.Append("| `normalization/<PID>/requirements.json` | requirements packet extracted from the COBOL (contracts, ports, rules, quirks, outputs), hash-pinned |\n");
        sb.Append("| `normalization/design.schema.json` | the design a model returns (mlcpu.capability-design.v1) |\n");
        sb.Append("| `normalization/<PID>/ast-outline.txt` | COBOL-85 AST outline (grammars-v4 Cobol85.g4 via ANTLR): data division with VALUE clauses, procedure logic nested |\n");
        sb.Append("| `normalization/<PID>/examples.md` | worked examples from the GnuCOBOL oracle, decoded field by field |\n");
        sb.Append("| `normalization/parity-notes.md` | COBOL storage and I/O rules that decide byte parity |\n");
        sb.Append("| `src/main/java/<platform>` | the platform layer the capability is written on |\n");
        sb.Append("| `src/test/java/**/*EquivalenceTest.java` | acceptance: normalized vs faithful in every scenario, and vs the GnuCOBOL oracle |\n");
        sb.Append("| `graphs/` | call graph, AST and data dependence graph per program (JSON) |\n\n");
        sb.Append($"Capabilities: {string.Join(", ", caps)}. Oracle clock: {p.Oracle.Clock}.\n");
        files.Add(new JavaProjectFile("MODERNIZATION.md", sb.ToString(), "text"));
        return files;
    }

    public async Task<BuildTestReport> BuildAndTestAsync(string projectId, Operation op, CancellationToken ct)
    {
        var p = projects.Get(projectId) ?? throw new ArgumentException($"no project {projectId}");
        var root = p.Publish.Last?.Path ?? throw new InvalidOperationException("nothing published yet");
        var report = await parity.BuildAndTestAsync(p, root, "legacy", "publish", op, 0, 1, ct);
        projects.Update(p.Id, x =>
        {
            if (x.Publish.Last is not null)
                x.Publish.Last.LastTest = new TestRecord { At = report.At, Tests = report.Tests, Failed = report.Failed, Outputs = report.Parity.Count, Match = report.Match, Implementation = "legacy" };
        });
        return report;
    }
}
