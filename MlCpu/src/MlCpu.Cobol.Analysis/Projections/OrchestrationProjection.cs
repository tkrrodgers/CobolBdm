using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MlCpu.Cobol.Analysis.Entities;

namespace MlCpu.Cobol.Analysis.Projections;

public sealed record OrchestrationParagraph(string ParagraphId, string Effect, string Reason, int StartLine, int EndLine, IReadOnlyList<string> StepIds);

public sealed record OrchestrationCandidate(string CandidateId, string EntryParagraphId, IReadOnlyList<OrchestrationParagraph> Paragraphs,
    IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs, IReadOnlyList<object> Interactions, IReadOnlyList<object> Nodes,
    IReadOnlyList<object> Edges, IReadOnlyList<object> StatusMappings, IReadOnlyList<string> Citations, IReadOnlyList<string> Frontiers,
    string CandidateHash, bool Representable, string? NotRepresentableBecause);

/// <summary>Evidence an orchestration promotion needs (Q11). Missing or invalid evidence fails closed.</summary>
public sealed record OrchestrationPromotionEvidence(string? CandidateHash, string? NamingLedgerHash, int Scenarios, int Passed, int Failed, int Inconclusive,
    bool ResultStateEqual, bool InteractionTranscriptEqual, IReadOnlyList<string> NodeWitnesses, int ExecutedFrontiers, int Lines, int CoveredLines,
    int MutantsGenerated, int MutantsKilled, bool SmeApproved, string? SmeName);

public sealed record OrchestrationDecision(string ProgramId, string CandidateId, string CandidateHash, bool Approved, IReadOnlyList<string> Reasons);

public static class OrchestrationPromotionPolicy
{
    public const string Schema = "mlcpu.orchestration-promotion-manifest.v1";

    public static OrchestrationDecision Decide(string programId, OrchestrationCandidate c, OrchestrationPromotionEvidence? e)
    {
        var reasons = new List<string>();
        if (!c.Representable) reasons.Add("candidate-not-representable");
        if (c.Frontiers.Count > 0) reasons.Add("candidate-has-frontiers");
        if (e is null) reasons.Add("evidence-missing");
        else
        {
            if (e.CandidateHash != c.CandidateHash) reasons.Add("evidence-for-other-candidate");
            if (e.Scenarios == 0 || e.Failed > 0 || e.Inconclusive > 0 || e.Passed != e.Scenarios) reasons.Add("scenarios-incomplete");
            if (!e.ResultStateEqual) reasons.Add("result-state-differs");
            if (!e.InteractionTranscriptEqual) reasons.Add("interaction-transcript-differs");
            if (e.ExecutedFrontiers > 0) reasons.Add("frontier-executed");
            if (e.Lines == 0 || e.CoveredLines < e.Lines) reasons.Add("coverage-incomplete");
            if (e.MutantsGenerated == 0 || e.MutantsKilled < e.MutantsGenerated) reasons.Add("mutants-survived");
            if (!e.SmeApproved || string.IsNullOrWhiteSpace(e.SmeName)) reasons.Add("sme-approval-missing");
        }
        return new OrchestrationDecision(programId, c.CandidateId, c.CandidateHash, reasons.Count == 0, reasons);
    }
}

/// <summary>Orchestration candidates (Q12): the program's control/effect skeleton and its CandidateHash (JavaMethod excluded).</summary>
public static partial class OrchestrationCandidateAnalyzer
{
    public const string Schema = "mlcpu.orchestration-candidate.v1";

    static readonly HashSet<string> Selected = new(StringComparer.Ordinal)
        { nameof(ParagraphEffect.Orchestration), nameof(ParagraphEffect.ExternalCall), nameof(ParagraphEffect.Cics), nameof(ParagraphEffect.Sql), nameof(ParagraphEffect.Io), nameof(ParagraphEffect.Console) };

