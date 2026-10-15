using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MlCpu.Host.Services.Workspace;

public sealed record GemmaCapability(string ProgramId, string Kind, string Package, string EntryClass, IReadOnlyList<string> Classes, string? Job);

public sealed record GemmaProgramStatus(string ProgramId, bool Designed, bool DesignValid, JsonObject? Design, int DesignAttempts,
    bool Implemented, bool Compiled, int TestsPassed, int TestsTotal, int Attempts, int BestAttempt, double? Minutes, bool AstOutline,
    int DebugTurns = 0, double? DesignMinutes = null);

/// <summary>
/// One run's options. DebugMode: once there is code that compiles, each repair turn sends the focused debug prompt (the contract,
/// the COBOL the first divergence goes through, the current code, the passing scenarios to keep) with the ratchet, and SEARCH/REPLACE
/// edits only once at least half the scenarios pass. WarmStart: begin from the model's previous best code (PID/java).
/// </summary>
public sealed record ModelOptions(int DesignAttempts = 3, int ImplementationAttempts = 15, bool AstOutline = true, bool WorkedExamples = true,
    bool DebugMode = false, bool WarmStart = false);

/// <summary>A folder or file of a model's output; Path is relative to the model's folder with '/' separators, Children is null for a file.</summary>
public sealed record GemmaCodeNode(string Name, string Path, IReadOnlyList<GemmaCodeNode>? Children)
{
    public bool IsDir => Children is not null;
}

/// <summary>
/// A local model on the published estate (MlCpu.DesignModel, llama.cpp): design the selected programs from their requirements
/// packets (+ AST outline), implement each design on the platform layer, judged by its equivalence test (compiler errors,
/// failing scenarios and every differing byte go back to the model), assemble the model's code into a copy of the published
/// project (&lt;model&gt;/service/), and test that service: every test, then the jobs with --mlcpu.implementation=normalized
/// against the GnuCOBOL oracle. Each model is a profile of this service: its weights, llama.cpp build, sampling, reasoning
/// budget and the folder its runs go to.
/// </summary>
public abstract class ModelRunService(ProjectService projects, JobParityService parity, ConfigService config)
{
    protected ConfigService Config => config;

    /// <summary>The short id: the project folder (projects/&lt;id&gt;/&lt;Id&gt;), the operation kinds, the API route and the test label.</summary>
    public abstract string Id { get; }
    public abstract string Display { get; }
    public abstract string ModelPath { get; }
    public abstract string ModelName { get; }
    public abstract string LlamaDir { get; }
    /// <summary>Reasoning tokens a reply may spend before it writes content (MLCPU_REASONING_BUDGET).</summary>
    public abstract int ReasoningBudget { get; }
    /// <summary>The options the page starts with (the settings this model did best with).</summary>
    public abstract ModelOptions Defaults { get; }
    /// <summary>What the page says about this model's settings.</summary>
    public abstract string SettingsNote { get; }
    public abstract ModelRunState State(ProjectDefinition p);
    /// <summary>llama-server arguments beyond the model: sampling and speculative decoding.</summary>
    protected abstract string? ServerArgs();

    public string Page => Id;
    public string CodePage => Id + "-code";
    public string DesignKind => Id + "-design";
    public string PublishKind => Id + "-publish";
    public string TestKind => Id + "-test";

