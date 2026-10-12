using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using MlCpu.Cobol.Analysis;
using MlCpu.Cobol.Analysis.Entities;
using MlCpu.Cobol.Analysis.JavaBridge;
using MlCpu.Cobol.Analysis.Projections;
using MlCpu.CobolKb.Knowledge;
using MlCpu.Core;

// Conformance gate (§11.1): executable harness; PASS/FAIL/BLOCKED per check; exit 1 on any FAIL, 3 when only BLOCKED.
// Run from the repository root:  dotnet run --project MlCpu/src/CobolConformance [-- --suite <name>] [--carddemo <app root>]
var opts = Args.Parse(args);
var repo = Repo.Root();
var corpus = Path.Combine(repo, "MlCpu", "src", "CobolConformance", "corpus");
var carddemo = opts.GetValueOrDefault("carddemo") ?? Path.GetFullPath(Path.Combine(repo, "..", "carddemo", "app"));
var report = new Report(opts.GetValueOrDefault("suite"));
var env = new Toolchain(repo);

Suites.ParserAcceptance(report, corpus);
Suites.ParserAst(report, corpus, carddemo);
Suites.Jcl(report, corpus, carddemo);
Suites.DdBinding(report, corpus, carddemo);
Suites.Replay(report, corpus, carddemo);
Suites.DomainBatch(report, corpus);
await Suites.Lowering(report, corpus, env);
await Suites.Truth(report, corpus, env);
await Suites.Parity(report, corpus, env);
Suites.KitValuesDecoding(report, corpus, env);
Suites.RecordKitScale(report, env);
await Suites.Projections(report, corpus, env);
return report.Finish();

sealed class Report(string? only)
{
    readonly List<(string Suite, string Check, string Status, string Detail)> _rows = [];

    public bool Wants(string suite) => only is null || suite.Contains(only, StringComparison.OrdinalIgnoreCase);

    public void Check(string suite, string check, bool pass, string detail = "") => Add(suite, check, pass ? "PASS" : "FAIL", detail);

    public void Blocked(string suite, string check, string why) => Add(suite, check, "BLOCKED", why);

    public void Fail(string suite, string check, Exception e) => Add(suite, check, "FAIL", e.GetType().Name + ": " + e.Message);

    void Add(string suite, string check, string status, string detail)
    {
        _rows.Add((suite, check, status, detail));
        var color = status switch { "PASS" => ConsoleColor.Green, "FAIL" => ConsoleColor.Red, _ => ConsoleColor.Yellow };
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write($"{status,-8}");
        Console.ForegroundColor = prev;
        Console.WriteLine($"{suite}/{check}{(detail.Length > 0 && status != "PASS" ? "  -- " + detail : "")}");
    }

    public int Finish()
    {
        Console.WriteLine();
        foreach (var g in _rows.GroupBy(r => r.Suite))
            Console.WriteLine($"{g.Key,-36} pass {g.Count(r => r.Status == "PASS"),3}  fail {g.Count(r => r.Status == "FAIL"),3}  blocked {g.Count(r => r.Status == "BLOCKED"),3}");
        int fail = _rows.Count(r => r.Status == "FAIL"), blocked = _rows.Count(r => r.Status == "BLOCKED");
        Console.WriteLine($"TOTAL pass {_rows.Count - fail - blocked}  fail {fail}  blocked {blocked}");
        return fail > 0 ? 1 : blocked > 0 ? 3 : 0;
    }
}

static class Args
{
    public static Dictionary<string, string?> Parse(string[] a)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < a.Length; i++)
            if (a[i].StartsWith("--")) d[a[i][2..]] = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : null;
        return d;
    }
}

static class Repo
{
    public static string Root()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "java-sidecar")) && Directory.Exists(Path.Combine(d.FullName, "MlCpu"))) return d.FullName;
        throw new InvalidOperationException("run from inside the repository (java-sidecar/ and MlCpu/ not found)");
    }
}

/// <summary>Sidecar, JDK, Gradle and GnuCOBOL as found on this machine; anything missing makes its lane BLOCKED, never PASS.</summary>
sealed class Toolchain
{
    public string Repo { get; }
    public string RuntimeClasses { get; }
    public string? JavaHome { get; }
    public string? Cobc { get; }
    readonly HttpClient _http;
    bool? _up;

