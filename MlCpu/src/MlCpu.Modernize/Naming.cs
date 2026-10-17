using System.Text;

namespace MlCpu.Modernize;

/// <summary>COBOL data names to Java names.</summary>
public static class Naming
{
    static readonly HashSet<string> Reserved =
    [
        "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue", "default", "do", "double",
        "else", "enum", "extends", "final", "finally", "float", "for", "goto", "if", "implements", "import", "instanceof", "int", "interface",
        "long", "native", "new", "package", "private", "protected", "public", "return", "short", "static", "strictfp", "super", "switch",
        "synchronized", "this", "throw", "throws", "transient", "try", "void", "volatile", "while", "var", "record", "yield", "true", "false",
        "null", "sealed", "permits", "_",
    ];

    static IEnumerable<string> Words(string cobol) =>
        cobol.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant());

    public static string Camel(string cobol)
    {
        var sb = new StringBuilder();
        foreach (var w in Words(cobol))
        {
            if (sb.Length == 0) sb.Append(w);
            else sb.Append(char.ToUpperInvariant(w[0])).Append(w[1..]);
        }
        return Safe(sb.Length == 0 ? "value" : sb.ToString());
    }

    public static string Pascal(string cobol)
    {
        var c = Camel(cobol).TrimEnd('_');
        return char.ToUpperInvariant(c[0]) + c[1..];
    }

    public static string Constant(string cobol) => Safe(string.Join('_', Words(cobol).Select(w => w.ToUpperInvariant())));

    public static string Safe(string name)
    {
        if (name.Length == 0) return "value";
        if (char.IsDigit(name[0])) name = "v" + name;
        return Reserved.Contains(name) ? name + "_" : name;
    }

    /// <summary>"loanApplica" is a truncated prefix of "loanApplicationTerms": complete it.</summary>
    public static string Complete(string truncated, IEnumerable<string> candidates)
    {
        string? best = null;
        foreach (var c in candidates)
        {
            if (c.Length <= truncated.Length || !c.StartsWith(truncated, StringComparison.Ordinal)) continue;
            if (best is null || c.Length < best.Length) best = c;
        }
        return best ?? truncated;
    }

    public static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
    public static string UpperFirst(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
