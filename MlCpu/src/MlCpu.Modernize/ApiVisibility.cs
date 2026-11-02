using System.Text.Json;
using System.Text.Json.Serialization;

namespace MlCpu.Modernize;

/// <summary>The access levels the lift prints (protected is never generated).</summary>
public enum Access { Private = 0, Package = 1, Public = 3 }

/// <summary>
/// Phase M rule R1 (api-visibility): every member gets the least access its references need. The decision is generic: it
/// sees opaque member, class, top-level type and package identities, the references between them, and per member either an
/// obligation that keeps its access (an override or interface contract, reflection, an unknown caller) or a proof that it is
/// on the external entry surface (reached from the job or replay dispatch: public). It never sees a Java or COBOL name:
/// <see cref="Member.Display"/> and <see cref="Member.SourceIds"/> only go to the ledger.
/// <list type="bullet">
/// <item>an obligation (<see cref="Member.Keep"/>): the current access, with the reason;</item>
/// <item>an entry-surface member (<see cref="Member.Entry"/>): public;</item>
/// <item>no reference, or a reference from an unknown context: the current access (unknown caller);</item>
/// <item>a reference from another package: public; from another top-level type of the package: package-private;</item>
/// <item>only references from its own top-level type (Java's private scope): private, except package-private when an
/// instance member or constructor of a class with subclasses is reached from another class (private members are not
/// inherited), or when an anonymous body in a static context (an enum constant's) reaches a member of another class.</item>
/// </list>
/// </summary>
public static class ApiVisibility
{
    public const string RuleName = "api-visibility";
    public const string UnknownContext = "?";

    public sealed class Member
    {
        /// <summary>Opaque, stable identity (built from COBOL program and function ids, or data item names).</summary>
        public required string Id { get; init; }
        /// <summary>Opaque identity of the top-level type (Java's private scope), of the declaring class, of the package.</summary>
        public required string Type { get; init; }
        public required string Class { get; init; }
        public required string Package { get; init; }
        public required string Kind { get; init; }
        public required Access Before { get; init; }
        public bool Static { get; init; }
        public bool OwnerHasSubclasses { get; init; }
        /// <summary>An obligation or an unknown caller: the member keeps <see cref="Before"/>.</summary>
        public string? Keep { get; set; }
        /// <summary>The proof that the member is on the external entry surface: public.</summary>
        public List<string> Entry { get; } = [];
        /// <summary>For the ledger only: the generated Java member, and the COBOL source ids.</summary>
        public string Display { get; init; } = "";
        public List<string> SourceIds { get; init; } = [];
    }

    /// <summary>A reference to <paramref name="Member"/> from code of a class (in a top-level type, in a package); the site is a source id.</summary>
    public sealed record Ref(string Member, string FromType, string FromClass, string FromPackage, string Site, bool FromStaticAnonymousBody = false);

    public sealed record Decision(Member M, Access After, string Scope, string Reason, List<Ref> Refs);

    public static List<Decision> Decide(IEnumerable<Member> members, IEnumerable<Ref> refs)
    {
        var byMember = refs.GroupBy(r => r.Member, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Distinct().OrderBy(r => r.FromPackage, StringComparer.Ordinal).ThenBy(r => r.FromType, StringComparer.Ordinal)
                .ThenBy(r => r.FromClass, StringComparer.Ordinal).ThenBy(r => r.Site, StringComparer.Ordinal).ThenBy(r => r.FromStaticAnonymousBody).ToList(), StringComparer.Ordinal);
        var res = new List<Decision>();
        foreach (var m in members.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            var rs = byMember.GetValueOrDefault(m.Id) ?? [];
            res.Add(One(m, rs));
        }
        return res;
    }

