using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MlCpu.DesignModel;

/// <summary>
/// Local-model runner for the normalization pipeline. llama.cpp (llama-server) is the backend; LM-Kit is the fallback
/// when llama-server cannot start or a request fails (--backend lmkit forces it, --no-fallback disables it).
///
///   design    --packet requirements.json [--schema design.schema.json] --out design.json
///   implement --program PID --packet requirements.json --design design.json --manifest capability-manifest.json
///             --project exported-project-dir --out dir [--harness dir] [--examples examples.md] [--notes parity-notes.md]
///   run       --programs PID[,PID] --normalization dir --manifest capability-manifest.json --project exported-project-dir
///             --out dir [--harness-root dir] [--with-examples]   (design, then implementation, per program, one model load;
///             --with-examples adds normalization/PID/examples.md and normalization/parity-notes.md to the implementation prompt)
///
/// Model options: --model gguf (first shard of a split model) [--name label] [--ctx 131072] [--max-tokens 49152]
///   [--reasoning low|medium|high] [--impl-reasoning high] [--attempts 3] [--impl-attempts 10] [--temperature t] [--seed 42]
///   [--moe-cpu] [--backend llama|lmkit|exchange] [--exchange dir] [--server http://host:port] [--llama-dir C:\models\llama.cpp] [--port 8090]
///   [--server-args "..."] [--no-fallback] [--test-timeout-min 15]
///   probe --harness dir --program PID --manifest capability-manifest.json --scenario "name[|name...]": the v2 difference probe's feedback
/// The pre-llama form (no verb, --packet ... --out ...) still runs the design step.
/// </summary>
public static class Program
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static int Main(string[] argv)
    {
        var verb = argv.Length > 0 && !argv[0].StartsWith("--") ? argv[0] : "design";
        var a = Args(argv.SkipWhile(x => !x.StartsWith("--")).ToArray());
        string Need(string k) => a.GetValueOrDefault(k) ?? throw new ArgumentException($"--{k} is required for '{verb}'");
        try
        {
            var modelPath = a.GetValueOrDefault("model") ?? @"C:\models\gpt-oss-120b-GGUF\gpt-oss-120b-MXFP4.gguf";
            var name = a.GetValueOrDefault("name") ?? ModelLabel(modelPath);
            int ctx = int.Parse(a.GetValueOrDefault("ctx") ?? (verb == "design" ? "65536" : "131072"));
            var reasoning = a.GetValueOrDefault("reasoning") ?? "medium";
            var implReasoning = a.GetValueOrDefault("impl-reasoning") ?? "high";
            int designAttempts = int.Parse(a.GetValueOrDefault("attempts") ?? "3");
            int implAttempts = int.Parse(a.GetValueOrDefault("impl-attempts") ?? "10");
            int maxTokens = int.Parse(a.GetValueOrDefault("max-tokens") ?? (verb == "design" ? "16000" : "49152"));
            var testTimeout = TimeSpan.FromMinutes(double.Parse(a.GetValueOrDefault("test-timeout-min") ?? "15"));
            var outArg = Need("out");
            var logDir = verb == "design" ? Path.GetDirectoryName(Path.GetFullPath(outArg))! : Path.GetFullPath(outArg);
            var options = new BackendOptions(modelPath, ctx, maxTokens, reasoning,
                a.GetValueOrDefault("temperature") is { } t ? double.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : null,
                int.Parse(a.GetValueOrDefault("seed") ?? "42"), a.ContainsKey("moe-cpu"), a.GetValueOrDefault("llama-dir") ?? @"C:\models\llama.cpp",
                a.GetValueOrDefault("server"), int.Parse(a.GetValueOrDefault("port") ?? "8090"), a.GetValueOrDefault("server-args"), logDir,
                !a.ContainsKey("no-fallback"), a.GetValueOrDefault("exchange"));
            using var chat = new FallbackBackend(options, a.GetValueOrDefault("backend") ?? "llama");
            switch (verb)
            {
                case "design":
                {
                    var packet = Need("packet");
                    var schema = a.GetValueOrDefault("schema") ?? Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(packet)))!, "design.schema.json");
                    return DesignStep.Run(chat, packet, schema, outArg, designAttempts, name, reasoning, ctx) ? 0 : 1;
                }
                case "implement":
                {
                    var cap = ImplementStep.FromManifest(Need("manifest"), Need("program"));
                    var r = ImplementStep.Run(chat, cap, Need("packet"), Need("design"), Need("project"), outArg,
                        a.GetValueOrDefault("harness") ?? Path.Combine(Path.GetTempPath(), "mlcpu-harness", Safe(name), cap.ProgramId),
                        implAttempts, name, implReasoning, maxTokens, testTimeout, a.GetValueOrDefault("examples"), a.GetValueOrDefault("notes"),
                        a.GetValueOrDefault("outline"), ctx);
                    return r.TestsTotal > 0 && r.TestsPassed == r.TestsTotal ? 0 : 1;
                }
                case "run":
                    return RunAll(chat, a, name, outArg, designAttempts, implAttempts, reasoning, implReasoning, ctx, maxTokens, testTimeout);
                case "design-all":
                    return DesignAll(chat, a, name, outArg, designAttempts, reasoning, ctx);
                case "implement-all":
                    return ImplementAll(chat, a, name, outArg, implAttempts, implReasoning, ctx, maxTokens, testTimeout);
                case "patterns":
                {
                    // patterns --dir library --program PID --packet requirements.json [--record java-dir --label model]:
                    // records the verified code in java-dir as PID's pattern, or shows what a prompt for PID would get.
                    var lib = new PatternLibrary(Need("dir"), a.GetValueOrDefault("embeddings"));
                    if (a.GetValueOrDefault("record") is { } javaDir)
                    {
                        var code = Directory.EnumerateFiles(javaDir, "*.java", SearchOption.AllDirectories)
                            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), File.ReadAllText);
                        lib.Record(Need("program"), a.GetValueOrDefault("label") ?? name, Need("packet"), code, []);
                        Console.WriteLine($"recorded {Need("program")}: {code.Count} file(s); features: {PatternLibrary.Features(Need("packet"))}");
                        return 0;
                    }
                    var text = lib.Retrieve(Need("program"), Need("packet"), out var from);
                    Console.WriteLine(text is null ? "(no pattern of another program)" : $"from {from}:\n{text[..Math.Min(text.Length, 1500)]}");
                    return 0;
                }
                case "replay":
                {
                    // replay --program PID --manifest m --project published-dir --packet requirements.json --run dir-of-a-recorded-run --attempt N
                    //        [--harness dir]: the feedback this pipeline gives for that run's code after its attempt N.
                    var cap = ImplementStep.FromManifest(Need("manifest"), Need("program"));
                    int upTo = int.Parse(Need("attempt"));
                    var replies = Enumerable.Range(1, upTo).Select(n => Path.Combine(Need("run"), $"implementation.attempt{n}.txt")).Where(File.Exists).Select(File.ReadAllText).ToList();
                    Console.WriteLine(ImplementStep.Replay(cap, Need("packet"), Need("project"),
                        a.GetValueOrDefault("harness") ?? Path.Combine(Path.GetTempPath(), "mlcpu-replay", cap.ProgramId), replies, testTimeout));
                    return 0;
                }
                case "shadow":
                {
                    // shadow --program PID --manifest m --project published-dir --packet requirements.json --java dir-of-a-model's-classes
                    //        --out dir [--harness dir]: the feedback an implementation run gives for that code (shadow feedback first when
                    //        the kit has the feature, or MLCPU_KIT_FEATURES says so); written to out/feedback.txt.
                    var cap = ImplementStep.FromManifest(Need("manifest"), Need("program"));
                    var code = new SortedDictionary<string, string>(Directory.EnumerateFiles(Need("java"), "*.java", SearchOption.AllDirectories)
                        .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => File.ReadAllText(f).Replace("\r\n", "\n")), StringComparer.Ordinal);
                    var text = ImplementStep.FeedbackFor(cap, Need("packet"), Need("project"),
                        a.GetValueOrDefault("harness") ?? Path.Combine(Path.GetTempPath(), "mlcpu-shadow", cap.ProgramId), code, testTimeout);
                    Directory.CreateDirectory(outArg);
                    File.WriteAllText(Path.Combine(outArg, "feedback.txt"), text);
                    Console.WriteLine(text);
                    return 0;
                }
                case "prompt-tokens":
                {
                    // prompt-tokens --old published-project --new upgraded-project --designs dir-with-PID/design.json --out dir [--programs P,Q]
                    //               [--tokenize llama-tokenize.exe] [--tokenizer-model gguf]: per-section token counts of the implementation prompts.
                    var tok = new LlamaTokenizer(a.GetValueOrDefault("tokenize") ?? @"C:\models\llama.cpp-unsloth-b11539\llama-tokenize.exe",
                        a.GetValueOrDefault("tokenizer-model") ?? @"C:\models\gemma-4-31B-it-GGUF\BF16\gemma-4-31B-it-BF16-00001-of-00002.gguf",
                        Path.Combine(Path.GetFullPath(outArg), "tokenize-scratch"));
                    return PromptTokens.Run(Need("old"), Need("new"), Need("designs"), outArg,
                        a.GetValueOrDefault("programs")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), tok, ctx, maxTokens);
                }
                case "kit-upgrade":
                    // kit-upgrade --from published-project --traces work/calltrace --out new-project [--budget 70000] [--programs P,Q] [--label v6]
                    //             [--runtime java-sidecar/src/main/java/com/mlcpu/rt] [--outlines graphs/ast]
                    return KitUpgrade.Run(Need("from"), Need("traces"), outArg, int.Parse(a.GetValueOrDefault("budget") ?? "70000"),
                        a.GetValueOrDefault("programs")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal),
                        a.GetValueOrDefault("label") ?? "v6", a.GetValueOrDefault("runtime"), a.GetValueOrDefault("outlines"));
                case "probe":
                {
                    var detail = ImplementStep.ProbeOnly(Need("harness"), Need("program"), Need("manifest"), Need("scenario"));
                    Console.WriteLine(detail ?? "(no differences or the probe could not run)");
                    return detail is null ? 1 : 0;
                }
                // Forward engineering (tools/eval/PROTOCOL-forward.md): --cas forward/cas is the SME package; --slices a,b limits it to
                // those slices (a pilot: forward/loans --slices calendar).
                case "fwd-architecture":
                    return Forward.ForwardDesign.Architecture(chat, FwdInputs(a), outArg, designAttempts, name, reasoning, maxTokens) ? 0 : 1;
                case "fwd-leaks":
                    // fwd-leaks --cas dir [--in dir]: the package's files against the leak checker (exit 1 on a hit), or with --in the
                    // source system's names in a model's output (designs, code): reported, written to out/overlap.json.
                    return Forward.ForwardContamination.Leaks(FwdInputs(a, scanPackage: false), a.GetValueOrDefault("in"), outArg);
                case "fwd-judge":
                    // fwd-judge --cas dir [--slices a,b] --java dir --out dir [--harness dir]: judge those files as an attempt is judged.
                    return Forward.ForwardImplement.JudgeOnly(FwdInputs(a), Need("java"),
                        a.GetValueOrDefault("harness") ?? Path.Combine(Path.GetTempPath(), "mlcpu-forward-judge"), testTimeout);
                case "fwd-contamination":
                    // fwd-contamination --cas dir --out dir: the probe of section 4.3, once per model before the design (no package in the prompt).
                    return Forward.ForwardContamination.Probe(chat, FwdInputs(a, scanPackage: false), outArg, name, reasoning, maxTokens);
                case "fwd-slices":
                    return Forward.ForwardDesign.Slices(chat, FwdInputs(a), Need("architecture"),
                        Need("normalization").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), outArg, designAttempts, name,
                        reasoning, maxTokens, ctx, a.GetValueOrDefault("only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) ? 0 : 1;
                case "fwd-review":
                {
                    // --designs "P=path\architecture.json;Q=path\architecture.json"
                    var designs = Need("designs").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(d => d.Split('=', 2)).ToDictionary(d => d[0], d => d[1], StringComparer.Ordinal);
                    return Forward.ForwardDesign.Review(chat, FwdInputs(a), designs, outArg, designAttempts, name, reasoning, maxTokens) ? 0 : 1;
                }
                case "fwd-implement":
                    return Forward.ForwardImplement.Run(chat, FwdInputs(a), Need("design"), outArg,
                        a.GetValueOrDefault("harness") ?? Path.Combine(Path.GetTempPath(), "mlcpu-forward-harness", Safe(name)), implAttempts, name, implReasoning,
                        maxTokens, ctx, testTimeout, a.GetValueOrDefault("arm") ?? "S", a.GetValueOrDefault("designer") ?? name,
                        int.Parse(a.GetValueOrDefault("plateau") ?? "3"), a.ContainsKey("debug-prompt"), a.GetValueOrDefault("resume"),
                        a.GetValueOrDefault("warm-start"));
                case "fwd-host":
                    // fwd-host --cas dir --out dir [--impl-attempts n] [--plateau 3] [--debug-prompt] [--warm-start holes-dir]: the model writes
                    // the host holes of the package's prepared plugin (forward.json "host"); each attempt is judged by the host (FE 9.1, S8).
                    return Forward.ForwardHost.Run(chat, FwdInputs(a), outArg, implAttempts, name, implReasoning, maxTokens, ctx,
                        int.Parse(a.GetValueOrDefault("plateau") ?? "3"), a.ContainsKey("debug-prompt"), a.GetValueOrDefault("warm-start"));
                case "fwd-host-prompt":
                    // fwd-host-prompt --cas dir --out dir: the host run's first prompt, leak-checked, without a model.
                    return Forward.ForwardHost.PromptOnly(FwdInputs(a), outArg);
                case "fwd-host-judge":
                    // fwd-host-judge --cas dir --holes dir --out dir [--repair-imports]: judge those hole files as an attempt is judged.
                    return Forward.ForwardHost.JudgeOnly(FwdInputs(a), Need("holes"), outArg, a.ContainsKey("repair-imports"));
                default:
                    Console.Error.WriteLine($"unknown verb '{verb}' (design | implement | run)");
                    return 2;
            }
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        catch (Forward.LeakException e)
        {
            // A prompt (or package file) carries the source system's names: nothing is sent (ForwardEngineeringFineract.md 4.3).
            Log(e.Message);
            Console.Error.WriteLine(e.Message);
            return 3;
        }
    }

    /// <summary>The forward package of --cas, limited to --slices when given.</summary>
    static Forward.ForwardInputs FwdInputs(Dictionary<string, string?> a, bool scanPackage = true)
    {
        var dir = a.GetValueOrDefault("cas") ?? throw new ArgumentException("--cas is required");
        var only = a.GetValueOrDefault("slices")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new Forward.ForwardInputs(dir, only, scanPackage);
    }

    static int RunAll(FallbackBackend chat, Dictionary<string, string?> a, string name, string outRoot, int designAttempts, int implAttempts,
        string reasoning, string implReasoning, int ctx, int maxTokens, TimeSpan testTimeout)
    {
        string Need(string k) => a.GetValueOrDefault(k) ?? throw new ArgumentException($"--{k} is required for 'run'");
        var programs = Need("programs").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var normalization = Need("normalization");
        var manifest = Need("manifest");
        var project = Need("project");
        var harnessRoot = a.GetValueOrDefault("harness-root") ?? Path.Combine(Path.GetTempPath(), "mlcpu-harness", Safe(name));
        Directory.CreateDirectory(outRoot);
        var summary = new JsonArray();
        foreach (var pid in programs)
        {
            Log($"===== {name}: {pid}");
            var dir = Path.Combine(outRoot, pid);
            Directory.CreateDirectory(dir);
            var started = DateTime.Now;
            var packet = Path.Combine(normalization, pid, "requirements.json");
            var design = Path.Combine(dir, "design.json");
            if (File.Exists(design)) File.Delete(design);
            var designOk = DesignStep.Run(chat, packet, Path.Combine(normalization, "design.schema.json"), design, designAttempts, name, reasoning, Math.Min(ctx, 131072));
            var entry = new JsonObject { ["programId"] = pid, ["designValid"] = designOk };
            if (designOk)
            {
                var cap = ImplementStep.FromManifest(manifest, pid);
                string? examples = null, notes = null;
                if (a.ContainsKey("with-examples"))
                {
                    examples = Path.Combine(normalization, pid, "examples.md") is var e && File.Exists(e) ? e : null;
                    notes = Path.Combine(normalization, "parity-notes.md") is var n && File.Exists(n) ? n : null;
                }
                var r = ImplementStep.Run(chat, cap, packet, design, project, dir, Path.Combine(harnessRoot, pid), implAttempts, name, implReasoning, maxTokens, testTimeout, examples, notes);
                entry["compiled"] = r.Compiled;
                entry["testsPassed"] = r.TestsPassed;
                entry["testsTotal"] = r.TestsTotal;
                entry["passedAll"] = r.TestsTotal > 0 && r.TestsPassed == r.TestsTotal;
                entry["implementationAttempts"] = r.AttemptsUsed;
                entry["bestAttempt"] = r.BestAttempt;
                entry["outputDifferences"] = r.OutputDifferences;
                entry["firstAttemptPassed"] = r.FirstAttemptPassed;
            }
            entry["minutes"] = Math.Round((DateTime.Now - started).TotalMinutes, 1);
            summary.Add(entry);
            WriteSummary(outRoot, name, chat, summary);
        }
        return summary.All(s => s?["passedAll"]?.GetValue<bool>() == true) ? 0 : 1;
    }

    /// <summary>
    /// design-all --programs PID[,PID] --normalization dir --out root [--outline]: one model load, a design per program
    /// (root/PID/design.json). --outline adds normalization/PID/ast-outline.txt (the COBOL-85 AST outline) to the prompt.
    /// </summary>
    static int DesignAll(FallbackBackend chat, Dictionary<string, string?> a, string name, string outRoot, int attempts, string reasoning, int ctx)
    {
        string Need(string k) => a.GetValueOrDefault(k) ?? throw new ArgumentException($"--{k} is required for 'design-all'");
        var programs = Need("programs").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var normalization = Need("normalization");
        Directory.CreateDirectory(outRoot);
        int ok = 0, k = 0;
        foreach (var pid in programs)
        {
            Log($"===== {name}: {pid} ({++k}/{programs.Length}) design");
            var dir = Path.Combine(outRoot, pid);
            Directory.CreateDirectory(dir);
            var design = Path.Combine(dir, "design.json");
            if (File.Exists(design)) File.Delete(design);
            var outline = a.ContainsKey("outline") ? Path.Combine(normalization, pid, "ast-outline.txt") : null;
            try
            {
                if (DesignStep.Run(chat, Path.Combine(normalization, pid, "requirements.json"), Path.Combine(normalization, "design.schema.json"), design,
                        attempts, name, reasoning, ctx, outline)) ok++;
            }
            catch (PromptTooLargeException e)
            {
                // One program's packet does not fit: record it and design the others.
                Log($"{pid}: design skipped, the prompt does not fit the context ({e.Message})");
            }
        }
        Log($"designs: {ok}/{programs.Length} valid");
        return ok == programs.Length ? 0 : 1;
    }

    /// <summary>
    /// implement-all --programs PID[,PID] --normalization dir --manifest capability-manifest.json --project published-dir --out root
    /// [--harness-root dir] [--with-examples] [--outline] [--patterns dir [--embeddings http://host:port]]: one model load; each
    /// program's design (root/PID/design.json) implemented, compiled and judged by its equivalence test; summary.json in root.
    /// --patterns keeps a pattern library: a program that passes leaves its verified code, and a later program gets the most
    /// similar pattern of another program in its prompt (BM25, or cosine over an OpenAI-compatible embeddings endpoint).
    /// </summary>
    static int ImplementAll(FallbackBackend chat, Dictionary<string, string?> a, string name, string outRoot, int attempts, string reasoning,
        int ctx, int maxTokens, TimeSpan testTimeout)
    {
        string Need(string k) => a.GetValueOrDefault(k) ?? throw new ArgumentException($"--{k} is required for 'implement-all'");
        var programs = Need("programs").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var normalization = Need("normalization");
        var manifest = Need("manifest");
        var project = Need("project");
        var harnessRoot = a.GetValueOrDefault("harness-root") ?? Path.Combine(Path.GetTempPath(), "mlcpu-harness", Safe(name));
        var summary = new JsonArray();
        int k = 0;
        foreach (var pid in programs)
        {
            Log($"===== {name}: {pid} ({++k}/{programs.Length}) implementation");
            var dir = Path.Combine(outRoot, pid);
            var design = Path.Combine(dir, "design.json");
            var entry = new JsonObject { ["programId"] = pid, ["designValid"] = File.Exists(design) };
            var started = DateTime.Now;
            if (File.Exists(design))
            {
                var cap = ImplementStep.FromManifest(manifest, pid);
                string? examples = null, notes = null;
                if (a.ContainsKey("with-examples"))
                {
                    examples = Path.Combine(normalization, pid, "examples.md") is var e && File.Exists(e) ? e : null;
                    notes = Path.Combine(normalization, "parity-notes.md") is var n && File.Exists(n) ? n : null;
                }
                var outline = a.ContainsKey("outline") ? Path.Combine(normalization, pid, "ast-outline.txt") : null;
                ImplementStep.Result r;
                try
                {
                    // --warm-start: begin from this program's previous deliverable (dir/java), the best attempt of the last run.
                    var previous = Path.Combine(dir, "java");
                    var warm = a.ContainsKey("warm-start") && Directory.Exists(previous)
                        ? Directory.EnumerateFiles(previous, "*.java", SearchOption.AllDirectories)
                            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => File.ReadAllText(f).Replace("\r\n", "\n"), StringComparer.Ordinal)
                        : null;
                    r = ImplementStep.Run(chat, cap, Path.Combine(normalization, pid, "requirements.json"), design, project, dir, Path.Combine(harnessRoot, pid),
                        attempts, name, reasoning, maxTokens, testTimeout, examples, notes, outline, ctx, a.GetValueOrDefault("patterns"), a.GetValueOrDefault("embeddings"),
                        warm, a.ContainsKey("debug-prompt"));
                }
                catch (PromptTooLargeException e)
                {
                    // The implementation prompt does not fit: this program stays faithful; the others go on.
                    Log($"{pid}: implementation skipped, the prompt does not fit the context ({e.Message})");
                    entry["skipped"] = "prompt larger than the context";
                    entry["minutes"] = Math.Round((DateTime.Now - started).TotalMinutes, 1);
                    summary.Add(entry);
                    WriteSummary(outRoot, name, chat, summary);
                    continue;
                }
                entry["compiled"] = r.Compiled;
                entry["testsPassed"] = r.TestsPassed;
                entry["testsTotal"] = r.TestsTotal;
                entry["passedAll"] = r.TestsTotal > 0 && r.TestsPassed == r.TestsTotal;
                entry["implementationAttempts"] = r.AttemptsUsed;
                entry["bestAttempt"] = r.BestAttempt;
                entry["outputDifferences"] = r.OutputDifferences;
                entry["firstAttemptPassed"] = r.FirstAttemptPassed;
            }
            else Log($"{pid}: no design at {design}; skipped");
            entry["minutes"] = Math.Round((DateTime.Now - started).TotalMinutes, 1);
            summary.Add(entry);
            WriteSummary(outRoot, name, chat, summary);
        }
        return summary.All(s => s?["passedAll"]?.GetValue<bool>() == true) ? 0 : 1;
    }

    static void WriteSummary(string outRoot, string name, FallbackBackend chat, JsonArray summary)
    {
        // Programs run earlier (a partial re-run) keep their entries; this run's entries replace theirs.
        var path = Path.Combine(outRoot, "summary.json");
        var mine = summary.Select(s => s!["programId"]!.GetValue<string>()).ToHashSet();
        var merged = new JsonArray();
        if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["programs"] is JsonArray old)
            foreach (var e in old.Where(e => e?["programId"]?.GetValue<string>() is { } id && !mine.Contains(id))) merged.Add(e!.DeepClone());
        foreach (var e in summary) merged.Add(e!.DeepClone());
        File.WriteAllText(path, new JsonObject
        {
            ["schema"] = "mlcpu.model-run-summary.v1", ["model"] = name, ["backend"] = chat.Name, ["updated"] = DateTime.Now.ToString("O"),
            ["backendEvents"] = Strings(chat.Events), ["programs"] = merged
        }.ToJsonString(Json) + "\n");
    }

    public static JsonObject AttemptJson(int attempt, ChatResult r, IReadOnlyCollection<string> problems) => new()
    {
        ["attempt"] = attempt, ["backend"] = r.Backend, ["promptTokens"] = r.PromptTokens, ["generatedTokens"] = r.GeneratedTokens,
        ["tokensPerSecond"] = Math.Round(r.TokensPerSecond, 2), ["promptTokensPerSecond"] = Math.Round(r.PromptTokensPerSecond, 1),
        ["seconds"] = Math.Round(r.Seconds, 1), ["stop"] = r.Stop, ["problems"] = Strings(problems)
    };

    public static JsonArray Strings(IEnumerable<string> s) => new(s.Select(x => (JsonNode?)x).ToArray());

    static string ModelLabel(string path)
    {
        var n = Path.GetFileNameWithoutExtension(path);
        n = System.Text.RegularExpressions.Regex.Replace(n, @"-\d{5}-of-\d{5}$", "");
        return n;
    }

    static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    static Dictionary<string, string?> Args(string[] a)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < a.Length; i++)
            if (a[i].StartsWith("--")) d[a[i][2..]] = i + 1 < a.Length && (!a[i + 1].StartsWith("--") || a[i] == "--server-args") ? a[++i] : null;
        return d;
    }

    public static void Log(string m) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {m}");
}
