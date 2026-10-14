using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MlCpu.DesignModel.Forward;

/// <summary>
/// The forward-engineering design round (tools/eval/PROTOCOL-forward.md section 5): 1a the architecture
/// (mlcpu.service-design.v1), 1b one design per implementation slice (mlcpu.slice-design.v1), and the peer review of another
/// model's anonymized architecture (mlcpu.design-review.v1). Each reply is checked (schema essentials and the automated
/// checks) and the model is asked to correct it until it passes or the attempts run out.
/// </summary>
public static class ForwardDesign
{
    public const string ArchitectSystem = """
        You are a senior software architect. You design a new Java 21 service, hexagonal, from a written specification, a fixed
        public API and rule digests distilled from legacy COBOL programs. You return ONE JSON document that conforms to the given
        JSON Schema, and nothing else: no prose, no markdown fences.
        """;

    public const string ReviewerSystem = """
        You are a senior software architect reviewing a design for a new service against a fixed rubric. You score each criterion
        from 1 to 5 against its written anchors, with a short justification that cites the design. You return ONE JSON document
        that conforms to the given JSON Schema, and nothing else: no prose, no markdown fences.
        """;

    // ------------------------------------------------------------------------------------------------ 1a architecture

    public static bool Architecture(IChatBackend chat, ForwardInputs inputs, string outDir, int attempts, string modelName, string reasoning, int maxTokens)
    {
        Directory.CreateDirectory(outDir);
        var prompt = ArchitecturePrompt(inputs);
        inputs.CheckLeaks(prompt, "architecture prompt");
        return JsonStep(chat, inputs.Java(ArchitectSystem), prompt, Path.Combine(outDir, "architecture.json"), attempts, modelName, reasoning, maxTokens,
            d => CheckArchitecture(d, inputs), "mlcpu.service-design.v1");
    }

    /// <summary>The architecture prompt (1a).</summary>
    public static string ArchitecturePrompt(ForwardInputs inputs)
    {
        var sb = new StringBuilder();
        sb.Append(inputs.Spec.TrimEnd()).Append("\n\n# Design rules (RULES.md)\n").Append(inputs.Rules.TrimEnd()).Append("\n\n");
        sb.Append(inputs.ApiSection()).Append('\n');
        sb.Append(inputs.AllDigests());
        sb.Append("# JSON Schema (mlcpu.service-design.v1)\n").Append(inputs.Schema("service-design.schema.json").TrimEnd()).Append("\n\n");
        sb.Append($"""
            # Task
            Design the architecture of the {inputs.ServiceName}:
            - the {ForwardInputs.Words(inputs.Modules.Count)} implementation modules ({string.Join(", ", inputs.Modules.Select(m => m.Module))}), their
              responsibilities, packages and dependencies;
            - the domain model of each bounded context: aggregates with the invariants they enforce, value objects, domain services
              and named policies;
            - one use-case flow per {inputs.ServiceInterface} method, naming the classes and methods involved, how idempotency is
              handled and what is written when (rejections, exceptions);
            - a trace of every rule to the module, class and method that will implement it;
            - your main decisions and the risks you see.
            The design must be implementable by another developer from this document alone, and it must respect RULES.md.
            {(inputs.NotBuilt.Count > 0 ? inputs.NotBuiltNote() + "\n" : "")}Rule ids to trace, each exactly once: {string.Join(", ", inputs.AllRules)}.
            Return the JSON now.
            """);
        return sb.ToString();
    }

