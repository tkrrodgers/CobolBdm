using System.Text.Json.Nodes;

namespace MlCpu.DesignModel.Forward;

/// <summary>
/// The leak checker's reports and the contamination probe (ForwardEngineeringFineract.md sections 4.3 and 10.3). The source
/// system is public, so a model may know it: the probe asks, without any package, for the code of an equal-installment schedule
/// calculator "in the style of an open-source core-banking system you know" and measures how many of the source system's
/// identifiers come back. The same overlap is measured on what the model writes later (fwd-leaks --in).
/// </summary>
public static class ForwardContamination
{
    public const string ProbePrompt = """
        Write the Java of an equal-installment declining-balance loan repayment schedule calculator in the style of an open-source
        core-banking system you know: its main classes (the loan terms, the repayment periods, the installment calculation, the
        schedule) with their fields and methods. Java code only.
        """;

    /// <summary>fwd-leaks: the package's files (exit 1 on a hit), or with a directory the overlap of the files in it.</summary>
    public static int Leaks(ForwardInputs inputs, string? modelOutput, string outDir)
    {
        var leaks = inputs.Leaks ?? throw new ArgumentException($"{inputs.Dir}/forward.json has no leakDictionary");
        Directory.CreateDirectory(outDir);
        if (modelOutput is null)
        {
            var hits = leaks.ScanPackage(inputs.Dir);
            foreach (var (file, h) in hits.Take(50)) Program.Log($"  LEAK {h.Token} in {file}:{h.Line}{(h.Where.Length > 0 ? " (" + h.Where + ")" : "")}");
            Program.Log($"leak check of package {inputs.Name}: {hits.Count} hit(s) ({leaks.LeakNames} names in the dictionary)");
            return hits.Count == 0 ? 0 : 1;
        }
        var files = new JsonArray();
        var all = new SortedSet<string>(StringComparer.Ordinal);
        var leaked = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(modelOutput, "*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".java") || f.EndsWith(".json") || f.EndsWith(".txt")).OrderBy(f => f, StringComparer.Ordinal))
        {
            var (a, l, w) = leaks.Overlap(File.ReadAllText(f));
            all.UnionWith(a);
            leaked.UnionWith(l);
            if (a.Count + w.Count > 0)
                files.Add(new JsonObject { ["file"] = Path.GetRelativePath(modelOutput, f), ["names"] = Program.Strings(a), ["leakNames"] = Program.Strings(l), ["nameWords"] = Program.Strings(w) });
        }
        var o = new JsonObject
        {
            ["schema"] = "mlcpu.forward-overlap.v1", ["of"] = modelOutput, ["names"] = Program.Strings(all), ["leakNames"] = Program.Strings(leaked), ["files"] = files
        };
        File.WriteAllText(Path.Combine(outDir, "overlap.json"), o.ToJsonString(Program.Json) + "\n");
        Program.Log($"overlap of {modelOutput}: {all.Count} source-system name(s), {leaked.Count} that no prompt gave ({string.Join(", ", leaked.Take(10))})");
        return 0;
    }

    /// <summary>fwd-contamination: the probe, once per model before the design.</summary>
    public static int Probe(IChatBackend chat, ForwardInputs inputs, string outDir, string modelName, string reasoning, int maxTokens)
    {
        var leaks = inputs.Leaks ?? throw new ArgumentException($"{inputs.Dir}/forward.json has no leakDictionary");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "probe.prompt.md"), ProbePrompt);
        var r = chat.Complete([new("system", "You are a senior Java engineer."), new("user", ProbePrompt)], maxTokens, reasoning);
        File.WriteAllText(Path.Combine(outDir, "probe.reply.txt"), r.Content);
        if (r.Reasoning is not null) File.WriteAllText(Path.Combine(outDir, "probe.reasoning.txt"), r.Reasoning);
        var (all, leaked, words) = leaks.Overlap(r.Content);
        var (rAll, rLeaked, rWords) = leaks.Overlap(r.Reasoning ?? "");
        var o = new JsonObject
        {
            ["schema"] = "mlcpu.forward-contamination.v1", ["model"] = modelName, ["backend"] = r.Backend, ["generatedTokens"] = r.GeneratedTokens,
            ["reply"] = new JsonObject { ["names"] = Program.Strings(all), ["leakNames"] = Program.Strings(leaked), ["nameWords"] = Program.Strings(words) },
            ["reasoning"] = new JsonObject { ["names"] = Program.Strings(rAll), ["leakNames"] = Program.Strings(rLeaked), ["nameWords"] = Program.Strings(rWords) },
            ["dictionary"] = new JsonObject { ["path"] = leaks.Path, ["leakNames"] = leaks.LeakNames }
        };
        File.WriteAllText(Path.Combine(outDir, "contamination.json"), o.ToJsonString(Program.Json) + "\n");
        Program.Log($"contamination probe ({modelName}): reply {all.Count} source-system name(s), {leaked.Count} of them leak names " +
                    $"({string.Join(", ", leaked.Take(12))}); name words {(words.Count == 0 ? "none" : string.Join(", ", words))}; " +
                    $"reasoning {rAll.Count} / {rLeaked.Count}{(rWords.Count > 0 ? ", name words " + string.Join(", ", rWords) : "")}");
        return 0;
    }
}
