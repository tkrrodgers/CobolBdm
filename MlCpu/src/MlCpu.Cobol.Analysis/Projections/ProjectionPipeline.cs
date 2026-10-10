namespace MlCpu.Cobol.Analysis.Projections;

public sealed record ProjectionOptions(
    bool DomainPartition = false,
    string? DomainEvidenceRoot = null,
    string? OrchestrationEvidenceRoot = null,
    IReadOnlyList<CertifiedCalculationSlice>? Calculations = null,
    IReadOnlyList<CertifiedDomainCluster>? DomainClusters = null);

/// <summary>Runs projections 2–10 in the order §8 requires and writes each pass's ledger at the project root (§7.11).</summary>
public static class ProjectionPipeline
{
    public static IReadOnlyList<ProjectionLedger> Run(ProjectFiles files, IReadOnlyList<ProgramUnit> units, string projectName, ProjectionOptions? options = null)
    {
        options ??= new ProjectionOptions();
        var ledgers = new List<ProjectionLedger>();
        void Write(string file, ProjectionLedger l) { ledgers.Add(l); files.Set(file, ProjectionJson.Ledger(l)); }

        Write("composition-projection-ledger.json", CompositionProjection.Apply(files, units));
        Write("projection-ledger.json", ProgramStateProjection.Split(files));
        Write("shared-copybook-ledger.json", SharedCopybookProjection.Apply(files, units));
        Write("domain-cluster-ledger.json", DomainClusterProjection.Apply(files, units, options.DomainEvidenceRoot, options.DomainClusters));
        Write("calculation-projection-ledger.json", CertifiedCalculationProjection.Apply(files, units, options.Calculations));
        Write("orchestration-projection-ledger.json", OrchestrationCapabilityProjection.Apply(files, units,
            u => ProgramSemanticNameCatalog.ForProgram(u.ProgramId) is var n ? (n.Package, n.Workflow) : default, options.OrchestrationEvidenceRoot));
        Write("semantic-capability-ledger.json", ProgramSemanticFacadeProjection.Apply(files, units));
        Write("program-capability-ledger.json", ProgramCapabilityProjection.Apply(files, units));
        if (options.DomainPartition) Write("domain-service-ledger.json", DomainServicePartition.Apply(files, units, projectName));
        files.Set("projections.json", ProjectionJson.Write(ledgers.Select(l => new
        {
            pass = l.Pass,
            applied = l.Applied.Count,
            skipped = l.Skipped.Count,
            firstSkipReasons = l.Skipped.Select(s => s.Reason).Distinct().Take(5).ToList()
        })));
        return ledgers;
    }
}
