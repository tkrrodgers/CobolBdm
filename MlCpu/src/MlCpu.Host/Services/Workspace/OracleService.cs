namespace MlCpu.Host.Services.Workspace;

/// <summary>
/// Produces a project's GnuCOBOL oracle: the generic provider builds (cached) and runs the JCL jobs per scenario; the
/// CardDemo profile runs its proven harness (tools/carddemo/Invoke-CardDemoParity.ps1, executables cached by hash too).
/// Afterwards project.json carries the job checks the published estate and Gemma's service are compared on.
/// </summary>
public sealed class OracleService(ProjectService projects, GnuCobolOracle generic, ConfigService config)
{
    public async Task BuildAndRunAsync(string projectId, Operation op, CancellationToken ct, bool force = false, double from = 0, double to = 1)
    {
        var p = projects.Get(projectId) ?? throw new ArgumentException($"no project {projectId}");
        if (p.Oracle.Provider == "carddemo-script")
        {
            await CardDemoScriptAsync(p, op, ct, from, to, force);
            return;
        }
        var (built, cached, problems) = await generic.BuildAsync(p, op, from, from + (to - from) * 0.4, force, ct);
        var scenarios = new List<string> { "base" };
        scenarios.AddRange(p.Oracle.Scenarios.Where(s => s != "base"));
        var checks = new List<JobCheck>();
        for (int i = 0; i < scenarios.Count; i++)
        {
            double a = from + (to - from) * (0.4 + 0.6 * i / scenarios.Count), b = from + (to - from) * (0.4 + 0.6 * (i + 1) / scenarios.Count);
            checks.AddRange(await generic.RunScenarioAsync(p, scenarios[i], op, a, b, ct));
        }
        projects.Update(p.Id, x =>
        {
            x.Oracle.JobChecks = checks;
            x.Oracle.JobCheckRoot = ".";
            x.Oracle.Status = new OracleStatus
            {
                BuiltAt = DateTime.Now.ToString("s"), Units = built + cached, UnitsCached = cached, RunAt = DateTime.Now.ToString("s"),
                Scenarios = scenarios, Problems = problems
            };
        });
        op.Info($"oracle: {checks.Count} job check(s) over {scenarios.Count} scenario(s), {checks.Sum(c => c.Outputs.Count)} outputs");
    }

    async Task CardDemoScriptAsync(ProjectDefinition p, Operation op, CancellationToken ct, double from, double to, bool force)
    {
        var paths = projects.Paths(p);
        var work = Path.Combine(paths.Dir, p.Oracle.JobCheckRoot ?? "oracle/script");
        Directory.CreateDirectory(work);
        var script = Path.Combine(config.Settings.RepoRoot, "tools", "carddemo", "Invoke-CardDemoParity.ps1");
        var app = projects.Resolve(p, "app");
        var args = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-CardDemo", app, "-Sidecar", config.Settings.SidecarUrl, "-Work", work };
        if (force) args.Add("-Rebuild");
        string[] stages = ["Build Java runtime", "Convert COBOL", "Prepare shared inputs", "Build GnuCOBOL oracle", "Run GnuCOBOL oracle", "Run generated Java", "Compare"];
        int stage = 0;
        var r = await Toolchain.RunAsync("powershell.exe", args, config.Settings.RepoRoot, line =>
        {
            op.Info(line);
            for (int i = 0; i < stages.Length; i++)
                if (line.Contains("== " + stages[i], StringComparison.Ordinal)) { stage = i; op.Report(from + (to - from) * i / stages.Length, stages[i]); }
        }, ct: ct);
        var problems = r.ExitCode == 0 ? new List<string>() : [$"Invoke-CardDemoParity.ps1 exited {r.ExitCode} (some comparisons differ or a step failed)"];
        projects.Update(p.Id, x =>
        {
            x.Oracle.Status = new OracleStatus { BuiltAt = DateTime.Now.ToString("s"), RunAt = DateTime.Now.ToString("s"), Scenarios = ["base"], Problems = problems };
        });
        if (!File.Exists(Path.Combine(work, "oracle", "sysout", "S5-POSTTRAN.txt"))) throw new InvalidOperationException($"the CardDemo oracle produced no outputs (exit {r.ExitCode})");
    }
}
