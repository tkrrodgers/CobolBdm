using System.Text.RegularExpressions;

namespace MlCpu.DesignModel;

/// <summary>
/// Missing imports, added by the harness before each compile (pipeline v2): a type the code uses by its simple name that is a
/// record-kit class, a platform class, an entry interface or a common java.* class, and that the file neither declares nor
/// imports, gets its import. A missing import is not a design question, so it should not cost an attempt.
/// </summary>
public static class Imports
{
    static readonly string[] Jdk =
    [
        "java.math.BigDecimal", "java.math.BigInteger", "java.math.RoundingMode",
        "java.util.List", "java.util.ArrayList", "java.util.Map", "java.util.HashMap", "java.util.LinkedHashMap", "java.util.TreeMap",
        "java.util.Set", "java.util.HashSet", "java.util.TreeSet", "java.util.Optional", "java.util.Arrays", "java.util.Collections",
        "java.util.Objects", "java.util.Iterator", "java.util.Deque", "java.util.ArrayDeque", "java.util.Locale",
        "java.util.function.Consumer", "java.util.function.Supplier", "java.util.function.Function", "java.util.function.BiConsumer",
        "java.util.function.Predicate", "java.nio.file.Path", "java.nio.file.Files", "java.nio.charset.StandardCharsets",
        "java.io.IOException", "java.io.UncheckedIOException", "java.time.OffsetDateTime", "java.time.LocalDate", "java.time.LocalDateTime",
        "java.time.LocalTime", "java.time.format.DateTimeFormatter"
    ];

    static readonly Regex ImportLine = new(@"^\s*import\s+(static\s+)?([\w.]+)(\.\*)?\s*;", RegexOptions.Multiline);
    static readonly Regex PackageLine = new(@"^\s*package\s+[\w.]+\s*;[^\n]*\n", RegexOptions.Multiline);
    static readonly Regex Strings = new(@"""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])'|//[^\n]*|/\*.*?\*/", RegexOptions.Singleline);
    static readonly Regex TypeToken = new(@"(?<![\w.])([A-Z]\w*)\b");

    /// <summary>Simple name → fully qualified name of every class the code may import (ambiguous names left out).</summary>
    public static Dictionary<string, string> Known(string harnessMain, string platformPackage, string kitPackage)
    {
        var all = new List<string>(Jdk) { "com.mlcpu.rt.ModernStep", "com.mlcpu.rt.ModernProgram" };
        foreach (var pkg in new[] { platformPackage, kitPackage })
        {
            var dir = Path.Combine(harnessMain, Path.Combine(pkg.Split('.')));
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.java", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(harnessMain, f).Replace('\\', '/');
                all.Add(rel[..^5].Replace('/', '.'));
            }
        }
        return all.GroupBy(fq => fq[(fq.LastIndexOf('.') + 1)..]).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
    }

    /// <summary>Adds the missing imports to every file (keyed by type name); returns "File: import" lines for the record.</summary>
    public static List<string> Complete(IDictionary<string, string> files, IReadOnlyDictionary<string, string> known)
    {
        var added = new List<string>();
        var own = files.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var name in files.Keys.ToList())
        {
            var src = files[name];
            var imported = new HashSet<string>(StringComparer.Ordinal);
            var wildcards = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in ImportLine.Matches(src))
            {
                if (m.Groups[3].Success) wildcards.Add(m.Groups[2].Value);
                else imported.Add(m.Groups[2].Value[(m.Groups[2].Value.LastIndexOf('.') + 1)..]);
            }
            // Types the file declares itself (nested classes, records, enums) shadow the known ones.
            var declared = Regex.Matches(src, @"\b(?:class|record|interface|enum)\s+([A-Z]\w*)").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            var code = Strings.Replace(src, " ");
            var missing = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match t in TypeToken.Matches(code))
            {
                var simple = t.Groups[1].Value;
                if (own.Contains(simple) || declared.Contains(simple) || imported.Contains(simple) || !known.TryGetValue(simple, out var fq)) continue;
                var pkg = fq[..fq.LastIndexOf('.')];
                if (wildcards.Contains(pkg)) continue;
                missing.Add(fq);
            }
            if (missing.Count == 0) continue;
            var lines = string.Concat(missing.Select(fq => $"import {fq};\n"));
            var lastImport = ImportLine.Matches(src).LastOrDefault();
            if (lastImport is not null)
            {
                int end = src.IndexOf('\n', lastImport.Index + lastImport.Length - 1);
                end = end < 0 ? src.Length : end + 1;
                files[name] = src[..end] + lines + src[end..];
            }
            else if (PackageLine.Match(src) is { Success: true } p)
                files[name] = src[..(p.Index + p.Length)] + "\n" + lines + src[(p.Index + p.Length)..];
            else continue;
            added.AddRange(missing.Select(fq => $"{name}: import {fq}"));
        }
        return added;
    }
}
