using System.Text.Json;
using System.Text.Json.Nodes;

namespace MlCpu.Host.Services.Workspace;

/// <summary>Every directory a project owns, under projects/&lt;id&gt;/.</summary>
public sealed record ProjectPaths(string Dir)
{
    public string File => Path.Combine(Dir, "project.json");
    public string Oracle => Path.Combine(Dir, "oracle");
    public string OracleBin => Path.Combine(Dir, "oracle", "bin");
    public string OracleRuns => Path.Combine(Dir, "oracle", "runs");
    public string Graphs => Path.Combine(Dir, "graphs");
    public string Transpile => Path.Combine(Dir, "transpile");
    public string Publish => Path.Combine(Dir, "publish");
    public string Capabilities => Path.Combine(Dir, "capabilities");
    public string Scenarios => Path.Combine(Dir, "scenarios");
    public string Gemma => Path.Combine(Dir, "gemma");
    public string Runs => Path.Combine(Dir, "runs");
    public string Logs => Path.Combine(Dir, "logs");
}

/// <summary>
/// The workspace (projects/ under the repository, or MlCpu:WorkspaceRoot): every onboarded project and the active one.
/// Activating a project points the host's estate settings (code, copybook, JCL, PROC and control-card roots, capabilities,
/// export directory) at it, so every page and service works on the selected repository.
/// </summary>
public sealed class ProjectService
{
    readonly ConfigService _config;
    readonly object _gate = new();
    List<ProjectDefinition> _projects = [];
    readonly MlCpuSettings _defaults;

    public event Action? Changed;

    public ProjectService(ConfigService config)
    {
        _config = config;
        _defaults = Clone(config.Settings);
        Directory.CreateDirectory(Root);
        Reload();
        var active = ReadWorkspace()?["activeProject"]?.GetValue<string>();
        var initial = _projects.FirstOrDefault(p => p.Id == active) ?? _projects.FirstOrDefault();
        if (initial is not null) Apply(initial);
    }

    public string Root => _config.Settings.WorkspaceRoot;

    public IReadOnlyList<ProjectDefinition> Projects { get { lock (_gate) return _projects.ToList(); } }

    public ProjectDefinition? Active { get; private set; }

    public ProjectPaths Paths(ProjectDefinition p) => new(Path.Combine(Root, p.Id));

    public ProjectPaths? ActivePaths => Active is null ? null : Paths(Active);

    public string SourceDir(ProjectDefinition p) => Path.GetFullPath(Path.Combine(Paths(p).Dir, p.Layout.SourceDir));

    public string Resolve(ProjectDefinition p, string relToSource) =>
        Path.IsPathRooted(relToSource) ? relToSource : Path.GetFullPath(Path.Combine(SourceDir(p), relToSource));

    readonly Dictionary<string, DateTime> _stamps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The project, re-read when its project.json changed on disk (hand edits are picked up).</summary>
    public ProjectDefinition? Get(string id)
    {
        lock (_gate)
        {
            var p = _projects.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (p is null) return null;
            var file = Paths(p).File;
            var stamp = System.IO.File.Exists(file) ? System.IO.File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
            if (_stamps.TryGetValue(p.Id, out var seen) && seen == stamp) return p;
            _stamps[p.Id] = stamp;
            try
            {
                var fresh = JsonSerializer.Deserialize<ProjectDefinition>(System.IO.File.ReadAllText(file), ProjectJson.Options);
                if (fresh is null) return p;
                _projects[_projects.IndexOf(p)] = fresh;
                if (Active?.Id == fresh.Id) Active = fresh;
                return fresh;
            }
            catch (Exception e) when (e is JsonException or IOException) { return p; }
        }
    }

