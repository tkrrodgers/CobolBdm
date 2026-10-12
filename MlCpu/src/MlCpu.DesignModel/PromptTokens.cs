using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MlCpu.DesignModel;

/// <summary>
/// Exact Gemma token counts without a GPU: llama.cpp's llama-tokenize with the model file loads the vocabulary only
/// (about 2 s a call) and tokenizes like llama-server's /tokenize (escapes off, no BOS). Counts are cached by content.
/// </summary>
public sealed class LlamaTokenizer(string exe, string model, string scratch)
{
    readonly ConcurrentDictionary<string, int> cache = new(StringComparer.Ordinal);
    public string Description => $"llama-tokenize ({exe}) on {Path.GetFileName(model)}, vocabulary only";

    public int Count(string text)
    {
        if (text.Length == 0) return 0;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        if (cache.TryGetValue(key, out var n)) return n;
        Directory.CreateDirectory(scratch);
        var file = Path.Combine(scratch, key + ".txt");
        File.WriteAllText(file, text, new UTF8Encoding(false));
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var x in new[] { "-m", model, "-f", file, "--no-escape", "--no-bos", "--show-count", "--ids", "--log-disable" }) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        File.Delete(file);
        var m = Regex.Match(output + errTask.Result, @"Total number of tokens: (\d+)");
        if (!m.Success) throw new InvalidOperationException("llama-tokenize gave no count: " + (output + errTask.Result)[^Math.Min(400, output.Length + errTask.Result.Length)..]);
        return cache[key] = int.Parse(m.Groups[1].Value);
    }
}

/// <summary>
/// prompt-tokens: the implementation prompt of every capability, assembled exactly as an implementation run assembles it
/// (ImplementStep.PlanPrompt: reply budget, examples fitted to the context, outline), for an old kit and a new kit, with the
/// tokens of each section. Nothing is sent to a model.
/// </summary>
public static class PromptTokens
{
    static readonly (string Label, string Prefix)[] Sections =
    [
        ("packet", "# Requirements packet"), ("design", "# Your design"), ("platform API", "# Platform API"), ("record kit", "# Generated record kit"),
        ("acceptance test", "# Acceptance test"), ("equivalences", "# Verified library equivalences"), ("callee contracts", "# Contracts of the programs"),
        ("parity notes", "# COBOL parity notes"), ("worked examples", "# Worked examples"), ("pattern", "# Pattern"),
        ("typed reference", "# Typed reference implementation"), ("AST outline", "# COBOL-85 AST outline"), ("task", "# Task")
    ];

    public sealed record Measured(string Program, string Kit, int Total, Dictionary<string, int> BySection, Dictionary<string, int> CharsBySection,
        bool Compact, bool Outline, int ReplyBudget, int CallsShown, int ExampleSections);

