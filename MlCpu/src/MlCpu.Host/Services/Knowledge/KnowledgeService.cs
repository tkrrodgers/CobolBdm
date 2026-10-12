using System.Text.Json;
using System.Text.Json.Nodes;
using MlCpu.Host.Services.Workspace;
using MlCpu.SemanticKb;
using MlCpu.SemanticKb.Embedding;
using MlCpu.SemanticKb.Index;
using MlCpu.SemanticKb.Labels;

namespace MlCpu.Host.Services.Knowledge;

/// <summary>
/// The knowledge base in the workbench (knowledgeBaseGroundGemma4.md §10): the indexes of the workspace's projects,
/// search, catalogs, glossary, grounding packets, and the build as a background operation. Settings: MlCpu:EmbeddingModelDir
/// (default C:\models\embeddinggemma-300m-ONNX), MlCpu:KbSetDir (default &lt;workspace&gt;\..\work\kb), MlCpu:KbWorkers (8),
/// MlCpu:AskBridgeUrl (the Phase 2 VSIX listener, default http://127.0.0.1:5199).
/// </summary>
public sealed class KnowledgeService : IDisposable
{
    public const string OpProject = "knowledge";
    public const string BuildKind = "kb-build";
    public static readonly string[] DefaultProjects = ["carddemo", "zopeneditor-sample", "fineract-legacy"];

    readonly OperationService _ops;
    readonly Lazy<KbRuntime?> _kb;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    Dictionary<string, int>? _termUse;

    public KbPaths? Paths { get; }
    public string? Problem { get; }
    public string Workspace { get; }
    public string SetDir { get; }
    public int Workers { get; }
    public string AskBridgeUrl { get; }

    public KnowledgeService(ConfigService config, IConfiguration configuration, OperationService ops)
    {
        _ops = ops;
        Workspace = config.Settings.WorkspaceRoot;
        SetDir = Path.GetFullPath(configuration["MlCpu:KbSetDir"] is { Length: > 0 } s ? s : Path.Combine(Workspace, "..", "work", "kb"));
        Workers = int.TryParse(configuration["MlCpu:KbWorkers"], out var w) ? w : 8;
        AskBridgeUrl = (configuration["MlCpu:AskBridgeUrl"] is { Length: > 0 } a ? a : "http://127.0.0.1:5199").TrimEnd('/');
        try { Paths = KbPaths.Discover(config.Settings.RepoRoot, configuration["MlCpu:EmbeddingModelDir"] is { Length: > 0 } m ? m : null); }
        catch (Exception e) { Problem = e.Message; }
        _kb = new(() => Paths is null ? null : new KbRuntime(Paths, Workspace));
    }

    public KbRuntime? Kb => _kb.Value;

    public IReadOnlyList<string> Indexed() => Kb?.Indexed() ?? [];

    public KbManifest? Manifest(string project)
    {
        var f = Path.Combine(Workspace, project, "knowledge", "manifest.json");
        return File.Exists(f) ? KbJson.Read<KbManifest>(f) : null;
    }

    public JsonNode? Catalog(string project)
    {
        var f = Path.Combine(Workspace, project, "knowledge", "catalog.json");
        return File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f)) : null;
    }

    public JsonNode? Classification()
    {
        var f = Path.Combine(SetDir, "classification.json");
        return File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f)) : null;
    }

    public bool ModelPresent(string variant) =>
        Paths is not null && File.Exists(Path.Combine(Paths.ModelDir, "onnx", EmbeddingGemma.ModelFileName(variant)));

    /// <summary>How many chunks use each glossary term (computed once per loaded index).</summary>
    public IReadOnlyDictionary<string, int> TermUse()
    {
        if (_termUse is not null || Kb is null) return _termUse ?? [];
        var use = new Dictionary<string, int>();
        foreach (var c in Kb.Search.Chunks)
            foreach (var h in Kb.Glossary.Find(c.Title + "\n" + c.Text))
                use[h.Term.Id] = use.GetValueOrDefault(h.Term.Id) + 1;
        return _termUse = use;
    }

    public Operation StartBuild(IReadOnlyList<string> projects, string variant = "fp32", bool rebuild = false) =>
        _ops.Start(OpProject, BuildKind, $"Build the knowledge base ({string.Join(", ", projects)})", async (op, ct) =>
        {
            if (Paths is null) throw new InvalidOperationException(Problem ?? "knowledge/ not found");
            if (!ModelPresent(variant))
            {
                op.Report(null, "downloading EmbeddingGemma");
                await ModelStore.EnsureAsync(Paths.ModelDir, variant, op.Info, ct);
            }
            var result = await Task.Run(() =>
            {
                using var embedder = new EmbeddingGemma(Paths.ModelDir, variant);
                return new KbBuilder(Paths, new BuildOptions(variant, Workers, Rebuild: rebuild)).Build(Workspace, projects, SetDir, embedder, op.Info, (p, s) => op.Report(p, s), ct);
            }, ct);
            op.Result = new
            {
                projects = result.Projects.Select(m => new { m.Project, m.Chunks, m.Tokens, m.Embedded, m.CacheHits, m.EmbedSeconds }),
                classifiers = result.Classifiers.Select(r => new { r.Family, r.Examples, r.MicroAccuracy, r.MacroAccuracy, r.PrototypeMicroAccuracy })
            };
            Kb?.Reload();
            _termUse = null;
        });

    /// <summary>The Phase 2 VSIX listener's /health, or null when it is not running.</summary>
    public async Task<JsonNode?> AskBridgeHealthAsync()
    {
        try { return JsonNode.Parse(await _http.GetStringAsync(AskBridgeUrl + "/health")); }
        catch { return null; }
    }

    public void Dispose()
    {
        _http.Dispose();
        if (_kb.IsValueCreated) _kb.Value?.Dispose();
    }

    public static readonly JsonSerializerOptions Json = new(KbJson.Indented);

    public static IReadOnlyDictionary<string, string>? ParseLabels(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        return label.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Split(':', 2)).Where(x => x.Length == 2 && Families.All.Contains(x[0], StringComparer.OrdinalIgnoreCase))
            .ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase);
    }
}
