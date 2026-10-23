using System.Text.Json;
using System.Text.RegularExpressions;

namespace MlCpu.Modernize;

/// <summary>
/// Model edits (the model-edit pilot of Gemma4ExpertPilot, 2026-10-23): changes to the recovered model that the Java writer
/// applies while it prints the model, so a change is made once, to the model, and every other line stays generated (the
/// lift keeps the pictures, the records and the strangler right; an edit brings only what changes). An edit names a class by
/// its full name (a nested class: Outer.Inner), an enum constant by its name and a method by its printed signature (<c>repeat(EarlyPayment, int, int)</c>);
/// the code it brings is Java text, compiled with the rest (GN1). Opt-in: <c>lift &lt;project&gt; &lt;out&gt; --model-edits
/// &lt;file&gt;</c>; without it the output is the same, byte for byte. An edit that applies nowhere is residue (GN0).
/// <code>
/// { "schema": "mlcpu.model-edits.v1", "edits": [
///   { "op": "add-enum-constant", "class": "p.Kind", "text": "EVERY_OTHER { ... }", "note": "why" },
///   { "op": "replace-method", "class": "p.Kind", "constant": "TO_END", "method": "getRepeated(Loan, int, EarlyPayment)", "text": "return ...;" },
///   { "op": "add-method" | "add-field", "class": "p.C", "text": "..." },
///   { "op": "add-component", "class": "p.Rec", "text": "Integer graceMonths" },
///   { "op": "add-import", "class": "p.C", "text": "java.util.TreeMap" } ] }
/// </code>
/// </summary>
public sealed class ModelEdits
{
    public const string Schema = "mlcpu.model-edits.v1";
    public static readonly string[] Ops = ["add-enum-constant", "add-component", "add-field", "add-method", "replace-method", "add-import"];

    /// <param name="Op">One of <see cref="Ops"/>.</param>
    /// <param name="Class">The class's full name (an enum constant's body: its enum's, with <paramref name="Constant"/>).</param>
    /// <param name="Method">replace-method: the printed signature, parameter types only.</param>
    /// <param name="Text">The Java the edit brings: a constant, a field, a method, a method's body, or an import's type.</param>
    public sealed record Edit(int No, string Op, string Class, string? Constant, string? Method, string Text, string? Note)
    {
        public string Where => Class + (Constant is null ? "" : "." + Constant) + (Method is null ? "" : " " + Method);
    }

    public IReadOnlyList<Edit> Edits { get; }
    readonly HashSet<int> applied = [];

    ModelEdits(IReadOnlyList<Edit> edits) => Edits = edits;

    public static ModelEdits Load(string file) => Parse(File.ReadAllText(file));

    public static ModelEdits Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("schema", out var s) && s.GetString() != Schema)
            throw new InvalidDataException($"model edits: schema {s.GetString()}, expected {Schema}");
        if (!root.TryGetProperty("edits", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("model edits: no \"edits\" array");
        var edits = new List<Edit>();
        int no = 0;
        foreach (var e in list.EnumerateArray())
        {
            no++;
            string? Str(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var op = Str("op") ?? throw new InvalidDataException($"model edit {no}: no op");
            if (!Ops.Contains(op)) throw new InvalidDataException($"model edit {no}: op {op} is not one of {string.Join(", ", Ops)}");
            var cls = Str("class") ?? throw new InvalidDataException($"model edit {no}: no class");
            var text = Str("text") ?? throw new InvalidDataException($"model edit {no}: no text");
            var method = Str("method");
            if (op == "replace-method" && method is null) throw new InvalidDataException($"model edit {no}: replace-method needs a method signature");
            if (op == "add-field") text = FieldText(text);
            edits.Add(new Edit(no, op, cls, Str("constant"), method is null ? null : Signature(method), text, Str("note")));
        }
        return new ModelEdits(edits);
    }

    /// <summary>A field declaration ends with its semicolon; the writer adds it when the edit's text has none
    /// (<c>private Integer graceMonths</c>), as it adds an enum constant's comma.</summary>
    public static string FieldText(string text)
    {
        var t = text.TrimEnd();
        return t.EndsWith(';') ? text : t + ";";
    }

    /// <summary>A signature as the writer prints it, spaces normalised: <c>repeat(EarlyPayment, int, int)</c>.</summary>
    public static string Signature(string s) => Regex.Replace(Regex.Replace(s.Trim(), @"\s+", " "), @"\s*([(),<>\[\]])\s*", "$1").Replace(",", ", ");

    /// <summary>The edits of an op for a class (and an enum constant's body, or none).</summary>
    public IEnumerable<Edit> For(string op, string cls, string? constant = null) =>
        Edits.Where(e => e.Op == op && e.Class == cls && e.Constant == constant);

    public Edit? Replacement(string cls, string? constant, string signature) =>
        Edits.FirstOrDefault(e => e.Op == "replace-method" && e.Class == cls && e.Constant == constant && e.Method == Signature(signature));

    public void Applied(Edit e) => applied.Add(e.No);

    /// <summary>The edits that applied nowhere, as residue lines.</summary>
    public IEnumerable<string> Unapplied() =>
        Edits.Where(e => !applied.Contains(e.No)).Select(e => $"model edit {e.No} {e.Op} {e.Where}: applied nowhere");

    /// <summary>The record of the edits a generation applied (modernize/model-edits.json).</summary>
    public string Report() => JsonSerializer.Serialize(new
    {
        schema = Schema,
        applied = Edits.Where(e => applied.Contains(e.No)).Select(e => new { no = e.No, op = e.Op, where = e.Where, note = e.Note }),
        unapplied = Unapplied(),
    }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>The lines of an edit's Java, their common indentation removed.</summary>
    public static List<string> Lines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        int pad = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return lines.Select(l => l.Length >= pad ? l[pad..].TrimEnd() : l.Trim()).ToList();
    }
}
