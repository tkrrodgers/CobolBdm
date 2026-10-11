using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MlCpu.DesignModel.Forward;

/// <summary>
/// The forward-engineering judge (tools/eval/PROTOCOL-forward.md section 6): a copy of forward/cas/service (the given API, the
/// architecture tests, the acceptance suite with public and hidden scenarios) with the candidate's files in the five
/// implementation modules. Each judgement compiles everything, runs the architecture tests and the acceptance scenarios of the
/// slices built so far, and returns the architecture failures in full, the public scenario failures in full, and the hidden
/// scenarios as counts only.
/// </summary>
public sealed class ForwardJudge
{
    public string Harness { get; }
    readonly HashSet<string> written = new(StringComparer.OrdinalIgnoreCase);

    public sealed record SliceCount(int PublicPassed, int PublicTotal, int HiddenPassed, int HiddenTotal);

    public sealed record Verdict(bool Compiled, IReadOnlyList<string> CompileErrors, string BuildOutput, int ArchPassed, int ArchTotal,
        IReadOnlyList<(string Test, string Message)> ArchFailures, IReadOnlyDictionary<string, SliceCount> Slices,
        IReadOnlyList<(string Scenario, string Message)> PublicFailures, bool TimedOut)
    {
        public bool ArchGreen => Compiled && ArchTotal > 0 && ArchPassed == ArchTotal;
        public int PublicPassed => Slices.Values.Sum(s => s.PublicPassed);
        public int PublicTotal => Slices.Values.Sum(s => s.PublicTotal);
        public int HiddenPassed => Slices.Values.Sum(s => s.HiddenPassed);
        public int HiddenTotal => Slices.Values.Sum(s => s.HiddenTotal);
        public bool AllPass => ArchGreen && !TimedOut && PublicTotal + HiddenTotal > 0 && PublicPassed == PublicTotal && HiddenPassed == HiddenTotal;
    }

