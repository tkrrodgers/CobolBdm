using System.Text;
using MlCpu.SemanticKb.Embedding;
using MlCpu.SemanticKb.Labels;
using MlCpu.SemanticKb.Search;

namespace MlCpu.SemanticKb.Grounding;

public sealed record GroundRequest(string Question, IReadOnlyList<string>? Projects = null, string Mode = "answer", string? Program = null, int K = 12, int ContextTokens = 24_000);

public sealed record GroundContext(string Cite, string ChunkId, string Project, string Program, string Kind, string Title, string Path, string AbsolutePath,
    int StartLine, int EndLine, IReadOnlyDictionary<string, string> Labels, string Text, int Tokens);

public sealed record GroundTerm(string Term, string Definition);

public sealed record ChatMessage(string Role, string Content);

public sealed record GroundingPacket(string Schema, string Id, string CreatedAt, string Question, string Mode, IReadOnlyList<string> Projects, string? Program,
    IReadOnlyDictionary<string, string> Filters, IReadOnlyList<GroundContext> Contexts, IReadOnlyList<GroundTerm> Glossary,
    IReadOnlyDictionary<string, string>? ProgramFacts, int ContextTokens, int Limit, IReadOnlyList<ChatMessage> Messages);

/// <summary>
/// The Phase 1 → Phase 2 seam (knowledgeBaseGroundGemma4.md §11.3): turns a question into a grounding packet for Gemma 4 —
/// numbered contexts with citations from hybrid search (answer mode) or every chunk of one program (document mode), the
/// glossary definitions of the financial terms the question and contexts use, and the chat messages built from the
/// prompts in knowledge/prompts. It calls no model.
/// </summary>
public sealed class GroundingBuilder(KbSearch search, Glossary glossary, Taxonomy taxonomy, GemmaTokenizer tokenizer, string promptsDir)
{
    static readonly string[] DocumentOrder = ["program", "design", "io", "rule", "paragraph", "quirk", "data", "java", "example"];

    public GroundingPacket Build(GroundRequest req)
    {
        var contexts = new List<GroundContext>();
        int used = 0;
        bool Add(KbChunk c)
        {
            if (contexts.Any(x => x.ChunkId == c.Id)) return true;
            var tokens = c.Tokens > 0 ? c.Tokens : tokenizer.Count(c.Title + "\n" + c.Text);
            if (used + tokens > req.ContextTokens) return false;
            used += tokens;
            var ix = search.Indexes.First(i => i.Manifest.Project == c.Project);
            contexts.Add(new GroundContext($"C{contexts.Count + 1}", c.Id, c.Project, c.Program, c.Kind, c.Title, c.Path, search.AbsolutePath(ix, c),
                c.StartLine, c.EndLine, c.Labels.ToDictionary(kv => kv.Key, kv => $"{kv.Value.Value} ({kv.Value.Source})"), c.Text, tokens));
            return true;
        }

        IReadOnlyDictionary<string, string> filters = new Dictionary<string, string>();
        Dictionary<string, string>? facts = null;
        var projects = req.Projects?.ToList() ?? search.Indexes.Select(i => i.Manifest.Project).ToList();
        if (req.Mode == "document" && req.Program is { Length: > 0 })
        {
            var chunks = search.Chunks.Where(c => c.Program.Equals(req.Program, StringComparison.OrdinalIgnoreCase) && (projects.Count == 0 || projects.Contains(c.Project)))
                .OrderBy(c => Array.IndexOf(DocumentOrder, c.Kind)).ThenBy(c => c.Kind == "java" && c.Fact("origin") == "faithful" ? 1 : 0)
                .ThenBy(c => c.Label(Families.Role) == "business-logic" ? 0 : 1).ThenBy(c => c.StartLine).ToList();
            // Faithful Java repeats its paragraph; it goes in only when the budget is left over.
            foreach (var c in chunks.Where(c => !(c.Kind == "java" && c.Fact("origin") == "faithful"))) if (!Add(c) && c.Kind is "program" or "design") break;
            foreach (var c in chunks.Where(c => c.Kind == "java" && c.Fact("origin") == "faithful")) if (!Add(c)) break;
            var summary = chunks.FirstOrDefault(c => c.Kind == "program");
            facts = new Dictionary<string, string>
            {
                ["program"] = req.Program.ToUpperInvariant(),
                ["project"] = summary?.Project ?? "",
                ["kind"] = summary?.Fact("programKind") ?? "",
                ["headerFunction"] = summary?.Fact("function") ?? "",
                ["businessFunction"] = summary?.Label(Families.Function) is { } f ? taxonomy.Name(f) : "",
                ["rules"] = chunks.Count(c => c.Kind == "rule").ToString(),
                ["paragraphs"] = chunks.Count(c => c.Kind == "paragraph").ToString(),
                ["chunksInProgram"] = chunks.Count.ToString(),
                ["chunksInPacket"] = contexts.Count.ToString()
            };
        }
        else
        {
            var res = search.Search(new SearchRequest(req.Question, projects, req.K, Program: req.Program, MaxPerAnchor: 2));
            filters = res.Filters;
            foreach (var h in res.Hits) Add(h.Chunk);
            // The other views of the three best paragraphs (rule ↔ COBOL ↔ Java) when the budget allows.
            foreach (var h in res.Hits.Take(3))
                foreach (var s in search.Anchor(h.Chunk.Anchor).Where(s => s.Kind is "rule" or "paragraph" or "java").Take(4))
                    Add(s);
        }

        var terms = glossary.Find(req.Question + "\n" + string.Join("\n", contexts.Select(c => c.Title + "\n" + c.Text)))
            .Where(h => h.Term.Definition.Length > 0)
            .OrderByDescending(h => req.Question.Contains(h.Term.Term, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(h => h.Count * h.Term.Weight)
            .Take(30).Select(h => new GroundTerm(h.Term.Term, h.Term.Definition)).ToList();

        var system = Prompt("answer.md");
        if (req.Mode == "document") system += "\n\n" + Prompt("document-program.md");
        var user = new StringBuilder();
        user.AppendLine(req.Mode == "document" ? $"Document program {req.Program} for a financial audience." : $"Question: {req.Question}");
        user.AppendLine();
        user.AppendLine("Financial terms (use them as defined):");
        foreach (var t in terms) user.AppendLine($"- {t.Term}: {t.Definition}");
        user.AppendLine();
        user.AppendLine("Contexts:");
        foreach (var c in contexts)
        {
            user.AppendLine();
            var where = c.StartLine > 0 ? $"{c.Path}:{c.StartLine}-{c.EndLine}" : c.Path;
            user.AppendLine($"[{c.Cite}] {c.Project} · {c.Title} · {where} · {string.Join(", ", c.Labels.Select(kv => $"{kv.Key}={kv.Value}"))}");
            user.AppendLine(c.Text);
        }
        if (facts is not null)
        {
            user.AppendLine();
            user.AppendLine("Program facts: " + string.Join("; ", facts.Select(kv => $"{kv.Key} = {kv.Value}")));
        }

        var id = KbJson.Sha256(req.Mode + "\n" + req.Program + "\n" + req.Question + "\n" + string.Join(",", contexts.Select(c => c.ChunkId)))[..16];
        return new GroundingPacket("mlcpu.kb-grounding.v1", id, DateTime.Now.ToString("s"), req.Question, req.Mode, projects, req.Program, filters,
            contexts, terms, facts, used, req.ContextTokens, [new ChatMessage("system", system), new ChatMessage("user", user.ToString().TrimEnd())]);
    }

    string Prompt(string name)
    {
        var path = Path.Combine(promptsDir, name);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
    }
}
