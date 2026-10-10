using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using MlCpu.Cobol.Antlr.Generated;

namespace MlCpu.Cobol.Antlr;

/// <summary>
/// The COBOL-85 parse tree as JSON (mlcpu.cobol85-ast.v1) and as an outline. Single-child rule chains collapse into their
/// outermost rule (moveToSendingArea → identifier → … → IDENTIFIER becomes one node carrying the token), so the tree keeps
/// every decision the grammar made without the wrapper noise.
/// </summary>
public static class AstExport
{
    public const string Schema = "mlcpu.cobol85-ast.v1";
    static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public static JsonObject ToJson(AntlrParse p)
    {
        int nodes = 0;
        var root = new JsonObject
        {
            ["schema"] = Schema,
            ["programId"] = p.ProgramId,
            ["source"] = System.IO.Path.GetFileName(p.Source.Path),
            ["grammar"] = CobolAntlr.Grammar,
            ["mode"] = p.Mode,
            ["parseMs"] = (long)p.Elapsed.TotalMilliseconds,
            ["ok"] = p.Ok,
            ["errors"] = new JsonArray(p.Errors.Select(e => (JsonNode?)e).ToArray()),
            ["legend"] = "r = grammar rule (outer>inner when a single-child chain collapsed), t = token text, l = program line, o = copybook, c = children",
        };
        if (p.Tree is not null) root["tree"] = Node(p.Tree, p, ref nodes);
        root["nodes"] = nodes;
        return root;
    }

    public static string ToJsonText(AntlrParse p) => ToJson(p).ToJsonString(Compact);

    static JsonNode Node(IParseTree t, AntlrParse p, ref int nodes)
    {
        nodes++;
        if (t is ITerminalNode term)
        {
            var leaf = new JsonObject { ["t"] = term.Symbol.Text, ["l"] = p.LineOf(term.Symbol) };
            var o = p.OriginOf(term.Symbol);
            if (o.Length > 0) leaf["o"] = o;
            return leaf;
        }
        var ctx = (ParserRuleContext)t;
        var rule = Cobol85Parser.ruleNames[ctx.RuleIndex];
        // Collapse single-child chains down to a token or to the first rule that branches.
        IParseTree inner = ctx;
        while (inner is ParserRuleContext r && r.ChildCount == 1) inner = r.GetChild(0);
        var obj = new JsonObject { ["r"] = rule };
        if (ctx.Start is { } start)
        {
            obj["l"] = p.LineOf(start);
            var o = p.OriginOf(start);
            if (o.Length > 0) obj["o"] = o;
        }
        if (inner is ITerminalNode leafToken)
        {
            obj["t"] = leafToken.Symbol.Text;
            return obj;
        }
        var branch = (ParserRuleContext)inner;
        if (!ReferenceEquals(branch, ctx)) obj["r"] = rule + ">" + Cobol85Parser.ruleNames[branch.RuleIndex];
        var children = new JsonArray();
        for (int i = 0; i < branch.ChildCount; i++)
        {
            var c = branch.GetChild(i);
            if (c is ITerminalNode tn && tn.Symbol.Type == TokenConstants.EOF) continue;
            children.Add(Node(c, p, ref nodes));
        }
        obj["c"] = children;
        return obj;
    }

    // ------------------------------------------------------------------ outline

    static readonly HashSet<string> BranchRules = new(StringComparer.Ordinal)
    {
        "ifElse", "evaluateWhenPhrase", "evaluateWhenOther", "atEndPhrase", "notAtEndPhrase", "invalidKeyPhrase", "notInvalidKeyPhrase",
        "onOverflowPhrase", "notOnOverflowPhrase", "onSizeErrorPhrase", "notOnSizeErrorPhrase", "onExceptionClause", "notOnExceptionClause",
        "searchWhen", "atEndOfPagePhrase", "notAtEndOfPagePhrase"
    };

