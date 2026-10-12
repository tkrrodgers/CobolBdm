namespace MlCpu.Cobol.Analysis;

/// <summary>Recursive-descent PROCEDURE DIVISION parser producing the resolved semantic tree.</summary>
public sealed class ProcedureParser
{
    static readonly HashSet<string> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACCEPT","ADD","ALTER","CALL","CANCEL","CLOSE","COMPUTE","CONTINUE","DELETE","DISPLAY","DIVIDE","ENTRY","EVALUATE",
        "EXEC","EXIT","GO","GOBACK","IF","INITIALIZE","INSPECT","MERGE","MOVE","MULTIPLY","NEXT","OPEN","PERFORM","READ",
        "RELEASE","RETURN","REWRITE","SEARCH","SET","SORT","START","STOP","STRING","SUBTRACT","UNSTRING","WRITE"
    };

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "ELSE","WHEN","THEN","NOT","AND","OR","TO","FROM","BY","INTO","GIVING","REMAINDER","ROUNDED","UNTIL","VARYING","AFTER","BEFORE",
        "TIMES","THRU","THROUGH","USING","RETURNING","INVALID","KEY","AT","END","ON","SIZE","ERROR","EXCEPTION","OVERFLOW","DELIMITED",
        "DELIMITER","COUNT","POINTER","WITH","TALLYING","REPLACING","CONVERTING","ALL","LEADING","FIRST","CHARACTERS","INITIAL","OF","IN",
        "IS","ARE","EQUAL","GREATER","LESS","THAN","NUMERIC","ALPHABETIC","ALPHABETIC-LOWER","ALPHABETIC-UPPER","POSITIVE","NEGATIVE",
        "ZERO","ZEROS","ZEROES","SPACE","SPACES","HIGH-VALUE","HIGH-VALUES","LOW-VALUE","LOW-VALUES","QUOTE","QUOTES","NULL","NULLS",
        "TRUE","FALSE","ALSO","OTHER","ANY","UPON","ADVANCING","LINE","LINES","PAGE","RECORD","NEXT","PREVIOUS","INPUT","OUTPUT","I-O",
        "EXTEND","FUNCTION","LENGTH","ADDRESS","CORR","CORRESPONDING","REFERENCE","CONTENT","VALUE","TEST","DEPENDING","SENTENCE",
        "PROGRAM","PARAGRAPH","SECTION","PERFORM","CYCLE","RUN","SIZE","EOP","END-OF-PAGE","UP","DOWN","NO","LOCK","REWIND","OMITTED",
        "DATE","DAY","TIME","DAY-OF-WEEK","YYYYMMDD","YYYYDDD","PROCEED",
        "ALPHANUMERIC","ALPHANUMERIC-EDITED","NUMERIC-EDITED","NATIONAL","NATIONAL-EDITED","DBCS","EGCS","DEFAULT","FILLER","DATA"
    };

    readonly List<Token> _t;
    int _p;
    readonly DataIndex _idx;
    readonly List<string> _diag;
    readonly HashSet<string> _paragraphNames;
    int _sentence;

    public ProcedureParser(List<Token> tokens, DataIndex index, List<string> diagnostics)
    {
        _t = tokens; _idx = index; _diag = diagnostics;
        _paragraphNames = PrescanParagraphs();
    }

    Token Cur => _t[_p];
    Token Peek(int n = 1) => _t[Math.Min(_p + n, _t.Count - 1)];
    bool At(string w) => Cur.Is(w);
    bool AtEnd => Cur.Kind == TokKind.Eof;
    Token Next() { var t = _t[_p]; if (_p < _t.Count - 1) _p++; return t; }
    bool Accept(string w) { if (At(w)) { Next(); return true; } return false; }
    void Expect(string w) { if (!Accept(w)) Fail($"expected {w} but found '{Cur.Text}'"); }

    sealed class ParseError(string msg) : Exception(msg);
    Exception Fail(string msg) => throw new ParseError($"L{Cur.Line}: {msg}");

    HashSet<string> PrescanParagraphs()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _t.Count - 1; i++)
            if (IsLabelAt(i)) names.Add(_t[i].Text);
        return names;
    }

    bool IsLabelAt(int i)
    {
        var t = _t[i];
        if (t.Kind is not (TokKind.Word or TokKind.Number) || Verbs.Contains(t.Text)) return false;
        bool prevBoundary = i == 0 || _t[i - 1].Kind == TokKind.Period;
        if (!prevBoundary) return false;
        if (_t[i + 1].Kind == TokKind.Period) return true;
        return _t[i + 1].Is("SECTION") && _t[Math.Min(i + 2, _t.Count - 1)].Kind == TokKind.Period;
    }

    public List<Paragraph> Parse(out List<string> usingNames)
    {
        usingNames = [];
        // PROCEDURE DIVISION [USING a b ...] [RETURNING x].
        while (!AtEnd && !(At("PROCEDURE") && Peek().Is("DIVISION"))) Next();
        Expect("PROCEDURE"); Expect("DIVISION");
        if (Accept("USING"))
            while (!AtEnd && Cur.Kind != TokKind.Period && !At("RETURNING"))
            {
                if (At("BY")) { Next(); Next(); continue; }
                usingNames.Add(Next().Text.ToUpperInvariant());
            }
        bool returning = At("RETURNING");
        int returningLine = Cur.Line;
        while (!AtEnd && Cur.Kind != TokKind.Period) Next();
        Next();

        var paragraphs = new List<Paragraph>();
        string? section = null;
        Paragraph? current = null;
        bool inDeclaratives = false;
        if (returning)
        {
            // GnuCOBOL 3.3 does not implement a program's RETURNING item ("program RETURNING is not implemented"):
            // the program stops when entered instead of guessing.
            current = new Paragraph { Name = "MAINLINE", Line = returningLine };
            current.Statements.Add(new Stmt { Verb = "RAW", Line = returningLine, Frontier = "PROCEDURE DIVISION RETURNING is not lowered (GnuCOBOL 3.3 does not implement it)", Text = "PROCEDURE DIVISION RETURNING" });
            paragraphs.Add(current);
        }

        while (!AtEnd)
        {
            if (At("DECLARATIVES") && Peek().Kind == TokKind.Period) { Next(); Next(); inDeclaratives = true; continue; }
            if (inDeclaratives && At("END") && Peek().Is("DECLARATIVES"))
            {
                Next(); Next(); if (Cur.Kind == TokKind.Period) Next();
                inDeclaratives = false; section = null; current = null;
                continue;
            }
            if (IsLabelAt(_p))
            {
                var name = Next();
                bool isSection = Accept("SECTION");
                Next(); // period
                if (isSection) section = name.Text.ToUpperInvariant();
                current = new Paragraph { Name = name.Text.ToUpperInvariant(), Section = section, Line = name.Line, IsSection = isSection, Declarative = inDeclaratives };
                paragraphs.Add(current);
                _sentence = 0;
                continue;
            }
            if (inDeclaratives && At("USE") && current is not null) { ParseUse(current); continue; }
            if (current is null)
            {
                current = new Paragraph { Name = "MAINLINE", Line = Cur.Line };
                paragraphs.Add(current);
            }
            ParseSentence(current.Statements);
        }
        foreach (var p in paragraphs) AssignStepIds(p);
        for (int i = 0; i < paragraphs.Count; i++)
            paragraphs[i].EndLine = i + 1 < paragraphs.Count ? paragraphs[i + 1].Line - 1 : (_t.Count > 1 ? _t[^2].Line : paragraphs[i].Line);
        return paragraphs;
    }

    /// <summary>
    /// USE [GLOBAL] AFTER [STANDARD] {ERROR|EXCEPTION} PROCEDURE [ON] {file...|INPUT|OUTPUT|I-O|EXTEND}. Other USE
    /// forms (FOR DEBUGGING, BEFORE REPORTING, LABEL) are reported: their sections never run.
    /// </summary>
    void ParseUse(Paragraph section)
    {
        int start = _p;
        var words = new List<string>();
        Next();
        while (!AtEnd && Cur.Kind != TokKind.Period) words.Add(Next().Upper);
        if (Cur.Kind == TokKind.Period) Next();
        int i = 0;
        if (i < words.Count && words[i] == "GLOBAL") i++;
        if (i < words.Count && words[i] == "AFTER") i++;
        if (i < words.Count && words[i] == "STANDARD") i++;
        if (i >= words.Count || words[i] is not ("ERROR" or "EXCEPTION"))
        {
            _diag.Add($"L{_t[start].Line}: USE {string.Join(" ", words)} is not lowered (its section never runs)");
            return;
        }
        i++;
        if (i < words.Count && words[i] == "PROCEDURE") i++;
        if (i < words.Count && words[i] == "ON") i++;
        var files = new List<string>();
        var modes = new List<string>();
        for (; i < words.Count; i++)
        {
            if (words[i] is "INPUT" or "OUTPUT" or "I-O" or "EXTEND") modes.Add(words[i]);
            else if (_idx.File(words[i]) is not null) files.Add(words[i]);
            else _diag.Add($"L{_t[start].Line}: USE ... ON {words[i]}: not a file or open mode");
        }
        if (files.Count > 0) section.UseFiles = files;
        if (modes.Count > 0) section.UseModes = modes;
    }

    void ParseSentence(List<Stmt> into)
    {
        var stmts = ParseStatements();
        into.AddRange(stmts);
        if (Cur.Kind == TokKind.Period) { Next(); _sentence++; return; }
        if (stmts.Count == 0 && !AtEnd && !IsLabelAt(_p))
        {
            // Unparseable token at sentence level: record it and skip to the next period (never drop silently).
            int start = _p;
            while (!AtEnd && Cur.Kind != TokKind.Period) Next();
            var raw = new Stmt { Verb = "RAW", Line = _t[start].Line, Sentence = _sentence, Frontier = "unparsed tokens", Tokens = _t.GetRange(start, _p - start) };
            raw.Text = TextOf(raw.Tokens);
            into.Add(raw);
            if (Cur.Kind == TokKind.Period) { Next(); _sentence++; }
        }
    }

    static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ELSE","WHEN","INVALID","EXCEPTION","OVERFLOW","END-OF-PAGE","EOP"
    };

    static readonly HashSet<string> ScopeTerminators = new(StringComparer.OrdinalIgnoreCase)
    {
        "END-IF","END-EVALUATE","END-PERFORM","END-READ","END-WRITE","END-REWRITE","END-DELETE","END-START","END-STRING",
        "END-UNSTRING","END-CALL","END-COMPUTE","END-ADD","END-SUBTRACT","END-MULTIPLY","END-DIVIDE","END-SEARCH","END-RETURN",
        "END-ACCEPT","END-DISPLAY","END-EXEC"
    };

    bool AtStopPhrase()
    {
        if (Cur.Kind != TokKind.Word) return false;
        var w = Cur.Upper;
        if (StopWords.Contains(w) || ScopeTerminators.Contains(w)) return true;
        if (w == "AT" && (Peek().Is("END") || Peek().Is("END-OF-PAGE") || Peek().Is("EOP"))) return true;
        if (w == "ON" && (Peek().Is("SIZE") || Peek().Is("EXCEPTION") || Peek().Is("OVERFLOW"))) return true;
        if (w == "SIZE" && Peek().Is("ERROR")) return true;
        if (w == "NOT" && (Peek().Is("INVALID") || Peek().Is("AT") || Peek().Is("ON") || Peek().Is("SIZE") || Peek().Is("EXCEPTION") || Peek().Is("OVERFLOW") || Peek().Is("END-OF-PAGE") || Peek().Is("EOP"))) return true;
        return false;
    }

    List<Stmt> ParseStatements()
    {
        var list = new List<Stmt>();
        while (!AtEnd && Cur.Kind != TokKind.Period && !IsLabelAt(_p) && !AtStopPhrase())
        {
            if (Cur.Kind != TokKind.Word || !Verbs.Contains(Cur.Text))
            {
                int start = _p;
                while (!AtEnd && Cur.Kind != TokKind.Period && !(Cur.Kind == TokKind.Word && Verbs.Contains(Cur.Text)) && !AtStopPhrase()) Next();
                var raw = new Stmt { Verb = "RAW", Line = _t[start].Line, Sentence = _sentence, Frontier = "unrecognised statement", Tokens = _t.GetRange(start, _p - start) };
                raw.Text = TextOf(raw.Tokens);
                _diag.Add($"L{raw.Line}: unrecognised statement '{raw.Text}'");
                list.Add(raw);
                continue;
            }
            list.Add(ParseStatement());
        }
        return list;
    }

    Stmt ParseStatement()
    {
        int start = _p;
        var verbTok = Cur;
        var s = new Stmt { Verb = verbTok.Upper, Line = verbTok.Line, Sentence = _sentence };
        try
        {
            switch (verbTok.Upper)
            {
                case "MOVE": ParseMove(s); break;
                case "COMPUTE": ParseCompute(s); break;
                case "ADD": case "SUBTRACT": case "MULTIPLY": case "DIVIDE": ParseArithmetic(s); break;
                case "IF": ParseIf(s); break;
                case "EVALUATE": ParseEvaluate(s); break;
                case "PERFORM": ParsePerform(s); break;
                case "GO": ParseGoTo(s); break;
                case "ALTER": ParseAlter(s); break;
                case "SORT": case "MERGE": ParseSort(s); break;
                case "RELEASE": Next(); s.Set("record", ParseRef()); if (Accept("FROM")) s.Set("from", ParseOperand()); break;
                case "RETURN": ParseReturn(s); break;
                case "DISPLAY": ParseDisplay(s); break;
                case "ACCEPT": ParseAccept(s); break;
                case "OPEN": ParseOpen(s); break;
                case "CLOSE": ParseClose(s); break;
                case "READ": ParseRead(s); break;
                case "WRITE": case "REWRITE": ParseWrite(s); break;
                case "DELETE": ParseDelete(s); break;
                case "START": ParseStart(s); break;
                case "STRING": ParseString(s); break;
                case "UNSTRING": ParseUnstring(s); break;
                case "INSPECT": ParseInspect(s); break;
                case "INITIALIZE": ParseInitialize(s); break;
                case "SET": ParseSet(s); break;
                case "CALL": ParseCall(s); break;
                case "GOBACK": Next(); break;
                case "STOP": Next(); if (!Accept("RUN")) { s.Set("literal", ParseOperand()); } else s.Verb = "STOP RUN"; break;
                case "EXIT": ParseExit(s); break;
                case "CONTINUE": Next(); break;
                case "NEXT": Next(); Expect("SENTENCE"); s.Verb = "NEXT SENTENCE"; break;
                case "EXEC": ParseExec(s); break;
                case "SEARCH": ParseSearch(s); break;
                default:
                    SkipGeneric(s, $"{verbTok.Upper} is not lowered");
                    break;
            }
        }
        catch (ParseError e)
        {
            _diag.Add(e.Message);
            // Resynchronise: skip to the next verb, stop phrase or period.
            if (_p == start) Next();
            while (!AtEnd && Cur.Kind != TokKind.Period && !(Cur.Kind == TokKind.Word && Verbs.Contains(Cur.Text)) && !AtStopPhrase()) Next();
            s.Frontier = e.Message;
            s.N = null; s.L = null; s.Blocks = null;
        }
        s.Tokens = _t.GetRange(start, _p - start);
        s.Text = TextOf(s.Tokens.TakeWhile((t, i) => i == 0 || !(t.Kind == TokKind.Word && Verbs.Contains(t.Text) && s.Blocks is not null)).ToList());
        if (s.Text.Length > 240) s.Text = s.Text.Substring(0, 240) + " …";
        return s;
    }

    static string TextOf(IEnumerable<Token> toks) =>
        string.Join(" ", toks.Select(t => t.Kind == TokKind.Literal ? "'" + t.Text.Replace("'", "''") + "'" : t.Text));

    void SkipGeneric(Stmt s, string reason)
    {
        Next();
        while (!AtEnd && Cur.Kind != TokKind.Period && !(Cur.Kind == TokKind.Word && Verbs.Contains(Cur.Text)) && !AtStopPhrase()) Next();
        if (Cur.Upper == "END-" + s.Verb) Next();
        s.Frontier = reason;
    }

    void SkipScoped(Stmt s, string terminator, string reason)
    {
        Next();
        while (!AtEnd && Cur.Kind != TokKind.Period && !At(terminator)) Next();
        Accept(terminator);
        s.Frontier = reason;
    }

    // ---------------------------------------------------------------- operands

    static readonly Dictionary<string, string> Figuratives = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SPACE"] = "SPACES", ["SPACES"] = "SPACES", ["ZERO"] = "ZEROS", ["ZEROS"] = "ZEROS", ["ZEROES"] = "ZEROS",
        ["HIGH-VALUE"] = "HIGH-VALUES", ["HIGH-VALUES"] = "HIGH-VALUES", ["LOW-VALUE"] = "LOW-VALUES", ["LOW-VALUES"] = "LOW-VALUES",
        ["QUOTE"] = "QUOTES", ["QUOTES"] = "QUOTES", ["NULL"] = "NULLS", ["NULLS"] = "NULLS"
    };

    bool IsRefStart() =>
        Cur.Kind == TokKind.Word && (!Reserved.Contains(Cur.Text) && !Verbs.Contains(Cur.Text) && !ScopeTerminators.Contains(Cur.Text) || At("LENGTH") && Peek().Is("OF") || At("ADDRESS") && Peek().Is("OF"));

    bool IsOperandStart() =>
        Cur.Kind is TokKind.Literal or TokKind.Number || IsRefStart() || Cur.Kind == TokKind.Word && (Figuratives.ContainsKey(Cur.Text) || At("ALL") || At("FUNCTION"));

    Node ParseOperand()
    {
        var t = Cur;
        if (t.Kind == TokKind.Literal) { Next(); return Node.Str(t.Text); }
        if (t.Kind == TokKind.Number) { Next(); return Node.Num(t.Text); }
        if (t.Kind == TokKind.Word && Figuratives.TryGetValue(t.Text, out var fig)) { Next(); return new Node { K = "fig", V = fig }; }
        if (At("ALL"))
        {
            Next();
            var inner = ParseOperand();
            return new Node { K = "all", L = inner };
        }
        if (At("FUNCTION")) return ParseFunction();
        if (At("LENGTH") && Peek().Is("OF")) { Next(); Next(); return new Node { K = "len", L = ParseRef() }; }
        if (At("ADDRESS") && Peek().Is("OF")) { Next(); Next(); return new Node { K = "addr", L = ParseRef() }; }
        if (IsRefStart()) return ParseRef();
        throw Fail($"expected an operand but found '{t.Text}'");
    }

    Node ParseFunction()
    {
        Expect("FUNCTION");
        var name = Next().Upper;
        var fn = new Node { K = "fn", V = name, Args = [] };
        if (Cur.IsPunct("(") && !LooksLikeRefMod())
        {
            Next();
            while (!Cur.IsPunct(")") && !AtEnd) fn.Args.Add(ParseExpr());
            Next();
        }
        if (Cur.IsPunct("(") && LooksLikeRefMod()) ParseRefMod(fn);
        return fn;
    }

    /// <summary>True when the parenthesised group at the cursor contains a top-level ':' (reference modification).</summary>
    bool LooksLikeRefMod()
    {
        int depth = 0;
        for (int i = _p; i < _t.Count; i++)
        {
            var t = _t[i];
            if (t.IsPunct("(")) depth++;
            else if (t.IsPunct(")")) { depth--; if (depth == 0) return false; }
            else if (t.IsPunct(":") && depth == 1) return true;
            else if (t.Kind == TokKind.Period) return false;
        }
        return false;
    }

    void ParseRefMod(Node n)
    {
        Next(); // (
        n.Start = ParseExpr();
        if (!Cur.IsPunct(":")) throw Fail("expected ':' in reference modification");
        Next();
        if (!Cur.IsPunct(")")) n.Len = ParseExpr();
        if (!Cur.IsPunct(")")) throw Fail("expected ')' after reference modification");
        Next();
    }

    Node ParseRef()
    {
        var nameTok = Next();
        var name = nameTok.Upper;
        var quals = new List<string>();
        while ((At("OF") || At("IN")) && Peek().Kind == TokKind.Word) { Next(); quals.Add(Next().Upper); }
        var item = _idx.Resolve(name, quals, out var err);
        if (item is null)
        {
            if (_idx.File(name) is not null) return new Node { K = "file", V = name };
            throw Fail(err!);
        }
        var n = item.Level == 88 ? new Node { K = "c88", Id = item.Id, V = name } : Node.Ref(item.Id, name);
        if (Cur.IsPunct("(") && !LooksLikeRefMod())
        {
            Next();
            n.Args = [];
            while (!Cur.IsPunct(")") && !AtEnd)
            {
                // ALL subscripts are not supported; relative subscripts are ordinary expressions.
                n.Args.Add(ParseExpr());
            }
            Next();
        }
        if (Cur.IsPunct("(") && LooksLikeRefMod()) ParseRefMod(n);
        return n;
    }

    // ---------------------------------------------------------------- arithmetic expressions

    Node ParseExpr()
    {
        var l = ParseTerm();
        while (Cur.IsPunct("+") || Cur.IsPunct("-"))
        {
            var op = Next().Text;
            l = new Node { K = "bin", Op = op, L = l, R = ParseTerm() };
        }
        return l;
    }

    Node ParseTerm()
    {
        var l = ParseFactor();
        while (Cur.IsPunct("*") || Cur.IsPunct("/"))
        {
            var op = Next().Text;
            l = new Node { K = "bin", Op = op, L = l, R = ParseFactor() };
        }
        return l;
    }

    Node ParseFactor()
    {
        var b = ParseUnary();
        if (Cur.IsPunct("**")) { Next(); return new Node { K = "bin", Op = "**", L = b, R = ParseFactor() }; }
        return b;
    }

    Node ParseUnary()
    {
        if (Cur.IsPunct("-")) { Next(); return new Node { K = "neg", L = ParseUnary() }; }
        if (Cur.IsPunct("+")) { Next(); return ParseUnary(); }
        if (Cur.IsPunct("(")) { Next(); var e = ParseExpr(); if (!Cur.IsPunct(")")) throw Fail("expected ')'"); Next(); return e; }
        return ParseOperand();
    }

    // ---------------------------------------------------------------- conditions

    Node? _lastSubject;
    string? _lastOp;

    Node ParseCondition()
    {
        _lastSubject = null; _lastOp = null;
        return ParseOr();
    }

    Node ParseOr()
    {
        var l = ParseAnd();
        while (At("OR")) { Next(); l = new Node { K = "or", L = l, R = ParseAnd() }; }
        return l;
    }

    Node ParseAnd()
    {
        var l = ParseNot();
        while (At("AND")) { Next(); l = new Node { K = "and", L = l, R = ParseNot() }; }
        return l;
    }

    Node ParseNot()
    {
        if (At("NOT") && !IsRelOpAt(_p)) { Next(); return new Node { K = "not", L = ParseNot() }; }
        return ParsePrimaryCondition();
    }

    bool IsRelOpAt(int i)
    {
        var t = _t[i];
        if (t.Kind == TokKind.Punct && t.Text is "=" or ">" or "<" or ">=" or "<=" or "<>") return true;
        if (t.Is("IS")) return IsRelOpAt(i + 1) || _t[i + 1].Is("NUMERIC") || _t[i + 1].Is("ALPHABETIC") || _t[i + 1].Is("NOT");
        if (t.Is("NOT")) return IsRelOpAt(i + 1);
        return t.Is("EQUAL") || t.Is("GREATER") || t.Is("LESS") || t.Is("EQUALS");
    }

    string? TryRelOp()
    {
        int save = _p;
        Accept("IS");
        bool neg = Accept("NOT");
        string? op = null;
        if (Cur.Kind == TokKind.Punct)
        {
            switch (Cur.Text)
            {
                case "=": op = "eq"; break;
                case ">": op = "gt"; break;
                case "<": op = "lt"; break;
                case ">=": op = "ge"; break;
                case "<=": op = "le"; break;
                case "<>": op = "ne"; break;
            }
            if (op is not null) Next();
        }
        else if (At("EQUAL") || At("EQUALS")) { Next(); Accept("TO"); op = "eq"; }
        else if (At("GREATER")) { Next(); Accept("THAN"); if (At("OR") && Peek().Is("EQUAL")) { Next(); Next(); Accept("TO"); op = "ge"; } else op = "gt"; }
        else if (At("LESS")) { Next(); Accept("THAN"); if (At("OR") && Peek().Is("EQUAL")) { Next(); Next(); Accept("TO"); op = "le"; } else op = "lt"; }
        if (op is null) { _p = save; return null; }
        if (neg) op = op switch { "eq" => "ne", "ne" => "eq", "gt" => "le", "lt" => "ge", "ge" => "lt", "le" => "gt", _ => op };
        return op;
    }

    Node ParsePrimaryCondition()
    {
        // Abbreviated combined relation: "... OR > 5" or "... OR '23'".
        if (_lastSubject is not null && IsRelOpAt(_p))
        {
            var op = TryRelOp()!;
            _lastOp = op;
            return new Node { K = "rel", Op = op, L = _lastSubject, R = ParseExpr() };
        }
        if (Cur.IsPunct("("))
        {
            int save = _p;
            var saveSubj = _lastSubject; var saveOp = _lastOp;
            try
            {
                Next();
                var c = ParseOr();
                if (Cur.IsPunct(")"))
                {
                    Next();
                    if (!IsRelOpAt(_p) && !At("IS")) return c;
                }
            }
            catch (ParseError) { }
            _p = save; _lastSubject = saveSubj; _lastOp = saveOp;
        }
        var subjStart = _p;
        var left = ParseExpr();
        if (left.K == "c88" && !IsRelOpAt(_p)) return left;
        // Class and sign conditions.
        {
            int save = _p;
            Accept("IS");
            bool neg = Accept("NOT");
            if (At("NUMERIC") || At("ALPHABETIC") || At("ALPHABETIC-LOWER") || At("ALPHABETIC-UPPER"))
                return new Node { K = "class", V = Next().Upper, L = left, Op = neg ? "not" : null };
            if (At("POSITIVE") || At("NEGATIVE") || (At("ZERO") && !IsRelOpAt(_p + 1)))
                return new Node { K = "sign", V = Next().Upper, L = left, Op = neg ? "not" : null };
            _p = save;
        }
        var rel = TryRelOp();
        if (rel is null)
        {
            // Abbreviation with only an object: "X = 'A' OR 'B'" → subject and operator inherited.
            if (_lastSubject is not null && _lastOp is not null && _p > subjStart)
                return new Node { K = "rel", Op = _lastOp, L = _lastSubject, R = left };
            throw Fail($"expected a relational operator after '{_t[subjStart].Text}'");
        }
        var right = ParseExpr();
        _lastSubject = left; _lastOp = rel;
        return new Node { K = "rel", Op = rel, L = left, R = right };
    }

    // ---------------------------------------------------------------- statements

    List<Node> ParseReceivers(bool allowRounded)
    {
        var list = new List<Node>();
        while (IsRefStart())
        {
            var r = ParseRef();
            if (allowRounded && Accept("ROUNDED")) r.Rounded = true;
            list.Add(r);
        }
        if (list.Count == 0) throw Fail($"expected a receiving item but found '{Cur.Text}'");
        return list;
    }

    void ParseMove(Stmt s)
    {
        Next();
        if (Accept("CORRESPONDING") || Accept("CORR")) s.Set("corr", "true");
        s.Set("src", ParseOperand());
        Expect("TO");
        s.Set("dst", ParseReceivers(false));
    }

    void ParseCompute(Stmt s)
    {
        Next();
        s.Set("dst", ParseReceivers(true));
        if (!Cur.IsPunct("=") && !Accept("EQUAL")) throw Fail("expected '=' in COMPUTE");
        if (Cur.IsPunct("=")) Next();
        s.Set("expr", ParseExpr());
        ParseSizeError(s);
        Accept("END-COMPUTE");
    }

    void ParseArithmetic(Stmt s)
    {
        var verb = Next().Upper;
        if (Accept("CORRESPONDING") || Accept("CORR")) throw Fail($"{verb} CORRESPONDING is not lowered");
        var ops = new List<Node>();
        while (IsOperandStart()) ops.Add(ParseExpr0());
        s.Set("ops", ops);
        switch (verb)
        {
            case "ADD":
                if (Accept("TO"))
                {
                    var to = ParseOperandsRounded();
                    if (Accept("GIVING")) { s.Set("form", "GIVING"); ops.AddRange(to); s.Set("giving", ParseReceivers(true)); }
                    else { RequireRefs(to); s.Set("form", "TO"); s.Set("dst", to); }
                }
                else { Expect("GIVING"); s.Set("form", "GIVING"); s.Set("giving", ParseReceivers(true)); }
                break;
            case "SUBTRACT":
                Expect("FROM");
                {
                    var from = ParseReceivers(true);
                    if (Accept("GIVING")) { s.Set("form", "GIVING"); s.Set("minuend", from[0]); s.Set("giving", ParseReceivers(true)); }
                    else { s.Set("form", "FROM"); s.Set("dst", from); }
                }
                break;
            case "MULTIPLY":
                Expect("BY");
                {
                    var by = ParseOperandsRounded();
                    if (Accept("GIVING")) { s.Set("form", "GIVING"); s.Set("by", by[0]); s.Set("giving", ParseReceivers(true)); }
                    else { RequireRefs(by); s.Set("form", "BY"); s.Set("dst", by); }
                }
                break;
            case "DIVIDE":
                if (Accept("INTO"))
                {
                    var into = ParseOperandsRounded();
                    if (Accept("GIVING")) { s.Set("form", "INTO-GIVING"); s.Set("by", into[0]); s.Set("giving", ParseReceivers(true)); }
                    else { RequireRefs(into); s.Set("form", "INTO"); s.Set("dst", into); }
                }
                else
                {
                    Expect("BY");
                    s.Set("by", ParseExpr0());
                    Expect("GIVING");
                    s.Set("form", "BY-GIVING");
                    s.Set("giving", ParseReceivers(true));
                }
                if (Accept("REMAINDER")) s.Set("rem", ParseRef());
                break;
        }
        ParseSizeError(s);
        Accept("END-" + verb);
    }

    Node ParseExpr0() => ParseOperand();

    List<Node> ParseOperandsRounded()
    {
        var list = new List<Node>();
        while (IsOperandStart())
        {
            var n = ParseOperand();
            if (Accept("ROUNDED")) n.Rounded = true;
            list.Add(n);
        }
        if (list.Count == 0) throw Fail($"expected an operand but found '{Cur.Text}'");
        return list;
    }

    void RequireRefs(List<Node> nodes)
    {
        if (nodes.Any(n => n.K != "ref")) throw Fail("a receiving operand must be a data item");
    }

    void ParseSizeError(Stmt s)
    {
        while (true)
        {
            if (At("ON") && Peek().Is("SIZE") || At("SIZE"))
            {
                Accept("ON"); Expect("SIZE"); Expect("ERROR");
                s.Block("SIZE-ERROR", ParseStatements());
                continue;
            }
            if (At("NOT") && (Peek().Is("ON") || Peek().Is("SIZE")))
            {
                Next(); Accept("ON"); Expect("SIZE"); Expect("ERROR");
                s.Block("NOT-SIZE-ERROR", ParseStatements());
                continue;
            }
            break;
        }
    }

    void ParseIf(Stmt s)
    {
        Next();
        s.Set("cond", ParseCondition());
        Accept("THEN");
        if (At("NEXT") && Peek().Is("SENTENCE"))
            s.Block("TRUE", [ParseStatement()]);
        else
            s.Block("TRUE", ParseStatements());
        if (Accept("ELSE"))
        {
            if (At("NEXT") && Peek().Is("SENTENCE")) s.Block("FALSE", [ParseStatement()]);
            else s.Block("FALSE", ParseStatements());
        }
        Accept("END-IF");
    }

    void ParseEvaluate(Stmt s)
    {
        Next();
        var subjects = new List<Node>();
        do
        {
            if (At("TRUE") || At("FALSE")) subjects.Add(Node.Bool(Next().Is("TRUE")));
            else
            {
                int save = _p;
                var e = ParseExpr();
                if (IsRelOpAt(_p) || At("IS") || e.K == "c88") { _p = save; subjects.Add(new Node { K = "cond", L = ParseCondition() }); }
                else subjects.Add(e);
            }
        } while (Accept("ALSO"));
        s.Set("subjects", subjects);
        var whens = new List<Stmt>();
        while (At("WHEN"))
        {
            var w = new Stmt { Verb = "WHEN", Line = Cur.Line, Sentence = _sentence };
            int start = _p;
            Node? cond = null;
            bool other = false;
            // Stacked WHENs with no statements between them are ORed into one branch.
            while (At("WHEN"))
            {
                Next();
                if (Accept("OTHER")) { other = true; break; }
                var c = ParseWhenObjects(subjects);
                cond = cond is null ? c : new Node { K = "or", L = cond, R = c };
            }
            w.Set("cond", other ? new Node { K = "any" } : cond!);
            if (other) w.Set("other", "true");
            w.Tokens = _t.GetRange(start, _p - start);
            w.Text = TextOf(w.Tokens);
            w.Block("TRUE", ParseStatements());
            whens.Add(w);
        }
        s.Block("WHEN", whens);
        Accept("END-EVALUATE");
    }

    Node ParseWhenObjects(List<Node> subjects)
    {
        Node? all = null;
        for (int i = 0; i < subjects.Count; i++)
        {
            if (i > 0) Expect("ALSO");
            var c = ParseWhenObject(subjects[i]);
            all = all is null ? c : new Node { K = "and", L = all, R = c };
        }
        return all!;
    }

    Node ParseWhenObject(Node subject)
    {
        if (Accept("ANY")) return new Node { K = "any" };
        if (subject.K == "bool" || subject.K == "cond")
        {
            Node c;
            if (At("TRUE") || At("FALSE")) c = Node.Bool(Next().Is("TRUE"));
            else c = ParseCondition();
            if (subject.K == "bool") return subject.V == "TRUE" ? c : new Node { K = "not", L = c };
            return new Node { K = "rel", Op = "eq", L = new Node { K = "condval", L = subject.L }, R = new Node { K = "condval", L = c } };
        }
        bool neg = Accept("NOT");
        var lo = ParseExpr();
        Node r;
        if (Accept("THRU") || Accept("THROUGH"))
        {
            var hi = ParseExpr();
            r = new Node { K = "and", L = new Node { K = "rel", Op = "ge", L = subject, R = lo }, R = new Node { K = "rel", Op = "le", L = subject, R = hi } };
        }
        else r = new Node { K = "rel", Op = "eq", L = subject, R = lo };
        return neg ? new Node { K = "not", L = r } : r;
    }

    void ParsePerform(Stmt s)
    {
        Next();
        bool inline = true;
        if (Cur.Kind is TokKind.Word or TokKind.Number && _paragraphNames.Contains(Cur.Text) && !Peek().Is("TIMES"))
        {
            inline = false;
            s.Set("target", Next().Upper);
            if (Accept("THRU") || Accept("THROUGH")) s.Set("thru", Next().Upper);
        }
        else if (Cur.Kind == TokKind.Word && _paragraphNames.Contains(Cur.Text))
        {
            inline = false;
            s.Set("target", Next().Upper);
        }
        if (Accept("WITH")) { Expect("TEST"); s.Set("test", Next().Upper); }
        else if (At("TEST")) { Next(); s.Set("test", Next().Upper); }
        if (At("UNTIL"))
        {
            Next();
            s.Set("until", ParseCondition());
        }
        else if (At("VARYING"))
        {
            Next();
            s.Set("var", ParseRef());
            Expect("FROM"); s.Set("from", ParseExpr());
            Expect("BY"); s.Set("by", ParseExpr());
            Expect("UNTIL"); s.Set("until", ParseCondition());
            // AFTER id FROM x BY y UNTIL cond ...: each an "after" node with Args [id, from, by, until].
            var afters = new List<Node>();
            while (Accept("AFTER"))
            {
                var v = ParseRef();
                Expect("FROM"); var from = ParseExpr();
                Expect("BY"); var by = ParseExpr();
                Expect("UNTIL"); var until = ParseCondition();
                afters.Add(new Node { K = "after", Args = [v, from, by, until] });
            }
            if (afters.Count > 0) s.Set("after", afters);
        }
        else if (IsOperandStart() && (Peek().Is("TIMES") || Cur.Kind == TokKind.Word && TimesAhead()))
        {
            s.Set("times", ParseOperand());
            Expect("TIMES");
        }
        if (inline)
        {
            s.Set("inline", "true");
            s.Block("LOOP", ParseStatements());
            Expect("END-PERFORM");
        }
    }

    bool TimesAhead()
    {
        for (int i = _p + 1; i < Math.Min(_p + 8, _t.Count); i++)
        {
            if (_t[i].Is("TIMES")) return true;
            if (_t[i].Kind == TokKind.Period || _t[i].Kind == TokKind.Word && Verbs.Contains(_t[i].Text)) return false;
        }
        return false;
    }

    void ParseGoTo(Stmt s)
    {
        Next(); Accept("TO");
        s.Verb = "GO TO";
        var targets = new List<Node>();
        while (Cur.Kind is TokKind.Word or TokKind.Number && _paragraphNames.Contains(Cur.Text)) targets.Add(new Node { K = "para", V = Next().Upper });
        if (targets.Count == 0 && Cur.Kind == TokKind.Period)
        {
            // GO TO. : the target is set by ALTER.
            s.Set("targets", targets);
            s.Set("alterable", "true");
            return;
        }
        if (targets.Count == 0) throw Fail("GO TO without a resolvable target");
        s.Set("targets", targets);
        if (Accept("DEPENDING")) { Accept("ON"); s.Set("depending", ParseRef()); }
    }

    /// <summary>ALTER p1 TO [PROCEED TO] q1 [p2 TO [PROCEED TO] q2]...: "pairs" of alter nodes (V = altered, Op = new target).</summary>
    void ParseAlter(Stmt s)
    {
        Next();
        var pairs = new List<Node>();
        while (Cur.Kind is TokKind.Word or TokKind.Number && _paragraphNames.Contains(Cur.Text) && Peek().Is("TO"))
        {
            var from = Next().Upper;
            Expect("TO");
            if (Accept("PROCEED")) Expect("TO");
            if (!(Cur.Kind is TokKind.Word or TokKind.Number && _paragraphNames.Contains(Cur.Text))) throw Fail($"ALTER {from} TO an unknown procedure '{Cur.Text}'");
            pairs.Add(new Node { K = "alter", V = from, Op = Next().Upper });
        }
        if (pairs.Count == 0) throw Fail("ALTER without a paragraph");
        s.Set("pairs", pairs);
    }

    void ParseDisplay(Stmt s)
    {
        Next();
        var items = new List<Node>();
        while (IsOperandStart()) items.Add(ParseOperand());
        s.Set("items", items);
        if (Accept("UPON")) s.Set("upon", Next().Upper);
        if (At("WITH") && Peek().Is("NO") || At("NO"))
        {
            Accept("WITH"); Expect("NO"); Accept("ADVANCING"); s.Set("noAdvancing", "true");
        }
        Accept("END-DISPLAY");
    }

    void ParseAccept(Stmt s)
    {
        Next();
        s.Set("dst", ParseRef());
        if (Accept("FROM"))
        {
            var src = Next().Upper;
            if (src == "DATE" && Accept("YYYYMMDD")) src = "DATE-YYYYMMDD";
            else if (src == "DAY" && Accept("YYYYDDD")) src = "DAY-YYYYDDD";
            s.Set("from", src);
        }
        Accept("END-ACCEPT");
    }

    void ParseOpen(Stmt s)
    {
        Next();
        var files = new List<Node>();
        while (At("INPUT") || At("OUTPUT") || At("I-O") || At("EXTEND"))
        {
            var mode = Next().Upper;
            while (Cur.Kind == TokKind.Word && _idx.File(Cur.Text) is not null)
            {
                files.Add(new Node { K = "file", V = Next().Upper, Op = mode });
                Accept("WITH"); if (Accept("NO")) Accept("REWIND");
            }
        }
        if (files.Count == 0) throw Fail("OPEN without files");
        s.Set("files", files);
    }

    void ParseClose(Stmt s)
    {
        Next();
        var files = new List<Node>();
        while (Cur.Kind == TokKind.Word && _idx.File(Cur.Text) is not null)
        {
            files.Add(new Node { K = "file", V = Next().Upper });
            if (Accept("WITH")) { Accept("LOCK"); if (Accept("NO")) Accept("REWIND"); }
            else if (Accept("LOCK")) { }
        }
        if (files.Count == 0) throw Fail("CLOSE without files");
        s.Set("files", files);
    }

    string ExpectFile()
    {
        var name = Next().Upper;
        if (_idx.File(name) is null) throw Fail($"unknown file {name}");
        return name;
    }

    void ParseRead(Stmt s)
    {
        Next();
        s.Set("file", ExpectFile());
        if (Accept("NEXT")) s.Set("direction", "NEXT");
        else if (Accept("PREVIOUS")) s.Set("direction", "PREVIOUS");
        Accept("RECORD");
        if (Accept("INTO")) s.Set("into", ParseRef());
        if (At("WITH") && (Peek().Is("LOCK") || Peek().Is("NO"))) { Next(); Accept("NO"); Accept("LOCK"); }
        if (Accept("KEY")) { Accept("IS"); s.Set("key", ParseRef()); }
        ParseIoPhrases(s);
        Accept("END-READ");
    }

    void ParseIoPhrases(Stmt s)
    {
        while (true)
        {
            if (At("AT") && Peek().Is("END") || At("END") && !Peek().Is("-"))
            {
                Accept("AT"); Expect("END");
                s.Block("AT-END", ParseStatements()); continue;
            }
            if (At("NOT") && Peek().Is("AT") || At("NOT") && Peek().Is("END"))
            {
                Next(); Accept("AT"); Expect("END");
                s.Block("NOT-AT-END", ParseStatements()); continue;
            }
            if (At("INVALID")) { Next(); Accept("KEY"); s.Block("INVALID", ParseStatements()); continue; }
            if (At("NOT") && Peek().Is("INVALID")) { Next(); Next(); Accept("KEY"); s.Block("NOT-INVALID", ParseStatements()); continue; }
            if (At("AT") && (Peek().Is("END-OF-PAGE") || Peek().Is("EOP")) || At("END-OF-PAGE") || At("EOP"))
            {
                Accept("AT"); Next(); s.Block("EOP", ParseStatements()); continue;
            }
            if (At("NOT") && (Peek().Is("AT") || Peek().Is("END-OF-PAGE") || Peek().Is("EOP")))
            {
                Next(); Accept("AT"); Next(); s.Block("NOT-EOP", ParseStatements()); continue;
            }
            break;
        }
    }

    void ParseWrite(Stmt s)
    {
        var verb = Next().Upper;
        var rec = ParseRef();
        s.Set("record", rec);
        if (Accept("FROM")) s.Set("from", ParseOperand());
        if (At("BEFORE") || At("AFTER"))
        {
            s.Set("advWhen", Next().Upper);
            Accept("ADVANCING");
            if (Accept("PAGE")) s.Set("advPage", "true");
            else { s.Set("advLines", ParseOperand()); if (!Accept("LINES")) Accept("LINE"); }
        }
        ParseIoPhrases(s);
        Accept("END-" + verb);
    }

    void ParseDelete(Stmt s)
    {
        Next();
        s.Set("file", ExpectFile());
        Accept("RECORD");
        ParseIoPhrases(s);
        Accept("END-DELETE");
    }

    void ParseStart(Stmt s)
    {
        Next();
        s.Set("file", ExpectFile());
        if (Accept("KEY"))
        {
            var op = TryRelOp() ?? throw Fail("expected a relational operator in START KEY");
            s.Set("keyOp", op);
            s.Set("key", ParseRef());
        }
        ParseIoPhrases(s);
        Accept("END-START");
    }

    void ParseString(Stmt s)
    {
        Next();
        var pieces = new List<Node>();
        while (!At("INTO"))
        {
            var group = new List<Node>();
            while (IsOperandStart()) group.Add(ParseOperand());
            if (group.Count == 0) throw Fail("STRING without sources");
            Node delim;
            if (Accept("DELIMITED")) { Accept("BY"); delim = Accept("SIZE") ? new Node { K = "size" } : ParseOperand(); }
            else delim = new Node { K = "size" };
            foreach (var g in group) pieces.Add(new Node { K = "piece", L = g, R = delim });
        }
        Expect("INTO");
        s.Set("pieces", pieces);
        s.Set("into", ParseRef());
        if (Accept("WITH") || At("POINTER")) { Expect("POINTER"); s.Set("pointer", ParseRef()); }
        ParseOverflow(s);
        Accept("END-STRING");
    }

    void ParseOverflow(Stmt s)
    {
        while (true)
        {
            if (At("ON") && Peek().Is("OVERFLOW") || At("OVERFLOW")) { Accept("ON"); Next(); s.Block("OVERFLOW", ParseStatements()); continue; }
            if (At("NOT") && (Peek().Is("ON") || Peek().Is("OVERFLOW"))) { Next(); Accept("ON"); Expect("OVERFLOW"); s.Block("NOT-OVERFLOW", ParseStatements()); continue; }
            break;
        }
    }

    void ParseUnstring(Stmt s)
    {
        Next();
        s.Set("src", ParseRef());
        var delims = new List<Node>();
        if (Accept("DELIMITED"))
        {
            Accept("BY");
            do
            {
                bool all = Accept("ALL");
                var d = ParseOperand();
                delims.Add(all ? new Node { K = "all", L = d } : d);
            } while (Accept("OR"));
        }
        s.Set("delims", delims);
        Expect("INTO");
        var targets = new List<Node>();
        while (IsRefStart())
        {
            var t = new Node { K = "utarget", L = ParseRef() };
            if (Accept("DELIMITER")) { Accept("IN"); t.Start = ParseRef(); }
            if (Accept("COUNT")) { Accept("IN"); t.Len = ParseRef(); }
            targets.Add(t);
        }
        s.Set("targets", targets);
        if (Accept("WITH") || At("POINTER")) { Expect("POINTER"); s.Set("pointer", ParseRef()); }
        if (Accept("TALLYING")) { Accept("IN"); s.Set("tallying", ParseRef()); }
        ParseOverflow(s);
        Accept("END-UNSTRING");
    }

    void ParseInspect(Stmt s)
    {
        Next();
        s.Set("target", ParseRef());
        var clauses = new List<Node>();
        if (Accept("CONVERTING"))
        {
            var from = ParseOperand(); Expect("TO"); var to = ParseOperand();
            var c = new Node { K = "convert", L = from, R = to };
            ParseInspectBounds(c);
            clauses.Add(c);
            s.Set("clauses", clauses);
            return;
        }
        if (Accept("TALLYING"))
        {
            while (IsRefStart() && Peek().Is("FOR"))
            {
                var counter = ParseRef(); Expect("FOR");
                while (At("CHARACTERS") || At("ALL") || At("LEADING"))
                {
                    var mode = Next().Upper;
                    var c = new Node { K = "tally", Op = mode, Args = [counter] };
                    if (mode != "CHARACTERS") c.L = ParseOperand();
                    ParseInspectBounds(c);
                    clauses.Add(c);
                }
            }
        }
        if (Accept("REPLACING"))
        {
            while (At("CHARACTERS") || At("ALL") || At("LEADING") || At("FIRST"))
            {
                var mode = Next().Upper;
                if (mode == "CHARACTERS") { Expect("BY"); var c = new Node { K = "replace", Op = mode, R = ParseOperand() }; ParseInspectBounds(c); clauses.Add(c); continue; }
                while (IsOperandStart() && !At("ALL"))
                {
                    var c = new Node { K = "replace", Op = mode, L = ParseOperand() };
                    Expect("BY"); c.R = ParseOperand();
                    ParseInspectBounds(c);
                    clauses.Add(c);
                    if (At("CHARACTERS") || At("ALL") || At("LEADING") || At("FIRST")) break;
                }
            }
        }
        if (clauses.Count == 0) throw Fail("INSPECT form is not lowered");
        s.Set("clauses", clauses);
    }

    void ParseInspectBounds(Node c)
    {
        while (At("BEFORE") || At("AFTER"))
        {
            var which = Next().Upper; Accept("INITIAL");
            var v = ParseOperand();
            if (which == "BEFORE") c.Start = v; else c.Len = v;
        }
    }

    static readonly HashSet<string> InitCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALPHABETIC", "ALPHANUMERIC", "ALPHANUMERIC-EDITED", "NUMERIC", "NUMERIC-EDITED", "NATIONAL", "NATIONAL-EDITED", "DBCS", "EGCS"
    };

    /// <summary>
    /// INITIALIZE id... [WITH FILLER] [{ALL | category} TO VALUE] [THEN] [REPLACING {category [DATA] BY value}...]
    /// [THEN TO DEFAULT]. The options become "filler", "toValue", "replacing" (category nodes) and "toDefault".
    /// </summary>
    void ParseInitialize(Stmt s)
    {
        Next();
        s.Set("dst", ParseReceivers(false));
        if (Accept("WITH")) { Expect("FILLER"); s.Set("filler", "true"); }
        else if (Accept("FILLER")) s.Set("filler", "true");
        if ((At("ALL") || InitCategories.Contains(Cur.Text)) && Peek().Is("TO"))
        {
            s.Set("toValue", Next().Upper);
            Expect("TO"); Expect("VALUE");
        }
        Accept("THEN");
        if (Accept("REPLACING"))
        {
            var reps = new List<Node>();
            while (Cur.Kind == TokKind.Word && InitCategories.Contains(Cur.Text))
            {
                var cat = Next().Upper;
                Accept("DATA");
                Expect("BY");
                reps.Add(new Node { K = "replace", V = cat, L = ParseOperand() });
            }
            if (reps.Count == 0) throw Fail("INITIALIZE REPLACING without a category");
            s.Set("replacing", reps);
            Accept("THEN");
        }
        if (Accept("TO")) { Expect("DEFAULT"); s.Set("toDefault", "true"); }
    }

    void ParseSet(Stmt s)
    {
        Next();
        if (At("ADDRESS") && Peek().Is("OF")) throw Fail("SET ADDRESS OF is not lowered (z/OS storage addressing)");
        var targets = new List<Node>();
        while (IsRefStart()) targets.Add(ParseRef());
        if (targets.Count == 0) throw Fail("SET without targets");
        s.Set("dst", targets);
        if (Accept("TO"))
        {
            if (At("TRUE")) { Next(); s.Set("mode", "TRUE"); return; }
            if (At("FALSE")) { Next(); s.Set("mode", "FALSE"); return; }
            if (At("ADDRESS") || At("NULL") || At("NULLS")) throw Fail("SET pointer is not lowered");
            s.Set("mode", "TO");
            s.Set("value", ParseExpr());
            return;
        }
        if (Accept("UP")) { Expect("BY"); s.Set("mode", "UP"); s.Set("value", ParseExpr()); return; }
        if (Accept("DOWN")) { Expect("BY"); s.Set("mode", "DOWN"); s.Set("value", ParseExpr()); return; }
        throw Fail("unsupported SET form");
    }

    /// <summary>
    /// SEARCH [ALL] table [VARYING item] [AT END imperative] WHEN condition imperative ... [END-SEARCH]. The search
    /// index is the VARYING item when it indexes the table, else the table's first INDEXED BY index.
    /// </summary>
    void ParseSearch(Stmt s)
    {
        Next();
        if (Accept("ALL")) s.Verb = "SEARCH ALL";
        var table = ParseRef();
        var t = _idx.Items.FirstOrDefault(x => x.Id == table.Id);
        if (t is null || t.Occurs == 0) throw Fail($"{s.Verb} of an item that is not a table");
        if (t.IndexedBy.Count == 0) throw Fail($"{s.Verb} of a table without INDEXED BY");
        s.Set("table", table);
        Node? varying = null;
        if (s.Verb == "SEARCH" && Accept("VARYING")) { varying = ParseRef(); s.Set("varying", varying); }
        var own = varying is not null && t.IndexedBy.Any(n => string.Equals(n, varying.V, StringComparison.OrdinalIgnoreCase))
            ? varying.V! : t.IndexedBy[0];
        var index = _idx.Items.FirstOrDefault(x => x.Level == 77 && x.Usage == Usage.Index && string.Equals(x.Name, own, StringComparison.OrdinalIgnoreCase))
            ?? throw Fail($"index {own} of {t.Name} not found");
        s.Set("index", Node.Ref(index.Id, index.Name));
        if (At("AT") && Peek().Is("END") || At("END"))
        {
            Accept("AT"); Expect("END");
            s.Block("AT-END", At("NEXT") && Peek().Is("SENTENCE") ? [ParseStatement()] : ParseStatements());
        }
        var whens = new List<Stmt>();
        while (At("WHEN"))
        {
            var w = new Stmt { Verb = "WHEN", Line = Cur.Line, Sentence = _sentence };
            int start = _p;
            Next();
            w.Set("cond", ParseCondition());
            w.Tokens = _t.GetRange(start, _p - start);
            w.Text = TextOf(w.Tokens);
            w.Block("TRUE", At("NEXT") && Peek().Is("SENTENCE") ? [ParseStatement()] : ParseStatements());
            whens.Add(w);
        }
        if (whens.Count == 0) throw Fail($"{s.Verb} without WHEN");
        if (s.Verb == "SEARCH ALL" && whens.Count > 1) throw Fail("SEARCH ALL with more than one WHEN");
        s.Block("WHEN", whens);
        Accept("END-SEARCH");
    }

    /// <summary>
    /// SORT file|table ON {ASCENDING|DESCENDING} KEY k... [WITH DUPLICATES IN ORDER] {INPUT PROCEDURE p [THRU q] |
    /// USING f...} {OUTPUT PROCEDURE p [THRU q] | GIVING f...}; MERGE file ... USING f f... {OUTPUT PROCEDURE | GIVING}.
    /// </summary>
    void ParseSort(Stmt s)
    {
        Next();
        if (_idx.File(Cur.Text) is { } sd) { s.Set("file", Next().Upper); }
        else s.Set("table", ParseRef());
        var keys = new List<Node>();
        while (true)
        {
            Accept("ON");
            if (!(At("ASCENDING") || At("DESCENDING"))) break;
            var dir = Next().Upper == "ASCENDING" ? "ASC" : "DESC";
            Accept("KEY"); Accept("IS");
            while (IsRefStart() && !At("ASCENDING") && !At("DESCENDING") && !At("WITH") && !At("DUPLICATES") && !At("COLLATING") && !At("INPUT") && !At("OUTPUT") && !At("USING") && !At("GIVING"))
                keys.Add(new Node { K = "key", Op = dir, L = ParseRef() });
        }
        s.Set("keys", keys);
        if (Accept("WITH") || At("DUPLICATES")) { Expect("DUPLICATES"); Accept("IN"); Accept("ORDER"); s.Set("duplicates", "true"); }
        if (At("COLLATING") || At("SEQUENCE")) throw Fail($"{s.Verb} with a COLLATING SEQUENCE is not lowered");
        if (Accept("INPUT"))
        {
            Expect("PROCEDURE"); Accept("IS");
            s.Set("inputProc", Next().Upper);
            if (Accept("THRU") || Accept("THROUGH")) s.Set("inputThru", Next().Upper);
        }
        else if (Accept("USING"))
        {
            var files = new List<Node>();
            while (Cur.Kind == TokKind.Word && _idx.File(Cur.Text) is not null) files.Add(new Node { K = "file", V = Next().Upper });
            s.Set("using", files);
        }
        if (Accept("OUTPUT"))
        {
            Expect("PROCEDURE"); Accept("IS");
            s.Set("outputProc", Next().Upper);
            if (Accept("THRU") || Accept("THROUGH")) s.Set("outputThru", Next().Upper);
        }
        else if (Accept("GIVING"))
        {
            var files = new List<Node>();
            while (Cur.Kind == TokKind.Word && _idx.File(Cur.Text) is not null) files.Add(new Node { K = "file", V = Next().Upper });
            s.Set("giving", files);
        }
    }

    /// <summary>RETURN sort-file [RECORD] [INTO id] AT END ... NOT AT END ... [END-RETURN].</summary>
    void ParseReturn(Stmt s)
    {
        Next();
        s.Set("file", ExpectFile());
        Accept("RECORD");
        if (Accept("INTO")) s.Set("into", ParseRef());
        ParseIoPhrases(s);
        Accept("END-RETURN");
    }

    void ParseCall(Stmt s)
    {
        Next();
        s.Set("target", ParseOperand());
        if (Accept("USING"))
        {
            var args = new List<Node>();
            string mode = "REFERENCE";
            while (true)
            {
                if (Accept("BY")) { mode = Next().Upper; continue; }
                if (At("REFERENCE") || At("CONTENT") || At("VALUE")) { mode = Next().Upper; continue; }
                if (Accept("OMITTED")) { args.Add(new Node { K = "omitted" }); continue; }
                if (!IsOperandStart()) break;
                var a = ParseOperand();
                args.Add(new Node { K = "arg", Op = mode, L = a });
            }
            s.Set("args", args);
        }
        if (Accept("RETURNING") || Accept("GIVING")) s.Set("returning", ParseRef());
        while (true)
        {
            if (At("ON") && (Peek().Is("EXCEPTION") || Peek().Is("OVERFLOW")) || At("EXCEPTION") || At("OVERFLOW"))
            { Accept("ON"); Next(); s.Block("EXCEPTION", ParseStatements()); continue; }
            if (At("NOT") && (Peek().Is("ON") || Peek().Is("EXCEPTION") || Peek().Is("OVERFLOW")))
            { Next(); Accept("ON"); Next(); s.Block("NOT-EXCEPTION", ParseStatements()); continue; }
            break;
        }
        Accept("END-CALL");
    }

    void ParseExit(Stmt s)
    {
        Next();
        if (Accept("PROGRAM")) s.Verb = "EXIT PROGRAM";
        else if (Accept("PARAGRAPH")) s.Verb = "EXIT PARAGRAPH";
        else if (Accept("SECTION")) s.Verb = "EXIT SECTION";
        else if (Accept("PERFORM")) s.Verb = Accept("CYCLE") ? "EXIT PERFORM CYCLE" : "EXIT PERFORM";
    }

    void ParseExec(Stmt s)
    {
        Next();
        var kind = Cur.Upper;
        s.Verb = "EXEC-" + kind;
        while (!AtEnd && !At("END-EXEC")) Next();
        Accept("END-EXEC");
        s.Frontier = $"EXEC {kind} is not lowered";
    }

    // ---------------------------------------------------------------- step ids

    void AssignStepIds(Paragraph p)
    {
        int seq = 0;
        void Walk(List<Stmt> list)
        {
            foreach (var s in list)
            {
                seq++;
                s.StepId = $"{p.Name}:{seq}:{s.Verb}:L{s.Line}";
                if (s.Blocks is null) continue;
                foreach (var b in s.Blocks.Values) Walk(b);
            }
        }
        Walk(p.Statements);
    }
}
