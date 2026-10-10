using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MlCpu.Cobol.Analysis;
using MlCpu.Cobol.Analysis.Normalization;
using MlCpu.Cobol.Analysis.Projections;

namespace MlCpu.CobolKb.Knowledge;

/// <summary>
/// Requirements packet (mlcpu.requirements-packet.v1): everything a design model needs to re-express one program as
/// normalized Java without reading generated code — purpose, data contracts, ports, the paragraph slices (logic split from
/// I/O), numbered business rules, observable outputs, legacy quirks, numeric semantics and the acceptance oracle.
/// The packet hash pins a design to the exact requirements it was made from.
/// </summary>
public static class RequirementsPacketBuilder
{
    public const string Schema = "mlcpu.requirements-packet.v1";

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public sealed record Rule(string Id, string Paragraph, string Kind, IReadOnlyList<string> When, IReadOnlyList<SliceStatement> Statements,
        IReadOnlyList<string> Reads, IReadOnlyList<string> Writes);

    /// <param name="regression">How the normalized implementation is regression-tested end to end (null = the CardDemo harness).</param>
    public static JsonObject Build(CobolProgram m, ProgramGraph g, string sourcePath, DdBindingPlan? plan, ReplayPlan? replay, string? regression = null)
    {
        bool abendsWithCee3abd = g.Calls.Any(c => string.Equals(c.Target, "CEE3ABD", StringComparison.OrdinalIgnoreCase));
        var slices = SliceAnalyzer.Analyze(m, g);
        var name = ProgramSemanticNameCatalog.ForProgram(m.ProgramId);
        var lines = File.ReadAllLines(sourcePath);
        var header = lines.Take(12).Where(l => l.Length > 7 && l[6] == '*').Select(l => l[7..].Trim()).Where(l => l.Contains(':')).ToList();
        var rules = Rules(slices).ToList();
        var steps = plan?.Steps.Where(s => s.CallClosure.Contains(m.ProgramId, StringComparer.OrdinalIgnoreCase)).ToList() ?? [];

        var packet = new JsonObject
        {
            ["schema"] = Schema,
            ["programId"] = m.ProgramId,
            ["source"] = new JsonObject
            {
                ["file"] = Path.GetFileName(sourcePath),
                ["sha256"] = ProjectionJson.Sha256(File.ReadAllText(sourcePath)),
                ["lines"] = lines.Length,
                ["copybooks"] = Arr(m.Copybooks)
            },
            ["purpose"] = new JsonObject
            {
                ["header"] = Arr(header),
                ["capability"] = name.Capability,
                ["package"] = name.Package,
                ["workflow"] = name.Workflow,
                ["domain"] = name.Domain,
                ["namingConfidence"] = name.Confidence,
                ["rationale"] = name.Rationale,
                ["citations"] = Arr(name.Citations)
            },
            ["invocation"] = Node(steps.Select(s => new
            {
                job = plan!.Job, step = s.Step, parm = s.Parm, s.StepCitation,
                dds = s.Bindings.Select(b => new { b.Dd, file = b.LogicalFile, b.Direction, dsn = b.ResolvedDsn, b.Generation, b.Status, b.JclLrecl })
            })),
            ["dataContracts"] = Node(Contracts(m)),
            ["ports"] = Node(Ports(m)),
            ["controlFlags"] = Node(slices.Flags),
            ["errorHandling"] = new JsonObject
            {
                ["paragraphs"] = Arr(slices.ErrorParagraphs),
                ["behaviour"] = abendsWithCee3abd
                    ? "Any unexpected file status displays a message and the file status (as 'FILE STATUS IS: NNNN' + 4 digits), " +
                      "then abends with code 999 (CALL 'CEE3ABD'). The step return code becomes 16 and later steps do not run."
                    : "The program checks file statuses where its paragraphs do (see rules and console outputs): it displays the messages " +
                      "and sets RETURN-CODE exactly as coded, and otherwise continues; it never abends on its own."
            },
            ["paragraphs"] = Node(slices.Paragraphs),
            ["rules"] = Node(rules),
            ["observableOutputs"] = new JsonObject
            {
                ["console"] = Node(Console(m)),
                ["returnCode"] = Node(ReturnCodes(m)),
                ["files"] = Node(replay?.Steps.Where(s => s.CallClosure.Contains(m.ProgramId, StringComparer.OrdinalIgnoreCase))
                    .SelectMany(s => s.Outputs).Select(o => new { o.Dd, o.VolatileFields, o.VolatileFieldsBasis, layouts = o.Layouts.Select(l => new { l.Record, l.Role, l.Length }) }) ?? [])
            },
            ["legacyQuirks"] = Node(slices.Quirks),
            ["semantics"] = Arr([
                "Numeric receivers keep their PICTURE: integer digits beyond the PICTURE are truncated on the left (no ON SIZE ERROR is coded), " +
                "fraction digits beyond the scale are truncated (no ROUNDED unless stated), an unsigned receiver stores the absolute value.",
                "COMPUTE evaluates exactly, then stores with the receiver's truncation; division results are truncated at the receiver's scale.",
                "Zoned decimal files carry the sign as an EBCDIC-style overpunch in the last byte ({ A-I positive, } J-R negative); unsigned fields hold plain digits.",
                "Storage without a VALUE clause starts as binary zeros (x'00'); STRING does not pad its receiver.",
                "DISPLAY of an unsigned numeric field prints its digits with leading zeros; DISPLAY of a group prints its raw bytes.",
                "Alphanumeric receivers are left-justified and space-padded; comparisons of alphanumerics are byte-wise after space padding.",
                "INDEXED files are fixed-length record images kept in primary-key order; SEQUENTIAL files are fixed-length records.",
                "FUNCTION CURRENT-DATE gives YYYYMMDDHHMMSShh+hhmm from the batch clock; the DB2 timestamp built from it is YYYY-MM-DD-HH.MM.SS.hh0000."
            ]),
            ["acceptance"] = new JsonObject
            {
                ["oracle"] = "GnuCOBOL 3.3 running the original source (-std=ibm -fsign=EBCDIC), storage default x'00'",
                ["equality"] = "Every output dataset byte-identical; SYSOUT line-identical; same return code. Fields listed as volatile may differ only when the clock is not pinned.",
                ["clock"] = "Tests pin the batch clock (FUNCTION CURRENT-DATE) to a fixed instant.",
                ["regression"] = regression ?? "tools/carddemo/Invoke-SpringJobParity.ps1 with --mlcpu.implementation=normalized"
            },
            ["designContract"] = new JsonObject
            {
                ["output"] = "One JSON document conforming to design.schema.json (mlcpu.capability-design.v1).",
                ["rules"] = Arr([
                    "Express business rules as typed Java over immutable records (BigDecimal for amounts, enums for reason codes, Optional for lookups).",
                    "Keep file formats behind repository/port interfaces; business services must not see byte offsets, file status codes or record images.",
                    "Every rule id in 'rules' must be traced to a design element, or dispositioned as 'not-needed' with a reason.",
                    "Every legacy quirk must be dispositioned: 'preserve' (needed for output parity) or 'fix' (behaviour change, requires business sign-off).",
                    "Every console line and output dataset in observableOutputs must be produced by a named design element.",
                    "Do not invent behaviour that the packet does not evidence."
                ])
            }
        };
        packet["requirementsHash"] = ProjectionJson.Sha256(packet.ToJsonString());
        return packet;
    }