    /// <summary>
    /// A compact, structured view of the program from its parse tree: the data division (levels indented, PICTURE, USAGE,
    /// VALUE, OCCURS, REDEFINES as written, copybook noted) and the procedure division (paragraphs; statements with their
    /// nested statements indented under IF/ELSE, EVALUATE/WHEN, AT END, INVALID KEY, inline PERFORM). Program line numbers
    /// prefix every line; literals keep their exact spacing.
    /// </summary>
    public static string Outline(AntlrParse p)
    {
        if (p.Tree is null) return $"* {p.ProgramId}: no parse tree ({string.Join("; ", p.Errors.Take(3))})\n";
        var sb = new StringBuilder();
        sb.Append($"* {p.ProgramId}: COBOL-85 AST outline ({CobolAntlr.Grammar}); L<n> = program line, [CPY] = record from copybook CPY\n");
        foreach (var unit in Descendants(p.Tree).OfType<Cobol85Parser.ProgramUnitContext>())
        {
            if (unit.dataDivision() is { } data) DataOutline(data, p, sb);
            if (unit.procedureDivision() is { } proc) ProcedureOutline(proc, p, sb);
        }
        return sb.ToString();
    }

    static void DataOutline(Cobol85Parser.DataDivisionContext data, AntlrParse p, StringBuilder sb)
    {
        sb.Append("DATA DIVISION.\n");
        foreach (var section in data.dataDivisionSection())
        {
            if (section.GetChild(0) is not ParserRuleContext header) continue;
            if (header is Cobol85Parser.FileSectionContext fs)
            {
                sb.Append("  FILE SECTION.\n");
                foreach (var fd in fs.fileDescriptionEntry())
                {
                    var head = new StringBuilder();
                    for (int i = 0; i < fd.ChildCount; i++)
                    {
                        if (fd.GetChild(i) is Cobol85Parser.DataDescriptionEntryContext) break;
                        AppendToken(head, Text(fd.GetChild(i), p));
                    }
                    sb.Append("  L").Append(p.LineOf(fd.Start)).Append(' ').Append(head).Append('\n');
                    Entries(fd.dataDescriptionEntry(), p, sb, "    ");
                }
                continue;
            }
            var name = Cobol85Parser.ruleNames[header.RuleIndex] switch
            {
                "workingStorageSection" => "WORKING-STORAGE SECTION",
                "linkageSection" => "LINKAGE SECTION",
                "localStorageSection" => "LOCAL-STORAGE SECTION",
                var other => other
            };
            sb.Append("  ").Append(name).Append(".\n");
            Entries(Descendants(header).OfType<Cobol85Parser.DataDescriptionEntryContext>(), p, sb, "    ");
        }
    }

    static void Entries(IEnumerable<Cobol85Parser.DataDescriptionEntryContext> entries, AntlrParse p, StringBuilder sb, string indent)
    {
        var levels = new Stack<int>();
        foreach (var e in entries)
        {
            var text = Text(e, p);
            int level = int.TryParse(text.Split(' ', 2)[0], out var lv) ? lv : 1;
            if (level is 1 or 77 or 66) levels.Clear();
            if (level != 88) while (levels.Count > 0 && levels.Peek() >= level) levels.Pop();
            int depth = levels.Count;
            if (level is not (88 or 66 or 77)) levels.Push(level);
            var origin = p.OriginOf(e.Start);
            var note = origin.Length > 0 && level == 1 ? $"   [{origin}]" : "";
            sb.Append(indent).Append('L').Append(p.LineOf(e.Start)).Append(' ').Append(new string(' ', 2 * depth)).Append(text).Append(note).Append('\n');
        }
    }

    static void ProcedureOutline(Cobol85Parser.ProcedureDivisionContext proc, AntlrParse p, StringBuilder sb)
    {
        var header = new StringBuilder();
        for (int i = 0; i < proc.ChildCount; i++)
        {
            if (proc.GetChild(i) is Cobol85Parser.ProcedureDivisionBodyContext) break;
            AppendToken(header, Text(proc.GetChild(i), p));
        }
        sb.Append('L').Append(p.LineOf(proc.Start)).Append(' ').Append(header).Append('\n');
        var body = proc.procedureDivisionBody();
        if (body is null) return;
        Paragraphs(body.paragraphs(), p, sb);
        foreach (var section in body.procedureSection())
        {
            sb.Append('L').Append(p.LineOf(section.Start)).Append(' ').Append(Text(section.procedureSectionHeader(), p)).Append(".\n");
            Paragraphs(section.paragraphs(), p, sb);
        }
    }

