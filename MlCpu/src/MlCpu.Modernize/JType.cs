namespace MlCpu.Modernize;

/// <summary>A Java type: a primitive, a JDK class, or a class of the estate (with type arguments).</summary>
public sealed class JType : IEquatable<JType>
{
    public string Name { get; }
    public ClassDef? Class { get; }
    public IReadOnlyList<JType> Args { get; }

    JType(string name, ClassDef? cls, IReadOnlyList<JType>? args) { Name = name; Class = cls; Args = args ?? []; }

    public static JType Of(string name, params JType[] args) => new(name, null, args);
    public static JType Of(ClassDef c) => new(c.FullName, c, null);
    public JType With(params JType[] args) => new(Name, Class, args);

    public static readonly JType Int = Of("int"), Long = Of("long"), Bool = Of("boolean"), Double = Of("double"), Char = Of("char"), Void = Of("void");
    public static readonly JType Object = Of("java.lang.Object"), String = Of("java.lang.String"), BigDecimal = Of("java.math.BigDecimal");
    public static readonly JType LocalDate = Of("java.time.LocalDate"), MathContext = Of("java.math.MathContext"), RoundingMode = Of("java.math.RoundingMode");
    public static readonly JType ChronoUnit = Of("java.time.temporal.ChronoUnit"), ChronoField = Of("java.time.temporal.ChronoField");
    public static readonly JType Integer = Of("java.lang.Integer"), LongBox = Of("java.lang.Long"), BooleanBox = Of("java.lang.Boolean");
    public static readonly JType CharArray = Of("char[]"), IntArray = Of("int[]"), ObjectArray = Of("java.lang.Object[]");
    public static readonly JType Null = Of("null");

    public bool IsPrimitive => Name is "int" or "long" or "boolean" or "double" or "char" or "void" or "byte" or "short" or "float";
    public bool IsArray => Name.EndsWith("[]", StringComparison.Ordinal);
    public bool IsObject => Name == "java.lang.Object";
    public string Simple => Name[(Name.LastIndexOf('.') + 1)..];

    public static JType ForKind(VKind k) => k switch
    {
        VKind.Bool => Bool, VKind.Int => Int, VKind.Long => Long, VKind.Dbl => Double, VKind.BigDec => BigDecimal, VKind.Date => LocalDate,
        VKind.Mc => MathContext, VKind.BoxInt => Integer, VKind.BoxLong => LongBox, VKind.BoxBool => BooleanBox, _ => Object,
    };

    public static JType? ValueClass(int id) => id switch
    {
        1 => BigDecimal, 2 => LocalDate, 3 => MathContext, 4 => Integer, 5 => LongBox, 6 => BooleanBox, 7 => RoundingMode, 8 => ChronoUnit, 9 => ChronoField,
        _ => null,
    };

    public bool Equals(JType? o) => o is not null && o.Name == Name && Args.Count == o.Args.Count && Args.Zip(o.Args).All(p => p.First.Equals(p.Second));
    public override bool Equals(object? obj) => obj is JType t && Equals(t);
    public override int GetHashCode() => Name.GetHashCode();
    public override string ToString() => Args.Count == 0 ? Name : $"{Name}<{string.Join(", ", Args)}>";
}