    public static OrchestrationCandidate? Analyze(ProgramUnit u, string generatedJava, string stateJava)
    {
        var paras = u.Model.Paragraphs.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var chosen = u.Plan.Paragraphs.Where(p => p.Reachable && Selected.Contains(p.Effect)).ToList();
        if (chosen.Count == 0) return null;
        var entry = u.Model.Paragraphs.Count > 0 && chosen.Any(c => c.Paragraph == u.Model.Paragraphs[0].Name) ? u.Model.Paragraphs[0].Name : chosen[0].Paragraph;
        var names = chosen.Select(c => c.Paragraph).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stmts = chosen.SelectMany(c => Flatten(paras[c.Paragraph].Statements).Select(s => (c.Paragraph, s))).ToList();
        var candidateId = $"{u.ProgramId}Orchestration01";
        var inputs = u.Model.Files.Where(f => !f.IsSort && Direction(u.Model, f.Name) is var d && (d.Contains("INPUT") || d.Contains("I-O"))).Select(f => f.Name).ToList();
        var outputs = u.Model.Files.Where(f => !f.IsSort && Direction(u.Model, f.Name) is var d && (d.Contains("OUTPUT") || d.Contains("EXTEND") || d.Contains("I-O"))).Select(f => f.Name).ToList();
        var interactions = new List<object>();
        foreach (var c in u.Graph.Calls.Where(c => names.Contains(c.Paragraph)))
            interactions.Add(new { stepId = c.StepId, kind = "call", target = c.Target, resolved = c.ResolvedTargets });
        foreach (var (p, s) in stmts.Where(x => x.s.Verb is "OPEN" or "CLOSE" or "READ" or "WRITE" or "REWRITE" or "DELETE" or "START"))
            interactions.Add(new { stepId = s.StepId, kind = "file", verb = s.Verb, file = s.Str("file") ?? string.Join(",", s.L?.GetValueOrDefault("files")?.Select(f => f.V) ?? []) });
        var nodes = stmts.Select(x => (object)new { stepId = x.s.StepId, paragraph = x.Paragraph, verb = x.s.Verb }).ToList();
        var edges = u.Graph.ParagraphEdges.Where(e => names.Contains(e.From) && names.Contains(e.To)).Select(e => (object)new { from = e.From, to = e.To, kind = e.Kind }).ToList();
        var status = u.Model.Files.Where(f => f.FileStatus is not null).Select(f => (object)new { file = f.Name, status = f.FileStatus }).ToList();
        var citations = chosen.Select(c => $"{u.ProgramId}.cbl:L{paras[c.Paragraph].Line}-L{paras[c.Paragraph].EndLine}").ToList();
        var frontiers = u.Graph.Calls.Where(c => names.Contains(c.Paragraph) && c.Kind == "dynamic" && c.ResolvedTargets.Count == 0).Select(c => $"unresolved dynamic CALL at {c.StepId}")
            .Concat(stmts.Where(x => x.s.Frontier is not null).Select(x => $"{x.s.StepId}: {x.s.Frontier}")).Distinct().ToList();
        var paragraphs = chosen.Select(c => new OrchestrationParagraph(c.Paragraph, c.Effect, c.Effect, paras[c.Paragraph].Line, paras[c.Paragraph].EndLine,
            Flatten(paras[c.Paragraph].Statements).Select(s => s.StepId).ToList())).ToList();
        var hash = ProjectionJson.Sha256(JsonSerializer.Serialize(new
        {
            schema = Schema, semanticSchema = "mlcpu.semantic-tree.v1", irVersion = "1.2", programId = u.ProgramId, candidateId,
            entryParagraphId = entry, paragraphs, inputs, outputs, interactions, nodes, edges, statusMappings = status, citations, frontiers
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 256 }));
        var why = Representability(u, generatedJava, stateJava, entry);
        return new OrchestrationCandidate(candidateId, entry, paragraphs, inputs, outputs, interactions, nodes, edges, status, citations, frontiers,
            hash, why is null, why);
    }

