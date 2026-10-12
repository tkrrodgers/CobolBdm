namespace MlCpu.SemanticKb.Text;

/// <summary>
/// Identifier-aware word splitting shared by BM25 and the glossary: COBOL names split at '-' and '_' (and are also kept
/// whole), Java camelCase and PascalCase split at case changes ("calculateEMIValue" → calculate, emi, value), all lower
/// case. Digits stay with their word ("FNEM0300", "r-cbtrn02c-004" → r, cbtrn02c, 004).
/// </summary>
public static class Terms
{
    /// <summary>Sub-words in order (for phrase matching).</summary>
    public static List<string> Words(string text)
    {
        var words = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetterOrDigit(text[i])) { i++; continue; }
            int start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]))) i++;
            SplitCamel(text.AsSpan(start, i - start), words);
        }
        return words;
    }

    /// <summary>Index terms: the sub-words plus every whole COBOL name with hyphens (e.g. "ws-df-rate") and every whole
    /// identifier that the camel split broke up.</summary>
    public static List<string> IndexTerms(string text)
    {
        var terms = Words(text);
        int i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetterOrDigit(text[i])) { i++; continue; }
            int start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || (text[i] is '-' or '_' && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1])))) i++;
            var whole = text.AsSpan(start, i - start);
            if (whole.IndexOfAny('-', '_') >= 0 || HasInnerCase(whole)) terms.Add(whole.ToString().ToLowerInvariant());
        }
        return terms;
    }

    static bool HasInnerCase(ReadOnlySpan<char> w)
    {
        for (int k = 1; k < w.Length; k++) if (char.IsUpper(w[k]) && char.IsLower(w[k - 1])) return true;
        return false;
    }

    static void SplitCamel(ReadOnlySpan<char> w, List<string> into)
    {
        int start = 0;
        for (int k = 1; k < w.Length; k++)
        {
            bool boundary =
                (char.IsUpper(w[k]) && char.IsLower(w[k - 1])) ||                                    // fooBar
                (char.IsUpper(w[k]) && k + 1 < w.Length && char.IsLower(w[k + 1]) && char.IsUpper(w[k - 1])); // EMIValue
            if (boundary) { Add(w[start..k], into); start = k; }
        }
        Add(w[start..], into);
    }

    static void Add(ReadOnlySpan<char> s, List<string> into)
    {
        if (s.Length > 0) into.Add(s.ToString().ToLowerInvariant());
    }
}
