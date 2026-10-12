using System.Text;
using System.Text.Json.Nodes;
using MlCpu.SemanticKb.Embedding;
using MlCpu.SemanticKb.Labels;
using MlCpu.SemanticKb.Text;

namespace MlCpu.SemanticKb.Harvest;

public sealed class HarvestResult
{
    public List<KbChunk> Chunks { get; } = [];
    internal HashSet<string> Ids { get; } = new(StringComparer.Ordinal);
    /// <summary>Every input read: path relative to the project → SHA-256 (the index records them, so staleness is visible).</summary>
    public SortedDictionary<string, string> Sources { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> Counts => new(Chunks.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.Count()), StringComparer.Ordinal);
}

/// <summary>
/// Turns one project's pipeline outputs into labeled chunks (knowledgeBaseGroundGemma4.md §3, §5): program summaries from
/// the COBOL headers, project.json and the call graph; paragraphs from the AST outline; the faithful Java per paragraph;
/// rules, quirks, data contracts and the I/O picture from the requirements packet; Gemma 4's designs and Java; worked
/// examples; the estate's normalized capability classes. Pipeline labels (role) and rule labels (ruleKind) are attached
/// here and shared along each paragraph's anchor; weak function labels come from the glossary.
/// </summary>
public sealed partial class Harvester(Glossary glossary, GemmaTokenizer? tokenizer, int maxTokens = 1024)
{
    public const string Version = "kb-chunker.1";
    /// <summary>Worked examples are byte-level traces; the first sections explain the program, the rest repeat them.</summary>
    const int MaxExampleChunks = 8;
    readonly FunctionLabeler _functions = new(glossary);

    sealed record CallInfo(SortedSet<string> Calls, SortedSet<string> Services, SortedSet<string> Jobs);

    public HarvestResult Harvest(KbProject project, Action<string>? log = null)
    {
        var r = new HarvestResult();
        var calls = CallGraph(project, r);
        var priors = new Dictionary<string, IReadOnlyDictionary<string, double>>();
        foreach (var p in project.Programs)
        {
            var before = r.Chunks.Count;
            priors[p.Id] = Program(project, p, calls.GetValueOrDefault(p.Id), r);
            log?.Invoke($"  {p.Id,-9} {r.Chunks.Count - before,4} chunks");
        }
        NormalizedJava(project, r);
        foreach (var c in r.Chunks)
        {
            c.Tokens = Tokens(c.Title) + Tokens(c.Text);
            c.ComputeSha();
            if (!Families.AppliesTo(Families.Function, c.Kind)) continue;
            var prior = priors.GetValueOrDefault(c.Program) ?? new Dictionary<string, double>();
            var label = c.Kind == "program" ? FunctionLabeler.Top(prior) : _functions.Label(c.Fact("name") + "\n" + c.Text, prior);
            if (label is not null) c.Labels[Families.Function] = label;
        }
        ShareAlongAnchors(r.Chunks);
        r.Chunks.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return r;
    }

    // ---------------------------------------------------------------- per program

