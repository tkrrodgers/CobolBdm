using System.Text;
using System.Text.RegularExpressions;
using MlCpu.CobolKb;

namespace MlCpu.Cobol.Analysis;

/// <summary>Builds the analysed program from a source path (§5.4.2). Copybooks resolve by walking up to the first library level.</summary>
public static partial class CobolModelBuilder
{
    static readonly Encoding Latin1 = Encoding.Latin1;

    static readonly string[] CopyDirNames = ["copybooks", "copybook", "copylib", "cpy", "copy", "include", "maclib", "dcl", "dclgen"];
    static readonly string[] CopyExts = ["", ".cpy", ".CPY", ".cpb", ".copy", ".cbl", ".CBL", ".txt"];

    [GeneratedRegex(@"^\s*ENVIRONMENT\s+DIVISION", RegexOptions.IgnoreCase)] private static partial Regex EnvRx();
    /// <summary>PROGRAM-ID. name [IS] {INITIAL | RECURSIVE | COMMON}... [PROGRAM].</summary>
    [GeneratedRegex(@"^\s*PROGRAM-ID\.?\s+['""]?[A-Z0-9][A-Z0-9-]*['""]?((?:\s+(?:IS|INITIAL|RECURSIVE|COMMON|PROGRAM))*)", RegexOptions.IgnoreCase)]
    private static partial Regex ProgramIdClauseRx();
    [GeneratedRegex(@"^\s*DATA\s+DIVISION", RegexOptions.IgnoreCase)] private static partial Regex DataRx();
    [GeneratedRegex(@"^\s*PROCEDURE\s+DIVISION", RegexOptions.IgnoreCase)] private static partial Regex ProcRx();

    /// <summary>The source to analyse: a sibling .preprocessed/.expanded file wins over the raw member.</summary>
    public static string PreferExpanded(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(path);
        foreach (var alt in new[] { $"{stem}.preprocessed.CBL", $"{stem}.expanded.CBL" })
        {
            var p = Path.Combine(dir, alt);
            if (File.Exists(p)) return p;
        }
        return path;
    }

