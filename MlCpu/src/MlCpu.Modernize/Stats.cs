namespace MlCpu.Modernize;

static class Stats
{
    public static int Run(string project)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var estate = Estate.Load(project);
        Console.WriteLine($"estate {estate.Name}: {estate.Programs.Count} programs parsed in {sw.ElapsedMilliseconds} ms");
        var m = Model.Build(estate);
        Console.WriteLine($"runtime {m.RuntimePrograms.Count}: {string.Join(' ', m.RuntimePrograms.Order())}");
        Console.WriteLine($"job {string.Join(' ', m.JobPrograms.Order())}; drivers {string.Join(' ', m.DriverPrograms.Order())}");
        Console.WriteLine($"pools {m.Pools.Count}, statics {m.StaticsByItem.Count}, functions {m.Functions.Count}, entries {m.Entries.Values.Sum(e => e.Count)}, class ids {m.ClassById.Count}, exceptions {m.ExceptionNames.Count}");
        foreach (var k in Enum.GetValues<ClassKind>())
        {
            var cs = m.Classes.Values.Where(c => c.Kind == k).OrderBy(c => c.FullName).ToList();
            Console.WriteLine($"-- {k}: {cs.Count}");
            foreach (var c in cs)
                Console.WriteLine($"   {c.FullName} ids[{string.Join(',', c.Ids)}] pool {c.Pool?.Prefix} super {c.Super?.SimpleName} fields {c.Fields.Count} fns {c.Functions.Count} abbr {c.Abbr}");
        }
        var unbound = m.Pools.Values.Where(p => p.Ids.Count == 0).Select(p => p.Prefix);
        Console.WriteLine("pools without allocations: " + string.Join(' ', unbound));
        var idsNoClass = m.Pools.Values.SelectMany(p => p.Ids.Select(i => (p.Prefix, i))).Where(x => !m.ClassById.ContainsKey(x.i));
        Console.WriteLine("allocated ids without class: " + string.Join(' ', idsNoClass.Select(x => $"{x.Prefix}:{x.i}")));
        var noOwner = m.Functions.Values.Where(f => f.Owner is null).Select(f => f.Id + " " + f.Signature);
        Console.WriteLine("functions without owner: " + string.Join(" | ", noOwner.Take(40)));
        Console.WriteLine($"problems {m.Problems.Count}:");
        foreach (var p in m.Problems.Take(40)) Console.WriteLine("   " + p);
        Console.WriteLine($"done in {sw.ElapsedMilliseconds} ms");
        return 0;
    }
}