    public static string ToJson(JsonObject packet) => packet.ToJsonString(Json) + "\n";

    static JsonArray Arr(IEnumerable<string> xs) => new(xs.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());

    static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, Json)!;

    /// <summary>Logic segments become numbered rules; the conditions on the path to them are kept as 'when'.</summary>
    static IEnumerable<Rule> Rules(SliceResult slices)
    {
        int n = 0;
        foreach (var p in slices.Paragraphs.Where(p => p.Role != "error-handling"))
        {
            IEnumerable<Rule> Walk(IEnumerable<Segment> segs, List<string> path)
            {
                foreach (var s in segs)
                {
                    if (s.Kind == SegmentKind.Logic)
                        yield return new Rule($"R-{slices.ProgramId}-{++n:000}", p.Paragraph, "logic", [.. path], s.Statements, s.Reads, s.Writes);
                    if (s.Kind == SegmentKind.Decision)
                        foreach (var r in Walk(s.Children, [.. path, s.Condition ?? ""])) yield return r;
                }
            }
            foreach (var r in Walk(p.Segments, [])) yield return r;
        }
    }

    static IEnumerable<object> Contracts(CobolProgram m)
    {
        foreach (var rec in m.Items.Where(i => i.Level == 1 && !i.IsElementary && (i.Origin.Length > 0 || i.FileName is not null)))
            yield return new
            {
                record = rec.Name, copybook = rec.Origin.Length > 0 ? rec.Origin : null, file = rec.FileName, length = rec.Length,
                fields = ReplayPlanBuilder.Leaves(rec).Select(l => new
                {
                    name = l.Name, offset = l.Offset, length = l.Length, pic = l.Pic, usage = l.Usage, scale = l.Scale, signed = l.Signed,
                    type = l.Item.Category == Category.Numeric ? (l.Usage == "DISPLAY" ? "zoned" : l.Usage.ToLowerInvariant()) : l.Item.Category.ToString().ToLowerInvariant(),
                    conditionNames = l.Roles
                })
            };
    }

    static IEnumerable<object> Ports(CobolProgram m) => m.Files.Where(f => !f.IsSort).Select(f => new
    {
        file = f.Name,
        dd = DdBindingPlanner.DdName(f.Assign),
        organization = f.Organization,
        access = f.Access,
        recordKey = f.RecordKey,
        alternateKeys = f.AlternateKeys.Select(k => k.Name),
        direction = DdBindingPlanner.Direction(m, f.Name),
        statusField = f.FileStatus,
        operations = DdBindingPlanner.Walk(m).Where(s => s.Str("file") == f.Name || (s.Get("record")?.Id is { } rid && f.Records.Any(r => r.Id == rid || m.Items.First(i => i.Id == rid).Record == r)))
            .Select(s => s.Verb + (s.Get("key") is not null ? " by alternate key" : "") + (s.Get("into") is { V: { } into } ? " INTO " + into : "") + (s.Get("from") is { V: { } from } ? " FROM " + from : ""))
            .Distinct(),
        recordLength = f.MaxRecord > 0 ? f.MaxRecord : f.Records.Select(r => r.Length).DefaultIfEmpty(0).Max()
    });

    static IEnumerable<object> Console(CobolProgram m) => DdBindingPlanner.Walk(m).Where(s => s.Verb == "DISPLAY")
        .Select(s => new { s.StepId, line = s.Line, cobol = System.Text.RegularExpressions.Regex.Replace(s.Text, @"\s+", " ").Trim() });

    static IEnumerable<object> ReturnCodes(CobolProgram m) => DdBindingPlanner.Walk(m)
        .Where(s => s.Verb == "MOVE" && (s.L?.GetValueOrDefault("dst") ?? []).Any(d => d.V == "RETURN-CODE"))
        .Select(s => new { s.StepId, line = s.Line, cobol = System.Text.RegularExpressions.Regex.Replace(s.Text, @"\s+", " ").Trim() });

    /// <summary>JSON Schema of the design a model must return.</summary>
    public const string DesignSchema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "mlcpu.capability-design.v1",
          "type": "object",
          "required": ["schema", "programId", "requirementsHash", "capability", "package", "domainModel", "ports", "services", "rules", "quirks", "outputs", "tests"],
          "properties": {
            "schema": { "const": "mlcpu.capability-design.v1" },
            "programId": { "type": "string" },
            "requirementsHash": { "type": "string", "description": "requirementsHash of the packet this design answers" },
            "capability": { "type": "string" },
            "package": { "type": "string", "description": "Java package, e.g. com.mlcpu.capabilities.transactionposting" },
            "summary": { "type": "string" },
            "domainModel": { "type": "array", "items": { "type": "object", "required": ["name", "kind", "fields", "source"],
              "properties": { "name": {"type":"string"}, "kind": {"enum":["record","enum","value"]}, "source": {"type":"string","description":"COBOL record or 88-levels it models"},
                "fields": { "type": "array", "items": { "type": "object", "required": ["name","type"], "properties": { "name":{"type":"string"}, "type":{"type":"string"}, "cobol":{"type":"string"} } } } } } },
            "ports": { "type": "array", "items": { "type": "object", "required": ["name", "kind", "methods", "file"],
              "properties": { "name":{"type":"string"}, "kind":{"enum":["repository","reader","writer","report","console","clock"]}, "file":{"type":"string"},
                "methods": { "type": "array", "items": { "type": "string" } } } } },
            "services": { "type": "array", "items": { "type": "object", "required": ["name", "responsibility", "methods"],
              "properties": { "name":{"type":"string"}, "responsibility":{"type":"string"}, "methods":{"type":"array","items":{"type":"string"}} } } },
            "rules": { "type": "array", "description": "One entry per packet rule id", "items": { "type": "object", "required": ["ruleId", "disposition"],
              "properties": { "ruleId":{"type":"string"}, "disposition":{"enum":["implemented","not-needed"]}, "element":{"type":"string"}, "description":{"type":"string"}, "reason":{"type":"string"} } } },
            "quirks": { "type": "array", "items": { "type": "object", "required": ["quirkId", "disposition", "reason"],
              "properties": { "quirkId":{"type":"string"}, "disposition":{"enum":["preserve","fix"]}, "reason":{"type":"string"}, "element":{"type":"string"} } } },
            "outputs": { "type": "array", "items": { "type": "object", "required": ["output", "producedBy"],
              "properties": { "output":{"type":"string","description":"DD name or console step id"}, "producedBy":{"type":"string"}, "format":{"type":"string"} } } },
            "tests": { "type": "array", "items": { "type": "object", "required": ["name", "kind"],
              "properties": { "name":{"type":"string"}, "kind":{"enum":["unit","equivalence","property"]}, "covers":{"type":"array","items":{"type":"string"}} } } },
            "openQuestions": { "type": "array", "items": { "type": "string" } }
          }
        }
        """;
}
