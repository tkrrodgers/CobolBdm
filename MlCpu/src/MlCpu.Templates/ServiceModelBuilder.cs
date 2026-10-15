using System.Text.Json.Nodes;

namespace MlCpu.Templates;

/// <summary>
/// Builds the service model (mlcpu.service-model.v1, GemmaJavaTemplates.md §3.2) for an api-first service from the fixed API
/// (mlcpu.api-model.v1, ApiImporter) and the SME's host mapping (mlcpu.host-mapping.v1): which record each aggregate stores, which
/// API calls become host services, which ports the templates implement and which are holes. Everything else is derived, in a fixed
/// order, so the same inputs give the same bytes:
/// <list type="bullet">
/// <item>aggregates: the root record flattened into one entity (nested records become prefixed columns, a component the mapping
/// declares the same as another is not stored twice), and each list of records (or of a sealed interface's records, with a
/// discriminator) into a child entity keyed by the parent key and a sequence number;</item>
/// <item>codecs: one per (record, prefix) pair, the read and write order the generated Java follows;</item>
/// <item>enums: the API enums the stored records and the service inputs use, with their host ids;</item>
/// <item>services: each call's command flattened into input attributes, with the host's types.</item>
/// </list>
/// </summary>
public sealed class ServiceModelBuilder
{
    readonly Dictionary<string, JsonObject> types = new(StringComparer.Ordinal);
    readonly JsonObject mapping;
    readonly List<string> problems = [];
    readonly SortedDictionary<string, JsonObject> enumsUsed = new(StringComparer.Ordinal);
    readonly List<JsonObject> codecs = [];
    readonly HashSet<string> codecNames = new(StringComparer.Ordinal);
    string currentAggregate = "";

    public IReadOnlyList<string> Problems => problems;

    public ServiceModelBuilder(JsonObject apiModel, JsonObject hostMapping)
    {
        foreach (var t in apiModel["types"]!.AsArray().OfType<JsonObject>()) types[(string)t["name"]!] = t;
        mapping = hostMapping;
    }

