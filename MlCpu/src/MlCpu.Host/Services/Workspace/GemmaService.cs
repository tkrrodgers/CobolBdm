using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MlCpu.Host.Services.Workspace;

public sealed record GemmaCapability(string ProgramId, string Kind, string Package, string EntryClass, IReadOnlyList<string> Classes, string? Job);

public sealed record GemmaProgramStatus(string ProgramId, bool Designed, bool DesignValid, JsonObject? Design, int DesignAttempts,
    bool Implemented, bool Compiled, int TestsPassed, int TestsTotal, int Attempts, int BestAttempt, double? Minutes, bool AstOutline);

public sealed record GemmaOptions(int DesignAttempts = 3, int ImplementationAttempts = 15, bool AstOutline = true, bool WorkedExamples = true);

/// <summary>A folder or file of Gemma's output; Path is relative to gemma/ with '/' separators, Children is null for a file.</summary>
public sealed record GemmaCodeNode(string Name, string Path, IReadOnlyList<GemmaCodeNode>? Children)
{
    public bool IsDir => Children is not null;
}

/// <summary>
/// Gemma 4 on the published estate (MlCpu.DesignModel, llama.cpp): design the selected programs from their requirements
/// packets (+ AST outline), implement each design on the platform layer, judged by its equivalence test (compiler errors,
/// failing scenarios and every differing byte go back to the model), assemble the model's code into a copy of the published
/// project (gemma/service/), and test that service: every test, then the jobs with --mlcpu.implementation=normalized
/// against the GnuCOBOL oracle.
/// </summary>
public sealed class GemmaService(ProjectService projects, JobParityService parity, ConfigService config)
{
    public string Dir(ProjectDefinition p) => projects.Paths(p).Gemma;

    public bool Published(ProjectDefinition p) => p.Publish.Last is { } last && Directory.Exists(last.Path)
                                                  && File.Exists(Path.Combine(last.Path, "capability-manifest.json"));

    public IReadOnlyList<GemmaCapability> Capabilities(ProjectDefinition p)
    {
        if (!Published(p)) return [];
        var m = JsonNode.Parse(File.ReadAllText(Path.Combine(p.Publish.Last!.Path, "capability-manifest.json")))!;
        return m["capabilities"]!.AsArray().OfType<JsonObject>()
            .Where(c => File.Exists(Path.Combine(p.Publish.Last.Path, "normalization", c["programId"]!.GetValue<string>(), "requirements.json")))
            .Select(c => new GemmaCapability(c["programId"]!.GetValue<string>(), c["kind"]?.GetValue<string>() ?? "step", c["package"]!.GetValue<string>(),
                c["step"]!.GetValue<string>(), c["classes"]!.AsArray().Select(x => x!.GetValue<string>()).ToList(), c["job"]?.GetValue<string>()))
            .ToList();
    }

    public GemmaProgramStatus Status(ProjectDefinition p, string pid)
    {
        var dir = Path.Combine(Dir(p), pid);
        JsonObject? Read(string f) => File.Exists(Path.Combine(dir, f)) ? JsonNode.Parse(File.ReadAllText(Path.Combine(dir, f))) as JsonObject : null;
        var design = Read("design.json");
        var designRun = Read("design.run.json");
        var impl = Read("implementation.run.json");
        var result = impl?["result"];
        var guidance = impl?["guidance"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];
        return new GemmaProgramStatus(pid, design is not null, designRun?["valid"]?.GetValue<bool>() == true, design,
            designRun?["attempts"]?.AsArray().Count ?? 0, impl is not null, result?["compiled"]?.GetValue<bool>() == true,
            result?["testsPassed"]?.GetValue<int>() ?? 0, result?["testsTotal"]?.GetValue<int>() ?? 0, result?["attemptsUsed"]?.GetValue<int>() ?? 0,
            result?["bestAttempt"]?.GetValue<int>() ?? 0, Minutes(dir), guidance.Contains("ast-outline.txt"));
    }

    static double? Minutes(string dir)
    {
        var f = Path.Combine(dir, "minutes.txt");
        return File.Exists(f) && double.TryParse(File.ReadAllText(f), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : null;
    }

    /// <summary>Whether Gemma has written code for any of the project's programs (gemma/&lt;PID&gt;/java).</summary>
    public bool HasCode(ProjectDefinition p) => Capabilities(p).Any(c => Directory.Exists(Path.Combine(Dir(p), c.ProgramId, "java")));

    static readonly StringComparer NaturalOrder = StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.CompareOptions.NumericOrdering | System.Globalization.CompareOptions.IgnoreCase);

