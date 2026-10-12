using System.Text;

namespace MlCpu.CobolKb;

/// <summary>
/// COPY ... REPLACING and the REPLACE statement as GnuCOBOL 3.x applies them: matching is by text words, not by
/// substrings, and is case-insensitive (literals included). A text word is a literal, a run of characters up to a
/// space, or one of the separators '(' ')' ':' (so ==:TAG:== and ==(TAG)== replace part of a word, and ==X== matches
/// the X of PIC X(4)); a comma or semicolon followed by a space is a space, and a period followed by a space (or the
/// end of a line) is a word of its own. The replacement text takes the place of exactly the characters it matched.
/// LEADING / TRAILING (COBOL 2002) replace the start or end of a single word.
/// </summary>
public static class TextReplacing
{
    public enum Mode { Full, Leading, Trailing }

    public sealed record Pair(List<string> From, string To, Mode Mode);

    /// <summary>A text word: its line (index into the line list), character span and text.</summary>
    public sealed record Word(int Line, int Start, int End, string Text)
    {
        public bool IsPeriod => Text == ".";
    }

    /// <summary>The text words of a sequence of lines.</summary>
    public static List<Word> Words(IReadOnlyList<string> lines)
    {
        var words = new List<Word>();
        for (int li = 0; li < lines.Count; li++)
        {
            var s = lines[li];
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if ((c is ',' or ';') && (i + 1 >= s.Length || char.IsWhiteSpace(s[i + 1]))) { i++; continue; }
                if (c is '(' or ')' or ':') { words.Add(new Word(li, i, i + 1, c.ToString())); i++; continue; }
                if (c == '.' && (i + 1 >= s.Length || char.IsWhiteSpace(s[i + 1]))) { words.Add(new Word(li, i, i + 1, ".")); i++; continue; }
                int start = i;
                // A literal, with an optional X/Z/N/G/B prefix glued to its quote.
                int q = c is '\'' or '"' ? i : (i + 1 < s.Length && char.IsLetter(c) && s[i + 1] is '\'' or '"' ? i + 1 : -1);
                if (q >= 0)
                {
                    char quote = s[q];
                    int j = q + 1;
                    while (j < s.Length)
                    {
                        if (s[j] == quote) { if (j + 1 < s.Length && s[j + 1] == quote) { j += 2; continue; } j++; break; }
                        j++;
                    }
                    words.Add(new Word(li, start, j, s[start..j]));
                    i = j;
                    continue;
                }
                int k = i;
                while (k < s.Length && !char.IsWhiteSpace(s[k]) && s[k] is not ('(' or ')' or ':' or '\'' or '"')
                       && !((s[k] is ',' or ';' or '.') && (k + 1 >= s.Length || char.IsWhiteSpace(s[k + 1]))))
                {
                    if (s[k] == '=' && k + 1 < s.Length && s[k + 1] == '=' ) break;
                    k++;
                }
                if (k == i)
                {
                    // '==' pseudo-text delimiter.
                    if (s[i] == '=' && i + 1 < s.Length && s[i + 1] == '=') { words.Add(new Word(li, i, i + 2, "==")); i += 2; continue; }
                    k = i + 1;
                }
                words.Add(new Word(li, i, k, s[i..k]));
                i = k;
            }
        }
        return words;
    }

    static List<string> Texts(string pseudo) => Words([pseudo]).Select(w => w.Text).ToList();

    /// <summary>
    /// Parse the operands of REPLACING / REPLACE: [LEADING|TRAILING] (==pseudo-text== | literal | word) BY (==...== | literal | word), repeated.
    /// Stops at the end of the words or at a period that is not inside pseudo-text. Returns null when malformed.
    /// </summary>
    public static List<Pair>? ParsePairs(IReadOnlyList<Word> w, IReadOnlyList<string> lines, ref int i)
    {
        var pairs = new List<Pair>();
        while (i < w.Count && !w[i].IsPeriod)
        {
            var mode = Mode.Full;
            if (Is(w[i], "LEADING")) { mode = Mode.Leading; i++; }
            else if (Is(w[i], "TRAILING")) { mode = Mode.Trailing; i++; }
            var from = Operand(w, lines, ref i);
            if (from is null || i >= w.Count || !Is(w[i], "BY")) return null;
            i++;
            var to = Operand(w, lines, ref i);
            if (to is null) return null;
            var fromWords = Texts(from);
            if (fromWords.Count == 0) return null;
            pairs.Add(new Pair(fromWords, to.Trim(), mode));
        }
        return pairs;
    }

    static bool Is(Word w, string kw) => string.Equals(w.Text, kw, StringComparison.OrdinalIgnoreCase);

    /// <summary>One operand's text: the characters between == and == (any lines), or a single literal or word.</summary>
    static string? Operand(IReadOnlyList<Word> w, IReadOnlyList<string> lines, ref int i)
    {
        if (i >= w.Count) return null;
        if (w[i].Text != "==") return w[i++].Text;
        var open = w[i];
        int j = i + 1;
        while (j < w.Count && w[j].Text != "==") j++;
        if (j >= w.Count) return null;
        var close = w[j];
        i = j + 1;
        return Slice(lines, open.Line, open.End, close.Line, close.Start);
    }

    /// <summary>The text between two positions, lines joined by a space.</summary>
    static string Slice(IReadOnlyList<string> lines, int l1, int c1, int l2, int c2)
    {
        if (l1 == l2) return lines[l1][c1..c2];
        var sb = new StringBuilder(lines[l1][c1..]);
        for (int l = l1 + 1; l < l2; l++) sb.Append(' ').Append(lines[l]);
        return sb.Append(' ').Append(lines[l2][..c2]).ToString();
    }

    /// <summary>One textual edit: replace the characters from (Line1, Start) to (Line2, End) by <see cref="Text"/>.</summary>
    public sealed record Edit(int Line1, int Start, int Line2, int End, string Text);

    /// <summary>Try every pair at word <paramref name="i"/>; on a match returns the edit and the number of words it consumed.</summary>
    public static (Edit Edit, int Count)? Match(IReadOnlyList<Word> w, int i, IReadOnlyList<Pair> pairs)
    {
        foreach (var p in pairs)
        {
            if (p.Mode != Mode.Full)
            {
                var word = w[i];
                var part = p.From[0];
                if (p.From.Count != 1 || word.Text.Length <= part.Length) continue;
                if (p.Mode == Mode.Leading && word.Text.StartsWith(part, StringComparison.OrdinalIgnoreCase))
                    return (new Edit(word.Line, word.Start, word.Line, word.Start + part.Length, p.To), 1);
                if (p.Mode == Mode.Trailing && word.Text.EndsWith(part, StringComparison.OrdinalIgnoreCase))
                    return (new Edit(word.Line, word.End - part.Length, word.Line, word.End, p.To), 1);
                continue;
            }
            if (i + p.From.Count > w.Count) continue;
            bool ok = true;
            for (int k = 0; k < p.From.Count && ok; k++) ok = string.Equals(w[i + k].Text, p.From[k], StringComparison.OrdinalIgnoreCase);
            if (!ok) continue;
            var last = w[i + p.From.Count - 1];
            return (new Edit(w[i].Line, w[i].Start, last.Line, last.End, p.To), p.From.Count);
        }
        return null;
    }

    /// <summary>Apply edits (non-overlapping, any order) to the lines in place. A multi-line edit leaves its text on the first line and blanks the rest.</summary>
    public static void ApplyEdits(IList<string> lines, IEnumerable<Edit> edits)
    {
        foreach (var e in edits.OrderByDescending(e => e.Line1).ThenByDescending(e => e.Start))
        {
            if (e.Line1 == e.Line2)
            {
                var s = lines[e.Line1];
                lines[e.Line1] = s[..e.Start] + e.Text + s[e.End..];
                continue;
            }
            lines[e.Line2] = new string(' ', e.End) + lines[e.Line2][e.End..];
            for (int l = e.Line1 + 1; l < e.Line2; l++) lines[l] = "";
            lines[e.Line1] = lines[e.Line1][..e.Start] + e.Text;
        }
    }

    /// <summary>COPY ... REPLACING: every match of the pairs in the member's lines.</summary>
    public static void ReplaceAll(IList<string> lines, IReadOnlyList<Pair> pairs)
    {
        if (pairs.Count == 0) return;
        var w = Words(lines.ToList());
        var edits = new List<Edit>();
        for (int i = 0; i < w.Count;)
        {
            var m = Match(w, i, pairs);
            if (m is null) { i++; continue; }
            edits.Add(m.Value.Edit);
            i += m.Value.Count;
        }
        ApplyEdits(lines, edits);
    }

    /// <summary>
    /// The REPLACE statement over a whole (COPY-expanded) source: each REPLACE ... . replaces the previous set from
    /// the word after its period; REPLACE OFF ends replacement. The statements themselves are blanked.
    /// Returns a diagnostic for a malformed REPLACE, else null.
    /// </summary>
    public static string? ApplyReplaceStatements(IList<string> lines)
    {
        var w = Words(lines.ToList());
        if (!w.Any(x => Is(x, "REPLACE"))) return null;
        var edits = new List<Edit>();
        IReadOnlyList<Pair> active = [];
        string? problem = null;
        for (int i = 0; i < w.Count;)
        {
            if (Is(w[i], "REPLACE") && i + 1 < w.Count && (w[i + 1].Text == "==" || Is(w[i + 1], "OFF") || Is(w[i + 1], "LEADING") || Is(w[i + 1], "TRAILING")))
            {
                int start = i;
                int j = i + 1;
                if (Is(w[j], "OFF")) { active = []; j++; }
                else
                {
                    var pairs = ParsePairs(w, lines.ToList(), ref j);
                    if (pairs is null) { problem ??= $"malformed REPLACE statement at source line {w[i].Line + 1}"; i++; continue; }
                    active = pairs;
                }
                if (j < w.Count && w[j].IsPeriod) j++;
                var last = w[j - 1];
                edits.Add(new Edit(w[start].Line, w[start].Start, last.Line, last.End, ""));
                i = j;
                continue;
            }
            var m = active.Count == 0 ? null : Match(w, i, active);
            if (m is null) { i++; continue; }
            edits.Add(m.Value.Edit);
            i += m.Value.Count;
        }
        ApplyEdits(lines, edits);
        return problem;
    }
}
