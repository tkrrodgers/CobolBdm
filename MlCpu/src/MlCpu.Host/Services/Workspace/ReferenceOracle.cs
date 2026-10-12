namespace MlCpu.Host.Services.Workspace;

/// <summary>
/// Compares a job's outputs with the project's reference oracle (<see cref="ReferenceDefinition"/>): the outputs of the
/// application the estate came from, or of the mainframe. For the Fineract estate the reference is oracle A, the Fineract
/// jar run by FnHarness, so the GnuCOBOL run (A = B), the published Java (A = C) and Gemma's service (A = D) are each
/// judged against the original Java.
/// </summary>
public static class ReferenceOracle
{
    /// <summary>
    /// One result per reference output the job writes. <paramref name="actual"/> maps an output key of the job check
    /// ("data:DSN" or "sysout:NAME") to the file the run under test wrote, or null when it wrote none.
    /// </summary>
    public static List<ParityResult> Compare(ProjectDefinition p, string projectDir, JobCheck check, Func<string, string?> actual)
    {
        var results = new List<ParityResult>();
        if (p.Reference is not { } r || r.Outputs.Count == 0) return results;
        foreach (var (output, file) in r.Outputs)
        {
            var key = output.Contains(':') ? output : "data:" + output;
            if (!check.Outputs.ContainsKey(key)) continue; // this job does not write it
            var name = key.Split(':', 2)[1];
            var expected = Expected(r, projectDir, check.Scenario, file);
            if (!File.Exists(expected))
            {
                results.Add(new ParityResult(check.Job, check.Scenario, name, "NOT COVERED", $"no reference file {expected}"));
                continue;
            }
            var got = actual(key);
            results.Add(got is null
                ? new ParityResult(check.Job, check.Scenario, name, "MISSING", "the run did not write it")
                : JobParityService.Compare(check, name, got, expected, key.StartsWith("sysout:", StringComparison.Ordinal)));
        }
        return results;
    }

    /// <summary>
    /// The reference folder of a scenario: <c>Root/ScenarioMap[scenario]</c>, else <c>Root/scenario</c>. A mapping may name
    /// several folders separated by ';' (a "base" scenario whose jobs were captured in different reference scenarios);
    /// this is the first of them.
    /// </summary>
    public static string Folder(ReferenceDefinition r, string projectDir, string scenario) => Folders(r, projectDir, scenario)[0];

    public static List<string> Folders(ReferenceDefinition r, string projectDir, string scenario)
    {
        var root = Path.IsPathRooted(r.Root) ? r.Root : Path.GetFullPath(Path.Combine(projectDir, r.Root));
        var names = r.ScenarioMap.TryGetValue(scenario, out var mapped) ? mapped.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [scenario];
        return names.Select(n => Path.Combine(root, n)).DefaultIfEmpty(Path.Combine(root, scenario)).ToList();
    }

    /// <summary>The reference file of an output in a scenario: in the first of its folders that has it (else the first folder's path).</summary>
    public static string Expected(ReferenceDefinition r, string projectDir, string scenario, string file)
    {
        var folders = Folders(r, projectDir, scenario);
        return folders.Select(f => Path.Combine(f, file)).FirstOrDefault(File.Exists) ?? Path.Combine(folders[0], file);
    }

    public static string Summary(ProjectDefinition p, IReadOnlyCollection<ParityResult> results, string what) =>
        $"reference: {results.Count(x => x.Verdict == "MATCH")}/{results.Count} {what} outputs MATCH {p.Reference?.Label}" +
        (results.Any(x => x.Verdict == "NOT COVERED") ? $" ({results.Count(x => x.Verdict == "NOT COVERED")} not covered)" : "");
}
