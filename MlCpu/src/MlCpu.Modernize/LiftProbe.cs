namespace MlCpu.Modernize;

static class LiftProbe
{
    public static int Run(string project, string[] fns)
    {
        var estate = Estate.Load(project);
        var m = Model.Build(estate);
        foreach (var id in fns)
        {
            if (!m.Functions.TryGetValue(id, out var fn)) { Console.WriteLine("no " + id); continue; }
            var l = FunctionLifter.Lift(m, fn);
            Console.WriteLine($"==== {fn.Id} {fn.Signature} owner {fn.Owner?.FullName}");
            if (Environment.GetEnvironmentVariable("SHOW_LOW") == "1") { Console.WriteLine("-- low"); Console.Write(IrText.Show(l.Low, 1)); }
            var probs = new List<string>();
            var body = Structurer.Run(l.Low, probs, fn.Id);
            Passes.FixLoops(body);
            body = Passes.Tidy(body);
            body = Fold.Expressions(body);
            body = Passes.Tidy(body);
            body = new Rewrite(m).Body(body);
            Console.WriteLine("-- folded");
            Console.Write(IrText.Show(body, 1));
            body = Modernizer.LiftOne(m, fn, null)!.Body;
            Console.WriteLine("-- final");
            Console.Write(IrText.Show(body, 1));
            foreach (var r in l.Residue) Console.WriteLine("RESIDUE " + r);
            foreach (var p in probs) Console.WriteLine("PROBLEM " + p);
        }
        return 0;
    }
}
