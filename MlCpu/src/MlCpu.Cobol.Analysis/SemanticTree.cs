using System.Text.Json.Serialization;

namespace MlCpu.Cobol.Analysis;

/// <summary>Expression / condition node of the semantic tree (IR "semantics", schema mlcpu.semantic-tree.v1).</summary>
public sealed class Node
{
    /// <summary>ref | num | str | fig | all | bin | neg | fn | len | addr | rel | and | or | not | c88 | class | sign | bool | any | range</summary>
    public string K { get; set; } = "";
    public string? V { get; set; }
    public string? Op { get; set; }
    public int? Id { get; set; }
    public Node? L { get; set; }
    public Node? R { get; set; }
    public List<Node>? Args { get; set; }
    public Node? Start { get; set; }
    public Node? Len { get; set; }
    public bool? Rounded { get; set; }

    public static Node Ref(int id, string name) => new() { K = "ref", Id = id, V = name };
    public static Node Num(string v) => new() { K = "num", V = v };
    public static Node Str(string v) => new() { K = "str", V = v };
    public static Node Bool(bool b) => new() { K = "bool", V = b ? "TRUE" : "FALSE" };
}

/// <summary>A statement of the semantic tree. Nested blocks live in <see cref="Blocks"/> keyed by branch (TRUE, FALSE, LOOP, INVALID, …).</summary>
public sealed class Stmt
{
    public string Verb { get; set; } = "";
    public string StepId { get; set; } = "";
    public int Line { get; set; }
    public int Sentence { get; set; }
    public string Text { get; set; } = "";
    /// <summary>Set when the statement is not lowered: the frontier reason.</summary>
    public string? Frontier { get; set; }
    public Dictionary<string, Node>? N { get; set; }
    public Dictionary<string, List<Node>>? L { get; set; }
    public Dictionary<string, string>? S { get; set; }
    public Dictionary<string, List<Stmt>>? Blocks { get; set; }

    [JsonIgnore] public List<Token> Tokens { get; set; } = [];

    public Stmt Set(string key, Node? n) { if (n is not null) (N ??= [])[key] = n; return this; }
    public Stmt Set(string key, List<Node> l) { (L ??= [])[key] = l; return this; }
    public Stmt Set(string key, string v) { (S ??= [])[key] = v; return this; }
    public Stmt Block(string key, List<Stmt> b) { (Blocks ??= [])[key] = b; return this; }
    public Node? Get(string k) => N?.GetValueOrDefault(k);
    public string? Str(string k) => S?.GetValueOrDefault(k);
}

public sealed class Paragraph
{
    public string Name { get; set; } = "";
    public string? Section { get; set; }
    public int Line { get; set; }
    public int EndLine { get; set; }
    public bool IsSection { get; set; }
    public List<Stmt> Statements { get; set; } = [];
}

/// <summary>The analysed program: data items with layouts, files, paragraphs with the statement tree.</summary>
public sealed class CobolProgram
{
    public string ProgramId { get; set; } = "";
    public string Path { get; set; } = "";
    public List<DataItem> Items { get; set; } = [];
    public List<FileDecl> Files { get; set; } = [];
    public List<Paragraph> Paragraphs { get; set; } = [];
    public List<string> Using { get; set; } = [];
    public List<string> Copybooks { get; set; } = [];
    public List<CobolKb.MissingCopybook> MissingCopybooks { get; set; } = [];
    public List<string> Diagnostics { get; set; } = [];
    public bool ExecSql { get; set; }
    public bool ExecCics { get; set; }
}
