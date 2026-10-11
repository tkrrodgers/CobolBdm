using System.Text.RegularExpressions;

namespace MlCpu.DesignModel;

/// <summary>
/// Repairs as edits (pipeline v2): after the first attempt a model may answer with SEARCH/REPLACE blocks instead of whole
/// files, so a one-line fix costs one line of output (fewer tokens, fewer syntax slips in long replies):
/// <code>
/// &lt;&lt;&lt;&lt;&lt;&lt;&lt; SEARCH Sam1Step.java
/// exact lines of the current file
/// =======
/// the lines that replace them
/// &gt;&gt;&gt;&gt;&gt;&gt;&gt; REPLACE
/// </code>
/// The SEARCH text must occur once in the file (exactly, or line by line ignoring indentation).
/// </summary>
public static class Edits
{
    public sealed record Edit(string File, string Search, string Replace);

    static readonly Regex Block = new(@"<<<<<<<[ \t]*SEARCH[ \t]*([^\r\n]*)\r?\n(.*?)\r?\n?=======[ \t]*\r?\n(.*?)\r?\n?>>>>>>>[ \t]*REPLACE[^\r\n]*",
        RegexOptions.Singleline);

    public const string Instructions = """
        Answer with edits where you change a few lines: one block per change, in this exact form (the file name, then lines copied
        exactly from the current file, then the lines that replace them):
        <<<<<<< SEARCH Name.java
        lines of the current file, exactly as they are
        =======
        the replacement lines
        >>>>>>> REPLACE
        The SEARCH lines must occur once in that file; include a few surrounding lines when a line is not unique. Return a whole
        file in a ```java block instead when you rewrite most of it or add a new class. Files you do not touch stay as they are.
        """;

    /// <summary>The edit blocks of a reply, and the reply without them (so fences that only held edits are dropped).</summary>
    public static (List<Edit> Edits, string Remainder) Extract(string reply)
    {
        var edits = new List<Edit>();
        var rest = Block.Replace(reply.Replace("\r\n", "\n"), m =>
        {
            var file = m.Groups[1].Value.Trim().Trim('`', '*', ':').Trim();
            edits.Add(new Edit(file, m.Groups[2].Value, m.Groups[3].Value));
            return "";
        });
        return (edits, rest);
    }

    /// <summary>Applies the edits to the files (keyed by type name); an edit that does not apply is reported, the file is left as it was.</summary>
    public static int Apply(IDictionary<string, string> files, IEnumerable<Edit> edits, List<string> problems)
    {
        int applied = 0, n = 0;
        foreach (var e in edits)
        {
            n++;
            var type = TypeName(e.File);
            string? key = type is not null && files.ContainsKey(type) ? type : null;
            if (key is null)
            {
                var holders = files.Where(f => f.Value.Contains(e.Search, StringComparison.Ordinal)).Select(f => f.Key).ToList();
                if (holders.Count == 1) key = holders[0];
            }
            if (key is null)
            {
                problems.Add($"edit {n} names {(e.File.Length == 0 ? "no file" : e.File)}, which is not one of your files ({string.Join(", ", files.Keys.Select(k => k + ".java"))})");
                continue;
            }
            var src = files[key];
            if (e.Search.Length == 0) { problems.Add($"edit {n} ({key}.java) has an empty SEARCH part"); continue; }
            int at = src.IndexOf(e.Search, StringComparison.Ordinal);
            if (at >= 0 && src.IndexOf(e.Search, at + 1, StringComparison.Ordinal) < 0)
            {
                files[key] = src[..at] + e.Replace + src[(at + e.Search.Length)..];
                applied++;
                continue;
            }
            if (at >= 0) { problems.Add($"edit {n} ({key}.java): the SEARCH lines occur more than once; include surrounding lines to make them unique"); continue; }
            // Line by line, ignoring indentation and trailing spaces.
            var lines = src.Split('\n');
            var want = e.Search.Split('\n').Select(l => l.Trim()).ToList();
            while (want.Count > 0 && want[^1].Length == 0) want.RemoveAt(want.Count - 1);
            var hits = new List<int>();
            for (int i = 0; i + want.Count <= lines.Length; i++)
                if (want.Select((w, k) => lines[i + k].Trim() == w).All(x => x)) hits.Add(i);
            if (hits.Count == 1)
            {
                var replaced = lines.Take(hits[0]).Concat(e.Replace.Split('\n')).Concat(lines.Skip(hits[0] + want.Count));
                files[key] = string.Join("\n", replaced);
                applied++;
                continue;
            }
            problems.Add(hits.Count == 0
                ? $"edit {n} ({key}.java): the SEARCH lines were not found; copy them exactly from the current file shown above"
                : $"edit {n} ({key}.java): the SEARCH lines occur {hits.Count} times; include surrounding lines to make them unique");
        }
        return applied;
    }

    static string? TypeName(string file)
    {
        if (file.Length == 0) return null;
        var name = file.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        if (name.EndsWith(".java", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
        return name.Length == 0 ? null : name;
    }
}