    public static Func<string, string[]?> CopybookResolver(string programPath)
    {
        var dirs = CopybookDirectories(programPath);
        var cache = new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);
        return member =>
        {
            if (cache.TryGetValue(member, out var hit)) return hit;
            string[]? found = null;
            foreach (var d in dirs)
            {
                foreach (var ext in CopyExts)
                {
                    var p = Path.Combine(d, member + ext);
                    if (!File.Exists(p)) continue;
                    var raw = File.ReadAllText(p, Latin1);
                    found = CobolParser.Strip(raw);
                    break;
                }
                if (found is not null) break;
            }
            cache[member] = found;
            return found;
        };
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<string>> ConfiguredRoots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Copybook roots configured for a source tree (a project's <c>layout.copybookRoots</c>): for every program under
    /// <paramref name="sourceTree"/> they are searched, in order, after the program's own directory and before the
    /// directory-name heuristics. An empty list removes the configuration.
    /// </summary>
    public static void ConfigureCopybookRoots(string sourceTree, IEnumerable<string> roots)
    {
        var tree = Path.GetFullPath(sourceTree).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var list = roots.Select(r => Path.GetFullPath(Path.IsPathRooted(r) ? r : Path.Combine(tree, r))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) ConfiguredRoots.TryRemove(tree, out _);
        else ConfiguredRoots[tree] = list;
    }

    /// <summary>The configured roots of the innermost configured source tree holding <paramref name="dir"/>.</summary>
    static IReadOnlyList<string> ConfiguredFor(string dir)
    {
        IReadOnlyList<string> best = [];
        int bestLen = -1;
        foreach (var (tree, roots) in ConfiguredRoots)
            if (tree.Length > bestLen && (string.Equals(dir, tree, StringComparison.OrdinalIgnoreCase)
                || dir.StartsWith(tree + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                best = roots;
                bestLen = tree.Length;
            }
        return best;
    }

    public static List<string> CopybookDirectories(string programPath)
    {
        var result = new List<string>();
        var progDir = Path.GetDirectoryName(Path.GetFullPath(programPath))!;
        result.Add(progDir);
        result.AddRange(ConfiguredFor(progDir));
        for (var dir = new DirectoryInfo(progDir); dir is not null; dir = dir.Parent)
        {
            var libs = dir.EnumerateDirectories()
                .Where(d => CopyDirNames.Contains(d.Name, StringComparer.OrdinalIgnoreCase)
                    || d.Name.StartsWith("cpy", StringComparison.OrdinalIgnoreCase)
                    || d.Name.StartsWith("copy", StringComparison.OrdinalIgnoreCase)
                    || d.Name.StartsWith("dcl", StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .Select(d => d.FullName).ToList();
            if (libs.Count > 0) { result.AddRange(libs); break; }
        }
        var extra = Environment.GetEnvironmentVariable("MLCPU_COPYBOOKS");
        if (!string.IsNullOrEmpty(extra)) result.AddRange(extra.Split(';', StringSplitOptions.RemoveEmptyEntries));
        var vendor = Environment.GetEnvironmentVariable("MLCPU_VENDOR_COPYBOOKS") ?? Environment.GetEnvironmentVariable("MLCPU_SYNTHETIC_COPYBOOKS");
        if (!string.IsNullOrEmpty(vendor)) result.AddRange(vendor.Split(';', StringSplitOptions.RemoveEmptyEntries));
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static CobolProgram BuildGroundedFull(string path)
    {
        var source = PreferExpanded(path);
        var raw = File.ReadAllText(source, Latin1);
        return Build(path, raw, CopybookResolver(path));
    }

    /// <summary>Every program of a source, separate or nested, in source order (the first is the one <see cref="BuildGroundedFull"/> returns).</summary>
    public static List<CobolProgram> BuildAllGroundedFull(string path)
    {
        var raw = File.ReadAllText(PreferExpanded(path), Latin1);
        var problems = new List<string>();
        var missing = new List<MissingCopybook>();
        var lines = CobolParser.ExpandCopies(CobolParser.Strip(raw), CopybookResolver(path), missing, problems);
        var units = ProgramUnits(lines);
        return units.Select((u, i) => BuildUnit(path, lines, u, i == 0 ? missing : [], problems)).ToList();
    }

    public static CobolProgram Build(string path, string raw, Func<string, string[]?>? resolver)
    {
        var problems = new List<string>();
        var missing = new List<MissingCopybook>();
        var lines = CobolParser.ExpandCopies(CobolParser.Strip(raw), resolver, missing, problems);
        var units = ProgramUnits(lines);
        return BuildUnit(path, lines, units.Count > 0 ? units[0] : (0, lines.Count, null), missing, problems);
    }

    [GeneratedRegex(@"^\s*(?:IDENTIFICATION|ID)\s+DIVISION", RegexOptions.IgnoreCase)] private static partial Regex IdDivisionRx();
    [GeneratedRegex(@"^\s*PROGRAM-ID\b", RegexOptions.IgnoreCase)] private static partial Regex ProgramIdLineRx();
    [GeneratedRegex(@"^\s*END\s+PROGRAM\b", RegexOptions.IgnoreCase)] private static partial Regex EndProgramRx();

    /// <summary>
    /// The programs of a source as line ranges: each starts at its IDENTIFICATION DIVISION (or PROGRAM-ID) and ends
    /// before the next program's start or at its END PROGRAM, so a nested program's text is never parsed as its
    /// parent's statements. Parent is the containing program's index for a nested program.
    /// </summary>
    static List<(int Start, int End, int? Parent)> ProgramUnits(List<SourceLine> lines)
    {
        var starts = new List<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (!ProgramIdLineRx().IsMatch(lines[i].Text)) continue;
            int s = i;
            for (int k = i - 1; k >= 0; k--)
            {
                if (lines[k].Text.Trim().Length == 0) continue;
                if (IdDivisionRx().IsMatch(lines[k].Text)) s = k;
                break;
            }
            starts.Add(s);
        }
        var units = new List<(int Start, int End, int? Parent)>();
        if (starts.Count == 0) { units.Add((0, lines.Count, null)); return units; }
        // Nesting: a program starting before its predecessor's END PROGRAM is contained in it.
        var open = new Stack<int>();
        int u = 0, next = 0;
        var ends = new int[starts.Count];
        var parents = new int?[starts.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            if (next < starts.Count && i == starts[next])
            {
                if (open.Count > 0 && ends[open.Peek()] == 0) ends[open.Peek()] = i;
                parents[next] = open.Count > 0 ? open.Peek() : null;
                open.Push(next);
                next++;
                continue;
            }
            if (EndProgramRx().IsMatch(lines[i].Text) && open.Count > 0)
            {
                var done = open.Pop();
                if (ends[done] == 0) ends[done] = i;
            }
        }
        while (open.Count > 0) { var done = open.Pop(); if (ends[done] == 0) ends[done] = lines.Count; }
        for (u = 0; u < starts.Count; u++) units.Add((starts[u], ends[u] == 0 ? lines.Count : ends[u], parents[u]));
        return units;
    }

    static CobolProgram BuildUnit(string path, List<SourceLine> all, (int Start, int End, int? Parent) unit, List<MissingCopybook> missing, List<string> problems)
    {
        var prog = new CobolProgram { Path = path };
        prog.Diagnostics.AddRange(problems);
        // Lines outside the unit are blanked, so line numbers stay those of the source.
        var lines = all.Select((l, i) => i >= unit.Start && i < unit.End ? l : l with { Text = "" }).ToList();
        prog.MissingCopybooks = missing;
        prog.Copybooks = all.Skip(unit.Start).Take(unit.End - unit.Start).Select(l => l.Origin).Where(o => o.Length > 0).Distinct().OrderBy(o => o, StringComparer.Ordinal).ToList();
        prog.ProgramId = CobolParser.FindProgramId(lines.Select(l => l.Text)) ?? Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
        if (lines.Select(l => ProgramIdClauseRx().Match(l.Text)).FirstOrDefault(m => m.Success) is { } pm)
        {
            prog.IsInitial = pm.Groups[1].Value.Contains("INITIAL", StringComparison.OrdinalIgnoreCase);
            prog.IsRecursive = pm.Groups[1].Value.Contains("RECURSIVE", StringComparison.OrdinalIgnoreCase);
        }
        foreach (var m in missing) prog.Diagnostics.Add($"L{m.ProgramLine}: missing copybook {m.Member}");

        int env = lines.FindIndex(l => EnvRx().IsMatch(l.Text));
        int data = lines.FindIndex(l => DataRx().IsMatch(l.Text));
        int proc = lines.FindIndex(l => ProcRx().IsMatch(l.Text));
        if (proc < 0) { prog.Diagnostics.Add("no PROCEDURE DIVISION"); proc = lines.Count; }

        var files = new List<FileDecl>();
        if (env >= 0)
        {
            var envEnd = data >= 0 ? data : proc;
            files = DataDivisionParser.ParseFileControl(DataDivisionParser.Sentences(lines.Skip(env + 1).Take(envEnd - env - 1)));
        }
        var items = new List<DataItem>();
        if (data >= 0)
            items = DataDivisionParser.ParseData(DataDivisionParser.Sentences(lines.Skip(data + 1).Take(proc - data - 1)), files, prog.Diagnostics);
        prog.Files = files;
        prog.Items = items;

        if (proc < lines.Count)
        {
            var tokens = Lexer.Tokenize(lines.Skip(proc).ToList());
            prog.ExecSql = tokens.Any(t => t.Is("EXEC") ) && tokens.Any(t => t.Is("SQL"));
            prog.ExecCics = tokens.Any(t => t.Is("CICS"));
            var index = new DataIndex(items, files);
            var parser = new ProcedureParser(tokens, index, prog.Diagnostics);
            prog.Paragraphs = parser.Parse(out var usingNames);
            prog.Using = usingNames;
            prog.Items = index.Items;
        }
        return prog;
    }
}
