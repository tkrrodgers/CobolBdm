using System.Security.Cryptography;
using System.Text;

namespace MlCpu.Modernize;

/// <summary>
/// The build-time data of a J3S target (fncobolgen.jre.JTargetData, docs/J3S.md section 17): the class-path resources the
/// target profile declares (`resources`: names, and per stored copy its bytes as hexadecimal chunks, in class-path order)
/// and the classes it may load by name (`reflection`). J3S compiled the table into the estate (the emulation's class
/// initializer); the lift evaluates that initializer, so the modern code gets the same bytes and the same class list, with
/// their provenance (program, function, SHA-256), not a copy from somewhere else.
/// </summary>
public sealed class TargetData
{
    public sealed record Resource(string Name, int Copies, byte[] Bytes, string Sha256);

    public List<string> Names { get; } = [];
    public List<Resource> Resources { get; } = [];
    public List<string> Reflect { get; } = [];
    public string Program { get; init; } = "";
    public string Function { get; init; } = "";
    public List<string> Problems { get; } = [];
    /// <summary>The functions a case can run, checked for static writes (Reflection.Lifetime); -1: not checked.</summary>
    public int CaseFunctions { get; set; } = -1;

    /// <summary>Whether the profile declares the resource name (a name it does not declare traps in the emulation).</summary>
    public bool Declared(string name) => Names.Contains(name, StringComparer.Ordinal);

    /// <summary>The stored copy of a declared resource the class path would return first (getResourceAsStream), or null when none.</summary>
    public Resource? First(string name) => Resources.FirstOrDefault(r => r.Name == name);

    public static TargetData? Load(Model m)
    {
        var fn = m.Functions.Values.FirstOrDefault(f => f.Owner is { Kind: ClassKind.Emulation } o && JdkMap.EmulationName(o) == "JTargetData" && f.MethodName == "<clinit>");
        if (fn is null) return null;
        var td = new TargetData { Program = fn.Program.Id, Function = fn.Id };
        var meth = Modernizer.LiftOne(m, fn, null);
        if (meth is null || meth.L.Residue.Count > 0) { td.Problems.Add("the table's initializer does not lift: " + string.Join("; ", meth?.L.Residue ?? [])); return td; }
        var env = new Dictionary<Var, object?>();
        var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        object? Eval(Expr e) => e switch
        {
            ELit l => l.Value,
            EVar v => env.TryGetValue(v.V, out var x) ? x : throw new InvalidDataException("unset " + v.V.Name),
            EArrayNew a => new object?[Convert.ToInt32(Eval(a.Len))],
            ECast c => Eval(c.E),
            _ => throw new InvalidDataException("expression " + e.GetType().Name),
        };
        try
        {
            foreach (var s in meth.Body)
            {
                switch (s)
                {
                    case SAssign { Lhs: EVar v, Rhs: var r }: env[v.V] = Eval(r); break;
                    case SAssign { Lhs: EIndex { A: var a, I: var i }, Rhs: var r }:
                        ((object?[])Eval(a)!)[Convert.ToInt32(Eval(i))] = Eval(r);
                        break;
                    case SAssign { Lhs: EStatic f, Rhs: var r }: fields[f.F.Name.Replace("_", "")] = Eval(r); break;
                    case SReturn { E: null }: break;
                    default: throw new InvalidDataException("statement " + s.GetType().Name);
                }
            }
        }
        catch (Exception e) when (e is InvalidDataException or InvalidCastException or IndexOutOfRangeException or FormatException)
        {
            td.Problems.Add("the table's initializer is not a constant table: " + e.Message);
            return td;
        }
        List<T> Array<T>(string name)
        {
            if (!fields.TryGetValue(name, out var a) || a is not object?[] arr) { td.Problems.Add("no table " + name); return []; }
            return arr.Select(x => x is T t ? t : x is long l && typeof(T) == typeof(int) ? (T)(object)(int)l : throw new InvalidDataException(name)).ToList();
        }
        try
        {
            td.Names.AddRange(Array<string>("NAMES"));
            td.Reflect.AddRange(Array<string>("REFLECT"));
            var owner = Array<int>("BLOBNAME");
            var length = Array<int>("BLOBLENGTH");
            var first = Array<int>("BLOBFIRST");
            var chunks = Array<string>("CHUNKS");
            if (owner.Count != length.Count || owner.Count != first.Count) td.Problems.Add("blob tables of different lengths");
            int chunk = chunks.Count == 0 ? 32 : chunks[0].Length / 2;
            for (int b = 0; b < owner.Count && td.Problems.Count == 0; b++)
            {
                var bytes = new byte[length[b]];
                for (int p = 0; p < bytes.Length; p++)
                {
                    var c = chunks[first[b] + p / chunk];
                    bytes[p] = Convert.ToByte(c.Substring(p % chunk * 2, 2), 16);
                }
                int copies = owner.Count(o => o == owner[b]);
                var name = td.Names[owner[b]];
                td.Resources.Add(new Resource(name, copies, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes))));
            }
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or FormatException)
        {
            td.Problems.Add("the table is inconsistent: " + e.Message);
        }
        return td;
    }

    /// <summary>modernize/target-data.json: what the lift took from the table, where it put it, and what the code does with it.</summary>
    public string Report(IEnumerable<string> written, IDictionary<string, string> reads, IDictionary<string, string> folds,
        IDictionary<string, string> properties, IDictionary<string, string> traps) => System.Text.Json.JsonSerializer.Serialize(new
    {
        source = new { program = Program, function = Function, emulation = "fncobolgen.jre.JTargetData" },
        declared = Names,
        resources = Resources.Select(r => new { name = r.Name, bytes = r.Bytes.Length, sha256 = r.Sha256, copies = r.Copies }).ToList(),
        absent = Names.Where(n => Resources.All(r => r.Name != n)).ToList(),
        reflection = Reflect,
        written = written.ToList(),
        reads = reads.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value),
        reflectionFolded = folds.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value),
        jobProperties = properties.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value),
        unsupportedTraps = traps.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value),
        lifetime = new
        {
            rule = "a case must not change static state: J3S resets the heap to the state after class initialization for every case, Java keeps statics for the run",
            functionsChecked = CaseFunctions,
            staticWrites = "residue when any (none in this generation if residue.json has no 'a case writes static' line)",
        },
        problems = Problems,
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    public static string Text(byte[] b) => Encoding.UTF8.GetString(b);
}
