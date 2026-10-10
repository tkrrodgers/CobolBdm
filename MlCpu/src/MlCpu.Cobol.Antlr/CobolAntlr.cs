using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Antlr4.Runtime;
using Antlr4.Runtime.Atn;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using MlCpu.Cobol.Analysis;
using MlCpu.Cobol.Antlr.Generated;
using MlCpu.CobolKb;

namespace MlCpu.Cobol.Antlr;

/// <summary>
/// Source as the COBOL-85 grammar expects it (the ProLeap preprocessor contract): sequence and indicator areas removed,
/// continuations folded, COPY … REPLACING expanded (the analyser's own front end), comment entries tagged <c>*&gt;CE</c> and
/// EXEC SQL / EXEC CICS blocks tagged <c>*&gt;EXECSQL</c> / <c>*&gt;EXECCICS</c>. <see cref="ProgramLines"/>[i] is the
/// program line (1-based) the i-th preprocessed line came from; <see cref="Origins"/>[i] its copybook ("" = the program).
/// </summary>
public sealed record PreprocessedSource(string Path, string Text, IReadOnlyList<int> ProgramLines, IReadOnlyList<string> Origins, IReadOnlyList<string> MissingCopybooks);

/// <summary>One parse: the tree, the token stream and every syntax error (line numbers are program lines).</summary>
public sealed record AntlrParse(string ProgramId, PreprocessedSource Source, Cobol85Parser.StartRuleContext? Tree, CommonTokenStream Tokens,
    IReadOnlyList<string> Errors, string Mode, TimeSpan Elapsed, bool TimedOut)
{
    public bool Ok => Tree is not null && Errors.Count == 0 && !TimedOut;

    /// <summary>Program line of a token of the preprocessed text.</summary>
    public int LineOf(IToken t) => t.Line >= 1 && t.Line <= Source.ProgramLines.Count ? Source.ProgramLines[t.Line - 1] : t.Line;

    public string OriginOf(IToken t) => t.Line >= 1 && t.Line <= Source.Origins.Count ? Source.Origins[t.Line - 1] : "";
}

/// <summary>COBOL-85 parsing with the grammars-v4 grammar (ANTLR 4.13.1): SLL first, full LL only when SLL reports an error.</summary>
public static partial class CobolAntlr
{
    public const string Grammar = "grammars-v4 cobol85/Cobol85.g4 (ProLeap COBOL 85, ANTLR 4.13.1)";
    static readonly Encoding Latin1 = Encoding.Latin1;

    [GeneratedRegex(@"^\s*(IDENTIFICATION|ID)\s+DIVISION\b", RegexOptions.IgnoreCase)] private static partial Regex IdDivRx();
    [GeneratedRegex(@"^\s*(ENVIRONMENT|DATA|PROCEDURE)\s+DIVISION\b", RegexOptions.IgnoreCase)] private static partial Regex OtherDivRx();
    [GeneratedRegex(@"^(\s*)(AUTHOR|INSTALLATION|DATE-WRITTEN|DATE-COMPILED|SECURITY|REMARKS)\s*\.(.*)$", RegexOptions.IgnoreCase)] private static partial Regex CommentParaRx();
    [GeneratedRegex(@"^\s*(PROGRAM-ID|AUTHOR|INSTALLATION|DATE-WRITTEN|DATE-COMPILED|SECURITY|REMARKS)\b", RegexOptions.IgnoreCase)] private static partial Regex IdParaRx();
    [GeneratedRegex(@"\bEXEC\s+(SQLIMS|SQL|CICS|DLI)\b", RegexOptions.IgnoreCase)] private static partial Regex ExecRx();
    [GeneratedRegex(@"\bEND-EXEC\b", RegexOptions.IgnoreCase)] private static partial Regex EndExecRx();

    public static PreprocessedSource Preprocess(string path)
    {
        var raw = File.ReadAllText(CobolModelBuilder.PreferExpanded(path), Latin1);
        var missing = new List<MissingCopybook>();
        var lines = CobolParser.ExpandCopies(CobolParser.Strip(raw), CobolModelBuilder.CopybookResolver(path), missing);
        var sb = new StringBuilder();
        var map = new List<int>();
        var origins = new List<string>();
        void Emit(string text, SourceLine from)
        {
            if (!text.StartsWith("*>", StringComparison.Ordinal)) text = SeparatorCommas(text);
            sb.Append(text).Append('\n');
            map.Add(from.ProgramLine);
            origins.Add(from.Origin);
        }
        bool inId = false, inCommentEntry = false;
        string? execTag = null;
        foreach (var l in lines)
        {
            var text = l.Text;
            if (execTag is not null)
            {
                var end = EndExecRx().Match(text);
                if (!end.Success) { if (text.Trim().Length > 0) Emit($"*>{execTag} {text.Trim()}", l); continue; }
                Emit($"*>{execTag} {text[..(end.Index + end.Length)].Trim()}", l);
                execTag = null;
                var rest = text[(end.Index + end.Length)..];
                if (rest.Trim().Length > 0) Emit(rest, l);
                continue;
            }
            if (IdDivRx().IsMatch(text)) { inId = true; inCommentEntry = false; Emit(text, l); continue; }
            if (OtherDivRx().IsMatch(text)) { inId = false; inCommentEntry = false; }
            if (inId)
            {
                var cp = CommentParaRx().Match(text);
                if (cp.Success)
                {
                    Emit(cp.Groups[1].Value + cp.Groups[2].Value + ".", l);
                    if (cp.Groups[3].Value.Trim().Length > 0) Emit("*>CE " + cp.Groups[3].Value.Trim(), l);
                    inCommentEntry = true;
                    continue;
                }
                if (inCommentEntry)
                {
                    if (IdParaRx().IsMatch(text)) inCommentEntry = false;
                    else { if (text.Trim().Length > 0) Emit("*>CE " + text.Trim(), l); continue; }
                }
            }
            var exec = ExecRx().Match(text);
            if (exec.Success && !InQuotes(text, exec.Index))
            {
                var tag = exec.Groups[1].Value.ToUpperInvariant() switch { "CICS" => "EXECCICS", "SQLIMS" or "DLI" => "EXECSQLIMS", _ => "EXECSQL" };
                if (exec.Index > 0 && text[..exec.Index].Trim().Length > 0) Emit(text[..exec.Index], l);
                var body = text[exec.Index..];
                var end = EndExecRx().Match(body);
                if (end.Success)
                {
                    Emit($"*>{tag} {body[..(end.Index + end.Length)].Trim()}", l);
                    var rest = body[(end.Index + end.Length)..];
                    if (rest.Trim().Length > 0) Emit(rest, l);
                }
                else
                {
                    Emit($"*>{tag} {body.Trim()}", l);
                    execTag = tag;
                }
                continue;
            }
            Emit(text, l);
        }
        return new PreprocessedSource(path, sb.ToString(), map, origins, missing.Select(m => m.Member).Distinct().ToList());
    }

