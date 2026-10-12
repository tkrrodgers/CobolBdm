using System.Text.Json;
using System.Text.Json.Serialization;
using MlCpu.Cobol.Analysis.Naming;

namespace MlCpu.Cobol.Analysis.JavaBridge;

/// <summary>Exports the analysed program as IR v1.2 with the semantic tree attached (§5.4.7).</summary>
public static class IrExporter
{
    public const string Version = "1.2";
    public const string SemanticSchema = "mlcpu.semantic-tree.v1";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 512,
        WriteIndented = false
    };

    public static string ToJson(IrRoot ir) => JsonSerializer.Serialize(ir, Json);

    /// <summary>
    /// The IR as the sidecar's /transpile-verify needs it: everything but the graph's data dependences, which the sidecar
    /// never reads and which grow with the square of a program's shared storage (a J3S program passing its heap and context
    /// records BY REFERENCE in every CALL has 5.2 million of them: a 300 MB request).
    /// </summary>
    public static string ToTransportJson(IrRoot ir)
    {
        var g = ir.Graph;
        if (g is null || g.DataDeps.Count == 0) return ToJson(ir);
        var copy = new IrRoot
        {
            IrVersion = ir.IrVersion, Program = ir.Program, Dictionary = ir.Dictionary, Paragraphs = ir.Paragraphs, Frontiers = ir.Frontiers,
            Naming = ir.Naming, Records = ir.Records, Semantics = ir.Semantics, SemanticSchema = ir.SemanticSchema,
            Graph = new IrGraph
            {
                ParagraphEdges = g.ParagraphEdges, CtrlDeps = g.CtrlDeps, DataDeps = [], DeadParagraphs = g.DeadParagraphs, Calls = g.Calls, Cfg = g.Cfg,
                DataflowIterations = g.DataflowIterations
            }
        };
        return ToJson(copy);
    }

    /// <summary>A /transpile-verify request: {"ir": transport IR, options...}, written as text (no JSON DOM of the IR).</summary>
    public static string TranspileRequest(IrRoot ir, System.Text.Json.Nodes.JsonObject options)
    {
        var rest = options.ToJsonString();
        return "{\"ir\":" + ToTransportJson(ir) + (rest.Length > 2 ? "," + rest[1..] : "}");
    }

    public static IrRoot Export(CobolProgram p) => Export(p, ProgramGraphBuilder.Build(p));

    public static IrRoot Export(CobolProgram p, ProgramGraph graph)
    {
        var ir = new IrRoot();
        var byName = p.Items.Where(i => !i.IsFiller).GroupBy(i => i.Name).ToDictionary(g => g.Key, g => g.First());
        int? IdOf(string? name) => name is not null && byName.TryGetValue(name, out var it) ? it.Id : null;

        ir.Program = new IrProgram
        {
            Id = p.ProgramId,
            Path = Path.GetFileName(p.Path),
            ExecSql = p.ExecSql,
            ExecCics = p.ExecCics,
            Initial = p.IsInitial, Recursive = p.IsRecursive,
            Using = p.Using.Count > 0 ? p.Using : null,
            FileStatus = p.Files.Where(f => f.FileStatus is not null).ToDictionary(f => f.Name, f => f.FileStatus!),
            FileRecord = p.Files.Where(f => f.Records.Count > 0).ToDictionary(f => f.Name, f => f.Records[0].Name),
            RecordFile = p.Files.SelectMany(f => f.Records.Select(r => (r.Name, f.Name))).GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.First().Item2),
            Files = p.Files.Select(f => new IrFile
            {
                Name = f.Name, Assign = f.Assign, Organization = f.Organization, Access = f.Access,
                RecordKey = KeyId(p, f, f.RecordKey),
                AlternateKeys = f.AlternateKeys.Count > 0 ? f.AlternateKeys.Select(a => new IrAltKey(KeyId(p, f, a.Name) ?? 0, a.Duplicates)).ToList() : null,
                FileStatus = IdOf(f.FileStatus),
                RecordingMode = f.RecordingMode, MinRecord = f.MinRecord, MaxRecord = f.MaxRecord,
                RecordDependingOn = IdOf(f.RecordDependingOn),
                Records = f.Records.Select(r => r.Id).ToList(),
                Sort = f.IsSort
            }).ToList()
        };

        foreach (var it in p.Items)
        {
            ir.Dictionary.Add(new IrField
            {
                Id = it.Id, Field = it.Name, Level = it.Level.ToString("00"), Pic = it.Pic ?? "",
                Usage = UsageText(it.Usage), Occurs = it.Occurs > 0 ? it.Occurs.ToString() : "",
                Redefines = it.Redefines ?? "", Signed = it.Signed, ImpliedDecimals = Math.Max(0, it.Scale),
                Type = TypeOf(it), Notes = it.Level == 88 ? "condition-name (88)" : it.Children.Count > 0 ? "group item (structural)" : "",
                Dims = it.Dims.Count > 0 ? it.Dims.Select(d => d.Count).ToList() : null,
                Strides = it.Dims.Count > 0 ? it.Dims.Select(d => d.Stride).ToList() : null,
                OccursOwner = it.Occurs > 0 ? it.Name : null,
                ParentId = it.Parent?.Id, RecordId = it.Record?.Id ?? it.Id, Section = it.Section.ToString(),
                Offset = it.Offset, Length = it.Length, Category = it.Category.ToString(), Digits = it.Digits,
                SignSeparate = it.SignSeparate, SignLeading = it.SignLeading, Justified = it.Justified, BlankWhenZero = it.BlankWhenZero,
                Values = it.Values.Count > 0 ? it.Values.Select(v => new IrValue { V = v.Low.Text, Q = v.Low.Quoted, Hi = v.High?.Text, Hq = v.High?.Quoted }).ToList() : null,
                ValueAll = it.ValueAll, File = it.FileName,
                FalseValue = it.FalseValue is { } fv ? new IrValue { V = fv.Text, Q = fv.Quoted } : null,
                RedefinesId = it.RedefinesItem?.Id, DependingOn = it.DependingOn, Line = it.Line,
                DependingOnId = it.DependingOnItem?.Id, External = it.External, OccursMin = it.DependingOn is not null ? it.OccursMin : null,
                Keys = it.Keys.Count > 0 && it.KeyItems.All(k => k is not null) ? it.Keys.Select((k, i) => new IrKey(k.Ascending, it.KeyItems[i]!.Id)).ToList() : null,
                IndexedBy = it.IndexedBy.Count > 0 ? it.IndexedBy.Select(n => p.Items.FirstOrDefault(x => x.Level == 77 && x.Usage == Usage.Index && x.Name == n)?.Id ?? 0).ToList() : null
            });
            ir.Naming.Add(new IrName(it.Name, IdentifierModernizer.Modernize(it.Name)));
        }
        ir.Naming = ir.Naming.Where(n => n.Original != "FILLER").GroupBy(n => n.Original).Select(g => g.First()).ToList();

        foreach (var rec in p.Items.Where(i => i.Parent is null && i.Level is 1 or 77))
        {
            var r = new IrRecord { Cobol = rec.Name, Length = rec.Length, Redefines = rec.Redefines, Occurs = Math.Max(1, rec.Occurs) };
            foreach (var leaf in Leaves(rec))
            {
                r.Fields.Add(new IrRecordField
                {
                    Cobol = leaf.Name, Offset = leaf.Offset, Length = leaf.Length, Decimals = Math.Max(0, leaf.Scale), Signed = leaf.Signed,
                    Numeric = leaf.Category == Category.Numeric, Packed = leaf.Usage == Usage.Comp3, Pic = leaf.Pic,
                    Value = leaf.Values.Count > 0 ? leaf.Values[0].Low.Text : null, Edited = leaf.Category is Category.NumericEdited or Category.AlphanumericEdited,
                    Overlay = IsOverlay(leaf), Binary = leaf.Usage is Usage.Binary or Usage.Comp5,
                    SignSeparate = leaf.SignSeparate ? (leaf.SignLeading ? "LEADING" : "TRAILING") : null
                });
                if (leaf.DependingOn is not null) { r.Placeable = false; r.DependingOn = leaf.DependingOn; }
            }
            ir.Records.Add(r);
        }

        foreach (var m in p.MissingCopybooks) ir.Frontiers.Add(new IrFrontier("copybook", m.Member, $"missing copybook at L{m.ProgramLine}"));
        foreach (var para in p.Paragraphs)
        {
            var irp = new IrParagraph { Name = para.Name, Start = para.Line, End = para.EndLine };
            Flatten(para.Statements, [], irp.Statements, ir.Frontiers, p.Items);
            ir.Paragraphs.Add(irp);
        }
        var paraNames = new HashSet<string>(p.Paragraphs.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var st in ir.Paragraphs.SelectMany(x => x.Statements))
        {
            if (st.PerformTarget is { } t && !paraNames.Contains(t)) ir.Frontiers.Add(new IrFrontier("perform", t, $"unresolved PERFORM target at {st.StepId}"));
            if (st.GoToTarget is { } g && !paraNames.Contains(g)) ir.Frontiers.Add(new IrFrontier("goto", g, $"unresolved GO TO target at {st.StepId}"));
        }
        foreach (var d in p.Diagnostics) ir.Frontiers.Add(new IrFrontier("diagnostic", p.ProgramId, d));
        foreach (var f in graph.Frontiers.Where(f => f.Kind is "dynamic-call" or "dataflow"))
            ir.Frontiers.Add(new IrFrontier(f.Kind, f.Subject, f.Detail));
        ir.Graph = new IrGraph
        {
            ParagraphEdges = graph.ParagraphEdges, CtrlDeps = graph.CtrlDeps, DataDeps = graph.DataDeps,
            DeadParagraphs = graph.DeadParagraphs, Calls = graph.Calls, Cfg = graph.Cfg, DataflowIterations = graph.DataflowIterations
        };

        ir.Semantics = new IrSemantics
        {
            Paragraphs = p.Paragraphs.Select(x => new IrSemParagraph
            {
                Name = x.Name, Section = x.Section, IsSection = x.IsSection, Line = x.Line, Statements = x.Statements,
                Declarative = x.Declarative, UseFiles = x.UseFiles, UseModes = x.UseModes
            }).ToList()
        };
        ir.SemanticSchema = SemanticSchema;
        return ir;
    }

    static int? KeyId(CobolProgram p, FileDecl f, string? name)
    {
        if (name is null) return null;
        // A record key is a field of the file's record area.
        var inFile = p.Items.FirstOrDefault(i => i.Name == name && i.Record?.FileName == f.Name);
        return (inFile ?? p.Items.FirstOrDefault(i => i.Name == name))?.Id;
    }

    static bool IsOverlay(DataItem it)
    {
        for (var x = it; x is not null; x = x.Parent) if (x.Redefines is not null) return true;
        return false;
    }

    static IEnumerable<DataItem> Leaves(DataItem it)
    {
        if (it.Level == 88) yield break;
        if (it.IsElementary) { yield return it; yield break; }
        foreach (var c in it.Children) foreach (var l in Leaves(c)) yield return l;
    }

    static string UsageText(Usage u) => u switch
    {
        Usage.Comp3 => "COMP-3", Usage.Binary => "COMP", Usage.Comp5 => "COMP-5", Usage.Comp1 => "COMP-1", Usage.Comp2 => "COMP-2",
        Usage.Pointer => "POINTER", Usage.Index => "INDEX", _ => "DISPLAY"
    };

    static string TypeOf(DataItem it) => it.Category switch
    {
        Category.Numeric when it.Scale > 0 || it.Usage == Usage.Comp3 || it.Signed => "decimal",
        Category.Numeric => it.Digits > 9 ? "long" : "int",
        Category.Group => "",
        Category.Condition => "",
        _ => "string"
    };

    static void Flatten(List<Stmt> list, List<IrGuard> guards, List<IrStatement> output, List<IrFrontier> frontiers, List<DataItem> items)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            bool endsSentence = i + 1 >= list.Count || list[i + 1].Sentence != s.Sentence;
            var st = new IrStatement
            {
                StepId = s.StepId, Line = s.Line, Opcode = s.Verb, Text = s.Text, Guards = [.. guards],
                IsExit = s.Verb is "GOBACK" or "STOP RUN" or "EXIT PROGRAM",
                EndsSentence = guards.Count == 0 && endsSentence,
                SemanticNodeId = s.StepId,
                SemanticStatus = s.Frontier is null ? "complete" : "raw"
            };
            if (s.Verb == "PERFORM" && s.Str("target") is { } t) { st.PerformTarget = t; st.PerformThru = s.Str("thru"); }
            if (s.Verb == "GO TO" && s.L?.GetValueOrDefault("targets") is { Count: > 0 } gt) st.GoToTarget = gt[0].V;
            CollectDefUse(s, st, items);
            if (s.Frontier is not null) frontiers.Add(new IrFrontier("statement", s.StepId, s.Frontier));
            output.Add(st);
            if (s.Blocks is null) continue;
            foreach (var (branch, block) in s.Blocks)
            {
                if (branch == "WHEN")
                {
                    Flatten(block, [.. guards, new IrGuard(s.StepId, "EVAL")], output, frontiers, items);
                    continue;
                }
                Flatten(block, [.. guards, new IrGuard(s.StepId, branch)], output, frontiers, items);
            }
        }
    }

    static void CollectDefUse(Stmt s, IrStatement st, List<DataItem> items)
    {
        var names = items.ToDictionary(i => i.Id, i => i.Name);
        void Refs(Node? n, List<string> into)
        {
            if (n is null) return;
            if (n.K is "ref" or "c88" && n.Id is { } id && names.TryGetValue(id, out var nm) && !into.Contains(nm)) into.Add(nm);
            Refs(n.L, into); Refs(n.R, into); Refs(n.Start, into); Refs(n.Len, into);
            if (n.Args is not null) foreach (var a in n.Args) Refs(a, into);
        }
        string[] defKeys = ["dst", "giving", "into", "rem", "pointer", "tallying", "var", "index", "varying"];
        if (s.N is not null)
            foreach (var (k, n) in s.N) Refs(n, defKeys.Contains(k) ? st.Defs : st.Uses);
        if (s.L is not null)
            foreach (var (k, l) in s.L) foreach (var n in l) Refs(n, defKeys.Contains(k) ? st.Defs : st.Uses);
        // ADD/SUBTRACT/MULTIPLY/DIVIDE without GIVING also read their receivers.
        if (s.Verb is "ADD" or "SUBTRACT" or "MULTIPLY" or "DIVIDE" && s.L?.GetValueOrDefault("dst") is { } dsts)
            foreach (var d in dsts) Refs(d, st.Uses);
    }
}
