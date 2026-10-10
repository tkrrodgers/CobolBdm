using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MlCpu.Cobol.Analysis;
using MlCpu.Cobol.Analysis.Entities;
using MlCpu.Cobol.Analysis.Projections;
using MlCpu.CobolKb.Knowledge;
using MlCpu.Core;

static class Suites
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
                var cc = Toolchain.Run(env.Cobc, ["-x", "-std=ibm", "-fsign=EBCDIC", "-o", exe, src], work);
                if (cc.Code != 0) { r.Fail(suite, name, new InvalidOperationException("cobc: " + cc.Output.Trim())); continue; }
                var oracle = Toolchain.Run(exe, [], work);
                var resp = await env.Transpile(src);
                var classes = env.CompileGenerated(Path.Combine(work, name), [(ClassOf(resp), resp["javaSource"]!.GetValue<string>())]);
                var java = env.RunJava(classes, "com.mlcpu.rt.StepRunner", ["--program", Pid(resp)],
                    new() { ["MLCPU_DISPLAY_NUMERIC"] = "gnucobol", ["MLCPU_BINARY_TRUNCATE"] = "no", ["MLCPU_DEFAULT_BYTE"] = "0" }, work);
                string[] Lines(string s) => s.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
                var (o, j) = (Lines(oracle.Output), Lines(java.Output));
                var first = Enumerable.Range(0, Math.Max(o.Length, j.Length)).FirstOrDefault(i => i >= o.Length || i >= j.Length || o[i] != j[i], -1);
                r.Check(suite, $"{name} DISPLAY output", first < 0 && oracle.Code == java.Code,
                    first < 0 ? $"rc {oracle.Code}/{java.Code}" : $"line {first + 1}: oracle '{(first < o.Length ? o[first] : "<none>")}' java '{(first < j.Length ? j[first] : "<none>")}'");
            }
            catch (Exception e) { r.Fail(suite, name, e); }
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
