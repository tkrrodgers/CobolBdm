using MlCpu.Modernize;

if (args.Length >= 3 && args[0] == "lift")
{
    // lift <project> <out> [--model-edits <file>] [--rule <name>]...: the model's edits applied while it is printed
    // (ModelEdits.cs); opt-in Phase M rules (api-visibility: ApiVisibility.cs, final-classes: FinalClasses.cs, program-interfaces:
    // ProgramInterfaces.cs; ledger modernize/<rule>.json)
    var at = Array.IndexOf(args, "--model-edits");
    var edits = at > 2 && at + 1 < args.Length ? ModelEdits.Load(args[at + 1]) : null;
    var rules = new List<string>();
    for (int i = 3; i + 1 < args.Length; i++) if (args[i] == "--rule") rules.Add(args[++i]);
    try { Modernizer.Authorize(args[1], rules); }
    catch (Exception e) when (e is ArgumentException or InvalidDataException)
    {
        // an unknown or unauthorized rule, a malformed deployment profile: nothing is generated
        Console.Error.WriteLine("lift: " + e.Message);
        return 2;
    }
    var r = Modernizer.Run(args[1], args[2], null, edits, rules);
    return r.Residue == 0 ? 0 : 1;
}
// r1-selftest [<project> <scratch dir>]: the R1 decision cases, permutation and poison checks (ApiVisibilityChecks.cs)
if (args.Length >= 1 && args[0] == "r1-selftest") return ApiVisibilityChecks.Run(args[1..]);
// r3-selftest [<project> <scratch dir>]...: the R3 decision cases, permutation and poison checks (FinalClassesChecks.cs)
if (args.Length >= 1 && args[0] == "r3-selftest") return FinalClassesChecks.Run(args[1..]);
// r5-selftest [<project> <scratch dir>]...: the R5 decision cases, binding rules, permutation and poison checks (ProgramInterfacesChecks.cs)
if (args.Length >= 1 && args[0] == "r5-selftest") return ProgramInterfacesChecks.Run(args[1..]);
if (args.Length >= 2 && args[0] == "gn2")
{
    // gn2 <generated estate dir> [--original <source root>]...
    var originals = new List<string>();
    for (int i = 2; i + 1 < args.Length; i++) if (args[i] == "--original") originals.Add(args[++i]);
    return Gn2.Run(args[1], originals);
}
if (args.Length >= 3 && args[0] == "liftall") return LiftAll.Run(args[1], args[2]);
if (args.Length >= 3 && args[0] == "lift1") return LiftProbe.Run(args[1], args[2..]);
if (args.Length >= 2 && args[0] == "stats") return Stats.Run(args[1]);
if (args.Length >= 3 && args[0] == "dump")
{
    // the semantic tree of a program's paragraphs (or of the named ones), for writing lift rules
    var est = Estate.Load(args[1]);
    var prog = est.ById[args[2]];
    var paras = prog.Model.Paragraphs.Where(p => args.Length < 4 || args[3..].Contains(p.Name, StringComparer.OrdinalIgnoreCase));
    var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    foreach (var p in paras) Console.WriteLine(p.Name + Environment.NewLine + System.Text.Json.JsonSerializer.Serialize(p.Statements, opts));
    if (args.Length < 4) foreach (var f in prog.Model.Files) Console.WriteLine($"FILE {f.Name} assign={f.Assign} org={f.Organization} access={f.Access} key={f.RecordKey} status={f.FileStatus} lrecl={f.MaxRecord} records={string.Join(",", f.Records.Select(r => r.Name))}");
    return 0;
}
if (args.Length >= 2 && args[0] == "census") return Probe2.Run(args[1]);
if (args.Length >= 2 && args[0] == "probe") return Probe.Run(args[1], args.Length > 2 ? args[2] : null, args.Length > 3 ? args[3] : null);
Console.Error.WriteLine("""
    MlCpu.Modernize
      lift <project dir> <out dir> [--model-edits <file>] [--rule api-visibility|final-classes|program-interfaces]...   COBOL estate -> modern Java (SemanticModernization.md); edits: ModelEdits.cs; opt-in Phase M rules (final-classes also needs project.json "deploymentProfile": { "closedWorld": true }, declared by the estate owner; program-interfaces needs no profile)
      r1-selftest [<project dir> <scratch dir>]   R1 api-visibility decision, permutation and poison checks
      r3-selftest [<project dir> <scratch dir>]...   R3 final-classes decision, permutation and poison checks
      r5-selftest [<project dir> <scratch dir>]...   R5 program-interfaces decision, binding, permutation and poison checks
      gn2 <estate dir> [--original <src root>]...   the modernity rules R1-R7 (modernize/gn2.json, metrics.json)
      lift1 <project dir> <Mnnnn>...    one function: low IR and structured IR
      liftall <project dir> <file>      every function: residue census
      stats <project dir>               the recovered model
    """);
return 2;
