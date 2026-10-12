using System.Text;
using System.Text.RegularExpressions;
using MlCpu.Cobol.Analysis;

namespace MlCpu.CobolKb.Knowledge;

/// <summary>
/// The contracts of the programs a capability CALLs, for its design and implementation prompts
/// (normalization/&lt;PID&gt;/callee-contracts.md). For each program the capability CALLs directly (a static CALL) whose
/// library-equivalence probe verified anything: the CALL statements, every place the capability reaches them (the CALL,
/// or a PERFORM of the paragraph range that holds it) with the argument fields it sets just before, and the callee's
/// verified statements and exception contracts (its equivalences.md). A model that computes the callee's result itself
/// instead of calling it must still produce every effect the contract lists, such as a reject site taken from the value
/// the caller placed in an argument field before that CALL. The requirements packet is untouched.
/// </summary>
public static class CalleeContracts
{
    /// <summary>The programs <paramref name="caller"/> CALLs by a literal name, in source order.</summary>
    public static IReadOnlyList<string> DirectCallees(CobolProgram caller) =>
        DdBindingPlanner.Walk(caller)
            .Where(s => s.Verb == "CALL" && s.Get("target") is { K: "str", V: not null })
            .Select(s => s.Get("target")!.V!.Trim().ToUpperInvariant())
            .Distinct().ToList();

    /// <summary>
    /// The callee contracts of <paramref name="caller"/>, or null when none of its direct callees has verified
    /// equivalences. <paramref name="equivalences"/> maps a program id to its equivalences.md.
    /// </summary>
    public static string? Build(CobolProgram caller, IReadOnlyDictionary<string, string> equivalences)
    {
        var callees = DirectCallees(caller).Where(c => equivalences.ContainsKey(c) && !c.Equals(caller.ProgramId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (callees.Count == 0) return null;
        var pid = caller.ProgramId;
        var sb = new StringBuilder($"# Contracts of the programs {pid} CALLs (verified)\n\n");
        sb.Append($"{pid} CALLs the programs below. Each contract was verified by the pipeline on the callee's own CALLs (traced from the faithful ");
        sb.Append("program, and generated ones run through it), field by field. You may CALL the program or compute its result yourself. If you compute ");
        sb.Append("it yourself, you must still produce every effect its contract lists for that CALL: the result fields, the return and status fields, ");
        sb.Append("and every field the callee copies from the caller's arguments (for example a reject site taken from the value the caller placed in ");
        sb.Append("an argument field before that CALL), on the error paths as well as on the normal one. The callee's field names are the argument ");
        sb.Append("records' fields as the caller passes them.\n");
        foreach (var callee in callees)
        {
            sb.Append($"\n## {callee}\n\n");
            var argFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (para, call) in Statements(caller).Where(x => IsCallTo(x.S, callee)))
            {
                var args = (call.L?.GetValueOrDefault("args") ?? []).Select(a => a.L?.V).OfType<string>().ToList();
                sb.Append($"- `CALL '{callee}' USING {string.Join(" ", args)}` in {para}, line {call.Line}\n");
                foreach (var a in (call.L?.GetValueOrDefault("args") ?? []).Select(a => a.L).OfType<Node>())
                    if (a.Id is { } id && caller.Items.FirstOrDefault(i => i.Id == id) is { } rec)
                        foreach (var it in Tree(rec)) argFields.Add(it.Name);
            }
            var sites = Sites(caller, callee, argFields);
            var shown = sites.Where(x => x.Sets.Count > 0).ToList();
            if (shown.Count > 0)
            {
                sb.Append($"\nWhere {pid} reaches it (the CALL, or a PERFORM that leads to it), with the argument fields its paragraph writes since the ");
                sb.Append("previous such place, on any branch (fields not written there keep the values they had):\n\n");
                foreach (var x in shown) sb.Append($"- {x.Paragraph}, line {x.Line} ({x.How}): {string.Join("; ", x.Sets)}\n");
            }
            // Only the sections for the codes the caller sets (a function code the caller never passes is not its contract).
            var codes = LiteralCodes(caller, argFields);
            var (body, left) = Sections(Body(equivalences[callee]), codes);
            sb.Append($"\nThe verified contract of {callee}");
            if (left.Count > 0) sb.Append($" for the codes {pid} sets (left out: {string.Join(", ", left)}, which {pid} never sets)");
            sb.Append(":\n\n");
            sb.Append(body.TrimEnd()).Append('\n');
        }
        return sb.ToString();
    }

    static readonly Regex SectionKey = new(@"^###\s+([A-Z0-9][A-Z0-9-]*)\s*=\s*'([^']*)'", RegexOptions.Compiled);
    static readonly Regex BulletKey = new(@"^-\s+([A-Z0-9][A-Z0-9-]*)\s*=\s*'([^']*)'", RegexOptions.Compiled);

    /// <summary>The literal values the caller MOVEs into each argument field (trimmed), anywhere in the program.</summary>
    static Dictionary<string, HashSet<string>> LiteralCodes(CobolProgram m, HashSet<string> argFields)
    {
        var codes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in DdBindingPlanner.Walk(m))
        {
            if (s.Verb != "MOVE" || s.Get("src") is not { K: "str", V: { } v }) continue;
            foreach (var d in s.L?.GetValueOrDefault("dst") ?? [])
                if (d.V is { } name && argFields.Contains(name))
                    (codes.TryGetValue(name, out var set) ? set : codes[name] = new HashSet<string>(StringComparer.Ordinal)).Add(v.Trim());
        }
        return codes;
    }

