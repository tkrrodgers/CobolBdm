using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MlCpu.Cobol.Analysis.Entities;

namespace MlCpu.Cobol.Analysis.Projections;

public sealed record DomainCharacterizationVector(string Name, IReadOnlyList<(string Cobol, string Literal)> Inputs);

public sealed record DomainPortField(string Cobol, string DomainName, string JavaType, bool Readable, bool Writable, int Capacity = 0);

public sealed record CertifiedDomainCluster(string ProgramId, string ClusterId, string CandidateHash, string Paragraph, string JavaParagraphMethod,
    string Package, string Capability, IReadOnlyList<DomainPortField> PortFields, IReadOnlyList<DomainCharacterizationVector> TestVectors,
    string Evidence, string ReviewedBy, string ReviewedAt);

/// <summary>
/// Reviewed, in-code data (Q11, ASK-7). Entries are added by an SME after review, never generated. CardDemo's batch programs
/// have no pure-domain paragraphs (every leaf mixes file I/O with logic), so the catalog is empty for this estate.
/// </summary>
public static class CertifiedDomainClusterCatalog
{
    public static readonly IReadOnlyList<CertifiedDomainCluster> Entries = [];
}

public sealed record DomainCandidateMethod(string Paragraph, string JavaMethod, IReadOnlyList<string> StepIds, IReadOnlyList<string> InstructionKinds);

public sealed record DomainCandidatePort(string Cobol, string Modern, string JavaType, string Access, string? Pic, int Scale, bool Signed);

public sealed record DomainCandidateFrontier(string Kind, string Paragraph, string? StepId, string Detail);

public sealed record DomainCandidate(string ClusterId, int StatementCount, IReadOnlyList<DomainCandidateMethod> Methods, IReadOnlyList<DomainCandidatePort> Ports,
    IReadOnlyList<DomainCandidateFrontier> Frontiers, string CandidateHash, string Category, string Rationale);

public sealed record PromotionDecision(string Schema, string ProgramId, string ClusterId, string CandidateHash, bool Approved, IReadOnlyList<string> Reasons);

/// <summary>Evidence a domain promotion needs (Q11). Missing or invalid evidence fails closed.</summary>
public sealed record DomainPromotionEvidence(string? ToolchainHash, bool Compiled, int DifferentialTotal, int DifferentialPassed, int DifferentialFailed,
    IReadOnlyList<string> WitnessedMethods, int Lines, int CoveredLines, int Branches, int CoveredBranches, int MutantsGenerated, int MutantsKilled,
    bool StructuralBoundaryPassed, bool Reproducible);

public static class DomainPromotionPolicy
{
    public const string Schema = "mlcpu.domain-promotion.v1";

    public static PromotionDecision Decide(string programId, DomainCandidate c, DomainPromotionEvidence? e, CertifiedDomainCluster? certified)
    {
        var reasons = new List<string>();
        if (c.Frontiers.Count > 0) reasons.Add("candidate-not-representable");
        if (certified is not null)
        {
            if (certified.CandidateHash != c.CandidateHash) reasons.Add("certified-hash-mismatch");
        }
        else if (e is null) reasons.Add("evidence-missing");
        else
        {
            if (string.IsNullOrEmpty(e.ToolchainHash)) reasons.Add("toolchain-unpinned");
            if (!e.Compiled) reasons.Add("not-compiled");
            if (e.DifferentialTotal == 0 || e.DifferentialFailed > 0 || e.DifferentialPassed != e.DifferentialTotal) reasons.Add("differential-incomplete");
            if (c.Methods.Any(m => !e.WitnessedMethods.Contains(m.JavaMethod))) reasons.Add("witness-missing");
            if (e.Lines == 0 || e.CoveredLines < e.Lines || e.CoveredBranches < e.Branches) reasons.Add("coverage-incomplete");
            if (e.MutantsGenerated == 0 || e.MutantsKilled < e.MutantsGenerated) reasons.Add("mutants-survived");
            if (!e.StructuralBoundaryPassed) reasons.Add("structural-boundary-failed");
            if (!e.Reproducible) reasons.Add("not-reproducible");
        }
        return new PromotionDecision(Schema, programId, c.ClusterId, c.CandidateHash, reasons.Count == 0, reasons);
    }
}

/// <summary>Domain candidates (Q12): per partition-plan cluster, its methods and ports, frontiers, and the CandidateHash.</summary>
public static class DomainClusterAnalyzer
{
    public const string Schema = "mlcpu.domain-candidates.v1";
    public const string RendererSchema = "mlcpu.domain-inner-class.v1";
    public const string VectorSchema = "mlcpu.characterization-vector.v1";

