using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MlCpu.Host.Services.Workspace;

public sealed record SweepResult(string Scenario, string Job, string Output, string Verdict, string Detail, double Seconds);

public sealed record SweepReport(string Project, string Service, string Implementation, string Reference, string At, int Scenarios,
    double Minutes, IReadOnlyList<SweepResult> Results)
{
    public int Match => Results.Count(r => r.Verdict == "MATCH");
}

/// <summary>
/// The reference sweep: runs a built service (the published estate, or a model's service) over every scenario the reference
/// oracle has — not only the few the GnuCOBOL oracle and the capability tests use — in parallel, and compares each job
/// output with the reference. For the Fineract estate this is gate G5 (A = C) or G6b (A = D) over the full corpus.
/// Each scenario's input datasets are built exactly as the GnuCOBOL oracle builds them (<see cref="GnuCobolOracle.Image"/>):
/// a file in the scenario's input folder overrides the base source by its file name, DSN or DSN.txt.
/// </summary>
public sealed class ReferenceSweepService(ProjectService projects)
{
    /// <summary>Per job; MLCPU_SWEEP_JOB_TIMEOUT_MIN overrides (10,000 long loans take the Java more than 2 hours on a busy machine).</summary>
    static int JobTimeoutMinutes => int.TryParse(Environment.GetEnvironmentVariable("MLCPU_SWEEP_JOB_TIMEOUT_MIN"), out var m) ? m : 480;

