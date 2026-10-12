using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using MlCpu.Cobol.Analysis;
using MlCpu.Cobol.Antlr;

namespace MlCpu.Host.Services.Workspace;

public sealed record GraphProgramStatus(string ProgramId, bool AstOk, int AstErrors, string? FirstError, long ParseMs, long AstBytes,
    int Paragraphs, int Statements, int DataDeps, int CtrlDeps, int Calls, int DeadParagraphs);

/// <summary>
/// Program and estate graphs for the active project (graphs/):
///   ast/&lt;PID&gt;.ast.json      COBOL-85 parse tree (grammars-v4 Cobol85.g4 via ANTLR), mlcpu.cobol85-ast.v1
///   ast/&lt;PID&gt;.outline.txt   the same tree as a structured outline (what the model reads)
///   ddg/&lt;PID&gt;.ddg.json      data dependence graph: statements and def→use edges over byte ranges, mlcpu.ddg.v2 (WriteDdg)
///   callgraph.json            jobs → programs (EXEC), programs → programs (CALL), per program paragraphs (PERFORM/GO TO/fall-through)
///   index.json                per-program status
/// The PDG (control + data dependences, statement CFG) is built in memory on demand and cached per program.
/// </summary>
public sealed class GraphService(ProjectService projects)
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    readonly ConcurrentDictionary<string, (CobolProgram Model, ProgramGraph Graph)> _pdg = new();

    public string Dir(ProjectDefinition p) => projects.Paths(p).Graphs;

    public async Task RebuildAsync(string projectId, Operation op, CancellationToken ct, double from = 0, double to = 1)
    {
        var p = projects.Get(projectId) ?? throw new ArgumentException($"no project {projectId}");
        var src = projects.SourceDir(p);
        var sources = OnboardingService.Sources(p, src);
        var dir = Dir(p);
        Directory.CreateDirectory(Path.Combine(dir, "ast"));
        Directory.CreateDirectory(Path.Combine(dir, "ddg"));
        // In-memory PDGs of this project are rebuilt on demand (Pdg); a rebuild must not keep every program's graph alive.
        foreach (var k in _pdg.Keys.Where(k => k.StartsWith(p.Id + "/", StringComparison.Ordinal)).ToList()) _pdg.TryRemove(k, out _);
        // Programs are parsed and analysed in parallel, the largest first; each keeps only its CALL sites for the estate call
        // graph, so the data dependences (millions for a large J3S program) are written and dropped one program at a time.
        var results = new (GraphProgramStatus Status, ProgramGraph? Calls, JsonObject? Node)[p.Programs.Count];
        var order = Enumerable.Range(0, p.Programs.Count).OrderByDescending(k => p.Programs[k].Lines).ToList();
        int done = 0;
        await Parallel.ForEachAsync(order, new ParallelOptions { MaxDegreeOfParallelism = GraphParallelism, CancellationToken = ct }, async (k, token) =>
        {
            var prog = p.Programs[k];
            var path = sources[prog.Id];
            var parse = await Task.Run(() => CobolAntlr.Parse(path, TimeSpan.FromMinutes(2)), token);
            var ast = AstExport.ToJsonText(parse);
            await File.WriteAllTextAsync(Path.Combine(dir, "ast", prog.Id + ".ast.json"), ast, token);
            await File.WriteAllTextAsync(Path.Combine(dir, "ast", prog.Id + ".outline.txt"), AstExport.Outline(parse), token);
            CobolProgram model;
            ProgramGraph graph;
            try
            {
                model = CobolModelBuilder.BuildGroundedFull(path);
                graph = ProgramGraphBuilder.Build(model);
            }
            catch (Exception e)
            {
                op.Info($"{prog.Id}: model failed: {e.Message}");
                results[k] = (new GraphProgramStatus(prog.Id, parse.Ok, parse.Errors.Count, parse.Errors.FirstOrDefault(), (long)parse.Elapsed.TotalMilliseconds, ast.Length, 0, 0, 0, 0, 0, 0), null, null);
                return;
            }
            WriteDdg(Path.Combine(dir, "ddg", prog.Id + ".ddg.json"), model, graph);
            var stmts = Statements(model).Count();
            var calls = new ProgramGraph { ProgramId = graph.ProgramId };
            calls.Calls.AddRange(graph.Calls);
            results[k] = (new GraphProgramStatus(prog.Id, parse.Ok, parse.Errors.Count, parse.Errors.FirstOrDefault(), (long)parse.Elapsed.TotalMilliseconds, ast.Length,
                model.Paragraphs.Count, stmts, graph.DataDeps.Count, graph.CtrlDeps.Count, graph.Calls.Count, graph.DeadParagraphs.Count), calls, ParagraphGraph(model, graph));
            op.Report(from + (to - from) * Interlocked.Increment(ref done) / Math.Max(1, p.Programs.Count + 1), $"graphs {prog.Id}");
            op.Info($"{prog.Id,-9} AST {(parse.Ok ? "ok" : $"{parse.Errors.Count} error(s)")} {parse.Elapsed.TotalMilliseconds:N0} ms, {stmts} statements, {graph.DataDeps.Count} data deps, {graph.Calls.Count} calls");
        });
        var statuses = results.Select(r => r.Status).ToList();
        var graphs = results.Select(r => r.Calls).OfType<ProgramGraph>().ToList();
        var programNodes = new JsonObject();
        foreach (var r in results.Where(r => r.Node is not null)) programNodes[r.Status.ProgramId] = r.Node;
        int i = p.Programs.Count;
        op.Report(from + (to - from) * i / Math.Max(1, p.Programs.Count + 1), "estate call graph");
        var estate = CallGraph(p, src, graphs);
        estate["programs"] = programNodes;
        await File.WriteAllTextAsync(Path.Combine(dir, "callgraph.json"), estate.ToJsonString(Indented), ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "index.json"), JsonSerializer.Serialize(new
        {
            schema = "mlcpu.graph-index.v1", project = p.Id, builtAt = DateTime.Now.ToString("s"), grammar = CobolAntlr.Grammar, programs = statuses
        }, Indented) + "\n", ct);
        op.Info($"graphs: {statuses.Count(s => s.AstOk)}/{statuses.Count} parsed cleanly by the COBOL-85 grammar; call graph {estate["edges"]!.AsArray().Count} edges");
    }

    public IReadOnlyList<GraphProgramStatus> Index(ProjectDefinition p)
    {
        var f = Path.Combine(Dir(p), "index.json");
        if (!File.Exists(f)) return [];
        var n = JsonNode.Parse(File.ReadAllText(f))!["programs"]!.ToJsonString();
        return JsonSerializer.Deserialize<List<GraphProgramStatus>>(n, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    public string? BuiltAt(ProjectDefinition p)
    {
        var f = Path.Combine(Dir(p), "index.json");
        return File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f))?["builtAt"]?.GetValue<string>() : null;
    }

    public JsonObject? CallGraphJson(ProjectDefinition p)
    {
        var f = Path.Combine(Dir(p), "callgraph.json");
        return File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f)) as JsonObject : null;
    }

    public string? Outline(ProjectDefinition p, string pid)
    {
        var f = Path.Combine(Dir(p), "ast", pid + ".outline.txt");
        return File.Exists(f) ? File.ReadAllText(f) : null;
    }

    /// <summary>The PDG of one program, in memory (built on first use).</summary>
    public (CobolProgram Model, ProgramGraph Graph) Pdg(ProjectDefinition p, string pid)
    {
        return _pdg.GetOrAdd(Key(p, pid), _ =>
        {
            var path = OnboardingService.Sources(p, projects.SourceDir(p))[pid];
            var m = CobolModelBuilder.BuildGroundedFull(path);
            return (m, ProgramGraphBuilder.Build(m));
        });
    }

    /// <summary>Cytoscape elements of a program's PDG restricted to one paragraph (statements, control and data dependences).</summary>
    public JsonObject PdgElements(ProjectDefinition p, string pid, string? paragraph)
    {
        var (model, graph) = Pdg(p, pid);
        var stmts = Statements(model).Where(s => paragraph is null || s.Paragraph == paragraph).ToDictionary(s => s.Stmt.StepId, s => s);
        var nodes = new JsonArray();
        foreach (var (id, s) in stmts)
            nodes.Add(new JsonObject { ["data"] = new JsonObject { ["id"] = id, ["label"] = $"{s.Stmt.Verb} L{s.Stmt.Line}", ["kind"] = "statement",
                ["title"] = Short(s.Stmt.Text, 160) } });
        var edges = new JsonArray();
        int n = 0;
        foreach (var e in graph.CtrlDeps.Where(e => stmts.ContainsKey(e.PredicateStepId) && stmts.ContainsKey(e.StepId)))
            edges.Add(new JsonObject { ["data"] = new JsonObject { ["id"] = $"c{n++}", ["source"] = e.PredicateStepId, ["target"] = e.StepId, ["kind"] = "control", ["label"] = e.Branch } });
        foreach (var e in graph.DataDeps.Where(e => stmts.ContainsKey(e.FromStepId) && stmts.ContainsKey(e.ToStepId)).Take(1500))
            edges.Add(new JsonObject { ["data"] = new JsonObject { ["id"] = $"d{n++}", ["source"] = e.FromStepId, ["target"] = e.ToStepId, ["kind"] = "data", ["label"] = e.Field } });
        return new JsonObject { ["nodes"] = nodes, ["edges"] = edges };
    }

    static string Key(ProjectDefinition p, string pid) => p.Id + "/" + pid;

    public static IEnumerable<(Stmt Stmt, string Paragraph)> Statements(CobolProgram m)
    {
        IEnumerable<(Stmt, string)> Walk(IEnumerable<Stmt> list, string para)
        {
            foreach (var s in list)
            {
                yield return (s, para);
                foreach (var b in s.Blocks?.Values ?? Enumerable.Empty<List<Stmt>>())
                    foreach (var x in Walk(b, para)) yield return x;
            }
        }
        return m.Paragraphs.SelectMany(p => Walk(p.Statements, p.Name));
    }

    /// <summary>Programs whose graphs are built at once (MLCPU_GRAPH_PARALLEL, default 4).</summary>
    static int GraphParallelism => int.TryParse(Environment.GetEnvironmentVariable("MLCPU_GRAPH_PARALLEL"), out var n) && n > 0 ? n : 4;

    /// <summary>
    /// The DDG file, mlcpu.ddg.v2, streamed: the statements as in v1, the fields a dependence goes through once each, and
    /// every edge as [from statement index, to statement index, field index]. v1's one object per edge took 684 MB for
    /// the largest J3S program (5.2 million edges); v2 takes about a tenth.
    /// </summary>
    static void WriteDdg(string file, CobolProgram m, ProgramGraph g)
    {
        using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var w = new Utf8JsonWriter(fs);
        w.WriteStartObject();
        w.WriteString("schema", "mlcpu.ddg.v2");
        w.WriteString("programId", m.ProgramId);
        w.WriteString("basis", "reaching definitions over byte ranges per storage area (group, REDEFINES and FD-record overlaps honoured)");
        w.WriteString("edgeFormat", "[from: index in nodes, to: index in nodes, field: index in fields]");
        w.WriteNumber("dataflowIterations", g.DataflowIterations);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        w.WriteStartArray("nodes");
        foreach (var s in Statements(m))
        {
            index.TryAdd(s.Stmt.StepId, index.Count);
            w.WriteStartObject();
            w.WriteString("id", s.Stmt.StepId);
            w.WriteString("paragraph", s.Paragraph);
            w.WriteString("verb", s.Stmt.Verb);
            w.WriteNumber("line", s.Stmt.Line);
            w.WriteString("text", Short(s.Stmt.Text, 240));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        var fields = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in g.DataDeps) fields.TryAdd(e.Field, fields.Count);
        w.WriteStartArray("fields");
        foreach (var f in fields.Keys) w.WriteStringValue(f);
        w.WriteEndArray();
        w.WriteStartArray("edges");
        foreach (var e in g.DataDeps)
        {
            if (!index.TryGetValue(e.FromStepId, out var from) || !index.TryGetValue(e.ToStepId, out var to)) continue;
            w.WriteStartArray();
            w.WriteNumberValue(from);
            w.WriteNumberValue(to);
            w.WriteNumberValue(fields[e.Field]);
            w.WriteEndArray();
            if (w.BytesPending > 1 << 16) w.Flush();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    static JsonObject ParagraphGraph(CobolProgram m, ProgramGraph g)
    {
        var nodes = new JsonArray(m.Paragraphs.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Name, ["line"] = p.Line, ["statements"] = p.Statements.Count, ["dead"] = g.DeadParagraphs.Contains(p.Name)
        }).ToArray());
        var edges = new JsonArray(g.ParagraphEdges.Distinct().Select(e => (JsonNode)new JsonObject { ["source"] = e.From, ["target"] = e.To, ["kind"] = e.Kind }).ToArray());
        var calls = new JsonArray(g.Calls.Select(c => (JsonNode)new JsonObject
        {
            ["paragraph"] = c.Paragraph, ["target"] = c.Target, ["kind"] = c.Kind, ["resolved"] = new JsonArray(c.ResolvedTargets.Select(t => (JsonNode)t).ToArray())
        }).ToArray());
        return new JsonObject { ["paragraphs"] = nodes, ["edges"] = edges, ["calls"] = calls };
    }

    /// <summary>Estate call graph: jobs (from the harvested JCL) → programs, programs → programs by CALL (dynamic targets closed by reaching definitions).</summary>
    static JsonObject CallGraph(ProjectDefinition p, string src, List<ProgramGraph> graphs)
    {
        var nodes = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var edges = new List<JsonObject>();
        var known = p.Programs.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        void Node(string id, string kind, string label, JsonObject? extra = null)
        {
            if (nodes.ContainsKey(id)) return;
            var n = new JsonObject { ["id"] = id, ["kind"] = kind, ["label"] = label };
            if (extra is not null) foreach (var (k, v) in extra) n[k] = v?.DeepClone();
            nodes[id] = n;
        }
        foreach (var prog in p.Programs) Node("P:" + prog.Id, "program", prog.Id, new JsonObject { ["programKind"] = prog.Kind, ["lines"] = prog.Lines });
        foreach (var job in OnboardingService.Harvest(p, src))
        {
            var steps = job.Expanded.Where(s => s.Program is not null).ToList();
            if (!steps.Any(s => known.ContainsKey(s.Program!))) continue;
            Node("J:" + job.Name, "job", job.Name, new JsonObject { ["file"] = Path.GetFileName(job.Path) });
            foreach (var s in steps)
            {
                var target = known.ContainsKey(s.Program!) ? "P:" + s.Program!.ToUpperInvariant() : "U:" + s.Program!.ToUpperInvariant();
                if (!known.ContainsKey(s.Program!)) Node(target, "utility", s.Program!.ToUpperInvariant());
                edges.Add(new JsonObject { ["source"] = "J:" + job.Name, ["target"] = target, ["kind"] = "exec", ["label"] = s.QualifiedStep });
            }
        }
        var ids = known.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var e in EstateCallGraph.Build(graphs, ids))
        {
            var target = e.InEstate ? "P:" + e.Callee.ToUpperInvariant() : "X:" + e.Callee.ToUpperInvariant();
            if (!e.InEstate) Node(target, "external", e.Callee.ToUpperInvariant());
            edges.Add(new JsonObject { ["source"] = "P:" + e.Caller.ToUpperInvariant(), ["target"] = target, ["kind"] = "call", ["label"] = e.Kind });
        }
        var distinct = edges.GroupBy(e => $"{e["source"]}>{e["target"]}>{e["kind"]}").Select(g => g.First()).ToList();
        return new JsonObject
        {
            ["schema"] = "mlcpu.callgraph.v1", ["project"] = p.Id, ["builtAt"] = DateTime.Now.ToString("s"),
            ["nodes"] = new JsonArray(nodes.Values.Select(n => (JsonNode)n).ToArray()),
            ["edges"] = new JsonArray(distinct.Select(e => (JsonNode)e).ToArray())
        };
    }

    static string Short(string s, int n)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length <= n ? s : s[..n] + "…";
    }
}
