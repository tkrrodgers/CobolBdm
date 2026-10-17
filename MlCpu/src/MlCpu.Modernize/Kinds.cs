using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

/// <summary>The value kinds of J3S storage (docs/J3S.md §5) and what Java type each is.</summary>
public enum VKind { Unknown, Bool, Int, Long, Dbl, Ref, Any, BigDec, Date, Mc, JEnum, BoxInt, BoxLong, BoxBool, Bytes }

public static class Kinds
{
    /// <summary>The kind of a J3S slot item: an elementary item, or a group whose children carry the -C/-I/-P suffixes.</summary>
    public static VKind Of(DataItem it)
    {
        if (it.IsElementary || it.IsLeaf)
        {
            if (it.Category is Category.Alphanumeric or Category.Alphabetic)
                return it.Length == 1 ? VKind.Bool : VKind.Bytes;
            if (it.Category == Category.Numeric)
            {
                if (it.Usage is Usage.Binary or Usage.Comp5)
                {
                    if (it.Scale == 0 && it.Digits == 10) return VKind.Int;
                    if (it.Scale == 0 && it.Digits == 18) return VKind.Long;
                }
                if (it.Usage == Usage.Comp3 && it.Digits == 18 && it.Scale == 5) return VKind.Dbl;
            }
            return VKind.Unknown;
        }
        var kids = it.Children.Where(c => c.Level != 88 && !c.IsFiller).Select(c => Suffix(it, c)).ToList();
        if (kids.Count == 0) return VKind.Unknown;
        if (kids.SequenceEqual(["C", "I"])) return VKind.Ref;
        if (kids.SequenceEqual(["C", "I", "V"])) return VKind.Any;
        if (kids.SequenceEqual(["C", "P"]))
        {
            var p = it.Children.First(c => Suffix(it, c) == "P");
            var pk = p.Children.Where(c => c.Level != 88 && !c.IsFiller).Select(c => Suffix(p, c)).ToList();
            if (pk.SequenceEqual(["U", "S", "Z"]) || pk.SequenceEqual(["U", "S"])) return VKind.BigDec;
            if (pk.SequenceEqual(["D"])) return VKind.Date;
            if (pk.SequenceEqual(["PR", "RM"])) return VKind.Mc;
            if (pk.SequenceEqual(["O"])) return VKind.JEnum;
            if (pk.SequenceEqual(["V"]))
            {
                var v = p.Children.First(c => c.Level != 88 && !c.IsFiller);
                if (v.Category is Category.Alphanumeric) return VKind.BoxBool;
                if (v.Digits == 18) return VKind.BoxLong;
                return VKind.BoxInt;
            }
        }
        return VKind.Unknown;
    }

    /// <summary>The suffix of a child relative to its parent: HM-AMOUNT-C under HM-AMOUNT gives C.</summary>
    public static string Suffix(DataItem parent, DataItem child)
    {
        var pn = parent.Name;
        var cn = child.Name;
        if (cn.StartsWith(pn + "-", StringComparison.OrdinalIgnoreCase)) return cn[(pn.Length + 1)..].ToUpperInvariant();
        int dash = cn.LastIndexOf('-');
        return dash < 0 ? cn : cn[(dash + 1)..].ToUpperInvariant();
    }

    public static bool IsValueKind(VKind k) => k is VKind.BigDec or VKind.Date or VKind.Mc or VKind.JEnum or VKind.BoxInt or VKind.BoxLong or VKind.BoxBool;

    /// <summary>Java class id of the value kinds (J3S §5: 1-9 are the value classes).</summary>
    public static int ValueClassId(VKind k) => k switch
    {
        VKind.BigDec => 1, VKind.Date => 2, VKind.Mc => 3, VKind.BoxInt => 4, VKind.BoxLong => 5, VKind.BoxBool => 6,
        _ => 0,
    };
}
