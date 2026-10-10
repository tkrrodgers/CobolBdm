using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MlCpu.Cobol.Analysis.Entities;
using MlCpu.Cobol.Analysis.Naming;

namespace MlCpu.Cobol.Analysis.Projections;

/// <summary>A generated program as the projections see it: the faithful class plus the analysis that produced it.</summary>
public sealed record ProgramUnit(string ProgramId, string ClassName, CobolProgram Model, ProgramGraph Graph, PartitionPlan Plan)
{
    public string Pid => ClassName["Prog_".Length..];
}

public sealed record ProjectionSkip(string Subject, string Reason);

/// <summary>What one projection did: applied subjects, skips with verbatim reasons, and pass-specific facts.</summary>
public sealed class ProjectionLedger(string pass)
{
    public string Pass { get; } = pass;
    public List<string> Applied { get; } = [];
    public List<ProjectionSkip> Skipped { get; } = [];
    public SortedDictionary<string, object> Facts { get; } = new(StringComparer.Ordinal);

    public void Skip(string subject, string reason) => Skipped.Add(new ProjectionSkip(subject, reason));
}

/// <summary>The project being assembled: relative path → text. Projections rewrite it in place.</summary>
public sealed class ProjectFiles
{
    readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _languages = new(StringComparer.Ordinal);

    public void Set(string path, string content, string? language = null)
    {
        _files[path] = content;
        _languages[path] = language ?? _languages.GetValueOrDefault(path) ?? Language(path);
    }

    public string? Get(string path) => _files.GetValueOrDefault(path);
    public bool Exists(string path) => _files.ContainsKey(path);
    public void Remove(string path) { _files.Remove(path); _languages.Remove(path); }

    public void Move(string from, string to)
    {
        var text = _files[from];
        var lang = _languages[from];
        Remove(from);
        Set(to, text, lang);
    }

    public IReadOnlyList<string> Paths(string prefix = "") => _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToList();

    public IEnumerable<(string Path, string Content, string Language)> All() =>
        _files.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value, _languages[kv.Key]));

    static string Language(string path) => Path.GetExtension(path) switch
    {
        ".java" => "java", ".json" => "json", ".kts" => "kotlin", ".md" => "markdown", ".csv" => "csv", ".properties" => "properties", _ => "text"
    };
}

public static class ProjectionJson
{
    public static readonly JsonSerializerOptions Camel = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Default naming (PascalCase), indented: the CandidateHash input format for domain candidates (Q12).</summary>
    public static readonly JsonSerializerOptions Pascal = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Write(object o) => JsonSerializer.Serialize(o, Camel) + "\n";

    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static string Ledger(ProjectionLedger l) => Write(new
    {
        pass = l.Pass,
        applied = l.Applied,
        skipped = l.Skipped.Select(s => new { subject = s.Subject, reason = s.Reason }),
        facts = l.Facts
    });
}

/// <summary>Textual Java helpers (no AST, §8): brace matching that skips strings, character literals and comments.</summary>
public static class JavaText
{
    /// <summary>Index of the '}' matching the '{' at <paramref name="open"/>, or -1 when unbalanced.</summary>
    public static int MatchBrace(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { i = s.IndexOf('\n', i); if (i < 0) return -1; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') { i = s.IndexOf("*/", i + 2, StringComparison.Ordinal); if (i < 0) return -1; i++; continue; }
            if (c is '"' or '\'')
            {
                if (c == '"' && i + 2 < s.Length && s[i + 1] == '"' && s[i + 2] == '"')
                {
                    i = s.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                    if (i < 0) return -1;
                    i += 2; continue;
                }
                for (i++; i < s.Length && s[i] != c; i++) if (s[i] == '\\') i++;
                continue;
            }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>
    /// The member whose declaration line starts with <paramref name="signature"/> (after indentation), including a preceding
    /// annotation line and the trailing newline: (start, end) or null when absent or unbalanced.
    /// </summary>
    public static (int Start, int End, int BodyOpen, int BodyClose)? Member(string s, string signature)
    {
        var at = s.IndexOf("\n    " + signature, StringComparison.Ordinal);
        if (at < 0) return null;
        int start = at + 1;
        var prev = s.LastIndexOf('\n', at - 1);
        if (prev >= 0 && s[(prev + 1)..at].Trim().StartsWith('@')) start = prev + 1;
        var open = s.IndexOf('{', at + 5);
        if (open < 0) return null;
        var close = MatchBrace(s, open);
        if (close < 0) return (start, -1, open, -1);
        var end = s.IndexOf('\n', close);
        return (start, end < 0 ? s.Length : end + 1, open, close);
    }

    public static bool MentionsThis(string body) => System.Text.RegularExpressions.Regex.IsMatch(body, @"(?<![A-Za-z0-9_$.])this(?![A-Za-z0-9_$])");

    public static string Lit(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            if (c is '"' or '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c < 0x20 || c > 0x7E) sb.Append($"\\u{(int)c:x4}");
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}

public static class JavaNames
{
    static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue", "default", "do", "double",
        "else", "enum", "extends", "final", "finally", "float", "for", "goto", "if", "implements", "import", "instanceof", "int", "interface",
        "long", "native", "new", "package", "private", "protected", "public", "return", "short", "static", "strictfp", "super", "switch",
        "synchronized", "this", "throw", "throws", "transient", "try", "void", "volatile", "while", "record", "var", "yield", "true", "false", "null"
    };

    public static string Member(string cobol)
    {
        var n = IdentifierModernizer.Modernize(cobol);
        return Reserved.Contains(n) ? n + "_" : n;
    }

    public static string Type(string cobol)
    {
        var m = Member(cobol).TrimEnd('_');
        return char.ToUpperInvariant(m[0]) + m[1..];
    }

    public static string Package(string text) => new string(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Distinct names within one scope: the second "x" becomes "x2".</summary>
    public static string Unique(string name, ISet<string> used)
    {
        var n = name;
        for (int k = 2; !used.Add(n); k++) n = name + k;
        return n;
    }
}
