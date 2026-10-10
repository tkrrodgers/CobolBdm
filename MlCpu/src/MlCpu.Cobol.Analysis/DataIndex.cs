namespace MlCpu.Cobol.Analysis;

/// <summary>Resolves (qualified) data names, condition names and files.</summary>
public sealed class DataIndex
{
    readonly Dictionary<string, List<DataItem>> _byName = new(StringComparer.OrdinalIgnoreCase);
    public List<DataItem> Items { get; }
    public List<FileDecl> Files { get; }
    int _nextId;

    public DataIndex(List<DataItem> items, List<FileDecl> files)
    {
        Items = items; Files = files;
        _nextId = items.Count == 0 ? 1 : items.Max(i => i.Id) + 1;
        foreach (var it in items) Add(it);
    }

    void Add(DataItem it)
    {
        if (it.IsFiller) return;
        if (!_byName.TryGetValue(it.Name, out var l)) _byName[it.Name] = l = [];
        l.Add(it);
    }

    public FileDecl? File(string name) => Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool Known(string name) => _byName.ContainsKey(name) || IsSpecial(name);

    public static bool IsSpecial(string name) => name.ToUpperInvariant() is "RETURN-CODE" or "TALLY" or "SORT-RETURN";

    /// <summary>Resolve NAME [OF q1 [OF q2…]]. Returns null with a reason when unknown or ambiguous.</summary>
    public DataItem? Resolve(string name, IReadOnlyList<string> qualifiers, out string? error)
    {
        error = null;
        if (!_byName.TryGetValue(name, out var cands))
        {
            if (IsSpecial(name)) return Special(name.ToUpperInvariant());
            error = $"unresolved data name {name}"; return null;
        }
        var matches = cands.Where(c => Qualifies(c, qualifiers)).ToList();
        if (matches.Count == 1) return matches[0];
        if (matches.Count == 0) { error = $"unresolved qualification {name} OF {string.Join(" OF ", qualifiers)}"; return null; }
        // Prefer a non-redefining declaration when the same name appears twice in one record (rare); otherwise ambiguous.
        error = $"ambiguous data name {name}";
        return null;
    }

    static bool Qualifies(DataItem c, IReadOnlyList<string> qualifiers)
    {
        var p = c.Parent;
        foreach (var q in qualifiers)
        {
            while (p is not null && !string.Equals(p.Name, q, StringComparison.OrdinalIgnoreCase)) p = p.Parent;
            if (p is null)
            {
                // A file name may qualify its record.
                if (c.Record?.FileName is { } fn && string.Equals(fn, q, StringComparison.OrdinalIgnoreCase)) continue;
                return false;
            }
            p = p.Parent;
        }
        return true;
    }

    DataItem Special(string name)
    {
        var existing = Items.FirstOrDefault(i => i.Name == name && i.Level == 77 && i.Origin == "<special>");
        if (existing is not null) return existing;
        var it = new DataItem
        {
            Id = _nextId++, Level = 77, Name = name, Section = DataSection.WorkingStorage, Origin = "<special>",
            Pic = name == "TALLY" ? "9(5)" : "S9(9)", Usage = Usage.Binary, Category = Category.Numeric,
            Digits = name == "TALLY" ? 5 : 9, Signed = name != "TALLY", Length = 4
        };
        it.Record = it;
        Items.Add(it); Add(it);
        return it;
    }
}
