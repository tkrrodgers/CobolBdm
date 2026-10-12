// The capability kit's steps around the library-equivalence probe, outside a publish (docs/EQUIVALENCE-PROBE.md):
//   kit-harness inputs OUT-DIR HOST.cbl SUBPROGRAM.cbl...
//       OUT-DIR/<PID>/record-layouts.json and conditions.json, as CapabilityKitBuilder.EquivalencesAsync writes them
//   kit-harness contracts OUT-DIR EQUIVALENCES-DIR SOURCE.cbl...
//       OUT-DIR/<PID>/callee-contracts.md for each caller of a program with EQUIVALENCES-DIR/<CALLEE>/equivalences.md
//   kit-harness examples ...            worked examples in values (docs/PIPELINE-OUTPUT.md)
//   kit-harness typed PROJECT-DIR SOURCE.cbl...
//       PROJECT-DIR/normalization/<PID>/typed-reference/*.java: the typed-interface projection (docs/PIPELINE-OUTPUT.md, Phase B)
//   kit-harness semtree SOURCE.cbl      the semantic tree, one statement per line (diagnostics)
using MlCpu.Cobol.Analysis;
using MlCpu.CobolKb.Knowledge;

switch (args[0])
{
    case "semtree":
    {
        var m = CobolModelBuilder.BuildGroundedFull(args[1]);
        var o = new System.Text.Json.JsonSerializerOptions { WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        foreach (var p in m.Paragraphs)
        {
            Console.WriteLine($"== {p.Name}");
            foreach (var s in p.Statements) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(s, o));
        }
        break;
    }
    case "typed":
    {
        var project = args[1];
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(project, "capability-manifest.json")))!;
        // Programs whose typed reference passed its own acceptance test (typed-reference/verified.txt): CALLs of them embed it.
        var srcDir = Path.GetDirectoryName(Path.GetFullPath(args[2]))!;
        var embeddable = new Dictionary<string, CobolProgram>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Directory.EnumerateDirectories(Path.Combine(project, "normalization")))
        {
            var v = Path.Combine(d, "typed-reference", "verified.txt");
            var pid = Path.GetFileName(d);
            if (File.Exists(v) && System.Text.RegularExpressions.Regex.Match(File.ReadAllText(v), @"^acceptance (\d+)/(\d+)") is { Success: true } mv
                && mv.Groups[1].Value == mv.Groups[2].Value && File.Exists(Path.Combine(srcDir, pid + ".cbl")))
                embeddable[pid] = CobolModelBuilder.BuildGroundedFull(Path.Combine(srcDir, pid + ".cbl"));
        }
        if (embeddable.Count > 0) Console.WriteLine($"embeddable (their typed reference passed): {string.Join(", ", embeddable.Keys.Order())}");
        foreach (var source in args.Skip(2))
        {
            var m = CobolModelBuilder.BuildGroundedFull(source);
            var pid = m.ProgramId.ToUpperInvariant();
            var cap = manifest["capabilities"]!.AsArray().First(c => c!["programId"]!.GetValue<string>() == pid)!;
            var package = cap["package"]!.GetValue<string>();
            var entry = cap["step"]!.GetValue<string>().Split('.')[^1];
            var norm = Path.Combine(project, "normalization", pid);
            var kitDir = Path.Combine(project, "src", "main", "java", Path.Combine(package.Split('.')), "records");
            var kitSources = Directory.EnumerateFiles(kitDir, "*.java").ToDictionary(f => Path.GetFileNameWithoutExtension(f), File.ReadAllText);
            // Every program with verified library operations (an embedded callee's own CALLs need them too).
            var callees = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in Directory.EnumerateDirectories(Path.Combine(project, "normalization")))
                if (File.Exists(Path.Combine(d, "equivalences.json")))
                    callees[Path.GetFileName(d)] = (File.ReadAllText(Path.Combine(d, "equivalences.json")), File.ReadAllText(Path.Combine(d, "record-layouts.json")));
            // A program's own record kit (an embedded callee's CALL areas this capability's kit lacks).
            (MlCpu.Kit.KitLayouts, IReadOnlyDictionary<string, string>)? KitOf(string callee)
            {
                var c = manifest["capabilities"]!.AsArray().FirstOrDefault(x => x!["programId"]!.GetValue<string>().Equals(callee, StringComparison.OrdinalIgnoreCase));
                var lj = Path.Combine(project, "normalization", callee.ToUpperInvariant(), "record-layouts.json");
                if (c is null || !File.Exists(lj)) return null;
                var dir = Path.Combine(project, "src", "main", "java", Path.Combine(c["package"]!.GetValue<string>().Split('.')), "records");
                return (MlCpu.Kit.KitLayouts.Parse(File.ReadAllText(lj)),
                    Directory.EnumerateFiles(dir, "*.java").ToDictionary(f => Path.GetFileNameWithoutExtension(f), File.ReadAllText));
            }
            var typed = MlCpu.CobolKb.Knowledge.Typed.TypedProjection.Build(new(pid, package, entry, m, File.ReadAllText(Path.Combine(norm, "record-layouts.json")),
                kitSources, callees, embeddable.Where(kv => !kv.Key.Equals(pid, StringComparison.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase))
                { KitOf = KitOf });
            var outDir = Path.Combine(norm, "typed-reference");
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);
            foreach (var (name, src) in typed.Files) File.WriteAllText(Path.Combine(outDir, name + ".java"), src);
            File.WriteAllText(Path.Combine(outDir, "gaps.txt"), string.Join("\n", typed.Gaps) + (typed.Gaps.Count > 0 ? "\n" : ""));
            File.WriteAllText(Path.Combine(outDir, "starting.txt"), string.Join("\n", typed.Starting) + "\n");
            File.WriteAllText(Path.Combine(norm, "typed-reference.md"), typed.Prompt);
            Console.WriteLine($"{pid}: {typed.Files.Count} file(s), {typed.Files.Sum(f => f.Source.Split('\n').Length):N0} lines, {typed.Gaps.Count} gap(s)");
            foreach (var g in typed.Gaps.Take(20)) Console.WriteLine("  gap: " + g);
        }
        break;
    }
    case "inputs":
    {
        var host = CobolModelBuilder.BuildGroundedFull(args[2]);
        foreach (var source in args.Skip(3))
        {
            var sub = CobolModelBuilder.BuildGroundedFull(source);
            var pid = sub.ProgramId.ToUpperInvariant();
            if (EquivalenceProbeInputs.Linkage(sub) is not { } linkage) continue;
            var dir = Path.Combine(args[1], pid);
            Directory.CreateDirectory(dir);
            var kit = RecordKitBuilder.Build(new RecordKitRequest(pid, "com.mlcpu.capabilities." + pid.ToLowerInvariant(), "subprogram", sub, host, [],
                "com.mlcpu.core.Prog_" + pid));
            File.WriteAllText(Path.Combine(dir, "record-layouts.json"), kit.LayoutsJson);
            File.WriteAllText(Path.Combine(dir, "conditions.json"), EquivalenceProbeInputs.ConditionNames(linkage));
            Console.WriteLine($"{pid}: probe inputs ({linkage.Sum(i => i.Length)} linkage bytes)");
        }
        break;
    }
    case "contracts":
    {
        var (outDir, eqDir) = (args[1], args[2]);
        var equivalences = Directory.Exists(eqDir)
            ? Directory.EnumerateDirectories(eqDir).Where(d => File.Exists(Path.Combine(d, "equivalences.md")))
                .ToDictionary(d => Path.GetFileName(d).ToUpperInvariant(), d => File.ReadAllText(Path.Combine(d, "equivalences.md")), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in args.Skip(3))
        {
            var caller = CobolModelBuilder.BuildGroundedFull(source);
            var md = CalleeContracts.Build(caller, equivalences);
            if (md is null) continue;
            var dir = Path.Combine(outDir, caller.ProgramId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "callee-contracts.md"), md);
            Console.WriteLine($"{caller.ProgramId}: contracts of {string.Join(", ", CalleeContracts.DirectCallees(caller).Where(equivalences.ContainsKey))}");
        }
        break;
    }
    case "examples":
    {
        // kit-harness examples OUT-DIR TRACES-DIR CLOCK JOB STEP HOST.cbl SUB.cbl...: OUT-DIR/<PID>/examples.md in values from the
        // CALL traces TRACES-DIR/<PID>/<scenario>/trace.json (base first, then by name), with the inputs CapabilityKitBuilder uses.
        var (outDir, traces, clock, job, step) = (args[1], args[2], args[3], args[4], args[5]);
        var host = CobolModelBuilder.BuildGroundedFull(args[6]);
        foreach (var source in args.Skip(7))
        {
            var sub = CobolModelBuilder.BuildGroundedFull(source);
            var pid = sub.ProgramId.ToUpperInvariant();
            var linkage = sub.Using.Select(u => sub.Items.FirstOrDefault(i => i.Name.Equals(u, StringComparison.OrdinalIgnoreCase) && i.Level is 1 or 77)).OfType<DataItem>().ToList();
            var kit = RecordKitBuilder.Build(new RecordKitRequest(pid, "com.mlcpu.capabilities." + pid.ToLowerInvariant(), "subprogram", sub, host, [],
                "com.mlcpu.core.Prog_" + pid));
            var layouts = MlCpu.Kit.KitLayouts.Parse(kit.LayoutsJson);
            var dir = Path.Combine(traces, pid);
            if (!Directory.Exists(dir)) continue;
            var scenarios = Directory.EnumerateDirectories(dir).Select(Path.GetFileName).OfType<string>()
                .OrderBy(s => s == "base" ? 0 : 1).ThenBy(s => s, StringComparer.Ordinal)
                .Where(s => File.Exists(Path.Combine(dir, s, "trace.json")))
                .Select(s => MlCpu.Kit.CallExamples.ReadTrace(s, Path.Combine(dir, s, "trace.json"))).ToList();
            var text = MlCpu.Kit.CallExamples.Render(pid, host.ProgramId.ToUpperInvariant(), layouts.LinkageNames, job, step, clock, layouts,
                KitExamples.Conditions(linkage), scenarios);
            Directory.CreateDirectory(Path.Combine(outDir, pid));
            File.WriteAllText(Path.Combine(outDir, pid, "examples.md"), text);
            Console.WriteLine($"{pid}: examples in values, {text.Length:N0} characters");
        }
        break;
    }
    default:
        Console.Error.WriteLine("usage: kit-harness inputs OUT-DIR HOST.cbl SUB.cbl... | contracts OUT-DIR EQ-DIR SOURCE.cbl... | examples OUT-DIR TRACES-DIR CLOCK JOB STEP HOST.cbl SUB.cbl...");
        return 2;
}
return 0;
