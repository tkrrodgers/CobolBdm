using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MlCpu.Modernize;

/// <summary>
/// GN2, the modernity gate (SemanticModernization.md §2.2, §8): rules R1-R7 over a generated estate's sources, without a
/// compiler. Layers by package: the application layer is <c>&lt;base&gt;.batch</c>; its adapters are
/// <c>batch.record</c> (the byte views), <c>batch.platform</c> (the runtime) and <c>batch.strangler</c>; the domain is
/// everything else. Writes <c>modernize/gn2.json</c> (violations, listings) and <c>modernize/metrics.json</c>.
/// </summary>
public static partial class Gn2
{
    public sealed record Finding(string Rule, string File, int Line, string What);

    sealed class Source
    {
        public required string Rel { get; init; }
        public required string Package { get; init; }
        public required string Layer { get; init; }
        /// <summary>Lines without comments (string literals kept).</summary>
        public required string[] Code { get; init; }
        /// <summary>Lines without comments and with string and char literals blanked (for braces and types).</summary>
        public required string[] Bare { get; init; }
        public required string[] Raw { get; init; }
    }

    sealed record MethodInfo(string File, string Name, int Line, int Lines, int Complexity);

    /// <param name="originals">Source roots of the original Java, where one exists (R6 compares sizes).</param>
    public static int Run(string estateDir, IReadOnlyList<string> originals, Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        var src = Path.Combine(estateDir, "src", "main", "java");
        var paths = Directory.EnumerateFiles(src, "*.java", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var platform = paths.Select(p => Path.GetRelativePath(src, Path.GetDirectoryName(p)!).Replace('\\', '/'))
            .FirstOrDefault(d => d.EndsWith("/batch/platform", StringComparison.Ordinal));
        var basePkg = platform is null ? "" : platform[..^"/batch/platform".Length].Replace('/', '.');
        var sources = paths.Select(p => Load(src, p, basePkg)).ToList();
        var packages = sources.Select(s => s.Package).ToHashSet(StringComparer.Ordinal);
        var findings = new List<Finding>();
        var listed = new List<Finding>();
        var methods = new List<MethodInfo>();
        var original = Originals(originals);
        foreach (var s in sources)
        {
            bool inner = s.Layer is "domain" or "application";
            // the literals each variable or accessor of the file is set to or compared with: Y and N alone make a flag,
            // more values a code (DW-OK holds Y, N and F)
            var domain = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var line in s.Code)
                foreach (Match lm in Literal().Matches(line))
                {
                    var v = lm.Groups["v1"].Success ? lm.Groups["v1"].Value : lm.Groups["v2"].Success ? lm.Groups["v2"].Value : lm.Groups["v3"].Value;
                    var lit = lm.Groups["l1"].Success ? lm.Groups["l1"].Value : lm.Groups["l2"].Success ? lm.Groups["l2"].Value : lm.Groups["l3"].Value;
                    (domain.TryGetValue(v, out var set) ? set : domain[v] = new HashSet<string>(StringComparer.Ordinal)).Add(lit);
                }
            for (int i = 0; i < s.Code.Length; i++)
            {
                var code = s.Code[i];
                var bare = s.Bare[i];
                int ln = i + 1;
                // R1: no CobolBdm runtime, no Prog_/State_, no byte arrays in the domain and the application
                if (code.Contains("com.mlcpu", StringComparison.Ordinal)) findings.Add(new("R1", s.Rel, ln, "CobolBdm runtime: " + code.Trim()));
                if (ProgState().IsMatch(bare)) findings.Add(new("R1", s.Rel, ln, "Prog_/State_: " + code.Trim()));
                if (inner && ByteArray().IsMatch(bare)) findings.Add(new("R1", s.Rel, ln, "byte array: " + code.Trim()));
                // R2: no emulated control flow
                if (inner && FlagTest().IsMatch(code))
                {
                    var vars = Literal().Matches(code).Select(m => m.Groups["v1"].Success ? m.Groups["v1"].Value : m.Groups["v2"].Success ? m.Groups["v2"].Value : m.Groups["v3"].Value)
                        .Where(v => domain.TryGetValue(v, out var d) && d.Overlaps(["Y", "N"])).Distinct().ToList();
                    if (vars.Count > 0 && vars.All(v => !domain[v].IsSubsetOf(["Y", "N"])))
                        listed.Add(new("R2", s.Rel, ln, $"code field ({string.Join(", ", vars.Select(v => v + ": " + string.Join("/", domain[v].Order(StringComparer.Ordinal))))}): " + code.Trim()));
                    else findings.Add(new("R2", s.Rel, ln, "Y/N flag: " + code.Trim()));
                }
                if (inner && Dispatch().IsMatch(bare)) findings.Add(new("R2", s.Rel, ln, "paragraph dispatch: " + code.Trim()));
                // R3: numbers int, long, boolean, BigDecimal; dates LocalDate; text String
                // (a type the source of truth declares in the same class is the domain's own: listed)
                if (inner && OddType().Match(bare) is { Success: true } ot)
                {
                    var odd = ot.Groups[1].Value;
                    if (s.Layer == "domain" && original.TryGetValue(s.Rel, out var org) && org.Text.Contains(odd + " ", StringComparison.Ordinal))
                        listed.Add(new("R3", s.Rel, ln, odd + " as in the original: " + code.Trim()));
                    else findings.Add(new("R3", s.Rel, ln, odd + ": " + code.Trim()));
                }
                if (inner && ObjectDecl().IsMatch(bare)) listed.Add(new("R3", s.Rel, ln, "Object-typed variable: " + code.Trim()));
                // R4: java.util, no emulation classes; R5: the domain sees no application, port or adapter
                if (Import().Match(code) is { Success: true } im)
                {
                    var imp = im.Groups[1].Value;
                    // a wildcard import names the package itself
                    var pkg = code.Contains(".*", StringComparison.Ordinal) ? imp : imp.Contains('.') ? imp[..imp.LastIndexOf('.')] : imp;
                    bool known = imp.StartsWith("java.", StringComparison.Ordinal) || imp.StartsWith("javax.", StringComparison.Ordinal)
                        || packages.Contains(pkg) || packages.Any(p => imp.StartsWith(p + ".", StringComparison.Ordinal));
                    if (!known) findings.Add(new("R4", s.Rel, ln, "import outside the JDK and the estate: " + imp));
                    if (s.Layer == "domain" && basePkg.Length > 0 && imp.StartsWith(basePkg + ".batch", StringComparison.Ordinal))
                        findings.Add(new("R5", s.Rel, ln, "the domain depends on " + imp));
                }
                if (code.Contains("fncobolgen", StringComparison.Ordinal)) findings.Add(new("R4", s.Rel, ln, "emulation class: " + code.Trim()));
                if (s.Layer == "domain" && basePkg.Length > 0 && bare.Contains(basePkg + ".batch.", StringComparison.Ordinal) && !code.TrimStart().StartsWith("import", StringComparison.Ordinal))
                    findings.Add(new("R5", s.Rel, ln, "the domain refers to the batch layer: " + code.Trim()));
                // R7: every public type and method carries its provenance
                if (PublicDecl().IsMatch(bare) && !bare.Contains(" new ", StringComparison.Ordinal))
                {
                    var doc = DocBefore(s.Raw, i);
                    if (doc is null) findings.Add(new("R7", s.Rel, ln, "no Javadoc: " + code.Trim()));
                    else if (inner && !doc.Contains("legacy", StringComparison.OrdinalIgnoreCase)) findings.Add(new("R7", s.Rel, ln, "no legacy provenance: " + code.Trim()));
                }
            }
            methods.AddRange(Methods(s));
        }
        // R6: size against the source of truth; long and complex methods are listed
        foreach (var m in methods.Where(m => m.Lines > 80 || m.Complexity > 15))
            listed.Add(new("R6", m.File, m.Line, $"{m.Name}: {m.Lines} lines, complexity {m.Complexity}"));
        int domainLines = 0, originalLines = 0;
        var ratios = new List<object>();
        foreach (var s in sources.Where(s => s.Layer == "domain"))
        {
            int mine = CodeLines(s.Code);
            if (!original.TryGetValue(s.Rel, out var o)) continue;
            domainLines += mine;
            originalLines += o.Lines;
            ratios.Add(new { file = s.Rel, modern = mine, original = o.Lines, ratio = Math.Round((double)mine / Math.Max(1, o.Lines), 2) });
        }
        if (originalLines > 0 && domainLines > 2 * originalLines)
            findings.Add(new("R6", "(domain)", 0, $"{domainLines} code lines against {originalLines} in the original: more than twice"));
        // report
        var byRule = new[] { "R1", "R2", "R3", "R4", "R5", "R6", "R7" }.ToDictionary(r => r, r => findings.Count(f => f.Rule == r));
        foreach (var (rule, n) in byRule) log($"GN2 {rule}: {(n == 0 ? "ok" : n + " violation" + (n == 1 ? "" : "s"))}");
        foreach (var f in findings.Take(40)) log($"  {f.Rule} {f.File}:{f.Line} {f.What}");
        if (findings.Count > 40) log($"  ... {findings.Count - 40} more in modernize/gn2.json");
        log($"GN2 listed: {listed.Count(l => l.Rule == "R3" && l.What.StartsWith("Object"))} Object-typed variables, {listed.Count(l => l.Rule == "R3" && !l.What.StartsWith("Object"))} types as in the original, {listed.Count(l => l.Rule == "R2")} code-field lines, {listed.Count(l => l.Rule == "R6")} long or complex methods"
            + (originalLines > 0 ? $"; domain {domainLines} code lines, original {originalLines} ({(double)domainLines / originalLines:0.00}x)" : ""));
        log(findings.Count == 0 ? "GN2: pass" : $"GN2: FAIL ({findings.Count} violations)");
        var meta = Path.Combine(estateDir, "modernize");
        Directory.CreateDirectory(meta);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(meta, "gn2.json"), JsonSerializer.Serialize(new
        {
            pass = findings.Count == 0,
            rules = byRule,
            violations = findings,
            listed,
        }, opts), new UTF8Encoding(false));
        var layers = sources.GroupBy(s => s.Layer).OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new { files = g.Count(), lines = g.Sum(s => s.Raw.Length), codeLines = g.Sum(s => CodeLines(s.Code)) });
        File.WriteAllText(Path.Combine(meta, "metrics.json"), JsonSerializer.Serialize(new
        {
            layers,
            methods = methods.Count,
            longestMethod = methods.OrderByDescending(m => m.Lines).Select(m => new { m.File, m.Name, m.Lines }).FirstOrDefault(),
            meanComplexity = methods.Count == 0 ? 0 : Math.Round(methods.Average(m => m.Complexity), 2),
            objectTypedVariables = listed.Count(l => l.Rule == "R3" && l.What.StartsWith("Object")),
            original = originalLines == 0 ? null : new { domainCodeLines = domainLines, originalCodeLines = originalLines, ratio = Math.Round((double)domainLines / originalLines, 2), files = ratios },
        }, opts), new UTF8Encoding(false));
        return findings.Count == 0 ? 0 : 1;
    }

    static Source Load(string src, string path, string basePkg)
    {
        var raw = File.ReadAllLines(path);
        var (code, bare) = Strip(raw);
        var rel = Path.GetRelativePath(src, path).Replace('\\', '/');
        var pkg = raw.Select(l => PackageDecl().Match(l)).FirstOrDefault(m => m.Success)?.Groups[1].Value ?? "";
        string layer = basePkg.Length == 0 ? "domain"
            : pkg == basePkg + ".batch" ? "application"
            : pkg.StartsWith(basePkg + ".batch.", StringComparison.Ordinal) ? "adapter"
            : "domain";
        return new Source { Rel = rel, Package = pkg, Layer = layer, Code = code, Bare = bare, Raw = raw };
    }

    /// <summary>Comments removed (both views); string and char literal contents blanked in the second.</summary>
    static (string[] Code, string[] Bare) Strip(string[] raw)
    {
        var code = new string[raw.Length];
        var bare = new string[raw.Length];
        bool block = false;
        for (int i = 0; i < raw.Length; i++)
        {
            var l = raw[i];
            var c = new StringBuilder();
            var b = new StringBuilder();
            for (int k = 0; k < l.Length; k++)
            {
                if (block)
                {
                    if (k + 1 < l.Length && l[k] == '*' && l[k + 1] == '/') { block = false; k++; }
                    continue;
                }
                if (k + 1 < l.Length && l[k] == '/' && l[k + 1] == '*') { block = true; k++; continue; }
                if (k + 1 < l.Length && l[k] == '/' && l[k + 1] == '/') break;
                if (l[k] is '"' or '\'')
                {
                    char q = l[k];
                    c.Append(q); b.Append(q);
                    k++;
                    while (k < l.Length && l[k] != q)
                    {
                        if (l[k] == '\\' && k + 1 < l.Length) { c.Append(l[k]).Append(l[k + 1]); b.Append("__"); k += 2; continue; }
                        c.Append(l[k]); b.Append('_');
                        k++;
                    }
                    if (k < l.Length) { c.Append(q); b.Append(q); }
                    continue;
                }
                c.Append(l[k]); b.Append(l[k]);
            }
            code[i] = c.ToString();
            bare[i] = b.ToString();
        }
        return (code, bare);
    }

    /// <summary>The Javadoc right above a declaration (annotations and blank lines between allowed), or null.</summary>
    static string? DocBefore(string[] raw, int line)
    {
        int k = line - 1;
        while (k >= 0 && (raw[k].Trim().Length == 0 || raw[k].TrimStart().StartsWith('@'))) k--;
        if (k < 0 || !raw[k].TrimEnd().EndsWith("*/", StringComparison.Ordinal)) return null;
        var sb = new StringBuilder();
        for (int j = k; j >= 0; j--)
        {
            sb.Insert(0, raw[j] + "\n");
            if (raw[j].Contains("/**", StringComparison.Ordinal)) return sb.ToString();
            if (raw[j].Contains("/*", StringComparison.Ordinal)) return null;
        }
        return null;
    }

    static IEnumerable<MethodInfo> Methods(Source s)
    {
        for (int i = 0; i < s.Bare.Length; i++)
        {
            var m = MethodDecl().Match(s.Bare[i]);
            if (!m.Success || ControlWord().IsMatch(s.Bare[i]) || !s.Bare[i].TrimEnd().EndsWith('{')) continue;
            int depth = 0, j = i, decisions = 0;
            for (; j < s.Bare.Length; j++)
            {
                foreach (var ch in s.Bare[j]) depth += ch == '{' ? 1 : ch == '}' ? -1 : 0;
                decisions += Decision().Matches(s.Bare[j]).Count;
                if (depth == 0) break;
            }
            yield return new MethodInfo(s.Rel, m.Groups[1].Value, i + 1, j - i + 1, 1 + decisions);
        }
    }

    /// <summary>The original sources (code lines, code text), keyed by their path under the source root (org/x/Y.java).</summary>
    static Dictionary<string, (int Lines, string Text)> Originals(IReadOnlyList<string> roots)
    {
        var map = new Dictionary<string, (int, string)>(StringComparer.Ordinal);
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(root, "*.java", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                var norm = f.Replace('\\', '/');
                int at = norm.LastIndexOf("/src/main/java/", StringComparison.Ordinal);
                var key = at >= 0 ? norm[(at + "/src/main/java/".Length)..] : Path.GetRelativePath(root, f).Replace('\\', '/');
                var code = Strip(File.ReadAllLines(f)).Code;
                map.TryAdd(key, (CodeLines(code), string.Join('\n', code)));
            }
        return map;
    }

    static int CodeLines(string[] code) => code.Count(l => l.Trim().Length > 0);

    [GeneratedRegex(@"^\s*package\s+([\w.]+)\s*;")] private static partial Regex PackageDecl();
    [GeneratedRegex(@"^\s*import\s+(?:static\s+)?([\w.]+?)(?:\.\*)?\s*;")] private static partial Regex Import();
    [GeneratedRegex(@"\b(?:Prog|State)_[A-Z0-9]")] private static partial Regex ProgState();
    [GeneratedRegex(@"\bbyte\s*\[\s*\]")] private static partial Regex ByteArray();
    [GeneratedRegex("""(\.equals\("[YN]"\)|"[YN]"\.equals\(|[=!]=\s*'[YN]'|=\s*"[YN]"\s*;)""")] private static partial Regex FlagTest();
    [GeneratedRegex(@"switch\s*\(\s*(pc|para|paragraph|next|goTo)\s*\)")] private static partial Regex Dispatch();
    [GeneratedRegex("""(?<v1>\w+)(?:\(\))?\.equals\("(?<l1>[^"]{1,2})"\)|"(?<l2>[^"]{1,2})"\.equals\((?<v2>\w+)(?:\(\))?\)|\b(?<v3>\w+)\s*=\s*"(?<l3>[^"]{1,2})"\s*;""")] private static partial Regex Literal();
    [GeneratedRegex(@"(?:^|[\s(,<])(double|float|short|char\s*\[\s*\])\s+\w+\s*[=;,)]")] private static partial Regex OddType();
    [GeneratedRegex(@"^\s*(?:private\s+|final\s+|static\s+)*Object\s+\w+\s*[=;]")] private static partial Regex ObjectDecl();
    [GeneratedRegex(@"^\s*public\s+(?:(?:static|final|abstract|sealed|non-sealed|synchronized|default)\s+)*(?:(?:class|interface|enum|record)\s+\w+|[\w<>\[\],.? ]+\s+\w+\s*\(|\w+\s*\()")] private static partial Regex PublicDecl();
    [GeneratedRegex(@"^\s*(?:(?:public|protected|private|static|final|abstract|synchronized|default)\s+)*[\w<>\[\],.? ]+?\s+(\w+)\s*\([^;]*$")] private static partial Regex MethodDecl();
    [GeneratedRegex(@"^\s*(?:if|for|while|switch|catch|else|try|do|return|new|throw)\b|\bclass\b|\binterface\b|\benum\b|\brecord\b|=")] private static partial Regex ControlWord();
    [GeneratedRegex(@"\b(?:if|for|while|case|catch)\b|&&|\|\||\?(?!\s*(?:extends|super|>|,))")] private static partial Regex Decision();
}
