using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

/// <summary>One program of the estate: the parsed model plus the comment lines the parser drops.</summary>
public sealed class SourceProgram
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public required CobolProgram Model { get; init; }
    /// <summary>Comment text by source line (columns 8-72, the leading '*' removed).</summary>
    public required SortedDictionary<int, string> Comments { get; init; }
    public Dictionary<int, DataItem> ItemsById { get; } = [];
    public Dictionary<string, DataItem> ItemsByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ParagraphIndex { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Index()
    {
        foreach (var it in Model.Items)
        {
            ItemsById[it.Id] = it;
            if (!it.IsFiller) ItemsByName.TryAdd(it.Name, it);
        }
        for (int i = 0; i < Model.Paragraphs.Count; i++) ParagraphIndex.TryAdd(Model.Paragraphs[i].Name, i);
    }

    /// <summary>The comment block that ends right before <paramref name="line"/> (contiguous comment lines), joined.</summary>
    public List<string> CommentBlockBefore(int line)
    {
        var lines = new List<string>();
        int l = line - 1;
        while (Comments.TryGetValue(l, out var c)) { lines.Insert(0, c); l--; }
        return lines;
    }
}

/// <summary>An estate: the programs of a CobolBdm project (or a source tree), parsed once.</summary>
public sealed class Estate
{
    public required string Name { get; init; }
    public required string SourceDir { get; init; }
    public List<SourceProgram> Programs { get; } = [];
    public Dictionary<string, SourceProgram> ById { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> CopybookDirs { get; } = [];
    public List<string> JclFiles { get; } = [];
    /// <summary>The legacy control flow structured before the lift (LegacyFlow), for modernize/legacy-flow.json.</summary>
    public List<LegacyFlow.Entry> FlowLedger { get; } = [];

    static readonly Encoding Latin1 = Encoding.Latin1;

    public static Estate Load(string projectOrSource, string? name = null)
    {
        string srcDir;
        List<string> files;
        var roots = new List<string>();
        var jcl = new List<string>();
        string estateName;
        var proj = Path.Combine(projectOrSource, "project.json");
        if (File.Exists(proj))
        {
            var j = JsonNode.Parse(File.ReadAllText(proj))!;
            srcDir = Path.GetFullPath(Path.Combine(projectOrSource, j["layout"]?["sourceDir"]?.GetValue<string>() ?? "source"));
            foreach (var r in j["layout"]?["copybookRoots"]?.AsArray() ?? []) roots.Add(Path.GetFullPath(Path.Combine(srcDir, r!.GetValue<string>())));
            foreach (var r in j["layout"]?["jclRoots"]?.AsArray() ?? [])
            {
                var d = Path.GetFullPath(Path.Combine(srcDir, r!.GetValue<string>()));
                if (Directory.Exists(d)) jcl.AddRange(Directory.GetFiles(d).Order(StringComparer.Ordinal));
            }
            files = j["programs"]!.AsArray().Select(p => Path.GetFullPath(Path.Combine(srcDir, p!["path"]!.GetValue<string>()))).ToList();
            estateName = name ?? j["id"]?.GetValue<string>() ?? Path.GetFileName(projectOrSource);
        }
        else
        {
            srcDir = Path.GetFullPath(projectOrSource);
            files = Directory.GetFiles(srcDir, "*.cbl", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
            roots.AddRange(Directory.GetDirectories(srcDir, "cpy", SearchOption.AllDirectories));
            estateName = name ?? Path.GetFileName(srcDir);
        }
        CobolModelBuilder.ConfigureCopybookRoots(srcDir, roots);
        var estate = new Estate { Name = estateName, SourceDir = srcDir };
        estate.CopybookDirs.AddRange(roots);
        estate.JclFiles.AddRange(jcl);
        var parsed = new ConcurrentDictionary<string, SourceProgram>();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, f =>
        {
            var model = CobolModelBuilder.BuildGroundedFull(f);
            var sp = new SourceProgram { Id = model.ProgramId, Path = f, Model = model, Comments = ReadComments(f) };
            sp.Index();
            parsed[f] = sp;
        });
        foreach (var f in files)
        {
            var sp = parsed[f];
            estate.Programs.Add(sp);
            estate.ById[sp.Id] = sp;
        }
        LegacyFlow.Run(estate);
        return estate;
    }

    /// <summary>Fixed-format comment lines (indicator '*' or '/' in column 7), keyed by 1-based line number.</summary>
    static SortedDictionary<int, string> ReadComments(string path)
    {
        var map = new SortedDictionary<int, string>();
        int n = 0;
        foreach (var raw in File.ReadLines(path, Latin1))
        {
            n++;
            if (raw.Length < 7) continue;
            if (raw[6] != '*' && raw[6] != '/') continue;
            var text = raw.Length > 7 ? raw[7..Math.Min(raw.Length, 72)] : "";
            map[n] = text.StartsWith(' ') ? text[1..] : text;
        }
        return map;
    }
}

/// <summary>J3S comment conventions (fineract-cobol-gen docs/J3S.md §6, §15): program header, frames, functions, entries.</summary>
public static partial class J3sComments
{
    [GeneratedRegex(@"^CLASS\s*:\s*(\S+)\s*$")] private static partial Regex ClassRx();
    [GeneratedRegex(@"^FRAME OF (M\d{4})-[A-Z0-9-]+ (.*)$")] private static partial Regex FrameRx();
    [GeneratedRegex(@"^SRC ([A-Z0-9_]+):(\d+)-(\d+) (.*)$")] private static partial Regex SrcRx();
    [GeneratedRegex(@"^ENTRY (\d+): (.*)$")] private static partial Regex EntryRx();

