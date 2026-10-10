using System.Text.RegularExpressions;

namespace MlCpu.Cobol.Analysis.Projections;

public sealed record ProgramSemanticName(string ProgramId, string Package, string Capability, string Workflow, double Confidence,
    IReadOnlyList<string> Citations, string Rationale, string Domain);

/// <summary>
/// Reviewed program names (Q11). The CardDemo entries were authored by the migration SME role (GitHub Copilot, at the
/// user's instruction) from each program's header and its JCL usage; they await human confirmation. Unreviewed programs
/// get a synthesized fallback with confidence 0 and no citations.
/// </summary>
public static class ProgramSemanticNameCatalog
{
    public const string Reviewer = "SME role (GitHub Copilot, per user instruction); pending human confirmation";

    static ProgramSemanticName N(string pid, string pkg, string cap, double conf, string cites, string why, string domain) =>
        new(pid, pkg, cap, cap + "Workflow", conf, cites.Split("; "), why, domain);

    public static readonly IReadOnlyList<ProgramSemanticName> Entries =
    [
        N("CBACT01C", "accountextract", "AccountExtract", .85, "CBACT01C.cbl:5", "reads the account master and writes extract files", "accounts"),
        N("CBACT02C", "cardlisting", "CardListing", .9, "CBACT02C.cbl:5; READCARD.jcl", "reads and prints the card data file", "accounts"),
        N("CBACT03C", "crossreferencelisting", "CardCrossReferenceListing", .9, "CBACT03C.cbl:5; READXREF.jcl", "reads and prints the card/account cross-reference", "accounts"),
        N("CBCUS01C", "customerlisting", "CustomerListing", .9, "CBCUS01C.cbl:5; READCUST.jcl", "reads and prints the customer data file", "accounts"),
        N("CBACT04C", "interestcalculation", "InterestCalculation", .95, "CBACT04C.cbl:5; INTCALC.jcl",
            "computes monthly interest per account and transaction category balance and writes interest transactions", "interest"),
        N("CBTRN01C", "dailytransactionverification", "DailyTransactionVerification", .8, "CBTRN01C.cbl:5",
            "reads daily transactions and verifies card cross-reference and account (header reads 'post'; the program does not write)", "transactions"),
        N("CBTRN02C", "transactionposting", "TransactionPosting", .95, "CBTRN02C.cbl:5; POSTTRAN.jcl",
            "validates and posts daily transactions to transactions, accounts and category balances; writes rejects", "transactions"),
        N("CBTRN03C", "transactionreporting", "TransactionDetailReport", .95, "CBTRN03C.cbl:5; TRANREPT.jcl",
            "prints the transaction detail report for a date range", "transactions"),
        N("CBSTM03A", "accountstatements", "AccountStatementPrinting", .9, "CBSTM03A.CBL:8; CREASTMT.JCL", "prints account statements from transaction data", "statements"),
        N("CBSTM03B", "statementfileaccess", "StatementFileAccess", .85, "CBSTM03B.CBL:8", "file access subroutine called by CBSTM03A", "statements"),
        N("CBEXPORT", "customerdataexport", "CustomerDataExport", .9, "CBEXPORT.cbl:8", "exports customer data for branch migration", "migration"),
        N("CBIMPORT", "customerdataimport", "CustomerDataImport", .9, "CBIMPORT.cbl:8", "imports customer data from a branch migration export", "migration")
    ];

    public static ProgramSemanticName ForProgram(string programId)
    {
        var e = Entries.FirstOrDefault(x => string.Equals(x.ProgramId, programId, StringComparison.OrdinalIgnoreCase));
        if (e is not null) return e;
        var cap = "Program" + new string(programId.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return new ProgramSemanticName(programId, JavaNames.Package(programId), cap, cap + "Workflow", 0, [], "unreviewed: synthesized fallback", "shared");
    }
}

/// <summary>
/// Projection 8 (§8, Q13.8): a semantic facade <c>capabilities/&lt;pkg&gt;/&lt;Workflow&gt;</c> over the typed command/result/
/// service boundary, with a test. Adds files only; skipped when the workflow already exists (projection 7).
/// </summary>
public static class ProgramSemanticFacadeProjection
{
    public const string Pass = "ProgramSemanticFacadeProjection";

