using System.Text;
using System.Text.RegularExpressions;

namespace MlCpu.SemanticKb.Text;

public sealed record JavaPart(string Name, string Kind, int StartLine, int EndLine, string Text, int? CobolLine = null);

/// <summary>
/// Splits Java for the index: the faithful translation by its "// PARAGRAPH NAME (Ln)" markers (one method per COBOL
/// paragraph), and idiomatic Java (normalized capabilities, Gemma 4's classes) into members with a small lexer that knows
/// comments, strings, text blocks and char literals, so braces inside them do not count.
/// </summary>
public static partial class JavaText
{
    public static IReadOnlyList<JavaPart> FaithfulParagraphs(string[] lines)
    {
        var parts = new List<JavaPart>();
        int start = -1; string? name = null; int cobol = 0;
        for (int i = 0; i <= lines.Length; i++)
        {
            var m = i < lines.Length ? ParagraphMarker().Match(lines[i]) : Match.Empty;
            if (i < lines.Length && !m.Success) continue;
            if (name is not null)
            {
                int end = i - 1;
                while (end > start && (lines[end].Trim().Length == 0 || lines[end].Trim() == "}" && i == lines.Length)) end--;
                parts.Add(new JavaPart(name, "paragraph", start + 1, end + 1, string.Join('\n', lines[start..(end + 1)]).TrimEnd(), cobol));
            }
            if (i == lines.Length) break;
            name = m.Groups[1].Value;
            cobol = int.Parse(m.Groups[2].Value);
            start = i;
        }
        return parts;
    }

    /// <summary>Members at class-body depth: methods and constructors one by one, nested types whole, fields together.</summary>
    public static IReadOnlyList<JavaPart> Members(string source)
    {
        var parts = new List<JavaPart>();
        var fields = new StringBuilder();
        int fieldStart = 0, fieldEnd = 0;
        int depth = 0, line = 1, memberStart = -1, memberLine = 0;
        string? typeName = null;
        int i = 0;
        void Emit(int endExclusive, int endLine)
        {
            var text = source[memberStart..endExclusive].Trim();
            memberStart = -1;
            if (text.Length == 0) return;
            var header = Header(text);
            var (kind, name) = Classify(header);
            if (kind == "field")
            {
                if (fields.Length == 0) fieldStart = memberLine;
                fields.AppendLine(text);
                fieldEnd = endLine;
                return;
            }
            parts.Add(new JavaPart(typeName is null ? name : $"{typeName}.{name}", kind, memberLine, endLine, text));
        }
        while (i < source.Length)
        {
            char c = source[i];
            if (c == '\n') line++;
            // Skip comments and literals, counting their lines.
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/') { i = SkipTo(source, i, "\n", ref line, consumeEnd: false); continue; }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                if (depth == 1 && memberStart < 0) { memberStart = i; memberLine = line; }
                i = SkipTo(source, i + 2, "*/", ref line, consumeEnd: true); continue;
            }
            if (c == '"' && i + 2 < source.Length && source[i + 1] == '"' && source[i + 2] == '"') { i = SkipTo(source, i + 3, "\"\"\"", ref line, consumeEnd: true); continue; }
            if (c == '"' || c == '\'') { i = SkipLiteral(source, i, c); continue; }
            if (depth == 1 && memberStart < 0 && !char.IsWhiteSpace(c) && c != '}' && c != ';') { memberStart = i; memberLine = line; }
            if (c == '{')
            {
                if (depth == 0) typeName ??= TypeName(source[..i]);
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 1 && memberStart >= 0) Emit(i + 1, line);
            }
            else if (c == ';' && depth == 1 && memberStart >= 0) Emit(i + 1, line);
            i++;
        }
        if (fields.Length > 0) parts.Insert(0, new JavaPart(typeName is null ? "fields" : $"{typeName}.fields", "fields", fieldStart, fieldEnd, fields.ToString().TrimEnd()));
        return parts;
    }

    static int SkipTo(string s, int from, string end, ref int line, bool consumeEnd)
    {
        var k = s.IndexOf(end, from, StringComparison.Ordinal);
        var stop = k < 0 ? s.Length : consumeEnd ? k + end.Length : k;
        for (int j = from; j < stop; j++) if (s[j] == '\n') line++;
        return stop;
    }

    static int SkipLiteral(string s, int i, char q)
    {
        int j = i + 1;
        while (j < s.Length && s[j] != q && s[j] != '\n') j += s[j] == '\\' ? 2 : 1;
        return Math.Min(j + 1, s.Length);
    }

    /// <summary>The declaration before the body or the semicolon, without comments and annotations.</summary>
    static string Header(string member)
    {
        var lines = member.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("//") && !l.StartsWith("/*") && !l.StartsWith('*') && !l.StartsWith('@'));
        var text = string.Join(' ', lines);
        var cut = text.IndexOfAny(['{', ';', '=']);
        return cut < 0 ? text : text[..cut];
    }

    static (string Kind, string Name) Classify(string header)
    {
        var t = TypeDecl().Match(header);
        if (t.Success) return ("type", t.Groups[2].Value);
        var m = MethodDecl().Match(header);
        if (m.Success) return ("method", m.Groups[1].Value);
        var f = FieldDecl().Match(header);
        return ("field", f.Success ? f.Groups[1].Value : "field");
    }

    static string? TypeName(string before)
    {
        var m = TypeDecl().Matches(before);
        return m.Count == 0 ? null : m[^1].Groups[2].Value;
    }

    [GeneratedRegex(@"^\s*// PARAGRAPH (\S+) \(L(\d+)\)")] private static partial Regex ParagraphMarker();
    [GeneratedRegex(@"\b(class|record|interface|enum)\s+([A-Za-z_]\w*)")] private static partial Regex TypeDecl();
    [GeneratedRegex(@"([A-Za-z_]\w*)\s*\([^)]*\)?\s*(throws [\w.,\s]+)?\s*$")] private static partial Regex MethodDecl();
    [GeneratedRegex(@"([A-Za-z_]\w*)\s*$")] private static partial Regex FieldDecl();
}
