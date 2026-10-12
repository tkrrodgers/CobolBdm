namespace MlCpu.SemanticKb.Labels;

public sealed class TaxonomyFunction
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class TaxonomyFile
{
    public string Schema { get; set; } = "";
    public bool Reviewed { get; set; }
    public string Note { get; set; } = "";
    public List<TaxonomyFunction> Functions { get; set; } = [];
}

/// <summary>The label families and their classes. <c>function</c> comes from knowledge/taxonomy.json; <c>role</c> is the
/// requirements packet's paragraph role; <c>ruleKind</c> is derived from the statements (<see cref="RuleKind"/>).</summary>
public static class Families
{
    public const string Role = "role";
    public const string RuleKind = "ruleKind";
    public const string Function = "function";

    public static readonly string[] All = [Role, RuleKind, Function];

    /// <summary>The chunk kinds a family applies to (role and rule kind describe code, not records or summaries).</summary>
    public static bool AppliesTo(string family, string kind) => family switch
    {
        Role or RuleKind => kind is "paragraph" or "java" or "rule",
        _ => kind is not "example"
    };

    public static readonly IReadOnlyDictionary<string, string> RoleDescriptions = new Dictionary<string, string>
    {
        ["orchestration"] = "Control flow that sequences other paragraphs: main loops, PERFORM chains, dispatch on a function code, open, process and close.",
        ["business-logic"] = "Business rules and calculations: computing amounts, interest, dates and balances, deciding outcomes from business data.",
        ["io-adapter"] = "File and dataset access: open, read, write, rewrite and close records, checking the file status after each operation.",
        ["error-handling"] = "Handling failures: displaying error messages and file statuses, setting the return code, abending the program.",
        ["mixed"] = "A paragraph that mixes file access, control flow and business logic."
    };

    public static readonly IReadOnlyDictionary<string, string> RuleKindDescriptions = new Dictionary<string, string>
    {
        ["rounding"] = "Rounding an amount to a scale or to significant digits with a rounding mode.",
        ["date-time"] = "Date and time arithmetic: day counts, adding periods, calendar dates, the current date.",
        ["calculation"] = "Arithmetic on business amounts: computing interest, installments, balances, rates and totals with formulas.",
        ["aggregation"] = "Accumulating counters and totals: adding to record counts, page totals and grand totals.",
        ["validation"] = "Checking input against rules and recording a reject reason, error code or status when it fails.",
        ["lookup"] = "Finding a record by key: keyed reads, table searches and queries.",
        ["formatting"] = "Building output text: report lines, edited numbers, strings and messages.",
        ["io"] = "Reading and writing files: open, read, write, rewrite and close.",
        ["error-handling"] = "Reacting to failures: error messages, return codes and abends.",
        ["decision"] = "Choosing a path from conditions on business data.",
        ["transformation"] = "Moving and reshaping data between records and fields.",
        ["control"] = "Sequencing work: performing other paragraphs, loops and exits."
    };
}

public sealed class Taxonomy(TaxonomyFile file)
{
    public TaxonomyFile File { get; } = file;
    public IReadOnlyList<TaxonomyFunction> Functions => File.Functions;

    public static Taxonomy Load(string path) => new(KbJson.Read<TaxonomyFile>(path));

    public string Name(string id) => Functions.FirstOrDefault(f => f.Id == id)?.Name ?? id;

    /// <summary>Class descriptions per family, for the zero-shot prototypes and the UI.</summary>
    public IReadOnlyDictionary<string, string> Descriptions(string family) => family switch
    {
        Families.Role => Families.RoleDescriptions,
        Families.RuleKind => Families.RuleKindDescriptions,
        _ => Functions.ToDictionary(f => f.Id, f => $"{f.Name}: {f.Description}")
    };
}