    [GeneratedRegex(@"\b[A-Za-z_]\w*\b")] private static partial Regex IdentRx();
    [GeneratedRegex(@"m\.put\(""(?:[^""\\]|\\.)*"", (?<j>\w+)\);")] private static partial Regex FieldMapRx();
    [GeneratedRegex(@"^\s*(?:final|protected final|private final) (?:Area|CobField|CobFile) (?<n>\w+) =", RegexOptions.Multiline)] private static partial Regex StorageDeclRx();

    /// <summary>Null when the entry, after port extraction, references no program storage; otherwise why not.</summary>
    public static string? Representability(ProgramUnit u, string java, string stateJava, string entry)
    {
        var x = OrchestrationCapabilityProjection.Extract(java, entry);
        if (x.Error is not null) return x.Error;
        var storage = StorageDeclRx().Matches(stateJava + "\n" + java).Select(m => m.Groups["n"].Value)
            .Concat(FieldMapRx().Matches(stateJava + "\n" + java).Select(m => m.Groups["j"].Value)).ToHashSet(StringComparer.Ordinal);
        var used = IdentRx().Matches(Regex.Replace(Regex.Replace(x.Workflow!, "//[^\n]*", ""), "\"(?:[^\"\\\\]|\\\\.)*\"", "\"\""))
            .Select(m => m.Value).Where(id => storage.Contains(id) || id is "perform" or "_goto" or "exitProgram" or "this" || Regex.IsMatch(id, @"^(N\d+|C88_\d+)$"))
            .Distinct().ToList();
        return used.Count == 0 ? null : $"entry {entry} uses program storage or control directly ({string.Join(", ", used.Take(6))})";
    }

    static string Direction(CobolProgram m, string file)
    {
        var modes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var s in Flatten(m.Paragraphs.SelectMany(p => p.Statements)).Where(s => s.Verb == "OPEN"))
            foreach (var f in s.L?.GetValueOrDefault("files") ?? [])
                if (string.Equals(f.V, file, StringComparison.OrdinalIgnoreCase)) modes.Add(f.Op!);
        return string.Join("+", modes);
    }

    internal static IEnumerable<Stmt> Flatten(IEnumerable<Stmt> list)
    {
        foreach (var s in list)
        {
            yield return s;
            if (s.Blocks is not null) foreach (var b in s.Blocks.Values) foreach (var x in Flatten(b)) yield return x;
        }
    }
}

/// <summary>
/// Projection 7 (§8, Q13.7): for an approved, representable orchestration candidate, the entry paragraph's control skeleton
/// moves to <c>capabilities.&lt;pkg&gt;.&lt;Workflow&gt;</c> over a typed <c>Port</c> (one method per PERFORMed range and per
/// condition); the program implements the port by delegating to its paragraphs. A frozen Baseline_&lt;PID&gt; already exists.
/// Output that still calls <c>CobRt.callProg</c> is rejected.
/// </summary>
public static partial class OrchestrationCapabilityProjection
{
    public const string Pass = "OrchestrationCapabilityProjection";

    [GeneratedRegex(@"perform\((?<a>\d+), (?<b>\d+)\);")] private static partial Regex PerformRx();
    [GeneratedRegex(@"// \[(?<step>[^\]:]+:(?<n>\d+):[^\]]*)\] (?<text>[^\n]*)\n(?<ind>[ \t]*)(?<lbl>\w+: )?(?<kw>if|while) \((?<cond>[^\n]*)\) \{")] private static partial Regex CondRx();
    [GeneratedRegex(@"String\[\]\{(?<names>[^}]*)\}; \}")] private static partial Regex NamesRx();

    public sealed record Extraction(string? Workflow, List<(string Name, string Code, string Doc)> Ports, string? Error, (int Start, int End, int Open, int Close)? Span);