    public async Task<SweepReport> RunAsync(string projectId, string serviceDir, string implementation, string scenarioInputs, string? filter,
        int parallel, Operation op, CancellationToken ct)
    {
        var p = projects.Get(projectId) ?? throw new ArgumentException($"no project {projectId}");
        if (p.Reference is not { } reference) throw new InvalidOperationException("the project has no reference oracle (project.json: reference)");
        var paths = projects.Paths(p);
        var lib = Directory.Exists(Path.Combine(serviceDir, "build", "install"))
            ? Directory.EnumerateDirectories(Path.Combine(serviceDir, "build", "install")).Select(d => Path.Combine(d, "lib")).FirstOrDefault(Directory.Exists)
            : null;
        if (lib is null) throw new InvalidOperationException($"{serviceDir} is not built (run Build & test first: gradle installDist)");
        var refRoot = ReferenceOracle.Folder(reference, paths.Dir, "");
        var glob = new Regex("^" + Regex.Escape(filter ?? "*").Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);
        var scenarios = Directory.EnumerateDirectories(refRoot).Select(Path.GetFileName).OfType<string>()
            .Where(s => glob.IsMatch(s) && Directory.Exists(Path.Combine(scenarioInputs, s))).Order(StringComparer.Ordinal).ToList();
        if (scenarios.Count == 0) throw new InvalidOperationException($"no scenario folder of {refRoot} matching '{filter}' has inputs under {scenarioInputs}");
        var label = $"sweep-{implementation}-{DateTime.Now:yyyyMMdd-HHmmss}";
        var root = Path.Combine(paths.Runs, label);
        op.Info($"reference sweep: {scenarios.Count} scenario(s), {parallel} in parallel, service {serviceDir} ({implementation}) -> {root}");
        var results = new ConcurrentBag<SweepResult>();
        // Output key ("data:DSN") -> the jobs that write it, from the oracle's job checks.
        var writers = p.Oracle.JobChecks.SelectMany(c => c.Outputs.Keys.Select(k => (k, c.Job))).GroupBy(x => x.k)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Job).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var clock = Stopwatch.StartNew();
        int done = 0;
        await Parallel.ForEachAsync(scenarios, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallel), CancellationToken = ct }, async (s, token) =>
        {
            var sw = Stopwatch.StartNew();
            var run = Path.Combine(root, s);
            var data = Directory.CreateDirectory(Path.Combine(run, "data")).FullName;
            var sysoutRoot = Directory.CreateDirectory(Path.Combine(run, "sysout")).FullName;
            foreach (var d in p.Oracle.Datasets)
                await File.WriteAllBytesAsync(JobSteps.DataFile(data, d.Dsn), GnuCobolOracle.Image(d, InputFile(p, scenarioInputs, s, d)), token);
            var log = new StringBuilder();
            bool timedOut = false;
            string sysout = sysoutRoot;
            foreach (var job in p.Oracle.Jobs.Where(j => OracleDefinition.RunsJob(p.Oracle, s, j)))
            {
                sysout = Directory.CreateDirectory(Path.Combine(sysoutRoot, job)).FullName; // SYSOUT per job, as the oracle keeps it
                var args = new List<string> { "-Xss64m", "-cp", Path.Combine(lib, "*"), "com.mlcpu.app.Application", "--mlcpu.job=" + job,
                    "--mlcpu.data-dir=" + data, "--mlcpu.sysout-dir=" + sysout, "--mlcpu.clock=FIXED=" + p.Oracle.Clock,
                    "--mlcpu.display-numeric=gnucobol", "--mlcpu.binary-truncate=no", "--mlcpu.ctl-dir=" + Path.Combine(serviceDir, "ctl"),
                    "--mlcpu.implementation=" + implementation };
                var r = await Toolchain.RunAsync(Toolchain.Java, args, run, line => log.AppendLine(line),
                    new Dictionary<string, string> { ["MLCPU_DEFAULT_BYTE"] = "0" }, TimeSpan.FromMinutes(JobTimeoutMinutes), token);
                if (r.TimedOut || r.ExitCode < 0)
                {
                    // A job cut by the time limit wrote partial outputs: report the scenario as not run, never as a difference.
                    results.Add(new SweepResult(s, job, "(job)", "TIMEOUT", $"the job did not finish in {JobTimeoutMinutes} minutes", sw.Elapsed.TotalSeconds));
                    timedOut = true;
                    break;
                }
            }
            if (timedOut)
            {
                await File.WriteAllTextAsync(Path.Combine(run, "job.log"), log.ToString(), token);
                op.Info($"{s}: TIMEOUT after {sw.Elapsed.TotalSeconds:F0}s (not compared)");
                Interlocked.Increment(ref done);
                return;
            }
            await File.WriteAllTextAsync(Path.Combine(run, "job.log"), log.ToString(), token);
            var ran = p.Oracle.Jobs.Where(j => OracleDefinition.RunsJob(p.Oracle, s, j)).ToList();
            foreach (var (output, file) in reference.Outputs)
            {
                var key = output.Contains(':') ? output : "data:" + output;
                // An output that, by the oracle's job checks, only jobs this scenario does not run write is not expected here.
                if (writers.TryGetValue(key, out var by) && !by.Overlaps(ran)) continue;
                var name = key.Split(':', 2)[1];
                var actual = key.StartsWith("sysout:", StringComparison.Ordinal)
                    ? ran.Select(j => Path.Combine(sysoutRoot, j, name)).LastOrDefault(File.Exists) ?? Path.Combine(sysout, name)
                    : Path.Combine(data, name);
                var expected = ReferenceOracle.Expected(reference, paths.Dir, s, file);
                var check = new JobCheck { Job = "*", Scenario = s };
                var res = !File.Exists(expected) ? new ParityResult("*", s, name, "NOT COVERED", "no reference file")
                    : !File.Exists(actual) ? new ParityResult("*", s, name, "MISSING", "the service did not write it")
                    : JobParityService.Compare(check, name, actual, expected, key.StartsWith("sysout:", StringComparison.Ordinal));
                results.Add(new SweepResult(s, res.Job, res.Output, res.Verdict, res.Detail, sw.Elapsed.TotalSeconds));
                if (res.Verdict != "MATCH") op.Info($"   {s} {name}: {res.Verdict} {res.Detail}");
            }
            // Keep the evidence of a difference; drop the data of a scenario that matched (the full corpus is gigabytes).
            if (results.Where(x => x.Scenario == s).All(x => x.Verdict == "MATCH")) Toolchain.DeleteTree(Path.Combine(run, "data"));
            int n = Interlocked.Increment(ref done);
            op.Report((double)n / scenarios.Count, $"{n}/{scenarios.Count} scenarios");
            op.Info($"{s}: {(results.Where(x => x.Scenario == s).All(x => x.Verdict == "MATCH") ? "MATCH" : "DIFFER")} in {sw.Elapsed.TotalSeconds:F0}s");
        });
        var list = results.OrderBy(r => r.Scenario, StringComparer.Ordinal).ThenBy(r => r.Output, StringComparer.Ordinal).ToList();
        var report = new SweepReport(p.Id, serviceDir, implementation, reference.Label, DateTime.Now.ToString("s"), scenarios.Count, clock.Elapsed.TotalMinutes, list);
        await File.WriteAllTextAsync(Path.Combine(root, "report.json"), JsonSerializer.Serialize(report, ProjectJson.Options) + "\n", ct);
        await File.WriteAllTextAsync(Path.Combine(root, "REPORT.md"), Markdown(report), ct);
        op.Info($"reference sweep: {report.Match}/{list.Count} outputs MATCH {reference.Label} over {scenarios.Count} scenario(s) in {report.Minutes:F1} min");
        return report;
    }

    /// <summary>The scenario's input for a dataset: a file named like the base source, the DSN or DSN.txt; else the base source.</summary>
    string? InputFile(ProjectDefinition p, string scenarioInputs, string scenario, DatasetDefinition d)
    {
        var dir = Path.Combine(scenarioInputs, scenario);
        foreach (var name in new[] { d.Source is null ? null : Path.GetFileName(d.Source), d.Dsn, d.Dsn + ".txt" }.OfType<string>())
            if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return d.Source is null ? null : projects.Resolve(p, d.Source);
    }

    static string Markdown(SweepReport r)
    {
        var sb = new StringBuilder($"# Reference sweep: {r.Project} ({r.Implementation})\n\n");
        sb.Append($"| | |\n| --- | --- |\n| Service | `{r.Service}` |\n| Reference | {r.Reference} |\n| At | {r.At} |\n");
        sb.Append($"| Scenarios | {r.Scenarios} |\n| Outputs matching | **{r.Match}/{r.Results.Count}** |\n| Minutes | {r.Minutes:F1} |\n\n");
        sb.Append("| Scenario | Output | Verdict | Seconds | Detail |\n| --- | --- | --- | --- | --- |\n");
        foreach (var x in r.Results)
            sb.Append($"| {x.Scenario} | {x.Output} | {x.Verdict} | {x.Seconds:F0} | {(x.Verdict == "MATCH" ? x.Detail : x.Detail.Replace("|", "\\|"))} |\n");
        return sb.ToString();
    }
}
