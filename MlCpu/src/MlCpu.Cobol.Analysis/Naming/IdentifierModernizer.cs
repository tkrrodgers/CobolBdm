using System.Text;

namespace MlCpu.Cobol.Analysis.Naming;

/// <summary>Display-only modern names (§5.4.9). COBOL names stay the keys of ProgramState.</summary>
public static class IdentifierModernizer
{
    // Seed copied from AbbreviationMiner.Seed (CobolToJavaAnswers Q8).
    static readonly Dictionary<string, string> Seed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WS"] = "working-storage", ["LS"] = "linkage", ["LK"] = "linkage", ["FD"] = "file-description",
        ["SW"] = "switch", ["FL"] = "flag", ["W"] = "work",
        ["QY"] = "quantity", ["QTY"] = "quantity", ["DT"] = "date", ["DTE"] = "date", ["TM"] = "time",
        ["TS"] = "timestamp", ["CD"] = "code", ["CDE"] = "code", ["AMT"] = "amount", ["BAL"] = "balance",
        ["NBR"] = "number", ["NO"] = "number", ["NUM"] = "number", ["CNT"] = "count", ["CTR"] = "counter",
        ["ID"] = "identifier", ["IND"] = "indicator", ["FLG"] = "flag", ["MSG"] = "message", ["HDR"] = "header",
        ["DTL"] = "detail", ["REC"] = "record", ["TBL"] = "table", ["PARM"] = "parameter", ["ADDR"] = "address",
        ["PCT"] = "percent", ["PGM"] = "program", ["RTN"] = "routine", ["RTNE"] = "routine", ["PROC"] = "procedure", ["ERR"] = "error", ["EOF"] = "end-of-file", ["EOJ"] = "end-of-job",
        ["FMT"] = "format", ["FMTR"] = "formatter", ["PREV"] = "previous", ["CURR"] = "current", ["TOT"] = "total",
        ["MAX"] = "maximum", ["MIN"] = "minimum", ["DY"] = "day", ["SEQ"] = "sequence", ["STAT"] = "status", ["STA"] = "status",
        ["YR"] = "year", ["MO"] = "month",
        ["CHKPT"] = "checkpoint", ["CKPT"] = "checkpoint", ["RST"] = "restart", ["RERUN"] = "rerun",
        ["LUW"] = "logical-unit-of-work", ["COMIT"] = "commit", ["CMT"] = "commit", ["ROLLBK"] = "rollback",
        ["TRAN"] = "transaction", ["TRD"] = "trade", ["TRDE"] = "trade", ["ISS"] = "issue", ["FUND"] = "fund",
        ["CUST"] = "customer", ["ACCT"] = "account", ["ACCTG"] = "accounting", ["GL"] = "general-ledger",
        ["CTL"] = "control", ["CNTL"] = "control", ["PUB"] = "publisher", ["PUBL"] = "publisher",
        ["REGI"] = "registry", ["REG"] = "registry", ["DB2"] = "db2", ["SQL"] = "sql", ["CICS"] = "cics",
        ["ATS"] = "automated-trade-settlement", ["FFI"] = "foreign-financial-institution", ["BRO"] = "broker",
        ["CALC"] = "calculate", ["CAL"] = "calendar", ["JBN"] = "job-name", ["ABEND"] = "abnormal-end",
    };

    static readonly HashSet<string> ScopePhrases = new(StringComparer.OrdinalIgnoreCase) { "working", "storage", "linkage", "file", "description" };

    /// <summary>Split on '-'/'_', expand known tokens, drop leading scope phrases, camelCase.</summary>
    public static string Modernize(string cobolName)
    {
        var words = new List<string>();
        foreach (var tok in cobolName.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries))
        {
            var expanded = Seed.TryGetValue(tok, out var e) ? e : tok.ToLowerInvariant();
            words.AddRange(expanded.Split('-', StringSplitOptions.RemoveEmptyEntries));
        }
        while (words.Count > 1 && ScopePhrases.Contains(words[0])) words.RemoveAt(0);
        var sb = new StringBuilder();
        foreach (var w in words)
        {
            var clean = new string(w.Where(char.IsLetterOrDigit).ToArray());
            if (clean.Length == 0) continue;
            sb.Append(sb.Length == 0 ? clean.ToLowerInvariant() : char.ToUpperInvariant(clean[0]) + clean.Substring(1).ToLowerInvariant());
        }
        var s = sb.ToString();
        if (s.Length == 0 || char.IsDigit(s[0])) s = "f" + s;
        return s;
    }
}