    public Toolchain(string repo)
    {
        Repo = repo;
        RuntimeClasses = Path.Combine(repo, "java-sidecar", "build", "classes", "java", "main");
        JavaHome = Environment.GetEnvironmentVariable("JAVA_HOME") ?? Environment.GetEnvironmentVariable("JAVA_HOME", EnvironmentVariableTarget.User);
        var gc = Environment.GetEnvironmentVariable("GNUCOBOL_HOME") ?? Environment.GetEnvironmentVariable("GNUCOBOL_HOME", EnvironmentVariableTarget.User)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GnuCOBOL");
        var cobc = Environment.GetEnvironmentVariable("MLCPU_COBC") ?? Path.Combine(gc, "bin", "cobc.exe");
        Cobc = File.Exists(cobc) ? cobc : null;
        if (Cobc is not null)
        {
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables(EnvironmentVariableTarget.User))
                if (e.Key.ToString()!.StartsWith("COB_", StringComparison.OrdinalIgnoreCase)) Environment.SetEnvironmentVariable(e.Key.ToString()!, e.Value?.ToString());
            Environment.SetEnvironmentVariable("PATH", $"{Path.Combine(gc, "bin")};{Path.Combine(gc, "mingw64", "bin")};{Environment.GetEnvironmentVariable("PATH")}");
        }
        var url = Environment.GetEnvironmentVariable("MLCPU_JAVA_SIDECAR") ?? "http://127.0.0.1:8079";
        _http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(120) };
    }

    public string? Java(string tool) => JavaHome is null ? null : Path.Combine(JavaHome, "bin", tool + (OperatingSystem.IsWindows() ? ".exe" : ""));

    public async Task<string?> Unavailable()
    {
        if (_up is null)
        {
            try { _up = (await _http.GetAsync("/health")).IsSuccessStatusCode; }
            catch (HttpRequestException) { _up = false; }
            catch (TaskCanceledException) { _up = false; }
        }
        if (_up == false) return "Java sidecar not reachable (tools/Start-Sidecar.ps1)";
        if (Java("javac") is not { } javac || !File.Exists(javac)) return "JDK not found (JAVA_HOME)";
        if (!Directory.Exists(RuntimeClasses)) return "runtime classes missing (cd java-sidecar; gradle compileJava)";
        return null;
    }

    public Task<JsonNode> Transpile(string cobolPath) => Transpile(CobolModelBuilder.BuildGroundedFull(cobolPath));

    /// <summary>Every program of the source (separate and nested), the first one first.</summary>
    public async Task<List<JsonNode>> TranspileAll(string cobolPath)
    {
        var all = new List<JsonNode>();
        foreach (var p in CobolModelBuilder.BuildAllGroundedFull(cobolPath)) all.Add(await Transpile(p));
        return all;
    }

    public async Task<JsonNode> Transpile(MlCpu.Cobol.Analysis.CobolProgram program)
    {
        var ir = IrExporter.Export(program);
        var request = new JsonObject
        {
            ["battery"] = new JsonArray(new JsonObject { ["inputs"] = new JsonObject(), ["expect"] = new JsonObject() }),
            ["useLir"] = false, ["loopPolicy"] = "TERMINATE", ["includeJavaSource"] = true, ["transpileOnly"] = true
        };
        var resp = await _http.PostAsync("/transpile-verify", new StringContent(IrExporter.TranspileRequest(ir, request), Encoding.UTF8, "application/json"));
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
    }

    public async Task<IReadOnlyDictionary<string, string>> Runtime()
    {
        var node = JsonNode.Parse(await _http.GetStringAsync("/runtime"))!["files"]!.AsObject();
        return node.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>(), StringComparer.Ordinal);
    }

    /// <summary>Compiles generated sources (package com.mlcpu.gen) against the runtime classes; returns the class directory.</summary>
    public string CompileGenerated(string workDir, IEnumerable<(string ClassName, string Source)> sources)
    {
        var src = Path.Combine(workDir, "src", "com", "mlcpu", "gen");
        var classes = Path.Combine(workDir, "classes");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(classes);
        var files = new List<string>();
        foreach (var (cls, text) in sources)
        {
            var f = Path.Combine(src, cls + ".java");
            File.WriteAllText(f, text);
            files.Add(f);
        }
        var r = Run(Java("javac")!, ["-encoding", "UTF-8", "-nowarn", "-cp", RuntimeClasses, "-d", classes, .. files], workDir);
        if (r.Code != 0) throw new InvalidOperationException("javac failed: " + r.Output.Split('\n').FirstOrDefault(l => l.Contains("error")));
        return classes;
    }

    public (int Code, string Output) RunJava(string classes, string main, IEnumerable<string> args, Dictionary<string, string>? env = null, string? cwd = null) =>
        Run(Java("java")!, ["-Xss64m", "-cp", classes + Path.PathSeparator + RuntimeClasses, main, .. args], cwd, env);

    public static (int Code, string Output) Run(string exe, IEnumerable<string> args, string? cwd = null, Dictionary<string, string>? env = null, int timeoutMs = 600_000)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = cwd ?? Directory.GetCurrentDirectory() };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env ?? []) psi.Environment[k] = v;
        psi.StandardOutputEncoding = Encoding.Latin1;
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outText = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(timeoutMs)) { p.Kill(true); return (-1, outText + "\n<timeout>"); }
        return (p.ExitCode, outText + err.Result);
    }
}