    IReadOnlyDictionary<string, double> Program(KbProject proj, KbProgram p, CallInfo? calls, HarvestResult r)
    {
        var srcPath = proj.SourcePath(p);
        var srcRel = proj.Relative(srcPath);
        string[] source = File.Exists(srcPath) ? Read(proj, srcPath, r).Split('\n').Select(l => l.TrimEnd('\r')).ToArray() : [];
        var header = CobolText.Header(source);
        var packet = Json(proj, proj.Requirements(p.Id), r);
        var design = Json(proj, proj.GemmaDesign(p.Id), r);
        var outlinePath = proj.AstOutline(p.Id);
        var paragraphs = outlinePath is null ? [] : Outline.Paragraphs(Read(proj, outlinePath, r).Split('\n').Select(l => l.TrimEnd('\r')));
        var function = Function(header, design);

        // Pipeline facts per paragraph: role and segment kinds from the requirements packet.
        var roles = new Dictionary<string, (string Role, string Segments)>(StringComparer.Ordinal);
        foreach (var par in packet?["paragraphs"]?.AsArray() ?? [])
            roles[Str(par, "paragraph")] = (Str(par, "role"), string.Join(", ", (par!["segments"]?.AsArray() ?? []).Select(s => Str(s, "kind")).Distinct()));

        // Program summary.
        var sb = new StringBuilder();
        sb.AppendLine($"Program {p.Id} ({p.Kind}{(p.Lines > 0 ? $", {p.Lines} lines" : "")}) in project {proj.Id}.");
        foreach (var (k, v) in header.Entries.Where(e => e.Key is not "Program" and not "Author")) sb.AppendLine($"{k}: {v}");
        if (header.Purpose.Count > 0) sb.AppendLine("Purpose: " + string.Join(" ", header.Purpose.Take(25)));
        if (header.SourceCitations.Count > 0) sb.AppendLine("Fineract Java source: " + string.Join("; ", header.SourceCitations));
        if (design is not null) sb.AppendLine($"Capability (Gemma 4 design): {Str(design, "capability")} — {Str(design, "summary")}");
        if (p.CalledBy.Count > 0) sb.AppendLine("Called by: " + string.Join(", ", p.CalledBy));
        if (calls?.Calls.Count > 0) sb.AppendLine("Calls: " + string.Join(", ", calls.Calls));
        if (calls?.Services.Count > 0) sb.AppendLine("Runtime services: " + string.Join(", ", calls.Services));
        var jobs = calls?.Jobs.Count > 0 ? calls.Jobs.ToList() : p.Jobs.ToList();
        if (jobs.Count > 0) sb.AppendLine("Jobs: " + string.Join(", ", jobs));
        foreach (var inv in packet?["invocation"]?.AsArray() ?? [])
        {
            if (p.Kind == "subprogram") { sb.AppendLine($"Runs inside job {Str(inv, "job")} step {Str(inv, "step")}, CALLed by {string.Join(", ", p.CalledBy)}."); break; }
            foreach (var dd in inv!["dds"]?.AsArray() ?? [])
                sb.AppendLine($"File {Str(dd, "file")} (DD {Str(dd, "dd")}, {Str(dd, "direction")}, {Str(dd, "dsn")})");
        }
        if (paragraphs.Count > 0) sb.AppendLine("Paragraphs: " + string.Join(", ", paragraphs.Where(x => !x.IsTrivial).Select(x => x.Name)));
        if (header.Maintenance.Count > 0) sb.AppendLine("Maintenance log: " + string.Join("; ", header.Maintenance));
        var programText = sb.ToString().TrimEnd();
        Add(r, Chunk(proj, p.Id, "program", "summary", "", $"{p.Id} · program · {function}", programText, srcRel,
            1, Math.Max(1, header.EndLine), new() { ["name"] = p.Id, ["programKind"] = p.Kind, ["function"] = function }));

        // Paragraphs (AST outline), with the pipeline's role and the statements' rule kind.
        foreach (var par in paragraphs.Where(x => !x.IsTrivial))
        {
            var facts = new Dictionary<string, string> { ["name"] = par.Name, ["paragraph"] = par.Name };
            if (roles.TryGetValue(par.Name, out var role)) { facts["role"] = role.Role; facts["segments"] = role.Segments; }
            var verbs = Outline.Verbs(par.Statements.Select(s => s.Text)).Distinct().ToList();
            facts["verbs"] = string.Join(", ", verbs);
            var kind = RuleKind.Classify(par.Statements.Select(s => s.Text));
            foreach (var (part, start, end, text) in Split(par.Statements, $"{p.Id} · paragraph {par.Name}"))
            {
                var c = Chunk(proj, p.Id, "paragraph", par.Name + Part(part), par.Name, $"{p.Id} · paragraph {par.Name}{PartTitle(part)} · {function}",
                    text, srcRel, start, end, new(facts));
                if (facts.TryGetValue("role", out var ro) && ro.Length > 0) c.Labels[Families.Role] = new KbLabel(ro, "pipeline", 1);
                c.Labels[Families.RuleKind] = new KbLabel(kind.Kind, "rule", kind.Confidence);
                Add(r, c);
            }
        }

        // The faithful Java, one method per COBOL paragraph.
        var java = proj.FaithfulJava(p.Id);
        if (java is not null)
        {
            var lines = Read(proj, java, r).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            var rel = proj.Relative(java);
            var trivial = paragraphs.Where(x => x.IsTrivial).Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var m in JavaText.FaithfulParagraphs(lines))
            {
                if (trivial.Contains(m.Name) || m.Text.Contains("// no operation") && m.Text.Split('\n').Length <= 5) continue;
                var numbered = m.Text.Split('\n').Select((t, i) => (m.StartLine + i, t)).ToList();
                var cls = Path.GetFileNameWithoutExtension(java);
                foreach (var (part, start, end, text) in Split(numbered, $"{p.Id} · Java {cls} · paragraph {m.Name}"))
                    Add(r, Chunk(proj, p.Id, "java", m.Name + Part(part), m.Name, $"{p.Id} · Java {cls} · paragraph {m.Name}{PartTitle(part)}",
                        text, rel, start, end, new() { ["name"] = m.Name, ["paragraph"] = m.Name, ["origin"] = "faithful", ["class"] = cls, ["cobolLine"] = m.CobolLine?.ToString() ?? "" }));
            }
        }