    /// <summary>
    /// Gemma's output per program, in manifest order (gemma/&lt;PID&gt;/: its classes under java/, the design, the prompt and every
    /// attempt with its reasoning), arranged as VS Code's explorer shows it: folders first, names in natural order (attempt2 before
    /// attempt10), and folder chains without files compacted into one node (java/com/mlcpu/capabilities/interestcalculation).
    /// </summary>
    public IReadOnlyList<GemmaCodeNode> CodeTree(ProjectDefinition p)
    {
        var root = Dir(p);
        return Capabilities(p).Where(c => Directory.Exists(Path.Combine(root, c.ProgramId)))
            .Select(c => CodeNode(root, Path.Combine(root, c.ProgramId), c.ProgramId)).ToList();
    }

    static GemmaCodeNode CodeNode(string root, string dir, string name)
    {
        var subdirs = Directory.GetDirectories(dir);
        while (subdirs.Length == 1 && Directory.GetFiles(dir).Length == 0)
        {
            dir = subdirs[0];
            name += "/" + Path.GetFileName(dir);
            subdirs = Directory.GetDirectories(dir);
        }
        string Rel(string f) => Path.GetRelativePath(root, f).Replace('\\', '/');
        var children = subdirs.Order(NaturalOrder).Select(d => CodeNode(root, d, Path.GetFileName(d)))
            .Concat(Directory.GetFiles(dir).Order(NaturalOrder).Select(f => new GemmaCodeNode(Path.GetFileName(f), Rel(f), null)))
            .ToList();
        return new GemmaCodeNode(name, Rel(dir), children);
    }