    static void Paragraphs(Cobol85Parser.ParagraphsContext ps, AntlrParse p, StringBuilder sb)
    {
        foreach (var s in ps.sentence()) Sentence(s, p, sb, 1);
        foreach (var para in ps.paragraph())
        {
            sb.Append('L').Append(p.LineOf(para.Start)).Append(' ').Append(para.paragraphName().GetText()).Append(".\n");
            if (para.alteredGoTo() is { } alt) sb.Append('L').Append(p.LineOf(alt.Start)).Append("   ").Append(Text(alt, p)).Append('\n');
            foreach (var s in para.sentence()) Sentence(s, p, sb, 1);
        }
    }

    static void Sentence(Cobol85Parser.SentenceContext s, AntlrParse p, StringBuilder sb, int depth)
    {
        foreach (var st in s.statement()) Statement(st, p, sb, depth);
    }

    /// <summary>A statement's own tokens on one line; nested statements indented below it; branch phrases (ELSE, WHEN, AT END…) and END-x on their own lines.</summary>
    static void Statement(ParserRuleContext st, AntlrParse p, StringBuilder sb, int depth)
    {
        var line = new StringBuilder();
        int lineNo = p.LineOf(st.Start);
        void Flush(int d)
        {
            var t = line.ToString().Trim();
            if (t.Length > 0) sb.Append('L').Append(lineNo).Append(' ').Append(new string(' ', 2 * d)).Append(t).Append('\n');
            line.Clear();
        }
        void Walk(IParseTree node, int d)
        {
            if (node is Cobol85Parser.StatementContext nested && !ReferenceEquals(nested, st))
            {
                Flush(Math.Max(depth, d - 1));
                Statement(nested, p, sb, d);
                return;
            }
            if (node is ITerminalNode term)
            {
                if (term.Symbol.Type == TokenConstants.EOF) return;
                if (term.Symbol.Text.StartsWith("END-", StringComparison.OrdinalIgnoreCase))
                {
                    Flush(d - 1);
                    lineNo = p.LineOf(term.Symbol);
                    line.Append(term.Symbol.Text);
                    Flush(d - 1);
                    return;
                }
                if (line.Length == 0) lineNo = p.LineOf(term.Symbol);
                AppendToken(line, term.Symbol.Text);
                return;
            }
            var ctx = (ParserRuleContext)node;
            if (BranchRules.Contains(Cobol85Parser.ruleNames[ctx.RuleIndex])) Flush(d - 1);
            for (int i = 0; i < ctx.ChildCount; i++) Walk(ctx.GetChild(i), d);
        }
        for (int i = 0; i < st.ChildCount; i++) Walk(st.GetChild(i), depth + 1);
        Flush(depth);
    }

    /// <summary>A rule's tokens (default channel) joined by single spaces; literal text is never altered.</summary>
    static string Text(IParseTree t, AntlrParse p)
    {
        if (t is ITerminalNode term) return term.Symbol.Type == TokenConstants.EOF ? "" : term.Symbol.Text.Trim();
        var ctx = (ParserRuleContext)t;
        if (ctx.Start is null || ctx.Stop is null || ctx.Stop.TokenIndex < ctx.Start.TokenIndex) return "";
        var sb = new StringBuilder();
        for (int i = ctx.Start.TokenIndex; i <= ctx.Stop.TokenIndex; i++)
        {
            var tok = p.Tokens.Get(i);
            if (tok.Channel != TokenConstants.DefaultChannel || tok.Type == TokenConstants.EOF) continue;
            AppendToken(sb, tok.Text);
        }
        return sb.ToString();
    }

    /// <summary>Appends a token with one separating space, tightening "X ( 5 )" to "X(5)" and "NAME ." to "NAME.".</summary>
    static void AppendToken(StringBuilder sb, string text)
    {
        if (!(text.StartsWith('\'') || text.StartsWith('"'))) text = text.Trim();
        else text = text.TrimEnd('\r', '\n');
        if (text.Length == 0) return;
        bool noSpace = sb.Length == 0 || sb[^1] == '(' || text is ")" or "." or "," || (text == "(" && char.IsLetterOrDigit(sb[^1]));
        if (!noSpace && sb[^1] != ' ') sb.Append(' ');
        sb.Append(text);
    }

    static IEnumerable<IParseTree> Descendants(IParseTree t)
    {
        var stack = new Stack<IParseTree>();
        stack.Push(t);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            for (int i = n.ChildCount - 1; i >= 0; i--) stack.Push(n.GetChild(i));
        }
    }
}
