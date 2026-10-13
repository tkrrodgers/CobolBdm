using MlCpu.SemanticKb.Grounding;
using MlCpu.SemanticKb.Search;

namespace MlCpu.Host.Services.Knowledge;

/// <summary>Headless knowledge-base API (knowledgeBaseGroundGemma4.md §10). POST /api/kb/ground is the Phase 2 seam the
/// VSIX worker calls: it returns the grounding packet, chat messages included, and calls no model.</summary>
public static class KnowledgeEndpoints
{
    public static void MapKnowledgeApi(this WebApplication app)
    {
        var kb = app.MapGroup("/api/kb").DisableAntiforgery();
        var json = KnowledgeService.Json;

        kb.MapGet("/status", async (KnowledgeService k) => Results.Json(new
        {
            problem = k.Problem,
            workspace = k.Workspace,
            setDir = k.SetDir,
            modelDir = k.Paths?.ModelDir,
            models = new { fp32 = k.ModelPresent("fp32"), q8 = k.ModelPresent("q8") },
            indexes = k.Indexed().Select(id => k.Manifest(id)).Where(m => m is not null).Select(m => new
            {
                m!.Project, m.BuiltAt, m.Variant, m.Chunks, m.Tokens, m.Kinds, m.Embedded, m.CacheHits, m.EmbedSeconds, m.Chunker, m.Revision
            }),
            askBridge = new { url = k.AskBridgeUrl, health = await k.AskBridgeHealthAsync() }
        }, json));

        kb.MapPost("/build", (string? projects, string? variant, bool? rebuild, KnowledgeService k) =>
        {
            var list = string.IsNullOrWhiteSpace(projects) ? KnowledgeService.DefaultProjects
                : projects.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var op = k.StartBuild(list, variant ?? "fp32", rebuild ?? false);
            return Results.Json(new { op.Id, op.ProjectId, op.Kind, op.Title, op.Running, op.Status, poll = $"/api/operations/{op.Id}" });
        });

        kb.MapGet("/search", (string q, string? projects, int? k, string? mode, string? kind, string? program, string? label, KnowledgeService svc) =>
        {
            if (svc.Kb is null) return Results.Problem(svc.Problem ?? "knowledge base unavailable");
            if (svc.Indexed().Count == 0) return Results.Problem("no project has a knowledge index yet: POST /api/kb/build");
            var list = string.IsNullOrWhiteSpace(projects) ? null : projects.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Results.Json(svc.Kb.Search.Search(new SearchRequest(q, list, Math.Clamp(k ?? 10, 1, 50), mode ?? "hybrid", kind, program, KnowledgeService.ParseLabels(label))), json);
        });

        kb.MapGet("/chunk/{*id}", (string id, KnowledgeService svc) =>
            svc.Kb?.Search.Chunks.FirstOrDefault(c => c.Id == id) is { } c ? Results.Json(c, json) : Results.NotFound());

        kb.MapGet("/catalog/{project}", (string project, KnowledgeService svc) =>
            svc.Catalog(project) is { } c ? Results.Json(c, json) : Results.NotFound(new { error = $"no catalog for {project}: build the knowledge base" }));

        kb.MapGet("/classification", (KnowledgeService svc) => svc.Classification() is { } c ? Results.Json(c, json) : Results.NotFound());

        kb.MapGet("/glossary", (string? term, KnowledgeService svc) =>
        {
            if (svc.Kb is null) return Results.Problem(svc.Problem ?? "knowledge base unavailable");
            var use = svc.Indexed().Count > 0 ? svc.TermUse() : new Dictionary<string, int>();
            var terms = svc.Kb.Glossary.Terms.Where(t => string.IsNullOrWhiteSpace(term) || t.Term.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || t.Aliases.Any(a => a.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .Select(t => new { t.Id, t.Term, t.Aliases, t.CodeTerms, t.Definition, t.Domains, t.Functions, t.Source, usedBy = use.GetValueOrDefault(t.Id) });
            return Results.Json(new { reviewed = svc.Kb.Glossary.File.Reviewed, terms }, json);
        });

        kb.MapPost("/ground", (GroundRequest req, KnowledgeService svc) =>
        {
            if (svc.Kb is null) return Results.Problem(svc.Problem ?? "knowledge base unavailable");
            if (string.IsNullOrWhiteSpace(req.Question) && req.Mode != "document") return Results.BadRequest(new { error = "question is required" });
            if (req.Mode == "document" && string.IsNullOrWhiteSpace(req.Program)) return Results.BadRequest(new { error = "document mode needs a program" });
            return Results.Json(svc.Kb.Grounding().Build(req with { K = Math.Clamp(req.K, 1, 40), ContextTokens = Math.Clamp(req.ContextTokens, 1000, 100_000) }), json);
        });
    }
}
