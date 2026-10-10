using System.Text;
using System.Text.RegularExpressions;

namespace MlCpu.CobolKb;

public enum CobolSourceFormat { Fixed, Free }

/// <summary>One logical source line after stripping. <see cref="Origin"/> is the member it came from ("" = the program).</summary>
public sealed record SourceLine(string Text, string Origin, int Line, int ProgramLine);

/// <summary>A copybook that could not be found. Reported, never guessed (I5).</summary>
public sealed record MissingCopybook(string Member, int ProgramLine);

/// <summary>Regex front end (§5.3): stripping, continuation folding, COPY/INCLUDE expansion with REPLACING.</summary>
public static partial class CobolParser
{
    [GeneratedRegex(@"(?<![\w-])COPY\s+['""]?([A-Z0-9][A-Z0-9-]*)['""]?\s*(?:\.|(?:OF|IN)\s+[A-Z0-9][A-Z0-9-]*\s*\.?|REPLACING\b(.*)$|$)", RegexOptions.IgnoreCase)]
    private static partial Regex CopyDirectiveRx();

    [GeneratedRegex(@"==\s*(.*?)\s*==\s+BY\s+==\s*(.*?)\s*==", RegexOptions.IgnoreCase)]
    private static partial Regex ReplacingPairRx();

    [GeneratedRegex(@"\bINCLUDE\s+([A-Z0-9][A-Z0-9-]*)", RegexOptions.IgnoreCase)]
    private static partial Regex SqlIncludeRx();

    [GeneratedRegex(@"^\s*PROGRAM-ID\.?\s+['""]?([A-Z0-9][A-Z0-9-]*)", RegexOptions.IgnoreCase)]
    private static partial Regex ProgramIdRx();

    public static CobolSourceFormat DetectSourceFormat(string[] lines)
    {
        int fixedVotes = 0, freeVotes = 0;
        foreach (var l in lines)
        {
            if (l.Length == 0) continue;
            if (l.Length >= 7 && (char.IsDigit(l[0]) || l[0] == ' ') && l.Substring(0, Math.Min(6, l.Length)).All(c => c == ' ' || char.IsDigit(c))) fixedVotes++;
            else freeVotes++;
        }
        return freeVotes > fixedVotes ? CobolSourceFormat.Free : CobolSourceFormat.Fixed;
    }

    /// <summary>Strip sequence/indicator/identification areas. Comment lines become empty so line numbers stay stable.</summary>
    public static string[] Strip(string raw) => Strip(raw, DetectSourceFormat(SplitLines(raw)));

    public static string[] Strip(string raw, CobolSourceFormat format) =>
        StripFoldingContinuations(SplitLines(raw), format);

