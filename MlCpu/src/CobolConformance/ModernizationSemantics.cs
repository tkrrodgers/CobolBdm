static partial class Suites
{
    public static async Task ModernizationMove(Report report, string corpus, Toolchain environment)
    {
        const string suite = "ModernizationMove";
        if (!report.Wants(suite)) return;
        if (await environment.Unavailable() is { } missing) { report.Blocked(suite, "multi-receiver", missing); return; }
        if (environment.Cobc is null) { report.Blocked(suite, "multi-receiver", "GnuCOBOL is required"); return; }
        try
        {
            var source = Path.Combine(corpus, "modernization", "MOVEALIAS.cbl");
            var work = Directory.CreateTempSubdirectory("mlcpu-movealias-").FullName;
            var executable = Path.Combine(work, "MOVEALIAS.exe");
            var compile = Toolchain.Run(environment.Cobc, ["-x", "-std=ibm", "-fsign=EBCDIC", "-o", executable, source], work);
            if (compile.Code != 0) throw new InvalidOperationException("cobc: " + compile.Output);
            var oracle = Toolchain.Run(executable, [], work);
            string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');
            report.Check(suite, "oracle rereads an aliased sender", oracle.Code == 0
                && Normalize(oracle.Output) == "SOURCE:CDCD\nRESULT:CDCD", oracle.Output);
            foreach (var composed in new[] { false, true })
            {
                var lane = new Toolchain(environment.Repo, composed);
                var response = await lane.Transpile(source);
                var classes = lane.CompileGenerated(Path.Combine(work, composed ? "composed" : "faithful"),
                    [("Prog_MOVEALIAS", response["javaSource"]!.GetValue<string>())]);
                var run = lane.RunJava(classes, "com.mlcpu.rt.StepRunner", ["--program", "MOVEALIAS"]);
                report.Check(suite, composed ? "composed MOVE matches oracle" : "faithful MOVE matches oracle",
                    run.Code == oracle.Code && Normalize(run.Output) == Normalize(oracle.Output), run.Output);
            }
        }
        catch (Exception error) { report.Fail(suite, "multi-receiver", error); }
    }
}
