using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MlCpu.Cobol.Analysis;
using MlCpu.CobolKb;
using MlCpu.CobolKb.Knowledge;
using MlCpu.Core;

namespace MlCpu.Host.Services.Workspace;

/// <summary>
/// Onboarding: clone (git) or copy the source into projects/&lt;id&gt;/source, scan it (programs deduplicated by PROGRAM-ID,
/// copybook / JCL / PROC / control-card / data directories), harvest the JCL, classify programs (batch step, CALLed
/// subprogram, online, unreferenced) and derive the GnuCOBOL oracle: the jobs to run and the initial datasets, matched to
/// data files by DSN qualifiers and shaped by the program FDs. A profile (tools/profiles/*.profile.json) can override any
/// of it for a known estate.
/// </summary>
public sealed partial class OnboardingService(ProjectService projects, ConfigService config)
{
    static readonly string[] SkipDirs = [".git", ".vscode", ".idea", "node_modules", "bin", "obj", "build"];
    static readonly string[] DataExtensions = [".txt", ".dat", ".ps", ".csv", ".seq", ".data", ".ebcdic", ".bin"];

    [GeneratedRegex(@"[^a-z0-9]+")] private static partial Regex SlugRx();

    public static string Slug(string name) => SlugRx().Replace(name.ToLowerInvariant(), "-").Trim('-');

    public async Task<ProjectDefinition> OnboardAsync(string name, string source, Operation op, CancellationToken ct)
    {
        var id = Slug(name);
        if (id.Length == 0) throw new ArgumentException("the project needs a name");
        if (projects.Get(id) is not null) throw new InvalidOperationException($"project {id} already exists; remove it first or choose another name");
        var paths = new ProjectPaths(Path.Combine(projects.Root, id));
        var target = Path.Combine(paths.Dir, "source");
        Directory.CreateDirectory(paths.Dir);
        op.Report(null, $"cloning {source}");
        await CloneAsync(source, target, op, ct);
        op.Report(0.6, "scanning");
        var project = Scan(id, name, source, target, op);
        projects.Save(project);
        op.Info($"onboarded {project.Programs.Count} programs, {project.Oracle.Jobs.Count} oracle job(s), {project.Oracle.Datasets.Count} dataset(s)");
        return project;
    }

    static async Task CloneAsync(string source, string target, Operation op, CancellationToken ct)
    {
        if (Directory.Exists(target)) Toolchain.DeleteTree(target);
        bool url = Regex.IsMatch(source, @"^(https?://|git@|ssh://)", RegexOptions.IgnoreCase);
        if (url || Directory.Exists(Path.Combine(source, ".git")))
        {
            var args = url ? new[] { "clone", "--depth", "1", source, target } : ["clone", "--no-hardlinks", source, target];
            var r = await Toolchain.RunAsync("git", args, Path.GetDirectoryName(target)!, op.Info, ct: ct, toolEnvironment: false);
            if (r.ExitCode != 0) throw new InvalidOperationException($"git clone failed ({r.ExitCode}): {r.Output.Trim()}");
            op.Info($"git clone {source} -> {target}");
            return;
        }
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        await Task.Run(() => Toolchain.CopyTree(source, target, d => d is ".git"), ct);
        op.Info($"copied {Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Count()} files from {source}");
    }

