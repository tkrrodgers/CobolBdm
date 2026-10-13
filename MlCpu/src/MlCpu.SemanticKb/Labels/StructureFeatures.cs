using System.Text.RegularExpressions;

namespace MlCpu.SemanticKb.Labels;

/// <summary>
/// The statement composition of a code chunk, for the <c>role</c> classifier: the pipeline assigns a paragraph's role from
/// what its statements do (sequence others, compute, move, do I/O, report errors), which an embedding sees only
/// indirectly. Verbs come from the COBOL lines of a paragraph, the "L&lt;n&gt; VERB" lines of a rule, or the
/// "// [PARA:n:VERB:Ln]" comments of the faithful Java. Chunks without COBOL statements (idiomatic Java) get zeros.
/// </summary>
public static partial class StructureFeatures
{
    static readonly string[][] Groups =
    [
        ["PERFORM", "GO"], ["MOVE", "SET", "INITIALIZE"], ["IF", "EVALUATE", "WHEN"], ["COMPUTE", "ADD", "SUBTRACT", "MULTIPLY", "DIVIDE"],
        ["READ", "START"], ["WRITE", "REWRITE", "DELETE"], ["OPEN", "CLOSE"], ["DISPLAY"], ["CALL"], ["ACCEPT"],
        ["STRING", "UNSTRING", "INSPECT"], ["GOBACK", "STOP", "EXIT", "CONTINUE"]
    ];

    /// <summary>12 verb-group shares, RETURN-CODE and ABEND mentions per statement, log statement count, has-statements.</summary>
    public const int Length = 16;

    public static float[] Compute(KbChunk c)
    {
        var verbs = Verbs(c).ToList();
        var f = new float[Length];
        if (verbs.Count == 0) return f;
        for (int g = 0; g < Groups.Length; g++) f[g] = (float)verbs.Count(v => Groups[g].Contains(v)) / verbs.Count;
        f[12] = Math.Min(1, (float)Count(c.Text, "RETURN-CODE") / verbs.Count);
        f[13] = Math.Min(1, (float)Count(c.Text, "ABEND") / verbs.Count);
        f[14] = (float)(Math.Log(1 + verbs.Count) / 5);
        f[15] = 1;
        return f;
    }

    static IEnumerable<string> Verbs(KbChunk c)
    {
        if (c.Kind == "java")
        {
            foreach (Match m in JavaStep().Matches(c.Text)) yield return m.Groups[1].Value.Split(' ')[0];
            yield break;
        }
        if (c.Kind is not ("paragraph" or "rule")) yield break;
        foreach (var raw in c.Text.Split('\n'))
        {
            var line = RuleLine().Replace(raw, "").TrimStart();
            var sp = line.IndexOf(' ');
            var w = sp < 0 ? line : line[..sp];
            if (Text.Outline.VerbSet.Contains(w)) yield return w;
        }
    }

    static int Count(string s, string what)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i += what.Length; }
        return n;
    }

    [GeneratedRegex(@"// \[[^\]:]+:\d+:([A-Z][A-Z -]*):L\d+\]")] private static partial Regex JavaStep();
    [GeneratedRegex(@"^\s*L\d+\s")] private static partial Regex RuleLine();
}
