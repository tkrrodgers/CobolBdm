using System.Text;
using System.Text.RegularExpressions;
using MlCpu.Core;

namespace MlCpu.CobolKb.Knowledge;

public sealed record JclCitation(string Member, int Line);

public sealed record JclDataset(string Dd, string? Dsn, string Direction, string? Disp, string? Generation, string? Member,
    bool Dummy, bool Temporary, bool Sysout, string? InStream, string? Recfm, string? Lrecl, JclCitation Citation);

public sealed record JclStep(int Order, string Step, string QualifiedStep, string? Program, string? Parm, string? Cond, bool Conditional,
    IReadOnlyList<JclDataset> Datasets, JclCitation Citation, IReadOnlyList<string> InvocationChain, IReadOnlyDictionary<string, string> Symbols);

public sealed record JclDiagnostic(string Member, int Line, string Message);

public sealed record JclJob(string Name, string Path, IReadOnlyList<JclStep> Steps, IReadOnlyList<JclDiagnostic> Diagnostics)
{
    /// <summary>Unconditional steps only (IF/THEN/ELSE-guarded EXECs stay in <see cref="Steps"/> but are not part of the expanded job).</summary>
    public IEnumerable<JclStep> Expanded => Steps.Where(s => !s.Conditional);
}

/// <summary>
/// JCL harvester (§5.5.1): statements from columns 1–72 with continuations and in-stream data; SET symbols;
/// in-stream and cataloged PROCs expanded through their callers (PROC defaults, then caller overrides, nested,
/// cycle-guarded); procstep.DD overrides; &amp;SYM substitution (4 passes, unknown symbols left intact).
/// </summary>
public static partial class JclHarvester
{
    sealed record Stmt(string? Name, string Op, string Operands, int Line, string Member, List<string> InStream);

    sealed record ProcDef(string Name, string Member, Dictionary<string, string> Defaults, List<Stmt> Body);

    [GeneratedRegex(@"&([A-Z#@$][A-Z0-9#@$]{0,7})\.?", RegexOptions.IgnoreCase)] private static partial Regex SymbolRx();
    [GeneratedRegex(@"^(.*)\(([+-]?\d+)\)$")] private static partial Regex GdgRx();
    [GeneratedRegex(@"^(.*)\(([A-Z#@$][A-Z0-9#@$]{0,7})\)$", RegexOptions.IgnoreCase)] private static partial Regex MemberRx();
    [GeneratedRegex(@"(^|,)\s*DUMMY\s*(,|$)", RegexOptions.IgnoreCase)] private static partial Regex DummyRx();

    public static List<JclJob> Harvest(IEnumerable<string> jclRoots, IEnumerable<string>? procRoots = null)
    {
        var procs = new Dictionary<string, ProcDef>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<JclDiagnostic>();
        foreach (var root in (procRoots ?? []).Concat(jclRoots))
            foreach (var path in CobolFiles.Jcl(root).Where(p => Path.GetExtension(p).ToLowerInvariant() is ".prc" or ".proc"))
                foreach (var proc in ProcsIn(Parse(path)))
                    if (!procs.TryAdd(proc.Name, proc)) diagnostics.Add(new JclDiagnostic(Path.GetFileName(path), 0, $"duplicate PROC {proc.Name}"));

        var jobs = new List<JclJob>();
        foreach (var root in jclRoots)
            foreach (var path in CobolFiles.Jcl(root).Where(p => Path.GetExtension(p).ToLowerInvariant() is ".jcl" or ".job" or ".jobs"))
                jobs.Add(HarvestJob(path, procs, diagnostics.ToList()));
        return jobs.OrderBy(j => j.Name, StringComparer.Ordinal).ToList();
    }

