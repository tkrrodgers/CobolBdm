// callee-contracts OUT-DIR EQUIVALENCES-DIR SOURCE.cbl...
//   For each caller source: OUT-DIR/<PID>/callee-contracts.md when a program it CALLs directly has
//   EQUIVALENCES-DIR/<CALLEE>/equivalences.md (the library-equivalence probe's output), as the capability kit writes it.
using MlCpu.Cobol.Analysis;
using MlCpu.CobolKb.Knowledge;

var (outDir, eqDir) = (args[0], args[1]);
var equivalences = Directory.Exists(eqDir)
    ? Directory.EnumerateDirectories(eqDir).Where(d => File.Exists(Path.Combine(d, "equivalences.md")))
        .ToDictionary(d => Path.GetFileName(d).ToUpperInvariant(), d => File.ReadAllText(Path.Combine(d, "equivalences.md")), StringComparer.OrdinalIgnoreCase)
    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var source in args.Skip(2))
{
    var caller = CobolModelBuilder.BuildGroundedFull(source);
    var md = CalleeContracts.Build(caller, equivalences);
    if (md is null) continue;
    var dir = Path.Combine(outDir, caller.ProgramId);
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "callee-contracts.md"), md);
    Console.WriteLine($"{caller.ProgramId}: contracts of {string.Join(", ", CalleeContracts.DirectCallees(caller).Where(equivalences.ContainsKey))}");
}
