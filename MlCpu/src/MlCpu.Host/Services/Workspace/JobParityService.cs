using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MlCpu.Host.Services.Workspace;

public sealed record ParityResult(string Job, string Scenario, string Output, string Verdict, string Detail);

public sealed record TestSuiteResult(string Suite, int Tests, int Failed, int Skipped);

public sealed record BuildTestReport(string Project, string Implementation, string At, bool Compiled, int Tests, int Failed,
    IReadOnlyList<TestSuiteResult> Suites, IReadOnlyList<string> FailedTests, IReadOnlyList<ParityResult> Parity)
{
    public int Match => Parity.Count(p => p.Verdict == "MATCH");
}

/// <summary>
/// Build &amp; test of a published estate (or a model's service): gradle test (every equivalence, oracle, characterization and
/// platform test), then each job check — stage the oracle's input datasets, run the job through the generated JobRunner
/// (java -cp build/install/…/lib/*), compare every output dataset byte for byte and SYSOUT line by line (GnuCOBOL on Windows
/// writes CRLF) with the GnuCOBOL oracle.
/// </summary>
public sealed class JobParityService(ProjectService projects)
{
    static readonly Encoding Latin1 = Encoding.Latin1;

    public async Task<BuildTestReport> BuildAndTestAsync(ProjectDefinition p, string projectDir, string implementation, string label, Operation op,
        double from, double to, CancellationToken ct, bool runTests = true)
    {
        var paths = projects.Paths(p);
        // 1. gradle test (+ installDist for the job runs)
        op.Report(from, "gradle test");
        int passed = 0, failed = 0;
        var failedTests = new List<string>();
        var expected = Math.Max(1, CountTests(projectDir));
        var tasks = runTests ? new[] { "test", "installDist" } : ["installDist"];
        var gradle = await Toolchain.RunAsync(Toolchain.Gradle, [.. tasks, "--continue", "--console=plain", "--no-daemon", "-q"], projectDir, line =>
        {
            var m = Regex.Match(line, @"^(\S.*) > (.+) (PASSED|FAILED|SKIPPED)\s*$");
            if (!m.Success) { if (line.Contains("error:") || line.Contains("FAILED")) op.Info(line); return; }
            if (m.Groups[3].Value == "FAILED") { failed++; failedTests.Add($"{m.Groups[1].Value} > {m.Groups[2].Value}"); op.Info($"FAILED {m.Groups[1].Value} > {m.Groups[2].Value}"); }
            else passed++;
            op.Report(from + (to - from) * 0.6 * Math.Min(1, (passed + failed) / (double)expected), $"gradle test: {passed + failed} run, {failed} failed");
        }, timeout: TimeSpan.FromMinutes(40), ct: ct);
        var suites = Suites(projectDir);
        bool compiled = Directory.Exists(Path.Combine(projectDir, "build", "install"));
        if (!compiled) op.Info("the project did not build: " + string.Join(" | ", gradle.Output.Split('\n').Where(l => l.Contains("error")).Take(8)));
        int total = suites.Sum(s => s.Tests), totalFailed = suites.Sum(s => s.Failed);
        op.Info($"gradle test: {total - totalFailed}/{total} passed in {suites.Count} suite(s)");

        // 2. job checks against the GnuCOBOL oracle
        var parity = new List<ParityResult>();
        if (compiled)
        {
            var lib = Directory.EnumerateDirectories(Path.Combine(projectDir, "build", "install")).Select(d => Path.Combine(d, "lib")).FirstOrDefault(Directory.Exists);
            var checks = p.Oracle.JobChecks;
            var root = Path.GetFullPath(Path.Combine(paths.Dir, p.Oracle.JobCheckRoot ?? "."));
            for (int i = 0; i < checks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var c = checks[i];
                op.Report(from + (to - from) * (0.6 + 0.4 * i / Math.Max(1, checks.Count)), $"job {c.Job} ({c.Scenario}) vs GnuCOBOL");
                parity.AddRange(await RunCheckAsync(p, c, root, lib!, projectDir, Path.Combine(paths.Runs, label, c.Scenario, c.Job), implementation, op, ct));
            }
            op.Info($"job checks: {parity.Count(r => r.Verdict == "MATCH")}/{parity.Count} outputs MATCH the GnuCOBOL oracle ({implementation})");
        }
        var report = new BuildTestReport(p.Id, implementation, DateTime.Now.ToString("s"), compiled, total, totalFailed, suites, failedTests, parity);
        Directory.CreateDirectory(Path.Combine(paths.Runs, label));
        await File.WriteAllTextAsync(Path.Combine(paths.Runs, label, "report.json"), JsonSerializer.Serialize(report, ProjectJson.Options) + "\n", ct);
        return report;
    }

    public BuildTestReport? LastReport(ProjectDefinition p, string label)
    {
        var f = Path.Combine(projects.Paths(p).Runs, label, "report.json");
        try { return File.Exists(f) ? JsonSerializer.Deserialize<BuildTestReport>(File.ReadAllText(f), ProjectJson.Options) : null; }
        catch (JsonException) { return null; }
    }