    public ForwardJudge(ForwardInputs inputs, string harnessDir)
    {
        Harness = Path.GetFullPath(harnessDir);
        if (Directory.Exists(Harness)) Directory.Delete(Harness, true);
        CopyTree(Path.Combine(inputs.CasDir, "service"), Harness);
        // The SME's tools never reach the judge (they are not in service/, but a stale copy must not either).
        var sme = Path.Combine(Harness, "cas-acceptance", "src", "test", "java", "com", "mlcpu", "cas", "acceptance", "sme");
        if (Directory.Exists(sme)) Directory.Delete(sme, true);
        foreach (var (module, _) in ForwardInputs.Modules)
        {
            var src = Path.Combine(Harness, module, "src");
            if (Directory.Exists(src)) Directory.Delete(src, true);
            // Gradle configures every module of settings.gradle.kts, so each must exist even before it has code.
            Directory.CreateDirectory(Path.Combine(Harness, module));
        }
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var d in Directory.EnumerateDirectories(from))
        {
            var n = Path.GetFileName(d);
            if (n is "build" or ".gradle" or ".idea") continue;
            CopyTree(d, Path.Combine(to, n));
        }
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
    }

    static readonly Regex PackageDecl = new(@"^\s*package\s+([\w.]+)\s*;", RegexOptions.Multiline);

    /// <summary>The module a package belongs to (by root package), or null.</summary>
    public static string? ModuleOf(string package) =>
        ForwardInputs.Modules.Where(m => package == m.Package || package.StartsWith(m.Package + ".", StringComparison.Ordinal))
            .Select(m => m.Module).FirstOrDefault();

    public static string? PackageOf(string source) => PackageDecl.Match(source) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Relative path of a file in the harness: module/src/main/java/package/Type.java.</summary>
    public static string PathOf(string type, string source)
    {
        var pkg = PackageOf(source) ?? "";
        return $"{ModuleOf(pkg) ?? "unplaced"}/src/main/java/{pkg.Replace('.', '/')}/{type}.java";
    }

    /// <summary>Puts the files (type name → source) into their modules and removes the files of an earlier write that are gone.</summary>
    public void Write(IReadOnlyDictionary<string, string> files)
    {
        var now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (type, src) in files)
        {
            var rel = PathOf(type, src);
            if (rel.StartsWith("unplaced/", StringComparison.Ordinal)) continue;
            var full = Path.Combine(Harness, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, src);
            now.Add(full);
        }
        foreach (var old in written.Where(f => !now.Contains(f)).ToList()) if (File.Exists(old)) File.Delete(old);
        written.Clear();
        written.UnionWith(now);
    }

    public Verdict Judge(IEnumerable<string> slices, TimeSpan timeout)
    {
        var build = Gradle("compileJava compileTestJava", TimeSpan.FromMinutes(10));
        if (build.ExitCode != 0)
            return new Verdict(false, CompileErrors(build.Output), build.Output, 0, 0, [], new Dictionary<string, SliceCount>(), [], build.TimedOut);
        foreach (var m in new[] { "cas-architecture", "cas-acceptance" })
        {
            var results = Path.Combine(Harness, m, "build", "test-results", "test");
            if (Directory.Exists(results)) Directory.Delete(results, true);
        }
        var list = string.Join(",", slices);
        var test = Gradle($":cas-architecture:test :cas-acceptance:test --continue \"-PcasSlices={list}\"", timeout);
        var arch = Cases(Path.Combine(Harness, "cas-architecture", "build", "test-results", "test"));
        var scenarios = Cases(Path.Combine(Harness, "cas-acceptance", "build", "test-results", "test"));
        var counts = new SortedDictionary<string, SliceCount>(StringComparer.Ordinal);
        foreach (var s in slices) counts[s] = new SliceCount(0, 0, 0, 0);
        var publicFailures = new List<(string, string)>();
        foreach (var (name, failure) in scenarios)
        {
            var parts = name.Split(' ', 3);
            if (parts.Length < 3 || !counts.TryGetValue(parts[1], out var c)) continue;
            bool pub = parts[0] == "public", ok = failure is null;
            counts[parts[1]] = pub
                ? c with { PublicTotal = c.PublicTotal + 1, PublicPassed = c.PublicPassed + (ok ? 1 : 0) }
                : c with { HiddenTotal = c.HiddenTotal + 1, HiddenPassed = c.HiddenPassed + (ok ? 1 : 0) };
            if (pub && !ok) publicFailures.Add((name, failure!));
        }
        var archFailures = arch.Where(a => a.Failure is not null).Select(a => (a.Name, a.Failure!)).ToList();
        return new Verdict(true, [], test.Output, arch.Count - archFailures.Count, arch.Count, archFailures, counts, publicFailures, test.TimedOut);
    }

    static readonly Regex AssertionPrefix = new(@"^(?:org\.opentest4j\.AssertionFailedError|java\.lang\.AssertionError): ");

    static List<(string Name, string? Failure)> Cases(string dir)
    {
        var list = new List<(string, string?)>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.EnumerateFiles(dir, "TEST-*.xml"))
            foreach (var tc in XDocument.Load(f).Descendants("testcase"))
            {
                if (tc.Element("skipped") is not null) continue;
                var fail = tc.Element("failure") ?? tc.Element("error");
                list.Add((tc.Attribute("name")!.Value, fail is null ? null
                    : AssertionPrefix.Replace(fail.Attribute("message")?.Value is { Length: > 0 } msg ? msg : Clip(fail.Value, 3000), "")));
            }
        return list;
    }

    public sealed record GradleRun(int ExitCode, string Output, bool TimedOut);

    public GradleRun Gradle(string tasks, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c gradle {tasks} --offline --no-daemon --console=plain")
        {
            WorkingDirectory = Harness, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var output = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        bool done = p.WaitForExit(timeout);
        if (!done) { try { p.Kill(entireProcessTree: true); } catch { } p.WaitForExit(30000); }
        else p.WaitForExit();
        lock (output) return new GradleRun(done ? p.ExitCode : -1, output.ToString(), !done);
    }

    static readonly Regex ErrorLine = new(@"^(.*?\.java):(\d+): error: (.*)$");

    /// <summary>javac errors (each once), with the harness-relative path, the message and javac's context lines.</summary>
    public List<string> CompileErrors(string output)
    {
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Count; i++)
        {
            var m = ErrorLine.Match(lines[i]);
            if (!m.Success) continue;
            var rel = Relative(m.Groups[1].Value);
            var head = $"{rel}:{m.Groups[2].Value}: error: {m.Groups[3].Value}";
            if (!seen.Add(head)) continue;
            var e = new StringBuilder(head);
            for (int k = i + 1; k < lines.Count && k <= i + 5 && !ErrorLine.IsMatch(lines[k]) && !lines[k].StartsWith("> Task", StringComparison.Ordinal)
                                && !Regex.IsMatch(lines[k], @"^\d+ errors?$") && !lines[k].StartsWith("FAILURE", StringComparison.Ordinal); k++)
                e.Append('\n').Append(lines[k]);
            errors.Add(e.ToString());
        }
        return errors;
    }

    /// <summary>The path from the module directory on (cas-accounts/src/main/java/...), whatever form Gradle printed it in.</summary>
    static string Relative(string path)
    {
        var p = path.Replace('\\', '/');
        var m = Regex.Match(p, @"/(cas-[a-z]+/src/.*)$");
        return m.Success ? m.Groups[1].Value : p;
    }

    /// <summary>Numbered source lines around a line of a harness file.</summary>
    public string Excerpt(string relative, int line, int around)
    {
        var path = Path.Combine(Harness, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return "";
        var lines = File.ReadAllLines(path);
        var sb = new StringBuilder();
        for (int i = Math.Max(1, line - around); i <= Math.Min(lines.Length, line + around); i++)
            sb.Append(i == line ? ">" : " ").Append($"{i,5}: ").Append(lines[i - 1]).Append('\n');
        return sb.ToString();
    }

    public static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + " ...";
}
