using MlCpu.SemanticKb.Embedding;
using MlCpu.SemanticKb.Grounding;
using MlCpu.SemanticKb.Index;
using MlCpu.SemanticKb.Labels;
using MlCpu.SemanticKb.Search;

namespace MlCpu.SemanticKb;

/// <summary>
/// The loaded knowledge base, shared by the CLI and the workbench: glossary, taxonomy, the project indexes of a workspace,
/// a lazily created EmbeddingGemma (same variant as the indexes, so query and document vectors share a space), search and
/// grounding. <see cref="Reload"/> after a build.
/// </summary>
public sealed class KbRuntime(KbPaths paths, string workspace, int queryThreads = 2) : IDisposable
{
    readonly object _gate = new();
    EmbeddingGemma? _embedder;
    KbSearch? _search;

    public KbPaths Paths { get; } = paths;
    public string Workspace { get; } = Path.GetFullPath(workspace);
    public Glossary Glossary { get; } = Glossary.Load(paths.Glossary);
    public Taxonomy Taxonomy { get; } = Taxonomy.Load(paths.Taxonomy);

    /// <summary>Project ids under the workspace that have a built index.</summary>
    public IReadOnlyList<string> Indexed() => Directory.Exists(Workspace)
        ? Directory.GetDirectories(Workspace).Where(d => KbIndex.Exists(Path.Combine(d, "knowledge"))).Select(Path.GetFileName).OfType<string>().OrderBy(x => x).ToList()
        : [];

    public KbSearch Search
    {
        get
        {
            lock (_gate)
            {
                if (_search is not null) return _search;
                var indexes = Indexed().Select(id => KbIndex.Load(Path.Combine(Workspace, id, "knowledge"))).ToList();
                var variants = indexes.Select(i => i.Manifest.Variant).Distinct().ToList();
                if (variants.Count > 1) throw new InvalidOperationException($"indexes were built with different EmbeddingGemma variants ({string.Join(", ", variants)}); rebuild them with one");
                var variant = variants.FirstOrDefault() ?? "fp32";
                return _search = new KbSearch(indexes, Glossary, q => Embedder(variant).EmbedQuery(q));
            }
        }
    }

    public EmbeddingGemma Embedder(string variant)
    {
        lock (_gate)
        {
            if (_embedder is not null && _embedder.Variant == variant) return _embedder;
            _embedder?.Dispose();
            return _embedder = new EmbeddingGemma(Paths.ModelDir, variant, queryThreads);
        }
    }

    public GroundingBuilder Grounding() =>
        new(Search, Glossary, Taxonomy, Embedder(Search.Indexes.FirstOrDefault()?.Manifest.Variant ?? "fp32").Tokenizer, Paths.Prompts);

    public void Reload()
    {
        lock (_gate) _search = null;
    }

    public void Dispose() => _embedder?.Dispose();
}
