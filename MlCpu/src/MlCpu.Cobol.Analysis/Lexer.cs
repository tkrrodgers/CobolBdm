using System.Text;
using MlCpu.CobolKb;

namespace MlCpu.Cobol.Analysis;

public enum TokKind { Word, Number, Literal, Punct, Period, Eof }

/// <summary>A lexical token. <see cref="Line"/> is the program line (COPY line for copybook text); <see cref="Col"/> is 0-based in the code area.</summary>
public sealed record Token(TokKind Kind, string Text, int Line, int Col, string Origin)
{
    public string Upper => Kind == TokKind.Word ? Text.ToUpperInvariant() : Text;
    public bool Is(string w) => Kind == TokKind.Word && string.Equals(Text, w, StringComparison.OrdinalIgnoreCase);
    public bool IsPunct(string p) => Kind == TokKind.Punct && Text == p;
    public override string ToString() => Text;
}

/// <summary>COBOL lexer over copy-expanded lines. Literals keep their raw value; hex literals are decoded; separator commas/semicolons are dropped.</summary>
public static class Lexer
{
    public static List<Token> Tokenize(IReadOnlyList<SourceLine> lines)
    {
        var toks = new List<Token>();
        foreach (var sl in lines)
        {
            var s = sl.Text;
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                int start = i;
                if ((c is 'X' or 'x' or 'Z' or 'z' or 'N' or 'n' or 'G' or 'g') && i + 1 < s.Length && (s[i + 1] is '\'' or '"'))
                {
                    var (lit, next) = ReadQuoted(s, i + 1);
                    if (c is 'X' or 'x') lit = DecodeHex(lit);
                    toks.Add(new Token(TokKind.Literal, lit, sl.ProgramLine, start, sl.Origin));
                    i = next; continue;
                }
                if (c is '\'' or '"')
                {
                    var (lit, next) = ReadQuoted(s, i);
                    toks.Add(new Token(TokKind.Literal, lit, sl.ProgramLine, start, sl.Origin));
                    i = next; continue;
                }
                if (c == '.' )
                {
                    if (i + 1 < s.Length && char.IsDigit(s[i + 1]) && (toks.Count == 0 || toks[^1].Kind != TokKind.Word || i == 0 || char.IsWhiteSpace(s[i - 1])))
                    {
                        int j = i + 1; while (j < s.Length && char.IsDigit(s[j])) j++;
                        toks.Add(new Token(TokKind.Number, "0" + s.Substring(i, j - i), sl.ProgramLine, start, sl.Origin));
                        i = j; continue;
                    }
                    toks.Add(new Token(TokKind.Period, ".", sl.ProgramLine, start, sl.Origin));
                    i++; continue;
                }
                if (c is ',' or ';') { i++; continue; }
                if ((c is '+' or '-') && i + 1 < s.Length && (char.IsDigit(s[i + 1]) || (s[i + 1] == '.' && i + 2 < s.Length && char.IsDigit(s[i + 2])))
                    && (i == 0 || char.IsWhiteSpace(s[i - 1]) || s[i - 1] == '('))
                {
                    int j = ScanNumber(s, i + 1);
                    if (j > 0 && (j >= s.Length || !IsWordChar(s[j])))
                    {
                        toks.Add(new Token(TokKind.Number, s.Substring(i, j - i), sl.ProgramLine, start, sl.Origin));
                        i = j; continue;
                    }
                }
                if (IsWordChar(c))
                {
                    // A word may hold hyphens anywhere but at its ends, consecutive ones included (M1-J3-S--EVENT--HEAD).
                    int j = i;
                    while (j < s.Length)
                    {
                        if (IsWordChar(s[j])) { j++; continue; }
                        int k = j;
                        while (k < s.Length && s[k] == '-') k++;
                        if (k > j && k < s.Length && IsWordChar(s[k])) { j = k; continue; }
                        break;
                    }
                    var w = s.Substring(i, j - i);
                    // A numeric literal with a decimal point: 12.50 (the point is followed by a digit).
                    if (w.All(char.IsDigit) && j + 1 < s.Length && s[j] == '.' && char.IsDigit(s[j + 1]))
                    {
                        int k = j + 1; while (k < s.Length && char.IsDigit(s[k])) k++;
                        toks.Add(new Token(TokKind.Number, s.Substring(i, k - i), sl.ProgramLine, start, sl.Origin));
                        i = k; continue;
                    }
                    toks.Add(new Token(w.All(char.IsDigit) ? TokKind.Number : TokKind.Word, w, sl.ProgramLine, start, sl.Origin));
                    i = j; continue;
                }
                if ((c is '>' or '<') && i + 1 < s.Length && s[i + 1] == '=') { toks.Add(new Token(TokKind.Punct, c + "=", sl.ProgramLine, start, sl.Origin)); i += 2; continue; }
                if (c == '<' && i + 1 < s.Length && s[i + 1] == '>') { toks.Add(new Token(TokKind.Punct, "<>", sl.ProgramLine, start, sl.Origin)); i += 2; continue; }
                if (c == '*' && i + 1 < s.Length && s[i + 1] == '*') { toks.Add(new Token(TokKind.Punct, "**", sl.ProgramLine, start, sl.Origin)); i += 2; continue; }
                toks.Add(new Token(TokKind.Punct, c.ToString(), sl.ProgramLine, start, sl.Origin));
                i++;
            }
        }
        toks.Add(new Token(TokKind.Eof, "", lines.Count > 0 ? lines[^1].ProgramLine : 0, 0, ""));
        return toks;
    }

    static int ScanNumber(string s, int i)
    {
        int j = i; bool digits = false;
        while (j < s.Length && char.IsDigit(s[j])) { j++; digits = true; }
        if (j + 1 < s.Length && s[j] == '.' && char.IsDigit(s[j + 1])) { j++; while (j < s.Length && char.IsDigit(s[j])) { j++; digits = true; } }
        return digits ? j : -1;
    }

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '#' || c == '@' || c == '$';

    static (string, int) ReadQuoted(string s, int i)
    {
        char q = s[i];
        var sb = new StringBuilder();
        int j = i + 1;
        while (j < s.Length)
        {
            if (s[j] == q)
            {
                if (j + 1 < s.Length && s[j + 1] == q) { sb.Append(q); j += 2; continue; }
                return (sb.ToString(), j + 1);
            }
            sb.Append(s[j]); j++;
        }
        return (sb.ToString(), j);
    }

    static string DecodeHex(string hex)
    {
        var sb = new StringBuilder();
        for (int i = 0; i + 1 < hex.Length; i += 2)
            sb.Append((char)Convert.ToInt32(hex.Substring(i, 2), 16));
        return sb.ToString();
    }
}