    /// <summary>A comma followed by a space or the end of the line is a separator (like a space); the grammar only hides ", ".</summary>
    static string SeparatorCommas(string s)
    {
        if (s.IndexOf(',') < 0) return s;
        var chars = s.ToCharArray();
        char q = '\0';
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (q != '\0') { if (c == q) q = '\0'; continue; }
            if (c is '\'' or '"') { q = c; continue; }
            if (c == ',' && (i + 1 == chars.Length || char.IsWhiteSpace(chars[i + 1]))) chars[i] = ' ';
        }
        return new string(chars);
    }

    static bool InQuotes(string s, int at)
    {
        char q = '\0';
        for (int i = 0; i < at && i < s.Length; i++)
        {
            if (q == '\0') { if (s[i] is '\'' or '"') q = s[i]; }
            else if (s[i] == q) q = '\0';
        }
        return q != '\0';
    }

    /// <summary>Parses one program. <paramref name="timeout"/> bounds the whole parse (the grammar can be slow on large programs).</summary>
    public static AntlrParse Parse(string path, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        var src = Preprocess(path);
        var programId = CobolParser.FindProgramId(src.Text.Split('\n')) ?? System.IO.Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
        var deadline = timeout ?? TimeSpan.FromMinutes(2);
        var lexer = new Cobol85Lexer(new AntlrInputStream(src.Text));
        lexer.RemoveErrorListeners();
        var lexErrors = new ErrorCollector(src);
        lexer.AddErrorListener(lexErrors);
        var tokens = new CommonTokenStream(lexer);
        tokens.Fill();
        var parser = new Cobol85Parser(tokens) { BuildParseTree = true };
        parser.RemoveErrorListeners();
        parser.Interpreter.PredictionMode = PredictionMode.SLL;
        parser.ErrorHandler = new BailErrorStrategy();
        var watchdog = new Watchdog(sw, deadline);
        parser.AddParseListener(watchdog);
        Cobol85Parser.StartRuleContext? tree = null;
        var errors = new List<string>();
        string mode = "SLL";
        bool timedOut = false;
        try
        {
            tree = parser.startRule();
        }
        catch (ParseCanceledException)
        {
            // SLL could not decide: rewind and parse again with full LL and error recovery, collecting every error.
            mode = "LL";
            tokens.Seek(0);
            parser.Reset();
            parser.Interpreter.PredictionMode = PredictionMode.LL;
            parser.ErrorHandler = new DefaultErrorStrategy();
            var collector = new ErrorCollector(src);
            parser.AddErrorListener(collector);
            try
            {
                tree = parser.startRule();
                errors.AddRange(collector.Errors);
            }
            catch (TimeoutException)
            {
                timedOut = true;
                errors.Add($"parse stopped after {deadline.TotalSeconds:N0}s");
            }
        }
        catch (TimeoutException)
        {
            timedOut = true;
            errors.Add($"parse stopped after {deadline.TotalSeconds:N0}s");
        }
        errors.InsertRange(0, lexErrors.Errors);
        foreach (var m in src.MissingCopybooks) errors.Add($"missing copybook {m}");
        return new AntlrParse(programId, src, timedOut ? null : tree, tokens, errors, mode, sw.Elapsed, timedOut);
    }

    sealed class Watchdog(Stopwatch sw, TimeSpan limit) : IParseTreeListener
    {
        int n;
        public void EnterEveryRule(ParserRuleContext ctx)
        {
            if ((++n & 0x3FF) == 0 && sw.Elapsed > limit) throw new TimeoutException();
        }
        public void ExitEveryRule(ParserRuleContext ctx) { }
        public void VisitErrorNode(IErrorNode node) { }
        public void VisitTerminal(ITerminalNode node) { }
    }

    sealed class ErrorCollector(PreprocessedSource src) : IAntlrErrorListener<IToken>, IAntlrErrorListener<int>
    {
        public List<string> Errors { get; } = [];
        int Line(int l) => l >= 1 && l <= src.ProgramLines.Count ? src.ProgramLines[l - 1] : l;

        public void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e)
        {
            if (Errors.Count < 200) Errors.Add($"L{Line(line)}: {msg}");
        }

        public void SyntaxError(TextWriter output, IRecognizer recognizer, int offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e)
        {
            if (Errors.Count < 200) Errors.Add($"L{Line(line)}: {msg}");
        }
    }
}