    static Decision One(Member m, List<Ref> rs)
    {
        if (m.Keep is { } keep) return new(m, m.Before, "kept", keep, rs);
        if (m.Entry.Count > 0) return new(m, Access.Public, "entry-surface", "on the external entry surface: " + string.Join("; ", m.Entry.Order(StringComparer.Ordinal)), rs);
        if (rs.Count == 0) return new(m, m.Before, "kept", "unknown caller: no reference in the generated graph and no entry proof", rs);
        if (rs.Any(r => r.FromType == UnknownContext || r.FromPackage == UnknownContext))
            return new(m, m.Before, "kept", "unknown caller: a reference from code whose class is not known", rs);
        if (rs.Any(r => r.FromPackage != m.Package)) return new(m, Access.Public, "other-package", "referenced from another package", rs);
        if (rs.Any(r => r.FromType != m.Type)) return new(m, Access.Package, "same-package", "referenced from another type of its package only", rs);
        if (rs.Any(r => r.FromStaticAnonymousBody && r.FromClass != m.Class))
            return new(m, Access.Package, "same-type", "referenced inside its type only, but from an anonymous body in a static context (an enum constant), which cannot reach a private member of another class", rs);
        if (!m.Static && m.OwnerHasSubclasses && rs.Any(r => r.FromClass != m.Class))
            return new(m, Access.Package, "same-type", "referenced inside its type only, but from another class of a class with subclasses (private members are not inherited)", rs);
        return new(m, Access.Private, "same-type", "referenced inside its own top-level type only", rs);
    }

    public static string Modifier(Access a) => a switch { Access.Private => "private ", Access.Public => "public ", _ => "" };

    static string Name(Access a) => a switch { Access.Private => "private", Access.Public => "public", _ => "package" };

    /// <summary>The per-rule ledger (modernize/api-visibility.json): every member's before, after, scope, references, proof or reason.</summary>
    public static string Ledger(List<Decision> decisions, object inputs, IEnumerable<object> skipped, IEnumerable<string> notes)
    {
        int Count(Func<Decision, bool> p) => decisions.Count(p);
        var doc = new
        {
            rule = "R1 " + RuleName,
            version = 1,
            evidence = new[]
            {
                "the generated estate's reference graph: calls, constructions, static and field reads and writes in the lifted IR (method bodies, static initializers, enum constant arguments), with lambda bodies attributed to the classes that print them",
                "the COBOL CALL census: every CALL '<program>' with the entry number moved to FA-ENTRY before it, the calling program and paragraph; callers outside the lifted code (job and replay drivers) are the external entry surface",
                "structural obligations of the generated Java: override and interface contracts by printed name and arity within the estate's class hierarchy, JDK supertype contracts, record accessors, abstract methods, virtual dispatch targets, reflection-folded constructions",
            },
            forbidden = "never read: original Java, J3S CLASS and FRAME comment text, legacy PEC citations; names are not decision inputs (opaque ids)",
            inputs,
            summary = new
            {
                members = decisions.Count,
                changed = Count(d => d.After != d.M.Before),
                narrowed = Count(d => d.After < d.M.Before),
                widened = Count(d => d.After > d.M.Before),
                kept = Count(d => d.Scope == "kept"),
                byScope = decisions.GroupBy(d => d.Scope).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
                byKind = decisions.GroupBy(d => d.M.Kind).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.GroupBy(d => Name(d.M.Before) + "->" + Name(d.After)).OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count())),
                keptReasons = decisions.Where(d => d.Scope == "kept").GroupBy(d => ReasonClass(d.Reason)).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            },
            notes,
            members = decisions.Select(d => new
            {
                id = d.M.Id,
                kind = d.M.Kind,
                member = d.M.Display,
                type = d.M.Type,
                @class = d.M.Class == d.M.Type ? null : d.M.Class,
                package = d.M.Package,
                before = Name(d.M.Before),
                after = Name(d.After),
                scope = d.Scope,
                reason = d.Reason,
                entryProof = d.M.Entry.Count > 0 ? d.M.Entry.Order(StringComparer.Ordinal).ToList() : null,
                references = d.Refs.Count,
                scopeRefs = d.Refs.GroupBy(r => (r.FromPackage, r.FromType)).Select(g => new { package = g.Key.FromPackage, type = g.Key.FromType, sites = g.Select(r => r.Site).Distinct().Order(StringComparer.Ordinal).Take(8).ToList(), count = g.Count() }).ToList(),
                sourceIds = d.M.SourceIds,
            }).ToList(),
            skipped,
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>The kind of a keep reason (the text before its first colon), for the summary.</summary>
    public static string ReasonClass(string reason) => reason.IndexOf(':') is var k && k > 0 ? reason[..k] : reason;
}
