namespace MlCpu.Modernize;

/// <summary>
/// The methods of the application layer's program classes: the classic paragraphs (ClassicLift) and the J3S job
/// functions (lifted with the domain code), a job function's reject paragraph becoming the handler of a try around it
/// (JX-C an instanceof of the exception, JX-SITE its legacy site from the source map).
/// </summary>
static class AppBuild
{
    public static (Dictionary<AppProgram, List<AppMethod>> Methods, Dictionary<AppProgram, List<FieldDef>> JobObjects, List<string> Residue)
        Run(AppModel app, Dictionary<FunctionDef, Method> lifted)
    {
        var methods = new Dictionary<AppProgram, List<AppMethod>>();
        var objects = new Dictionary<AppProgram, List<FieldDef>>();
        var residue = new List<string>();
        foreach (var ap in app.Programs)
        {
            var cl = new ClassicLift(app, ap);
            var list = new List<AppMethod>();
            foreach (var para in ap.Classic)
            {
                int temps = cl.Temps.Count;
                var body = cl.Paragraph(para);
                if (body.Count == 0 && para != ap.Classic[0]) continue;
                var fn = new FunctionDef { Id = ap.P.Id + "/" + para.Name, Program = ap.P, Paragraph = para.Name, MethodName = ap.MethodNames[para.Name] };
                var l = new Lifted { Fn = fn };
                l.Vars.AddRange(cl.Temps.Skip(temps));
                var m = new Method { Fn = fn, L = l, Body = Passes.Tidy(body) };
                list.Add(new AppMethod($"private void {fn.MethodName}", m, para.Name));
            }
            // the reject mapping (EVENT-REJECT): a method of the exception and its legacy site
            foreach (var f in ap.RejectFunctions)
            {
                var e = new Var { Name = "e", Type = JType.Of("java.lang.Throwable") };
                var site = new Var { Name = "site", Type = JType.String };
                cl.Exception = e;
                cl.Site = site;
                var body = cl.Paragraph(ap.P.Model.Paragraphs[f.FirstParagraph]);
                cl.Exception = null;
                cl.Site = null;
                var fn = new FunctionDef { Id = ap.P.Id + "/" + f.Paragraph, Program = ap.P, Paragraph = f.Paragraph, MethodName = ap.MethodNames[f.Paragraph] };
                var l = new Lifted { Fn = fn };
                l.Params.Add(e);
                l.Params.Add(site);
                list.Add(new AppMethod($"private void {fn.MethodName}", new Method { Fn = fn, L = l, Body = Passes.Tidy(body) }, $"{f.MethodName.ToLowerInvariant()} (job function {f.Id}): the reject record of an exception"));
            }
            foreach (var f in ap.JobFunctions.Where(f => !ap.RejectFunctions.Contains(f)))
            {
                if (!lifted.TryGetValue(f, out var jm)) { residue.Add($"{ap.P.Id} {f.Id} job function not lifted"); continue; }
                var reject = ap.P.Model.Paragraphs.FirstOrDefault(p => p.Name.Equals("F" + f.Id[1..] + "-REJECT", StringComparison.OrdinalIgnoreCase));
                if (reject is not null)
                {
                    var e = new Var { Name = "e", Type = JType.Of("RuntimeException | LinkageError") };
                    var site = new Var { Name = "site", Type = JType.String };
                    cl.Exception = e;
                    cl.Site = site;
                    var handler = new List<St> { new SAssign(new EVar(site), new ECall { Kind = CallKind.JdkStatic, Owner = app.PlatformPackage + ".LegacySites", Name = "site", Args = [new EVar(e)], Type = JType.String }) };
                    handler.AddRange(cl.Paragraph(reject));
                    cl.Exception = null;
                    cl.Site = null;
                    var tr = new STry(jm.Body);
                    tr.Catches.Add((e.Type, e, handler));
                    jm.Body = [tr];
                    jm.L.Vars.Add(site);
                }
                list.Add(new AppMethod($"private void {ap.MethodNames[f.Paragraph]}", jm, $"{f.MethodName.ToLowerInvariant()} (job function {f.Id})"));
                // the job objects it keeps between calls
                foreach (var st in Passes.All(jm.Body))
                    foreach (var x in Fold.Exprs(st).SelectMany(Deep))
                        if (x is EStatic { F: { Group: "SJOB" } sf } && !(objects.TryGetValue(ap, out var ol) && ol.Contains(sf)))
                            (objects.TryGetValue(ap, out var ol2) ? ol2 : objects[ap] = []).Add(sf);
                // record fields read or written as dates: the record's date views
                foreach (var st in Passes.All(jm.Body))
                {
                    foreach (var x in Fold.Exprs(st).SelectMany(Deep))
                        if (x is ERecordField { Type.Name: "java.time.LocalDate" } rf && app.RecordOf(ap, rf.Item) is { } r) r.Rc.DateViews.Add(rf.Item.Name);
                    if (st is SAssign { Lhs: ERecordField lrf } && lrf.Type?.Name == "java.time.LocalDate" && app.RecordOf(ap, lrf.Item) is { } r2) r2.Rc.DateViews.Add(lrf.Item.Name);
                }
            }
            residue.AddRange(cl.Residue);
            methods[ap] = list;
        }
        return (methods, objects, residue);
    }

    static IEnumerable<Expr> Deep(Expr e)
    {
        yield return e;
        foreach (var c in e.Children)
            foreach (var d in Deep(c)) yield return d;
    }
}