    async Task<List<ParityResult>> RunCheckAsync(ProjectDefinition p, JobCheck c, string root, string lib, string projectDir, string runDir, string implementation, Operation op, CancellationToken ct)
    {
        if (Directory.Exists(runDir)) Toolchain.DeleteTree(runDir);
        var data = Directory.CreateDirectory(Path.Combine(runDir, "data")).FullName;
        var sysout = Directory.CreateDirectory(Path.Combine(runDir, "sysout")).FullName;
        foreach (var (dsn, rel) in c.Inputs) File.Copy(Path.Combine(root, rel), Path.Combine(data, dsn), true);
        var args = new List<string> { "-Xss64m", "-cp", Path.Combine(lib, "*"), "com.mlcpu.app.Application",
            "--mlcpu.job=" + c.Job, "--mlcpu.data-dir=" + data, "--mlcpu.sysout-dir=" + sysout, "--mlcpu.clock=FIXED=" + p.Oracle.Clock,
            "--mlcpu.display-numeric=gnucobol", "--mlcpu.binary-truncate=no", "--mlcpu.ctl-dir=" + Path.Combine(projectDir, "ctl") };
        if (implementation is "legacy" or "normalized") args.Add("--mlcpu.implementation=" + implementation);
        var log = new StringBuilder();
        var r = await Toolchain.RunAsync(Toolchain.Java, args, runDir, line => log.AppendLine(line),
            new Dictionary<string, string> { ["MLCPU_DEFAULT_BYTE"] = "0" }, TimeSpan.FromMinutes(20), ct);
        await File.WriteAllTextAsync(Path.Combine(runDir, "job.log"), log.ToString(), ct);
        foreach (var line in log.ToString().Split('\n').Where(l => Regex.IsMatch(l, @" rc=\d+ ") || l.StartsWith("MAXCC"))) op.Info($"   {c.Job}: {line.Trim()}");
        var results = new List<ParityResult>();
        foreach (var (key, rel) in c.Outputs)
        {
            var kind = key.Split(':', 2)[0];
            var name = key.Split(':', 2)[1];
            var actual = Path.Combine(kind == "data" ? data : sysout, name);
            var oracle = Path.Combine(root, rel);
            results.Add(Compare(c, name, actual, oracle, kind == "sysout"));
        }
        if (r.ExitCode < 0) results.Add(new ParityResult(c.Job, c.Scenario, "(job)", "TIMEOUT", "the job did not finish"));
        return results;
    }

    static ParityResult Compare(JobCheck c, string name, string actual, string oracle, bool sysout)
    {
        if (!File.Exists(oracle)) return new ParityResult(c.Job, c.Scenario, name, "NO ORACLE", oracle);
        if (!File.Exists(actual)) return new ParityResult(c.Job, c.Scenario, name, "MISSING", "the job did not write it");
        var a = File.ReadAllBytes(actual);
        var b = File.ReadAllBytes(oracle);
        if (sysout)
        {
            a = Latin1.GetBytes(Latin1.GetString(a).Replace("\r\n", "\n"));
            b = Latin1.GetBytes(Latin1.GetString(b).Replace("\r\n", "\n"));
        }
        int i = a.AsSpan().CommonPrefixLength(b);
        if (i == a.Length && a.Length == b.Length) return new ParityResult(c.Job, c.Scenario, name, "MATCH", $"{a.Length:N0} bytes");
        return new ParityResult(c.Job, c.Scenario, name, "DIFFER",
            $"first difference at byte {i} (java {a.Length:N0} / oracle {b.Length:N0}): java «{Excerpt(a, i)}» oracle «{Excerpt(b, i)}»");
    }

    static string Excerpt(byte[] b, int at)
    {
        var sb = new StringBuilder();
        for (int i = Math.Max(0, at - 16); i < Math.Min(b.Length, at + 32); i++)
            sb.Append(b[i] is >= 0x20 and < 0x7F ? (char)b[i] : b[i] == '\n' ? "\\n" : $"\\x{b[i]:x2}");
        return sb.ToString();
    }

    /// <summary>Rough number of test cases (for the progress bar): @Test methods plus parameterized scenarios.</summary>
    static int CountTests(string projectDir)
    {
        var dir = Path.Combine(projectDir, "src", "test", "java");
        if (!Directory.Exists(dir)) return 1;
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*.java", SearchOption.AllDirectories))
        {
            var t = File.ReadAllText(f);
            n += Regex.Matches(t, @"@Test\b").Count + Regex.Matches(t, @"@ParameterizedTest").Count * 8;
        }
        return n;
    }

    static List<TestSuiteResult> Suites(string projectDir)
    {
        var dir = Path.Combine(projectDir, "build", "test-results", "test");
        if (!Directory.Exists(dir)) return [];
        var list = new List<TestSuiteResult>();
        foreach (var f in Directory.EnumerateFiles(dir, "TEST-*.xml"))
        {
            try
            {
                var x = System.Xml.Linq.XDocument.Load(f).Root!;
                int Attr(string n) => int.TryParse(x.Attribute(n)?.Value, out var v) ? v : 0;
                list.Add(new TestSuiteResult(x.Attribute("name")?.Value ?? Path.GetFileName(f), Attr("tests"), Attr("failures") + Attr("errors"), Attr("skipped")));
            }
            catch (System.Xml.XmlException) { }
        }
        return list.OrderBy(s => s.Suite, StringComparer.Ordinal).ToList();
    }
}
