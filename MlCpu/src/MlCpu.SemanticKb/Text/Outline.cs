using System.Text.RegularExpressions;

namespace MlCpu.SemanticKb.Text;

/// <summary>A paragraph of the PROCEDURE DIVISION as the AST outline prints it: its statement lines (program line, text).</summary>
public sealed record OutlineParagraph(string Name, int Line, IReadOnlyList<(int Line, string Text)> Statements)
{
    public int EndLine => Statements.Count == 0 ? Line : Statements.Max(s => s.Line);

    /// <summary>Only EXIT / CONTINUE (the "-EXIT" paragraphs of PERFORM THRU ranges): no behaviour to explain.</summary>
    public bool IsTrivial => Statements.All(s => s.Text.Trim() is "EXIT" or "CONTINUE" or "EXIT PROGRAM" or "GOBACK" or "");
}

/// <summary>
/// Reads the AST outline (graphs/ast/&lt;PID&gt;.outline.txt, written by MlCpu.Cobol.Antlr): "L&lt;n&gt; " + two spaces per
/// nesting level. Paragraph headers are the unindented "NAME." lines after "PROCEDURE DIVISION."; statements before the
/// first header form MAINLINE, the name the pipeline and the transpiler give them.
/// </summary>
public static partial class Outline
{
    public static IReadOnlyList<OutlineParagraph> Paragraphs(IEnumerable<string> lines)
    {
        var result = new List<OutlineParagraph>();
        bool inProcedure = false;
        string? name = null;
        int line = 0;
        var statements = new List<(int, string)>();
        void Flush()
        {
            if (name is not null && (statements.Count > 0 || name != "MAINLINE")) result.Add(new OutlineParagraph(name, line, statements.ToList()));
            statements.Clear();
        }
        foreach (var raw in lines)
        {
            var m = Numbered().Match(raw);
            if (!m.Success) continue;
            var n = int.Parse(m.Groups[1].Value);
            var body = m.Groups[2].Value;
            if (!inProcedure)
            {
                if (body.TrimStart().StartsWith("PROCEDURE DIVISION", StringComparison.Ordinal)) { inProcedure = true; name = "MAINLINE"; line = n; }
                continue;
            }
            var h = Header().Match(body);
            if (h.Success)
            {
                Flush();
                name = h.Groups[1].Value;
                line = n;
                continue;
            }
            if (name == "MAINLINE" && statements.Count == 0) line = n;
            statements.Add((n, body.Length >= 2 && body.StartsWith("  ") ? body[2..] : body.TrimStart()));
        }
        Flush();
        return result;
    }

    /// <summary>The leading verb of each statement line (COBOL verbs are upper case; condition fragments are skipped).</summary>
    public static IEnumerable<string> Verbs(IEnumerable<string> statements)
    {
        foreach (var s in statements)
        {
            var t = s.TrimStart();
            var sp = t.IndexOf(' ');
            var w = sp < 0 ? t : t[..sp];
            if (VerbSet.Contains(w)) yield return w;
        }
    }

    public static readonly HashSet<string> VerbSet = new(StringComparer.Ordinal)
    {
        "ACCEPT", "ADD", "ALTER", "CALL", "CANCEL", "CLOSE", "COMPUTE", "CONTINUE", "DELETE", "DISPLAY", "DIVIDE", "ELSE",
        "END-IF", "END-EVALUATE", "END-PERFORM", "END-READ", "EVALUATE", "EXEC", "EXIT", "GO", "GOBACK", "IF", "INITIALIZE",
        "INSPECT", "MERGE", "MOVE", "MULTIPLY", "OPEN", "PERFORM", "READ", "RELEASE", "RETURN", "REWRITE", "SEARCH", "SET",
        "SORT", "START", "STOP", "STRING", "SUBTRACT", "UNSTRING", "WHEN", "WRITE"
    };

    [GeneratedRegex(@"^L(\d+) (.*)$")] private static partial Regex Numbered();
    [GeneratedRegex(@"^([A-Z0-9][A-Z0-9-]*)(?: SECTION)?\.\s*$")] private static partial Regex Header();
}