    /// <summary>The entry body with PERFORMs and conditions turned into port calls.</summary>
    public static Extraction Extract(string java, string entry)
    {
        var method = DomainClusterAnalyzer.JavaMethod(java, entry);
        if (method is null) return new(null, [], $"generated method for {entry} was not found", null);
        var m = JavaText.Member(java, $"private void {method}() {{");
        if (m is null || m.Value.End < 0) return new(null, [], $"generated method {method} was not found", null);
        var names = NamesRx().Match(java) is { Success: true } nm
            ? nm.Groups["names"].Value.Split(", ").Select(s => s.Trim('"')).ToArray() : [];
        var body = java[(m.Value.BodyOpen + 1)..m.Value.BodyClose];
        var ports = new List<(string Name, string Code, string Doc)>();
        var used = new HashSet<string>(StringComparer.Ordinal) { "run" };
        body = CondRx().Replace(body, c =>
        {
            var name = JavaNames.Unique("cond" + c.Groups["n"].Value, used);
            var cond = c.Groups["cond"].Value;
            string test;
            if (c.Groups["kw"].Value == "while" && cond.StartsWith("!(") && cond.EndsWith(')')) { test = cond[2..^1]; cond = $"!port.{name}()"; }
            else { test = cond; cond = $"port.{name}()"; }
            ports.Add((name, $"public boolean {name}() {{ return {test}; }}", c.Groups["text"].Value));
            return $"// [{c.Groups["step"].Value}] {c.Groups["text"].Value}\n{c.Groups["ind"].Value}{c.Groups["lbl"].Value}{c.Groups["kw"].Value} ({cond}) {{";
        });
        var performs = new Dictionary<string, string>(StringComparer.Ordinal);
        body = PerformRx().Replace(body, p =>
        {
            var key = p.Groups["a"].Value + "," + p.Groups["b"].Value;
            if (!performs.TryGetValue(key, out var name))
            {
                int a = int.Parse(p.Groups["a"].Value), b = int.Parse(p.Groups["b"].Value);
                var label = a < names.Length ? names[a] + (b != a && b < names.Length ? "-THRU-" + names[b] : "") : $"range-{a}-{b}";
                performs[key] = name = JavaNames.Unique(JavaNames.Member(label), used);
                ports.Add((name, $"public void {name}() {{ perform({a}, {b}); }}", $"PERFORM {label.Replace("-THRU-", " THRU ")}"));
            }
            return $"port.{name}();";
        });
        return new(body, ports, null, (m.Value.Start, m.Value.End, m.Value.BodyOpen, m.Value.BodyClose));
    }