    /// <summary>The automated checks of protocol section 5 for an architecture: problems, empty when it passes.</summary>
    public static List<string> CheckArchitecture(JsonObject d, ForwardInputs inputs)
    {
        var p = new List<string>();
        if (d["schema"]?.ToString() != "mlcpu.service-design.v1") p.Add("schema must be \"mlcpu.service-design.v1\"");
        foreach (var k in new[] { "summary", "modules", "domainModel", "useCases", "ruleTrace", "decisions", "risks" })
            if (d[k] is null) p.Add($"missing property {k}");
        var roots = inputs.Modules.ToDictionary(m => m.Module, m => m.Package);
        var modules = (d["modules"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        foreach (var m in roots.Keys.Where(m => modules.All(x => x["module"]?.ToString() != m))) p.Add($"modules[] has no entry for {m}");
        foreach (var m in modules)
        {
            var name = m["module"]?.ToString() ?? "";
            if (!roots.TryGetValue(name, out var root)) { p.Add($"modules[]: {name} is not one of the {ForwardInputs.Words(roots.Count)} implementation modules"); continue; }
            foreach (var pkg in (m["packages"] as JsonArray ?? []).Select(x => x?.ToString() ?? ""))
                if (pkg != root && !pkg.StartsWith(root + ".", StringComparison.Ordinal)) p.Add($"modules[{name}]: package {pkg} is not under {root} (RULES.md A-7)");
            var allowed = inputs.AllowedDependencies(name);
            foreach (var dep in (m["dependsOn"] as JsonArray ?? []).Select(x => x?.ToString() ?? ""))
                if (inputs.NotBuilt.Contains(dep)) p.Add($"modules[{name}]: depends on {dep}, which this run does not build: keep that code in {name}");
                else if (!allowed.Contains(dep)) p.Add($"modules[{name}]: depends on {dep}, which RULES.md forbids (A-1, A-2)");
        }
        var seenContexts = (d["domainModel"] as JsonArray ?? []).Select(x => x?["context"]?.ToString()).ToHashSet();
        foreach (var c in inputs.Slices.Select(s => s.Name).Where(c => !seenContexts.Contains(c))) p.Add($"domainModel[] has no entry for context {c}");
        var methods = (d["useCases"] as JsonArray ?? []).Select(x => x?["method"]?.ToString()).ToHashSet();
        foreach (var m in inputs.UseCaseMethods.Where(m => !methods.Contains(m))) p.Add($"useCases[] has no flow for {inputs.ServiceInterface}.{m}");
        foreach (var u in (d["useCases"] as JsonArray ?? []).OfType<JsonObject>())
            if (u["class"]?.ToString() is { } cls && !cls.StartsWith(inputs.ApplicationPackage + ".", StringComparison.Ordinal))
                p.Add($"useCases[{u["method"]}]: class {cls} is not in {inputs.ApplicationPackage}");
        TraceProblems(p, d["ruleTrace"] as JsonArray, inputs.AllRules, roots, null);
        return p;
    }

    static void TraceProblems(List<string> p, JsonArray? trace, IEnumerable<string> rules, IReadOnlyDictionary<string, string> roots, ISet<string>? classes)
    {
        var want = rules.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in (trace ?? []).OfType<JsonObject>())
        {
            var id = t["ruleId"]?.ToString() ?? "";
            if (!want.Contains(id)) { p.Add($"ruleTrace: {id} is not a rule of this design's scope"); continue; }
            seen.Add(id);
            var cls = t["class"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(t["method"]?.ToString())) p.Add($"ruleTrace[{id}]: no method");
            if (t["module"]?.ToString() is { } module)
            {
                if (!roots.TryGetValue(module, out var root)) p.Add($"ruleTrace[{id}]: module {module} is not an implementation module");
                else if (!cls.StartsWith(root + ".", StringComparison.Ordinal)) p.Add($"ruleTrace[{id}]: class {cls} is not in {module} ({root})");
            }
            else if (!roots.Values.Any(r => cls.StartsWith(r + ".", StringComparison.Ordinal))) p.Add($"ruleTrace[{id}]: class {cls} is not in an implementation module's package");
            if (classes is not null && !classes.Contains(cls)) p.Add($"ruleTrace[{id}]: class {cls} is not one of classes[]");
        }
        var missing = want.Where(r => !seen.Contains(r)).OrderBy(r => r, StringComparer.Ordinal).ToList();
        if (missing.Count > 0) p.Add($"ruleTrace[] misses {missing.Count} rule(s): {string.Join(", ", missing)}");
    }

    /// <summary>Pass or fail per automated check (reported per design).</summary>
    public static JsonObject ArchitectureChecks(JsonObject? d, ForwardInputs inputs)
    {
        var problems = d is null ? ["no JSON object"] : CheckArchitecture(d, inputs);
        return new JsonObject
        {
            ["schemaValid"] = d is not null && !problems.Any(x => x.StartsWith("schema") || x.StartsWith("missing property")),
            ["everyApiMethodAssigned"] = !problems.Any(x => x.StartsWith("useCases")),
            ["everyRuleTraced"] = !problems.Any(x => x.StartsWith("ruleTrace")),
            ["dependenciesAllowed"] = !problems.Any(x => x.Contains("RULES.md forbids") || x.Contains("A-7")),
            ["problems"] = Program.Strings(problems)
        };
    }

    // ------------------------------------------------------------------------------------------------ 1b slices

    public static bool Slices(IChatBackend chat, ForwardInputs inputs, string architecturePath, IReadOnlyList<string> normalizationDirs, string outDir,
        int attempts, string modelName, string reasoning, int maxTokens, int context, IEnumerable<string>? only = null)
    {
        Directory.CreateDirectory(outDir);
        var architecture = JsonNode.Parse(File.ReadAllText(architecturePath))!.ToJsonString(Program.Json);
        bool all = true;
        foreach (var slice in inputs.Slices.Where(s => only is null || only.Contains(s.Name)))
        {
            Program.Log($"===== {modelName}: slice design {slice.Name}");
            var prompt = SlicePrompt(inputs, slice, architecture, normalizationDirs, maxTokens, context);
            inputs.CheckLeaks(prompt, $"slice design prompt of {slice.Name}", architecture);
            var rules = inputs.RulesOf(slice).ToList();
            var roots = inputs.Modules.ToDictionary(m => m.Module, m => m.Package);
            all &= JsonStep(chat, inputs.Java(ArchitectSystem), prompt, Path.Combine(outDir, $"slice-{slice.Name}.json"), attempts, modelName, reasoning, maxTokens,
                d => CheckSlice(d, slice, rules, roots, inputs.ApplicationModule), "mlcpu.slice-design.v1");
        }
        return all;
    }

    /// <summary>A slice-design prompt (1b): the architecture, the slice's digests and requirements packets (left out when the
    /// prompt would not fit the context).</summary>
    public static string SlicePrompt(ForwardInputs inputs, ForwardInputs.Slice slice, string architecture, IReadOnlyList<string> normalizationDirs,
        int maxTokens, int context)
    {
        var sb = new StringBuilder();
        sb.Append(inputs.Spec.TrimEnd()).Append("\n\n# Design rules (RULES.md)\n").Append(inputs.Rules.TrimEnd()).Append("\n\n");
        sb.Append(inputs.ApiSection()).Append('\n');
        sb.Append("# Your architecture (mlcpu.service-design.v1)\n").Append(architecture).Append("\n\n");
        sb.Append(inputs.DigestSection(slice.Digests));
        var packets = new StringBuilder();
        foreach (var pid in slice.Packets)
        {
            var file = normalizationDirs.Select(d => Path.Combine(d, pid, "requirements.json")).FirstOrDefault(File.Exists)
                       ?? throw new FileNotFoundException($"requirements packet of {pid} not found in {string.Join("; ", normalizationDirs)}");
            packets.Append($"# Requirements packet of {pid} (mlcpu.requirements-packet.v1, extracted from the COBOL source)\n")
                .Append(JsonNode.Parse(File.ReadAllText(file))!.ToJsonString()).Append("\n\n");
        }
        var schema = inputs.Schema("slice-design.schema.json");
        var rules = inputs.RulesOf(slice).ToList();
        var task = $"""
            # Task
            Detail the slice "{slice.Name}" of your architecture: every class of {slice.Module} ({slice.Package}) and the application
            classes ({inputs.ApplicationPackage}) that its use cases need ({string.Join(", ", slice.Methods.Select(m => inputs.ServiceInterface + "." + m))}).
            For each class give its kind, fields and method signatures with their purpose and the rules they implement. Simple class
            names must be unique across the service. Trace every rule of the slice, each exactly once: {string.Join(", ", rules)}.
            Add the notes an implementer needs: numeric details, ordering, edge cases.{(slice.Packets.Length == 0 ? " This slice has no legacy program: SPEC.md section 6 is its source." : "")}
            Return the JSON now.
            """;
        var prompt = sb + packets.ToString() + "# JSON Schema (mlcpu.slice-design.v1)\n" + schema.TrimEnd() + "\n\n" + task;
        if (prompt.Length / 3.6 + maxTokens > context)
        {
            Program.Log($"  requirements packets left out of the {slice.Name} prompt (context)");
            prompt = sb + "# JSON Schema (mlcpu.slice-design.v1)\n" + schema.TrimEnd() + "\n\n" + task;
        }
        return prompt;
    }

    public static List<string> CheckSlice(JsonObject d, ForwardInputs.Slice slice, IEnumerable<string> rules, IReadOnlyDictionary<string, string> roots,
        string applicationModule)
    {
        var p = new List<string>();
        if (d["schema"]?.ToString() != "mlcpu.slice-design.v1") p.Add("schema must be \"mlcpu.slice-design.v1\"");
        if (d["slice"]?.ToString() != slice.Name) p.Add($"slice must be \"{slice.Name}\"");
        foreach (var k in new[] { "classes", "ruleTrace", "notes" }) if (d[k] is null) p.Add($"missing property {k}");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var simple = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in (d["classes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = c["name"]?.ToString() ?? "";
            var module = c["module"]?.ToString() ?? "";
            names.Add(name);
            if (!simple.Add(name[(name.LastIndexOf('.') + 1)..])) p.Add($"classes[]: the simple name of {name} is used twice");
            if (module != slice.Module && module != applicationModule) p.Add($"classes[{name}]: module {module} is not {slice.Module} or {applicationModule}");
            else if (!name.StartsWith(roots[module] + ".", StringComparison.Ordinal)) p.Add($"classes[{name}]: not in {roots[module]}");
        }
        if (names.Count == 0) p.Add("classes[] is empty");
        TraceProblems(p, d["ruleTrace"] as JsonArray, rules, roots, names);
        return p;
    }

    // ------------------------------------------------------------------------------------------------ peer review

    /// <summary>Reviews each design (label → architecture JSON file) against the rubric; one conversation per design.</summary>
    public static bool Review(IChatBackend chat, ForwardInputs inputs, IReadOnlyDictionary<string, string> designs, string outDir, int attempts,
        string modelName, string reasoning, int maxTokens)
    {
        Directory.CreateDirectory(outDir);
        bool all = true;
        foreach (var (label, file) in designs)
        {
            Program.Log($"===== {modelName}: review of design {label}");
            var sb = new StringBuilder("# The rubric (rubric.md)\n").Append(inputs.Rubric.TrimEnd()).Append("\n\n");
            sb.Append(inputs.Spec.TrimEnd()).Append("\n\n# Design rules (RULES.md)\n").Append(inputs.Rules.TrimEnd()).Append("\n\n");
            sb.Append(inputs.ApiSection()).Append('\n').Append(inputs.AllDigests());
            sb.Append($"# Design {label} (mlcpu.service-design.v1)\n").Append(JsonNode.Parse(File.ReadAllText(file))!.ToJsonString(Program.Json)).Append("\n\n");
            sb.Append("# JSON Schema (mlcpu.design-review.v1)\n").Append(inputs.Schema("design-review.schema.json").TrimEnd()).Append("\n\n");
            sb.Append($"""
                # Task
                Score design {label} on the rubric's six criteria (C1 to C6), each from 1 to 5 against its anchors, with a justification
                that cites the design. "design" must be "{label}". Return the JSON now.
                """);
            inputs.CheckLeaks(sb.ToString(), $"review prompt of design {label}", JsonNode.Parse(File.ReadAllText(file))!.ToJsonString(Program.Json));
            all &= JsonStep(chat, ReviewerSystem, sb.ToString(), Path.Combine(outDir, $"review-{label}.json"), attempts, modelName, reasoning, maxTokens,
                d => CheckReview(d, label), "mlcpu.design-review.v1");
        }
        return all;
    }

    public static List<string> CheckReview(JsonObject d, string label)
    {
        var p = new List<string>();
        if (d["schema"]?.ToString() != "mlcpu.design-review.v1") p.Add("schema must be \"mlcpu.design-review.v1\"");
        if (d["design"]?.ToString() != label) p.Add($"design must be \"{label}\"");
        foreach (var c in new[] { "C1", "C2", "C3", "C4", "C5", "C6" })
        {
            var s = d["scores"]?[c]?["score"];
            if (s is null || !int.TryParse(s.ToString(), out var v) || v < 1 || v > 5) p.Add($"scores.{c}.score must be an integer from 1 to 5");
            if (string.IsNullOrWhiteSpace(d["scores"]?[c]?["justification"]?.ToString())) p.Add($"scores.{c}.justification is missing");
        }
        return p;
    }

    // ------------------------------------------------------------------------------------------------ the JSON reply loop

    static bool JsonStep(IChatBackend chat, string system, string prompt, string outPath, int attempts, string modelName, string reasoning, int maxTokens,
        Func<JsonObject, List<string>> check, string schemaName)
    {
        File.WriteAllText(Path.ChangeExtension(outPath, ".prompt.md"), prompt);
        var messages = new List<ChatMessage> { new("system", system), new("user", prompt) };
        var runs = new JsonArray();
        JsonObject? doc = null;
        List<string> problems = ["not attempted"];
        string backend = chat.Name;
        var started = DateTime.Now;
        for (int attempt = 1; attempt <= attempts && problems.Count > 0; attempt++)
        {
            ChatResult r;
            try { r = chat.Complete(messages, maxTokens, reasoning); }
            catch (HttpRequestException e) when (e.Message.StartsWith("llama-server error:", StringComparison.Ordinal) && e.Message.Contains("\"code\":500"))
            {
                Program.Log($"{Path.GetFileName(outPath)} attempt {attempt}: the server could not read the reply; counted as a failed attempt");
                runs.Add(new JsonObject { ["attempt"] = attempt, ["replyError"] = e.Message.Length > 500 ? e.Message[..500] : e.Message });
                problems = ["the reply could not be read by the server"];
                continue;
            }
            backend = r.Backend;
            Program.Log($"{Path.GetFileName(outPath)} attempt {attempt}: prompt {r.PromptTokens} tok, generated {r.GeneratedTokens} tok @ {r.TokensPerSecond:N1} tok/s, {r.Seconds:N0}s, stop={r.Stop}");
            File.WriteAllText(Path.ChangeExtension(outPath, $".attempt{attempt}.txt"), r.Content);
            if (r.Reasoning is not null) File.WriteAllText(Path.ChangeExtension(outPath, $".attempt{attempt}.reasoning.txt"), r.Reasoning);
            doc = Extract(r.Content);
            problems = doc is null
                ? [r.Stop == "length" ? "the reply was cut off at the token limit before the JSON was complete: answer with the JSON only, more compactly" : "the reply is not a JSON object"]
                : check(doc);
            runs.Add(Program.AttemptJson(attempt, r, problems));
            foreach (var x in problems.Take(12)) Program.Log("  - " + x);
            messages.Add(new("assistant", r.Content));
            messages.Add(new("user", $"The document fails the checks:\n- {string.Join("\n- ", problems.Take(40))}\nReturn the corrected, complete {schemaName} JSON only."));
        }
        if (doc is not null) File.WriteAllText(outPath, doc.ToJsonString(Program.Json) + "\n");
        File.WriteAllText(Path.ChangeExtension(outPath, ".run.json"), new JsonObject
        {
            ["schema"] = "mlcpu.forward-design-run.v1", ["document"] = schemaName, ["model"] = modelName, ["backend"] = backend, ["reasoning"] = reasoning,
            ["valid"] = problems.Count == 0, ["minutes"] = Math.Round((DateTime.Now - started).TotalMinutes, 2), ["attempts"] = runs,
            ["remainingProblems"] = Program.Strings(problems)
        }.ToJsonString(Program.Json) + "\n");
        Program.Log(problems.Count == 0 ? $"VALID {schemaName} written to {outPath}" : $"INVALID {schemaName} after {attempts} attempts ({problems.Count} problems)");
        return problems.Count == 0;
    }

    static JsonObject? Extract(string text)
    {
        var fenced = Regex.Match(text, @"```(?:json)?\s*\n(\{.*\})\s*```", RegexOptions.Singleline);
        if (fenced.Success && TryParse(fenced.Groups[1].Value) is { } f) return f;
        int s = text.IndexOf('{'), e = text.LastIndexOf('}');
        return s < 0 || e <= s ? null : TryParse(text[s..(e + 1)]);
    }

    static JsonObject? TryParse(string s)
    {
        try { return JsonNode.Parse(s) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
