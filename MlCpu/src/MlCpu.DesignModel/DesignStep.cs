using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MlCpu.DesignModel;

/// <summary>
/// Requirements packet (mlcpu.requirements-packet.v1) -> capability design (mlcpu.capability-design.v1). The design is
/// validated against the packet (hash, every rule id, every quirk, every output) and the model is asked to correct it
/// until it passes or the attempts run out.
/// </summary>
public static class DesignStep
{
    public const string SystemPrompt = """
        You are a senior Java architect modernizing a COBOL batch program into a semantically normalized Java 21 capability.
        You receive a requirements packet extracted from the COBOL source by static analysis. You return ONE JSON document
        that conforms to the given JSON Schema, and nothing else: no prose, no markdown fences.
        Design rules:
        - Business rules are typed Java over immutable records: BigDecimal amounts, enums for codes and reasons, Optional for lookups.
        - File formats, record images, byte offsets and file status codes stay behind repository/reader/writer ports.
        - The service must reproduce every observable output of the COBOL program exactly (datasets byte-for-byte, console lines, return code).
        - Trace every packet rule id; disposition every legacy quirk as preserve (needed for parity) or fix (needs business sign-off) with a reason.
        - Never invent behaviour the packet does not evidence. Put doubts in openQuestions.
        """;