    /// <summary>A file of Gemma's output by its tree path; null when it is missing or lies outside gemma/.</summary>
    public string? ReadCode(ProjectDefinition p, string path)
    {
        var root = Path.GetFullPath(Dir(p)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, path));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? File.ReadAllText(full) : null;
    }

    string Dll => Path.Combine(config.Settings.RepoRoot, "MlCpu", "src", "MlCpu.DesignModel", "bin", "Release", "net10.0", "MlCpu.DesignModel.dll");

    async Task EnsureBuiltAsync(Operation op, CancellationToken ct)
    {
        var r = await Toolchain.RunAsync("dotnet", ["build", Path.Combine(config.Settings.RepoRoot, "MlCpu", "src", "MlCpu.DesignModel"), "-c", "Release", "-v", "q", "-nologo"],
            config.Settings.RepoRoot, ct: ct);
        if (r.ExitCode != 0 || !File.Exists(Dll)) throw new InvalidOperationException("MlCpu.DesignModel does not build: " + r.Output.Trim());
    }

    /// <summary>
    /// Model arguments. When Gemma's multi-token-prediction draft model sits next to it (…\MTP\mtp-*.gguf), llama-server decodes
    /// speculatively with it (about 3x the tokens per second, same output distribution).
    /// </summary>
    List<string> ModelArgs()
    {
        var args = new List<string> { "--model", config.Settings.GemmaModel, "--name", config.Settings.GemmaName, "--llama-dir", config.Settings.LlamaDir, "--backend", "llama" };
        var root = Path.GetDirectoryName(Path.GetDirectoryName(config.Settings.GemmaModel));
        var mtp = root is null || !Directory.Exists(Path.Combine(root, "MTP")) ? null
            : Directory.EnumerateFiles(Path.Combine(root, "MTP"), "mtp-*.gguf").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
        if (mtp is not null) args.AddRange(["--server-args", $"--spec-type draft-mtp --spec-draft-model {mtp}"]);
        return args;
    }

    void CheckModel()
    {
        if (!File.Exists(config.Settings.GemmaModel)) throw new FileNotFoundException($"Gemma model not found at {config.Settings.GemmaModel} (Settings → Gemma model)");
        if (!File.Exists(Path.Combine(config.Settings.LlamaDir, "llama-server.exe"))) throw new FileNotFoundException($"llama-server.exe not found in {config.Settings.LlamaDir}");
    }

    /// <summary>Gemma designs each selected program (one model load).</summary>
    public async Task DesignAsync(string projectId, IReadOnlyList<string> programs, GemmaOptions options, Operation op, CancellationToken ct)
    {
        var p = projects.Get(projectId) ?? throw new ArgumentException($"no project {projectId}");
        if (!Published(p)) throw new InvalidOperationException("publish the estate first (Conversion → Publish)");
        CheckModel();
        op.Report(null, "building MlCpu.DesignModel");
        await EnsureBuiltAsync(op, ct);
        var publish = p.Publish.Last!.Path;
        List<string> args = [Dll, "design-all", .. ModelArgs(), "--programs", string.Join(",", programs), "--normalization", Path.Combine(publish, "normalization"),
            "--out", Dir(p), "--attempts", options.DesignAttempts.ToString(), "--ctx", "131072"];
        if (options.AstOutline) args.Add("--outline");
        await RunModelAsync(args, programs, "design", op, ct, expectedTokens: 7000);
        projects.Update(p.Id, x => x.Gemma.Designed = programs.Where(pid => Status(x, pid).DesignValid).ToList());
    }

    /// <summary>Gemma implements each designed program until its equivalence test passes (or the attempts run out), then the service is assembled.</summary>
    public async Task PublishToServiceAsync(string projectId, IReadOnlyList<string> programs, GemmaOptions options, Operation op, CancellationToken ct)
    {
        var p = projects.Get(projectId) ?? throw new ArgumentException($"no project {projectId}");
        if (!Published(p)) throw new InvalidOperationException("publish the estate first (Conversion → Publish)");
        var designed = programs.Where(pid => Status(p, pid).DesignValid).ToList();
        if (designed.Count == 0) throw new InvalidOperationException("no valid Gemma design among the selected programs (run Design first)");
        CheckModel();
        op.Report(null, "building MlCpu.DesignModel");
        await EnsureBuiltAsync(op, ct);
        var publish = p.Publish.Last!.Path;
        List<string> args = [Dll, "implement-all", .. ModelArgs(), "--programs", string.Join(",", designed), "--normalization", Path.Combine(publish, "normalization"),
            "--manifest", Path.Combine(publish, "capability-manifest.json"), "--project", publish, "--out", Dir(p),
            "--harness-root", Path.Combine(Dir(p), "harness"), "--impl-attempts", options.ImplementationAttempts.ToString(), "--ctx", "131072"];
        if (options.WorkedExamples) args.Add("--with-examples");
        if (options.AstOutline) args.Add("--outline");
        await RunModelAsync(args, designed, "implement", op, ct, expectedTokens: 0, attempts: options.ImplementationAttempts, until: 0.9);
        op.Report(0.92, "assembling the service with Gemma's code");
        var service = AssembleService(p, designed, op);
        projects.Update(p.Id, x => x.Gemma.ServicePath = service);
    }

    /// <summary>A copy of the published project in which each designed program's authored or placeholder classes are replaced by Gemma's code.</summary>
    string AssembleService(ProjectDefinition p, IReadOnlyList<string> programs, Operation op)
    {
        var publish = p.Publish.Last!.Path;
        var target = Path.Combine(Dir(p), "service", Path.GetFileName(publish));
        if (Directory.Exists(target)) Toolchain.DeleteTree(target);
        Toolchain.CopyTree(publish, target);
        // Every capability with Gemma code is considered (not only this run's programs), so earlier passing programs stay in the service.
        foreach (var cap in Capabilities(p).Where(c => programs.Contains(c.ProgramId) || Directory.Exists(Path.Combine(Dir(p), c.ProgramId, "java"))))
        {
            var pkg = cap.Package.Replace('.', Path.DirectorySeparatorChar);
            var src = Path.Combine(Dir(p), cap.ProgramId, "java", pkg);
            if (!Directory.Exists(src)) { op.Info($"{cap.ProgramId}: no Gemma code; the published classes stay"); continue; }
            // Only code that passed its acceptance test goes into the service; a failing program keeps the published classes.
            var status = Status(p, cap.ProgramId);
            if (!(status.Compiled && status.TestsTotal > 0 && status.TestsPassed == status.TestsTotal))
            {
                op.Info($"{cap.ProgramId}: Gemma's best attempt passes {status.TestsPassed}/{status.TestsTotal}; the published classes stay in the service");
                continue;
            }
            var dst = Path.Combine(target, "src", "main", "java", pkg);
            foreach (var c in cap.Classes) File.Delete(Path.Combine(dst, c + ".java"));
            var files = Directory.EnumerateFiles(src, "*.java").ToList();
            foreach (var f in files) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            var st = Status(p, cap.ProgramId);
            op.Info($"{cap.ProgramId}: {files.Count} Gemma file(s) -> {cap.Package} (acceptance {st.TestsPassed}/{st.TestsTotal})");
        }
        return target;
    }

    public async Task<BuildTestReport> TestAsync(string projectId, Operation op, CancellationToken ct)
    {
        var p = projects.Get(projectId) ?? throw new ArgumentException($"no project {projectId}");
        var service = p.Gemma.ServicePath ?? throw new InvalidOperationException("publish Gemma's design to the service first");
        var report = await parity.BuildAndTestAsync(p, service, "normalized", "gemma", op, 0, 1, ct);
        projects.Update(p.Id, x => x.Gemma.LastTest = new TestRecord
        {
            At = report.At, Tests = report.Tests, Failed = report.Failed, Outputs = report.Parity.Count, Match = report.Match, Implementation = "normalized"
        });
        return report;
    }

    /// <summary>Runs the DesignModel CLI and turns its log into progress: program k of n, then tokens streamed or implementation attempts.</summary>
    async Task RunModelAsync(List<string> args, IReadOnlyList<string> programs, string phase, Operation op, CancellationToken ct,
        int expectedTokens, int attempts = 1, double until = 1)
    {
        int index = -1, attempt = 0;
        double inner = 0;
        var started = DateTime.Now;
        string? current = null;
        void Progress(string status) => op.Report(until * (Math.Max(0, index) + Math.Min(0.98, inner)) / Math.Max(1, programs.Count), status);
        ProcessResult r;
        try
        {
        r = await Toolchain.RunAsync("dotnet", args, config.Settings.RepoRoot, line =>
        {
            var text = Regex.Replace(line, @"^\d\d:\d\d:\d\d ", "");
            op.Info(text);
            Match m;
            if ((m = Regex.Match(text, @"^===== .+?: (\S+) \((\d+)/(\d+)\)")).Success)
            {
                if (current is not null) WriteMinutes(current, started);
                current = m.Groups[1].Value;
                started = DateTime.Now;
                index = int.Parse(m.Groups[2].Value) - 1;
                inner = 0;
                attempt = 0;
                Progress($"{phase} {current} ({index + 1}/{programs.Count})");
            }
            else if ((m = Regex.Match(text, @"~(\d+) tokens streamed")).Success)
            {
                var tokens = int.Parse(m.Groups[1].Value);
                inner = expectedTokens > 0
                    ? Math.Max(inner, tokens / (double)expectedTokens)
                    : Math.Max(inner, (attempt + Math.Min(0.9, tokens / 8000.0)) / Math.Max(1, attempts));
                Progress($"{phase} {current}: {(expectedTokens > 0 ? "" : $"attempt {attempt + 1}, ")}~{tokens} tokens");
            }
            else if ((m = Regex.Match(text, @"^implement attempt (\d+)")).Success)
            {
                attempt = int.Parse(m.Groups[1].Value);
                inner = attempt / (double)Math.Max(1, attempts);
                Progress($"{phase} {current}: attempt {attempt} compiled and testing");
            }
            else if ((m = Regex.Match(text, @"acceptance (\d+)/(\d+)")).Success)
                Progress($"{phase} {current}: acceptance {m.Groups[1].Value}/{m.Groups[2].Value}");
            else if (text.Contains("VALID design")) { inner = 1; Progress($"{phase} {current}: {text.Trim()}"); }
        }, ct: ct);
        }
        finally
        {
            // The CLI starts llama-server itself; a cancelled or crashed run must not leave the model resident on the GPU.
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName("llama-server"))
                try
                {
                    if (proc.StartTime < started.AddSeconds(-5)) continue; // not ours
                    proc.Kill(entireProcessTree: true);
                    op.Info($"stopped llama-server (pid {proc.Id})");
                }
                catch (Exception) { }
        }
        if (current is not null) WriteMinutes(current, started);
        if (r.ExitCode is not (0 or 1)) throw new InvalidOperationException($"MlCpu.DesignModel {phase} exited {r.ExitCode}");

        void WriteMinutes(string pid, DateTime from)
        {
            var p = projects.Projects.FirstOrDefault(x => args.Contains(Path.Combine(projects.Paths(x).Gemma)) || args.Contains(Dir(x)));
            if (p is null) return;
            var dir = Path.Combine(Dir(p), pid);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, phase == "design" ? "design.minutes.txt" : "minutes.txt"),
                Math.Round((DateTime.Now - from).TotalMinutes, 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
