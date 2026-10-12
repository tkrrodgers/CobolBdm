using System.Diagnostics;
using System.Text.Json;
using MlCpu.SemanticKb;
using MlCpu.SemanticKb.Embedding;
using MlCpu.SemanticKb.Eval;
using MlCpu.SemanticKb.Grounding;
using MlCpu.SemanticKb.Harvest;
using MlCpu.SemanticKb.Index;
using MlCpu.SemanticKb.Labels;
using MlCpu.SemanticKb.Search;

// kb: the semantic knowledge base over CobolBdm's published projects (knowledgeBaseGroundGemma4.md §13).
var cmd = args.Length > 0 ? args[0] : "help";
var opts = Options(args.Skip(1).ToArray());
string Opt(string key, string def) => opts.TryGetValue(key, out var v) ? v : def;
bool Flag(string key) => opts.ContainsKey(key);
var paths = KbPaths.Discover(Opt("knowledge", Directory.GetCurrentDirectory()), opts.GetValueOrDefault("model-dir"));
var repoRoot = Path.GetFullPath(Path.Combine(paths.KnowledgeRoot, ".."));
var workspace = Path.GetFullPath(Opt("workspace", Path.Combine(repoRoot, "projects")));
var projects = Opt("projects", "carddemo,zopeneditor-sample,fineract-legacy").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var variant = Opt("variant", "fp32");
var query = opts.GetValueOrDefault("_") ?? "";