    static JclJob HarvestJob(string path, Dictionary<string, ProcDef> catalog, List<JclDiagnostic> diags)
    {
        var stmts = Parse(path);
        var member = Path.GetFileName(path);
        var jobName = stmts.FirstOrDefault(s => s.Op == "JOB")?.Name ?? Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
        var instream = ProcsIn(stmts).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var symbols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var steps = new List<JclStep>();
        int depth = 0;
        bool inProc = false;
        for (int i = 0; i < stmts.Count; i++)
        {
            var s = stmts[i];
            if (s.Op == "PROC") { inProc = true; continue; }
            if (s.Op == "PEND") { inProc = false; continue; }
            if (inProc) continue;
            switch (s.Op)
            {
                case "SET":
                    foreach (var (k, v) in Keywords(s.Operands)) symbols[k] = Subst(v, symbols);
                    continue;
                case "IF": depth++; continue;
                case "ENDIF": depth = Math.Max(0, depth - 1); continue;
                case "EXEC":
                {
                    var (pos, kw) = Split(s.Operands);
                    var dds = new List<Stmt>();
                    int j = i + 1;
                    while (j < stmts.Count && stmts[j].Op == "DD") dds.Add(stmts[j++]);
                    var stepName = s.Name ?? $"STEP{steps.Count + 1:000}";
                    if (kw.TryGetValue("PGM", out var pgm))
                    {
                        steps.Add(MakeStep(steps.Count + 1, stepName, stepName, Subst(pgm, symbols), kw, dds, symbols, depth > 0, member, s.Line, [jobName], null));
                    }
                    else
                    {
                        var procName = kw.TryGetValue("PROC", out var pn) ? pn : pos.FirstOrDefault() ?? "";
                        var callerKw = kw.Where(k => !k.Key.Equals("PROC", StringComparison.OrdinalIgnoreCase)).ToDictionary(k => k.Key, k => Subst(k.Value, symbols), StringComparer.OrdinalIgnoreCase);
                        ExpandProc(procName, stepName, callerKw, dds, symbols, instream, catalog, steps, depth > 0, member, s.Line, [jobName], diags, new Stack<string>());
                    }
                    i = j - 1;
                    continue;
                }
            }
        }
        return new JclJob(jobName.ToUpperInvariant(), path, steps, diags.Where(d => d.Member == member || d.Line == 0).ToList());
    }

