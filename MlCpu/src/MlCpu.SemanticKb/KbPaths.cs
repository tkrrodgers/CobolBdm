namespace MlCpu.SemanticKb;

/// <summary>Where the knowledge base finds its authored assets and the embedding model.</summary>
public sealed record KbPaths(string KnowledgeRoot, string ModelDir)
{
    public const string DefaultModelDir = @"C:\models\embeddinggemma-300m-ONNX";

    public string Glossary => Path.Combine(KnowledgeRoot, "glossary", "financial-terms.json");
    public string Taxonomy => Path.Combine(KnowledgeRoot, "taxonomy.json");
    public string GoldQuestions => Path.Combine(KnowledgeRoot, "gold", "questions.json");
    public string Fixture => Path.Combine(KnowledgeRoot, "fixtures", "embeddinggemma-reference.json");
    public string Prompts => Path.Combine(KnowledgeRoot, "prompts");

    /// <summary>knowledge/ found by walking up from <paramref name="start"/> (the repository root holds it).</summary>
    public static KbPaths Discover(string start, string? modelDir = null)
    {
        for (var d = new DirectoryInfo(Path.GetFullPath(start)); d is not null; d = d.Parent)
        {
            var k = Path.Combine(d.FullName, "knowledge");
            if (File.Exists(Path.Combine(k, "taxonomy.json"))) return new KbPaths(k, modelDir ?? DefaultModelDir);
        }
        throw new DirectoryNotFoundException($"no knowledge/taxonomy.json above {start}");
    }
}