    public static List<DomainCandidate> Analyze(ProgramUnit u, string generatedJava)
    {
        var paras = u.Model.Paragraphs.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var items = u.Model.Items.Where(i => i.Level != 88).GroupBy(i => i.Name).ToDictionary(g => g.Key, g => g.First());
        var result = new List<DomainCandidate>();
        foreach (var cluster in u.Plan.Clusters)
        {
            var frontiers = new List<DomainCandidateFrontier>();
            var methods = new List<DomainCandidateMethod>();
            int statements = 0;
            foreach (var p in cluster.Paragraphs)
            {
                var stmts = Flatten(paras[p].Statements).Where(s => s.Verb is not ("CONTINUE" or "EXIT")).ToList();
                statements += stmts.Count;
                var method = JavaMethod(generatedJava, p);
                if (method is null) frontiers.Add(new("method-not-found", p, null, "generated paragraph method not located"));
                foreach (var s in stmts.Where(s => s.Frontier is not null)) frontiers.Add(new("frontier", p, s.StepId, s.Frontier!));
                methods.Add(new DomainCandidateMethod(p, method ?? "", stmts.Select(s => s.StepId).ToList(), stmts.Select(s => s.Verb).Distinct().ToList()));
            }
            if (cluster.Paragraphs.Count != 1) frontiers.Add(new("shape", cluster.Paragraphs[0], null, "cluster is not exactly one paragraph"));
            if (statements > 150 || cluster.Paragraphs.Count > 16) frontiers.Add(new("size", cluster.Paragraphs[0], null, $"{statements} statements in {cluster.Paragraphs.Count} paragraphs"));
            var plans = u.Plan.Paragraphs.Where(pp => cluster.Paragraphs.Contains(pp.Paragraph)).ToList();
            var ports = plans.SelectMany(pp => pp.Reads.Select(r => (r, "read")).Concat(pp.Writes.Select(w => (w, "write"))))
                .GroupBy(x => x.Item1).Select(g =>
                {
                    var access = g.Select(x => x.Item2).Distinct().Count() > 1 ? "readwrite" : g.First().Item2;
                    items.TryGetValue(g.Key, out var it);
                    var type = it is null ? "String" : it.Category == Category.Numeric ? "BigDecimal" : "String";
                    return new DomainCandidatePort(g.Key, JavaNames.Member(g.Key), type, access, it?.Pic, it?.Scale ?? 0, it?.Signed ?? false);
                }).OrderBy(p => p.Cobol, StringComparer.Ordinal).ToList();
            var hash = ProjectionJson.Sha256(JsonSerializer.Serialize(new
            {
                schema = Schema,
                renderer = RendererSchema,
                vectors = VectorSchema,
                ProgramId = u.ProgramId,
                ClusterId = cluster.Id,
                StatementCount = statements,
                Methods = methods,
                Ports = ports,
                Frontiers = frontiers.Distinct().Select(f => new { f.Kind, f.Paragraph, f.StepId, f.Detail })
            }, ProjectionJson.Pascal));
            var arithmetic = methods.SelectMany(m => m.InstructionKinds).Any(k => k is "COMPUTE" or "ADD" or "SUBTRACT" or "MULTIPLY" or "DIVIDE");
            result.Add(new DomainCandidate(cluster.Id, statements, methods, ports, frontiers.Distinct().ToList(), hash,
                arithmetic ? "calculation" : "transformation", arithmetic ? "assigns computed values" : "moves and tests data only"));
        }
        return result;
    }

    public static string? JavaMethod(string java, string paragraph)
    {
        var san = new string(paragraph.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        var m = Regex.Match(java, $@"\n    private void (p\d+_{Regex.Escape(san)})\(\) \{{");
        return m.Success ? m.Groups[1].Value : null;
    }

    static IEnumerable<Stmt> Flatten(IEnumerable<Stmt> list)
    {
        foreach (var s in list)
        {
            yield return s;
            if (s.Blocks is not null) foreach (var b in s.Blocks.Values) foreach (var x in Flatten(b)) yield return x;
        }
    }
}

/// <summary>
/// Projection 5 (§8, Q13.5): writes domain-candidates/&lt;PID&gt;.json and promotion-manifest.json for every program, and for
/// each approved single-paragraph cluster moves the paragraph method verbatim into an inner class the original method
/// delegates to. Bodies mentioning <c>this</c> are refused. Baseline_&lt;PID&gt; keeps the pre-extraction bodies.
/// </summary>
public static class DomainClusterProjection
{
    public const string Pass = "DomainClusterProjection";