    /// <summary>The program header's class (the CLASS lines concatenated), or null.</summary>
    public static string? HeaderClass(SourceProgram p)
    {
        var sb = new StringBuilder();
        foreach (var (line, text) in p.Comments)
        {
            if (line > 20) break;
            var m = ClassRx().Match(text.Trim());
            if (m.Success) sb.Append(m.Groups[1].Value);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>Joins wrapped comment lines that continue a header (the next line is not a new keyword).</summary>
    static IEnumerable<(int Line, string Text)> Logical(SourceProgram p)
    {
        int? startLine = null;
        var sb = new StringBuilder();
        int prev = -10;
        foreach (var (line, text) in p.Comments)
        {
            bool starts = text.StartsWith("FRAME OF ") || text.StartsWith("SRC ") || text.StartsWith("ENTRY ") || text.StartsWith("CLASS") || text.StartsWith("----") || text.StartsWith("====") || text.StartsWith("PROGRAM") || text.StartsWith("FUNCTIONS") || text.StartsWith("J3S ") || text.StartsWith("SHARED") || text.StartsWith("REFERENCE") || text.StartsWith("RECURSIVE") || text.StartsWith("BY GO TO") || text.StartsWith("job ") || text.StartsWith("RUNTIME") || text.StartsWith("JOB ");
            if (startLine is not null && (starts || line != prev + 1))
            {
                yield return (startLine.Value, sb.ToString());
                startLine = null;
                sb.Clear();
            }
            if (startLine is null) { startLine = line; sb.Append(text.TrimEnd()); }
            else sb.Append(text.TrimEnd());
            prev = line;
        }
        if (startLine is not null) yield return (startLine.Value, sb.ToString());
    }

    public sealed record FrameNote(string Fn, string Signature, int Line);
    public sealed record SrcNote(string Abbr, int From, int To, string Signature, int Line);
    public sealed record EntryNote(int No, string Signature, int Line);

    public static (List<FrameNote> Frames, List<SrcNote> Srcs, List<EntryNote> Entries) Notes(SourceProgram p)
    {
        var frames = new List<FrameNote>();
        var srcs = new List<SrcNote>();
        var entries = new List<EntryNote>();
        foreach (var (line, text) in Logical(p))
        {
            var t = text.Trim();
            var m = FrameRx().Match(t);
            if (m.Success) { frames.Add(new FrameNote(m.Groups[1].Value, m.Groups[2].Value.Trim(), line)); continue; }
            m = SrcRx().Match(t);
            if (m.Success) { srcs.Add(new SrcNote(m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), m.Groups[4].Value.Trim(), line)); continue; }
            m = EntryRx().Match(t);
            if (m.Success) entries.Add(new EntryNote(int.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim(), line));
        }
        return (frames, srcs, entries);
    }
}
