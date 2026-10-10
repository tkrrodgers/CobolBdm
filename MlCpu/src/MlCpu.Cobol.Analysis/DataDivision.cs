using System.Text;
using System.Text.RegularExpressions;

namespace MlCpu.Cobol.Analysis;

public enum DataSection { File, WorkingStorage, LocalStorage, Linkage }

public enum Usage { Display, Comp3, Binary, Comp5, Comp1, Comp2, Pointer, Index }

public enum Category { Group, Alphanumeric, Alphabetic, Numeric, NumericEdited, AlphanumericEdited, Condition, Pointer, Index }

/// <summary>A raw clause word with its quote flag (VALUE literals keep their text exactly).</summary>
public sealed record RawWord(string Text, bool Quoted, bool Hex = false);

/// <summary>One data description entry with its resolved byte layout (§5.4.5 RecordMap semantics).</summary>
public sealed class DataItem
{
    public int Id { get; set; }
    public int Level { get; set; }
    public string Name { get; set; } = "FILLER";
    public bool IsFiller => Name == "FILLER";
    public DataSection Section { get; set; }
    public string? Pic { get; set; }
    public Usage Usage { get; set; } = Usage.Display;
    public Category Category { get; set; }
    public int Digits { get; set; }
    public int Scale { get; set; }
    public bool Signed { get; set; }
    public bool SignSeparate { get; set; }
    public bool SignLeading { get; set; }
    public bool Justified { get; set; }
    public bool BlankWhenZero { get; set; }
    public int Occurs { get; set; }
    public int OccursMin { get; set; }
    public string? DependingOn { get; set; }
    public List<string> IndexedBy { get; } = [];
    public string? Redefines { get; set; }
    public DataItem? RedefinesItem { get; set; }
    /// <summary>VALUE literal(s). For 88 items: values, with THRU ranges as (low, high).</summary>
    public List<(RawWord Low, RawWord? High)> Values { get; } = [];
    public bool ValueAll { get; set; }
    public DataItem? Parent { get; set; }
    public List<DataItem> Children { get; } = [];
    public string? FileName { get; set; }
    public int Line { get; set; }
    public string Origin { get; set; } = "";
    public int Offset { get; set; }
    /// <summary>Length of ONE occurrence.</summary>
    public int Length { get; set; }
    /// <summary>The 01/77 record this item lives in (itself for a record).</summary>
    public DataItem Record { get; set; } = null!;
    /// <summary>OCCURS dimensions from outermost to innermost, each with its stride in bytes.</summary>
    public List<(int Count, int Stride)> Dims { get; } = [];
    public bool IsElementary => Children.Count == 0 && Level != 88;

    public override string ToString() => $"{Level:00} {Name} @{Offset}+{Length}";
}

public sealed record AlternateKey(string Name, bool Duplicates);

/// <summary>A SELECT entry joined with its FD/SD.</summary>
public sealed class FileDecl
{
    public string Name { get; set; } = "";
    public string Assign { get; set; } = "";
    public string Organization { get; set; } = "SEQUENTIAL";
    public string Access { get; set; } = "SEQUENTIAL";
    public string? RecordKey { get; set; }
    public List<AlternateKey> AlternateKeys { get; } = [];
    public string? FileStatus { get; set; }
    public bool IsSort { get; set; }
    public string? RecordingMode { get; set; }
    public int MinRecord { get; set; }
    public int MaxRecord { get; set; }
    public string? RecordDependingOn { get; set; }
    public List<DataItem> Records { get; } = [];
    public int Line { get; set; }
}

/// <summary>DATA and ENVIRONMENT division parser and layout calculator.</summary>
public static partial class DataDivisionParser
{
    [GeneratedRegex(@"^(.)\((\d+)\)")]
    private static partial Regex RepeatRx();

    public sealed record Sentence(List<RawWord> Words, int Line, string Origin);

