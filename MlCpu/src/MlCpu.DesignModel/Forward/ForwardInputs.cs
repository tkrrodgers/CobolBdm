using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MlCpu.DesignModel.Forward;

/// <summary>
/// The forward-engineering evaluation's inputs (tools/eval/PROTOCOL-forward.md, forward/cas): the specification, the design
/// rules, the given API, the rule digests, the schemas, the acceptance scenarios' manifest and the implementation slices.
/// </summary>
public sealed class ForwardInputs
{
    public string CasDir { get; }
    public string Spec { get; }
    public string Rules { get; }
    public string Rubric { get; }
    /// <summary>cas-api source files: path relative to the source root (com/mlcpu/cas/api/...) → text.</summary>
    public SortedDictionary<string, string> Api { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, string> Digests { get; } = new(StringComparer.Ordinal);
    public JsonArray Scenarios { get; }
    public IReadOnlyList<string> AllRules { get; }

    /// <summary>
    /// One implementation slice: its context module and package, the digests and requirements packets it rests on, the rule
    /// prefixes it owns and the use-case methods it implements.
    /// </summary>
    public sealed record Slice(string Name, string Module, string Package, string[] Digests, string[] Packets, string[] RulePrefixes, string[] Methods);

    public static readonly IReadOnlyList<Slice> Slices =
    [
        new("accounts", "cas-accounts", "com.mlcpu.cas.accounts", ["CBTRN02C", "CBACT04C"], ["CBTRN02C", "CBACT04C"], ["POST-", "INT-", "IDEM-"],
            ["postTransaction", "postBatch", "accrueInterest"]),
        new("customers", "cas-customers", "com.mlcpu.cas.customers", ["SAM1", "SAM2"], ["SAM1", "SAM2"], ["CUST-", "UPD-", "AVG-"],
            ["applyCustomerChanges"]),
        new("reporting", "cas-reporting", "com.mlcpu.cas.reporting", ["CBTRN03C"], ["CBTRN03C"], ["RPT-"], ["transactionReport"]),
        new("statements", "cas-statements", "com.mlcpu.cas.statements", [], [], ["STMT-"], ["customerStatement"]),
    ];

    public static readonly IReadOnlyList<(string Module, string Package)> Modules =
    [
        ("cas-accounts", "com.mlcpu.cas.accounts"), ("cas-customers", "com.mlcpu.cas.customers"), ("cas-reporting", "com.mlcpu.cas.reporting"),
        ("cas-statements", "com.mlcpu.cas.statements"), ("cas-application", "com.mlcpu.cas.application"),
    ];

    public static readonly string[] UseCaseMethods = ["postTransaction", "postBatch", "accrueInterest", "applyCustomerChanges", "transactionReport", "customerStatement"];

    public ForwardInputs(string casDir)
    {
        CasDir = Path.GetFullPath(casDir);
        Spec = Read("SPEC.md");
        Rules = Read("RULES.md");
        Rubric = Read("rubric.md");
        var apiRoot = Path.Combine(CasDir, "service", "cas-api", "src", "main", "java");
        foreach (var f in Directory.EnumerateFiles(apiRoot, "*.java", SearchOption.AllDirectories))
            Api[Path.GetRelativePath(apiRoot, f).Replace('\\', '/')] = Norm(File.ReadAllText(f));
        foreach (var f in Directory.EnumerateFiles(Path.Combine(CasDir, "digests"), "*.md"))
            Digests[Path.GetFileNameWithoutExtension(f)] = Norm(File.ReadAllText(f));
        Scenarios = JsonNode.Parse(File.ReadAllText(Path.Combine(CasDir, "scenarios.json")))!["scenarios"]!.AsArray();
        var docs = string.Join("\n", Digests.Values) + "\n" + Spec;
        AllRules = Regex.Matches(docs, @"\| ((?:POST|INT|RPT|CUST|UPD|AVG)-\d\d) \|").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(docs, @"\*\*((?:STMT|IDEM)-\d\d):\*\*").Select(m => m.Groups[1].Value))
            .Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();
    }

    string Read(string name) => Norm(File.ReadAllText(Path.Combine(CasDir, name)));

    public static string Norm(string s) => s.Replace("\r\n", "\n");

    public string Schema(string name) => Norm(File.ReadAllText(Path.Combine(CasDir, "schemas", name)));

    public IEnumerable<string> RulesOf(Slice s) => AllRules.Where(r => s.RulePrefixes.Any(p => r.StartsWith(p, StringComparison.Ordinal)));

    /// <summary>The API as one markdown section: every source file, complete.</summary>
    public string ApiSection()
    {
        var sb = new StringBuilder("# The given API: cas-api (complete source; it is fixed)\n");
        foreach (var (path, src) in Api)
            sb.Append($"## {path}\n```java\n{src.TrimEnd()}\n```\n");
        return sb.ToString();
    }

    public string DigestSection(IEnumerable<string> names)
    {
        var sb = new StringBuilder();
        foreach (var n in names)
            sb.Append($"# Rule digest digests/{n}.md\n{Digests[n].TrimEnd()}\n\n");
        return sb.ToString();
    }

    public string AllDigests() => DigestSection(Digests.Keys);

    /// <summary>The scenarios of a slice in the manifest (id, visibility, rules, origin).</summary>
    public IEnumerable<JsonNode> ScenariosOf(string slice) => Scenarios.Where(s => s!["slice"]!.GetValue<string>() == slice)!;

    /// <summary>A scenario's JSON file (public or hidden).</summary>
    public string ScenarioFile(string slice, string visibility, string id) =>
        Path.Combine(CasDir, "service", "cas-acceptance", "src", "test", "resources", "scenarios", visibility, slice, id + ".json");
}
