using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MlCpu.Cobol.Analysis;
using MlCpu.CobolKb.Knowledge;

namespace MlCpu.Host.Services.Workspace;

/// <summary>One GnuCOBOL executable: a main program (with its PARM driver when the step passes one), its CALL closure and stubs.</summary>
public sealed record OracleUnit(string Id, string Main, IReadOnlyList<string> Programs, bool ParmDriver, string Exe);

/// <summary>A step the oracle ran: program or utility, return code, and the datasets it read and wrote.</summary>
public sealed record OracleStepRecord(string Job, string StepId, string? Program, string? Parm, int ReturnCode, string Detail,
    IReadOnlyDictionary<string, string> Dds, IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs, string SysoutFile);

/// <summary>
/// The GnuCOBOL oracle for a generic project (§5.6.5): the original COBOL compiled with cobc (-std=ibm -fsign=EBCDIC)
/// and run job by job over prepared datasets with the clock pinned. Executables are built once and reused while their
/// content hash (sources, copybooks, stubs, flags, cobc version) is unchanged: oracle/bin/build-manifest.json.
/// Utilities replay the job engine's semantics (IEFBR14, compile/link no-ops, IEBGENER, IDCAMS REPRO/DELETE/DEFINE,
/// SORT through com.mlcpu.rt.SortReplay), so both sides differ only in the programs.
/// Each run keeps per-job and per-step snapshots (oracle/runs/&lt;scenario&gt;/jobs, steps) — the job checks and the
/// equivalence-test resources.
/// </summary>
public sealed class GnuCobolOracle(ProjectService projects, ConfigService config)
{
    static readonly Encoding Latin1 = Encoding.Latin1;
    static readonly JsonSerializerOptions Json = new(ProjectJson.Options);