try
{
    switch (cmd)
    {
        case "model":
            foreach (var v in variant == "all" ? new[] { "fp32", "q8" } : [variant])
                await ModelStore.EnsureAsync(paths.ModelDir, v, Console.WriteLine);
            break;

        case "verify-model":
            return VerifyModel(paths, variant) ? 0 : 1;

        case "harvest":
        {
            var harvester = new Harvester(Glossary.Load(paths.Glossary), new GemmaTokenizer(Path.Combine(paths.ModelDir, "tokenizer.model")), int.Parse(Opt("max-tokens", "1024")));
            foreach (var id in projects)
            {
                var sw = Stopwatch.StartNew();
                var r = harvester.Harvest(KbProject.Load(workspace, id), Console.WriteLine);
                Console.WriteLine($"{id}: {r.Chunks.Count} chunks, {r.Sources.Count} inputs, {r.Chunks.Sum(c => c.Tokens):N0} tokens, {sw.Elapsed.TotalSeconds:N1}s");
                Console.WriteLine("  kinds: " + string.Join(", ", r.Counts.Select(kv => $"{kv.Key} {kv.Value}")));
                foreach (var f in Families.All)
                    Console.WriteLine($"  {f}: " + string.Join(", ", r.Chunks.Where(c => c.Labels.ContainsKey(f)).GroupBy(c => c.Labels[f].Value).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
                if (opts.TryGetValue("dump", out var dump))
                {
                    Directory.CreateDirectory(dump);
                    File.WriteAllLines(Path.Combine(dump, id + ".chunks.jsonl"), r.Chunks.Select(c => JsonSerializer.Serialize(c, KbJson.Options)));
                }
            }
            break;
        }

        case "build":
        {
            var sw = Stopwatch.StartNew();
            if (opts.TryGetValue("prior", out var prior)) FunctionLabeler.PriorTop = double.Parse(prior, System.Globalization.CultureInfo.InvariantCulture);
            using var proc = Process.GetCurrentProcess();
            proc.PriorityClass = ProcessPriorityClass.BelowNormal;   // the machine is shared with the model runs
            var options = new BuildOptions(variant, int.Parse(Opt("threads", "8")), int.Parse(Opt("max-tokens", "1024")), !Flag("no-classify"), Flag("rebuild"));
            var setDir = Path.GetFullPath(Opt("set-dir", Path.Combine(workspace, "..", "work", "kb")));
            using var embedder = new EmbeddingGemma(paths.ModelDir, variant);
            var result = new KbBuilder(paths, options).Build(workspace, projects, setDir, embedder, Console.WriteLine,
                (p, s) => Console.Title = $"kb build {p:P0} {s}");
            Console.WriteLine($"built {result.Projects.Count} project(s) in {sw.Elapsed.TotalMinutes:N1} min; classifiers and report in {setDir}");
            break;
        }

        case "search":
        {
            using var kb = new KbRuntime(paths, workspace);
            var res = kb.Search.Search(new SearchRequest(query, opts.ContainsKey("projects") ? projects : null, int.Parse(Opt("k", "10")), Opt("mode", "hybrid"),
                opts.GetValueOrDefault("kind"), opts.GetValueOrDefault("program")));
            if (Flag("json")) { Console.WriteLine(JsonSerializer.Serialize(res, KbJson.Indented)); break; }
            Console.WriteLine($"\"{res.Text}\" {(res.Filters.Count > 0 ? "filters " + string.Join(" ", res.Filters.Select(kv => $"{kv.Key}:{kv.Value}")) : "")} ({res.Milliseconds} ms)");
            if (res.Expansion.Count > 0) Console.WriteLine("  glossary expansion: " + string.Join(" ", res.Expansion.Take(20)));
            int n = 0;
            foreach (var h in res.Hits)
            {
                var labels = string.Join(" ", h.Chunk.Labels.Select(kv => $"{kv.Key}={kv.Value.Value}{(kv.Value.Source == "model" ? "*" : "")}"));
                Console.WriteLine($"{++n,2}. {h.Score:F4} d{h.DenseRank?.ToString() ?? "-"}/l{h.LexicalRank?.ToString() ?? "-"} [{h.Chunk.Project}] {h.Chunk.Title}");
                Console.WriteLine($"      {h.Chunk.Path}{(h.Chunk.StartLine > 0 ? $":{h.Chunk.StartLine}-{h.Chunk.EndLine}" : "")}  {labels}");
            }
            break;
        }

        case "eval":
        {
            using var kb = new KbRuntime(paths, workspace);
            var gold = KbJson.Read<GoldFile>(Opt("gold", paths.GoldQuestions));
            if (opts.TryGetValue("prose-lexical", out var w)) KbSearch.LexicalWeightForProse = double.Parse(w, System.Globalization.CultureInfo.InvariantCulture);
            var modes = Opt("modes", "dense,lexical,hybrid").Split(',');
            var (scores, results) = RetrievalEval.Run(kb.Search, gold, modes);
            var v = kb.Search.Indexes.FirstOrDefault()?.Manifest.Variant ?? variant;
            var md = RetrievalEval.Markdown(scores, results, gold, v);
            var outDir = Path.GetFullPath(Opt("out", Path.Combine(workspace, "..", "work", "kb", "eval")));
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, $"retrieval-{v}.md"), md);
            KbJson.Write(Path.Combine(outDir, $"retrieval-{v}.json"), new { variant = v, scores, results });
            foreach (var s in scores)
                Console.WriteLine($"{s.Mode,-8} R@1 {s.RecallAt1:N3}  R@5 {s.RecallAt5:N3}  R@10 {s.RecallAt10:N3}  MRR@10 {s.Mrr10:N3}   " + string.Join(", ", s.RecallAt5ByProject.Select(kv => $"{kv.Key} {kv.Value:N2}"))
                    + "   by kind R@5/MRR: " + string.Join(", ", s.RecallAt5ByKind.Select(kv => $"{kv.Key} {kv.Value:N2}/{s.Mrr10ByKind[kv.Key]:N2}")));
            Console.WriteLine($"report: {Path.Combine(outDir, $"retrieval-{v}.md")}");
            var labelsGold = Path.Combine(paths.KnowledgeRoot, "gold", "function-labels.tsv");
            if (File.Exists(labelsGold))
            {
                var le = LabelEval.Run(kb.Search.Chunks, labelsGold);
                Console.WriteLine($"function labels vs SME sample: {le.Correct}/{le.Found} = {le.Accuracy:P1} (" +
                    string.Join(", ", le.BySource.Select(kv => $"{kv.Key} {kv.Value.Correct}/{kv.Value.Items}")) + ")");
                foreach (var m in le.Misses) Console.WriteLine($"  miss {m.ChunkId}: got {m.Got ?? "-"} ({m.Source}), SME {m.Accepted}");
                KbJson.Write(Path.Combine(outDir, "function-labels.json"), new { le.Items, le.Found, le.Correct, le.Accuracy, bySource = le.BySource.ToDictionary(kv => kv.Key, kv => new { kv.Value.Items, kv.Value.Correct }), misses = le.Misses.Select(m => new { m.ChunkId, m.Accepted, m.Got, m.Source }) });
            }
            break;
        }

        case "ground":
        {
            using var kb = new KbRuntime(paths, workspace);
            var packet = kb.Grounding().Build(new GroundRequest(query, opts.ContainsKey("projects") ? projects : null, Opt("mode", "answer"),
                opts.GetValueOrDefault("program"), int.Parse(Opt("k", "12")), int.Parse(Opt("context-tokens", "24000"))));
            var json = JsonSerializer.Serialize(packet, KbJson.Indented);
            if (opts.TryGetValue("out", out var outFile)) { File.WriteAllText(outFile, json); Console.WriteLine($"{packet.Contexts.Count} contexts, {packet.ContextTokens:N0} tokens, {packet.Glossary.Count} terms → {outFile}"); }
            else Console.WriteLine(json);
            break;
        }

        case "terms":
        {
            // Glossary diagnostics for SME tuning: the terms a text uses, its function scores, and the label a chunk would get.
            var glossary = Glossary.Load(paths.Glossary);
            var labeler = new FunctionLabeler(glossary);
            foreach (var h in glossary.Find(query)) Console.WriteLine($"  {h.Term.Id,-28} x{h.Count}  weight {h.Term.Weight}  → {string.Join(", ", h.Term.Functions)}");
            Console.WriteLine("scores: " + string.Join(", ", labeler.Scores(query).OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:N2}")));
            var prior = labeler.Prior(query, "");
            Console.WriteLine($"as a program's Function line: {FunctionLabeler.Top(prior)?.Value ?? "(no clear class)"}; as chunk text: {labeler.Label(query, new Dictionary<string, double>())?.Value ?? "(no weak label)"}");
            break;
        }

        case "bench":
        {
            var tok = new GemmaTokenizer(Path.Combine(paths.ModelDir, "tokenizer.model"));
            var chunks = new Harvester(Glossary.Load(paths.Glossary), tok).Harvest(KbProject.Load(workspace, projects[0])).Chunks;
            var sample = chunks.Where((_, i) => i % Math.Max(1, chunks.Count / int.Parse(Opt("n", "96"))) == 0).ToList();
            var texts = sample.Select(c => EmbeddingGemma.Document(c.Title, c.Text)).ToList();
            var tokens = sample.Sum(c => c.Tokens);
            foreach (var cfg in Opt("configs", "8x1").Split(','))
            {
                var (w, t) = (int.Parse(cfg.Split('x')[0]), int.Parse(cfg.Split('x')[1]));
                using var e = new EmbeddingGemma(paths.ModelDir, variant, t);
                e.Embed("warm up");
                var sw = Stopwatch.StartNew();
                e.EmbedMany(texts, w);
                Console.WriteLine($"{variant} {w} workers x {t} threads: {sample.Count} chunks, {tokens:N0} tokens in {sw.Elapsed.TotalSeconds:N1}s = {tokens / sw.Elapsed.TotalSeconds:N0} tokens/s");
            }
            break;
        }

        default:
            Console.WriteLine("""
                kb <command> [--workspace <projects dir>] [--projects a,b,c] [--knowledge <dir>] [--model-dir <dir>] [--variant fp32|q8]
                  model          [--variant fp32|q8|all]   download EmbeddingGemma at the pinned revision; verify SHA-256
                  verify-model                             token ids and vectors against the Hugging Face reference fixture
                  harvest        [--max-tokens 1024] [--dump <dir>]        chunks and labels only (no embedding)
                  build          [--threads 8] [--max-tokens 1024] [--rebuild] [--no-classify] [--set-dir <dir>]
                  search "<q>"   [--k 10] [--mode hybrid|dense|lexical] [--kind k] [--program P] [--json]
                  eval           [--gold <file>] [--modes dense,lexical,hybrid] [--prose-lexical 0.25] [--out <dir>]
                  ground "<q>"   [--mode answer|document] [--program P] [--k 12] [--context-tokens 24000] [--out <file>]
                  bench          [--n 96] [--configs 8x1,16x1]
                """);
            break;
    }
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"kb {cmd}: {e.Message}");
    return 2;
}