    public static int Run(string oldProject, string newProject, string designs, string outDir, IReadOnlyList<string>? only, LlamaTokenizer tok,
        int context, int maxTokens)
    {
        Directory.CreateDirectory(outDir);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(newProject, "capability-manifest.json")))!;
        var pids = manifest["capabilities"]!.AsArray().Select(c => c!["programId"]!.GetValue<string>()).Where(p => only is null || only.Contains(p)).ToList();
        var jobs = pids.SelectMany(p => new[] { (Pid: p, Kit: "old", Dir: oldProject), (Pid: p, Kit: "new", Dir: newProject) }).ToList();
        var results = new ConcurrentBag<Measured>();
        int system = tok.Count(ImplementStep.SystemPrompt);
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = 6 }, j =>
        {
            var design = Path.Combine(designs, j.Pid, "design.json");
            if (!File.Exists(design)) { Program.Log($"{j.Pid}: no design at {design}; skipped"); return; }
            try { results.Add(Measure(j.Pid, j.Kit, j.Dir, design, Path.Combine(outDir, j.Pid, j.Kit), tok, context, maxTokens)); }
            catch (Exception e) { Program.Log($"{j.Pid} ({j.Kit}): {e.GetType().Name}: {e.Message}"); }
        });
        var all = results.OrderBy(r => pids.IndexOf(r.Program)).ThenBy(r => r.Kit == "old" ? 0 : 1).ToList();
        var md = Report(all, pids, system, tok.Description, context, maxTokens);
        File.WriteAllText(Path.Combine(outDir, "prompt-tokens.md"), md);
        File.WriteAllText(Path.Combine(outDir, "prompt-tokens.json"), new JsonArray(all.Select(r => (JsonNode)new JsonObject
        {
            ["program"] = r.Program, ["kit"] = r.Kit, ["total"] = r.Total, ["compact"] = r.Compact, ["outline"] = r.Outline, ["replyBudget"] = r.ReplyBudget,
            ["callsShown"] = r.CallsShown, ["exampleSections"] = r.ExampleSections,
            ["sections"] = new JsonObject(r.BySection.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["chars"] = new JsonObject(r.CharsBySection.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)))
        }).ToArray()).ToJsonString(Program.Json) + "\n");
        Console.WriteLine(md);
        return 0;
    }

    static Measured Measure(string pid, string kitName, string project, string designPath, string outDir, LlamaTokenizer tok, int context, int maxTokens)
    {
        Directory.CreateDirectory(outDir);
        var cap = ImplementStep.FromManifest(Path.Combine(project, "capability-manifest.json"), pid);
        var stepSimple = cap.StepClass[(cap.StepClass.LastIndexOf('.') + 1)..];
        var pkgPath = cap.Package.Replace('.', Path.DirectorySeparatorChar);
        var pkgDir = Path.Combine(project, "src", "main", "java", pkgPath);
        var testDir = Path.Combine(project, "src", "test", "java", pkgPath);
        // The names in the harness package: the published package without the authored classes, its tests, and the probe class.
        var reserved = Directory.EnumerateFiles(pkgDir, "*.java").Select(Path.GetFileNameWithoutExtension).Select(n => n!)
            .Where(n => !cap.AuthoredClasses.Contains(n))
            .Concat(Directory.Exists(testDir) ? Directory.EnumerateFiles(testDir, "*.java").Select(Path.GetFileNameWithoutExtension).Select(n => n!) : [])
            .Append(Probe.ProbeClass).ToHashSet(StringComparer.Ordinal);
        var testFile = Directory.EnumerateFiles(testDir, "*EquivalenceTest.java").Single();
        var norm = Path.Combine(project, "normalization", pid);
        var packet = Path.Combine(norm, "requirements.json");
        var kitPath = Path.Combine(norm, "record-kit.md");
        var kit = File.Exists(kitPath) && Directory.Exists(Path.Combine(pkgDir, "records")) ? File.ReadAllText(kitPath).Replace("\r\n", "\n") : null;
        var examples = Path.Combine(norm, "examples.md");
        var notes = Path.Combine(project, "normalization", "parity-notes.md");
        var outline = Path.Combine(norm, "ast-outline.txt");
        var features = KitFeatures.Of(packet);
        var typed = cap.Kind == "subprogram" ? TypedReferenceKit.Load(packet, features) : null;
        if (typed is not null) foreach (var g in typed.Given.Keys) reserved.Add(g);
        var plan = ImplementStep.PlanPrompt(cap, stepSimple, packet, designPath, project, File.ReadAllText(testFile), reserved,
            File.Exists(examples) ? examples : null, File.Exists(notes) ? notes : null, File.Exists(outline) ? outline : null, kit, null,
            DesignStep.Equivalences(packet), DesignStep.CalleeContracts(packet), features, context, maxTokens, tok.Count, outDir, typed?.Prompt);
        File.WriteAllText(Path.Combine(outDir, "implementation.prompt.md"), plan.Prompt);
        var by = new Dictionary<string, int>(StringComparer.Ordinal);
        var chars = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (label, text) in Split(plan.Prompt))
        {
            by[label] = by.GetValueOrDefault(label) + tok.Count(text);
            chars[label] = chars.GetValueOrDefault(label) + text.Length;
        }
        var shownExamples = plan.ExamplesPath is not null ? File.ReadAllText(plan.ExamplesPath) : "";
        var r = new Measured(pid, kitName, tok.Count(plan.Prompt), by, chars, plan.Compact, plan.OutlineUsed, plan.MaxTokens,
            Regex.Matches(shownExamples, @"(?m)^### CALL").Count, Regex.Matches(shownExamples, @"(?m)^## Scenario").Count);
        Program.Log($"{pid} ({kitName}): {r.Total:N0} tokens; {string.Join(", ", by.Select(kv => $"{kv.Key} {kv.Value:N0}"))}");
        return r;
    }

    /// <summary>The prompt's top-level sections ("# " headings), labelled.</summary>
    static IEnumerable<(string Label, string Text)> Split(string prompt)
    {
        var parts = Regex.Split(prompt, @"(?m)^(?=# )");
        foreach (var p in parts.Where(p => p.Length > 0))
        {
            var label = Sections.FirstOrDefault(s => p.StartsWith(s.Prefix, StringComparison.Ordinal)).Label ?? "other";
            yield return (label, p);
        }
    }

    static string Pct(int a, int b) { var x = Math.Round(100.0 * (b - a) / a); return (x > 0 ? "+" : x < 0 ? "−" : "±") + Math.Abs(x).ToString("0") + "%"; }

    static string Report(List<Measured> all, List<string> pids, int system, string tokenizer, int context, int maxTokens)
    {
        var sb = new StringBuilder("# Implementation prompts, old kit vs new kit (tokens)\n\n");
        sb.Append($"Tokenizer: {tokenizer} (exact Gemma 4 counts; llama-server adds the chat template and the {system:N0}-token system prompt). ");
        sb.Append($"Each prompt is assembled as an implementation run assembles it ({context:N0}-token context, {maxTokens:N0}-token reply budget unless lowered): ");
        sb.Append("the worked examples are cut to fit, the AST outline goes in when it fits (always under the compact packet).\n\n");
        sb.Append("| Program | old total | new total | change | old: packet / examples / outline / record kit | new: packet / examples / outline / record kit | CALLs shown old → new | reply budget old → new |\n");
        sb.Append("|---|---:|---:|---:|---|---|---|---|\n");
        int to = 0, tn = 0;
        foreach (var pid in pids)
        {
            var o = all.FirstOrDefault(r => r.Program == pid && r.Kit == "old");
            var n = all.FirstOrDefault(r => r.Program == pid && r.Kit == "new");
            if (o is null || n is null) continue;
            to += o.Total; tn += n.Total;
            string Parts(Measured m) => $"{m.BySection.GetValueOrDefault("packet"):N0} / {m.BySection.GetValueOrDefault("worked examples"):N0} / " +
                                        $"{(m.Outline ? m.BySection.GetValueOrDefault("AST outline").ToString("N0") : "left out")} / {m.BySection.GetValueOrDefault("record kit"):N0}";
            sb.Append($"| {pid} | {o.Total:N0} | {n.Total:N0} | {Pct(o.Total, n.Total)} | {Parts(o)} | {Parts(n)} | {o.CallsShown} → {n.CallsShown} | {o.ReplyBudget:N0} → {n.ReplyBudget:N0} |\n");
        }
        if (to > 0) sb.Append($"| **all** | **{to:N0}** | **{tn:N0}** | **{Pct(to, tn)}** | | | | |\n");
        sb.Append("\n## Every section\n\n");
        var labels = Sections.Select(s => s.Label).Append("other").Where(l => all.Any(r => r.BySection.ContainsKey(l))).ToList();
        sb.Append("| Program | kit | ").Append(string.Join(" | ", labels)).Append(" | total |\n");
        sb.Append("|---|---|").Append(string.Concat(labels.Select(_ => "---:|"))).Append("---:|\n");
        foreach (var r in all)
            sb.Append($"| {r.Program} | {r.Kit} | ").Append(string.Join(" | ", labels.Select(l => r.BySection.TryGetValue(l, out var v) ? v.ToString("N0") : ""))).Append($" | {r.Total:N0} |\n");
        sb.Append("\n## Characters per token, by section (all programs)\n\n| section | old chars/token | new chars/token |\n|---|---:|---:|\n");
        foreach (var l in labels)
        {
            double Ratio(string kit)
            {
                var rs = all.Where(r => r.Kit == kit && r.BySection.ContainsKey(l)).ToList();
                int t = rs.Sum(r => r.BySection[l]), c = rs.Sum(r => r.CharsBySection[l]);
                return t == 0 ? 0 : (double)c / t;
            }
            sb.Append($"| {l} | {Ratio("old"):0.00} | {Ratio("new"):0.00} |\n");
        }
        return sb.ToString();
    }
}