    public static readonly HashSet<string> NoOpUtilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "IEFBR14", "IGYCRCTL", "IGYWCL", "IGYWCLG", "IEWL", "HEWL", "IEWBLINK", "HEWLH096", "IEWLF440", "ASMA90", "IBMZPLI", "IEL1AA", "CCNDRVR", "DSNHPC"
    };

    const string Cee3abdStub = """
               IDENTIFICATION DIVISION.
               PROGRAM-ID. CEE3ABD.
               DATA DIVISION.
               LINKAGE SECTION.
               01 LK-CODE   PIC S9(9) BINARY.
               01 LK-TIMING PIC S9(9) BINARY.
               PROCEDURE DIVISION USING LK-CODE LK-TIMING.
                   DISPLAY 'ABEND CEE3ABD'
                   MOVE 16 TO RETURN-CODE
                   STOP RUN.
        """;

    static string ParmDriver(string main) => $"""
               IDENTIFICATION DIVISION.
               PROGRAM-ID. DRVPARM.
               DATA DIVISION.
               WORKING-STORAGE SECTION.
               01 PARM-AREA.
                  05 PARM-LEN   PIC S9(4) COMP.
                  05 PARM-TEXT  PIC X(100).
               01 ENV-PARM      PIC X(100).
               PROCEDURE DIVISION.
                   ACCEPT ENV-PARM FROM ENVIRONMENT 'MLCPU_PARM'
                   MOVE ENV-PARM TO PARM-TEXT
                   MOVE FUNCTION LENGTH(FUNCTION TRIM(ENV-PARM)) TO PARM-LEN
                   CALL '{main}' USING PARM-AREA
                   STOP RUN.
        """;

    // ------------------------------------------------------------------ plan and build

    sealed record Context(ProjectDefinition Project, ProjectPaths Paths, string SourceDir, Dictionary<string, string> Sources,
        IReadOnlyList<JclJob> Jobs, Dictionary<string, CobolProgram> Models);

    Context Load(ProjectDefinition p)
    {
        var src = projects.SourceDir(p);
        var sources = OnboardingService.Sources(p, src);
        var jobs = OnboardingService.Harvest(p, src).Where(j => p.Oracle.Jobs.Contains(j.Name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(j => p.Oracle.Jobs.FindIndex(x => x.Equals(j.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        return new Context(p, projects.Paths(p), src, sources, jobs, new(StringComparer.OrdinalIgnoreCase));
    }

    CobolProgram Model(Context c, string pid)
    {
        if (!c.Models.TryGetValue(pid, out var m)) c.Models[pid] = m = CobolModelBuilder.BuildGroundedFull(c.Sources[pid]);
        return m;
    }

    /// <summary>Executables the jobs need: one per (main program, PARM or not).</summary>
    List<OracleUnit> Units(Context c)
    {
        var units = new Dictionary<string, OracleUnit>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in c.Jobs)
            foreach (var s in job.Expanded.Where(s => s.Program is not null && c.Sources.ContainsKey(s.Program)))
            {
                var pid = s.Program!.ToUpperInvariant();
                var diagnostics = new List<string>();
                var closure = DdBindingPlanner.CallClosure(pid, c.Sources, diagnostics, c.Models);
                bool driver = s.Parm is not null && Model(c, pid).Using.Count > 0;
                var id = driver ? pid + "-PARM" : pid;
                if (!units.ContainsKey(id)) units[id] = new OracleUnit(id, pid, closure, driver, Path.Combine(c.Paths.OracleBin, id + ".exe"));
            }
        return units.Values.OrderBy(u => u.Id, StringComparer.Ordinal).ToList();
    }

    string[] CobcFlags(Context c)
    {
        var flags = new List<string>(c.Project.Oracle.CobcFlags);
        foreach (var root in OnboardingService.Roots(c.SourceDir, c.Project.Layout.CopybookRoots)) { flags.Add("-I"); flags.Add(root); }
        return flags.ToArray();
    }

    static string? _cobcVersion;

    static async Task<string> CobcVersion(CancellationToken ct)
    {
        if (_cobcVersion is not null) return _cobcVersion;
        var r = await Toolchain.RunAsync(Toolchain.Cobc, ["--version"], Path.GetTempPath(), ct: ct);
        return _cobcVersion = r.Output.Split('\n').FirstOrDefault()?.Trim() ?? "cobc";
    }

    /// <summary>Content hash of a program as it compiles: its source and every copybook it expands.</summary>
    string ProgramHash(Context c, string pid)
    {
        var parts = new List<string> { pid + "\n" + File.ReadAllText(c.Sources[pid], Latin1) };
        var resolve = CobolModelBuilder.CopybookResolver(c.Sources[pid]);
        foreach (var cpy in Model(c, pid).Copybooks.OrderBy(x => x, StringComparer.Ordinal))
            parts.Add(cpy + "\n" + string.Join("\n", resolve(cpy) ?? ["(missing)"]));
        return Toolchain.Sha256([.. parts]);
    }

    /// <summary>Programs cobc compiles at once (MLCPU_COBC_PARALLEL; default a quarter of the cores, at most 8).</summary>
    static int CompileParallelism => int.TryParse(Environment.GetEnvironmentVariable("MLCPU_COBC_PARALLEL"), out var n) && n > 0 ? n
        : Math.Clamp(Environment.ProcessorCount / 4, 1, 8);

    sealed class BuildManifest
    {
        public string Compiler { get; set; } = "";
        public Dictionary<string, BuildEntry> Units { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Program objects (obj/&lt;PID&gt;[.main].o) by file name: what the units link.</summary>
        public Dictionary<string, BuildEntry> Objects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    sealed class BuildEntry
    {
        public string Hash { get; set; } = "";
        public string Exe { get; set; } = "";
        public List<string> Programs { get; set; } = [];
        public string BuiltAt { get; set; } = "";
        public double Seconds { get; set; }
    }

    BuildManifest ReadManifest(string bin)
    {
        var f = Path.Combine(bin, "build-manifest.json");
        try { return File.Exists(f) ? JsonSerializer.Deserialize<BuildManifest>(File.ReadAllText(f), Json) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>Builds every unit (and the KSDS load/unload utilities) whose hash changed; returns (built, cached, problems).</summary>
    public async Task<(int Built, int Cached, List<string> Problems)> BuildAsync(ProjectDefinition p, Operation op, double from, double to, bool force, CancellationToken ct)
    {
        var c = Load(p);
        Directory.CreateDirectory(c.Paths.OracleBin);
        var version = await CobcVersion(ct);
        var flags = CobcFlags(c);
        var manifest = ReadManifest(c.Paths.OracleBin);
        manifest.Compiler = version;
        var units = Units(c);
        var ksds = p.Oracle.Datasets.Where(d => d.Format == "ksds" && d.Key is not null).ToList();
        int total = units.Count + 2 * ksds.Count, done = 0, built = 0, cached = 0;
        var problems = new List<string>();
        File.WriteAllText(Path.Combine(c.Paths.OracleBin, "CEE3ABD.cbl"), Cee3abdStub, Latin1);

        async Task Compile(string id, string exe, IReadOnlyList<string> sourceFiles, string hash, IReadOnlyList<string> programs)
        {
            ct.ThrowIfCancellationRequested();
            op.Report(from + (to - from) * done / Math.Max(1, total), $"GnuCOBOL {id}");
            if (!force && manifest.Units.TryGetValue(id, out var e) && e.Hash == hash && File.Exists(exe))
            {
                cached++;
                op.Info($"cached  {id} ({Path.GetFileName(exe)}, built {e.BuiltAt})");
            }
            else
            {
                var r = await Toolchain.RunAsync(Toolchain.Cobc, ["-x", .. flags, "-o", exe, .. sourceFiles], c.Paths.OracleBin, ct: ct);
                if (r.ExitCode != 0)
                {
                    problems.Add($"{id}: cobc failed: {Tail(r.Output, 6)}");
                    op.Info($"FAILED  {id}: {Tail(r.Output, 6)}");
                    manifest.Units.Remove(id);
                }
                else
                {
                    built++;
                    manifest.Units[id] = new BuildEntry { Hash = hash, Exe = Path.GetFileName(exe), Programs = programs.ToList(), BuiltAt = DateTime.Now.ToString("s"), Seconds = Math.Round(r.Elapsed.TotalSeconds, 1) };
                    op.Info($"built   {id} in {r.Elapsed.TotalSeconds:N1}s");
                }
            }
            done++;
        }

        // Each program is compiled once to an object (cached by its own content hash, in parallel), and each unit links the
        // objects it needs: two jobs over one CALL closure of 144 programs compiled every program twice, one after the other.
        var objDir = Directory.CreateDirectory(Path.Combine(c.Paths.OracleBin, "obj")).FullName;
        var objects = new Dictionary<string, (string Source, bool Main, string Hash)>(StringComparer.OrdinalIgnoreCase);
        var unitObjects = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string ObjectFor(string name, string source, bool main, string hash)
        {
            var obj = name + (main ? ".main" : "") + ".o";
            objects.TryAdd(obj, (source, main, Toolchain.Sha256(version, string.Join(" ", flags.Select(f => Path.IsPathRooted(f) ? Path.GetFileName(f) : f)), main ? "main" : "module", hash)));
            return obj;
        }
        foreach (var u in units)
        {
            var list = new List<string>();
            if (u.ParmDriver)
            {
                var drv = Path.Combine(c.Paths.OracleBin, $"DRVPARM-{u.Main}.cbl");
                File.WriteAllText(drv, ParmDriver(u.Main), Latin1);
                list.Add(ObjectFor($"DRVPARM-{u.Main}", drv, true, Toolchain.Sha256(ParmDriver(u.Main))));
            }
            foreach (var pid in u.Programs)
                list.Add(ObjectFor(pid, c.Sources[pid], !u.ParmDriver && pid.Equals(u.Main, StringComparison.OrdinalIgnoreCase), ProgramHash(c, pid)));
            var calls = u.Programs.SelectMany(x => ProgramGraphBuilder.Calls(Model(c, x))).Select(x => x.Target.ToUpperInvariant()).ToHashSet();
            if (calls.Contains("CEE3ABD") && !c.Sources.ContainsKey("CEE3ABD"))
                list.Add(ObjectFor("CEE3ABD", Path.Combine(c.Paths.OracleBin, "CEE3ABD.cbl"), false, Toolchain.Sha256(Cee3abdStub)));
            unitObjects[u.Id] = list;
        }
        var stale = objects.Where(o => force || !manifest.Objects.TryGetValue(o.Key, out var e) || e.Hash != o.Value.Hash || !File.Exists(Path.Combine(objDir, o.Key))).ToList();
        int compiled = 0;
        var objClock = System.Diagnostics.Stopwatch.StartNew();
        await Parallel.ForEachAsync(stale, new ParallelOptions { MaxDegreeOfParallelism = CompileParallelism, CancellationToken = ct }, async (o, token) =>
        {
            var r = await Toolchain.RunAsync(Toolchain.Cobc, ["-c", .. (o.Value.Main ? new[] { "-x" } : []), .. flags, "-o", Path.Combine(objDir, o.Key), o.Value.Source],
                objDir, ct: token);
            lock (manifest)
            {
                if (r.ExitCode != 0)
                {
                    problems.Add($"{o.Key}: cobc failed: {Tail(r.Output, 6)}");
                    op.Info($"FAILED  {o.Key}: {Tail(r.Output, 6)}");
                    manifest.Objects.Remove(o.Key);
                }
                else manifest.Objects[o.Key] = new BuildEntry { Hash = o.Value.Hash, Exe = o.Key, BuiltAt = DateTime.Now.ToString("s"), Seconds = Math.Round(r.Elapsed.TotalSeconds, 1) };
                op.Report(from + (to - from) * 0.8 * ++compiled / Math.Max(1, stale.Count), $"GnuCOBOL {compiled}/{stale.Count} programs");
            }
        });
        op.Info($"GnuCOBOL objects: {stale.Count} compiled in {objClock.Elapsed.TotalSeconds:N1}s ({CompileParallelism} in parallel), {objects.Count - stale.Count} reused");
        foreach (var u in units)
        {
            var list = unitObjects[u.Id];
            if (list.Any(o => !manifest.Objects.ContainsKey(o)))
            {
                problems.Add($"{u.Id}: not linked (an object failed to compile)");
                manifest.Units.Remove(u.Id);
                done++;
                continue;
            }
            // Linked inside obj/ with bare object names: the command line stays short whatever the number of programs.
            var hash = Toolchain.Sha256([version, .. list.Select(o => o + "=" + manifest.Objects[o].Hash)]);
            await Link(u.Id, u.Exe, list, hash, u.Programs);
        }

        async Task Link(string id, string exe, IReadOnlyList<string> objectFiles, string hash, IReadOnlyList<string> programs)
        {
            ct.ThrowIfCancellationRequested();
            op.Report(from + (to - from) * (0.8 + 0.2 * done / Math.Max(1, total)), $"GnuCOBOL link {id}");
            if (!force && manifest.Units.TryGetValue(id, out var e) && e.Hash == hash && File.Exists(exe))
            {
                cached++;
                op.Info($"cached  {id} ({Path.GetFileName(exe)}, linked {e.BuiltAt})");
            }
            else
            {
                var r = await Toolchain.RunAsync(Toolchain.Cobc, ["-x", "-o", exe, .. objectFiles], objDir, ct: ct);
                if (r.ExitCode != 0)
                {
                    problems.Add($"{id}: link failed: {Tail(r.Output, 6)}");
                    op.Info($"FAILED  {id}: {Tail(r.Output, 6)}");
                    manifest.Units.Remove(id);
                }
                else
                {
                    built++;
                    manifest.Units[id] = new BuildEntry { Hash = hash, Exe = Path.GetFileName(exe), Programs = programs.ToList(), BuiltAt = DateTime.Now.ToString("s"), Seconds = Math.Round(r.Elapsed.TotalSeconds, 1) };
                    op.Info($"linked  {id} from {objectFiles.Count} objects in {r.Elapsed.TotalSeconds:N1}s");
                }
            }
            done++;
        }
        foreach (var d in ksds)
            foreach (var kind in new[] { "LOAD", "UNLOAD" })
            {
                var id = KsdsUtilityId(kind, d.Dsn);
                var src = Path.Combine(c.Paths.OracleBin, id + ".cbl");
                var text = KsdsUtility(d, kind);
                File.WriteAllText(src, text, Latin1);
                await Compile(id, Path.Combine(c.Paths.OracleBin, id + ".exe"), [src], Toolchain.Sha256(version, string.Join(" ", p.Oracle.CobcFlags), text), []);
            }
        File.WriteAllText(Path.Combine(c.Paths.OracleBin, "build-manifest.json"), JsonSerializer.Serialize(manifest, Json) + "\n");
        op.Info($"GnuCOBOL: {built} built, {cached} reused from the cache, {problems.Count} failed ({version})");
        return (built, cached, problems);
    }

    /// <summary>
    /// The KSDS utility's file name. cobc derives a C identifier from the source's base name and refuses long ones, so a
    /// DSN such as FINERACT.CURRENCY.VSAM.KSDS is kept only as far as it fits, with a hash of the whole DSN.
    /// </summary>
    static string KsdsUtilityId(string kind, string dsn)
    {
        var safe = Safe(dsn).Replace('.', '_');
        var name = $"{kind}-{safe}";
        return name.Length <= 24 ? name : $"{kind}-{safe[..Math.Min(safe.Length, 10)]}-{Toolchain.Sha256(dsn)[..8]}";
    }

    /// <summary>LOAD: fixed image → INDEXED; UNLOAD: INDEXED → fixed image, with the keys the programs declare.</summary>
    static string KsdsUtility(DatasetDefinition d, string kind)
    {
        var segs = new List<(int Pos, int Len, string Name)>();
        var k = d.Key!.Split(',');
        segs.Add((int.Parse(k[0]), int.Parse(k[1]), "OUT-KEY"));
        int n = 0;
        foreach (var a in d.AlternateKeys)
        {
            var s = a.Split(',');
            segs.Add((int.Parse(s[0]), int.Parse(s[1]), $"OUT-ALT{++n}"));
        }
        var fields = new List<string>();
        int cur = 1;
        foreach (var s in segs.OrderBy(s => s.Pos))
        {
            if (s.Pos > cur) fields.Add($"           05 FILLER PIC X({s.Pos - cur}).");
            if (s.Pos < cur) continue; // overlapping keys are declared by their first segment only
            fields.Add($"           05 {s.Name} PIC X({s.Len}).");
            cur = s.Pos + s.Len;
        }
        if (cur <= d.Lrecl) fields.Add($"           05 FILLER PIC X({d.Lrecl - cur + 1}).");
        var alts = string.Concat(segs.Skip(1).Where(s => fields.Any(f => f.Contains(s.Name))).Select(s => $"\n               ALTERNATE RECORD KEY IS {s.Name} WITH DUPLICATES"));
        bool load = kind == "LOAD";
        return $"""
                   IDENTIFICATION DIVISION.
                   PROGRAM-ID. {(load ? "KSDSLOAD" : "KSDSUNLD")}.
                   ENVIRONMENT DIVISION.
                   INPUT-OUTPUT SECTION.
                   FILE-CONTROL.
                       SELECT SEQ-FILE ASSIGN TO SEQFILE
                           ORGANIZATION IS SEQUENTIAL
                           FILE STATUS IS WS-SEQ.
                       SELECT OUT-FILE ASSIGN TO KSDSFILE
                           ORGANIZATION IS INDEXED
                           ACCESS MODE IS DYNAMIC
                           RECORD KEY IS OUT-KEY{alts}
                           FILE STATUS IS WS-FS.
                   DATA DIVISION.
                   FILE SECTION.
                   FD SEQ-FILE.
                   01 SEQ-REC PIC X({d.Lrecl}).
                   FD OUT-FILE.
                   01 OUT-REC.
            {string.Join("\n", fields)}
                   WORKING-STORAGE SECTION.
                   01 WS-SEQ PIC XX.
                   01 WS-FS  PIC XX.
                   01 WS-EOF PIC X VALUE 'N'.
                   PROCEDURE DIVISION.
                       OPEN {(load ? "INPUT SEQ-FILE OUTPUT OUT-FILE" : "OUTPUT SEQ-FILE INPUT OUT-FILE")}
                       PERFORM UNTIL WS-EOF = 'Y'
                           READ {(load ? "SEQ-FILE" : "OUT-FILE NEXT")}
                               AT END MOVE 'Y' TO WS-EOF
                               NOT AT END
                                   {(load ? "WRITE OUT-REC FROM SEQ-REC" : "WRITE SEQ-REC FROM OUT-REC")}
                                   IF {(load ? "WS-FS" : "WS-SEQ")} NOT = '00'
                                       DISPLAY 'WRITE FAILED ' WS-FS ' ' WS-SEQ
                                       MOVE 8 TO RETURN-CODE
                                   END-IF
                           END-READ
                       END-PERFORM
                       CLOSE SEQ-FILE OUT-FILE
                       GOBACK.
            """;
    }

    // ------------------------------------------------------------------ datasets

    /// <summary>The scenario's version of a source file: projects/&lt;id&gt;/scenarios/&lt;scenario&gt;/&lt;file name&gt; when present.</summary>
    string? ScenarioFile(Context c, string scenario, DatasetDefinition d)
    {
        if (scenario == "base") return d.Source is null ? null : projects.Resolve(c.Project, d.Source);
        var dir = Path.Combine(c.Paths.Scenarios, scenario);
        foreach (var name in new[] { d.Source is null ? null : Path.GetFileName(d.Source), d.Dsn, d.Dsn + ".txt" }.OfType<string>())
            if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return d.Source is null ? null : projects.Resolve(c.Project, d.Source);
    }

    /// <summary>A dataset image from a source file: text lines become fixed (padded), variable (RDW) or key-ordered records.</summary>
    public static byte[] Image(DatasetDefinition d, string? file)
    {
        if (file is null || !File.Exists(file)) return [];
        var raw = File.ReadAllBytes(file);
        bool text = Path.GetExtension(file).Equals(".txt", StringComparison.OrdinalIgnoreCase) || raw.Contains((byte)'\n');
        if (!text || d.Format == "line") return d.Format == "line" ? Latin1.GetBytes(Latin1.GetString(raw).Replace("\r\n", "\n")) : raw;
        var lines = Latin1.GetString(raw).Replace("\r\n", "\n").Split('\n').ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var ms = new MemoryStream();
        IEnumerable<string> records = lines;
        if (d.Format == "ksds" && d.Key is { } key)
        {
            var k = key.Split(',');
            int off = int.Parse(k[0]) - 1, len = int.Parse(k[1]);
            records = lines.Select(l => l.PadRight(d.Lrecl)).OrderBy(l => l.Substring(off, Math.Min(len, Math.Max(0, l.Length - off))), StringComparer.Ordinal);
        }
        foreach (var l in records)
        {
            if (d.Format == "variable")
            {
                var rec = l.Length > d.Lrecl ? l[..d.Lrecl] : l.Length < d.MinRecord ? l.PadRight(d.MinRecord) : l;
                ms.Write([(byte)(rec.Length >> 8), (byte)(rec.Length & 0xFF), 0, 0]);
                ms.Write(Latin1.GetBytes(rec));
            }
            else
            {
                var rec = l.Length >= d.Lrecl ? l[..d.Lrecl] : l.PadRight(d.Lrecl);
                ms.Write(Latin1.GetBytes(rec));
            }
        }
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ run

    /// <summary>Runs every job of the oracle sequence for one scenario; returns its job checks.</summary>
    public async Task<List<JobCheck>> RunScenarioAsync(ProjectDefinition p, string scenario, Operation op, double from, double to, CancellationToken ct)
    {
        var c = Load(p);
        var run = Path.Combine(c.Paths.OracleRuns, scenario);
        if (Directory.Exists(run)) Toolchain.DeleteTree(run);
        var data = Directory.CreateDirectory(Path.Combine(run, "data")).FullName;
        var sysoutRoot = Directory.CreateDirectory(Path.Combine(run, "sysout")).FullName;
        var work = Directory.CreateDirectory(Path.Combine(run, "work")).FullName;
        foreach (var d in p.Oracle.Datasets)
        {
            var img = Image(d, ScenarioFile(c, scenario, d));
            File.WriteAllBytes(JobSteps.DataFile(data, d.Dsn), img);
            op.Info($"[{scenario}] dataset {d.Dsn}: {img.Length:N0} bytes ({d.Format})");
        }
        var manifest = ReadManifest(c.Paths.OracleBin);
        var units = Units(c);
        var checks = new List<JobCheck>();
        var steps = new List<OracleStepRecord>();
        var clock = DateTimeOffset.Parse(p.Oracle.Clock, CultureInfo.InvariantCulture);
        var cobDate = clock.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "00";
        int jobIndex = 0;
        var jobs = c.Jobs.Where(j => OracleDefinition.RunsJob(p.Oracle, scenario, j.Name)).ToList();
        if (jobs.Count < c.Jobs.Count) op.Info($"[{scenario}] runs {string.Join(", ", jobs.Select(j => j.Name))} (oracle.scenarioJobs)");
        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            op.Report(from + (to - from) * jobIndex++ / Math.Max(1, jobs.Count), $"[{scenario}] GnuCOBOL job {job.Name}");
            var jobDir = Path.Combine(run, "jobs", job.Name);
            var before = Snapshot(data);
            // SYSOUT belongs to its job: two jobs with a step of the same name (STEP20) each write their own
            // <STEP>.SYSOUT.txt, as the Java job engine does in a job check (one job per run).
            var sysout = Directory.CreateDirectory(Path.Combine(sysoutRoot, job.Name)).FullName;
            var sysBefore = Snapshot(sysout);
            CopyDir(data, Path.Combine(jobDir, "in"));
            var plan = DdBindingPlanner.Plan(job, c.Sources, DateTime.Now.ToString("yyyy-MM-dd"), out var models);
            foreach (var (m, mod) in models) c.Models.TryAdd(m, mod);
            foreach (var (step, stepId) in JobSteps.Ids(job))
            {
                ct.ThrowIfCancellationRequested();
                var rec = await RunStepAsync(c, job, step, stepId, plan, units, manifest, data, sysout, work, run, cobDate, op, ct);
                steps.Add(rec);
                op.Info($"[{scenario}] {job.Name}.{stepId,-10} {rec.Program ?? "-",-9} rc={rec.ReturnCode} {rec.Detail}");
                if (rec.ReturnCode >= 12) { op.Info($"[{scenario}] {job.Name} stops after {stepId} (rc {rec.ReturnCode})"); break; }
            }
            var after = Snapshot(data);
            var sysAfter = Snapshot(sysout);
            var outDir = Path.Combine(jobDir, "out");
            var check = new JobCheck { Job = job.Name, Scenario = scenario };
            foreach (var (name, _) in before) check.Inputs[name] = Rel(c, Path.Combine(jobDir, "in", name));
            foreach (var (name, hash) in after.Where(kv => !before.TryGetValue(kv.Key, out var h) || h != kv.Value))
            {
                Directory.CreateDirectory(outDir);
                File.Copy(Path.Combine(data, name), Path.Combine(outDir, name), true);
                check.Outputs["data:" + name] = Rel(c, Path.Combine(outDir, name));
            }
            foreach (var (name, hash) in sysAfter.Where(kv => kv.Key.EndsWith(".SYSOUT.txt", StringComparison.OrdinalIgnoreCase)
                                                               && (!sysBefore.TryGetValue(kv.Key, out var h) || h != kv.Value)))
            {
                Directory.CreateDirectory(Path.Combine(outDir, "sysout"));
                File.Copy(Path.Combine(sysout, name), Path.Combine(outDir, "sysout", name), true);
                check.Outputs["sysout:" + name] = Rel(c, Path.Combine(outDir, "sysout", name));
            }
            checks.Add(check);
        }
        File.WriteAllText(Path.Combine(run, "oracle-run.json"), JsonSerializer.Serialize(new
        {
            schema = "mlcpu.oracle-run.v1", project = p.Id, scenario, clock = p.Oracle.Clock, compiler = manifest.Compiler, at = DateTime.Now.ToString("s"),
            jobChecks = checks, steps
        }, Json) + "\n");
        return checks;
    }

    async Task<OracleStepRecord> RunStepAsync(Context c, JclJob job, JclStep step, string stepId, DdBindingPlan plan, List<OracleUnit> units,
        BuildManifest manifest, string data, string sysout, string work, string run, string cobDate, Operation op, CancellationToken ct)
    {
        var pgm = step.Program?.ToUpperInvariant();
        var dds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sysoutFile = Path.Combine(sysout, stepId + ".SYSOUT.txt");
        foreach (var d in step.Datasets.Where(d => d.Dd.Length > 0))
        {
            string path;
            if (d.Sysout) path = Path.Combine(sysout, $"{stepId}.{d.Dd}.txt");
            else if (d.InStream is not null) { path = Path.Combine(sysout, $"{stepId}.{d.Dd}.instream"); File.WriteAllText(path, d.InStream + "\n", Latin1); }
            else if (d.Dummy || d.Dsn is null) { path = Path.Combine(sysout, $"{stepId}.{d.Dd}.dummy"); File.WriteAllBytes(path, []); }
            else if (d.Member is not null) path = ControlCard(c, d.Member) ?? JobSteps.DataFile(data, $"{d.Dsn}({d.Member})");
            else path = JobSteps.DataFile(data, d.Dsn);
            dds.TryAdd(d.Dd, path);
        }
        if (dds.ContainsKey("SYSOUT")) dds["SYSOUT"] = sysoutFile;
        var stepPlan = plan.Steps.FirstOrDefault(s => s.Order == step.Order);
        var reads = new List<string>();
        var writes = new List<string>();
        foreach (var b in stepPlan?.Bindings ?? [])
        {
            if (b.ResolvedDsn is null || b.IsDummy) continue;
            if (b.Direction.Contains("input") || b.Direction.Contains("i-o") || b.Direction.Contains("extend")) reads.Add(b.ResolvedDsn);
            if (b.Direction.Contains("output") || b.Direction.Contains("i-o") || b.Direction.Contains("extend")) writes.Add(b.ResolvedDsn);
        }
        var stepDir = Path.Combine(run, "steps", $"{job.Name}.{stepId}");

        if (pgm is not null && c.Sources.ContainsKey(pgm))
        {
            var unit = units.FirstOrDefault(u => u.Main == pgm && u.ParmDriver == (step.Parm is not null && Model(c, pgm).Using.Count > 0));
            if (unit is null || !File.Exists(unit.Exe) || !manifest.Units.ContainsKey(unit.Id))
                return new OracleStepRecord(job.Name, stepId, pgm, step.Parm, 12, "not built (GnuCOBOL compile failed)", dds, reads, writes, Rel(c, sysoutFile));
            foreach (var dsn in reads.Concat(writes).Distinct()) CopyIfExists(JobSteps.DataFile(data, dsn), Path.Combine(stepDir, "in", dsn));
            // INDEXED files: load the image into the file handler's format before the step, unload what the step wrote after it.
            var env = new Dictionary<string, string> { ["COB_CURRENT_DATE"] = cobDate };
            var indexed = new List<(string Dsn, string Bdb, bool Writes)>();
            foreach (var b in (stepPlan?.Bindings ?? []).Where(b => b.ResolvedDsn is not null && !b.IsDummy))
            {
                var fd = c.Models.TryGetValue(b.OwnerProgram, out var om) ? om.Files.FirstOrDefault(f => f.Name == b.LogicalFile) : null;
                if (fd is null || !fd.Organization.Contains("INDEXED", StringComparison.OrdinalIgnoreCase)) continue;
                var dsn = b.ResolvedDsn!;
                var bdb = Path.Combine(work, $"{Safe(dsn)}.{stepId}.bdb");
                var img = JobSteps.DataFile(data, dsn);
                if (File.Exists(img))
                {
                    var loader = Path.Combine(c.Paths.OracleBin, KsdsUtilityId("LOAD", dsn) + ".exe");
                    if (!File.Exists(loader)) return new OracleStepRecord(job.Name, stepId, pgm, step.Parm, 12, $"no KSDS loader for {dsn} (add it to the oracle datasets)", dds, reads, writes, Rel(c, sysoutFile));
                    var lr = await Toolchain.RunAsync(loader, [], work, env: new Dictionary<string, string> { ["DD_SEQFILE"] = img, ["DD_KSDSFILE"] = bdb }, ct: ct);
                    if (lr.ExitCode != 0) return new OracleStepRecord(job.Name, stepId, pgm, step.Parm, 12, $"KSDS load of {dsn} failed: {Tail(lr.Output, 3)}", dds, reads, writes, Rel(c, sysoutFile));
                }
                dds[b.Dd] = bdb;
                indexed.Add((dsn, bdb, writes.Contains(dsn)));
            }
            foreach (var (dd, path) in dds) env["DD_" + dd] = path;
            if (unit.ParmDriver) env["MLCPU_PARM"] = step.Parm!;
            var r = await Toolchain.RunAsync(unit.Exe, [], work, line => { }, env, TimeSpan.FromMinutes(10), ct, stdoutFile: sysoutFile);
            foreach (var (dsn, bdb, w) in indexed.Where(x => x.Writes))
            {
                var unloader = Path.Combine(c.Paths.OracleBin, KsdsUtilityId("UNLOAD", dsn) + ".exe");
                if (!File.Exists(unloader)) continue;
                var img = JobSteps.DataFile(data, dsn);
                var ur = await Toolchain.RunAsync(unloader, [], work, env: new Dictionary<string, string> { ["DD_SEQFILE"] = img + ".unl", ["DD_KSDSFILE"] = bdb }, ct: ct);
                if (ur.ExitCode == 0) File.Move(img + ".unl", img, true);
            }
            foreach (var dsn in writes.Distinct()) CopyIfExists(JobSteps.DataFile(data, dsn), Path.Combine(stepDir, "out", dsn));
            CopyIfExists(sysoutFile, Path.Combine(stepDir, "out", "SYSOUT.txt"));
            var stderr = r.Output.Trim();
            var rec = new OracleStepRecord(job.Name, stepId, pgm, step.Parm, r.TimedOut ? 20 : r.ExitCode, r.TimedOut ? "timed out" : stderr.Length == 0 ? "ok" : Tail(stderr, 2),
                dds, reads.Distinct().ToList(), writes.Distinct().ToList(), Rel(c, sysoutFile));
            Directory.CreateDirectory(stepDir);
            File.WriteAllText(Path.Combine(stepDir, "step.json"), JsonSerializer.Serialize(rec, Json) + "\n");
            return rec;
        }
        return await UtilityAsync(c, job, step, stepId, pgm, dds, data, sysoutFile, op, ct);
    }

    async Task<OracleStepRecord> UtilityAsync(Context c, JclJob job, JclStep step, string stepId, string? pgm, Dictionary<string, string> dds,
        string data, string sysoutFile, Operation op, CancellationToken ct)
    {
        OracleStepRecord Rec(int rc, string detail) => new(job.Name, stepId, pgm, step.Parm, rc, detail, dds, [], [], Rel(c, sysoutFile));
        if (pgm is null) return Rec(12, "PROC step without a program");
        if (NoOpUtilities.Contains(pgm)) return Rec(0, NoOpUtilities.Contains(pgm) && pgm != "IEFBR14" ? "build step (compile/link): no effect on datasets" : "no operation");
        switch (pgm)
        {
            case "IEBGENER" or "ICEGENER":
                if (!dds.TryGetValue("SYSUT1", out var i1) || !dds.TryGetValue("SYSUT2", out var o1)) return Rec(12, "IEBGENER without SYSUT1/SYSUT2");
                File.Copy(i1, o1, true);
                return Rec(0, "copied SYSUT1 to SYSUT2");
            case "SORT" or "ICEMAN" or "SYNCSORT" or "DFSORT":
            {
                if (!dds.TryGetValue("SYSIN", out var sysin) || !dds.TryGetValue("SORTOUT", out var sortout)) return Rec(12, "SORT without SYSIN/SORTOUT");
                var cards = File.ReadAllText(sysin, Latin1);
                if (dds.TryGetValue("SYMNAMES", out var sym)) cards = ResolveSymbols(cards, File.ReadAllText(sym, Latin1));
                var inputs = step.Datasets.SkipWhile(d => d.Dd != "SORTIN").TakeWhile((d, i) => i == 0 || d.Dd.Length == 0)
                    .Select(d => d.Dsn is null ? null : JobSteps.DataFile(data, d.Dsn)).OfType<string>().ToList();
                var lrecl = step.Datasets.FirstOrDefault(d => d.Dd == "SORTOUT")?.Lrecl ?? step.Datasets.FirstOrDefault(d => d.Dd == "SORTIN")?.Lrecl;
                if (lrecl is null) return Rec(12, "SORT: record length of SORTIN is not established");
                var args = new List<string> { "-cp", RuntimeClasses(), "com.mlcpu.rt.SortReplay" };
                foreach (var card in SortStatements(cards)) { args.Add("--card"); args.Add(card); }
                args.AddRange(["--sortin", string.Join(",", inputs), "--lrecl", lrecl, "--out", sortout]);
                var r = await Toolchain.RunAsync(Toolchain.Java, args, data, ct: ct);
                return Rec(r.ExitCode == 0 ? 0 : 16, r.ExitCode == 0 ? "sorted (SortReplay)" : "SortReplay failed: " + Tail(r.Output, 3));
            }
            case "IDCAMS":
            {
                if (!dds.TryGetValue("SYSIN", out var sysin)) return Rec(12, "IDCAMS without SYSIN");
                var done = new List<string>();
                foreach (var cmd in IdcamsCommands(File.ReadAllText(sysin, Latin1)))
                {
                    var u = Regex.Replace(cmd.ToUpperInvariant(), @"\s+", " ");
                    Match m;
                    if ((m = Regex.Match(u, @"REPRO\s+(INFILE|INDATASET)\(([^)]+)\)\s+(OUTFILE|OUTDATASET)\(([^)]+)\)")).Success)
                    {
                        var from = m.Groups[1].Value == "INFILE" ? dds.GetValueOrDefault(m.Groups[2].Value.Trim()) : JobSteps.DataFile(data, m.Groups[2].Value.Trim());
                        var to = m.Groups[3].Value == "OUTFILE" ? dds.GetValueOrDefault(m.Groups[4].Value.Trim()) : JobSteps.DataFile(data, m.Groups[4].Value.Trim());
                        if (from is null || to is null || !File.Exists(from)) return Rec(12, "IDCAMS REPRO: dataset not allocated or missing");
                        File.Copy(from, to, true);
                        done.Add($"REPRO {Path.GetFileName(from)} -> {Path.GetFileName(to)}");
                    }
                    else if ((m = Regex.Match(u, @"^DELETE\s+([A-Z0-9#@$.]+)")).Success)
                    {
                        var f = JobSteps.DataFile(data, m.Groups[1].Value);
                        if (File.Exists(f)) File.Delete(f);
                        done.Add("DELETE " + m.Groups[1].Value);
                    }
                    else if ((m = Regex.Match(u, @"DEFINE\s+CLUSTER\s*\(\s*NAME\(([^)]+)\)")).Success)
                    {
                        var f = JobSteps.DataFile(data, m.Groups[1].Value.Trim());
                        if (!File.Exists(f)) File.WriteAllBytes(f, []);
                        done.Add("DEFINE " + m.Groups[1].Value.Trim());
                    }
                    else if (u.StartsWith("DEFINE ALTERNATEINDEX") || u.StartsWith("DEFINE PATH") || u.StartsWith("BLDINDEX") || u.StartsWith("SET ") || u.StartsWith("IF ") || u.StartsWith("LISTCAT"))
                        done.Add(u.Split(' ')[0] + " (no effect on record images)");
                    else return Rec(12, "IDCAMS command not supported: " + cmd);
                }
                return Rec(0, string.Join("; ", done));
            }
            default:
                return Rec(12, $"FRONTIER program {pgm} is neither in the estate nor a supported utility");
        }
    }

    // ------------------------------------------------------------------ helpers

    string RuntimeClasses() => Path.Combine(config.Settings.RepoRoot, "java-sidecar", "build", "classes", "java", "main");

    string? ControlCard(Context c, string member)
    {
        foreach (var root in OnboardingService.Roots(c.SourceDir, c.Project.Layout.ControlCardRoots))
            foreach (var f in Directory.EnumerateFiles(root))
                if (Path.GetFileNameWithoutExtension(f).Equals(member, StringComparison.OrdinalIgnoreCase)) return f;
        return null;
    }

    static IEnumerable<string> IdcamsCommands(string sysin)
    {
        var cur = new StringBuilder();
        foreach (var raw in sysin.Replace("\r\n", "\n").Split('\n'))
        {
            var line = Regex.Replace(raw, @"/\*.*?\*/", "").TrimEnd();
            if (line.Trim().Length == 0) continue;
            bool cont = line.EndsWith('-');
            cur.Append(' ').Append(cont ? line[..^1] : line);
            if (!cont) { yield return cur.ToString().Trim(); cur.Clear(); }
        }
        if (cur.Length > 0) yield return cur.ToString().Trim();
    }

    static IEnumerable<string> SortStatements(string sysin)
    {
        var cur = new StringBuilder();
        foreach (var raw in sysin.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Length > 71 ? raw[..71] : raw;
            if (line.TrimStart().StartsWith('*') || line.Trim().Length == 0) continue;
            var t = line.Trim();
            if (cur.Length > 0 && !cur.ToString().TrimEnd().EndsWith(',')) { yield return cur.ToString().Trim(); cur.Clear(); }
            cur.Append(t).Append(' ');
        }
        if (cur.Length > 0) yield return cur.ToString().Trim();
    }

    /// <summary>DFSORT SYMNAMES: "NAME,pos,len,fmt" lines substitute into the control statements.</summary>
    static string ResolveSymbols(string cards, string symnames)
    {
        foreach (var line in symnames.Replace("\r\n", "\n").Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('*')) continue;
            var parts = t.Split(',', 2);
            if (parts.Length == 2) cards = Regex.Replace(cards, $@"\b{Regex.Escape(parts[0].Trim())}\b", parts[1].Trim());
        }
        return cards;
    }

    static Dictionary<string, string> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir).ToDictionary(Path.GetFileName, f => Toolchain.FileSha256(f), StringComparer.OrdinalIgnoreCase)!;

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
    }

    static void CopyIfExists(string from, string to)
    {
        if (!File.Exists(from)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, true);
    }

    string Rel(Context c, string path) => Path.GetRelativePath(c.Paths.Dir, path).Replace('\\', '/');

    public static string Safe(string s) => Regex.Replace(s, "[^A-Za-z0-9.#@$-]", "_");

    static string Tail(string s, int lines) => string.Join(" | ", s.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(lines));
}
