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

    public static List<string> CopybookDirectories(string programPath)
    {
        var result = new List<string>();
        var progDir = Path.GetDirectoryName(Path.GetFullPath(programPath))!;
        result.Add(progDir);
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

    public static CobolProgram Build(string path, string raw, Func<string, string[]?>? resolver)
    {
        var prog = new CobolProgram { Path = path };
        var stripped = CobolParser.Strip(raw);
        var missing = new List<MissingCopybook>();
        var lines = CobolParser.ExpandCopies(stripped, resolver, missing);
        prog.MissingCopybooks = missing;
        prog.Copybooks = lines.Select(l => l.Origin).Where(o => o.Length > 0).Distinct().OrderBy(o => o, StringComparer.Ordinal).ToList();
        prog.ProgramId = CobolParser.FindProgramId(lines.Select(l => l.Text)) ?? Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
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
