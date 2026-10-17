using System.Text.Json;
using System.Text.Json.Serialization;
using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

/// <summary>Development probe: prints a program's paragraphs and statement trees.</summary>
static class Probe
{
    public static int Run(string file, string? from, string? to)
    {
        var p = CobolModelBuilder.BuildGroundedFull(file);
        Console.WriteLine($"{p.ProgramId}: {p.Items.Count} items, {p.Paragraphs.Count} paragraphs, diags {p.Diagnostics.Count}");
        foreach (var d in p.Diagnostics.Take(5)) Console.WriteLine("  diag " + d);
        var opts = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        bool on = from is null;
        foreach (var para in p.Paragraphs)
        {
            if (para.Name == from) on = true;
            if (!on) continue;
            Console.WriteLine($"== {para.Name} (line {para.Line})");
            foreach (var s in para.Statements) Console.WriteLine("  " + JsonSerializer.Serialize(Strip(s), opts));
            if (para.Name == to) break;
        }
        return 0;
    }

    static object Strip(Stmt s) => new
    {
        s.Verb, s.Line, N = s.N, L = s.L, S = s.S, s.Frontier,
        B = s.Blocks?.ToDictionary(kv => kv.Key, kv => kv.Value.Select(Strip).ToList())
    };
}