    /// <returns>true when a valid design was written to <paramref name="outPath"/>.</returns>
    public static bool Run(IChatBackend chat, string packetPath, string schemaPath, string outPath, int attempts, string modelName, string reasoning, int context,
        string? outlinePath = null)
    {
        var packet = JsonNode.Parse(File.ReadAllText(packetPath))!.AsObject();
        var schema = File.ReadAllText(schemaPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        // Verified library equivalences sit next to the packet (normalization/<PID>/equivalences.md); the packet is unchanged.
        var equivalences = Equivalences(packetPath);
        // So do the verified contracts of the programs it CALLs (normalization/<PID>/callee-contracts.md).
        var callees = CalleeContracts(packetPath);
        var eqSection = (equivalences is null ? "" : equivalences + "\n\n") + (callees is null ? "" : callees + "\n\n");
        var outline = "";
        if (outlinePath is not null && File.Exists(outlinePath))
        {
            var text = File.ReadAllText(outlinePath).Replace("\r\n", "\n");
            // Keep room for the schema, the packet and a 16k-token reply (about 3.6 characters per token).
            if ((schema.Length + packet.ToJsonString().Length + eqSection.Length + text.Length) / 3.6 + 20000 < context)
                outline = $"COBOL-85 AST outline of the program (the original logic, statement by statement):\n```\n{text.TrimEnd()}\n```\n\n";
            else Program.Log("  AST outline left out of the design prompt (context)");
        }
        var eqRule = equivalences is null ? "" :
            "Where \"Verified library equivalences\" lists a group of CALLs, design that group's service operation around the named JDK operation " +
            "(BigDecimal with MathContext/RoundingMode, LocalDate, ChronoUnit) and its field mapping, not around a digit-by-digit or calendar algorithm.\n";
        if (callees is not null)
            eqRule += "Where the design computes the result of a program it CALLs instead of calling it, it must produce every effect that program's " +
                      "verified contract lists for that CALL (result, return and status fields, fields copied from the caller's arguments), on the error paths too.\n";
        var messages = new List<ChatMessage>
        {
            new("system", SystemPrompt),
            new("user", $"JSON Schema (mlcpu.capability-design.v1):\n{schema}\n\nRequirements packet:\n{packet.ToJsonString()}\n\n{eqSection}{outline}{eqRule}" +
                        $"Return the design JSON for program {packet["programId"]} now. requirementsHash must be \"{packet["requirementsHash"]}\".")
        };
        var runs = new JsonArray();
        JsonObject? design = null;
        List<string> problems = ["not attempted"];
        string backend = chat.Name;
        for (int attempt = 1; attempt <= attempts && problems.Count > 0; attempt++)
        {
            var r = chat.Complete(messages, reasoning: reasoning);
            backend = r.Backend;
            Program.Log($"design attempt {attempt}: prompt {r.PromptTokens} tok @ {r.PromptTokensPerSecond:N0} tok/s, generated {r.GeneratedTokens} tok @ {r.TokensPerSecond:N1} tok/s, {r.Seconds:N0}s, stop={r.Stop} [{r.Backend}]");
            File.WriteAllText(Path.ChangeExtension(outPath, $".attempt{attempt}.txt"), r.Content);
            if (r.Reasoning is not null) File.WriteAllText(Path.ChangeExtension(outPath, $".attempt{attempt}.reasoning.txt"), r.Reasoning);
            design = Extract(r.Content);
            problems = design is null ? ["the reply is not a JSON object"] : Validate(design, packet);
            runs.Add(Program.AttemptJson(attempt, r, problems));
            foreach (var p in problems.Take(15)) Program.Log("  - " + p);
            messages.Add(new("assistant", r.Content));
            messages.Add(new("user", "The design failed validation:\n- " + string.Join("\n- ", problems.Take(40)) + "\nReturn the corrected, complete design JSON only."));
        }
        if (design is not null) File.WriteAllText(outPath, design.ToJsonString(Program.Json) + "\n");
        File.WriteAllText(Path.ChangeExtension(outPath, ".run.json"), new JsonObject
        {
            ["schema"] = "mlcpu.design-run.v1", ["programId"] = packet["programId"]!.GetValue<string>(), ["requirementsHash"] = packet["requirementsHash"]!.GetValue<string>(),
            ["model"] = modelName, ["backend"] = backend, ["reasoning"] = reasoning, ["context"] = context,
            ["valid"] = problems.Count == 0, ["attempts"] = runs, ["remainingProblems"] = Program.Strings(problems)
        }.ToJsonString(Program.Json) + "\n");
        Program.Log(problems.Count == 0 ? $"VALID design written to {outPath}" : $"INVALID design after {attempts} attempts ({problems.Count} problems)");
        return problems.Count == 0;
    }

    /// <summary>
    /// normalization/&lt;PID&gt;/equivalences.md next to the packet (the library-equivalence probe's verified statements for a
    /// subprogram), as a "# Verified library equivalences" section; null when there is none.
    /// </summary>
    public static string? Equivalences(string packetPath)
    {
        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(packetPath))!, "equivalences.md");
        if (!File.Exists(path)) return null;
        var lines = File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd().Split('\n').ToList();
        if (lines.Count > 0 && lines[0].StartsWith("# ", StringComparison.Ordinal)) lines.RemoveAt(0);
        return "# Verified library equivalences\n" + string.Join("\n", lines).TrimEnd();
    }

    /// <summary>normalization/&lt;PID&gt;/callee-contracts.md next to the packet (the verified contracts of the programs it CALLs); null when there is none.</summary>
    public static string? CalleeContracts(string packetPath)
    {
        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(packetPath))!, "callee-contracts.md");
        return File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd() : null;
    }

    static JsonObject? Extract(string text)
    {
        int s = text.IndexOf('{'), e = text.LastIndexOf('}');
        if (s < 0 || e <= s) return null;
        try { return JsonNode.Parse(text[s..(e + 1)]) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Schema essentials plus traceability: hash, every rule, every quirk, every output dataset and the console.</summary>
    public static List<string> Validate(JsonObject d, JsonObject packet)
    {
        var p = new List<string>();
        if (d["schema"]?.ToString() != "mlcpu.capability-design.v1") p.Add("schema must be \"mlcpu.capability-design.v1\"");
        if (d["programId"]?.ToString() != packet["programId"]!.ToString()) p.Add($"programId must be {packet["programId"]}");
        if (d["requirementsHash"]?.ToString() != packet["requirementsHash"]!.ToString()) p.Add($"requirementsHash must be {packet["requirementsHash"]}");
        foreach (var k in new[] { "capability", "package", "domainModel", "ports", "services", "rules", "quirks", "outputs", "tests" })
            if (d[k] is null) p.Add($"missing property {k}");
        var ruleIds = (d["rules"] as JsonArray)?.Select(r => r?["ruleId"]?.ToString()).ToHashSet() ?? [];
        foreach (var r in packet["rules"]!.AsArray().Select(r => r!["id"]!.ToString()).Where(id => !ruleIds.Contains(id))) p.Add($"rule {r} is not traced in rules[]");
        foreach (var r in (d["rules"] as JsonArray ?? []).Where(r => r?["disposition"]?.ToString() == "implemented" && string.IsNullOrWhiteSpace(r?["element"]?.ToString())))
            p.Add($"rule {r!["ruleId"]} is implemented but names no element");
        var quirkIds = (d["quirks"] as JsonArray)?.Select(q => q?["quirkId"]?.ToString()).ToHashSet() ?? [];
        foreach (var q in packet["legacyQuirks"]!.AsArray().Select(q => q!["id"]!.ToString()).Where(id => !quirkIds.Contains(id))) p.Add($"quirk {q} has no disposition in quirks[]");
        var outputs = string.Join(" ", (d["outputs"] as JsonArray ?? []).Select(o => o?["output"]?.ToString()));
        foreach (var dd in packet["observableOutputs"]!["files"]!.AsArray().Select(f => f!["dd"]!.ToString()).Where(dd => !outputs.Contains(dd, StringComparison.OrdinalIgnoreCase)))
            p.Add($"output dataset {dd} is not produced by any design element: add {{\"output\": \"{dd}\", \"producedBy\": \"<element that writes it>\"}} to outputs[]");
        if (packet["observableOutputs"]!["console"]!.AsArray().Count > 0 && !outputs.Contains("SYSOUT", StringComparison.OrdinalIgnoreCase) && !outputs.Contains("console", StringComparison.OrdinalIgnoreCase))
            p.Add("console output (SYSOUT) is not produced by any design element: add one entry {\"output\": \"SYSOUT\", \"producedBy\": \"<element that writes the console lines>\"} to outputs[]");
        if (d["package"]?.ToString() is { } pkg && !Regex.IsMatch(pkg, @"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$")) p.Add($"package '{pkg}' is not a Java package name");
        return p;
    }
}