    public static ProjectionLedger Apply(ProjectFiles files, IReadOnlyList<ProgramUnit> units)
    {
        var ledger = new ProjectionLedger(Pass);
        var names = new List<object>();
        foreach (var u in units)
        {
            var n = ProgramSemanticNameCatalog.ForProgram(u.ProgramId);
            names.Add(new { n.ProgramId, n.Package, n.Capability, n.Workflow, n.Confidence, n.Citations, n.Rationale });
            var pkg = JavaNames.Package(n.Package);
            if (!files.Exists($"src/main/java/com/mlcpu/usecases/{u.Pid}Command.java") || !files.Exists($"src/main/java/com/mlcpu/usecases/{u.Pid}Result.java")
                || !files.Exists($"src/main/java/com/mlcpu/services/{u.Pid}Service.java"))
            { ledger.Skip(u.ProgramId, $"{u.ProgramId}: typed command/result/service boundary not found"); continue; }
            var path = $"src/main/java/com/mlcpu/capabilities/{pkg}/{n.Workflow}.java";
            if (files.Exists(path)) { ledger.Skip(u.ProgramId, $"{u.ProgramId}: semantic facade not emitted because {path} already exists"); continue; }
            files.Set(path, $$"""
                package com.mlcpu.capabilities.{{pkg}};

                import com.mlcpu.services.{{u.Pid}}Service;
                import com.mlcpu.usecases.{{u.Pid}}Command;
                import com.mlcpu.usecases.{{u.Pid}}Result;
                import java.util.List;
                import org.springframework.stereotype.Component;

                /**
                 * {{n.Capability}}: {{n.Rationale}}.
                 * Program {{u.ProgramId}}; naming confidence {{n.Confidence:0.00}}; citations {{string.Join(", ", n.Citations)}} (projection 8).
                 */
                @Component
                public class {{n.Workflow}} {
                    public static final String CAPABILITY = {{JavaText.Lit(n.Capability)}};
                    public static final String PROGRAM = {{JavaText.Lit(u.ProgramId)}};
                    public static final List<String> CITATIONS = List.of({{string.Join(", ", n.Citations.Select(JavaText.Lit))}});

                    private final {{u.Pid}}Service service;

                    public {{n.Workflow}}({{u.Pid}}Service service) {
                        this.service = service;
                    }

                    public {{u.Pid}}Result run({{u.Pid}}Command command) {
                        return service.execute(command);
                    }
                }

                """);
            var hasDds = u.Model.Files.Any(f => !f.IsSort);
            files.Set($"src/test/java/com/mlcpu/capabilities/{pkg}/{n.Workflow}Test.java", $$"""
                package com.mlcpu.capabilities.{{pkg}};

                import static org.junit.jupiter.api.Assertions.assertEquals;
                import static org.junit.jupiter.api.Assertions.assertThrows;

                import com.mlcpu.services.{{u.Pid}}Service;
                import com.mlcpu.usecases.{{u.Pid}}Command;
                import java.util.Map;
                import org.junit.jupiter.api.Test;

                class {{n.Workflow}}Test {
                    @Test
                    void namesTheCapabilityAndProgram() {
                        assertEquals({{JavaText.Lit(n.Capability)}}, {{n.Workflow}}.CAPABILITY);
                        assertEquals({{JavaText.Lit(u.ProgramId)}}, {{n.Workflow}}.PROGRAM);
                    }
                {{(hasDds ? $$"""

                    @Test
                    void refusesACommandWithUnboundDatasets() {
                        {{n.Workflow}} workflow = new {{n.Workflow}}(new {{u.Pid}}Service());
                        assertThrows(IllegalArgumentException.class, () -> workflow.run(new {{u.Pid}}Command(Map.of(), null)));
                    }
                """ : "")}}
                }

                """);
            ledger.Applied.Add(u.ProgramId);
        }
        files.Set("semantic-name-ledger.json", ProjectionJson.Write(names));
        ledger.Facts["reviewer"] = ProgramSemanticNameCatalog.Reviewer;
        return ledger;
    }
}

/// <summary>
/// Projection 9 (§8, Q13.9): implementations move to <c>capabilities/&lt;pkg&gt;/legacy/{Prog,State,Baseline}_&lt;PID&gt;</c>;
/// <c>com.mlcpu.core.Prog_&lt;PID&gt;</c> stays as the compatibility entry point (JCL steps, CALL resolution, tests).
/// </summary>
public static partial class ProgramCapabilityProjection
{
    public const string Pass = "ProgramCapabilityProjection";