    /// <summary>Split raw text into period-terminated sentences of raw words (quotes respected).</summary>
    public static List<Sentence> Sentences(IEnumerable<CobolKb.SourceLine> lines)
    {
        var result = new List<Sentence>();
        var words = new List<RawWord>();
        int line = 0; string origin = "";
        foreach (var sl in lines)
        {
            var s = sl.Text;
            int i = 0;
            while (i < s.Length)
            {
                if (char.IsWhiteSpace(s[i]) || s[i] == ',' && (i + 1 >= s.Length || char.IsWhiteSpace(s[i + 1])) || s[i] == ';') { i++; continue; }
                if (words.Count == 0) { line = sl.ProgramLine; origin = sl.Origin; }
                bool hex = false;
                if ((s[i] is 'X' or 'x') && i + 1 < s.Length && s[i + 1] is '\'' or '"') { hex = true; i++; }
                if (s[i] is '\'' or '"')
                {
                    char q = s[i]; var sb = new StringBuilder(); int j = i + 1;
                    while (j < s.Length)
                    {
                        if (s[j] == q) { if (j + 1 < s.Length && s[j + 1] == q) { sb.Append(q); j += 2; continue; } j++; break; }
                        sb.Append(s[j]); j++;
                    }
                    var text = sb.ToString();
                    if (hex) text = string.Concat(Enumerable.Range(0, text.Length / 2).Select(k => (char)Convert.ToInt32(text.Substring(k * 2, 2), 16)));
                    words.Add(new RawWord(text, true, hex));
                    i = j;
                    if (i < s.Length && s[i] == '.' && (i + 1 >= s.Length || char.IsWhiteSpace(s[i + 1]))) { Flush(); i++; }
                    continue;
                }
                int k2 = i;
                while (k2 < s.Length && !char.IsWhiteSpace(s[k2]) && s[k2] != '\'' && s[k2] != '"') k2++;
                var w = s.Substring(i, k2 - i);
                bool ends = false;
                if (w.EndsWith('.') && (k2 >= s.Length || char.IsWhiteSpace(s[k2]))) { w = w.Substring(0, w.Length - 1); ends = true; }
                if (w.EndsWith(',') && w.Length > 1) w = w.Substring(0, w.Length - 1);
                if (w.Length > 0) words.Add(new RawWord(w, false));
                if (ends) Flush();
                i = k2;
            }
        }
        if (words.Count > 0) Flush();
        return result;

        void Flush()
        {
            if (words.Count > 0) result.Add(new Sentence(words, line, origin));
            words = [];
        }
    }