    public string Dir(ProjectDefinition p) => Path.Combine(projects.Paths(p).Dir, Id);

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
        // A debug turn leaves its prompt (implementation.debugN.prompt.md); the last one may be for an attempt the run did not make.
        var used = result?["attemptsUsed"]?.GetValue<int>() ?? 0;
        var debugTurns = Enumerable.Range(1, used).Count(n => File.Exists(Path.Combine(dir, $"implementation.debug{n}.prompt.md")));
        return new GemmaProgramStatus(pid, design is not null, designRun?["valid"]?.GetValue<bool>() == true, design,
            designRun?["attempts"]?.AsArray().Count ?? 0, impl is not null, result?["compiled"]?.GetValue<bool>() == true,
            result?["testsPassed"]?.GetValue<int>() ?? 0, result?["testsTotal"]?.GetValue<int>() ?? 0, result?["attemptsUsed"]?.GetValue<int>() ?? 0,
            result?["bestAttempt"]?.GetValue<int>() ?? 0, Minutes(dir, "minutes.txt") ?? SummaryMinutes(p, pid), guidance.Contains("ast-outline.txt"),
            debugTurns, Minutes(dir, "design.minutes.txt"));
    }

    static double? Minutes(string dir, string file)
    {
        var f = Path.Combine(dir, file);
        return File.Exists(f) && double.TryParse(File.ReadAllText(f), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : null;
    }

    /// <summary>A run made outside the workbench (the CLI) has no minutes.txt; its summary.json records the implementation minutes.</summary>
    double? SummaryMinutes(ProjectDefinition p, string pid)
    {
        var f = Path.Combine(Dir(p), "summary.json");
        try
        {
            return File.Exists(f) && JsonNode.Parse(File.ReadAllText(f))?["programs"] is JsonArray a
                ? a.FirstOrDefault(e => e?["programId"]?.GetValue<string>() == pid)?["minutes"]?.GetValue<double>()
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Whether the model has written code for any of the project's programs (&lt;model&gt;/&lt;PID&gt;/java).</summary>
    public bool HasCode(ProjectDefinition p) => Capabilities(p).Any(c => Directory.Exists(Path.Combine(Dir(p), c.ProgramId, "java")));

    static readonly StringComparer NaturalOrder = StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.CompareOptions.NumericOrdering | System.Globalization.CompareOptions.IgnoreCase);

    /// <summary>
    /// The model's output per program, in manifest order (&lt;model&gt;/&lt;PID&gt;/: its classes under java/, the design, the prompt and
    /// every attempt with its reasoning), arranged as VS Code's explorer shows it: folders first, names in natural order (attempt2
    /// before attempt10), and folder chains without files compacted into one node (java/com/mlcpu/capabilities/interestcalculation).
    /// </summary>
    public IReadOnlyList<GemmaCodeNode> CodeTree(ProjectDefinition p)
    {
        var root = Dir(p);
        return Capabilities(p).Where(c => Directory.Exists(Path.Combine(root, c.ProgramId)))
            .Select(c => CodeNode(root, Path.Combine(root, c.ProgramId), c.ProgramId)).ToList();
    }

    public static GemmaCodeNode CodeNode(string root, string dir, string name)
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

    /// <summary>A file of the model's output by its tree path; null when it is missing or lies outside the model's folder.</summary>
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

    /// <summary>The model, its llama.cpp build and the server arguments of its profile.</summary>
    public List<string> ModelArgs()
    {
        var args = new List<string> { "--model", ModelPath, "--name", ModelName, "--llama-dir", LlamaDir, "--backend", "llama" };
        if (ServerArgs() is { Length: > 0 } s) args.AddRange(["--server-args", s]);
        return args;
    }

    /// <summary>The first file matching pattern in &lt;model root&gt;\&lt;folder&gt; (the model root holds the weights' folder), or null.</summary>
    protected string? Beside(string folder, string pattern)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(ModelPath));
        return root is null || !Directory.Exists(Path.Combine(root, folder)) ? null
            : Directory.EnumerateFiles(Path.Combine(root, folder), pattern).OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
    }

    void CheckModel()
    {
        if (!File.Exists(ModelPath)) throw new FileNotFoundException($"{Display} model not found at {ModelPath} (Settings → {Display} model)");
        if (!File.Exists(Path.Combine(LlamaDir, "llama-server.exe"))) throw new FileNotFoundException($"llama-server.exe not found in {LlamaDir}");
    }

    /// <summary>The model designs each selected program (one model load).</summary>
    public async Task DesignAsync(string projectId, IReadOnlyList<string> programs, ModelOptions options, Operation op, CancellationToken ct)
    {
        var p = projects.Get(projectId) ?? throw new ArgumentException($"no project {projectId}");
        if (!Published(p)) throw new InvalidOperationException("publish the estate first (Conversion → Publish)");
        CheckModel();
        op.Report(null, "building MlCpu.DesignModel");
        await EnsureBuiltAsync(op, ct);
        List<string> args = [Dll, .. DesignArgs(p, programs, options)];
        await RunModelAsync(args, programs, "design", op, ct, expectedTokens: 7000);
        projects.Update(p.Id, x => State(x).Designed = programs.Where(pid => Status(x, pid).DesignValid).ToList());
    }

    /// <summary>The design-all command line (after the DLL), as the page runs it; a script can run the same.</summary>
    public List<string> DesignArgs(ProjectDefinition p, IReadOnlyList<string> programs, ModelOptions options)
    {
        var publish = p.Publish.Last!.Path;
        List<string> args = ["design-all", .. ModelArgs(), "--programs", string.Join(",", programs), "--normalization", Path.Combine(publish, "normalization"),
            "--out", Dir(p), "--attempts", options.DesignAttempts.ToString(), "--ctx", "131072"];
        if (options.AstOutline) args.Add("--outline");
        return args;
    }

    /// <summary>The implement-all command line (after the DLL), as the page runs it.</summary>
    public List<string> ImplementArgs(ProjectDefinition p, IReadOnlyList<string> programs, ModelOptions options)
    {
        var publish = p.Publish.Last!.Path;
        List<string> args = ["implement-all", .. ModelArgs(), "--programs", string.Join(",", programs), "--normalization", Path.Combine(publish, "normalization"),
            "--manifest", Path.Combine(publish, "capability-manifest.json"), "--project", publish, "--out", Dir(p),
            "--harness-root", Path.Combine(Dir(p), "harness"), "--impl-attempts", options.ImplementationAttempts.ToString(), "--ctx", "131072"];
        if (options.WorkedExamples) args.Add("--with-examples");
        if (options.AstOutline) args.Add("--outline");
        if (options.DebugMode) args.Add("--debug-prompt");
        if (options.WarmStart) args.Add("--warm-start");
        return args;
    }

    /// <summary>The model implements each designed program until its equivalence test passes (or the attempts run out), then the service is assembled.</summary>
    public async Task PublishToServiceAsync(string projectId, IReadOnlyList<string> programs, ModelOptions options, Operation op, CancellationToken ct)
    {
        var p = projects.Get(projectId) ?? throw new ArgumentException($"no project {projectId}");
        if (!Published(p)) throw new InvalidOperationException("publish the estate first (Conversion → Publish)");
        var designed = programs.Where(pid => Status(p, pid).DesignValid).ToList();
        if (designed.Count == 0) throw new InvalidOperationException($"no valid {Display} design among the selected programs (run Design first)");
        CheckModel();
        op.Report(null, "building MlCpu.DesignModel");
        await EnsureBuiltAsync(op, ct);
        List<string> args = [Dll, .. ImplementArgs(p, designed, options)];
        await RunModelAsync(args, designed, "implement", op, ct, expectedTokens: 0, attempts: options.ImplementationAttempts, until: 0.9);
        op.Report(0.92, $"assembling the service with {Display}'s code");
        Assemble(projectId, designed, op);
    }

    /// <summary>Assembles the service from the model's code already on disk (a run made by the page or by a script) and records it.</summary>
    public string Assemble(string projectId, IReadOnlyList<string> programs, Operation op)
    {
        var p = projects.Get(projectId) ?? throw new ArgumentException($"no project {projectId}");
        var service = AssembleService(p, programs, op);
        projects.Update(p.Id, x =>
        {
            State(x).ServicePath = service;
            State(x).Designed = Capabilities(x).Select(c => c.ProgramId).Where(pid => Status(x, pid).DesignValid).ToList();
        });
        return service;
    }

    /// <summary>A copy of the published project in which each designed program's authored or placeholder classes are replaced by the model's code.</summary>
    string AssembleService(ProjectDefinition p, IReadOnlyList<string> programs, Operation op)
    {
        var publish = p.Publish.Last!.Path;
        var target = Path.Combine(Dir(p), "service", Path.GetFileName(publish));
        if (Directory.Exists(target)) Toolchain.DeleteTree(target);
        Toolchain.CopyTree(publish, target);
        // Every capability with the model's code is considered (not only this run's programs), so earlier passing programs stay in the service.
        foreach (var cap in Capabilities(p).Where(c => programs.Contains(c.ProgramId) || Directory.Exists(Path.Combine(Dir(p), c.ProgramId, "java"))))
        {
            var pkg = cap.Package.Replace('.', Path.DirectorySeparatorChar);
            var src = Path.Combine(Dir(p), cap.ProgramId, "java", pkg);
            if (!Directory.Exists(src)) { op.Info($"{cap.ProgramId}: no {Display} code; the published classes stay"); continue; }
            // Only code that passed its acceptance test goes into the service; a failing program keeps the published classes.
            var status = Status(p, cap.ProgramId);
            if (!(status.Compiled && status.TestsTotal > 0 && status.TestsPassed == status.TestsTotal))
            {
                op.Info($"{cap.ProgramId}: {Display}'s best attempt passes {status.TestsPassed}/{status.TestsTotal}; the published classes stay in the service");
                continue;
            }
            var dst = Path.Combine(target, "src", "main", "java", pkg);
            foreach (var c in cap.Classes) File.Delete(Path.Combine(dst, c + ".java"));
            var files = Directory.EnumerateFiles(src, "*.java").ToList();
            foreach (var f in files) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            op.Info($"{cap.ProgramId}: {files.Count} {Display} file(s) -> {cap.Package} (acceptance {status.TestsPassed}/{status.TestsTotal})");
        }
        return target;
    }

    public async Task<BuildTestReport> TestAsync(string projectId, Operation op, CancellationToken ct)
    {
        var p = projects.Get(projectId) ?? throw new ArgumentException($"no project {projectId}");
        var service = State(p).ServicePath ?? throw new InvalidOperationException($"publish {Display}'s design to the service first");
        var report = await parity.BuildAndTestAsync(p, service, "normalized", Id, op, 0, 1, ct);
        projects.Update(p.Id, x => State(x).LastTest = new TestRecord
        {
            At = report.At, Tests = report.Tests, Failed = report.Failed, Outputs = report.Parity.Count, Match = report.Match,
            ReferenceOutputs = report.ReferenceResults.Count, ReferenceMatch = report.ReferenceMatch, Implementation = "normalized"
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
        }, new Dictionary<string, string> { ["MLCPU_REASONING_BUDGET"] = ReasoningBudget.ToString() }, ct: ct);
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
            var p = projects.Projects.FirstOrDefault(x => args.Contains(Dir(x)));
            if (p is null) return;
            var dir = Path.Combine(Dir(p), pid);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, phase == "design" ? "design.minutes.txt" : "minutes.txt"),
                Math.Round((DateTime.Now - from).TotalMinutes, 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>
/// Gemma 4 31B (BF16). When its multi-token-prediction draft model sits next to it (…\MTP\mtp-*.gguf), llama-server decodes
/// speculatively with it (about 3x the tokens per second, same output distribution); sampling is the one its GGUF embeds.
/// </summary>
public sealed class GemmaService(ProjectService projects, JobParityService parity, ConfigService config) : ModelRunService(projects, parity, config)
{
    public override string Id => "gemma";
    public override string Display => "Gemma 4";
    public override string ModelPath => Config.Settings.GemmaModel;
    public override string ModelName => Config.Settings.GemmaName;
    public override string LlamaDir => Config.Settings.LlamaDir;
    public override int ReasoningBudget => 24000;
    public override ModelOptions Defaults => new();
    public override string SettingsNote => "reasoning budget 24,000 tokens; the production configuration (debug mode off unless ticked)";
    public override ModelRunState State(ProjectDefinition p) => p.Gemma;

    protected override string? ServerArgs() => Beside("MTP", "mtp-*.gguf") is { } mtp ? $"--spec-type draft-mtp --spec-draft-model {mtp}" : null;
}

/// <summary>
/// Muse Glimmer 30B (BF16), on the llama.cpp build that supports it, with the model card's sampling (its GGUF embeds none) and
/// its DFlash drafter when present (…\drafter\dflash-*.gguf). Its settings are the ones it passed FNEM0300 with (71/71): debug
/// mode, edits only once half the scenarios pass, and a 48,000-token reasoning budget (it needed about 26,000; at 24,000 it
/// answered with thinking off and broke the code).
/// </summary>
public sealed class MuseService(ProjectService projects, JobParityService parity, ConfigService config) : ModelRunService(projects, parity, config)
{
    public override string Id => "muse";
    public override string Display => "Muse Glimmer";
    public override string ModelPath => Config.Settings.MuseModel;
    public override string ModelName => Config.Settings.MuseName;
    public override string LlamaDir => Config.Settings.MuseLlamaDir;
    public override int ReasoningBudget => 48000;
    public override ModelOptions Defaults => new(DebugMode: true);
    public override string SettingsNote => "reasoning budget 48,000 tokens, debug mode on (focused repair prompts, the ratchet, edits only once half pass): the settings it passed FNEM0300 with";
    public override ModelRunState State(ProjectDefinition p) => p.Muse;

    protected override string? ServerArgs()
    {
        var args = "--temp 1.0 --top-k 64 --top-p 0.95";
        return Beside("drafter", "dflash-*.gguf") is { } draft ? $"{args} --spec-type draft-dflash --spec-draft-model {draft}" : args;
    }
}

/// <summary>The models the workbench can run, by id (the API's /projects/{id}/{model}/… routes and the pages).</summary>
public sealed class ModelRunners(GemmaService gemma, MuseService muse)
{
    public IReadOnlyList<ModelRunService> All { get; } = [gemma, muse];

    public ModelRunService? Get(string id) => All.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
