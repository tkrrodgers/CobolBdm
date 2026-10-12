namespace MlCpu.Cobol.Analysis.JavaBridge;

// IR v1.2 (§7.1), serialised camelCase. Layout and file-control fields marked "ext" extend v1.2 additively.

public sealed class IrRoot
{
    public string IrVersion { get; set; } = IrExporter.Version;
    public IrProgram Program { get; set; } = new();
    public List<IrField> Dictionary { get; set; } = [];
    public List<IrParagraph> Paragraphs { get; set; } = [];
    public List<IrFrontier> Frontiers { get; set; } = [];
    public List<IrName> Naming { get; set; } = [];
    public List<IrRecord> Records { get; set; } = [];
    public IrSemantics? Semantics { get; set; }
    public string? SemanticSchema { get; set; }
    /// <summary>ext: program dependence graph (paragraph call graph, control and data dependence).</summary>
    public IrGraph? Graph { get; set; }
}

public sealed class IrGraph
{
    public List<ParagraphEdge> ParagraphEdges { get; set; } = [];
    public List<CtrlDepEdge> CtrlDeps { get; set; } = [];
    public List<DataDepEdge> DataDeps { get; set; } = [];
    public List<string> DeadParagraphs { get; set; } = [];
    public List<CallSite> Calls { get; set; } = [];
    public Dictionary<string, List<string>> Cfg { get; set; } = [];
    public int DataflowIterations { get; set; }
}

public sealed class IrProgram
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public bool ExecSql { get; set; }
    public bool ExecCics { get; set; }
    public Dictionary<string, string> FileStatus { get; set; } = [];
    public Dictionary<string, string>? FileRecord { get; set; }
    public List<string>? Using { get; set; }
    public Dictionary<string, string>? RecordFile { get; set; }
    /// <summary>ext: full FILE-CONTROL + FD description.</summary>
    public List<IrFile>? Files { get; set; }
    public bool Initial { get; set; }
    public bool Recursive { get; set; }
}

public sealed class IrFile
{
    public string Name { get; set; } = "";
    public string Assign { get; set; } = "";
    public string Organization { get; set; } = "";
    public string Access { get; set; } = "";
    public int? RecordKey { get; set; }
    public List<IrAltKey>? AlternateKeys { get; set; }
    public int? FileStatus { get; set; }
    public string? RecordingMode { get; set; }
    public int MinRecord { get; set; }
    public int MaxRecord { get; set; }
    public int? RecordDependingOn { get; set; }
    public List<int> Records { get; set; } = [];
    public bool Sort { get; set; }
}

public sealed record IrAltKey(int Key, bool Duplicates);

public sealed class IrValue
{
    public string V { get; set; } = "";
    public bool Q { get; set; }
    public string? Hi { get; set; }
    public bool? Hq { get; set; }
}

public sealed class IrField
{
    public int Id { get; set; }
    public string Field { get; set; } = "";
    public string Level { get; set; } = "";
    public string Pic { get; set; } = "";
    public string Usage { get; set; } = "";
    public string Occurs { get; set; } = "";
    public string Redefines { get; set; } = "";
    public bool Signed { get; set; }
    public int ImpliedDecimals { get; set; }
    public string Type { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<int>? Dims { get; set; }
    public string? OccursOwner { get; set; }
    // ext: layout
    public int? ParentId { get; set; }
    public int RecordId { get; set; }
    public string Section { get; set; } = "";
    public int Offset { get; set; }
    public int Length { get; set; }
    public List<int>? Strides { get; set; }
    public string Category { get; set; } = "";
    public int Digits { get; set; }
    public bool SignSeparate { get; set; }
    public bool SignLeading { get; set; }
    public bool Justified { get; set; }
    public bool BlankWhenZero { get; set; }
    public List<IrValue>? Values { get; set; }
    public bool ValueAll { get; set; }
    /// <summary>An 88's WHEN SET TO FALSE literal.</summary>
    public IrValue? FalseValue { get; set; }
    public string? File { get; set; }
    public int? RedefinesId { get; set; }
    public string? DependingOn { get; set; }
    /// <summary>The OCCURS DEPENDING ON object's id (resolved with its qualifiers).</summary>
    public int? DependingOnId { get; set; }
    /// <summary>OCCURS integer-1 of OCCURS integer-1 TO integer-2 DEPENDING ON.</summary>
    public int? OccursMin { get; set; }
    /// <summary>EXTERNAL record: its storage is shared across the run unit by name.</summary>
    public bool External { get; set; }
    /// <summary>ASCENDING/DESCENDING KEY phrases of a table, major key first.</summary>
    public List<IrKey>? Keys { get; set; }
    /// <summary>INDEXED BY index items of a table, first one first.</summary>
    public List<int>? IndexedBy { get; set; }
    public int Line { get; set; }
}

public sealed record IrKey(bool Ascending, int Id);

public sealed class IrParagraph
{
    public string Name { get; set; } = "";
    public int Start { get; set; }
    public int End { get; set; }
    public List<IrStatement> Statements { get; set; } = [];
}

public sealed record IrGuard(string PredicateStepId, string Branch);

public sealed class IrStatement
{
    public string StepId { get; set; } = "";
    public int Line { get; set; }
    public string Opcode { get; set; } = "";
    public string Text { get; set; } = "";
    public List<string> Defs { get; set; } = [];
    public List<string> Uses { get; set; } = [];
    public List<IrGuard> Guards { get; set; } = [];
    public string? PerformTarget { get; set; }
    public string? PerformThru { get; set; }
    public string? GoToTarget { get; set; }
    public bool IsExit { get; set; }
    public bool EndsSentence { get; set; }
    public string? SemanticNodeId { get; set; }
    public string SemanticStatus { get; set; } = "raw";
}

public sealed record IrFrontier(string Kind, string Subject, string Detail);

public sealed record IrName(string Original, string Modern);

public sealed class IrRecord
{
    public string Cobol { get; set; } = "";
    public int Length { get; set; }
    public List<IrRecordField> Fields { get; set; } = [];
    public bool Placeable { get; set; } = true;
    public int Occurs { get; set; } = 1;
    public string? DependingOn { get; set; }
    public string? Redefines { get; set; }
}

public sealed class IrRecordField
{
    public string Cobol { get; set; } = "";
    public int Offset { get; set; }
    public int Length { get; set; }
    public int Decimals { get; set; }
    public bool Signed { get; set; }
    public bool Numeric { get; set; }
    public bool Packed { get; set; }
    public string? Pic { get; set; }
    public string? Value { get; set; }
    public bool Edited { get; set; }
    public bool Overlay { get; set; }
    public bool Binary { get; set; }
    public string? SignSeparate { get; set; }
}

public sealed class IrSemantics
{
    public string Schema { get; set; } = IrExporter.SemanticSchema;
    public List<IrSemParagraph> Paragraphs { get; set; } = [];
}

public sealed class IrSemParagraph
{
    public string Name { get; set; } = "";
    public string? Section { get; set; }
    public bool IsSection { get; set; }
    public int Line { get; set; }
    public List<Stmt> Statements { get; set; } = [];
    public bool Declarative { get; set; }
    public List<string>? UseFiles { get; set; }
    public List<string>? UseModes { get; set; }
}
