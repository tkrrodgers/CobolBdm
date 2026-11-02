using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MlCpu.Cobol.Analysis;
using MlCpu.Cobol.Analysis.Entities;
using MlCpu.Cobol.Analysis.Projections;
using MlCpu.CobolKb.Knowledge;
using MlCpu.Core;

static partial class Suites
{
    static IEnumerable<string[]> Rows(string manifest) =>
        File.ReadAllLines(manifest).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split('\t'));

    static void Guard(Report r, string suite, string check, Action body)
    {
        if (!r.Wants(suite)) return;
        try { body(); }
        catch (Exception e) { r.Fail(suite, check, e); }
    }

    // ------------------------------------------------------------------ parser acceptance (manifest.tsv)

    public static void ParserAcceptance(Report r, string corpus)
    {
        const string suite = "ParserAcceptance";
        if (!r.Wants(suite)) return;
        foreach (var row in Rows(Path.Combine(corpus, "manifest.tsv")))
        {
            var (path, expect) = (row[0], row[1]);
            string detail;
            bool accepted;
            try
            {
                var m = CobolModelBuilder.BuildGroundedFull(Path.Combine(corpus, path));
                var frontiers = m.Paragraphs.SelectMany(p => p.Statements).Where(s => s.Frontier is not null).Select(s => s.Frontier!).ToList();
                accepted = m.Diagnostics.Count == 0 && frontiers.Count == 0 && m.Paragraphs.Count > 0;
                detail = m.Diagnostics.Concat(frontiers).FirstOrDefault() ?? "accepted";
            }
            catch (Exception e) { accepted = false; detail = e.Message; }
            r.Check(suite, path, expect == "accept" ? accepted : !accepted, $"expected {expect}: {detail}");
        }
        // Configured copybook roots (project.json layout.copybookRoots) are searched before the directory-name heuristics.
        var layout = Path.Combine(corpus, "accept", "layout");
        var prog = Path.Combine(layout, "src", "LAYOUT01.cbl");
        try
        {
            CobolModelBuilder.ConfigureCopybookRoots(layout, []);
            var before = CobolModelBuilder.BuildGroundedFull(prog);
            r.Check(suite, "copybook roots: without configuration the copybook is missing", before.MissingCopybooks.Count == 1,
                $"{before.MissingCopybooks.Count} missing");
            CobolModelBuilder.ConfigureCopybookRoots(layout, ["members"]);
            var after = CobolModelBuilder.BuildGroundedFull(prog);
            r.Check(suite, "copybook roots: a configured root finds it", after.MissingCopybooks.Count == 0 && after.Diagnostics.Count == 0
                && after.Items.Any(i => i.Name == "LAY-FIELD"), string.Join("; ", after.Diagnostics));
        }
        catch (Exception e) { r.Fail(suite, "copybook roots", e); }
        finally { CobolModelBuilder.ConfigureCopybookRoots(layout, []); }
    }

    // ------------------------------------------------------------------ COBOL-85 grammar over the parser lane (docs/AST-GRAMMAR.md)

    /// <summary>
    /// The AST stage's grammar (MlCpu.Cobol.Antlr) on the parser lane: every manifest.tsv program parses without errors and in
    /// SLL mode alone, the mode a 130K-line program needs (full LL takes minutes there); a fourth manifest column overrides
    /// ("ll": without errors in any mode, "-": not parsed). And CardDemo: no syntax errors, every program SLL, the only missing
    /// copybooks the IBM CICS ones the repository does not ship (DFHAID, DFHBMSCA).
    /// </summary>
    public static void ParserAst(Report r, string corpus, string carddemo)
    {
        const string suite = "ParserAst";
        if (!r.Wants(suite)) return;
        foreach (var row in Rows(Path.Combine(corpus, "manifest.tsv")))
        {
            var path = row[0];
            var expect = row.Length > 3 && row[3].Trim().Length > 0 ? row[3].Trim() : "sll";
            if (expect == "-") continue;
            Guard(r, suite, path, () =>
            {
                var p = MlCpu.Cobol.Antlr.CobolAntlr.Parse(Path.Combine(corpus, path));
                r.Check(suite, path, p.Ok && (expect == "ll" || p.Mode == "SLL"),
                    $"expected {expect}: {p.Mode}, {p.Errors.Count} error(s){(p.Errors.Count > 0 ? ": " + p.Errors[0] : "")}");
            });
        }
        var cbl = Path.Combine(carddemo, "cbl");
        if (!Directory.Exists(cbl)) { r.Blocked(suite, "carddemo", $"{cbl} not found"); return; }
        Guard(r, suite, "carddemo", () =>
        {
            var programs = CobolFiles.Programs(cbl);
            var syntax = new List<string>();
            var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var notSll = new List<string>();
            foreach (var f in programs)
            {
                var p = MlCpu.Cobol.Antlr.CobolAntlr.Parse(f);
                if (p.Mode != "SLL") notSll.Add(p.ProgramId);
                foreach (var e in p.Errors)
                    if (e.StartsWith("missing copybook ", StringComparison.Ordinal)) missing.Add(e["missing copybook ".Length..]);
                    else syntax.Add($"{p.ProgramId} {e}");
            }
            r.Check(suite, "carddemo: every program without syntax errors, SLL; only DFHAID/DFHBMSCA missing",
                programs.Count > 0 && syntax.Count == 0 && notSll.Count == 0 && missing.IsSubsetOf(["DFHAID", "DFHBMSCA"]),
                $"{programs.Count} programs; {syntax.Count} syntax error(s){(syntax.Count > 0 ? " (" + syntax[0] + ")" : "")}; " +
                $"not SLL [{string.Join(", ", notSll)}]; missing [{string.Join(", ", missing)}]");
        });
    }

    // ------------------------------------------------------------------ JCL suites

    public static void Jcl(Report r, string corpus, string carddemo)
    {
        var jobs = new Lazy<List<JclJob>>(() => JclHarvester.Harvest([Path.Combine(corpus, "job-control", "jcl")], [Path.Combine(corpus, "job-control", "proc")]));
        JclJob Job(string n) => jobs.Value.Single(j => j.Name == n);

        Guard(r, "JclConditionalRegression", "if-then-else", () =>
        {
            var j = Job("COND01");
            r.Check("JclConditionalRegression", "catalog keeps every EXEC", j.Steps.Select(s => s.Step).SequenceEqual(["STEP1", "STEP2", "STEP3", "STEP4"]), string.Join(",", j.Steps.Select(s => s.Step)));
            r.Check("JclConditionalRegression", "guarded EXECs are conditional", j.Steps.Where(s => s.Conditional).Select(s => s.Step).SequenceEqual(["STEP2", "STEP3"]));
            r.Check("JclConditionalRegression", "expanded job is unconditional", j.Expanded.Select(s => s.Step).SequenceEqual(["STEP1", "STEP4"]));
            r.Check("JclConditionalRegression", "COND retained", j.Steps.Single(s => s.Step == "STEP4").Cond == "(4,LT)", j.Steps.Single(s => s.Step == "STEP4").Cond ?? "null");
        });
        Guard(r, "JclInstreamDataRegression", "in-stream", () =>
        {
            var s = Job("INSTRM").Steps.Single();
            var d = s.Datasets.ToDictionary(x => x.Dd);
            r.Check("JclInstreamDataRegression", "DD * ends at /*", d["SYSIN"].InStream?.Trim() == "SORT FIELDS=(1,5,CH,A)", d["SYSIN"].InStream ?? "null");
            r.Check("JclInstreamDataRegression", "DD DATA,DLM keeps // lines", d["SYMNAMES"].InStream?.Contains("//NOT-A-STATEMENT") == true && d["SYMNAMES"].InStream!.Contains("KEY,1,5,CH"), d["SYMNAMES"].InStream ?? "null");
            r.Check("JclInstreamDataRegression", "continuation carries DCB", d["SORTOUT"].Lrecl == "80" && d["SORTOUT"].Recfm == "FB", $"{d["SORTOUT"].Recfm}/{d["SORTOUT"].Lrecl}");
            r.Check("JclInstreamDataRegression", "directions from DISP", d["SORTIN"].Direction == "consumer" && d["SORTOUT"].Direction == "producer", $"{d["SORTIN"].Direction}/{d["SORTOUT"].Direction}");
        });
        Guard(r, "JclNestedProcRegression", "nested", () =>
        {
            var j = Job("NESTPROC");
            var inner = j.Steps.SingleOrDefault(s => s.Program == "INNERPGM");
            var o2 = j.Steps.SingleOrDefault(s => s.QualifiedStep.EndsWith("O2", StringComparison.Ordinal));
            r.Check("JclNestedProcRegression", "nested PROC step expanded", inner is not null, string.Join(",", j.Steps.Select(s => s.QualifiedStep + ":" + s.Program)));
            r.Check("JclNestedProcRegression", "caller symbol reaches inner PARM", inner?.Parm == "QA", inner?.Parm ?? "null");
            r.Check("JclNestedProcRegression", "inner DSN substituted", inner?.Datasets.Single(d => d.Dd == "INFILE").Dsn == "QA.INNER.DATA", inner?.Datasets.FirstOrDefault()?.Dsn ?? "null");
            r.Check("JclNestedProcRegression", "invocation chain names both PROCs",
                inner is not null && inner.InvocationChain.Any(c => c.Contains("OUTER")) && inner.InvocationChain.Any(c => c.Contains("INNER")), string.Join(" > ", inner?.InvocationChain ?? []));
            r.Check("JclNestedProcRegression", "procstep.DD override", o2?.Datasets.Single(d => d.Dd == "DD1").Dsn == "OVERRIDE.DSN", o2?.Datasets.FirstOrDefault(d => d.Dd == "DD1")?.Dsn ?? "null");
            r.Check("JclNestedProcRegression", "SET symbol through caller override", o2?.Datasets.Single(d => d.Dd == "DD2").Dsn == "QA.OUTER.JAN", o2?.Datasets.FirstOrDefault(d => d.Dd == "DD2")?.Dsn ?? "null");
        });
        if (!Directory.Exists(carddemo)) { if (r.Wants("JclEstateInventoryRegression")) r.Blocked("JclEstateInventoryRegression", "carddemo", $"{carddemo} not found"); return; }
        var estate = new Lazy<List<JclJob>>(() => JclHarvester.Harvest([Path.Combine(carddemo, "jcl")], [Path.Combine(carddemo, "proc")]));
        Guard(r, "JclEstateInventoryRegression", "inventory", () =>
        {
            r.Check("JclEstateInventoryRegression", "38 jobs", estate.Value.Count == 38, estate.Value.Count.ToString());
            r.Check("JclEstateInventoryRegression", "101 steps", estate.Value.Sum(j => j.Steps.Count) == 101, estate.Value.Sum(j => j.Steps.Count).ToString());
        });
        Guard(r, "JobResolutionRegression", "resolution", () =>
        {
            var t = estate.Value.Single(j => j.Name == "TRANREPT").Expanded.ToList();
            r.Check("JobResolutionRegression", "TRANREPT = REPROC IDCAMS, SORT, CBTRN03C", t.Select(s => s.Program).SequenceEqual(["IDCAMS", "SORT", "CBTRN03C"]), string.Join(",", t.Select(s => s.Program)));
            r.Check("JobResolutionRegression", "PROC step qualified", t[0].QualifiedStep == "STEP05R.PRC001" && t[0].Datasets.Any(d => d.Dd == "SYSIN" && d.Member == "REPROCT"), t[0].QualifiedStep);
            var p = estate.Value.Single(j => j.Name == "POSTTRAN").Expanded.Single();
            r.Check("JobResolutionRegression", "POSTTRAN DALYREJS(+1)", p.Datasets.Single(d => d.Dd == "DALYREJS").Generation == "+1");
            var i = estate.Value.Single(j => j.Name == "INTCALC").Expanded.Single();
            r.Check("JobResolutionRegression", "INTCALC PARM", i.Parm == "2022071800", i.Parm ?? "null");
        });
    }

    // ------------------------------------------------------------------ DD binding (§5.5.2) and replay plan (§5.6.4)

    static Dictionary<string, string> CorpusPrograms(string corpus) =>
        CobolFiles.Programs(Path.Combine(corpus, "job-control", "cbl")).ToDictionary(p => Path.GetFileNameWithoutExtension(p).ToUpperInvariant(), p => p, StringComparer.OrdinalIgnoreCase);

    static Dictionary<string, string> EstatePrograms(string carddemo) =>
        CobolFiles.Programs(Path.Combine(carddemo, "cbl")).ToDictionary(p => Path.GetFileNameWithoutExtension(p).ToUpperInvariant(), p => p, StringComparer.OrdinalIgnoreCase);

    static JclJob CorpusJob(string corpus, string name) =>
        JclHarvester.Harvest([Path.Combine(corpus, "job-control", "jcl")], [Path.Combine(corpus, "job-control", "proc")]).Single(j => j.Name == name);

    static JclJob EstateJob(string carddemo, string name) =>
        JclHarvester.Harvest([Path.Combine(carddemo, "jcl")], [Path.Combine(carddemo, "proc")]).Single(j => j.Name == name);

    public static void DdBinding(Report r, string corpus, string carddemo)
    {
        const string suite = "DdBindingRegression";
        Guard(r, suite, "statuses", () =>
        {
            var plan = DdBindingPlanner.Plan(CorpusJob(corpus, "BIND01"), CorpusPrograms(corpus), "2026-10-01", out _);
            var b = plan.Steps.Single().Bindings.ToDictionary(x => x.Dd);
            void Is(string dd, DdBindingStatus st) => r.Check(suite, $"{dd} is {st}", b.TryGetValue(dd, out var x) && x.Status == st.ToString(), b.TryGetValue(dd, out var y) ? y.Status + " " + y.Note : "no binding");
            Is("INFILE", DdBindingStatus.Bound);
            Is("OUTFILE", DdBindingStatus.LengthMismatch);
            Is("AUDITF", DdBindingStatus.Dummy);
            Is("REPORTF", DdBindingStatus.NoDdInStep);
            Is("SYMF", DdBindingStatus.UnresolvedSymbolic);
            r.Check(suite, "ASSIGN UT-S-SYMF binds DD SYMF", b.ContainsKey("SYMF"));
            r.Check(suite, "directions from OPEN", b["INFILE"].Direction == "input" && b["OUTFILE"].Direction == "output", $"{b["INFILE"].Direction}/{b["OUTFILE"].Direction}");
            r.Check(suite, "SYSOUT unclaimed as sysout", plan.Steps.Single().UnclaimedDds.Any(u => u.Dd == "SYSOUT" && u.Kind == "sysout"));
            r.Check(suite, "unbound lists every non-bound join", plan.Unbound.Count == 3, string.Join("; ", plan.Unbound));
            r.Check(suite, "deterministic JSON", DdBindingPlanner.ToJson(plan) == DdBindingPlanner.ToJson(DdBindingPlanner.Plan(CorpusJob(corpus, "BIND01"), CorpusPrograms(corpus), "2026-10-01", out _)));
        });
        if (!Directory.Exists(carddemo)) return;
        Guard(r, suite, "carddemo", () =>
        {
            foreach (var (job, count) in new[] { ("POSTTRAN", 6), ("INTCALC", 5), ("TRANREPT", 6) })
            {
                var plan = DdBindingPlanner.Plan(EstateJob(carddemo, job), EstatePrograms(carddemo), "2026-10-01", out _);
                r.Check(suite, $"{job}: {count} bindings, all bound", plan.Unbound.Count == 0 && plan.Steps.Sum(s => s.Bindings.Count) == count,
                    $"{plan.Steps.Sum(s => s.Bindings.Count)} bindings; {string.Join("; ", plan.Unbound)}");
            }
        });
    }

    public static void Replay(Report r, string corpus, string carddemo)
    {
        const string suite = "ReplayPlanRegression";
        Guard(r, suite, "fixture", () =>
        {
            var plan = DdBindingPlanner.Plan(CorpusJob(corpus, "BIND01"), CorpusPrograms(corpus), "2026-10-01", out var models);
            var replay = ReplayPlanBuilder.Build(plan, models, "bind01");
            var o = replay.Steps.Single().Outputs.Single(x => x.Dd == "OUTFILE");
            r.Check(suite, "WRITE FROM shape is the output layout", o.Layouts.Single().Record == "WS-OUT");
            r.Check(suite, "clock-derived field is volatile", o.VolatileFields.SequenceEqual(["WS-OUT.WS-OUT-TS"]), string.Join(",", o.VolatileFields));
            r.Check(suite, "writers cite the MOVE", o.Writers.TryGetValue("WS-OUT.WS-OUT-TS", out var w) && w.Any(x => x.Contains("MOVE WS-NOW TO WS-OUT-TS")), string.Join(" | ", w ?? []));
            r.Check(suite, "unbound joins become frontiers", replay.Frontiers.Count == 3, string.Join("; ", replay.Frontiers));
            r.Check(suite, "leaf offsets", o.Layouts.Single().Fields.Select(f => (f.Name, f.Offset, f.Length)).SequenceEqual([("WS-OUT-DATA", 0, 54), ("WS-OUT-TS", 54, 26)]));
        });
        if (!Directory.Exists(carddemo)) return;
        Guard(r, suite, "carddemo", () =>
        {
            ReplayOutput Out(string job, string dd)
            {
                var plan = DdBindingPlanner.Plan(EstateJob(carddemo, job), EstatePrograms(carddemo), "2026-10-01", out var models);
                return ReplayPlanBuilder.Build(plan, models, job).Steps.SelectMany(s => s.Outputs).Single(o => o.Dd == dd);
            }
            r.Check(suite, "POSTTRAN TRANFILE volatile = TRAN-PROC-TS", Out("POSTTRAN", "TRANFILE").VolatileFields.SequenceEqual(["TRAN-RECORD.TRAN-PROC-TS"]));
            r.Check(suite, "POSTTRAN DALYREJS has no volatile field", Out("POSTTRAN", "DALYREJS").VolatileFields.Count == 0);
            r.Check(suite, "INTCALC TRANSACT volatile = ORIG-TS, PROC-TS", Out("INTCALC", "TRANSACT").VolatileFields.SequenceEqual(["TRAN-RECORD.TRAN-ORIG-TS", "TRAN-RECORD.TRAN-PROC-TS"]));
        });
    }

    // ------------------------------------------------------------------ partition planning (Q12)

    public static void DomainBatch(Report r, string corpus)
    {
        const string suite = "DomainBatchRegression";
        Guard(r, suite, "calcdemo", () =>
        {
            var m = CobolModelBuilder.BuildGroundedFull(Path.Combine(corpus, "projection", "CALCDEMO.cbl"));
            var plan = BulkPartitionPlanner.Plan(m, ProgramGraphBuilder.Build(m));
            var eff = plan.Paragraphs.ToDictionary(p => p.Paragraph, p => p.Effect);
            r.Check(suite, "effects", eff["MAIN-PARA"] == "Orchestration" && eff["SETUP-PARA"] == "Domain" && eff["CALC-PARA"] == "Domain" && eff["REPORT-PARA"] == "Console",
                string.Join(", ", eff.Select(kv => kv.Key + "=" + kv.Value)));
            r.Check(suite, "two single-paragraph clusters", plan.Clusters.Count == 2 && plan.Clusters.All(c => c.Paragraphs.Count == 1), string.Join("; ", plan.Clusters.Select(c => c.Id + ":" + string.Join(",", c.Paragraphs))));
            r.Check(suite, "summary row", BulkPartitionPlanner.SummaryRow(plan).StartsWith("CALCDEMO,4,4,", StringComparison.Ordinal), BulkPartitionPlanner.SummaryRow(plan));
            var fp = CertifiedCalculationProjection.Fingerprint(m.Paragraphs.Single(p => p.Name == "CALC-PARA"));
            var m2 = CobolModelBuilder.BuildGroundedFull(Path.Combine(corpus, "projection", "CALCDEMO.cbl"));
            r.Check(suite, "semantic fingerprint is stable", fp == CertifiedCalculationProjection.Fingerprint(m2.Paragraphs.Single(p => p.Name == "CALC-PARA")));
        });
    }

    // ------------------------------------------------------------------ sidecar lanes

    public static async Task Lowering(Report r, string corpus, Toolchain env)
    {
        const string suite = "Lowering";
        if (!r.Wants(suite)) return;
        if (await env.Unavailable() is { } why) { r.Blocked(suite, "lower-manifest", why); return; }
        foreach (var row in Rows(Path.Combine(corpus, "lower-manifest.tsv")))
        {
            try
            {
                var resp = await env.Transpile(Path.Combine(corpus, row[0]));
                var java = resp["javaSource"]?.GetValue<string>() ?? "";
                var abstained = resp["abstained"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];
                bool Frontiered(string verb) => abstained.Any(a => a.Contains(":" + verb + ":", StringComparison.Ordinal));
                r.Check(suite, $"{row[0]} compiles", resp["compiled"]?.GetValue<bool>() == true, resp["error"]?.ToString() ?? "");
                foreach (var s in Split(row, 1)) r.Check(suite, $"{row[0]} contains {s}", java.Contains(s, StringComparison.Ordinal));
                foreach (var v in Split(row, 2)) r.Check(suite, $"{row[0]} lowers {v}", !Frontiered(v), string.Join("; ", abstained));
                foreach (var v in Split(row, 3)) r.Check(suite, $"{row[0]} abstains {v}", Frontiered(v), string.Join("; ", abstained));
            }
            catch (Exception e) { r.Fail(suite, row[0], e); }
        }
    }

    static IEnumerable<string> Split(string[] row, int i) => i < row.Length ? row[i].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];

    static string Pid(JsonNode resp) => resp["programId"]?.GetValue<string>() ?? "?";

    static string ClassOf(JsonNode resp) => (resp["className"]?.GetValue<string>() ?? "Prog_" + Pid(resp)).Split('.')[^1];

    public static async Task Truth(Report r, string corpus, Toolchain env)
    {
        const string suite = "TruthTable";
        if (!r.Wants(suite)) return;
        if (await env.Unavailable() is { } why) { r.Blocked(suite, "truth-manifest", why); return; }
        var work = Directory.CreateTempSubdirectory("mlcpu-truth-").FullName;
        var compiled = new Dictionary<string, (string Classes, string Pid)>();
        foreach (var row in Rows(Path.Combine(corpus, "truth-manifest.tsv")))
        {
            var (path, field) = (row[0], row[1]);
            try
            {
                if (!compiled.TryGetValue(path, out var c))
                {
                    var resp = await env.Transpile(Path.Combine(corpus, path));
                    var dir = Path.Combine(work, Path.GetFileNameWithoutExtension(path));
                    c = (env.CompileGenerated(dir, [(ClassOf(resp), resp["javaSource"]!.GetValue<string>())]), Pid(resp));
                    compiled[path] = c;
                }
                foreach (var kase in row[2].Split('|'))
                {
                    var arrow = kase.LastIndexOf("=>", StringComparison.Ordinal);
                    var (inputs, expected) = (kase[..arrow], kase[(arrow + 2)..]);
                    var a = new List<string> { "--program", c.Pid };
                    foreach (var kv in inputs.Split(';', StringSplitOptions.RemoveEmptyEntries)) { a.Add("--in"); a.Add(kv); }
                    a.Add("--out"); a.Add(field);
                    var run = env.RunJava(c.Classes, "com.mlcpu.rt.TruthRunner", a);
                    var line = run.Output.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith(field + "=", StringComparison.Ordinal));
                    var actual = line?[(field.Length + 1)..];
                    r.Check(suite, $"{Path.GetFileNameWithoutExtension(path)} {field} [{inputs}]", actual == expected, $"expected '{expected}', got '{actual ?? run.Output.Trim()}'");
                }
            }
            catch (Exception e) { r.Fail(suite, $"{path} {field}", e); }
        }
    }

    public static async Task Parity(Report r, string corpus, Toolchain env)
    {
        const string suite = "GnuCobolParity";
        if (!r.Wants(suite)) return;
        if (await env.Unavailable() is { } why) { r.Blocked(suite, "parity", why); return; }
        if (env.Cobc is null) { r.Blocked(suite, "parity", "GnuCOBOL (cobc) not found; parity never passes without it"); return; }
        var work = Directory.CreateTempSubdirectory("mlcpu-parity-").FullName;
        foreach (var src in Directory.EnumerateFiles(Path.Combine(corpus, "parity"), "*.cbl").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(src);
            try
            {
                var exe = Path.Combine(work, name + ".exe");
                var cc = Toolchain.Run(env.Cobc, ["-x", "-std=ibm", "-fsign=EBCDIC", "-I", Path.GetDirectoryName(src)!, "-o", exe, src], work);
                if (cc.Code != 0) { r.Fail(suite, name, new InvalidOperationException("cobc: " + cc.Output.Trim())); continue; }
                // <name>.dd lists DD names the program writes: each run gets its own files, compared byte for byte below.
                var ddFile = Path.ChangeExtension(src, ".dd");
                var dds = File.Exists(ddFile) ? File.ReadAllLines(ddFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToList() : [];
                var oracleEnv = dds.ToDictionary(d => "DD_" + d, d => Path.Combine(work, $"{name}.oracle.{d}.dat"));
                foreach (var f in oracleEnv.Values.Where(File.Exists)) File.Delete(f);
                var oracle = Toolchain.Run(exe, [], work, oracleEnv);
                // every program of the source (a CALLed one may follow the main one); the first one runs
                var all = await env.TranspileAll(src);
                var resp = all[0];
                var classes = env.CompileGenerated(Path.Combine(work, name), all.Select(x => (ClassOf(x), x["javaSource"]!.GetValue<string>())));
                var javaArgs = new List<string> { "--program", Pid(resp) };
                foreach (var d in dds) { javaArgs.Add("--dd"); javaArgs.Add($"{d}={Path.Combine(work, $"{name}.java.{d}.dat")}"); }
                var java = env.RunJava(classes, "com.mlcpu.rt.StepRunner", javaArgs,
                    new() { ["MLCPU_DISPLAY_NUMERIC"] = "gnucobol", ["MLCPU_BINARY_TRUNCATE"] = "no", ["MLCPU_DEFAULT_BYTE"] = "0" }, work);
                foreach (var d in dds)
                {
                    string Bytes(string who) => Path.Combine(work, $"{name}.{who}.{d}.dat") is var f && File.Exists(f) ? Convert.ToHexString(File.ReadAllBytes(f)) : "<none>";
                    var (ob, jb) = (Bytes("oracle"), Bytes("java"));
                    r.Check(suite, $"{name} file {d}", ob == jb, ob == jb ? $"{ob.Length / 2} bytes" : $"oracle {ob[..Math.Min(80, ob.Length)]} java {jb[..Math.Min(80, jb.Length)]}");
                }
                string[] Lines(string s) => s.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
                // libcob's own diagnostics on stderr ("libcob: warning: implicit CLOSE ...") are not program output
                var (o, j) = (Lines(oracle.Output).Where(l => !l.StartsWith("libcob: warning:", StringComparison.Ordinal)).ToArray(), Lines(java.Output));
                var first = Enumerable.Range(0, Math.Max(o.Length, j.Length)).FirstOrDefault(i => i >= o.Length || i >= j.Length || o[i] != j[i], -1);
                r.Check(suite, $"{name} DISPLAY output", first < 0 && oracle.Code == java.Code,
                    first < 0 ? $"rc {oracle.Code}/{java.Code}" : $"line {first + 1}: oracle '{(first < o.Length ? o[first] : "<none>")}' java '{(first < j.Length ? j[first] : "<none>")}'");
            }
            catch (Exception e) { r.Fail(suite, name, e); }
        }
    }

    // ------------------------------------------------------------------ worked examples in values (MlCpu.Kit.KitValues) against GnuCOBOL

    /// <summary>
    /// The worked examples and the shadow feedback show numbers decoded from record bytes (packed, binary, zoned) with their
    /// scale. Each corpus/kitvalues program DISPLAYs the items of a record, in order, and writes the record to DD KVOUT; every
    /// item decoded from the written bytes with the record's layout must equal what GnuCOBOL DISPLAYs (as a number, or as text).
    /// </summary>
    /// <summary>
    /// Phase 5: the record kit at scale. A LINKAGE record of more than RecordKitBuilder.MaxRecordItems items (the J3S heap)
    /// gets no class, with a note; a record of hundreds of items gets its accessor checks over several test methods (one
    /// method passed the JVM's 64 KB: FSEM0100's FS-CTX); a small kit keeps its single method, as before.
    /// </summary>
    public static void RecordKitScale(Report r, Toolchain env)
    {
        const string suite = "RecordKitScale";
        if (!r.Wants(suite)) return;
        Guard(r, suite, "kit of a large linkage", () =>
        {
            var dir = Directory.CreateTempSubdirectory("mlcpu-kitscale-").FullName;
            var sb = new StringBuilder();
            void L(string line) => sb.Append("       ").Append(line).Append('\n');
            L("IDENTIFICATION DIVISION.");
            L("PROGRAM-ID. KITSCALE.");
            L("DATA DIVISION.");
            L("LINKAGE SECTION.");
            L("01 BIG-REC.");
            for (int i = 1; i <= RecordKitBuilder.MaxRecordItems + 100; i++) L($"   05 BIG-I{i:D4} PIC 9(4).");
            L("01 MID-REC.");
            for (int i = 1; i <= 400; i++) L($"   05 MID-I{i:D4} PIC X(3).");
            L("01 SMALL-REC.");
            L("   05 SMALL-A PIC 9(4).");
            L("PROCEDURE DIVISION USING BIG-REC MID-REC SMALL-REC.");
            L("    MOVE 1 TO SMALL-A");
            L("    GOBACK.");
            var path = Path.Combine(dir, "KITSCALE.cbl");
            File.WriteAllText(path, sb.ToString());
            var model = CobolModelBuilder.BuildGroundedFull(path);
            var kit = RecordKitBuilder.Build(new RecordKitRequest("KITSCALE", "com.mlcpu.capabilities.kitscale", "subprogram", model, model, [],
                "com.mlcpu.core.Prog_KITSCALE"));
            var test = kit.Files.First(f => f.Item1.EndsWith("RecordKitTest.java", StringComparison.Ordinal)).Item2;
            r.Check(suite, "a record over the item limit gets no class, with a note", !kit.Files.Any(f => f.Item1.EndsWith("/BigRec.java", StringComparison.Ordinal))
                && kit.Notes.Any(n => n.Contains("BIG-REC") && n.Contains("no record class")), string.Join("; ", kit.Notes));
            r.Check(suite, "a large record's accessor checks span several test methods", test.Contains("void accessorsStoreAndReadLikeTheProgram1()")
                && test.Contains("void accessorsStoreAndReadLikeTheProgram3()") && !test.Contains("void accessorsStoreAndReadLikeTheProgram()"));
            var small = RecordKitBuilder.Build(new RecordKitRequest("KITSCALE", "com.mlcpu.capabilities.kitscale", "subprogram",
                CobolModelBuilder.BuildGroundedFull(Path.Combine(dir, "KITSCALE.cbl")), model, [], "com.mlcpu.core.Prog_KITSCALE"));
            r.Check(suite, "the small record keeps a class", small.Files.Any(f => f.Item1.EndsWith("/SmallRec.java", StringComparison.Ordinal)));
        });

        // Thousands of accessor checks (FSJJHM02: 28 methods) passed the 65,535 constants of one class ("too many constants"):
        // the methods go into @Nested classes of AccessorMethodsPerClass each, and the kit compiles (JUnit stubbed).
        Guard(r, suite, "kit of thousands of accessor checks", () =>
        {
            var dir = Directory.CreateTempSubdirectory("mlcpu-kitchecks-").FullName;
            var sb = new StringBuilder();
            void L(string line) => sb.Append("       ").Append(line).Append('\n');
            L("IDENTIFICATION DIVISION.");
            L("PROGRAM-ID. KITCHECK.");
            L("DATA DIVISION.");
            L("LINKAGE SECTION.");
            foreach (var rec in new[] { "WIDE-A", "WIDE-B", "WIDE-C" })
            {
                L($"01 {rec}.");
                L($"   05 {rec}-ROW OCCURS 2 TIMES.");
                for (int i = 1; i <= 800; i++) L($"      10 {rec}-F{i:D4} PIC {(i % 2 == 0 ? "S9(5)V99 COMP-3" : "X(3)")}.");
            }
            L("PROCEDURE DIVISION USING WIDE-A WIDE-B WIDE-C.");
            L("    MOVE SPACES TO WIDE-A");
            L("    GOBACK.");
            var path = Path.Combine(dir, "KITCHECK.cbl");
            File.WriteAllText(path, sb.ToString());
            var model = CobolModelBuilder.BuildGroundedFull(path);
            var kit = RecordKitBuilder.Build(new RecordKitRequest("KITCHECK", "com.mlcpu.capabilities.kitcheck", "subprogram", model, model, [],
                "com.mlcpu.core.Prog_KITCHECK"));
            var test = kit.Files.First(f => f.Item1.EndsWith("RecordKitTest.java", StringComparison.Ordinal)).Item2;
            var classes = System.Text.RegularExpressions.Regex.Split(test, @"\n    @Nested\n").Skip(1).ToList();
            int methods = System.Text.RegularExpressions.Regex.Matches(test, @"void accessorsStoreAndReadLikeTheProgram\d+\(").Count;
            r.Check(suite, "thousands of accessor checks go into @Nested classes", methods > RecordKitBuilder.AccessorMethodsPerClass
                && classes.Count == (methods + RecordKitBuilder.AccessorMethodsPerClass - 1) / RecordKitBuilder.AccessorMethodsPerClass
                && classes.All(c => System.Text.RegularExpressions.Regex.Matches(c, @"void accessorsStoreAndReadLikeTheProgram\d+\(").Count <= RecordKitBuilder.AccessorMethodsPerClass)
                && test.Contains("import org.junit.jupiter.api.Nested;"), $"{methods} methods, {classes.Count} nested classes");
            if (env.Java("javac") is not { } javac || !File.Exists(javac) || !Directory.Exists(env.RuntimeClasses))
            { r.Blocked(suite, "the kit of thousands of checks compiles", "JDK (JAVA_HOME) or runtime classes not found"); return; }
            var files = new List<string>();
            foreach (var (rel, content) in kit.Files)
            {
                var f = Path.Combine(dir, "src", rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                File.WriteAllText(f, content);
                files.Add(f);
            }
            var junit = Path.Combine(dir, "src", "stub", "org", "junit", "jupiter", "api");
            Directory.CreateDirectory(junit);
            foreach (var a in new[] { "Test", "Nested", "AfterEach" })
            {
                File.WriteAllText(Path.Combine(junit, a + ".java"), $"package org.junit.jupiter.api;\npublic @interface {a} {{}}\n");
                files.Add(Path.Combine(junit, a + ".java"));
            }
            File.WriteAllText(Path.Combine(junit, "Assertions.java"), "package org.junit.jupiter.api;\npublic final class Assertions {\n"
                + "    public static void assertArrayEquals(byte[] a, byte[] b, String m) {}\n    public static void assertEquals(Object a, Object b, String m) {}\n"
                + "    public static void assertEquals(Object a, Object b) {}\n}\n");
            files.Add(Path.Combine(junit, "Assertions.java"));
            var cc = Toolchain.Run(javac, ["-encoding", "UTF-8", "-nowarn", "-J-Xmx2g", "-cp", env.RuntimeClasses, "-d", Path.Combine(dir, "classes"), .. files], dir);
            r.Check(suite, "the kit of thousands of checks compiles", cc.Code == 0,
                string.Join(" | ", cc.Output.Split('\n').Where(l => l.Contains("error")).Take(3)));
        });
    }

    public static void KitValuesDecoding(Report r, string corpus, Toolchain env)
    {
        const string suite = "KitValues";
        if (!r.Wants(suite)) return;
        if (env.Cobc is null) { r.Blocked(suite, "decode", "GnuCOBOL (cobc) not found"); return; }
        var work = Directory.CreateTempSubdirectory("mlcpu-kitvalues-").FullName;
        foreach (var src in Directory.EnumerateFiles(Path.Combine(corpus, "kitvalues"), "*.cbl").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(src);
            Guard(r, suite, name, () =>
            {
                var exe = Path.Combine(work, name + ".exe");
                var cc = Toolchain.Run(env.Cobc, ["-x", "-std=ibm", "-fsign=EBCDIC", "-o", exe, src], work);
                if (cc.Code != 0) throw new InvalidOperationException("cobc: " + cc.Output.Trim());
                var file = Path.Combine(work, name + ".dat");
                var run = Toolchain.Run(exe, [], work, new() { ["DD_KVOUT"] = file });
                var lines = run.Output.Replace("\r\n", "\n").Split('\n').Where(l => !l.StartsWith("libcob:", StringComparison.Ordinal)).ToList();
                var record = File.ReadAllBytes(file);
                var model = CobolModelBuilder.BuildGroundedFull(src);
                var rec = model.Items.First(i => i.Level == 1 && i.FileName is null && i.Name.EndsWith("-REC", StringComparison.Ordinal));
                var fields = ReplayPlanBuilder.Layout(rec);
                r.Check(suite, $"{name} items", fields.Count > 0 && fields.Count <= lines.Count, $"{fields.Count} items, {lines.Count} DISPLAY lines");
                for (int k = 0; k < fields.Count; k++)
                {
                    var f = fields[k];
                    var kit = new MlCpu.Kit.KitField(f.Name, f.Offset, f.Length,
                        f.Usage switch { "PACKED" => "packed", "BINARY" => "binary", _ => f.Numeric ? "zoned" : "text" }, f.Pic, 0, f.Scale, f.Signed,
                        false, false, false, [], null);
                    var shown = MlCpu.Kit.KitValues.Show(kit, record, f.Offset);
                    var display = k < lines.Count ? lines[k] : "<none>";
                    bool same = kit.Numeric ? Canon(shown) == Canon(Displayed(display, f.Scale)) : shown == "'" + display.TrimEnd(' ') + "'";
                    r.Check(suite, $"{name} {f.Name} ({f.Usage} {f.Pic})", same, $"examples show {shown}, GnuCOBOL DISPLAYs {display}");
                }
            });
        }

        // GnuCOBOL (-std=ibm) DISPLAYs a numeric item as its digits with the decimal point implied and the sign as a separate
        // leading or trailing character ("+00000000010005000", "0012345-"): the value, with the point placed by the scale.
        static string Displayed(string d, int scale)
        {
            d = d.Trim();
            bool neg = d.StartsWith('-') || d.EndsWith('-');
            var digits = d.Trim('+', '-');
            if (digits.Contains('.') || scale <= 0) return (neg ? "-" : "") + digits;
            digits = digits.PadLeft(scale + 1, '0');
            return (neg ? "-" : "") + digits[..^scale] + "." + digits[^scale..];
        }

        // A number as digits: sign, integer part without leading zeros, the fraction as given ("+0001.50" and "1.50" agree).
        static string Canon(string s)
        {
            s = s.Trim();
            bool neg = s.StartsWith('-');
            s = s.TrimStart('+', '-');
            var parts = s.Split('.');
            var integer = parts[0].TrimStart('0');
            return (neg ? "-" : "") + (integer.Length == 0 ? "0" : integer) + (parts.Length > 1 ? "." + parts[1] : "");
        }
    }

    // ------------------------------------------------------------------ projections 2–10 end to end (Gradle)

    public static async Task Projections(Report r, string corpus, Toolchain env)
    {
        const string suite = "ProjectionRegression";
        if (!r.Wants(suite)) return;
        if (await env.Unavailable() is { } why) { r.Blocked(suite, "calcdemo", why); return; }
        try
        {
            var path = Path.Combine(corpus, "projection", "CALCDEMO.cbl");
            var model = CobolModelBuilder.BuildGroundedFull(path);
            var graph = ProgramGraphBuilder.Build(model);
            var plan = BulkPartitionPlanner.Plan(model, graph);
            var resp = await env.Transpile(path);
            var cls = ClassOf(resp);
            var src = resp["javaSource"]!.GetValue<string>().Replace("package com.mlcpu.gen;", "package com.mlcpu.core;");
            var unit = new ProgramUnit("CALCDEMO", cls, model, graph, plan);

            ProjectFiles Fresh()
            {
                var f = new ProjectFiles();
                f.Set("settings.gradle.kts", "rootProject.name = \"calcdemo-conformance\"\n");
                f.Set("build.gradle.kts", ProjectionBuild);
                foreach (var (file, text) in env.Runtime().Result) f.Set($"src/main/java/com/mlcpu/rt/{file}", text);
                f.Set($"src/main/java/com/mlcpu/core/{cls}.java", src);
                f.Set($"src/main/java/com/mlcpu/core/Baseline_CALCDEMO.java", src.Replace($"public final class {cls} ", "public final class Baseline_CALCDEMO "));
                var vectors = CharacterizationSeedBuilder.Build(model, graph, plan);
                f.Set("src/test/java/com/mlcpu/core/CALCDEMOCharacterizationTest.java", JavaCharacterizationRenderer.RenderTest("CALCDEMO", vectors, "2026-01-01T00:00:00Z"));
                return f;
            }

            // Fail closed: no catalog entries, no evidence.
            var closed = Fresh();
            var closedLedgers = ProjectionPipeline.Run(closed, [unit], "calcdemo");
            var byPass = closedLedgers.ToDictionary(l => l.Pass);
            r.Check(suite, "P5 without evidence: evidence-missing", byPass[DomainClusterProjection.Pass].Applied.Count == 0 && byPass[DomainClusterProjection.Pass].Skipped.All(s => s.Reason.Contains("evidence-missing")));
            r.Check(suite, "P7 without evidence: evidence-missing", byPass[OrchestrationCapabilityProjection.Pass].Skipped.Any(s => s.Reason.Contains("evidence-missing")));
            var cand = JsonNode.Parse(closed.Get("orchestration-candidates/CALCDEMO.json")!)!["candidate"]!;
            r.Check(suite, "orchestration candidate representable", cand["representable"]!.GetValue<bool>(), cand["notRepresentableBecause"]?.ToString() ?? "");

            // Certified catalogs and evidence (fixtures, as an SME would supply them).
            var java = closed.Get($"src/main/java/com/mlcpu/core/{cls}.java") ?? src;
            var setup = DomainClusterAnalyzer.Analyze(unit, src).Single(c => c.Methods.Single().Paragraph == "SETUP-PARA");
            var domainCatalog = new List<CertifiedDomainCluster>
            {
                new("CALCDEMO", setup.ClusterId, setup.CandidateHash, "SETUP-PARA", setup.Methods[0].JavaMethod, "calcdemo.domain", "InterestSetup",
                    [], [], "corpus fixture", "conformance", "2026-10-05")
            };
            var fp = CertifiedCalculationProjection.Fingerprint(model.Paragraphs.Single(p => p.Name == "CALC-PARA"));
            CertifiedCalculationSlice Slice(string fingerprint) => new("CALCDEMO", "CALC-PARA", "calcdemo.interest", "MonthlyInterest",
                [new("WS-BALANCE", "balance"), new("WS-RATE", "rate"), new("WS-TOTAL", "total")],
                [new("WS-INTEREST", "interest"), new("WS-TOTAL", "total")],
                [new("typical", ["1234.56", "18.50", "0"]), new("zero-rate", ["1000.00", "0", "5.00"]), new("negative", ["-500.00", "12.00", "0"]), new("half-cent", ["100.00", "0.06", "0"])],
                fingerprint, "corpus fixture: CALCDEMO.cbl CALC-PARA", "conformance", "2026-10-05", CalculationRoutingStage.ShadowReady);
            var evidence = Directory.CreateTempSubdirectory("mlcpu-evidence-").FullName;
            var orch = OrchestrationCandidateAnalyzer.Analyze(unit, src, "")!;
            File.WriteAllText(Path.Combine(evidence, "CALCDEMO.orchestration-evidence.json"), JsonSerializer.Serialize(new OrchestrationPromotionEvidence(
                orch.CandidateHash, "fixture", 3, 3, 0, 0, true, true, ["MAIN-PARA"], 0, 10, 10, 4, 4, true, "conformance SME fixture")));

            // Drifted fingerprint: refused, faithful core retained.
            var drifted = Fresh();
            var dl = ProjectionPipeline.Run(drifted, [unit], "calcdemo", new ProjectionOptions(Calculations: [Slice(new string('0', 64))]));
            r.Check(suite, "P6 drifted fingerprint refused", dl.Single(l => l.Pass == CertifiedCalculationProjection.Pass).Applied.Count == 0
                && drifted.Get("calculation-projection-rejections.json")?.Contains("semantics drifted") == true);

            // Approved: every projection applies.
            var files = Fresh();
            var ledgers = ProjectionPipeline.Run(files, [unit], "calcdemo", new ProjectionOptions(false, null, evidence, [Slice(fp)], domainCatalog)).ToDictionary(l => l.Pass);
            var pkg = JavaNames.Package(ProgramSemanticNameCatalog.ForProgram("CALCDEMO").Package);
            var legacy = $"src/main/java/com/mlcpu/capabilities/{pkg}/legacy";
            var prog = files.Get($"{legacy}/{cls}.java") ?? "";
            r.Check(suite, "P2 command/result/service", files.Exists("src/main/java/com/mlcpu/services/CALCDEMOService.java"));
            r.Check(suite, "P3 state split", files.Exists($"{legacy}/State_CALCDEMO.java") && prog.Contains("extends State_CALCDEMO"));
            r.Check(suite, "P5 certified cluster moved to inner class", ledgers[DomainClusterProjection.Pass].Applied.Count == 1 && prog.Contains($"final class {setup.ClusterId} {{"),
                string.Join("; ", ledgers[DomainClusterProjection.Pass].Skipped.Select(s => s.Reason)));
            r.Check(suite, "P6 calculator and differential test", files.Exists("src/main/java/com/mlcpu/capabilities/calcdemointerest/MonthlyInterestCalculator.java")
                && files.Exists("src/test/java/com/mlcpu/capabilities/calcdemointerest/MonthlyInterestDifferentialTest.java") && prog.Contains("_calcMonthlyInterest"),
                string.Join("; ", ledgers[CertifiedCalculationProjection.Pass].Skipped.Select(s => s.Reason)) + files.Get("calculation-projection-rejections.json"));
            var workflow = ProgramSemanticNameCatalog.ForProgram("CALCDEMO").Workflow;
            r.Check(suite, "P7 typed workflow over a port", files.Get($"src/main/java/com/mlcpu/capabilities/{pkg}/{workflow}.java")?.Contains("public interface Port") == true
                && prog.Contains($".{workflow}().run("), string.Join("; ", ledgers[OrchestrationCapabilityProjection.Pass].Skipped.Select(s => s.Reason)));
            r.Check(suite, "P8 facade skipped because P7 owns the workflow", ledgers[ProgramSemanticFacadeProjection.Pass].Skipped.Any(s => s.Reason.Contains("already exists")));
            r.Check(suite, "P9 compatibility wrapper", files.Get($"src/main/java/com/mlcpu/core/{cls}.java")?.Contains($"extends com.mlcpu.capabilities.{pkg}.legacy.{cls}") == true);

            // Build and run every generated test: characterization (projected Prog vs frozen Baseline), the calculator
            // differential test, the codec round trips and the facade tests.
            var dir = Directory.CreateTempSubdirectory("mlcpu-calcdemo-").FullName;
            foreach (var (p, content, _) in files.All())
            {
                var full = Path.Combine(dir, p);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content.Replace("\r\n", "\n"));
            }
            var gradle = OperatingSystem.IsWindows() ? "cmd" : "gradle";
            string[] gargs = OperatingSystem.IsWindows() ? ["/c", "gradle", "test", "--console=plain", "-q"] : ["test", "--console=plain", "-q"];
            var g = Toolchain.Run(gradle, gargs, dir);
            var results = Directory.Exists(Path.Combine(dir, "build", "test-results", "test"))
                ? Directory.EnumerateFiles(Path.Combine(dir, "build", "test-results", "test"), "*.xml").Select(f => System.Xml.Linq.XDocument.Load(f).Root!).ToList() : [];
            int Count(string suiteName, string attr) => results.Where(x => ((string?)x.Attribute("name") ?? "").EndsWith(suiteName)).Sum(x => (int?)x.Attribute(attr) ?? 0);
            r.Check(suite, "gradle test passes", g.Code == 0, g.Output.Split('\n').FirstOrDefault(l => l.Contains("FAILED") || l.Contains("error:")) ?? g.Output[..Math.Min(400, g.Output.Length)]);
            r.Check(suite, "differential test ran 4 vectors", Count("MonthlyInterestDifferentialTest", "tests") == 4 && Count("MonthlyInterestDifferentialTest", "failures") == 0);
            r.Check(suite, "characterization covers the projected program", Count("CALCDEMOCharacterizationTest", "tests") > 0 && Count("CALCDEMOCharacterizationTest", "failures") == 0,
                $"{Count("CALCDEMOCharacterizationTest", "tests")} tests");
        }
        catch (Exception e) { r.Fail(suite, "calcdemo", e); }

        // Phase 5: a program past the JVM's per-method limit (PARITY23: field views, _init and _fields in helper methods)
        // splits into State_ and Prog_ with its helpers on the state side, and both compile.
        try
        {
            var big = Path.Combine(corpus, "parity", "PARITY23.cbl");
            var resp = await env.Transpile(big);
            var cls = ClassOf(resp);
            var src = resp["javaSource"]!.GetValue<string>().Replace("package com.mlcpu.gen;", "package com.mlcpu.core;");
            var (state, prog, reason) = ProgramStateProjection.SplitOne("PARITY23", src);
            r.Check(suite, "P3 state split of a large program moves its _init/_fields helpers", reason is null && state!.Contains("void _fields0(")
                && state.Contains("void _init0(") && !prog!.Contains("void _fields0(") && !prog.Contains("void _init0("), reason ?? "");
            if (reason is null)
            {
                var work = Directory.CreateTempSubdirectory("mlcpu-bigsplit-").FullName;
                env.CompileGenerated(work, [("State_PARITY23", state!), (cls, prog!)]);
                r.Check(suite, "P3 state split of a large program compiles", true);
            }
        }
        catch (Exception e) { r.Fail(suite, "large program state split", e); }

        try
        {
            var composed = await new Toolchain(env.Repo, composeStorage: true).Transpile(Path.Combine(corpus, "projection", "CALCDEMO.cbl"));
            var src = composed["javaSource"]!.GetValue<string>();
            var faithful = composed["faithfulJavaSource"]!.GetValue<string>();
            var (state, prog, reason) = ProgramStateProjection.SplitOne("CALCDEMO", src.Replace("package com.mlcpu.gen;", "package com.mlcpu.core;"));
            r.Check(suite, "composed storage cannot be converted back to inheritance",
                state is null && prog is null && reason?.Contains("storage composition already emitted") == true, reason ?? "");
            r.Check(suite, "composed storage preserves an independent faithful baseline",
                src.Contains("private final Storage _storage") && !faithful.Contains("private final Storage _storage")
                && composed["compiled"]?.GetValue<bool>() == true);
        }
        catch (Exception e) { r.Fail(suite, "composed storage projection guard", e); }
    }

    const string ProjectionBuild = """
        plugins { java }
        java { sourceCompatibility = JavaVersion.VERSION_21; targetCompatibility = JavaVersion.VERSION_21 }
        repositories { mavenCentral() }
        dependencies {
            implementation(platform("org.springframework.boot:spring-boot-dependencies:3.3.4"))
            implementation("org.springframework.boot:spring-boot-starter")
            testImplementation(platform("org.junit:junit-bom:5.11.3"))
            testImplementation("org.junit.jupiter:junit-jupiter")
            testImplementation("org.junit.jupiter:junit-jupiter-params")
            testRuntimeOnly("org.junit.platform:junit-platform-launcher")
        }
        tasks.withType<JavaCompile>().configureEach { options.encoding = "UTF-8" }
        tasks.test {
            useJUnitPlatform()
            jvmArgs("-Xss64m")
            systemProperty("mlcpu.characterization.dir", "${projectDir}/characterization")
        }

        """;
}
