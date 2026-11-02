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
                bool entry = para == ap.Classic[0];
                cl.InEntry = entry;
                var body = cl.Paragraph(para);
                cl.InEntry = false;
                if (body.Count == 0 && !entry) continue;
                var fn = new FunctionDef { Id = ap.P.Id + "/" + para.Name, Program = ap.P, Paragraph = para.Name, MethodName = ap.MethodNames[para.Name] };
                var l = new Lifted { Fn = fn };
                l.Vars.AddRange(cl.Temps.Skip(temps));
                // a subprogram's paragraph takes the caller's records it reads or writes; its entry is the CALL
                if (ap.IsSubprogram)
                {
                    l.Params.AddRange(ap.ParagraphLinkage.GetValueOrDefault(para.Name, []).Select(r => ap.LinkageVars[r]));
                    if (entry && para.Statements.LastOrDefault()?.Verb is not ("GOBACK" or "STOP RUN" or "EXIT PROGRAM") && ap.Classic.Count > 1)
                        residue.Add($"{ap.P.Id}:{para.Line} the entry paragraph {para.Name} falls through into the next");
                }
                var m = new Method { Fn = fn, L = l, Body = Passes.Tidy(body) };
                list.Add(new AppMethod(ap.IsSubprogram && entry ? "public void call" : $"private void {fn.MethodName}", m, para.Name));
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
            int classic = list.Count;
            var writes = new List<(Method M, SAssign S)>();
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
                {
                    foreach (var x in Fold.Exprs(st).SelectMany(Deep))
                        if (x is EStatic { F: { Group: "SJOB" } sf } && !(objects.TryGetValue(ap, out var ol) && ol.Contains(sf)))
                            (objects.TryGetValue(ap, out var ol2) ? ol2 : objects[ap] = []).Add(sf);
                    if (st is SAssign { Lhs: EStatic { F: { Group: "SJOB" } } } wst) writes.Add((jm, wst));
                }
                // record fields read or written as dates: the record's date views
                foreach (var st in Passes.All(jm.Body))
                {
                    foreach (var x in Fold.Exprs(st).SelectMany(Deep))
                        if (x is ERecordField { Type.Name: "java.time.LocalDate" } rf && app.RecordOf(ap, rf.Item) is { } r) r.Rc.DateViews.Add(rf.Item.Name);
                    if (st is SAssign { Lhs: ERecordField lrf } && lrf.Type?.Name == "java.time.LocalDate" && app.RecordOf(ap, lrf.Item) is { } r2) r2.Rc.DateViews.Add(lrf.Item.Name);
                }
            }
            // a job object no job function reads (a run object of the spec the events never use): a store of a new JDK object
            // has no effect and goes; any other store keeps a field for it
            foreach (var group in writes.Where(w => !(objects.TryGetValue(ap, out var ro) && ro.Contains(((EStatic)w.S.Lhs).F))).GroupBy(w => ((EStatic)w.S.Lhs).F))
            {
                if (group.All(w => w.S.Rhs is ENew { JdkClass: not null, Args.Count: 0 }))
                    foreach (var (wm, ws) in group) Remove(wm.Body, ws);
                else (objects.TryGetValue(ap, out var ow) ? ow : objects[ap] = []).Add(group.Key);
            }
            residue.AddRange(cl.Residue);
            // a performed paragraph's GOBACK ends the CALL: the entry method catches its CallEnded (the CALL returns)
            if (ap.IsSubprogram && ap.EndsCallInParagraph && list.Count > 0 && list[0].Head == "public void call")
            {
                var ended = new Var { Name = "ended", Type = JType.Of(ClassicLift.CallEndedClass) };
                var inner = list[0].M.Body;
                while (inner.Count > 0 && inner[^1] is SReturn { E: null }) inner.RemoveAt(inner.Count - 1);
                var tr = new STry(inner);
                tr.Catches.Add((ended.Type, ended, [new SComment("a performed paragraph's GOBACK: the CALL returns")]));
                list[0].M.Body = [tr];
            }
            // a void method's trailing return (GOBACK, EXIT at the end) is implicit in Java
            foreach (var am in list.Where(x => x.Head.Contains(" void ", StringComparison.Ordinal)))
                while (am.M.Body.Count > 0 && am.M.Body[^1] is SReturn { E: null }) am.M.Body.RemoveAt(am.M.Body.Count - 1);
            // the abend routine: its code a parameter, its DISPLAY the abend's reported line (P4)
            AbendRoutines.Run(app, ap, list);
            // a CALL of a kernel the JDK replaces: its typed method where the function code is proven (Q1)
            KernelCalls.Run(app, ap, list, residue);
            methods[ap] = ap.IsStep ? ClassicFlow.Run(app, ap, list) : list;
            // the classic paragraphs' code fields as enums (a job program's job functions keep their lifted form)
            CodeSets.Run(app, ap, (ap.IsJob ? list.Take(classic) : methods[ap].Concat(ap.Flow?.Nested.SelectMany(n => n.Methods) ?? [])).Select(m => m.M));
        }
        return (methods, objects, residue);
    }

    static bool Remove(List<St> body, St target)
    {
        if (body.Remove(target)) return true;
        foreach (var s in body)
        {
            bool done = s switch
            {
                SIf i => Remove(i.Then, target) || Remove(i.Else, target),
                SLoop l => Remove(l.Body, target),
                SBlock b => Remove(b.Body, target),
                STry t => Remove(t.Body, target) || t.Catches.Any(c => Remove(c.Body, target)),
                SSwitch sw => sw.Cases.Any(c => Remove(c.Body, target)) || sw.Default is { } d && Remove(d, target),
                _ => false,
            };
            if (done) return true;
        }
        return false;
    }

    static IEnumerable<Expr> Deep(Expr e)
    {
        yield return e;
        foreach (var c in e.Children)
            foreach (var d in Deep(c)) yield return d;
    }
}