    public void Reload()
    {
        var list = new List<ProjectDefinition>();
        foreach (var dir in Directory.EnumerateDirectories(Root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var file = Path.Combine(dir, "project.json");
            if (!System.IO.File.Exists(file)) continue;
            try
            {
                var p = JsonSerializer.Deserialize<ProjectDefinition>(System.IO.File.ReadAllText(file), ProjectJson.Options);
                if (p is not null) list.Add(p);
            }
            catch (JsonException) { /* a broken project.json is skipped, not fatal */ }
        }
        lock (_gate) _projects = list;
        foreach (var p in list) ConfigureCopybooks(p);
        if (Active is not null && Get(Active.Id) is { } fresh) Apply(fresh, notify: false);
        Changed?.Invoke();
    }

    public void Save(ProjectDefinition p)
    {
        var paths = Paths(p);
        Directory.CreateDirectory(paths.Dir);
        System.IO.File.WriteAllText(paths.File, JsonSerializer.Serialize(p, ProjectJson.Options) + "\n");
        lock (_gate)
        {
            _projects.RemoveAll(x => x.Id == p.Id);
            _projects.Add(p);
            _projects.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
            _stamps[p.Id] = System.IO.File.GetLastWriteTimeUtc(paths.File);
        }
        ConfigureCopybooks(p);
        if (Active?.Id == p.Id) Apply(p, notify: false);
        Changed?.Invoke();
    }

    /// <summary>The analyser searches the project's configured copybook roots (layout.copybookRoots) for its programs.</summary>
    void ConfigureCopybooks(ProjectDefinition p)
    {
        var src = SourceDir(p);
        MlCpu.Cobol.Analysis.CobolModelBuilder.ConfigureCopybookRoots(src, OnboardingService.Roots(src, p.Layout.CopybookRoots));
    }

    /// <summary>Re-reads, changes and saves one project (the only safe way to update it from a background operation).</summary>
    public ProjectDefinition Update(string id, Action<ProjectDefinition> change)
    {
        lock (_gate)
        {
            var file = Path.Combine(Root, id, "project.json");
            var p = JsonSerializer.Deserialize<ProjectDefinition>(System.IO.File.ReadAllText(file), ProjectJson.Options)!;
            change(p);
            Save(p);
            return p;
        }
    }

    public void Activate(string id)
    {
        var p = Get(id) ?? throw new ArgumentException($"no project {id}");
        Apply(p);
        var ws = ReadWorkspace() ?? new JsonObject();
        ws["activeProject"] = p.Id;
        System.IO.File.WriteAllText(Path.Combine(Root, "workspace.json"), ws.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    JsonObject? ReadWorkspace()
    {
        var f = Path.Combine(Root, "workspace.json");
        try { return System.IO.File.Exists(f) ? JsonNode.Parse(System.IO.File.ReadAllText(f)) as JsonObject : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>Points the estate settings at the project (first root of each kind; the estate service reads the full lists from the project).</summary>
    void Apply(ProjectDefinition p, bool notify = true)
    {
        var s = _config.Settings;
        var src = SourceDir(p);
        string First(List<string> roots, string fallback) => roots.Count > 0 ? Path.GetFullPath(Path.Combine(src, roots[0])) : fallback;
        s.CorpusRoot = src;
        s.CodeRoot = First(p.Layout.CodeRoots, src);
        s.CopybooksRoot = First(p.Layout.CopybookRoots, "");
        s.JclRoot = First(p.Layout.JclRoots, "");
        s.ProcRoot = First(p.Layout.ProcRoots, "");
        s.ControlCardsRoot = First(p.Layout.ControlCardRoots, "");
        s.ExportDir = Paths(p).Publish;
        s.KbSiteDir = Path.Combine(Paths(p).Dir, "kb");
        s.CapabilitiesRoot = !string.IsNullOrWhiteSpace(p.Publish.CapabilitiesRoot)
            ? Path.GetFullPath(Path.IsPathRooted(p.Publish.CapabilitiesRoot) ? p.Publish.CapabilitiesRoot : Path.Combine(s.RepoRoot, p.Publish.CapabilitiesRoot))
            : Paths(p).Capabilities;
        Active = p;
        if (notify) Changed?.Invoke();
    }

    static MlCpuSettings Clone(MlCpuSettings s) => JsonSerializer.Deserialize<MlCpuSettings>(JsonSerializer.Serialize(s))!;
}
