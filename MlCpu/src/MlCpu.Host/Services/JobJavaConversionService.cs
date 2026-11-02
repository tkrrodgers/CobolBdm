namespace MlCpu.Host.Services;

public sealed record JobJavaConversion(string JobName, IReadOnlyList<JavaTranspileResult> Results, JavaProject? Project, JobScope? Scope);

/// <summary>Job conversion (§5.7): resolve, transpile each program sequentially, fetch the runtime, assemble.</summary>
public sealed class JobJavaConversionService(EstateService estate, JavaSidecarService sidecar, JavaProjectAssembler assembler, ConfigService config)
{
    public async Task<JobJavaConversion> ConvertAsync(string jobName, bool includeCalled = true, Action<string>? onProgram = null, CancellationToken ct = default,
        bool composeStorage = false)
    {
        var scope = estate.Resolve(jobName);
        if (scope is null) return new JobJavaConversion(jobName, [], null, null);
        var programs = includeCalled ? scope.Entries.Concat(scope.Called).Distinct().ToList() : scope.Entries.ToList();
        return await ConvertProgramsAsync(scope.Name, programs, [scope], false, onProgram, ct, composeStorage);
    }

    /// <summary>Several jobs in one project (an estate slice); with <paramref name="partition"/>, projection 10 splits it into domain modules.</summary>
    public async Task<JobJavaConversion> ConvertEstateAsync(string name, IReadOnlyList<string> jobNames, bool partition, Action<string>? onProgram = null, CancellationToken ct = default,
        bool composeStorage = false)
    {
        var scopes = jobNames.Select(estate.Resolve).OfType<JobScope>().ToList();
        if (scopes.Count == 0) return new JobJavaConversion(name, [], null, null);
        var programs = scopes.SelectMany(s => s.Entries.Concat(s.Called)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal).ToList();
        return await ConvertProgramsAsync(name, programs, scopes, partition, onProgram, ct, composeStorage);
    }

    public async Task<JobJavaConversion> ConvertProgramsAsync(string name, IReadOnlyList<string> programs, IReadOnlyList<JobScope> scopes, bool partition = false,
        Action<string>? onProgram = null, CancellationToken ct = default, bool composeStorage = false)
    {
        var results = new List<JavaTranspileResult>();
        foreach (var p in programs)
        {
            ct.ThrowIfCancellationRequested();
            onProgram?.Invoke(p);
            var path = estate.SourcePath(p);
            results.Add(path is null
                ? new JavaTranspileResult { Program = p, Verdict = "ERROR", Error = "source not found" }
                : await sidecar.TranspileAsync(path, transpileOnly: true, ct, composeStorage));
        }
        if (composeStorage && results.Any(r => !r.Ok || !r.StorageComposition))
            return new JobJavaConversion(name, results, null, scopes.FirstOrDefault());
        var scope = scopes.FirstOrDefault();
        if (results.All(r => !r.Compiled)) return new JobJavaConversion(name, results, null, scope);
        var runtime = await sidecar.RuntimeAsync(ct);
        var sources = estate.Programs().ToDictionary(p => Path.GetFileNameWithoutExtension(p).ToUpperInvariant(), p => p, StringComparer.OrdinalIgnoreCase);
        var s = config.Settings;
        var options = new AssemblyOptions(s.ControlCardsRoot, sources, partition, NullIfEmpty(s.DomainEvidenceRoot), NullIfEmpty(s.OrchestrationEvidenceRoot),
            CapabilitiesRoot: NullIfEmpty(s.CapabilitiesRoot));
        return new JobJavaConversion(name, results, assembler.Assemble(name, results, runtime, scopes, options), scope);
    }

    static string? NullIfEmpty(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;
}