    public static string[] SplitLines(string raw) => raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>Strip, folding '-' continuation lines into the previous code line (§5.3 step 2). The continuation line is returned empty.</summary>
    public static string[] StripFoldingContinuations(string[] lines, CobolSourceFormat format)
    {
        var result = new string[lines.Length];
        int lastCode = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (format == CobolSourceFormat.Free)
            {
                var t = l.TrimStart();
                result[i] = t.StartsWith('*') || t.StartsWith('/') || t.StartsWith("*>") ? "" : StripInlineComment(l);
                if (result[i].Trim().Length > 0) lastCode = i;
                continue;
            }
            if (l.Length <= 7) { result[i] = ""; continue; }
            char ind = l[6];
            var code = l.Length > 72 ? l.Substring(7, 65) : l.Substring(7);
            if (ind is '*' or '/' or 'D' or 'd') { result[i] = ""; continue; }
            code = StripInlineComment(code);
            if (ind == '-' && lastCode >= 0)
            {
                result[lastCode] = FoldContinuation(result[lastCode], code);
                result[i] = "";
                continue;
            }
            // An open literal runs to column 72, so its trailing spaces are part of the value.
            result[i] = OpenQuote(code) != '\0' ? code.PadRight(65) : code.TrimEnd();
            if (result[i].Trim().Length > 0) lastCode = i;
        }
        return result;
    }

    static string StripInlineComment(string code)
    {
        bool inQ = false; char q = '\0';
        for (int i = 0; i < code.Length - 1; i++)
        {
            char c = code[i];
            if (inQ) { if (c == q) inQ = false; continue; }
            if (c is '\'' or '"') { inQ = true; q = c; continue; }
            if (c == '*' && code[i + 1] == '>') return code.Substring(0, i);
        }
        return code;
    }

    /// <summary>Fold one continuation: an open literal continues after the quote on the next line; otherwise the words join.</summary>
    static string FoldContinuation(string prev, string cont)
    {
        var open = OpenQuote(prev);
        var c = cont.TrimStart();
        if (open != '\0')
        {
            // Continued literal: the previous line runs to column 72 (padding is part of the literal); next starts after its quote.
            if (c.Length > 0 && c[0] == open) c = c.Substring(1);
            return prev + c;
        }
        return prev.TrimEnd() + c;
    }

    static char OpenQuote(string s)
    {
        char q = '\0';
        foreach (var ch in s)
        {
            if (q == '\0') { if (ch is '\'' or '"') q = ch; }
            else if (ch == q) q = '\0';
        }
        return q;
    }

    public static string? FindProgramId(IEnumerable<string> lines)
    {
        foreach (var l in lines)
        {
            var m = ProgramIdRx().Match(l);
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
        }
        return null;
    }

    /// <summary>
    /// Expand COPY and EXEC SQL INCLUDE recursively (depth 8, stack-based cycle guard). Lines must be stripped (<see cref="Strip(string)"/>).
    /// REPLACING pairs are case-insensitive literal substring replacements over every member line (§5.3 step 3).
    /// </summary>
    public static List<SourceLine> ExpandCopies(string[] stripped, Func<string, string[]?>? copyResolver, List<MissingCopybook>? missing = null)
    {
        var output = new List<SourceLine>();
        var programLines = stripped.Select((t, i) => new SourceLine(t, "", i + 1, i + 1)).ToList();
        Expand(programLines, copyResolver, output, missing, new Stack<string>(), 0);
        return output;
    }

    static void Expand(List<SourceLine> lines, Func<string, string[]?>? resolver, List<SourceLine> output,
        List<MissingCopybook>? missing, Stack<string> stack, int depth)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var m = CopyDirectiveRx().Match(line.Text);
            bool isInclude = false;
            if (!m.Success)
            {
                var inc = SqlIncludeRx().Match(line.Text);
                if (inc.Success && line.Text.Contains("EXEC", StringComparison.OrdinalIgnoreCase))
                {
                    m = inc; isInclude = true;
                }
            }
            if (!m.Success || depth >= 8) { output.Add(line); continue; }

            var member = m.Groups[1].Value.ToUpperInvariant();
            // Gather the whole directive up to its terminating period (REPLACING may span lines).
            var directive = new StringBuilder(line.Text.Substring(m.Index));
            int last = i;
            if (!isInclude)
            {
                while (!EndsWithPeriod(directive.ToString()) && last + 1 < lines.Count)
                {
                    last++;
                    directive.Append(' ').Append(lines[last].Text);
                }
            }
            else
            {
                // INCLUDE: consume through END-EXEC (at most 12 lines).
                while (!directive.ToString().Contains("END-EXEC", StringComparison.OrdinalIgnoreCase) && last + 1 < lines.Count && last - i < 12)
                {
                    last++;
                    directive.Append(' ').Append(lines[last].Text);
                }
            }
            var prefix = line.Text.Substring(0, m.Index);
            if (isInclude)
            {
                var execAt = prefix.LastIndexOf("EXEC", StringComparison.OrdinalIgnoreCase);
                if (execAt >= 0) prefix = prefix.Substring(0, execAt);
            }
            var pairs = ReplacingPairRx().Matches(directive.ToString())
                .Select(p => (Old: p.Groups[1].Value, New: p.Groups[2].Value)).ToList();

            var memberLines = stack.Contains(member) ? null : resolver?.Invoke(member);
            if (memberLines is null)
            {
                missing?.Add(new MissingCopybook(member, line.ProgramLine));
                output.Add(line with { Text = prefix });
                for (int k = i + 1; k <= last; k++) output.Add(lines[k] with { Text = "" });
                i = last;
                continue;
            }
            if (prefix.Trim().Length > 0) output.Add(line with { Text = prefix });
            var stripped = memberLines;
            var sub = new List<SourceLine>();
            for (int k = 0; k < stripped.Length; k++)
            {
                var text = stripped[k];
                foreach (var (o, n) in pairs)
                    if (o.Length > 0) text = text.Replace(o, n, StringComparison.OrdinalIgnoreCase);
                sub.Add(new SourceLine(text, member, k + 1, line.ProgramLine));
            }
            stack.Push(member);
            Expand(sub, resolver, output, missing, stack, depth + 1);
            stack.Pop();
            // Text after the directive's period on the last line stays in place.
            var lastText = lines[last].Text;
            var tail = TextAfterDirective(lastText, isInclude);
            for (int k = i + 1; k < last; k++) output.Add(lines[k] with { Text = "" });
            if (last > i) output.Add(lines[last] with { Text = tail });
            else if (tail.Trim().Length > 0) output.Add(line with { Text = tail });
            i = last;
        }
    }

    static bool EndsWithPeriod(string s)
    {
        var t = s.TrimEnd();
        if (!t.EndsWith('.')) return false;
        // A period inside an unterminated pseudo-text (==...==) does not end the directive.
        return Regex.Matches(t, "==").Count % 2 == 0;
    }

    static string TextAfterDirective(string lastText, bool include)
    {
        if (include)
        {
            var at = lastText.IndexOf("END-EXEC", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return "";
            var rest = lastText.Substring(at + 8);
            return rest.TrimStart().StartsWith('.') ? rest.TrimStart().Substring(1) : rest;
        }
        var p = lastText.LastIndexOf('.');
        return p >= 0 && p + 1 < lastText.Length ? lastText.Substring(p + 1) : "";
    }
}
