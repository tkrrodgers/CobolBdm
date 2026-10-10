using System.Text.RegularExpressions;

namespace MlCpu.Core;

public enum SourceLanguage { Cobol, Easytrieve, Unknown }

/// <summary>Content classification (§5.1). An unreadable file counts as COBOL so I/O errors never shrink the inventory.</summary>
public static partial class CobolSourceClassifier
{
    [GeneratedRegex(@"IDENTIFICATION\s+DIVISION|\bID\s+DIVISION", RegexOptions.IgnoreCase)]
    private static partial Regex IdDivision();

    [GeneratedRegex(@"\bPIC(TURE)?\s", RegexOptions.IgnoreCase)]
    private static partial Regex Picture();

    [GeneratedRegex(@"\bJOB\s+INPUT\b", RegexOptions.IgnoreCase)]
    private static partial Regex EztJob();

    public static SourceLanguage ClassifyText(string? text)
    {
        if (text is null) return SourceLanguage.Cobol;
        if (IdDivision().IsMatch(text) || Picture().IsMatch(text)) return SourceLanguage.Cobol;
        if (EztJob().IsMatch(text)) return SourceLanguage.Easytrieve;
        return SourceLanguage.Unknown;
    }

    public static SourceLanguage Classify(string path)
    {
        try { return ClassifyText(File.ReadAllText(path)); }
        catch (IOException) { return SourceLanguage.Cobol; }
        catch (UnauthorizedAccessException) { return SourceLanguage.Cobol; }
    }

    public static bool IsCobol(string path) => Classify(path) == SourceLanguage.Cobol;
}
