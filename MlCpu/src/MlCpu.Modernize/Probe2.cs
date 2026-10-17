using System.Text.Json;
using System.Text.Json.Serialization;
using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

static class Probe2
{
    public static int Run(string dir)
    {
        var opts = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        var seen = new Dictionary<string, (string Example, int Count)>();
        foreach (var f in Directory.GetFiles(dir, "*.cbl").Order())
        {
            var p = CobolModelBuilder.BuildGroundedFull(f);
            void Walk(Stmt s)
            {
                var key = s.Verb + " N[" + string.Join(",", (s.N?.Keys ?? Enumerable.Empty<string>()).Order()) + "] L[" + string.Join(",", (s.L?.Keys ?? Enumerable.Empty<string>()).Order()) + "] S[" + string.Join(",", (s.S?.Keys ?? Enumerable.Empty<string>()).Order()) + "] B[" + string.Join(",", (s.Blocks?.Keys ?? Enumerable.Empty<string>()).Order()) + "]" + (s.Frontier is null ? "" : " FRONTIER");
                if (seen.TryGetValue(key, out var e)) seen[key] = (e.Example, e.Count + 1);
                else seen[key] = (Path.GetFileName(f) + ":" + s.Line + " " + JsonSerializer.Serialize(new { s.N, s.L, s.S, s.Frontier }, opts), 1);
                foreach (var b in s.Blocks?.Values ?? Enumerable.Empty<List<Stmt>>()) foreach (var c in b) Walk(c);
            }
            foreach (var para in p.Model_Paragraphs()) foreach (var s in para.Statements) Walk(s);
        }
        foreach (var (k, v) in seen.OrderBy(x => x.Key)) Console.WriteLine($"{v.Count,7} {k}\n        {v.Example[..Math.Min(700, v.Example.Length)]}");
        return 0;
    }
    static List<Paragraph> Model_Paragraphs(this CobolProgram p) => p.Paragraphs;
}