    public ProjectDefinition Scan(string id, string name, string origin, string sourceDir, Operation? op = null)
    {
        var profile = ProjectProfiles.Detect(config.Settings.RepoRoot, sourceDir);
        var files = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(sourceDir, f).Split(Path.DirectorySeparatorChar).Any(seg => SkipDirs.Contains(seg, StringComparer.OrdinalIgnoreCase)))
            .Select(f => Path.GetRelativePath(sourceDir, f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        string Dir(string rel) => (Path.GetDirectoryName(rel) ?? "").Replace('\\', '/');
        bool Ext(string rel, string[] exts) => exts.Contains(Path.GetExtension(rel), StringComparer.OrdinalIgnoreCase);

        var layout = new ProjectLayout();
        var programFiles = files.Where(f => Ext(f, CobolFiles.ProgramExtensions) && CobolSourceClassifier.IsCobol(Path.Combine(sourceDir, f))).ToList();
        var copybookDirs = files.Where(f => Ext(f, CobolFiles.CopybookExtensions)).Select(Dir).Distinct().ToList();
        var jclFiles = files.Where(f => Ext(f, CobolFiles.JclExtensions)).ToList();
        layout.JclRoots = jclFiles.Select(Dir).Distinct().Where(d => !IsProcDir(d)).ToList();
        layout.ProcRoots = jclFiles.Select(Dir).Distinct().Where(IsProcDir).ToList();
        layout.ControlCardRoots = files.Where(f => Ext(f, CobolFiles.ControlCardExtensions)).Select(Dir).Distinct().ToList();
        layout.CopybookRoots = copybookDirs;
        layout.DataRoots = files.Where(f => Ext(f, DataExtensions) && !Path.GetFileName(f).Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase)
                                            && !Path.GetFileName(f).StartsWith("README", StringComparison.OrdinalIgnoreCase))
            .Select(Dir).Distinct().Where(d => !d.Contains("node_modules")).ToList();

        // Programs, deduplicated by PROGRAM-ID: the member named like its PROGRAM-ID wins, then the shallower path.
        var byId = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in programFiles)
        {
            var text = File.ReadAllText(Path.Combine(sourceDir, f), System.Text.Encoding.Latin1);
            var pid = CobolParser.FindProgramId(CobolParser.Strip(text)) ?? Path.GetFileNameWithoutExtension(f).ToUpperInvariant();
            (byId.TryGetValue(pid, out var l) ? l : byId[pid] = []).Add(f);
        }
        var programs = new List<ProgramEntry>();
        var excluded = new List<ExcludedProgram>();
        foreach (var (pid, candidates) in byId.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var ordered = candidates.OrderBy(c => string.Equals(Path.GetFileNameWithoutExtension(c), pid, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(c => c.Count(ch => ch is '/' or '\\')).ThenBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
            programs.Add(new ProgramEntry { Id = pid, Path = ordered[0].Replace('\\', '/'), Lines = File.ReadLines(Path.Combine(sourceDir, ordered[0])).Count() });
            foreach (var other in ordered.Skip(1))
                excluded.Add(new ExcludedProgram(other.Replace('\\', '/'), $"duplicate PROGRAM-ID {pid} (kept {ordered[0].Replace('\\', '/')})"));
        }
        layout.CodeRoots = programs.Select(p => Dir(p.Path)).Distinct().ToList();

        var project = new ProjectDefinition
        {
            Id = id, Name = name, Origin = origin, OnboardedAt = DateTime.Now.ToString("s"), Layout = layout, Programs = programs, Excluded = excluded
        };
        if (profile is not null) ProjectProfiles.ApplyLayout(profile, project, op);
        // Programs outside the profile's code roots are not part of the estate.
        if (profile?["layout"]?["codeRoots"] is JsonArray)
        {
            var roots = project.Layout.CodeRoots.Select(r => r.TrimEnd('/') + "/").ToList();
            foreach (var p in project.Programs.Where(p => !roots.Any(r => p.Path.StartsWith(r, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                project.Programs.Remove(p);
                project.Excluded.Add(new ExcludedProgram(p.Path, "outside the profile's code roots"));
            }
        }
        Classify(project, sourceDir, op);
        DeriveOracle(project, sourceDir, op);
        if (profile is not null) ProjectProfiles.ApplyRest(profile, project, op);
        return project;
    }

    static bool IsProcDir(string d)
    {
        var leaf = d.Split('/').Last();
        return leaf.Contains("PROC", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> Roots(string sourceDir, IEnumerable<string> rel) =>
        rel.Select(r => Path.GetFullPath(Path.Combine(sourceDir, r))).Where(Directory.Exists).ToList();

    public static Dictionary<string, string> Sources(ProjectDefinition p, string sourceDir) =>
        p.Programs.ToDictionary(x => x.Id, x => Path.GetFullPath(Path.Combine(sourceDir, x.Path)), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<JclJob> Harvest(ProjectDefinition p, string sourceDir) =>
        JclHarvester.Harvest(Roots(sourceDir, p.Layout.JclRoots), Roots(sourceDir, p.Layout.ProcRoots));

    /// <summary>Program kinds and the jobs that run them; the CALL closure decides which programs are subprograms.</summary>
    static void Classify(ProjectDefinition p, string sourceDir, Operation? op)
    {
        var sources = Sources(p, sourceDir);
        var jobs = Harvest(p, sourceDir);
        var called = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var prog in p.Programs)
        {
            try
            {
                var model = CobolModelBuilder.BuildGroundedFull(sources[prog.Id]);
                if (model.ExecCics) prog.Kind = "online";
                foreach (var c in ProgramGraphBuilder.Calls(model))
                    foreach (var t in c.ResolvedTargets.Count > 0 ? c.ResolvedTargets : [c.Target])
                        if (sources.ContainsKey(t)) (called.TryGetValue(t, out var s) ? s : called[t] = new(StringComparer.OrdinalIgnoreCase)).Add(prog.Id);
            }
            catch (Exception e) { op?.Info($"{prog.Id}: analysis failed: {e.Message}"); }
        }
        foreach (var prog in p.Programs)
        {
            prog.Jobs = jobs.Where(j => j.Expanded.Any(s => string.Equals(s.Program, prog.Id, StringComparison.OrdinalIgnoreCase))).Select(j => j.Name).Distinct().ToList();
            prog.CalledBy = called.TryGetValue(prog.Id, out var by) ? by.ToList() : [];
            if (prog.Kind == "online") continue;
            prog.Kind = prog.Jobs.Count > 0 ? "batch" : prog.CalledBy.Count > 0 ? "subprogram" : "unreferenced";
        }
    }

    /// <summary>
    /// The oracle's jobs (those whose steps run an estate batch program) and their initial datasets: every DSN a step reads
    /// that no earlier step of the sequence writes, shaped by the reading program's FD and filled from the best-matching data file.
    /// </summary>
    static void DeriveOracle(ProjectDefinition p, string sourceDir, Operation? op)
    {
        var sources = Sources(p, sourceDir);
        var jobs = Harvest(p, sourceDir).Where(j => j.Expanded.Any(s => s.Program is not null && sources.ContainsKey(s.Program)
                                                    && p.Programs.First(x => x.Id.Equals(s.Program, StringComparison.OrdinalIgnoreCase)).Kind != "online"))
            .OrderBy(j => Path.GetFileName(j.Path), StringComparer.OrdinalIgnoreCase).ToList();
        p.Oracle.Jobs = jobs.Select(j => j.Name).ToList();
        var dataFiles = Roots(sourceDir, p.Layout.DataRoots)
            .SelectMany(r => Directory.EnumerateFiles(r)).Where(f => DataExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).ToList();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            var plan = DdBindingPlanner.Plan(job, sources, DateTime.Now.ToString("yyyy-MM-dd"), out var models);
            foreach (var step in plan.Steps)
            {
                foreach (var b in step.Bindings.Where(b => b.ResolvedDsn is not null && !b.IsDummy))
                {
                    var dsn = b.ResolvedDsn!;
                    bool reads = b.Direction.Contains("input") || b.Direction.Contains("i-o") || b.Direction.Contains("extend");
                    bool writes = b.Direction.Contains("output") || b.Direction.Contains("i-o") || b.Direction.Contains("extend");
                    if (reads && !written.Contains(dsn) && p.Oracle.Datasets.All(d => !d.Dsn.Equals(dsn, StringComparison.OrdinalIgnoreCase)))
                    {
                        var def = Shape(dsn, b, models);
                        var match = BestDataFile(dsn, dataFiles);
                        def.Source = match is null ? null : Path.GetRelativePath(sourceDir, match).Replace('\\', '/');
                        p.Oracle.Datasets.Add(def);
                        op?.Info($"dataset {dsn}: {def.Format} lrecl {def.Lrecl}{(def.Key is null ? "" : " key " + def.Key)} <- {def.Source ?? "(empty: no matching data file)"}");
                        if (def.Source is null) p.Notes.Add($"{job.Name}: no data file matches {dsn}; it starts empty (add one under scenarios/ or edit project.json)");
                    }
                    if (writes) written.Add(dsn);
                }
            }
        }
        if (p.Oracle.Jobs.Count == 0) p.Notes.Add("no JCL job runs an estate batch program: the oracle has nothing to run");
        p.Publish.Jobs = p.Oracle.Jobs.ToList();
        p.Publish.Capabilities = ReachableCapabilities(p);
    }

    /// <summary>
    /// Batch programs the oracle jobs run, and every subprogram in their CALL closure at any depth (a decimal kernel CALLed
    /// by a subprogram of a subprogram is a capability too), in the order of <see cref="ProjectDefinition.Programs"/>.
    /// </summary>
    public static List<string> ReachableCapabilities(ProjectDefinition p)
    {
        var reached = new HashSet<string>(p.Programs.Where(x => x.Kind == "batch" && x.Jobs.Any(j => p.Oracle.Jobs.Contains(j))).Select(x => x.Id),
            StringComparer.OrdinalIgnoreCase);
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var x in p.Programs.Where(x => x.Kind == "subprogram" && !reached.Contains(x.Id) && x.CalledBy.Any(reached.Contains)))
                grew |= reached.Add(x.Id);
        }
        return p.Programs.Where(x => reached.Contains(x.Id)).Select(x => x.Id).ToList();
    }

    /// <summary>The dataset's format from the FD that reads it: INDEXED → ksds (key from RECORD KEY), RECORDING V / VARYING → variable, LINE SEQUENTIAL → line, else fixed.</summary>
    static DatasetDefinition Shape(string dsn, DdBinding b, Dictionary<string, CobolProgram> models)
    {
        var def = new DatasetDefinition { Dsn = dsn, From = $"{b.OwnerProgram}:{b.LogicalFile}" };
        if (!models.TryGetValue(b.OwnerProgram, out var m)) return def;
        var fd = m.Files.FirstOrDefault(f => f.Name.Equals(b.LogicalFile, StringComparison.OrdinalIgnoreCase));
        if (fd is null) return def;
        var recLen = fd.Records.Select(r => r.Length).DefaultIfEmpty(0).Max();
        def.Lrecl = fd.MaxRecord > 0 ? fd.MaxRecord : recLen;
        def.MinRecord = fd.MinRecord > 0 ? fd.MinRecord : fd.Records.Select(r => r.Length).DefaultIfEmpty(def.Lrecl).Min();
        if (fd.Organization.Contains("INDEXED", StringComparison.OrdinalIgnoreCase))
        {
            def.Format = "ksds";
            def.Key = KeySpec(m, fd, fd.RecordKey);
            def.AlternateKeys = fd.AlternateKeys.Select(k => KeySpec(m, fd, k.Name)).OfType<string>().ToList();
        }
        else if (fd.Organization.Contains("LINE", StringComparison.OrdinalIgnoreCase)) def.Format = "line";
        else if (string.Equals(fd.RecordingMode, "V", StringComparison.OrdinalIgnoreCase) || (fd.MinRecord > 0 && fd.MinRecord != fd.MaxRecord)
                 || fd.Records.Select(r => r.Length).Distinct().Count() > 1)
            def.Format = "variable";
        return def;
    }

    static string? KeySpec(CobolProgram m, FileDecl fd, string? keyName)
    {
        if (keyName is null) return null;
        var item = m.Items.FirstOrDefault(i => i.Name.Equals(keyName, StringComparison.OrdinalIgnoreCase) && fd.Records.Contains(i.Record));
        return item is null ? null : $"{item.Offset + 1},{item.Length}";
    }

    /// <summary>The data file whose name qualifiers all appear, in order, among the DSN's qualifiers (most qualifiers wins).</summary>
    static string? BestDataFile(string dsn, IReadOnlyList<string> files)
    {
        var q = dsn.ToUpperInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
        string? best = null;
        int bestScore = 0;
        foreach (var f in files)
        {
            var name = Path.GetFileNameWithoutExtension(f).ToUpperInvariant().Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
            int at = 0, score = 0;
            foreach (var part in name)
            {
                int i = Array.IndexOf(q, part, at);
                if (i < 0) { score = 0; break; }
                at = i + 1;
                score++;
            }
            if (score > bestScore) { best = f; bestScore = score; }
        }
        return best;
    }
}

/// <summary>Profiles for known estates (tools/profiles/*.profile.json): detection files and overrides of the derived project.</summary>
public static class ProjectProfiles
{
    public static JsonObject? Detect(string repoRoot, string sourceDir)
    {
        var dir = Path.Combine(repoRoot, "tools", "profiles");
        if (!Directory.Exists(dir)) return null;
        foreach (var f in Directory.EnumerateFiles(dir, "*.profile.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            if (JsonNode.Parse(File.ReadAllText(f)) is not JsonObject profile) continue;
            var detect = profile["detect"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
            if (detect.Count > 0 && detect.All(d => File.Exists(Path.Combine(sourceDir, d)))) return profile;
        }
        return null;
    }

    public static void ApplyLayout(JsonObject profile, ProjectDefinition p, Operation? op)
    {
        if (profile["layout"] is JsonObject layout)
            p.Layout = JsonSerializer.Deserialize<ProjectLayout>(layout.ToJsonString(), ProjectJson.Options)!;
        op?.Info($"profile {profile["id"]}: layout applied");
    }

    public static void ApplyRest(JsonObject profile, ProjectDefinition p, Operation? op)
    {
        if (profile["oracle"] is JsonObject oracle)
        {
            var o = JsonSerializer.Deserialize<OracleDefinition>(oracle.ToJsonString(), ProjectJson.Options)!;
            if (o.Datasets.Count == 0) o.Datasets = p.Oracle.Datasets;
            p.Oracle = o;
        }
        if (profile["publish"] is JsonObject publish)
            p.Publish = JsonSerializer.Deserialize<PublishDefinition>(publish.ToJsonString(), ProjectJson.Options)!;
        if (profile["notes"] is JsonArray notes) p.Notes.AddRange(notes.Select(n => n!.GetValue<string>()));
        op?.Info($"profile {profile["id"]}: oracle and publish settings applied");
    }
}
