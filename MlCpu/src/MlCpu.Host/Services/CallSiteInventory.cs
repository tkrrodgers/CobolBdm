using System.Text.RegularExpressions;
using MlCpu.Cobol.Analysis;

namespace MlCpu.Host.Services;

/// <summary>
/// The composed lane's CALL/CANCEL inventory (storage-composition.json <c>callSites</c>): each site with its argument modes and
/// a fail-closed status. Only forms with a proven composed-lane behavior are <c>supported</c>; BY VALUE (no proven target
/// ABI, and not BY CONTENT) is <c>blocked</c>; CANCEL is <c>unavailable</c> unless the program lowers it, and even then
/// <c>unverified</c> until fresh GnuCOBOL CANCEL conformance exists. The inventory records; it does not change emission.
/// </summary>
public static partial class CallSiteInventory
{
    [GeneratedRegex(@"^\s*CANCEL\s+(?<targets>.*?)\.?\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex CancelRx();

    public static List<object> Build(CobolProgram model, ProgramGraph? graph, string? javaSource)
    {
        var resolved = graph?.Calls.ToDictionary(c => c.StepId, StringComparer.Ordinal) ?? [];
        var cancelLowered = javaSource?.Contains("CobRt.cancel(", StringComparison.Ordinal) == true;
        var sites = new List<object>();
        foreach (var p in model.Paragraphs)
            foreach (var s in Flatten(p.Statements))
            {
                if (s.Verb == "CALL") sites.Add(Call(s, p.Name, resolved.GetValueOrDefault(s.StepId)));
                else if (s.Verb == "CANCEL") sites.Add(Cancel(s, p.Name, cancelLowered));
            }
        return sites;
    }

    static object Call(Stmt s, string paragraph, CallSite? site)
    {
        var args = (s.L?.GetValueOrDefault("args") ?? []).Select((a, i) => new
        {
            position = i + 1,
            mode = a.K == "omitted" ? "OMITTED" : a.Op ?? "REFERENCE",
            operand = a.K == "omitted" ? null : Operand(a.L)
        }).ToList();
        var reasons = new List<string>();
        var status = "supported";
        if (args.Any(a => a.mode == "VALUE"))
        {
            status = "blocked";
            reasons.Add("BY VALUE: no proven composed-lane target ABI; it must not be treated as BY CONTENT");
        }
        if (args.Any(a => a.mode == "OMITTED"))
        {
            if (status == "supported") status = "unverified";
            reasons.Add("OMITTED argument: composed-lane behavior not characterized");
        }
        var dynamic = site?.Kind == "dynamic" || s.Get("target")?.K != "str";
        if (dynamic && (site?.ResolvedTargets.Count ?? 0) == 0)
        {
            if (status == "supported") status = "unverified";
            reasons.Add("data-name target without a proven candidate set: resolved at run time only");
        }
        if (args.Count(a => a.mode == "REFERENCE") > 1)
            reasons.Add("several BY REFERENCE arguments: overlap is preserved only by the alias-aware view ABI, not by the byte[][] ABI");
        return new
        {
            verb = "CALL",
            stepId = s.StepId,
            paragraph,
            line = s.Line,
            target = site?.Target ?? Operand(s.Get("target")),
            targetKind = dynamic ? "dynamic" : "static",
            candidates = site?.ResolvedTargets ?? [],
            arguments = args,
            returning = Operand(s.Get("returning")),
            onException = s.Blocks?.ContainsKey("EXCEPTION") == true,
            notOnException = s.Blocks?.ContainsKey("NOT-EXCEPTION") == true,
            composedLane = new { status, reasons }
        };
    }

    static object Cancel(Stmt s, string paragraph, bool lowered) => new
    {
        verb = "CANCEL",
        stepId = s.StepId,
        paragraph,
        line = s.Line,
        targets = CancelRx().Match(s.Text) is { Success: true } m
            ? m.Groups["targets"].Value.Split((char[])[' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries).ToList() : new List<string>(),
        composedLane = lowered
            ? new { status = "unverified", reasons = new List<string> { "lowered to CobRt.cancel; fresh GnuCOBOL CANCEL conformance not established" } }
            : new { status = "unavailable", reasons = new List<string> { "CANCEL is not lowered by the composed lane" } }
    };

    static string? Operand(Node? n) => n is null ? null : n.K switch
    {
        "str" => "'" + n.V + "'",
        "ref" => n.V,
        _ => n.V ?? n.K
    };

    static IEnumerable<Stmt> Flatten(IEnumerable<Stmt> list)
    {
        foreach (var s in list)
        {
            yield return s;
            if (s.Blocks is not null) foreach (var b in s.Blocks.Values) foreach (var x in Flatten(b)) yield return x;
        }
    }
}