    public static bool W(RawWord w, params string[] options) => !w.Quoted && options.Any(o => string.Equals(w.Text, o, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parse FILE-CONTROL sentences into file declarations.</summary>
    public static List<FileDecl> ParseFileControl(List<Sentence> envSentences)
    {
        var files = new List<FileDecl>();
        foreach (var s in envSentences)
        {
            var w = s.Words;
            int i = w.FindIndex(x => W(x, "SELECT"));
            if (i < 0) continue;
            var f = new FileDecl { Line = s.Line };
            i++;
            if (i < w.Count && W(w[i], "OPTIONAL")) i++;
            f.Name = w[i++].Text.ToUpperInvariant();
            while (i < w.Count)
            {
                var t = w[i];
                if (W(t, "ASSIGN"))
                {
                    i++; if (i < w.Count && W(w[i], "TO", "USING")) i++;
                    if (i < w.Count && W(w[i], "EXTERNAL", "DYNAMIC")) i++;
                    f.Assign = w[i].Text.ToUpperInvariant(); i++;
                    // AS-ddname / UT-S-ddname conventions keep only the ddname.
                    var dash = f.Assign.LastIndexOf('-');
                    if (!w[i - 1].Quoted && dash >= 0 && (f.Assign.StartsWith("UT-") || f.Assign.StartsWith("AS-") || f.Assign.StartsWith("DA-"))) f.Assign = f.Assign[(dash + 1)..];
                    continue;
                }
                if (W(t, "ORGANIZATION")) { i++; if (W(w[i], "IS")) i++; if (W(w[i], "LINE")) { f.Organization = "LINE SEQUENTIAL"; i += 2; } else { f.Organization = w[i].Text.ToUpperInvariant(); i++; } continue; }
                if (W(t, "INDEXED", "SEQUENTIAL", "RELATIVE") && f.Organization == "SEQUENTIAL") { f.Organization = t.Text.ToUpperInvariant(); i++; continue; }
                if (W(t, "ACCESS")) { i++; if (W(w[i], "MODE")) i++; if (W(w[i], "IS")) i++; f.Access = w[i].Text.ToUpperInvariant(); i++; continue; }
                if (W(t, "RECORD") && i + 1 < w.Count && (W(w[i + 1], "KEY") || W(w[i + 1], "IS")))
                {
                    i++; if (W(w[i], "KEY")) i++; if (W(w[i], "IS")) i++;
                    f.RecordKey = w[i].Text.ToUpperInvariant(); i++; continue;
                }
                if (W(t, "ALTERNATE"))
                {
                    i++; if (W(w[i], "RECORD")) i++; if (W(w[i], "KEY")) i++; if (W(w[i], "IS")) i++;
                    var name = w[i].Text.ToUpperInvariant(); i++;
                    bool dup = false;
                    if (i < w.Count && W(w[i], "WITH")) i++;
                    if (i < w.Count && W(w[i], "DUPLICATES")) { dup = true; i++; }
                    f.AlternateKeys.Add(new AlternateKey(name, dup));
                    continue;
                }
                if (W(t, "FILE") && i + 1 < w.Count && W(w[i + 1], "STATUS")) { i += 2; if (W(w[i], "IS")) i++; f.FileStatus = w[i].Text.ToUpperInvariant(); i++; continue; }
                if (W(t, "STATUS") ) { i++; if (W(w[i], "IS")) i++; f.FileStatus = w[i].Text.ToUpperInvariant(); i++; continue; }
                i++;
            }
            files.Add(f);
        }
        return files;
    }

    /// <summary>Parse DATA DIVISION sentences into items, attach FDs to files, and compute layouts.</summary>
    public static List<DataItem> ParseData(List<Sentence> sentences, List<FileDecl> files, List<string> diagnostics)
    {
        var items = new List<DataItem>();
        var section = DataSection.WorkingStorage;
        FileDecl? currentFd = null;
        var stack = new Stack<DataItem>();
        int nextId = 1;
        foreach (var s in sentences)
        {
            var w = s.Words;
            if (w.Count == 0) continue;
            if (w.Count >= 2 && W(w[1], "SECTION"))
            {
                section = w[0].Text.ToUpperInvariant() switch
                {
                    "FILE" => DataSection.File,
                    "LINKAGE" => DataSection.Linkage,
                    "LOCAL-STORAGE" => DataSection.LocalStorage,
                    _ => DataSection.WorkingStorage
                };
                stack.Clear(); currentFd = null;
                continue;
            }
            if (W(w[0], "FD", "SD"))
            {
                var name = w[1].Text.ToUpperInvariant();
                currentFd = files.FirstOrDefault(f => f.Name == name);
                if (currentFd is null) { currentFd = new FileDecl { Name = name }; files.Add(currentFd); diagnostics.Add($"FD {name} has no SELECT"); }
                currentFd.IsSort = W(w[0], "SD");
                ParseFdClauses(currentFd, w);
                stack.Clear();
                continue;
            }
            if (!int.TryParse(w[0].Text, out var level) || w[0].Quoted) continue;
            var item = new DataItem { Id = nextId++, Level = level, Section = section, Line = s.Line, Origin = s.Origin };
            int i = 1;
            if (i < w.Count && !IsClauseWord(w[i]))
            {
                item.Name = w[i].Text.ToUpperInvariant();
                i++;
            }
            ParseClauses(item, w, i, diagnostics);
            if (level == 1 || level == 77 || level == 66)
            {
                stack.Clear();
                if (section == DataSection.File && currentFd is not null) { item.FileName = currentFd.Name; currentFd.Records.Add(item); }
            }
            else if (level == 88)
            {
                if (stack.Count > 0) { item.Parent = stack.Peek(); stack.Peek().Children.Add(item); }
                item.Category = Category.Condition;
                items.Add(item);
                continue;
            }
            else
            {
                while (stack.Count > 0 && stack.Peek().Level >= level) stack.Pop();
                if (stack.Count > 0) { item.Parent = stack.Peek(); stack.Peek().Children.Add(item); }
            }
            stack.Push(item);
            items.Add(item);
        }
        foreach (var owner in items.Where(x => x.IndexedBy.Count > 0).ToList())
            foreach (var idx in owner.IndexedBy)
                items.Add(new DataItem { Id = nextId++, Level = 77, Name = idx, Section = DataSection.WorkingStorage, Usage = Usage.Index, Line = owner.Line, Origin = owner.Origin });
        foreach (var it in items) Classify(it);
        Layout(items, files, diagnostics);
        return items;
    }

    static void ParseFdClauses(FileDecl fd, List<RawWord> w)
    {
        for (int i = 2; i < w.Count; i++)
        {
            if (W(w[i], "RECORDING")) { i++; if (W(w[i], "MODE")) i++; if (W(w[i], "IS")) i++; fd.RecordingMode = w[i].Text.ToUpperInvariant(); }
            else if (W(w[i], "RECORD"))
            {
                int j = i + 1;
                if (j < w.Count && W(w[j], "IS")) j++;
                if (j < w.Count && W(w[j], "VARYING"))
                {
                    j++; if (W(w[j], "IN")) j++; if (W(w[j], "SIZE")) j++;
                    if (W(w[j], "FROM")) j++;
                    if (int.TryParse(w[j].Text, out var mn)) { fd.MinRecord = mn; j++; }
                    if (W(w[j], "TO")) j++;
                    if (int.TryParse(w[j].Text, out var mx)) { fd.MaxRecord = mx; j++; }
                    if (j < w.Count && W(w[j], "CHARACTERS")) j++;
                    if (j < w.Count && W(w[j], "DEPENDING")) { j++; if (W(w[j], "ON")) j++; fd.RecordDependingOn = w[j].Text.ToUpperInvariant(); }
                }
                else if (j < w.Count && W(w[j], "CONTAINS")) { j++; if (int.TryParse(w[j].Text, out var n)) fd.MaxRecord = n; }
                i = j;
            }
        }
    }

    static readonly HashSet<string> ClauseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "PIC","PICTURE","USAGE","VALUE","VALUES","OCCURS","REDEFINES","SIGN","JUST","JUSTIFIED","BLANK","SYNC","SYNCHRONIZED",
        "EXTERNAL","GLOBAL","COMP","COMP-1","COMP-2","COMP-3","COMP-4","COMP-5","COMPUTATIONAL","COMPUTATIONAL-1","COMPUTATIONAL-2",
        "COMPUTATIONAL-3","COMPUTATIONAL-4","COMPUTATIONAL-5","BINARY","PACKED-DECIMAL","DISPLAY","POINTER","INDEX","RENAMES","IS","LEADING","TRAILING"
    };

    static bool IsClauseWord(RawWord w) => !w.Quoted && ClauseWords.Contains(w.Text);

    static void ParseClauses(DataItem item, List<RawWord> w, int i, List<string> diagnostics)
    {
        while (i < w.Count)
        {
            var t = w[i];
            if (W(t, "FILLER")) { i++; continue; }
            if (W(t, "PIC", "PICTURE")) { i++; if (W(w[i], "IS")) i++; item.Pic = w[i].Text.ToUpperInvariant(); i++; continue; }
            if (W(t, "USAGE")) { i++; if (i < w.Count && W(w[i], "IS")) i++; continue; }
            if (W(t, "COMP-3", "COMPUTATIONAL-3", "PACKED-DECIMAL")) { item.Usage = Usage.Comp3; i++; continue; }
            if (W(t, "COMP", "COMPUTATIONAL", "COMP-4", "COMPUTATIONAL-4", "BINARY")) { item.Usage = Usage.Binary; i++; continue; }
            if (W(t, "COMP-5", "COMPUTATIONAL-5")) { item.Usage = Usage.Comp5; i++; continue; }
            if (W(t, "COMP-1", "COMPUTATIONAL-1")) { item.Usage = Usage.Comp1; i++; continue; }
            if (W(t, "COMP-2", "COMPUTATIONAL-2")) { item.Usage = Usage.Comp2; i++; continue; }
            if (W(t, "POINTER")) { item.Usage = Usage.Pointer; i++; continue; }
            if (W(t, "INDEX")) { item.Usage = Usage.Index; i++; continue; }
            if (W(t, "DISPLAY")) { i++; continue; }
            if (W(t, "REDEFINES")) { item.Redefines = w[i + 1].Text.ToUpperInvariant(); i += 2; continue; }
            if (W(t, "JUST", "JUSTIFIED")) { item.Justified = true; i++; if (i < w.Count && W(w[i], "RIGHT")) i++; continue; }
            if (W(t, "BLANK")) { item.BlankWhenZero = true; i++; if (i < w.Count && W(w[i], "WHEN")) i++; if (i < w.Count && W(w[i], "ZERO", "ZEROS", "ZEROES")) i++; continue; }
            if (W(t, "SIGN"))
            {
                i++; if (i < w.Count && W(w[i], "IS")) i++;
                item.SignLeading = W(w[i], "LEADING"); i++;
                if (i < w.Count && W(w[i], "SEPARATE")) { item.SignSeparate = true; i++; if (i < w.Count && W(w[i], "CHARACTER")) i++; }
                continue;
            }
            if (W(t, "LEADING", "TRAILING"))
            {
                item.SignLeading = W(t, "LEADING"); i++;
                if (i < w.Count && W(w[i], "SEPARATE")) { item.SignSeparate = true; i++; if (i < w.Count && W(w[i], "CHARACTER")) i++; }
                continue;
            }
            if (W(t, "OCCURS"))
            {
                i++;
                int n1 = int.Parse(w[i].Text); i++;
                item.Occurs = n1; item.OccursMin = n1;
                if (i < w.Count && W(w[i], "TO")) { item.Occurs = int.Parse(w[i + 1].Text); i += 2; }
                if (i < w.Count && W(w[i], "TIMES")) i++;
                while (i < w.Count)
                {
                    if (W(w[i], "DEPENDING")) { i++; if (W(w[i], "ON")) i++; item.DependingOn = w[i].Text.ToUpperInvariant(); i++; continue; }
                    if (W(w[i], "ASCENDING", "DESCENDING"))
                    {
                        i++; if (W(w[i], "KEY")) i++; if (W(w[i], "IS")) i++;
                        while (i < w.Count && !IsClauseWord(w[i]) && !W(w[i], "INDEXED", "ASCENDING", "DESCENDING")) i++;
                        continue;
                    }
                    if (W(w[i], "INDEXED"))
                    {
                        i++; if (W(w[i], "BY")) i++;
                        while (i < w.Count && !IsClauseWord(w[i]) && !W(w[i], "ASCENDING", "DESCENDING")) { item.IndexedBy.Add(w[i].Text.ToUpperInvariant()); i++; }
                        continue;
                    }
                    break;
                }
                continue;
            }
            if (W(t, "VALUE", "VALUES"))
            {
                i++;
                if (i < w.Count && W(w[i], "IS", "ARE")) i++;
                while (i < w.Count && !IsClauseWord(w[i]))
                {
                    if (W(w[i], "ALL")) { item.ValueAll = true; i++; continue; }
                    var low = w[i]; i++;
                    RawWord? high = null;
                    if (i < w.Count && W(w[i], "THRU", "THROUGH")) { high = w[i + 1]; i += 2; }
                    item.Values.Add((low, high));
                    if (item.Level != 88) break;
                }
                continue;
            }
            if (W(t, "SYNC", "SYNCHRONIZED", "EXTERNAL", "GLOBAL", "IS", "LEFT", "RIGHT")) { i++; continue; }
            diagnostics.Add($"L{item.Line}: unrecognised data clause '{t.Text}' on {item.Name}");
            i++;
        }
    }

    /// <summary>Expand repeat counts: 9(05) → 99999.</summary>
    public static string ExpandPic(string pic)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < pic.Length; i++)
        {
            var m = RepeatRx().Match(pic.Substring(i));
            if (m.Success) { sb.Append(m.Groups[1].Value[0], int.Parse(m.Groups[2].Value)); i += m.Length - 1; }
            else sb.Append(pic[i]);
        }
        return sb.ToString();
    }

    static void Classify(DataItem it)
    {
        if (it.Level == 88) { it.Category = Category.Condition; return; }
        if (it.Usage == Usage.Pointer) { it.Category = Category.Pointer; return; }
        if (it.Usage == Usage.Index) { it.Category = Category.Index; return; }
        if (it.Usage is Usage.Comp1 or Usage.Comp2 && it.Pic is null) { it.Category = Category.Numeric; it.Signed = true; it.Digits = it.Usage == Usage.Comp1 ? 7 : 16; return; }
        if (it.Pic is null) { it.Category = Category.Group; return; }
        var p = ExpandPic(it.Pic);
        bool hasX = p.Contains('X'), hasA = p.Contains('A');
        bool editSymbols = p.IndexOfAny(['Z', '*', '+', '-', ',', '.', '$', 'B', '0', '/']) >= 0 || p.Contains("CR") || p.Contains("DB");
        if (!hasX && !hasA && !editSymbols)
        {
            it.Category = Category.Numeric;
            it.Signed = p.StartsWith('S');
            var v = p.IndexOf('V');
            var body = p.Replace("S", "");
            it.Digits = body.Count(c => c == '9');
            it.Scale = v < 0 ? 0 : p.Substring(v + 1).Count(c => c == '9');
            int pCount = body.Count(c => c == 'P');
            if (pCount > 0)
            {
                // P scaling: leading P's add decimal positions; trailing P's are a negative scale.
                if (body.StartsWith('P') || body.StartsWith("VP")) it.Scale = it.Digits + pCount; else it.Scale = -pCount;
            }
            return;
        }
        if ((hasX || hasA) && !editSymbols) { it.Category = hasX ? Category.Alphanumeric : Category.Alphabetic; return; }
        if (hasX || hasA) { it.Category = Category.AlphanumericEdited; return; }
        it.Category = Category.NumericEdited;
        // Digit positions: 9, Z, *, and floating insertion symbols beyond the first.
        int digits = p.Count(c => c is '9' or 'Z' or '*');
        foreach (var f in new[] { '+', '-', '$' })
        {
            int cnt = p.Count(c => c == f);
            if (cnt > 1) digits += cnt - 1;
        }
        it.Digits = digits;
        var dot = p.IndexOf('.');
        var vv = p.IndexOf('V');
        int dp = dot >= 0 ? dot : vv;
        it.Scale = dp < 0 ? 0 : p.Substring(dp + 1).Count(c => c is '9' or 'Z' or '*' or '+' or '-' or '$');
        if (p.EndsWith("CR") || p.EndsWith("DB")) it.Scale = dp < 0 ? 0 : p.Substring(dp + 1, p.Length - dp - 3).Count(c => c is '9' or 'Z' or '*');
        it.Signed = p.Contains('+') || p.Contains('-') || p.Contains("CR") || p.Contains("DB");
    }

    /// <summary>Byte width of one occurrence of an elementary item.</summary>
    public static int ElementaryWidth(DataItem it)
    {
        switch (it.Usage)
        {
            case Usage.Pointer:
            case Usage.Index: return 4;
            case Usage.Comp1: return 4;
            case Usage.Comp2: return 8;
            case Usage.Comp3: return it.Digits / 2 + 1;
            case Usage.Binary:
            case Usage.Comp5:
                return it.Digits <= 4 ? 2 : it.Digits <= 9 ? 4 : 8;
        }
        if (it.Pic is null) return 0;
        var p = ExpandPic(it.Pic);
        if (it.Category == Category.Numeric)
            return it.Digits + (it.SignSeparate ? 1 : 0);
        if (it.Category is Category.Alphanumeric or Category.Alphabetic or Category.AlphanumericEdited)
            return p.Length;
        // Numeric edited: every symbol occupies one byte except V; CR/DB occupy two (already two chars).
        return p.Count(c => c != 'V' && c != 'S' && c != 'P');
    }

    static void Layout(List<DataItem> items, List<FileDecl> files, List<string> diagnostics)
    {
        var byName = new Dictionary<string, List<DataItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items) { if (!byName.TryGetValue(it.Name, out var l)) byName[it.Name] = l = []; l.Add(it); }

        DataItem? previousRecord = null;
        foreach (var rec in items.Where(i => i.Parent is null && i.Level != 88))
        {
            rec.Offset = 0;
            ComputeSize(rec, diagnostics);
            SetRecord(rec, rec);
            if (rec.Redefines is not null && rec.Level == 1)
            {
                // 01 REDEFINES 01: one shared buffer (Appendix A).
                rec.RedefinesItem = previousRecord?.Name == rec.Redefines ? previousRecord
                    : items.FirstOrDefault(x => x.Parent is null && x.Name == rec.Redefines && x != rec);
            }
            if (rec.Redefines is null) previousRecord = rec;
        }
        foreach (var rec in items.Where(i => i.Parent is null && i.Level != 88))
            AssignOffsets(rec, rec.Offset, []);
        foreach (var f in files)
            if (f.MaxRecord == 0 && f.Records.Count > 0) f.MaxRecord = f.Records.Max(r => r.Length);
    }

    static void SetRecord(DataItem it, DataItem rec)
    {
        it.Record = rec;
        foreach (var c in it.Children) SetRecord(c, rec);
    }

    /// <summary>Compute <see cref="DataItem.Length"/> bottom-up. Subordinate REDEFINES overlay their target and do not add bytes.</summary>
    static int ComputeSize(DataItem it, List<string> diagnostics)
    {
        if (it.Level == 88) return 0;
        if (it.Children.Count(c => c.Level != 88) == 0)
        {
            it.Length = ElementaryWidth(it);
            if (it.Pic is null && it.Usage == Usage.Display && it.Level != 66) diagnostics.Add($"L{it.Line}: {it.Name} has no PICTURE and no children");
            return it.Length * Math.Max(1, it.Occurs);
        }
        int cursor = 0, max = 0;
        var starts = new Dictionary<DataItem, int>();
        foreach (var c in it.Children.Where(c => c.Level != 88))
        {
            int size = ComputeSize(c, diagnostics);
            if (c.Redefines is not null)
            {
                var target = it.Children.FirstOrDefault(x => x.Name == c.Redefines && x != c);
                if (target is null) { diagnostics.Add($"L{c.Line}: REDEFINES target {c.Redefines} not found"); continue; }
                c.RedefinesItem = target;
                starts[c] = starts.GetValueOrDefault(target);
                max = Math.Max(max, starts[c] + size);
                continue;
            }
            starts[c] = cursor;
            cursor += size;
            max = Math.Max(max, cursor);
        }
        foreach (var (c, s) in starts) c.Offset = s;
        it.Length = max;
        return it.Length * Math.Max(1, it.Occurs);
    }

    static void AssignOffsets(DataItem it, int absolute, List<(int, int)> dims)
    {
        it.Offset = absolute;
        var childDims = it.Occurs > 0 ? [.. dims, (it.Occurs, it.Length)] : dims;
        it.Dims.Clear();
        it.Dims.AddRange(childDims);
        foreach (var c in it.Children)
        {
            if (c.Level == 88) { c.Offset = it.Offset; c.Length = it.Length; c.Dims.Clear(); c.Dims.AddRange(childDims); c.Record = it.Record; continue; }
            AssignOffsets(c, absolute + c.Offset, childDims);
        }
    }
}