    static void ExpandProc(string procName, string callerStep, Dictionary<string, string> callerKw, List<Stmt> overrideDds,
        Dictionary<string, string> jobSymbols, Dictionary<string, ProcDef> instream, Dictionary<string, ProcDef> catalog,
        List<JclStep> steps, bool conditional, string member, int line, List<string> chain, List<JclDiagnostic> diags, Stack<string> guard)
    {
        if (!instream.TryGetValue(procName, out var proc) && !catalog.TryGetValue(procName, out proc))
        {
            diags.Add(new JclDiagnostic(member, line, $"PROC {procName} not found (frontier)"));
            steps.Add(new JclStep(steps.Count + 1, callerStep, callerStep, null, null, null, conditional, [], new JclCitation(member, line), [.. chain, "PROC " + procName], jobSymbols));
            return;
        }
        if (guard.Contains(procName)) { diags.Add(new JclDiagnostic(member, line, $"PROC cycle through {procName}")); return; }
        guard.Push(procName);
        // PROC defaults first, then the caller's overrides; SET symbols fill the rest.
        var symbols = new Dictionary<string, string>(jobSymbols, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in proc.Defaults) symbols[k] = v;
        var stepParms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in callerKw)
        {
            var dot = k.IndexOf('.');
            if (dot > 0) stepParms[k] = v; // PARM.PRC001=..., COND.PRC001=...
            else if (k is "PARM" or "COND" or "REGION" or "TIME") stepParms[k] = v;
            else symbols[k] = v;
        }
        var body = proc.Body;
        for (int i = 0; i < body.Count; i++)
        {
            var s = body[i];
            if (s.Op != "EXEC") continue;
            var (pos, kw) = Split(s.Operands);
            var dds = new List<Stmt>();
            int j = i + 1;
            while (j < body.Count && body[j].Op == "DD") dds.Add(body[j++]);
            var procStep = s.Name ?? $"PS{i}";
            // Caller DD overrides: //PROCSTEP.DDNAME DD … replace or add.
            foreach (var o in overrideDds.Where(o => o.Name is not null && o.Name.StartsWith(procStep + ".", StringComparison.OrdinalIgnoreCase)))
            {
                var dd = o.Name![(procStep.Length + 1)..];
                var existing = dds.FindIndex(d => string.Equals(d.Name, dd, StringComparison.OrdinalIgnoreCase));
                var replacement = o with { Name = dd };
                if (existing >= 0) dds[existing] = replacement; else dds.Add(replacement);
            }
            var stepKw = kw.ToDictionary(k => k.Key, k => k.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var key in new[] { "PARM", "COND" })
            {
                if (stepParms.TryGetValue($"{key}.{procStep}", out var v)) stepKw[key] = v;
                else if (stepParms.TryGetValue(key, out var all) && i == body.FindIndex(x => x.Op == "EXEC")) stepKw[key] = all;
            }
            if (stepKw.TryGetValue("PGM", out var pgm))
                steps.Add(MakeStep(steps.Count + 1, procStep, $"{callerStep}.{procStep}", Subst(pgm, symbols), stepKw, dds, symbols, conditional,
                    proc.Member, s.Line, [.. chain, "PROC " + proc.Name], new JclCitation(member, line)));
            else
            {
                var nested = stepKw.TryGetValue("PROC", out var np) ? np : pos.FirstOrDefault() ?? "";
                ExpandProc(nested, $"{callerStep}.{procStep}", stepKw.Where(k => k.Key != "PROC").ToDictionary(k => k.Key, k => Subst(k.Value, symbols), StringComparer.OrdinalIgnoreCase),
                    [], symbols, instream, catalog, steps, conditional, proc.Member, s.Line, [.. chain, "PROC " + proc.Name], diags, guard);
            }
            i = j - 1;
        }
        guard.Pop();
    }

    static JclStep MakeStep(int order, string name, string qualified, string program, Dictionary<string, string> kw, List<Stmt> dds,
        Dictionary<string, string> symbols, bool conditional, string member, int line, List<string> chain, JclCitation? invokedFrom)
    {
        var datasets = dds.Select(d => Dataset(d, symbols)).ToList();
        var parm = kw.TryGetValue("PARM", out var p) ? Unquote(Subst(p, symbols)) : null;
        var cond = kw.TryGetValue("COND", out var c) ? Subst(c, symbols) : null;
        var cite = new JclCitation(member, line);
        return new JclStep(order, name.ToUpperInvariant(), qualified.ToUpperInvariant(), program.ToUpperInvariant(), parm, cond, conditional, datasets, cite,
            invokedFrom is null ? chain : [.. chain, $"{invokedFrom.Member}:{invokedFrom.Line}"], new Dictionary<string, string>(symbols, StringComparer.OrdinalIgnoreCase));
    }

    static JclDataset Dataset(Stmt d, Dictionary<string, string> symbols)
    {
        var operands = Subst(d.Operands, symbols);
        var (pos, kw) = Split(operands);
        string? dsn = kw.TryGetValue("DSN", out var x) ? x : kw.TryGetValue("DSNAME", out var y) ? y : null;
        string? gen = null, mem = null;
        if (dsn is not null)
        {
            var g = GdgRx().Match(dsn);
            if (g.Success) { gen = g.Groups[2].Value; dsn = g.Groups[1].Value; }
            else
            {
                var m = MemberRx().Match(dsn);
                if (m.Success) { mem = m.Groups[2].Value.ToUpperInvariant(); dsn = m.Groups[1].Value; }
            }
        }
        var disp = kw.TryGetValue("DISP", out var dp) ? dp.Trim('(', ')') : null;
        bool dummy = DummyRx().IsMatch(operands) || string.Equals(dsn, "NULLFILE", StringComparison.OrdinalIgnoreCase);
        bool sysout = kw.ContainsKey("SYSOUT");
        bool temp = dsn?.StartsWith("&&") == true;
        string? recfm = null, lrecl = null;
        if (kw.TryGetValue("DCB", out var dcb))
        {
            var (_, dkw) = Split(dcb.Trim('(', ')'));
            recfm = dkw.GetValueOrDefault("RECFM");
            lrecl = dkw.GetValueOrDefault("LRECL");
        }
        recfm ??= kw.GetValueOrDefault("RECFM");
        lrecl ??= kw.GetValueOrDefault("LRECL");
        string direction;
        if (d.InStream.Count > 0 || pos.Contains("*") || pos.Any(p => p.Equals("DATA", StringComparison.OrdinalIgnoreCase))) direction = "instream";
        else if (sysout) direction = "sysout";
        else if (dummy) direction = "dummy";
        else if (gen is not null) direction = gen.StartsWith('+') ? "producer" : "consumer";
        else
        {
            var first = disp?.Split(',')[0].Trim().ToUpperInvariant() ?? "";
            direction = first is "NEW" or "MOD" ? "producer" : "consumer";
        }
        return new JclDataset(d.Name?.ToUpperInvariant() ?? "", dsn?.ToUpperInvariant(), direction, disp, gen, mem, dummy, temp, sysout,
            d.InStream.Count > 0 ? string.Join("\n", d.InStream) : null, recfm, lrecl, new JclCitation(d.Member, d.Line));
    }

    static string Unquote(string s) => s.Length >= 2 && s[0] == '\'' && s[^1] == '\'' ? s[1..^1].Replace("''", "'") : s.Trim('(', ')');

    /// <summary>Substitute &amp;SYM (and &amp;SYM.) — at most 4 passes; unknown symbols and &amp;&amp;temp names are left intact.</summary>
    public static string Subst(string text, IReadOnlyDictionary<string, string> symbols)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            var next = SymbolRx().Replace(text, m =>
            {
                if (m.Index > 0 && text[m.Index - 1] == '&') return m.Value;
                return symbols.TryGetValue(m.Groups[1].Value, out var v) ? Unquote(v) : m.Value;
            });
            if (next == text) break;
            text = next;
        }
        return text;
    }

    // ------------------------------------------------------------------ statements

    static IEnumerable<ProcDef> ProcsIn(List<Stmt> stmts)
    {
        for (int i = 0; i < stmts.Count; i++)
        {
            if (stmts[i].Op != "PROC" || stmts[i].Name is null) continue;
            var body = new List<Stmt>();
            int j = i + 1;
            while (j < stmts.Count && stmts[j].Op != "PEND" && stmts[j].Op != "PROC") body.Add(stmts[j++]);
            yield return new ProcDef(stmts[i].Name!.ToUpperInvariant(), stmts[i].Member, Keywords(stmts[i].Operands).ToDictionary(k => k.Key, k => k.Value, StringComparer.OrdinalIgnoreCase), body);
            i = j;
        }
    }

    static List<Stmt> Parse(string path)
    {
        var member = Path.GetFileName(path);
        var lines = File.ReadAllLines(path, Encoding.Latin1);
        var result = new List<Stmt>();
        Stmt? pendingDd = null;
        string delimiter = "/*";
        bool dataMode = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (pendingDd is not null)
            {
                // In-stream records are 80 columns. DD DATA keeps '//' lines; DD * ends at the next statement too.
                if (lines[i].StartsWith(delimiter, StringComparison.Ordinal)) { pendingDd = null; continue; }
                if (dataMode || !lines[i].StartsWith("//"))
                {
                    pendingDd.InStream.Add(lines[i].TrimEnd());
                    continue;
                }
                pendingDd = null;
            }
            var raw = lines[i].Length > 72 ? lines[i][..72] : lines[i];
            if (!raw.StartsWith("//") && !raw.StartsWith("/*")) continue;
            if (raw.StartsWith("/*") || raw.StartsWith("//*")) continue;
            var body = raw[2..];
            string? name = null;
            if (body.Length > 0 && body[0] != ' ')
            {
                var sp = body.IndexOf(' ');
                name = (sp < 0 ? body : body[..sp]).ToUpperInvariant();
                body = sp < 0 ? "" : body[sp..];
            }
            body = body.TrimStart();
            var opEnd = body.IndexOf(' ');
            var op = (opEnd < 0 ? body : body[..opEnd]).ToUpperInvariant();
            var operands = OperandField(opEnd < 0 ? "" : body[(opEnd + 1)..].TrimStart());
            int line = i + 1;
            // Continuation: operands ending in ',' continue on the next // line.
            while (operands.EndsWith(',') && i + 1 < lines.Length && lines[i + 1].StartsWith("//") && !lines[i + 1].StartsWith("//*"))
            {
                i++;
                var next = lines[i].Length > 72 ? lines[i][..72] : lines[i];
                operands += OperandField(next[2..].TrimStart());
            }
            if (op is "IF") { result.Add(new Stmt(name, "IF", operands, line, member, [])); continue; }
            var st = new Stmt(name, op, operands, line, member, []);
            result.Add(st);
            if (op == "DD" && (operands.StartsWith('*') || operands.StartsWith("DATA", StringComparison.OrdinalIgnoreCase)))
            {
                pendingDd = st;
                dataMode = operands.StartsWith("DATA", StringComparison.OrdinalIgnoreCase);
                var dlm = Keywords(operands).FirstOrDefault(k => k.Key.Equals("DLM", StringComparison.OrdinalIgnoreCase)).Value;
                delimiter = string.IsNullOrEmpty(dlm) ? "/*" : dlm.Trim('\'');
            }
        }
        return result;
    }

    /// <summary>The operand field ends at the first blank outside quotes (the rest is a comment).</summary>
    static string OperandField(string s)
    {
        bool q = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\'') q = !q;
            else if (s[i] == ' ' && !q) return s[..i];
        }
        return s.TrimEnd();
    }

    static IEnumerable<KeyValuePair<string, string>> Keywords(string operands) => Split(operands).Keywords;

    /// <summary>Split operands at top-level commas into positional and KEY=VALUE parameters.</summary>
    static (List<string> Positional, Dictionary<string, string> Keywords) Split(string operands)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;
        bool q = false;
        foreach (var c in operands)
        {
            if (c == '\'') q = !q;
            if (!q && c == '(') depth++;
            if (!q && c == ')') depth--;
            if (!q && depth == 0 && c == ',') { parts.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        var pos = new List<string>();
        var kw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts.Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            var eq = p.IndexOf('=');
            if (eq > 0 && !p[..eq].Contains('(') && !p[..eq].Contains('\'')) kw[p[..eq].Trim()] = p[(eq + 1)..].Trim();
            else pos.Add(p);
        }
        return (pos, kw);
    }
}