static bool VerifyModel(KbPaths paths, string variant)
{
    var fixture = JsonDocument.Parse(File.ReadAllText(paths.Fixture)).RootElement;
    var v = fixture.GetProperty("variants").TryGetProperty(variant, out var items) ? items : throw new InvalidDataException($"no {variant} vectors in {paths.Fixture}");
    using var e = new EmbeddingGemma(paths.ModelDir, variant);
    bool ok = true;
    double worst = 1;
    foreach (var item in v.EnumerateArray())
    {
        var text = item.GetProperty("text").GetString()!;
        var ids = item.GetProperty("ids").EnumerateArray().Select(x => x.GetInt64()).ToArray();
        var expected = item.GetProperty("vector").EnumerateArray().Select(x => x.GetSingle()).ToArray();
        var got = e.Tokenizer.Encode(text);
        bool sameIds = got.SequenceEqual(ids);
        var vec = e.Embed(text);
        double cos = System.Numerics.Tensors.TensorPrimitives.CosineSimilarity(vec, expected);
        worst = Math.Min(worst, cos);
        var pass = sameIds && cos >= 0.99999;
        ok &= pass;
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} ids {(sameIds ? "equal" : $"differ ({got.Length} vs {ids.Length})")}, cosine {cos:F7}  {text[..Math.Min(70, text.Length)].Replace("\n", "\\n")}");
    }
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {variant}, worst cosine {worst:F7} (fixture {fixture.GetProperty("generator").GetString()})");
    return ok;
}

static Dictionary<string, string> Options(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < a.Length; i++)
    {
        if (!a[i].StartsWith("--")) { d.TryAdd("_", a[i]); continue; }
        var key = a[i][2..];
        if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) d[key] = a[++i]; else d[key] = "true";
    }
    return d;
}