    static string Pascal(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    static string Camel(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    /// <summary>UPPER_SNAKE of a camel or Pascal name: InstallmentLine -> INSTALLMENT_LINE.</summary>
    public static string UpperSnake(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && (char.IsLower(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1])))) sb.Append('_');
            sb.Append(char.ToUpperInvariant(s[i]));
        }
        return sb.ToString();
    }

    string Str(JsonNode? n, string key, string fallback = "") => n?[key]?.GetValue<string>() ?? fallback;

    JsonObject Map(string key) => mapping[key] as JsonObject ?? [];

    /// <summary>The kind of a type reference: string, decimal, int, long, boolean, date, enum, record, sealed, list, or unsupported.</summary>
    (string Kind, string? Ref, JsonObject? Element) Classify(JsonObject typeRef)
    {
        var fqn = (string)typeRef["fqn"]!;
        var simple = (string)typeRef["simple"]!;
        switch (fqn)
        {
            case "java.lang.String": return ("string", null, null);
            case "java.math.BigDecimal": return ("decimal", null, null);
            case "int" or "java.lang.Integer" or "short" or "java.lang.Short": return ("int", null, null);
            case "long" or "java.lang.Long": return ("long", null, null);
            case "boolean" or "java.lang.Boolean": return ("boolean", null, null);
            case "java.time.LocalDate": return ("date", null, null);
            case "java.util.List":
                return ("list", null, typeRef["args"]!.AsArray()[0] as JsonObject);
        }
        if (typeRef["enum"]?.GetValue<bool>() == true) return ("enum", fqn, null);
        if (types.TryGetValue(simple, out var t))
            return ((string)t["kind"]! switch { "record" => "record", "sealed" => "sealed", "enum" => "enum", _ => "unsupported" }, simple, null);
        return ("unsupported", fqn, null);
    }

    static bool Nullable(JsonObject typeRef) => typeRef["primitive"]?.GetValue<bool>() != true;

    /// <summary>The model: service, enums, entities (aggregates and their children, then host-only entities), codecs, services, ports.</summary>
    public JsonObject Build()
    {
        var model = new JsonObject
        {
            ["schema"] = "mlcpu.service-model.v1",
            ["service"] = Map("service").DeepClone(),
            ["api"] = Api(),
            ["plugin"] = Map("plugin").DeepClone(),
        };
        var entities = new JsonArray();
        var aggregates = new JsonArray();
        foreach (var agg in (mapping["aggregates"] as JsonArray ?? []).OfType<JsonObject>())
            aggregates.Add(Aggregate(agg, entities));
        foreach (var e in (mapping["entities"] as JsonArray ?? []).OfType<JsonObject>())
            entities.Add(HostEntity(e));
        var services = new JsonArray();
        foreach (var s in (mapping["services"] as JsonArray ?? []).OfType<JsonObject>()) services.Add(Service(s));
        var statusEnums = (Map("enums")["status"] as JsonArray ?? []).Select(x => (string)x!).ToHashSet(StringComparer.Ordinal);
        var enums = new JsonArray();
        foreach (var (_, e) in enumsUsed)
        {
            e["status"] = statusEnums.Contains((string)e["name"]!);
            enums.Add(e);
        }
        FkNames(entities);
        model["enums"] = enums;
        model["aggregates"] = aggregates;
        model["entities"] = entities;
        model["codecs"] = new JsonArray(codecs.Select(c => (JsonNode)c).ToArray());
        model["services"] = services;
        model["ports"] = Ports();
        model["seca"] = (mapping["seca"] as JsonArray ?? []).DeepClone();
        model["seed"] = Rows(mapping["seed"] as JsonArray);
        model["demo"] = Rows(mapping["demo"] as JsonArray);
        model["testData"] = Rows(mapping["testData"] as JsonArray);
        model["facade"] = Map("facade").DeepClone();
        return model;
    }

    /// <summary>
    /// A foreign-key name for every relation, unique in the model and at most 18 characters (OFBiz names an unnamed one after the
    /// related entity alone, so two relations to Loan would both be LOAN): the first 3 letters of each word of the entity, then
    /// of the title and related entity; a clash or an overflow takes a 2-character hash.
    /// </summary>
    void FkNames(JsonArray entities)
    {
        static string Abbrev(string name) => string.Join("", UpperSnake(name).Split('_').Select(w => w.Length <= 3 ? w : w[..3]));
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entities.OfType<JsonObject>())
            foreach (var r in e["relations"]!.AsArray().OfType<JsonObject>())
            {
                var full = $"{Abbrev((string)e["name"]!)}_{Abbrev(((string?)r["title"] ?? "") + (string)r["entity"]!)}";
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                    $"{e["name"]}.{r["title"]}.{r["entity"]}.{r["fields"]![0]}")))[..2];
                var name = full.Length <= 18 ? full : full[..15] + "_" + hash;
                if (!used.Add(name)) { name = (name.Length > 15 ? name[..15] : name) + "_" + hash; used.Add(name); }
                r["fkName"] = name;
            }
    }

    /// <summary>The mapping's api section, with the ports record's fully qualified name.</summary>
    JsonObject Api()
    {
        var api = Map("api").DeepClone().AsObject();
        var ports = Str(api, "ports");
        if (types.TryGetValue(ports, out var t)) api["portsFqn"] = t["fqn"]!.DeepClone();
        else problems.Add($"api.ports: no record {ports} in the API");
        return api;
    }

    /// <summary>Data rows as entity and attribute list, the rows marked first (types other rows refer to) ahead, else in the mapping's order.</summary>
    static JsonArray Rows(JsonArray? rows)
    {
        var list = (rows ?? []).OfType<JsonObject>().ToList();
        var ordered = list.Where(r => r["first"]?.GetValue<bool>() == true).Concat(list.Where(r => r["first"]?.GetValue<bool>() != true));
        return new JsonArray(ordered.Select(r => (JsonNode)new JsonObject
        {
            ["entity"] = (string)r["entity"]!,
            ["attributes"] = new JsonArray(r.Where(kv => kv.Key is not ("entity" or "first"))
                .Select(kv => (JsonNode)new JsonObject { ["name"] = kv.Key, ["value"] = kv.Value?.ToString() }).ToArray())
        }).ToArray());
    }

    /// <summary>An enum the stored records or the inputs use: its constants and the host id of each (PREFIX_TYPE_CONSTANT).</summary>
    JsonObject UseEnum(string fqn)
    {
        var simple = fqn[(fqn.LastIndexOf('.') + 1)..];
        if (enumsUsed.TryGetValue(simple, out var known)) return known;
        string[] constants = fqn switch
        {
            "java.math.RoundingMode" => ["UP", "DOWN", "CEILING", "FLOOR", "HALF_UP", "HALF_DOWN", "HALF_EVEN", "UNNECESSARY"],
            _ when types.TryGetValue(simple, out var t) => t["constants"]!.AsArray().Select(x => (string)x!).ToArray(),
            _ => throw new InvalidOperationException($"unknown enum {fqn}")
        };
        var enums = Map("enums");
        var prefix = Str(enums, "prefix", "X");
        var limit = enums["idLength"]?.GetValue<long>() is long l ? (int)l : 20;
        var spec = (enums["types"] as JsonObject ?? [])[simple] as JsonObject ?? [];
        var snake = UpperSnake(simple);
        var typeId = Str(spec, "typeId", snake.StartsWith(prefix + "_", StringComparison.Ordinal) ? snake : $"{prefix}_{snake}");
        // A constant's host id: the prefix, the type's code (its words' initials unless the mapping names one; empty is allowed)
        // and the constant, shortened deterministically when the host's id is shorter (vowels after each word's first letter go,
        // then a cut with a 2-character hash), unless the mapping gives the id.
        var code = spec["code"] is { } cn ? (string)cn! : string.Concat(snake.Split('_').Select(w => w[0]));
        var overrides = spec["ids"] as JsonObject ?? [];
        var e = new JsonObject
        {
            ["name"] = simple, ["fqn"] = fqn, ["typeId"] = Fit(typeId, limit, $"enum type of {simple}"),
            ["constants"] = new JsonArray(constants.Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c, ["id"] = overrides[c]?.GetValue<string>() ?? Fit(code.Length == 0 ? $"{prefix}_{c}" : $"{prefix}_{code}_{c}", limit, $"{simple}.{c}")
            }).ToArray())
        };
        foreach (var c in e["constants"]!.AsArray())
            if (!hostIds.Add((string)c!["id"]!)) problems.Add($"{simple}.{c["name"]}: id {c["id"]} is not unique; name one in enums.types.{simple}.ids");
        enumsUsed[simple] = e;
        return e;
    }

    readonly HashSet<string> hostIds = new(StringComparer.Ordinal);

    /// <summary>An id cut to the host's length: vowels after each word's first letter go, then a cut with a 2-character hash.</summary>
    string Fit(string id, int limit, string what)
    {
        if (id.Length <= limit) return id;
        var words = id.Split('_');
        var head = words[0];
        var rest = string.Join("_", words.Skip(1).Select(w => w.Length <= 1 ? w : w[0] + new string(w[1..].Where(ch => "AEIOU".IndexOf(ch) < 0).ToArray())));
        var shorter = $"{head}_{rest}";
        if (shorter.Length <= limit) return shorter;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..2];
        var cut = shorter[..(limit - 3)] + "_" + hash;
        if (cut.Length > limit) problems.Add($"{what}: id {id} cannot fit {limit} characters");
        return cut;
    }

    string HostType(string kind, string column, JsonObject overrides) => overrides[column]?.GetValue<string>() ?? kind switch
    {
        "string" => "id-long",
        "decimal" => "fixed-point",
        "int" or "long" => "numeric",
        "boolean" => "indicator",
        "date" => "date",
        "enum" => "id",
        _ => "id-long"
    };

    /// <summary>
    /// A record flattened under a column prefix: its codec (read and write order), and its columns added to the entity's list. A
    /// nested record becomes its own codec; a list becomes a child entity (the aggregate's job).
    /// </summary>
    string Codec(string record, string path, string columnPrefix, JsonObject agg, JsonArray columns, List<(string Path, JsonObject Element, string Field)> lists,
        HashSet<string> columnNames)
    {
        var name = record + Pascal(columnPrefix);
        while (!codecNames.Add(name)) name += "X";
        var codec = new JsonObject { ["name"] = name, ["record"] = record, ["fqn"] = types[record]["fqn"]!.DeepClone(), ["aggregate"] = currentAggregate,
            ["fields"] = new JsonArray() };
        codecs.Add(codec);
        var prefixes = agg["prefixes"] as JsonObject ?? [];
        var sameAs = agg["sameAs"] as JsonObject ?? [];
        var renames = agg["columns"] as JsonObject ?? [];
        var hostTypes = agg["fieldTypes"] as JsonObject ?? [];
        foreach (var c in types[record]["components"]!.AsArray().OfType<JsonObject>())
        {
            var cname = (string)c["name"]!;
            var cpath = path.Length == 0 ? cname : $"{path}.{cname}";
            var typeRef = (JsonObject)c["type"]!;
            var (kind, rf, element) = Classify(typeRef);
            var field = new JsonObject
            {
                ["name"] = cname, ["path"] = cpath, ["kind"] = kind, ["javaType"] = JavaType(typeRef), ["nullable"] = Nullable(typeRef), ["stored"] = false,
                ["column"] = null, ["codec"] = null, ["child"] = null, ["elementType"] = null, ["enum"] = null, ["enumTypeId"] = null, ["enumFqn"] = null
            };
            ((JsonArray)codec["fields"]!).Add(field);
            if (sameAs[cpath] is { } same)
            {
                field["column"] = (string)same!;
                field["stored"] = false;
                if (kind == "enum") { field["enum"] = UseEnum(rf!)["name"]!.GetValue<string>(); field["enumTypeId"] = UseEnum(rf!)["typeId"]!.DeepClone(); field["enumFqn"] = rf; }
                continue;
            }
            switch (kind)
            {
                case "string" or "decimal" or "int" or "long" or "boolean" or "date" or "enum":
                {
                    var column = renames[cpath]?.GetValue<string>()
                        ?? (columnPrefix.Length == 0 ? cname : columnPrefix + Pascal(cname)) + (kind == "enum" ? "EnumId" : "");
                    field["column"] = column;
                    field["stored"] = true;
                    if (!columnNames.Add(column))
                    {
                        // The variants of a sealed line share a column of the same kind (every line has a due date); anything else
                        // is a clash the mapping must resolve.
                        var existing = columns.OfType<JsonObject>().FirstOrDefault(x => (string)x["name"]! == column);
                        if (agg["merge"]?.GetValue<bool>() != true || existing is null || (string)existing["kind"]! != kind)
                            problems.Add($"{agg["entity"]}: column {column} ({cpath}) is defined twice; name one in 'columns' or 'sameAs'");
                        continue;
                    }
                    var col = new JsonObject { ["name"] = column, ["type"] = HostType(kind, column, hostTypes), ["kind"] = kind, ["path"] = cpath };
                    if (kind == "enum")
                    {
                        var e = UseEnum(rf!);
                        field["enum"] = e["name"]!.GetValue<string>();
                        field["enumTypeId"] = e["typeId"]!.DeepClone();
                        field["enumFqn"] = rf;
                        col["enum"] = e["name"]!.GetValue<string>();
                    }
                    columns.Add(col);
                    break;
                }
                case "record":
                {
                    var prefix = prefixes[cpath]?.GetValue<string>() ?? (columnPrefix.Length == 0 ? cname : columnPrefix + Pascal(cname));
                    field["codec"] = Codec(rf!, cpath, prefix, agg, columns, lists, columnNames);
                    break;
                }
                case "list":
                    field["child"] = cpath;
                    field["elementType"] = JavaType(element!);
                    lists.Add((cpath, element!, cname));
                    break;
                default:
                    problems.Add($"{record}.{cname}: type {typeRef["fqn"]} cannot be stored (kind {kind})");
                    break;
            }
        }
        return name;
    }

    static string JavaType(JsonObject typeRef)
    {
        var simple = (string)typeRef["simple"]!;
        if (typeRef["args"] is JsonArray args && args.Count > 0)
            return $"{simple}<{string.Join(", ", args.OfType<JsonObject>().Select(JavaType))}>";
        return simple;
    }

    /// <summary>One aggregate: its root entity and child entities, added to the model's entity list.</summary>
    JsonObject Aggregate(JsonObject agg, JsonArray entities)
    {
        var record = Str(agg, "record");
        var entityName = Str(agg, "entity");
        var key = Str(agg, "key");
        currentAggregate = Str(agg, "id");
        var firstCodec = codecs.Count;
        var columns = new JsonArray();
        var lists = new List<(string Path, JsonObject Element, string Field)>();
        var codec = Codec(record, "", "", agg, columns, lists, new HashSet<string>(StringComparer.Ordinal));
        var root = Entity(entityName, agg, columns, [key]);
        root["aggregate"] = Str(agg, "id");
        root["root"] = true;
        root["codec"] = codec;
        entities.Add(root);
        var children = new JsonArray();
        var childMaps = (agg["children"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(c => Str(c, "path"), StringComparer.Ordinal);
        foreach (var (path, element, _) in lists)
        {
            if (!childMaps.TryGetValue(path, out var cm)) { problems.Add($"{record}: list {path} needs a child entity in 'children'"); continue; }
            var (kind, rf, _) = Classify(element);
            var childColumns = new JsonArray();
            var keyColumn = key;
            var names = new HashSet<string>(StringComparer.Ordinal) { keyColumn, "lineSeqId" };
            var keyType = columns.OfType<JsonObject>().First(c => (string)c["name"]! == key)["type"]!.GetValue<string>();
            var child = new JsonObject { ["path"] = path, ["entity"] = Str(cm, "entity"), ["kind"] = kind, ["elementType"] = JavaType(element), ["codec"] = null,
                ["sealed"] = null, ["discriminator"] = null, ["variants"] = new JsonArray(),
                ["accessors"] = new JsonArray(path.Split('.').Select(x => (JsonNode)x).ToArray()), ["method"] = string.Concat(path.Split('.').Select(Pascal)) };
            if (kind == "record")
                child["codec"] = Codec(rf!, "", "", cm, childColumns, [], names);
            else if (kind == "sealed")
            {
                var discriminator = Str(cm, "discriminator", "lineTypeId");
                var values = cm["values"] as JsonObject ?? [];
                names.Add(discriminator);
                var variants = new JsonArray();
                cm["merge"] = true;
                foreach (var v in types[rf!]["permits"]!.AsArray().Select(x => (string)x!))
                {
                    var vc = Codec(v, "", "", cm, childColumns, [], names);
                    variants.Add(new JsonObject { ["record"] = v, ["codec"] = vc, ["value"] = values[v]?.GetValue<string>() ?? UpperSnake(v) });
                }
                child["sealed"] = rf;
                child["discriminator"] = discriminator;
                child["variants"] = variants;
                childColumns.Insert(0, new JsonObject { ["name"] = discriminator, ["type"] = "id", ["kind"] = "string", ["path"] = "(variant)" });
            }
            else { problems.Add($"{record}: list {path} of {kind} cannot be stored"); continue; }
            childColumns.Insert(0, new JsonObject { ["name"] = "lineSeqId", ["type"] = "id", ["kind"] = "string", ["path"] = "(position)" });
            childColumns.Insert(0, new JsonObject { ["name"] = keyColumn, ["type"] = keyType, ["kind"] = "string", ["path"] = "(parent)" });
            var ce = Entity(Str(cm, "entity"), cm, childColumns, [keyColumn, "lineSeqId"]);
            ce["aggregate"] = Str(agg, "id");
            ce["parent"] = entityName;
            ce["parentKey"] = keyColumn;
            ((JsonArray)ce["relations"]!).Insert(0, Relation("one", keyColumn, entityName));

            entities.Add(ce);
            children.Add(child);
        }
        var imports = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var c in codecs.Skip(firstCodec))
        {
            imports.Add((string)c["fqn"]!);
            foreach (var f in c["fields"]!.AsArray().OfType<JsonObject>())
                if (f["enumFqn"] is { } ef) imports.Add((string)ef!);
        }
        foreach (var ch in children.OfType<JsonObject>())
            if (ch["sealed"] is { } sealedName) imports.Add((string)types[(string)sealedName!]["fqn"]!);
        return new JsonObject
        {
            ["id"] = Str(agg, "id"), ["record"] = record, ["fqn"] = types[record]["fqn"]!.DeepClone(), ["entity"] = entityName, ["key"] = key,
            ["codec"] = codec, ["children"] = children, ["imports"] = new JsonArray(imports.Select(i => (JsonNode)i).ToArray())
        };
    }

    /// <summary>
    /// A relation from a local field to "Entity" (the same field name there) or "Entity.field". Relations to the same entity get a
    /// title from the local field (ownerPartyId to Party.partyId: OwnerParty), as OFBiz needs relation names to be unique.
    /// </summary>
    static JsonObject Relation(string type, string field, string target)
    {
        var dot = target.IndexOf('.');
        var entity = dot < 0 ? target : target[..dot];
        var remote = dot < 0 ? field : target[(dot + 1)..];
        var r = new JsonObject { ["type"] = type, ["entity"] = entity, ["fields"] = new JsonArray(field), ["remote"] = remote, ["title"] = null };
        if (remote != field)
        {
            var title = Pascal(field);
            var suffix = Pascal(remote);
            title = title.EndsWith(suffix, StringComparison.Ordinal) && title.Length > suffix.Length ? title[..^suffix.Length]
                : title.EndsWith("Id", StringComparison.Ordinal) ? title[..^2] : title;
            r["title"] = title;
        }
        return r;
    }

    /// <summary>SQL reserved words (SQL:2016 and H2, PostgreSQL, Derby, MySQL, Oracle) a column must not be called.</summary>
    static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "ALL", "AND", "ANY", "ARRAY", "AS", "BETWEEN", "BOTH", "CASE", "CAST", "CHECK", "COLUMN", "CONSTRAINT", "CROSS", "DATE", "DAY", "DEFAULT",
        "DISTINCT", "ELSE", "END", "EXCEPT", "EXISTS", "FALSE", "FETCH", "FOR", "FOREIGN", "FROM", "FULL", "GROUP", "GROUPS", "HAVING", "HOUR", "IF",
        "IN", "INNER", "INTERSECT", "INTERVAL", "IS", "JOIN", "KEY", "LEADING", "LEFT", "LEVEL", "LIKE", "LIMIT", "MINUS", "MINUTE", "MONTH",
        "NATURAL", "NOT", "NULL", "NUMBER", "OFFSET", "ON", "OR", "ORDER", "OVER", "PARTITION", "PRIMARY", "RANGE", "RIGHT", "ROW", "ROWNUM",
        "ROWS", "SECOND", "SELECT", "SET", "SIZE", "SOME", "TABLE", "TO", "TOP", "TRAILING", "TRUE", "UNION", "UNIQUE", "UNKNOWN", "USER",
        "USING", "VALUE", "VALUES", "WHEN", "WHERE", "WINDOW", "WITH", "YEAR"
    };

    JsonObject Entity(string name, JsonObject spec, JsonArray columns, string[] pk)
    {
        var relations = new JsonArray();
        // An aggregate's references to host records carry no foreign key: the use case decides the order of its writes (a loan
        // may be stored before its account exists); a host-only entity's references do.
        var refType = spec["fields"] is null ? "one-nofk" : "one";
        foreach (var r in (spec["relations"] as JsonObject ?? [])) relations.Add(Relation(refType, r.Key, (string)r.Value!));
        foreach (var c in columns.OfType<JsonObject>())
            if (Reserved.Contains(UpperSnake((string)c["name"]!)))
                problems.Add($"{name}: column {c["name"]} is an SQL reserved word; rename it in 'columns'");
        foreach (var c in columns.OfType<JsonObject>().Where(c => c["enum"] is not null))
        {
            var e = enumsUsed[(string)c["enum"]!];
            var isStatus = (Map("enums")["status"] as JsonArray ?? []).Any(x => (string)x! == (string)e["name"]!);
            relations.Add(Relation("one", (string)c["name"]!, isStatus ? "StatusItem.statusId" : "Enumeration.enumId"));
        }
        return new JsonObject
        {
            ["aggregate"] = null, ["root"] = false, ["codec"] = null, ["parent"] = null, ["parentKey"] = null, ["hostOnly"] = false,
            ["name"] = name, ["title"] = Str(spec, "title", name), ["columns"] = columns, ["pk"] = new JsonArray(pk.Select(p => (JsonNode)p).ToArray()),
            ["relations"] = relations, ["unique"] = (spec["unique"] as JsonArray ?? []).DeepClone(), ["indexes"] = (spec["index"] as JsonArray ?? []).DeepClone()
        };
    }

    /// <summary>A host-only entity the SME declares field by field (a loan product).</summary>
    JsonObject HostEntity(JsonObject e)
    {
        var columns = new JsonArray();
        foreach (var f in (e["fields"] as JsonObject ?? []))
            columns.Add(new JsonObject { ["name"] = f.Key, ["type"] = (string)f.Value!, ["kind"] = "host", ["path"] = "(host)" });
        var entity = Entity(Str(e, "name"), e, columns, (e["pk"] as JsonArray ?? []).Select(x => (string)x!).ToArray());
        entity["hostOnly"] = true;
        return entity;
    }

    /// <summary>A host service: the API call behind it and its command flattened into input attributes.</summary>
    JsonObject Service(JsonObject s)
    {
        var call = Str(s, "calls");
        var dot = call.IndexOf('.');
        var iface = call[..dot];
        var method = call[(dot + 1)..];
        var m = types[iface]["methods"]!.AsArray().OfType<JsonObject>().FirstOrDefault(x => (string)x["name"]! == method)
                ?? throw new InvalidOperationException($"{call}: no such method in the API");
        var param = m["params"]!.AsArray().OfType<JsonObject>().Single();
        var command = (string)param["type"]!["simple"]!;
        var inputs = new JsonArray();
        var prefixes = s["prefixes"] as JsonObject ?? [];
        var derived = s["derived"] as JsonObject ?? [];
        var optional = (s["optional"] as JsonArray ?? []).Select(x => (string)x!).ToHashSet(StringComparer.Ordinal);
        var argument = Argument(command, "", "", prefixes, derived, optional, inputs, s["attributes"] as JsonObject ?? []);
        foreach (var extra in (s["inputs"] as JsonObject ?? []))
            inputs.Add(new JsonObject { ["name"] = extra.Key, ["type"] = (string)extra.Value!, ["optional"] = true, ["extra"] = true });
        var returns = (string)m["returns"]!["simple"]!;
        var imports = new SortedSet<string>(StringComparer.Ordinal) { (string)types[iface]["fqn"]! };
        Imports(argument, imports);
        imports.Add((string)types[returns]["fqn"]!);
        return new JsonObject
        {
            ["imports"] = new JsonArray(imports.Select(i => (JsonNode)i).ToArray()),
            ["name"] = Str(s, "name"), ["description"] = Str(s, "description"), ["interface"] = iface, ["method"] = method,
            ["via"] = Str(s, "via"), ["command"] = command, ["argument"] = argument, ["returns"] = returns, ["inputs"] = inputs,
            ["action"] = Str(s, "action", "VIEW"), ["rest"] = s["rest"]?.GetValue<bool>() ?? false, ["outcome"] = Outcome(returns),
            ["ignoreOutcomes"] = (s["ignoreOutcomes"] as JsonArray ?? []).DeepClone(), ["errorOutcomes"] = (s["errorOutcomes"] as JsonArray ?? []).DeepClone()
        };
    }

    /// <summary>
    /// The Java expression that builds a command from the service context, adding the input attributes it reads. A nested record is
    /// built in place; a component the mapping derives (a Java expression over the context) is not an input.
    /// </summary>
    JsonObject Argument(string record, string path, string prefix, JsonObject prefixes, JsonObject derived, HashSet<string> optional, JsonArray inputs,
        JsonObject renames)
    {
        var parts = new JsonArray();
        foreach (var c in types[record]["components"]!.AsArray().OfType<JsonObject>())
        {
            var cname = (string)c["name"]!;
            var cpath = path.Length == 0 ? cname : $"{path}.{cname}";
            var typeRef = (JsonObject)c["type"]!;
            var (kind, rf, _) = Classify(typeRef);
            var part = new JsonObject
            {
                ["name"] = cname, ["path"] = cpath, ["kind"] = kind, ["javaType"] = JavaType(typeRef), ["nullable"] = Nullable(typeRef),
                ["derived"] = null, ["record"] = null, ["attribute"] = null, ["enum"] = null, ["enumTypeId"] = null
            };
            parts.Add(part);
            if (derived[cpath] is { } expr) { part["derived"] = (string)expr!; continue; }
            if (kind == "record")
            {
                part["record"] = Argument(rf!, cpath, prefixes[cpath]?.GetValue<string>() ?? (prefix.Length == 0 ? cname : prefix + Pascal(cname)),
                    prefixes, derived, optional, inputs, renames);
                continue;
            }
            var attr = renames[cpath]?.GetValue<string>() ?? (prefix.Length == 0 ? cname : prefix + Pascal(cname)) + (kind == "enum" ? "EnumId" : "");
            part["attribute"] = attr;
            if (kind == "enum") { part["enum"] = UseEnum(rf!)["name"]!.GetValue<string>(); part["enumTypeId"] = UseEnum(rf!)["typeId"]!.DeepClone(); }
            var type = kind switch
            {
                "string" or "enum" => "String", "decimal" => "BigDecimal", "int" => "Integer", "long" => "Long", "boolean" => "Boolean",
                "date" => "java.sql.Date", _ => "Object"
            };
            if (kind is "list" or "sealed" or "unsupported") problems.Add($"{record}.{cname}: a {kind} cannot be a service input; derive it");
            inputs.Add(new JsonObject { ["name"] = attr, ["type"] = type, ["optional"] = Nullable(typeRef) || optional.Contains(cpath), ["path"] = cpath });
        }
        return new JsonObject { ["record"] = record, ["fqn"] = types[record]["fqn"]!.DeepClone(), ["parts"] = parts };
    }

    void Imports(JsonObject argument, SortedSet<string> imports)
    {
        imports.Add((string)argument["fqn"]!);
        foreach (var p in argument["parts"]!.AsArray().OfType<JsonObject>())
        {
            if (p["record"] is JsonObject r) Imports(r, imports);
            if (p["enum"] is { } e) imports.Add((string)enumsUsed[(string)e!]["fqn"]!);
        }
    }

    /// <summary>A call's result: a sealed outcome (its records) or one record.</summary>
    JsonObject Outcome(string type)
    {
        var t = types[type];
        var variants = (string)t["kind"]! == "sealed" ? t["permits"]!.AsArray().Select(x => (string)x!).ToList() : [type];
        return new JsonObject { ["type"] = type, ["sealed"] = (string)t["kind"]! == "sealed", ["variants"] = new JsonArray(variants.Select(v => (JsonNode)v).ToArray()) };
    }

    /// <summary>
    /// The role of each method of a port the templates implement over an aggregate: byKey (the key in, Optional of the root out),
    /// save (the root in, nothing out), byChild (a finder the mapping names: the roots whose child row has the field), byField (the
    /// roots whose column has the value). A method no rule explains is a problem: the mapping must name it.
    /// </summary>
    void Roles(JsonArray methods, string aggId, JsonObject spec)
    {
        var agg = (mapping["aggregates"] as JsonArray ?? []).OfType<JsonObject>().First(a => Str(a, "id") == aggId);
        var root = Str(agg, "record");
        var finders = spec["finders"] as JsonObject ?? [];
        foreach (var m in methods.OfType<JsonObject>())
        {
            var name = (string)m["name"]!;
            var ps = m["params"]!.AsArray().OfType<JsonObject>().ToList();
            m["role"] = null; m["finder"] = null; m["paramKind"] = null; m["paramEnum"] = null; m["many"] = false;
            var ret = (JsonObject)m["returns"]!;
            if (finders[name] is JsonObject f)
            {
                m["role"] = f["child"] is not null ? "byChild" : "byField";
                m["finder"] = f.DeepClone();
                var p = ps.Single();
                var (kind, rf, _) = Classify((JsonObject)p["type"]!);
                m["paramKind"] = kind;
                if (kind == "enum") m["paramEnum"] = UseEnum(rf!)["typeId"]!.DeepClone();
                m["many"] = (string)ret["fqn"]! == "java.util.List";
            }
            else if (ps.Count == 1 && (string)ret["fqn"]! == "java.util.Optional" && (string)ps[0]["name"]! == Str(agg, "key")) m["role"] = "byKey";
            else if (ps.Count == 1 && (string)ret["fqn"]! == "void" && (string)ps[0]["type"]!["simple"]! == root) m["role"] = "save";
            else problems.Add($"{spec["class"] ?? "port"}.{name}: no role; name it in the mapping's finders");
        }
    }

    /// <summary>A port's mapping with every key the templates read present (null when the mapping does not set it).</summary>
    static JsonObject PortSpec(JsonObject ps)
    {
        var spec = ps.DeepClone().AsObject();
        foreach (var k in new[] { "aggregate", "derive", "prefix", "finders", "rules", "class" })
            if (!spec.ContainsKey(k)) spec[k] = null;
        return spec;
    }

    SortedSet<string> PortImports(JsonArray methods, string iface)
    {
        var imports = new SortedSet<string>(StringComparer.Ordinal) { iface };
        void Add(JsonNode? t)
        {
            if (t is not JsonObject o) return;
            var fqn = (string)o["fqn"]!;
            if (!fqn.Contains('.') || fqn.StartsWith("java.lang.", StringComparison.Ordinal)) { }
            else imports.Add(fqn);
            foreach (var a in o["args"] as JsonArray ?? []) Add(a);
        }
        foreach (var m in methods.OfType<JsonObject>())
        {
            Add(m["returns"]);
            foreach (var p in m["params"]!.AsArray()) Add(p!["type"]);
        }
        return imports;
    }

    /// <summary>The ports record's components, each generated or a hole, with the port interface's methods.</summary>
    JsonArray Ports()
    {
        var spec = Map("ports");
        var record = Str(Map("api"), "ports");
        var result = new JsonArray();
        foreach (var c in types[record]["components"]!.AsArray().OfType<JsonObject>())
        {
            var iface = (string)c["type"]!["simple"]!;
            var ps = spec[iface] as JsonObject ?? [];
            var impl = Str(ps, "implementation", "hole");
            var methods = types[iface]["methods"]!.DeepClone().AsArray();
            foreach (var m in methods.OfType<JsonObject>())
            {
                m["role"] = null; m["finder"] = null; m["paramKind"] = null; m["paramEnum"] = null; m["many"] = false;
            }
            if (ps["aggregate"] is { } aggId) Roles(methods, (string)aggId!, ps);
            result.Add(new JsonObject
            {
                ["component"] = (string)c["name"]!, ["interface"] = iface, ["fqn"] = types[iface]["fqn"]!.DeepClone(), ["implementation"] = impl,
                ["class"] = Str(ps, "class", "Ofbiz" + iface), ["rules"] = (ps["rules"] as JsonArray ?? []).DeepClone(),
                ["methods"] = methods, ["spec"] = PortSpec(ps), ["aggregate"] = ps["aggregate"]?.DeepClone(),
                ["imports"] = new JsonArray(PortImports(methods, (string)types[iface]["fqn"]!).Select(i => (JsonNode)i).ToArray())
            });
        }
        return result;
    }
}