        if (packet is not null) Packet(proj, p, packet, design, srcRel, function, r);
        if (design is not null) Design(proj, p, design, function, r);
        Examples(proj, p, r);
        GemmaJava(proj, p, r);

        // The prior is what this program does: its Function line (or, without one, its first purpose line) weighted over
        // the rest of the purpose and the design. The Application line names the estate (the same for every program), so it
        // would pull them all toward one class.
        var primary = header.Get("Function") is { Length: > 0 } fn ? fn : header.Purpose.FirstOrDefault() ?? "";
        var context = $"{string.Join(" ", header.Purpose)} {(design is null ? "" : Str(design, "summary") + " " + Str(design, "capability"))}";
        return _functions.Prior(primary, context);
    }

    static string Function(CobolHeader header, JsonNode? design)
    {
        var f = header.Get("Function");
        if (f.Length == 0 && header.Purpose.Count > 0) f = header.Purpose[0];
        if (f.Length == 0 && design is not null) f = Str(design, "summary");
        if (f.Length == 0) return "no header";
        var cut = f.IndexOfAny(['.', ';']);
        f = cut > 20 ? f[..cut] : f;
        return f.Length > 90 ? f[..90].TrimEnd() + "…" : f;
    }

    // ---------------------------------------------------------------- requirements packet

    void Packet(KbProject proj, KbProgram p, JsonNode packet, JsonNode? design, string srcRel, string function, HarvestResult r)
    {
        var designRules = (design?["rules"]?.AsArray() ?? []).ToDictionary(x => Str(x, "ruleId"), x => x!, StringComparer.Ordinal);
        var designQuirks = (design?["quirks"]?.AsArray() ?? []).ToDictionary(x => Str(x, "quirkId"), x => x!, StringComparer.Ordinal);
        var roles = (packet["paragraphs"]?.AsArray() ?? []).ToDictionary(x => Str(x, "paragraph"), x => Str(x, "role"), StringComparer.Ordinal);

        foreach (var rule in packet["rules"]?.AsArray() ?? [])
        {
            var id = Str(rule, "id");
            var par = Str(rule, "paragraph");
            var statements = (rule!["statements"]?.AsArray() ?? []).Select(s => (Line: Int(s, "line"), Cobol: Str(s, "cobol"))).ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"Rule {id} in paragraph {par} of {p.Id}.");
            var when = (rule["when"]?.AsArray() ?? []).Select(w => w is JsonValue ? w.GetValue<string>() : w!.ToJsonString()).ToList();
            if (when.Count > 0) sb.AppendLine("When: " + string.Join(" AND ", when));
            foreach (var (line, cobol) in statements) sb.AppendLine($"  L{line} {cobol}");
            var reads = Strs(rule["reads"]); var writes = Strs(rule["writes"]);
            if (reads.Count > 0) sb.AppendLine("Reads: " + string.Join(", ", reads));
            if (writes.Count > 0) sb.AppendLine("Writes: " + string.Join(", ", writes));
            if (designRules.TryGetValue(id, out var dr))
                sb.AppendLine($"Gemma 4 design: {Str(dr, "description")} (reason: {Str(dr, "reason")}; {Str(dr, "disposition")} in {Str(dr, "element")})");
            var facts = new Dictionary<string, string> { ["name"] = par, ["paragraph"] = par, ["ruleId"] = id, ["reads"] = string.Join(", ", reads), ["writes"] = string.Join(", ", writes) };
            if (roles.TryGetValue(par, out var role)) facts["role"] = role;
            int start = statements.Count == 0 ? 0 : statements.Min(s => s.Line), end = statements.Count == 0 ? 0 : statements.Max(s => s.Line);
            var c = Chunk(proj, p.Id, "rule", id, par, $"{p.Id} · rule {id} · paragraph {par}", sb.ToString().TrimEnd(), srcRel, start, end, facts);
            if (role is { Length: > 0 }) c.Labels[Families.Role] = new KbLabel(role, "pipeline", 1);
            var kind = RuleKind.Classify(when.Select(w => "IF " + w).Concat(statements.Select(s => s.Cobol)));
            c.Labels[Families.RuleKind] = new KbLabel(kind.Kind, "rule", kind.Confidence);
            Add(r, c);
        }

        foreach (var q in packet["legacyQuirks"]?.AsArray() ?? [])
        {
            var id = Str(q, "id");
            var text = $"Legacy quirk {id} ({Str(q, "kind")}) in paragraph {Str(q, "paragraph")} of {p.Id}: {Str(q, "description")} Evidence: {Str(q, "evidence")}.";
            if (designQuirks.TryGetValue(id, out var dq)) text += $"\nGemma 4 design: {Str(dq, "disposition")} — {Str(dq, "reason")}";
            var line = int.TryParse(Str(q, "evidence").Split(':').LastOrDefault(), out var l) ? l : 0;
            Add(r, Chunk(proj, p.Id, "quirk", id, Str(q, "paragraph"), $"{p.Id} · quirk {id} · {Str(q, "kind")}", text, srcRel, line, line,
                new() { ["name"] = Str(q, "kind"), ["paragraph"] = Str(q, "paragraph"), ["quirk"] = Str(q, "kind") }));
        }

        foreach (var rec in packet["dataContracts"]?.AsArray() ?? [])
        {
            var name = Str(rec, "record");
            var cpy = Str(rec, "copybook");
            var cpyPath = cpy.Length > 0 ? proj.Copybook(cpy) : null;
            if (cpyPath is not null) Read(proj, cpyPath, r);
            var lines = new List<(int, string)> { (0, $"Record {name}{(cpy.Length > 0 ? $" from copybook {cpy}" : "")}, file {Str(rec, "file")}, {Int(rec, "length")} bytes. Fields:") };
            foreach (var f in rec!["fields"]?.AsArray() ?? [])
                lines.Add((0, $"  {Str(f, "name")} PIC {Str(f, "pic")} {Str(f, "usage")} {Str(f, "type")} offset {Int(f, "offset")} length {Int(f, "length")}"));
            foreach (var (part, _, _, text) in Split(lines, $"{p.Id} · record {name}"))
                Add(r, Chunk(proj, p.Id, "data", name + Part(part), name, $"{p.Id} · record {name}{(cpy.Length > 0 ? $" ({cpy})" : "")}{PartTitle(part)}", text,
                    cpyPath is null ? srcRel : proj.Relative(cpyPath), 0, 0, new() { ["name"] = name, ["copybook"] = cpy, ["file"] = Str(rec, "file") }));
        }

        var io = new List<(int, string)>();
        foreach (var inv in packet["invocation"]?.AsArray() ?? [])
        {
            io.Add((0, p.Kind == "subprogram"
                ? $"Runs inside job {Str(inv, "job")} step {Str(inv, "step")} ({Str(inv, "stepCitation")}), CALLed by {string.Join(", ", p.CalledBy)}; the step's files:"
                : $"Runs in job {Str(inv, "job")} step {Str(inv, "step")} ({Str(inv, "stepCitation")})."));
            foreach (var dd in inv!["dds"]?.AsArray() ?? [])
                io.Add((0, $"  DD {Str(dd, "dd")} → {Str(dd, "file")} {Str(dd, "direction")} {Str(dd, "dsn")} ({Str(dd, "status")})"));
        }
        foreach (var port in packet["ports"]?.AsArray() ?? [])
            io.Add((0, $"File {Str(port, "file")} (DD {Str(port, "dd")}): {Str(port, "organization")} {Str(port, "access")} {Str(port, "direction")}, record {Int(port, "recordLength")} bytes, status {Str(port, "statusField")}; operations: {string.Join(", ", Strs(port!["operations"]))}"));
        var outputs = packet["observableOutputs"];
        foreach (var d in outputs?["console"]?.AsArray() ?? []) io.Add((Int(d, "line"), $"Console L{Int(d, "line")}: {Str(d, "cobol")}"));
        foreach (var d in outputs?["returnCode"]?.AsArray() ?? []) io.Add((Int(d, "line"), $"Return code L{Int(d, "line")}: {Str(d, "cobol")}"));
        foreach (var f in outputs?["files"]?.AsArray() ?? [])
        {
            var vol = Strs(f!["volatileFields"]);
            var layouts = (f["layouts"]?.AsArray() ?? []).Select(x => $"{Str(x, "record")} {Str(x, "role")}").Distinct();
            io.Add((0, $"Output {Str(f, "dd")}: records {string.Join(", ", layouts)}{(vol.Count > 0 ? "; clock-derived fields " + string.Join(", ", vol) : "")}"));
        }
        if (io.Count > 0)
            foreach (var (part, _, _, text) in Split(io, $"{p.Id} · files and outputs"))
                Add(r, Chunk(proj, p.Id, "io", "io" + Part(part), "io", $"{p.Id} · files, job steps and outputs{PartTitle(part)}", text, srcRel, 0, 0, new() { ["name"] = "files and outputs" }));
    }

    // ---------------------------------------------------------------- Gemma 4

    void Design(KbProject proj, KbProgram p, JsonNode d, string function, HarvestResult r)
    {
        var lines = new List<(int, string)>
        {
            (0, $"Gemma 4 design of {p.Id}: capability {Str(d, "capability")} ({Str(d, "package")})."),
            (0, $"Summary: {Str(d, "summary")}")
        };
        foreach (var m in d["domainModel"]?.AsArray() ?? [])
            lines.Add((0, $"Domain {Str(m, "kind")} {Str(m, "name")} (from {Str(m, "source")}): " +
                string.Join(", ", (m!["fields"]?.AsArray() ?? []).Select(f => $"{Str(f, "name")} {Str(f, "type")} = {Str(f, "cobol")}"))));
        foreach (var s in d["services"]?.AsArray() ?? [])
            lines.Add((0, $"Service {Str(s, "name")}: {Str(s, "responsibility")} Methods: {string.Join("; ", Strs(s!["methods"]))}"));
        foreach (var s in d["ports"]?.AsArray() ?? [])
            lines.Add((0, $"Port {Str(s, "name")} ({Str(s, "kind")}, file {Str(s, "file")}): {string.Join("; ", Strs(s!["methods"]))}"));
        foreach (var s in d["rules"]?.AsArray() ?? [])
            lines.Add((0, $"Rule {Str(s, "ruleId")}: {Str(s, "description")}"));
        foreach (var o in d["outputs"]?.AsArray() ?? [])
            lines.Add((0, $"Output {Str(o, "output")} produced by {Str(o, "producedBy")} ({Str(o, "format")})"));
        foreach (var q in d["openQuestions"]?.AsArray() ?? [])
            lines.Add((0, "Open question: " + (q is JsonValue ? q.GetValue<string>() : q!.ToJsonString())));
        var rel = proj.Relative(proj.GemmaDesign(p.Id)!);
        foreach (var (part, _, _, text) in Split(lines, $"{p.Id} · Gemma 4 design"))
            Add(r, Chunk(proj, p.Id, "design", "design" + Part(part), "design", $"{p.Id} · Gemma 4 design · {Str(d, "capability")}{PartTitle(part)}", text, rel, 0, 0,
                new() { ["name"] = Str(d, "capability"), ["capability"] = Str(d, "capability") }));
    }

    void GemmaJava(KbProject proj, KbProgram p, HarvestResult r)
    {
        var dir = proj.GemmaJavaDir(p.Id);
        if (dir is null) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.java", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            Members(proj, p.Id, file, "gemma", "Gemma 4", r);
    }

    /// <summary>The published estate's normalized capability classes (com.mlcpu.capabilities.&lt;pkg&gt;, not legacy/ or
    /// records/), mapped to programs by the capability manifest. Placeholders that only call the faithful program are skipped.</summary>
    void NormalizedJava(KbProject proj, HarvestResult r)
    {
        var manifest = Json(proj, proj.CapabilityManifest(), r);
        if (manifest is null || proj.EstateDir is null) return;
        var main = Path.Combine(proj.EstateDir, "src", "main", "java");
        foreach (var cap in manifest["capabilities"]?.AsArray() ?? [])
        {
            var pid = Str(cap, "programId");
            var dir = Path.Combine([main, .. Str(cap, "package").Split('.')]);
            if (!Directory.Exists(dir) || proj.Programs.All(x => x.Id != pid)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.java").OrderBy(f => f, StringComparer.Ordinal))
            {
                var text = File.ReadAllText(file);
                if (text.Contains("placeholder until the capability is modernized") || text.Count(ch => ch == '\n') < 15) continue;
                Members(proj, pid, file, "normalized", "normalized", r);
            }
        }
    }

    void Members(KbProject proj, string pid, string file, string origin, string originTitle, HarvestResult r)
    {
        var source = Read(proj, file, r);
        var rel = proj.Relative(file);
        var cls = Path.GetFileNameWithoutExtension(file);
        foreach (var m in JavaText.Members(source))
        {
            var numbered = m.Text.Split('\n').Select((t, i) => (m.StartLine + i, t.TrimEnd('\r'))).ToList();
            var name = m.Name.Contains('.') ? m.Name : $"{cls}.{m.Name}";
            foreach (var (part, start, end, text) in Split(numbered, $"{pid} · Java {name}"))
                Add(r, Chunk(proj, pid, "java", $"{origin}:{name}{Part(part)}", name, $"{pid} · Java {name} ({originTitle}){PartTitle(part)}", text, rel, start, end,
                    new() { ["name"] = m.Name, ["origin"] = origin, ["class"] = cls, ["member"] = m.Kind }));
        }
    }

    void Examples(KbProject proj, KbProgram p, HarvestResult r)
    {
        var path = proj.Examples(p.Id);
        if (path is null) return;
        var lines = Read(proj, path, r).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var rel = proj.Relative(path);
        int start = 0, emitted = 0;
        string section = "overview";
        for (int i = 0; i <= lines.Length; i++)
        {
            if (i < lines.Length && !lines[i].StartsWith("## ")) continue;
            var body = lines[start..i].Select((t, k) => (start + k + 1, RawBytes().Replace(t, "«…»")))
                .Where(x => x.Item2.Length > 0 && !x.Item2.StartsWith("# ")).ToList();
            if (body.Count > 0)
                foreach (var (part, s, e, text) in Split(body, $"{p.Id} · worked example · {section}"))
                {
                    if (++emitted > MaxExampleChunks) return;
                    Add(r, Chunk(proj, p.Id, "example", Slug(section) + Part(part), "examples", $"{p.Id} · worked example · {section}{PartTitle(part)}", text, rel, s, e,
                        new() { ["name"] = section }));
                }
            if (i == lines.Length) break;
            section = lines[i][3..].Trim();
            start = i;
        }
    }

    // ---------------------------------------------------------------- shared

    Dictionary<string, CallInfo> CallGraph(KbProject proj, HarvestResult r)
    {
        var result = new Dictionary<string, CallInfo>(StringComparer.Ordinal);
        var g = Json(proj, proj.CallGraph(), r);
        CallInfo Of(string pid) => result.TryGetValue(pid, out var c) ? c : result[pid] = new([], [], []);
        foreach (var e in g?["edges"]?.AsArray() ?? [])
        {
            var src = Str(e, "source"); var dst = Str(e, "target"); var kind = Str(e, "kind");
            if (src.StartsWith("P:") && kind == "call")
            {
                if (dst.StartsWith("P:")) Of(src[2..]).Calls.Add(dst[2..]);
                else if (dst.Length > 2) Of(src[2..]).Services.Add(dst[2..]);
            }
            else if (src.StartsWith("J:") && dst.StartsWith("P:")) Of(dst[2..]).Jobs.Add($"{src[2..]} ({Str(e, "label")})");
        }
        return result;
    }

    /// <summary>The faithful Java and the rules of a paragraph share its pipeline role and rule kind (rules keep the rule
    /// kind of their own statements).</summary>
    static void ShareAlongAnchors(List<KbChunk> chunks)
    {
        var byAnchor = chunks.Where(c => c.Kind == "paragraph").GroupBy(c => c.Anchor).ToDictionary(g => g.Key, g => g.First());
        foreach (var c in chunks.Where(c => c.Kind is "java" or "rule"))
        {
            if (!byAnchor.TryGetValue(c.Anchor, out var par)) continue;
            foreach (var family in new[] { Families.Role, Families.RuleKind })
                if (!c.Labels.ContainsKey(family) && par.Labels.TryGetValue(family, out var l)) c.Labels[family] = l;
            if (!c.Labels.ContainsKey(Families.Function) && par.Labels.TryGetValue(Families.Function, out var f)) c.Labels[Families.Function] = f;
            foreach (var k in new[] { "role", "segments" })
                if (!c.Facts.ContainsKey(k) && par.Facts.TryGetValue(k, out var v)) c.Facts[k] = v;
        }
    }

    /// <summary>Line groups that fit the token cap with the title; returns (part, start, end, text), part 0 when unsplit.</summary>
    IEnumerable<(int Part, int Start, int End, string Text)> Split(IReadOnlyList<(int Line, string Text)> lines, string title)
    {
        int budget = Math.Max(64, maxTokens - Tokens(title) - 12);
        var groups = new List<List<(int Line, string Text)>>();
        var current = new List<(int, string)>();
        int used = 0;
        foreach (var l in lines)
        {
            int t = Tokens(l.Text) + 1;
            if (current.Count > 0 && used + t > budget) { groups.Add(current); current = []; used = 0; }
            current.Add(l);
            used += t;
        }
        if (current.Count > 0) groups.Add(current);
        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            var nums = g.Where(x => x.Line > 0).Select(x => x.Line).ToList();
            yield return (groups.Count == 1 ? 0 : i + 1, nums.Count == 0 ? 0 : nums.Min(), nums.Count == 0 ? 0 : nums.Max(), string.Join('\n', g.Select(x => x.Text)).TrimEnd());
        }
    }

    int Tokens(string text) => tokenizer?.Count(text) ?? (int)Math.Ceiling(text.Length / 3.5);

    static string Part(int part) => part == 0 ? "" : $"#{part}";
    static string PartTitle(int part) => part == 0 ? "" : $" (part {part})";
    static string Slug(string s) => new(s.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());

    static KbChunk Chunk(KbProject proj, string program, string kind, string key, string anchor, string title, string text, string path, int start, int end, Dictionary<string, string> facts) => new()
    {
        Id = $"{proj.Id}:{program}:{kind}:{key}",
        Project = proj.Id,
        Program = program,
        Kind = kind,
        Anchor = $"{proj.Id}/{program}/{anchor}",
        Title = title,
        Text = text,
        Path = path,
        StartLine = start,
        EndLine = end,
        Facts = facts.Where(kv => kv.Value.Length > 0).ToDictionary()
    };

    static void Add(HarvestResult r, KbChunk c)
    {
        if (c.Text.Trim().Length == 0) return;
        // Ids are unique: a repeated key (two members with the same name) gets a suffix.
        var id = c.Id;
        for (int n = 2; !r.Ids.Add(id); n++) id = $"{c.Id}~{n}";
        c.Id = id;
        r.Chunks.Add(c);
    }

    static string Read(KbProject proj, string path, HarvestResult r)
    {
        var text = File.ReadAllText(path);
        r.Sources[proj.Relative(path)] = KbJson.Sha256(text);
        return text;
    }

    static JsonNode? Json(KbProject proj, string? path, HarvestResult r) => path is null ? null : JsonNode.Parse(Read(proj, path, r));

    /// <summary>Raw record bytes in the worked examples («...»): noise to an embedding, so they are elided.</summary>
    [System.Text.RegularExpressions.GeneratedRegex("«[^»]{12,}»")] private static partial System.Text.RegularExpressions.Regex RawBytes();

    static string Str(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?[key]?.ToString() ?? "";
    static int Int(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
    static List<string> Strs(JsonNode? n) => (n as JsonArray ?? []).Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : x?.ToJsonString() ?? "").ToList();
}