    /// <summary>
    /// The contract's sections (and the bullets of its "not explained" list) whose selector code the caller sets; a section
    /// keyed by a field the caller never sets a literal into stays. Returns the kept text and the codes left out.
    /// </summary>
    static (string Text, List<string> Left) Sections(string body, Dictionary<string, HashSet<string>> codes)
    {
        bool Keep(Match k) => !k.Success || !codes.TryGetValue(k.Groups[1].Value, out var set) || set.Contains(k.Groups[2].Value.Trim());
        var left = new List<string>();
        var parts = Regex.Split(body, "(?m)^(?=### )");
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            var key = SectionKey.Match(part);
            if (!Keep(key))
            {
                left.Add($"{key.Groups[1].Value} = '{key.Groups[2].Value}'");
                continue;
            }
            var lines = part.Split('\n').Where(line =>
            {
                var b = BulletKey.Match(line);
                if (Keep(b)) return true;
                left.Add($"{b.Groups[1].Value} = '{b.Groups[2].Value}'");
                return false;
            });
            var kept = string.Join("\n", lines);
            // a "not explained" section with no bullet left says nothing
            if (kept.TrimStart().StartsWith("### Not explained", StringComparison.Ordinal) && !kept.Contains("\n- ", StringComparison.Ordinal)) continue;
            sb.Append(kept);
        }
        return (sb.ToString(), left.Distinct().ToList());
    }

    /// <summary>equivalences.md without its title and opening paragraph, its sections one level down.</summary>
    static string Body(string md)
    {
        var text = md.Replace("\r\n", "\n");
        int first = text.IndexOf("\n## ", StringComparison.Ordinal);
        if (first < 0) return "(nothing verified)";
        return Regex.Replace(text[(first + 1)..], "(?m)^## ", "### ");
    }

    sealed record Site(string Paragraph, int Line, string How, List<string> Sets);

    static bool IsCallTo(Stmt s, string callee) =>
        s.Verb == "CALL" && s.Get("target") is { K: "str", V: { } v } && v.Trim().Equals(callee, StringComparison.OrdinalIgnoreCase);

    static IEnumerable<(string Para, Stmt S)> Statements(CobolProgram m) =>
        m.Paragraphs.SelectMany(p => Flatten(p.Statements).Select(s => (p.Name, s)));

    static IEnumerable<Stmt> Flatten(IEnumerable<Stmt> list)
    {
        foreach (var s in list)
        {
            yield return s;
            if (s.Blocks is not null)
                foreach (var b in s.Blocks.Values)
                    foreach (var x in Flatten(b)) yield return x;
        }
    }

    static IEnumerable<DataItem> Tree(DataItem d)
    {
        yield return d;
        foreach (var c in d.Children)
            foreach (var x in Tree(c)) yield return x;
    }

    /// <summary>
    /// Every CALL of <paramref name="callee"/>, and every out-of-line PERFORM whose paragraph range reaches one, with the
    /// statements writing an argument field since the previous such place in the same paragraph.
    /// </summary>
    static List<Site> Sites(CobolProgram m, string callee, HashSet<string> argFields)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < m.Paragraphs.Count; i++) index.TryAdd(m.Paragraphs[i].Name, i);
        var memo = new Dictionary<int, bool>();
        bool Reaches(int i, HashSet<int> visiting)
        {
            if (memo.TryGetValue(i, out var r)) return r;
            if (!visiting.Add(i)) return false;
            r = false;
            foreach (var s in Flatten(m.Paragraphs[i].Statements))
            {
                if (IsCallTo(s, callee)) { r = true; break; }
                if (Range(s) is { } range && range.Any(j => Reaches(j, visiting))) { r = true; break; }
            }
            visiting.Remove(i);
            return memo[i] = r;
        }
        IReadOnlyList<int>? Range(Stmt s)
        {
            if (s.Verb != "PERFORM" || s.Str("inline") == "true" || s.Str("target") is not { } t || !index.TryGetValue(t, out var lo)) return null;
            var hi = s.Str("thru") is { } thru && index.TryGetValue(thru, out var h) ? h : lo;
            return Enumerable.Range(lo, Math.Max(1, hi - lo + 1)).ToList();
        }
        var sites = new List<Site>();
        foreach (var p in m.Paragraphs)
        {
            var pending = new List<(string Field, string Text)>();
            foreach (var s in Flatten(p.Statements))
            {
                if (IsCallTo(s, callee))
                {
                    sites.Add(new Site(p.Name, s.Line, "the CALL", pending.Select(x => x.Text).ToList()));
                    pending.Clear();
                    continue;
                }
                if (Range(s) is { } range && range.Any(j => Reaches(j, [])))
                {
                    sites.Add(new Site(p.Name, s.Line, $"PERFORM {s.Str("target")}{(s.Str("thru") is { } th ? " THRU " + th : "")}", pending.Select(x => x.Text).ToList()));
                    pending.Clear();
                    continue;
                }
                foreach (var d in s.L?.GetValueOrDefault("dst") ?? [])
                {
                    if (d.V is not { } name || !argFields.Contains(name)) continue;
                    pending.RemoveAll(x => x.Field.Equals(name, StringComparison.OrdinalIgnoreCase));
                    pending.Add((name, Written(s, name)));
                }
            }
        }
        return sites;
    }

    /// <summary>A write of an argument field in words: FIELD = 'literal', FIELD = OTHER-FIELD, or the statement.</summary>
    static string Written(Stmt s, string field)
    {
        if (s.Verb == "MOVE" && s.Get("src") is { } src)
            return src.K switch
            {
                "str" => $"{field} = '{src.V}'",
                "num" or "ref" or "fig" when src.V is not null => $"{field} = {src.V}",
                _ => Regex.Replace(s.Text, @"\s+", " ").Trim()
            };
        return Regex.Replace(s.Text, @"\s+", " ").Trim();
    }
}