    [GeneratedRegex(@"^src/main/java/com/mlcpu/core/Prog_(?<program>[A-Za-z0-9_]+)\.java$")] private static partial Regex ProgRx();
    [GeneratedRegex(@"^package\s+com\.mlcpu\.core\s*;", RegexOptions.Multiline)] private static partial Regex PackageRx();
    [GeneratedRegex(@"^@CobolProgram\(([^\r\n]+)\)", RegexOptions.Multiline)] private static partial Regex AnnotationRx();

    public static ProjectionLedger Apply(ProjectFiles files, IReadOnlyList<ProgramUnit> units)
    {
        var ledger = new ProjectionLedger(Pass);
        var implementations = new List<object>();
        int synthesizedCount = 0;
        foreach (var path in files.Paths("src/main/java/com/mlcpu/core/"))
        {
            var pm = ProgRx().Match(path);
            if (!pm.Success) continue;
            var pid = pm.Groups["program"].Value;
            var unit = units.FirstOrDefault(u => u.Pid == pid);
            var statePath = $"src/main/java/com/mlcpu/core/State_{pid}.java";
            if (!files.Exists(statePath)) { ledger.Skip(pid, $"{pid}: projected state not found"); continue; }
            var prog = files.Get(path)!;
            var ann = AnnotationRx().Match(prog);
            if (!ann.Success) { ledger.Skip(pid, $"{pid}: @CobolProgram annotation not found"); continue; }
            var n = ProgramSemanticNameCatalog.ForProgram(unit?.ProgramId ?? pid);
            var pkg = $"com.mlcpu.capabilities.{JavaNames.Package(n.Package)}.legacy";
            var dir = $"src/main/java/com/mlcpu/capabilities/{JavaNames.Package(n.Package)}/legacy";
            string Repackage(string text) => PackageRx().Replace(text, $"package {pkg};", 1);
            var baselinePath = $"src/main/java/com/mlcpu/core/Baseline_{pid}.java";
            bool synthesized = !files.Exists(baselinePath);
            var baseline = synthesized
                ? prog.Replace($"public final class Prog_{pid} ", $"public final class Baseline_{pid} ")
                : files.Get(baselinePath)!;
            files.Set($"{dir}/Prog_{pid}.java", Repackage(prog.Replace($"public final class Prog_{pid} ", $"public class Prog_{pid} ")));
            files.Set($"{dir}/State_{pid}.java", Repackage(files.Get(statePath)!.Replace($"abstract class State_{pid} ", $"public abstract class State_{pid} ")));
            files.Set($"{dir}/Baseline_{pid}.java", Repackage(baseline));
            files.Remove(statePath);
            if (!synthesized) files.Remove(baselinePath);
            files.Set(path, $$"""
                package com.mlcpu.core;

                import com.mlcpu.rt.CobolProgram;

                /** Compatibility entry point for {{unit?.ProgramId ?? pid}} (projection 9); the implementation is {@link {{pkg}}.Prog_{{pid}}}. */
                @CobolProgram({{ann.Groups[1].Value}})
                public final class Prog_{{pid}} extends {{pkg}}.Prog_{{pid}} {
                }

                """);
            foreach (var test in files.Paths("src/test/java/"))
            {
                var t = files.Get(test)!;
                var r = Regex.Replace(t, $@"\b(?:com\.mlcpu\.core\.)?Baseline_{pid}\b", $"{pkg}.Baseline_{pid}");
                if (r != t) files.Set(test, r);
            }
            implementations.Add(new { programId = unit?.ProgramId ?? pid, package = pkg, synthesizedBaseline = synthesized });
            if (synthesized) synthesizedCount++;
            ledger.Applied.Add(pid);
        }
        ledger.Facts["programs"] = implementations.Count;
        ledger.Facts["implementations"] = implementations;
        ledger.Facts["coreCompatibilityClasses"] = implementations.Count;
        ledger.Facts["synthesizedBaselines"] = synthesizedCount;
        return ledger;
    }
}
