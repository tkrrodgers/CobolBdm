using System.Text.RegularExpressions;

namespace MlCpu.SemanticKb.Text;

/// <summary>The header comment block of a COBOL program: "Key : value" entries (Function, Application, Type, Input,
/// Output, Interface, Java/SRC citations), free purpose lines and the maintenance log.</summary>
public sealed record CobolHeader(IReadOnlyList<(string Key, string Value)> Entries, IReadOnlyList<string> Purpose,
    IReadOnlyList<string> Maintenance, IReadOnlyList<string> SourceCitations, int EndLine)
{
    public string Get(string key) => string.Join("; ", Entries.Where(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(e => e.Value));
}

public static partial class CobolText
{
    /// <summary>The program text of a fixed-format line: columns 8-72, or the whole line when it is free format.</summary>
    public static string Code(string line)
    {
        if (line.Length < 7) return "";
        var end = Math.Min(line.Length, 72);
        return line[7..end].TrimEnd();
    }

    public static bool IsComment(string line) => line.Length >= 7 && (line[6] == '*' || line[6] == '/');

    /// <summary>Parses the comment lines before the DATA DIVISION. License boilerplate and ruler lines are dropped.</summary>
    public static CobolHeader Header(IReadOnlyList<string> lines)
    {
        var entries = new List<(string Key, string Value)>();
        var purpose = new List<string>();
        var maintenance = new List<string>();
        var citations = new List<string>();
        int end = 0, keyCol = 0;
        string? key = null;
        for (int i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];
            var code = Code(raw).Trim();
            if (!IsComment(raw))
            {
                if (code.Contains("DATA DIVISION", StringComparison.OrdinalIgnoreCase) || code.Contains("PROCEDURE DIVISION", StringComparison.OrdinalIgnoreCase)) { end = i; break; }
                key = null;
                continue;
            }
            var text = code.Trim('*', ' ', '\t').Trim();
            if (text.Length == 0 || Ruler().IsMatch(text) || Boilerplate().IsMatch(text)) { key = null; continue; }
            if (MaintenanceLine().IsMatch(text)) { maintenance.Add(Squash(text)); key = null; continue; }
            if (text.StartsWith("DATE ", StringComparison.OrdinalIgnoreCase) && text.Contains("DESCRIPTION", StringComparison.OrdinalIgnoreCase)) continue;
            if (text.StartsWith("MAINTENANCE LOG", StringComparison.OrdinalIgnoreCase)) continue;
            var col = raw.IndexOf(text, StringComparison.Ordinal);
            var src = SrcCitation().Match(text);
            var kv = KeyValue().Match(text);
            if (kv.Success && !src.Success)
            {
                key = Normalize(kv.Groups[1].Value);
                keyCol = col;
                var value = kv.Groups[2].Value.Trim();
                var s2 = SrcCitation().Match(value);
                if (s2.Success) { citations.Add(Squash(s2.Groups[1].Value)); value = value[..s2.Index].Trim(); }
                entries.Add((key, value));
                continue;
            }
            if (src.Success)
            {
                citations.Add(Squash(src.Groups[1].Value));
                key = "SRC";
                keyCol = col + src.Index;
                continue;
            }
            // A continuation of the previous entry (indented past its key), or a free purpose line.
            if (key is not null && col > keyCol)
            {
                if (key == "SRC" && citations.Count > 0) citations[^1] = Join(citations[^1], text);
                else if (entries.Count > 0) entries[^1] = (entries[^1].Key, Join(entries[^1].Value, text));
                continue;
            }
            key = null;
            purpose.Add(Squash(text));
        }
        return new CobolHeader(entries, purpose, maintenance, citations, end);
    }

    /// <summary>Joins a wrapped comment line: "calculateRateFactorForRepayment-" + "Period" loses the hyphen.</summary>
    static string Join(string a, string b) =>
        a.EndsWith('-') && b.Length > 0 && char.IsLetter(b[0]) && a.Length > 1 && char.IsLower(a[^2]) ? a[..^1] + b : a + " " + b;

    static string Normalize(string key) => Squash(key) switch
    {
        var k when k.Equals("PROGRAM", StringComparison.OrdinalIgnoreCase) => "Program",
        var k => char.ToUpperInvariant(k[0]) + k[1..].ToLowerInvariant()
    };

    public static string Squash(string s) => Spaces().Replace(s, " ").Trim();

    [GeneratedRegex(@"^[-=*_.\s]+$")] private static partial Regex Ruler();
    [GeneratedRegex(@"copyright|licen[cs]e|apache|warrant|restricted|gsa adp|all rights reserved|https?://|you may not|unless required|distributed on an|either express|limitations under|property of ibm|amazon\.com|contract with ibm|^\.\.\.\.", RegexOptions.IgnoreCase)]
    private static partial Regex Boilerplate();
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}\s")] private static partial Regex MaintenanceLine();
    [GeneratedRegex(@"^(Program|Application|Type|Function|Input|Output|Interface|Java|Layer|Author|Purpose)\s*:\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValue();
    [GeneratedRegex(@"SRC\s+(\S.*)$")] private static partial Regex SrcCitation();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
}
