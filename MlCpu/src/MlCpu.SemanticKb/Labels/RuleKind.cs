using System.Text.RegularExpressions;

namespace MlCpu.SemanticKb.Labels;

/// <summary>
/// The business-rule kind of a paragraph or rule, derived deterministically from its COBOL statements (the AST outline's
/// statement lines or the requirements packet's statements). Each statement adds weight to one or more kinds; the
/// heaviest kind wins, ties in the order of <see cref="Order"/>. Weights are documented in knowledgeBaseGroundGemma4.md §8.
/// </summary>
public static partial class RuleKind
{
    public static readonly string[] Order =
        ["rounding", "date-time", "calculation", "aggregation", "validation", "lookup", "formatting", "io", "error-handling", "decision", "transformation", "control"];

    public sealed record Result(string Kind, double Confidence, IReadOnlyDictionary<string, double> Scores);

    public static Result Classify(IEnumerable<string> statements)
    {
        var s = Order.ToDictionary(k => k, _ => 0.0);
        var list = statements.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        bool hasCondition = list.Any(x => x.StartsWith("IF ") || x.StartsWith("EVALUATE ") || x.StartsWith("WHEN "));
        foreach (var st in list)
        {
            var verb = st.Split(' ', 2)[0];
            var rest = st.Length > verb.Length ? st[(verb.Length + 1)..] : "";
            switch (verb)
            {
                case "COMPUTE" or "MULTIPLY" or "DIVIDE":
                    s["calculation"] += 2;
                    if (st.Contains(" ROUNDED")) s["rounding"] += 2;
                    if (DateWords().IsMatch(st)) s["date-time"] += 1.5;
                    break;
                case "ADD" or "SUBTRACT":
                    if (Counter().IsMatch(st)) s["aggregation"] += 1.5; else s["calculation"] += 1.5;
                    if (DateWords().IsMatch(st)) s["date-time"] += 1;
                    break;
                case "IF" or "EVALUATE" or "WHEN":
                    s["decision"] += 0.5;
                    if (DateWords().IsMatch(st)) s["date-time"] += 0.3;
                    break;
                case "READ" or "START":
                    if (st.Contains(" KEY") || st.Contains("INVALID")) s["lookup"] += 2; else s["io"] += 1;
                    break;
                case "SEARCH":
                    s["lookup"] += 2;
                    break;
                case "OPEN" or "CLOSE" or "WRITE" or "REWRITE" or "DELETE" or "RETURN" or "RELEASE":
                    s["io"] += 1;
                    if (verb == "WRITE" && ReportWords().IsMatch(rest)) s["formatting"] += 1;
                    break;
                case "STRING" or "UNSTRING":
                    s["formatting"] += 1;
                    break;
                case "INSPECT" or "INITIALIZE":
                    s["transformation"] += 0.5;
                    break;
                case "MOVE" or "SET":
                    if (RejectTarget().IsMatch(rest) && hasCondition) s["validation"] += 1.5;
                    else if (st.Contains("RETURN-CODE")) s["error-handling"] += 1.5;
                    else s["transformation"] += 0.5;
                    if (RoundWords().IsMatch(rest)) s["rounding"] += 0.5;
                    break;
                case "DISPLAY":
                    if (ErrorWords().IsMatch(rest)) s["error-handling"] += 1.5; else s["formatting"] += 0.3;
                    break;
                case "ACCEPT":
                    if (rest.Contains("DATE") || rest.Contains("TIME") || rest.Contains("DAY")) s["date-time"] += 1.5;
                    break;
                case "CALL":
                    if (rest.Contains("CEE3ABD")) s["error-handling"] += 2;
                    else if (rest.Contains("CEEDAYS") || DateWords().IsMatch(rest)) s["date-time"] += 1;
                    else s["control"] += 0.5;
                    break;
                case "PERFORM" or "GO" or "EXIT" or "CONTINUE" or "GOBACK" or "STOP":
                    s["control"] += 0.3;
                    if (st.Contains("ABEND")) s["error-handling"] += 1.5;
                    if (RoundWords().IsMatch(rest)) s["rounding"] += 1;
                    break;
            }
            if (st.Contains("FUNCTION CURRENT-DATE") || st.Contains("INTEGER-OF-DATE") || st.Contains("DATE-OF-INTEGER")) s["date-time"] += 1.5;
        }
        var best = Order.OrderByDescending(k => s[k]).ThenBy(k => Array.IndexOf(Order, k)).First();
        var total = s.Values.Sum();
        if (s[best] < 0.5) return new Result("control", total == 0 ? 1 : 0.5, s);
        return new Result(best, total == 0 ? 0 : Math.Round(s[best] / total, 3), s);
    }

    [GeneratedRegex(@"(?:^|[\s(])(?:1|\+1)\s+TO\s|COUNT|CNT\b|-CTR\b|TOTAL|TOT\b|TOT-|-TOT|ACCUM|-SUM\b|-NUM\b|SEQ\b", RegexOptions.CultureInvariant)]
    private static partial Regex Counter();
    [GeneratedRegex(@"DATE|DAYS?\b|-DAY|YEAR|MONTH|LEAP|\bDIY\b|DUE-|WEEK|YYYY|CCYY", RegexOptions.CultureInvariant)]
    private static partial Regex DateWords();
    [GeneratedRegex(@"REJECT|ERROR|ERR\b|ERR-|FAIL|INVALID|REASON|-MSG|MESSAGE|ST-RC|RJ-|BAD-|NOT-OK|-RC\b", RegexOptions.CultureInvariant)]
    private static partial Regex RejectTarget();
    [GeneratedRegex(@"RPT|REPORT|PRINT|LINE|HDR|HEADER|DTL|DETAIL|TRL|TRAILER|TOTAL|HEADING", RegexOptions.CultureInvariant)]
    private static partial Regex ReportWords();
    [GeneratedRegex(@"ERROR|ABEND|FAIL|INVALID|STATUS|NOT FOUND|PROBLEM", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorWords();
    [GeneratedRegex(@"ROUND|RND|HALF|CEILING|FLOOR|SCALE|SIGNIF|PRECISION|-MODE\b", RegexOptions.CultureInvariant)]
    private static partial Regex RoundWords();
}
