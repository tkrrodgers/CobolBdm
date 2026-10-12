using System.Text.RegularExpressions;
using MlCpu.CobolKb.Knowledge;
using MlCpu.Core;
using MlCpu.Host.Services.Workspace;

namespace MlCpu.Host.Services;

public sealed record JobStep(int Order, string Step, string? Program, string? Proc, string? Parm);

public sealed record JobScope(string Name, string Path, IReadOnlyList<JobStep> Steps, IReadOnlyList<string> Entries,
    IReadOnlyList<string> Called, IReadOnlyList<string> Utilities, JclJob? Jcl = null);

/// <summary>
/// Program and job resolution (§5.7) for the active project: its programs (deduplicated by PROGRAM-ID at onboarding) and
/// its jobs from the JCL harvester (PROCs expanded, symbols substituted), cached until <see cref="Reload"/>. Without a
/// project, programs come from a code-root scan.
/// </summary>
public sealed partial class EstateService(ConfigService config, ProjectService projects)
{
    [GeneratedRegex(@"CALL\s+'([A-Z0-9#@$-]+)'", RegexOptions.IgnoreCase)]
    private static partial Regex CallRx();

    IReadOnlyList<JclJob>? _jobs;
    string? _jobsFor;

    public void Reload() => _jobs = null;

    public IReadOnlyList<JclJob> Harvest()
    {
        var key = projects.Active?.Id ?? "";
        if (_jobs is not null && _jobsFor == key) return _jobs;
        _jobsFor = key;
        if (projects.Active is { } p) return _jobs = OnboardingService.Harvest(p, projects.SourceDir(p));
        return _jobs = JclHarvester.Harvest([config.Settings.JclRoot], Directory.Exists(config.Settings.ProcRoot) ? [config.Settings.ProcRoot] : []);
    }

    public IReadOnlyList<string> Programs() => projects.Active is { } p
        ? OnboardingService.Sources(p, projects.SourceDir(p)).Values.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
        : CobolFiles.Programs(config.Settings.CodeRoot);

    /// <summary>Program id → source path (the PROGRAM-ID, which may differ from the member name).</summary>
    public IReadOnlyDictionary<string, string> Sources() => projects.Active is { } p
        ? OnboardingService.Sources(p, projects.SourceDir(p))
        : CobolFiles.Programs(config.Settings.CodeRoot).ToDictionary(x => System.IO.Path.GetFileNameWithoutExtension(x).ToUpperInvariant(), x => x, StringComparer.OrdinalIgnoreCase);

    public string? SourcePath(string programId) => Sources().TryGetValue(programId, out var p) ? p : null;

    public IReadOnlyList<string> Jobs() => Harvest().Select(j => j.Name).ToList();

    public JobScope? Resolve(string job)
    {
        var jcl = Harvest().FirstOrDefault(j => string.Equals(j.Name, job, StringComparison.OrdinalIgnoreCase)
            || string.Equals(System.IO.Path.GetFileNameWithoutExtension(j.Path), job, StringComparison.OrdinalIgnoreCase));
        return jcl is null ? null : Resolve(jcl, Sources());
    }

    /// <summary>A job's steps, the estate programs it runs (entries), their static CALL closure, and the utilities it uses.</summary>
    public static JobScope Resolve(JclJob jcl, IReadOnlyDictionary<string, string> known)
    {
        var steps = jcl.Steps.Select(s => new JobStep(s.Order, s.QualifiedStep, s.Program,
            s.InvocationChain.Count > 1 ? string.Join(" > ", s.InvocationChain.Skip(1)) : null, s.Parm)).ToList();
        var entries = jcl.Expanded.Where(s => s.Program is not null && known.ContainsKey(s.Program)).Select(s => s.Program!.ToUpperInvariant()).Distinct().ToList();
        var utilities = steps.Where(s => s.Program is null || !known.ContainsKey(s.Program)).Select(s => s.Program ?? "PROC (unresolved)").Distinct().ToList();
        // Breadth-first CALL closure over in-estate programs: literal CALLs, plus dynamic CALLs resolved by reaching definitions.
        var called = new SortedSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(entries);
        while (queue.Count > 0 && called.Count < 2000)
        {
            var p = queue.Dequeue();
            if (!known.TryGetValue(p, out var src)) continue;
            var targets = CallRx().Matches(File.ReadAllText(src)).Select(c => c.Groups[1].Value.ToUpperInvariant()).ToList();
            try
            {
                foreach (var c in MlCpu.Cobol.Analysis.ProgramGraphBuilder.Calls(MlCpu.Cobol.Analysis.CobolModelBuilder.BuildGroundedFull(src)))
                    targets.AddRange(c.ResolvedTargets.Select(t => t.ToUpperInvariant()));
            }
            catch (Exception) { /* the literal scan still applies */ }
            foreach (var target in targets.Distinct())
                if (known.ContainsKey(target) && !entries.Contains(target) && called.Add(target)) queue.Enqueue(target);
        }
        return new JobScope(jcl.Name, jcl.Path, steps, entries, called.ToList(), utilities, jcl);
    }
}
