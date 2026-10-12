using System.Diagnostics;
using System.Text;
using MlCpu.SemanticKb.Catalog;
using MlCpu.SemanticKb.Classify;
using MlCpu.SemanticKb.Embedding;
using MlCpu.SemanticKb.Harvest;
using MlCpu.SemanticKb.Labels;

namespace MlCpu.SemanticKb.Index;

public sealed record BuildOptions(string Variant = "fp32", int Workers = 8, int MaxTokens = 1024, bool Classify = true, bool Rebuild = false, int Folds = 5);

public sealed record BuildResult(IReadOnlyList<KbManifest> Projects, IReadOnlyList<ClassifierReport> Classifiers, string SetDir);

/// <summary>
/// Builds the knowledge base for a set of projects: harvest → embed (EmbeddingGemma through ML.NET, content-hash cache)
/// → classify (ML.NET, trained on the pooled labels of all projects in the set, predictions fill the gaps) → catalog →
/// save per project (projects/&lt;id&gt;/knowledge) and the set's classifiers and report (&lt;setDir&gt;).
/// </summary>
public sealed class KbBuilder(KbPaths paths, BuildOptions options)
{
    public BuildResult Build(string workspace, IReadOnlyList<string> projectIds, string setDir, EmbeddingGemma embedder,
        Action<string> log, Action<double, string>? progress = null, CancellationToken ct = default)
    {
        var glossary = Glossary.Load(paths.Glossary);
        var taxonomy = Taxonomy.Load(paths.Taxonomy);
        var harvester = new Harvester(glossary, embedder.Tokenizer, options.MaxTokens);

        // 1. Harvest.
        var sets = new List<(KbProject Project, HarvestResult Harvest, KbManifest Manifest)>();
        foreach (var id in projectIds)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            var proj = KbProject.Load(workspace, id);
            progress?.Invoke(0.02 * sets.Count / projectIds.Count, $"harvesting {id}");
            var h = harvester.Harvest(proj);
            var m = new KbManifest
            {
                Project = id, Chunker = Harvester.Version, Model = EmbeddingGemma.Repository, Revision = EmbeddingGemma.Revision,
                Variant = options.Variant, Dimensions = EmbeddingGemma.Dimensions, Chunks = h.Chunks.Count, Tokens = h.Chunks.Sum(c => (long)c.Tokens),
                Kinds = h.Counts, Sources = h.Sources, HarvestSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1)
            };
            log($"{id}: {h.Chunks.Count} chunks ({string.Join(", ", h.Counts.Select(kv => $"{kv.Key} {kv.Value}"))}), {m.Tokens:N0} tokens from {h.Sources.Count} inputs");
            sets.Add((proj, h, m));
        }

        // 2. Embed, project by project, through the cache.
        long totalTokens = sets.Sum(s => s.Manifest.Tokens), doneTokens = 0;
        var vectors = new Dictionary<string, float[][]>();
        foreach (var (proj, h, m) in sets)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            var cache = new EmbeddingCache(Path.Combine(proj.KnowledgeDir, $"embed-cache.{options.Variant}.bin"));
            var texts = h.Chunks.Select(c => EmbeddingGemma.Document(c.Title, c.Text)).ToList();
            var keys = texts.Select(t => EmbeddingCache.Key(options.Variant, t)).ToList();
            var result = new float[texts.Count][];
            var missing = new List<int>();
            for (int i = 0; i < texts.Count; i++)
                if (!options.Rebuild && cache.TryGet(keys[i], out var v)) result[i] = v; else missing.Add(i);
            doneTokens += h.Chunks.Where((_, i) => result[i] is not null).Sum(c => (long)c.Tokens);
            log($"{proj.Id}: embedding {missing.Count} of {texts.Count} chunks ({texts.Count - missing.Count} cached), {options.Workers} workers, EmbeddingGemma {options.Variant}");
            long lastReport = 0;
            object gate = new();
            var embedded = embedder.EmbedMany(missing.Select(i => texts[i]).ToList(), options.Workers, k =>
            {
                lock (gate)
                {
                    doneTokens += h.Chunks[missing[k]].Tokens;
                    if (doneTokens - lastReport < 20_000) return;
                    lastReport = doneTokens;
                }
                progress?.Invoke(0.02 + 0.88 * doneTokens / Math.Max(1, totalTokens), $"embedding {proj.Id}: {doneTokens:N0} of {totalTokens:N0} tokens");
            }, ct);
            for (int k = 0; k < missing.Count; k++) { result[missing[k]] = embedded[k]; cache.Put(keys[missing[k]], embedded[k]); }
            cache.Save();
            m.Embedded = missing.Count;
            m.CacheHits = texts.Count - missing.Count;
            m.EmbedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
            vectors[proj.Id] = result;
            var missingTokens = missing.Sum(i => (long)h.Chunks[i].Tokens);
            log($"{proj.Id}: embedded {missingTokens:N0} tokens in {sw.Elapsed.TotalSeconds:N0}s ({missingTokens / Math.Max(0.1, sw.Elapsed.TotalSeconds):N0} tokens/s)");
        }

        // 3. Classify: one ML.NET model per family over the pooled labels of the set; predictions fill unlabeled chunks.
        var reports = new List<ClassifierReport>();
        if (options.Classify)
        {
            progress?.Invoke(0.91, "training classifiers");
            var sw = Stopwatch.StartNew();
            var all = sets.SelectMany(s => s.Harvest.Chunks.Select((c, i) => (Chunk: c, Vector: vectors[s.Project.Id][i]))).ToList();
            foreach (var family in Families.All)
            {
                ct.ThrowIfCancellationRequested();
                var protos = taxonomy.Descriptions(family).ToDictionary(kv => kv.Key, kv => embedder.Embed(EmbeddingGemma.Document(kv.Key, kv.Value)));
                // role is the statement composition the pipeline saw, so it also gets the verb-share features; rule kinds are
                // derived from those same verbs, so they (and function) learn from the embedding alone and transfer to idiomatic Java.
                bool structure = family == Families.Role;
                float[] Features(KbChunk c, float[] v) => structure ? [.. v, .. StructureFeatures.Compute(c)] : v;
                var examples = all.Where(x => x.Chunk.Labels.TryGetValue(family, out var l) && l.Source != "model" && protos.ContainsKey(l.Value))
                    .Select(x => new LabeledExample(Features(x.Chunk, x.Vector), x.Chunk.Labels[family].Value, $"{x.Chunk.Project}/{x.Chunk.Program}")).ToList();
                var source = family switch { Families.Role => "pipeline", Families.RuleKind => "rule", _ => "glossary (weak)" };
                var featureNote = structure ? $"EmbeddingGemma 768-d + {StructureFeatures.Length} statement-composition features" : "EmbeddingGemma 768-d";
                using var model = LabelClassifier.Train(family, source, featureNote, examples, protos, structure ? ["sdca", "lightgbm"] : ["sdca"], options.Folds);
                if (model is null) { log($"classifier {family}: too few labels ({examples.Count})"); continue; }
                reports.Add(model.Report);
                model.Save(Path.Combine(setDir, "classifiers", family + ".zip"));
                int predicted = 0;
                foreach (var (c, v) in all)
                {
                    if (!Families.AppliesTo(family, c.Kind) || c.Labels.ContainsKey(family)) continue;
                    var (label, p) = model.Predict(Features(c, v));
                    c.Labels[family] = new KbLabel(label, "model", p);
                    predicted++;
                }
                var r = model.Report;
                log($"classifier {family}: {r.Examples} examples, {r.Classes.Count} classes, {r.Folds}-fold CV by program, deployed {r.Trainer}: micro {r.MicroAccuracy:P1}, macro {r.MacroAccuracy:P1} " +
                    $"[candidates {string.Join("; ", r.Candidates.Select(x => $"{x.Trainer} {x.MicroAccuracy:P1}/{x.MacroAccuracy:P1}"))}] " +
                    $"(zero-shot prototypes: micro {r.PrototypeMicroAccuracy:P1}, macro {r.PrototypeMacroAccuracy:P1}); predicted {predicted} unlabeled chunks");
            }
            foreach (var s in sets) s.Manifest.ClassifySeconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
            KbJson.Write(Path.Combine(setDir, "classification.json"), reports);
            File.WriteAllText(Path.Combine(setDir, "classification-report.md"), ReportMarkdown(reports, sets.Select(s => s.Project.Id).ToList(), taxonomy));
        }

        // 4. Catalog and save.
        progress?.Invoke(0.97, "writing indexes and catalogs");
        foreach (var (proj, h, m) in sets)
        {
            m.BuiltAt = DateTime.Now.ToString("s");
            KbIndex.Save(proj.KnowledgeDir, m, h.Chunks, vectors[proj.Id]);
            var catalog = CatalogBuilder.Build(proj.Id, h.Chunks, taxonomy);
            KbJson.Write(Path.Combine(proj.KnowledgeDir, "catalog.json"), catalog);
            File.WriteAllText(Path.Combine(proj.KnowledgeDir, "catalog.md"), CatalogBuilder.Markdown(catalog, taxonomy));
            log($"{proj.Id}: index and catalog written to {proj.KnowledgeDir}");
        }
        KbJson.Write(Path.Combine(setDir, "set.json"), new { schema = "mlcpu.kb-set.v1", builtAt = DateTime.Now.ToString("s"), projects = projectIds, workspace, variant = options.Variant });
        return new BuildResult(sets.Select(s => s.Manifest).ToList(), reports, setDir);
    }

    static string ReportMarkdown(IReadOnlyList<ClassifierReport> reports, IReadOnlyList<string> projects, Taxonomy taxonomy)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Knowledge base classifiers");
        sb.AppendLine();
        sb.AppendLine($"Projects: {string.Join(", ", projects)}. Generated {DateTime.Now:s}. Features: EmbeddingGemma 300M sentence embeddings (768-d), plus statement composition for role. " +
                      "Cross-validation folds are grouped by program, so no program is on both sides of a fold. The zero-shot baseline assigns the class whose " +
                      "description embedding is nearest (no training).");
        foreach (var r in reports)
        {
            sb.AppendLine();
            sb.AppendLine($"## {r.Family}");
            sb.AppendLine();
            sb.AppendLine($"- Training labels: {r.LabelSource}; {r.Examples} examples from {r.Groups} programs; trainer {r.Trainer}; features: {r.Features}.");
            sb.AppendLine($"- {r.Folds}-fold CV, pooled over the held-out rows: micro accuracy **{r.MicroAccuracy:P1}**, macro accuracy (mean recall) **{r.MacroAccuracy:P1}**; " +
                          $"ML.NET fold averages: micro {r.FoldMicroAccuracy:P1}, macro {r.FoldMacroAccuracy:P1}, log-loss {r.LogLoss:N3}.");
            sb.AppendLine($"- Candidates (pooled micro / macro): {string.Join("; ", r.Candidates.Select(x => $"{x.Trainer} {x.MicroAccuracy:P1} / {x.MacroAccuracy:P1}"))}; deployed: {r.Trainer}.");
            sb.AppendLine($"- Zero-shot prototypes: micro {r.PrototypeMicroAccuracy:P1}, macro {r.PrototypeMacroAccuracy:P1}.");
            if (r.Family == Families.Function)
                sb.AppendLine("- These labels are weak (glossary terms), so CV measures agreement with the glossary, not accuracy. The SME sample measures accuracy.");
            sb.AppendLine();
            sb.AppendLine("| Class | Support | Precision | Recall | F1 |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var c in r.PerClass) sb.AppendLine($"| {c.Label} | {c.Support} | {c.Precision:N3} | {c.Recall:N3} | {c.F1:N3} |");
            sb.AppendLine();
            sb.AppendLine("Confusion (rows = truth, columns = predicted, pooled over the held-out folds):");
            sb.AppendLine();
            sb.AppendLine("| | " + string.Join(" | ", r.ConfusionLabels.Select((_, i) => $"p{i + 1}")) + " |");
            sb.AppendLine("|---|" + string.Concat(r.ConfusionLabels.Select(_ => "---|")));
            for (int i = 0; i < r.ConfusionLabels.Count; i++)
                sb.AppendLine($"| p{i + 1} {r.ConfusionLabels[i]} | " + string.Join(" | ", r.Confusion[i]) + " |");
        }
        return sb.ToString();
    }
}