    public static ProjectionLedger Apply(ProjectFiles files, IReadOnlyList<ProgramUnit> units, string? evidenceRoot = null,
        IReadOnlyList<CertifiedDomainCluster>? catalog = null)
    {
        catalog ??= CertifiedDomainClusterCatalog.Entries;
        var ledger = new ProjectionLedger(Pass);
        var decisions = new List<PromotionDecision>();
        int moved = 0, lines = 0, clusters = 0;
        foreach (var u in units)
        {
            var path = $"src/main/java/com/mlcpu/core/{u.ClassName}.java";
            var java = files.Get(path);
            if (java is null) { ledger.Skip(u.ProgramId, $"{u.Pid}: generated program not found"); continue; }
            var candidates = DomainClusterAnalyzer.Analyze(u, java);
            clusters += candidates.Count;
            files.Set($"domain-candidates/{u.ProgramId}.json", ProjectionJson.Write(new { schema = DomainClusterAnalyzer.Schema, programId = u.ProgramId, clusters = candidates }));
            foreach (var c in candidates)
            {
                var certified = catalog.FirstOrDefault(x => x.ProgramId == u.ProgramId && x.ClusterId == c.ClusterId);
                var d = DomainPromotionPolicy.Decide(u.ProgramId, c, LoadEvidence(evidenceRoot, u.ProgramId, c.ClusterId), certified);
                decisions.Add(d);
                if (!d.Approved) { ledger.Skip($"{u.ProgramId}/{c.ClusterId}", string.Join(", ", d.Reasons)); continue; }
                var method = c.Methods[0].JavaMethod;
                var m = JavaText.Member(java, $"private void {method}() {{");
                if (m is null) { ledger.Skip($"{u.ProgramId}/{c.ClusterId}", $"{u.Pid}/{method}: method not found"); continue; }
                if (m.Value.End < 0) { ledger.Skip($"{u.ProgramId}/{c.ClusterId}", $"{u.Pid}: generated class is unbalanced"); continue; }
                var body = java[(m.Value.BodyOpen + 1)..m.Value.BodyClose];
                if (JavaText.MentionsThis(body)) { ledger.Skip($"{u.ProgramId}/{c.ClusterId}", $"{u.Pid}/{method}: body mentions 'this'"); continue; }
                var inner = c.ClusterId;
                var field = "_" + char.ToLowerInvariant(inner[0]) + inner[1..];
                var delegating = $"    private void {method}() {{\n        {field}.{method}();\n    }}\n";
                var nested = $"\n    /** Domain cluster {inner} ({c.Methods[0].Paragraph}), moved verbatim by projection 5. */\n" +
                             $"    final class {inner} {{\n        void {method}() {{{body.Replace("\n", "\n    ")}}}\n    }}\n\n    private final {inner} {field} = new {inner}();\n";
                java = java[..m.Value.Start] + delegating + java[m.Value.End..];
                var close = java.LastIndexOf('}');
                java = java[..close] + nested + java[close..];
                moved++;
                lines += body.Count(ch => ch == '\n');
                ledger.Applied.Add($"{u.ProgramId}/{c.ClusterId}");
            }
            files.Set(path, java);
        }
        files.Set("domain-candidates/promotion-manifest.json", ProjectionJson.Write(new
        {
            schema = DomainPromotionPolicy.Schema,
            approved = decisions.Count(d => d.Approved),
            pending = decisions.Count(d => !d.Approved),
            decisions = decisions.Select(d => new Dictionary<string, object>
            {
                ["Schema"] = d.Schema, ["ProgramId"] = d.ProgramId, ["ClusterId"] = d.ClusterId, ["CandidateHash"] = d.CandidateHash,
                ["Approved"] = d.Approved, ["Reasons"] = d.Reasons
            })
        }));
        ledger.Facts["programs"] = units.Count;
        ledger.Facts["clusters"] = clusters;
        ledger.Facts["methodsMoved"] = moved;
        ledger.Facts["linesMoved"] = lines;
        ledger.Facts["topLevelCapabilities"] = 0;
        return ledger;
    }

    static DomainPromotionEvidence? LoadEvidence(string? root, string pid, string clusterId)
    {
        if (root is null) return null;
        var path = Path.Combine(root, pid, clusterId + ".evidence.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<DomainPromotionEvidence>(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
    }
}