    public static ProjectionLedger Apply(ProjectFiles files, IReadOnlyList<ProgramUnit> units, Func<ProgramUnit, (string Package, string Workflow)> naming,
        string? evidenceRoot = null)
    {
        var ledger = new ProjectionLedger(Pass);
        var decisions = new List<OrchestrationDecision>();
        var programs = new List<object>();
        int frozen = 0;
        foreach (var u in units)
        {
            var path = $"src/main/java/com/mlcpu/core/{u.ClassName}.java";
            var java = files.Get(path);
            if (java is null) { ledger.Skip(u.ProgramId, $"{u.Pid}: generated program not found"); continue; }
            var state = files.Get($"src/main/java/com/mlcpu/core/State_{u.Pid}.java") ?? "";
            OrchestrationCandidate? c;
            try { c = OrchestrationCandidateAnalyzer.Analyze(u, java, state); }
            catch (Exception e) { ledger.Skip(u.ProgramId, $"orchestration-candidates/{u.ProgramId}.json: unreadable candidate ({e.Message})"); continue; }
            if (c is null) { ledger.Skip(u.ProgramId, $"{u.Pid}: no orchestration paragraphs"); continue; }
            files.Set($"orchestration-candidates/{u.ProgramId}.json", ProjectionJson.Write(new { schema = OrchestrationCandidateAnalyzer.Schema, programId = u.ProgramId, candidate = c }));
            var d = OrchestrationPromotionPolicy.Decide(u.ProgramId, c, LoadEvidence(evidenceRoot, u.ProgramId));
            decisions.Add(d);
            if (!d.Approved)
            {
                ledger.Skip($"{u.ProgramId}/{c.CandidateId}", string.Join(", ", d.Reasons) + (c.NotRepresentableBecause is null ? "" : $" ({c.NotRepresentableBecause})"));
                continue;
            }
            var x = Extract(java, c.EntryParagraphId);
            if (x.Error is not null) { ledger.Skip($"{u.ProgramId}/{c.CandidateId}", $"{u.Pid}: {x.Error}"); continue; }
            var (pkg, workflow) = naming(u);
            var pkgName = JavaNames.Package(pkg);
            var wf = new StringBuilder($$"""
                package com.mlcpu.capabilities.{{pkgName}};

                import com.mlcpu.rt.CobRt;

                /**
                 * Typed workflow of {{u.ProgramId}} (entry {{c.EntryParagraphId}}), projection 7. Candidate {{c.CandidateId}} {{c.CandidateHash[..16]}}.
                 * Control flow is the converted entry paragraph's; every effect goes through {@link Port}.
                 */
                public final class {{workflow}} {
                    /** What the workflow needs from the program: one method per PERFORMed range and per tested condition. */
                    public interface Port {

                """);
            foreach (var p in x.Ports)
                wf.Append($"        /** {p.Doc.Replace("*/", "* /")} */\n        {(p.Code.StartsWith("public boolean") ? "boolean" : "void")} {p.Name}();\n");
            wf.Append("    }\n\n    public void run(Port port) {").Append(x.Workflow!.TrimEnd()).Append("\n    }\n}\n");
            if (wf.ToString().Contains("CobRt.callProg", StringComparison.Ordinal)) { ledger.Skip($"{u.ProgramId}/{c.CandidateId}", $"{u.Pid}: legacy external CALL remains in projected program behavior"); continue; }
            var adapter = $"        new com.mlcpu.capabilities.{pkgName}.{workflow}().run(new com.mlcpu.capabilities.{pkgName}.{workflow}.Port() {{\n" +
                          string.Join("\n", x.Ports.Select(p => "            @Override " + p.Code)) + "\n        });\n";
            var span = x.Span!.Value;
            var newJava = java[..(span.Open + 1)] + "\n" + adapter + "    " + java[span.Close..];
            if (!newJava.Contains($".{workflow}().run(", StringComparison.Ordinal)) { ledger.Skip($"{u.ProgramId}/{c.CandidateId}", $"{u.Pid}: projected entry does not delegate to the typed workflow adapter"); continue; }
            files.Set($"src/main/java/com/mlcpu/capabilities/{pkgName}/{workflow}.java", wf.ToString());
            files.Set(path, newJava);
            frozen++;
            programs.Add(new { ProgramId = u.ProgramId, CandidateId = c.CandidateId, Workflow = workflow, Methods = x.Ports.Select(p => p.Name).ToList() });
            ledger.Applied.Add($"{u.ProgramId}/{c.CandidateId}");
        }
        files.Set("orchestration-candidates/promotion-manifest.json", ProjectionJson.Write(new
        {
            schema = OrchestrationPromotionPolicy.Schema,
            decisions = decisions.Select(d => new Dictionary<string, object>
            {
                ["ProgramId"] = d.ProgramId, ["CandidateId"] = d.CandidateId, ["CandidateHash"] = d.CandidateHash, ["Approved"] = d.Approved, ["Reasons"] = d.Reasons
            })
        }));
        ledger.Facts["programs"] = programs;
        ledger.Facts["baselinesFrozen"] = frozen;
        return ledger;
    }

    static OrchestrationPromotionEvidence? LoadEvidence(string? root, string pid)
    {
        if (root is null) return null;
        var path = Path.Combine(root, pid + ".orchestration-evidence.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<OrchestrationPromotionEvidence>(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
    }
}
