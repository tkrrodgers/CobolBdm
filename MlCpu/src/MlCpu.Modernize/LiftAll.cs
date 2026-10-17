using System.Text.RegularExpressions;

namespace MlCpu.Modernize;

static class LiftAll
{
    public static int Run(string project, string outFile)
    {
        var estate = Estate.Load(project);
        var m = Model.Build(estate);
        var residue = new List<string>();
        var problems = new List<string>();
        int ok = 0, total = 0;
        var sb = new System.Text.StringBuilder();
        foreach (var fn in m.Functions.Values.OrderBy(f => f.Id, StringComparer.Ordinal))
        {
            if (fn.Owner?.Kind == ClassKind.Emulation) continue;
            total++;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Console.Error.Write($"{fn.Id} ");
            Lifted l;
            try { l = FunctionLifter.Lift(m, fn); }
            catch (Exception ex) { residue.Add($"{fn.Program.Id} {fn.Id} CRASH {ex.GetType().Name}: {ex.Message}"); continue; }
            var probs = new List<string>();
            List<St> body;
            try
            {
                body = Structurer.Run(l.Low, probs, fn.Id);
                Passes.FixLoops(body);
                body = Passes.Tidy(body);
                body = Fold.Expressions(body);
                body = Passes.Tidy(body);
            }
            catch (Exception ex) { residue.Add($"{fn.Program.Id} {fn.Id} STRUCTURE CRASH {ex.GetType().Name}: {ex.Message}"); continue; }
            if (sw.ElapsedMilliseconds > 2000) Console.Error.WriteLine($" SLOW {fn.Id} {sw.ElapsedMilliseconds} ms low {l.Low.Count}");
            residue.AddRange(l.Residue);
            problems.AddRange(probs.Select(p => fn.Program.Id + " " + p));
            if (l.Residue.Count == 0 && probs.Count == 0) ok++;
            sb.AppendLine($"==== {fn.Id} {fn.Signature} [{fn.Owner?.FullName}]");
            sb.Append(IrText.Show(body, 1));
        }
        File.WriteAllText(outFile, sb.ToString());
        Console.WriteLine($"functions {total}, clean {ok}, residue {residue.Count}, structure problems {problems.Count}");
        var groups = residue.Select(r => Regex.Replace(Regex.Replace(r, @"^\S+ M\d{4} |^\S+:\d+ M\d{4} ", ""), @"\d+", "#")).GroupBy(x => x).OrderByDescending(g => g.Count());
        foreach (var g in groups.Take(60)) Console.WriteLine($"{g.Count(),6} {g.Key}");
        foreach (var g in problems.Select(p => Regex.Replace(p, @"\d+", "#")).GroupBy(x => x).OrderByDescending(g => g.Count()).Take(15)) Console.WriteLine($"{g.Count(),6} PROBLEM {g.Key}");
        return 0;
    }
}
