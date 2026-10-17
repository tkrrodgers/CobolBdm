using MlCpu.Modernize;

if (args.Length >= 3 && args[0] == "lift")
{
    var r = Modernizer.Run(args[1], args[2]);
    return r.Residue == 0 ? 0 : 1;
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
      lift <project dir> <out dir>      COBOL estate -> modern Java (SemanticModernization.md)
      lift1 <project dir> <Mnnnn>...    one function: low IR and structured IR
      liftall <project dir> <file>      every function: residue census
      stats <project dir>               the recovered model
    """);
return 2;
