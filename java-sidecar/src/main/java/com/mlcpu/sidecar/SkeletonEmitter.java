package com.mlcpu.sidecar;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.regex.Pattern;

/**
 * Renders IR (semantic tree) to one Java class per COBOL program. Storage is byte-backed ({@code com.mlcpu.rt.Area}
 * + {@code CobField} views); statements lower to {@code CobRt} calls. Any construct that cannot be lowered faithfully
 * becomes a frontier marker plus a runtime stop ({@code CobRt.frontier}) — never a guess (I1).
 */
public final class SkeletonEmitter {

    public static final class Emission {
        public String className;
        public String source;
        public int paragraphCount;
        public int fieldCount;
        public int frontierCount;
        public int loweredStatements;
        public int abstainedStatements;
        public int lirLoweredStatements;
        public int prunedFieldCount;
        public int linkageTypedCalls;
        public int linkageWholeStateCalls;
        public List<String> scopeFrontiers = new ArrayList<>();
        public List<String> abstained = new ArrayList<>();
        /** Typed rule sources (JDK-only), one map per file: path, className, sourceSet, role, ruleIds, content. Empty unless extracting. */
        public List<Map<String, Object>> additionalSources = List.of();
        /** mlcpu.rule-contract.v1 descriptors of the rules the program uses. */
        public List<Map<String, Object>> ruleContracts = List.of();
        /** mlcpu.rule-extraction-ledger.v1 (APPLIED/SKIPPED sites), or null when rules were not extracted. */
        public Map<String, Object> extractionLedger;
    }

    static final class Abstain extends RuntimeException {
        Abstain(String m) { super(m); }
    }

    private static final Set<String> JAVA_KEYWORDS = Set.of(
            "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue", "default", "do",
            "double", "else", "enum", "extends", "final", "finally", "float", "for", "goto", "if", "implements", "import", "instanceof",
            "int", "interface", "long", "native", "new", "package", "private", "protected", "public", "return", "short", "static",
            "strictfp", "super", "switch", "synchronized", "this", "throw", "throws", "transient", "try", "void", "volatile", "while",
            "true", "false", "null", "var", "record", "yield", "sealed", "permits", "_init", "_dispatch", "_count", "_linkage", "_fields",
            "perform", "call", "run", "programId", "exitProgram", "ensureInit", "resumeFrom");

    private static final Pattern NUMERIC_LIT = Pattern.compile("[+-]?(\\d+(\\.\\d*)?|\\.\\d+)");
    private static final Set<String> NUMERIC_FNS = Set.of("MOD", "REM", "INTEGER", "INTEGER-PART", "ABS", "MAX", "MIN", "NUMVAL",
            "NUMVAL-C", "LENGTH", "INTEGER-OF-DATE", "DATE-OF-INTEGER", "TEST-NUMVAL", "TEST-NUMVAL-C", "ORD", "ORD-MAX", "ORD-MIN",
            "SUM", "MEAN", "MEDIAN", "RANGE", "SQRT", "FACTORIAL", "INTEGER-OF-DAY", "DAY-OF-INTEGER", "TEST-DATE-YYYYMMDD",
            "YEAR-TO-YYYY", "DATE-TO-YYYYMMDD");

    // per-emission state
    private Ir ir;
    private boolean composeStorage;
    private final Map<Integer, Ir.Field> byId = new HashMap<>();
    private final Map<Integer, List<Ir.Field>> children = new HashMap<>();
    private final Map<Integer, String> jname = new HashMap<>();
    private final Map<Integer, String> areaOf = new LinkedHashMap<>();
    private final Map<String, Integer> areaSize = new LinkedHashMap<>();
    private final Set<String> linkageAreas = new HashSet<>();
    /** LINKAGE areas named in PROCEDURE DIVISION USING: no storage of their own until a CALL binds them (Area.linkage). */
    private final Set<String> usingAreas = new HashSet<>();
    /** EXTERNAL record areas: area -> the record's name in the run unit's registry. */
    private final Map<String, String> externalAreas = new LinkedHashMap<>();
    private final Map<String, String> fileVar = new LinkedHashMap<>();
    private final Map<String, Ir.File> files = new LinkedHashMap<>();
    private final Map<String, Integer> paraIndex = new HashMap<>();
    private final List<Ir.SemParagraph> paras = new ArrayList<>();
    private final Map<String, String> numConsts = new LinkedHashMap<>();
    private final StringBuilder statics = new StringBuilder();
    private int tmp;
    private int lowered;
    private int abstainedCount;
    private final List<String> abstainedList = new ArrayList<>();
    private String sentenceLabel;
    private final List<String> loopLabels = new ArrayList<>();
    private int currentPara;
    /** Groups whose length follows an OCCURS DEPENDING ON table at their end: group id -> that table. */
    private final Map<Integer, Ir.Field> odoTable = new HashMap<>();
    /** Items the emitter abstains on because of OCCURS DEPENDING ON (complex ODO, items whose position slides): id -> reason. */
    private final Map<Integer, String> odoAbstain = new HashMap<>();
    /** ALTERed paragraphs: paragraph index -> the instance field holding its GO TO's current target (-1: none yet). */
    private final Map<Integer, String> alterVar = new LinkedHashMap<>();
    /** USE AFTER ERROR: file -> its declarative section's paragraph index; open mode -> the same. */
    private final Map<String, Integer> useFor = new HashMap<>();
    private final Map<String, Integer> useForMode = new LinkedHashMap<>();
    /** Sort files used by SORT / MERGE / RELEASE / RETURN: each gets an instance field holding its running SORT. */
    private final Set<String> sortFiles = new java.util.TreeSet<>();
    /** Why the program's ALTERs are not lowered (an altered paragraph that is not a lone GO TO sentence), or null. */
    private String alterProblem;
    /** Typed rule slices of the program (extracting only), else null. */
    private RuleSet ruleSet;

    public Emission emit(Ir in) {
        return emit(in, false);
    }

    public Emission emit(Ir in, boolean composedStorage) {
        return emit(in, composedStorage, false);
    }

    /**
     * With {@code extractRules} (composed lane only) conditions and arithmetic the {@link RulePlanner} proves equivalent
     * run behind generated typed contracts; every other site keeps its faithful lowering and is recorded as SKIPPED.
     */
    public Emission emit(Ir in, boolean composedStorage, boolean extractRules) {
        if (extractRules && !composedStorage) throw new IllegalArgumentException("extractRules requires composeStorage");
        reset();
        composeStorage = composedStorage;
        ir = in;
        if (extractRules) ruleSet = new RuleSet(ir.program.id);
        if (!Ir.supports(ir.irVersion)) throw new IllegalArgumentException("unsupported IR version " + ir.irVersion);
        if (ir.semantics == null || ir.semantics.paragraphs == null)
            throw new IllegalArgumentException("IR has no semantic tree (schema mlcpu.semantic-tree.v1 required)");
        validateStepIds();
        for (Ir.Field f : ir.dictionary) {
            byId.put(f.id, f);
            if (f.parentId != null) children.computeIfAbsent(f.parentId, k -> new ArrayList<>()).add(f);
        }
        planNames();
        planAreas();
        planOdo();
        paras.addAll(ir.semantics.paragraphs);
        for (int i = 0; i < paras.size(); i++) paraIndex.putIfAbsent(paras.get(i).name.toUpperCase(), i);
        planAlter();
        for (int i = 0; i < paras.size(); i++) {
            Ir.SemParagraph sp = paras.get(i);
            if (sp.useFiles != null) for (String f : sp.useFiles) useFor.putIfAbsent(f.toUpperCase(), i);
            if (sp.useModes != null) for (String m : sp.useModes) useForMode.putIfAbsent(m.toUpperCase(), i);
        }

        String cls = "Prog_" + sanitize(ir.program.id);
        StringBuilder body = new StringBuilder();
        // paragraphs first: they populate the constant pools
        StringBuilder methods = new StringBuilder();
        for (int i = 0; i < paras.size(); i++) emitParagraph(methods, i);

        body.append("package com.mlcpu.gen;\n\n");
        body.append("import com.mlcpu.rt.*;\nimport java.math.BigDecimal;\n\n");
        body.append("/** Generated from ").append(ir.program.path).append(" by MlCpu SkeletonEmitter (IR ").append(ir.irVersion).append("). */\n");
        body.append("@CobolProgram(programId = \"").append(ir.program.id).append("\", source = \"").append(esc(ir.program.path)).append("\")\n");
        body.append("public final class ").append(cls).append(" extends CobolBase {\n\n");
        if (composeStorage) {
            body.append("    // Storage composition: execution and files remain on the program adapter.\n");
            body.append("    private final Storage _storage = new Storage();\n\n");
            body.append("    private static final class Storage {\n");
            StringBuilder storage = new StringBuilder();
            emitDeclarations(storage);
            body.append(storage.toString().indent(4));
            body.append("    }\n\n");
        } else emitDeclarations(body);
        boolean rules = ruleSet != null && !ruleSet.usedRules().isEmpty();
        if (rules) {
            String binding = ruleSet.bindingFqn();
            body.append("    // Typed rule seam: extracted predicates/calculations run behind generated JDK-only contracts.\n");
            body.append("    private final ").append(binding).append(" _rules;\n\n");
            body.append("    public ").append(cls).append("() {\n        this(").append(binding).append(".selected());\n    }\n\n");
            body.append("    public ").append(cls).append("(").append(binding).append(" rules) {\n")
                    .append("        this._rules = java.util.Objects.requireNonNull(rules, \"rules\");\n    }\n\n");
        }
        if (!alterVar.isEmpty()) {
            body.append("    // ---- ALTERed GO TO targets (paragraph indexes; -1 until an ALTER when the GO TO names none)\n");
            for (Map.Entry<Integer, String> e : alterVar.entrySet()) {
                Ir.Stmt go = paras.get(e.getKey()).statements.get(0);
                List<Ir.Node> tg = go.l("targets");
                body.append("    private int ").append(e.getValue()).append(" = ").append(tg.isEmpty() ? -1 : startIndex(tg.get(0).v)).append(";\n");
            }
            body.append("\n");
        }
        for (String sd : sortFiles) body.append("    private CobSort _sort_").append(sanitize(sd)).append("; // the running SORT / MERGE of ").append(sd).append("\n");
        if (!sortFiles.isEmpty()) body.append("\n");
        emitFiles(body);
        emitInit(body);
        body.append(methods);
        emitDriver(body);
        for (Map.Entry<String, String> e : numConsts.entrySet())
            body.append("    private static final NumLit ").append(e.getValue()).append(" = new NumLit(\"").append(e.getKey()).append("\");\n");
        body.append(statics);
        body.append("}\n");

        Emission em = new Emission();
        em.className = "com.mlcpu.gen." + cls;
        em.source = body.toString();
        em.paragraphCount = paras.size();
        em.fieldCount = (int) ir.dictionary.stream().filter(f -> f.lvl() != 88 && !"FILLER".equals(f.field)).count();
        em.frontierCount = ir.frontiers == null ? 0 : ir.frontiers.size();
        em.loweredStatements = lowered;
        em.abstainedStatements = abstainedCount;
        em.abstained = abstainedList;
        if (ruleSet != null) {
            RuleSources.Output out = RuleSources.render(ruleSet, byId::get, em.className);
            em.additionalSources = out.additionalSources();
            em.ruleContracts = out.ruleContracts();
            em.extractionLedger = out.ledger();
        }
        return em;
    }

    private void reset() {
        byId.clear(); children.clear(); jname.clear(); areaOf.clear(); areaSize.clear(); linkageAreas.clear(); usingAreas.clear(); fileVar.clear();
        files.clear(); externalAreas.clear(); paraIndex.clear(); paras.clear(); numConsts.clear(); statics.setLength(0); tmp = 0; lowered = 0;
        abstainedCount = 0; abstainedList.clear(); sentenceLabel = null; loopLabels.clear();
        odoTable.clear(); odoAbstain.clear(); c88Consts.clear(); alterVar.clear(); alterProblem = null; useFor.clear(); useForMode.clear(); sortFiles.clear();
        osvsTrunc.clear();
        ruleSet = null;
    }

    private void validateStepIds() {
        Set<String> seen = new HashSet<>();
        for (Ir.SemParagraph p : ir.semantics.paragraphs) walkIds(p.statements, seen);
    }

    private void walkIds(List<Ir.Stmt> list, Set<String> seen) {
        if (list == null) return;
        for (Ir.Stmt s : list) {
            if (!seen.add(s.stepId)) throw new IllegalArgumentException("duplicate statement id " + s.stepId);
            if (s.blocks != null) for (List<Ir.Stmt> b : s.blocks.values()) walkIds(b, seen);
        }
    }

    // ------------------------------------------------------------------ naming

    static String sanitize(String s) {
        StringBuilder sb = new StringBuilder();
        for (char c : s.toCharArray()) sb.append(Character.isLetterOrDigit(c) ? Character.toUpperCase(c) : '_');
        return sb.toString();
    }

    private void planNames() {
        Map<String, String> modern = new HashMap<>();
        if (ir.naming != null) for (Ir.Name n : ir.naming) modern.put(n.original, n.modern);
        Map<String, Integer> used = new HashMap<>();
        // The emitter's own members: storage areas (a<id>, aF_<file>) and files (file<Name>). A data item named A2 or
        // FILE-IN must not take their names (it did: "variable a2 is already defined").
        Set<String> reserved = new HashSet<>();
        if (composeStorage) reserved.add("_storage");
        if (ruleSet != null) reserved.add("_rules");
        for (Ir.Field f : ir.dictionary) reserved.add("a" + f.id);
        if (ir.program.files != null) for (Ir.File fd : ir.program.files) reserved.add("file" + camelUpper(fd.name));
        for (Ir.Field f : ir.dictionary) {
            if (f.lvl() == 88) continue;
            String base = "FILLER".equals(f.field) ? "filler" + f.id : modern.getOrDefault(f.field, camel(f.field));
            if (base.isEmpty() || !Character.isJavaIdentifierStart(base.charAt(0))) base = "f" + base;
            if (JAVA_KEYWORDS.contains(base) || reserved.contains(base)) base = base + "_";
            int n = used.merge(base, 1, Integer::sum);
            jname.put(f.id, n == 1 ? base : base + "_" + n);
        }
    }

    static String camel(String cobol) {
        StringBuilder sb = new StringBuilder();
        boolean up = false;
        for (char c : cobol.toLowerCase().toCharArray()) {
            if (c == '-' || c == '_') { up = true; continue; }
            sb.append(up ? Character.toUpperCase(c) : c);
            up = false;
        }
        return sb.toString();
    }

    // ------------------------------------------------------------------ storage

    private void planAreas() {
        Map<String, Ir.File> fileByName = new HashMap<>();
        if (ir.program.files != null) for (Ir.File f : ir.program.files) { fileByName.put(f.name, f); files.put(f.name, f); }
        for (Ir.Field f : ir.dictionary) {
            if (f.parentId != null || f.lvl() == 88) continue;
            String area;
            if (f.file != null) {
                area = "aF_" + sanitize(f.file);
                Ir.File fd = fileByName.get(f.file);
                int max = fd != null ? Math.max(fd.maxRecord, f.length) : f.length;
                areaSize.merge(area, max, Math::max);
            } else if (f.redefinesId != null && areaOf.containsKey(f.redefinesId)) {
                area = areaOf.get(f.redefinesId);
                areaSize.merge(area, f.length, Math::max);
            } else {
                area = "a" + f.id;
                int occurs = f.occurs == null || f.occurs.isEmpty() ? 1 : Integer.parseInt(f.occurs);
                areaSize.merge(area, f.length * occurs, Math::max);
                if ("Linkage".equals(f.section)) linkageAreas.add(area);
                if (f.external) externalAreas.put(area, f.field);
            }
            areaOf.put(f.id, area);
        }
        for (Ir.Field f : ir.dictionary) if (!areaOf.containsKey(f.id)) areaOf.put(f.id, areaOf.get(f.recordId));
        for (String a : usingLinkageAreas()) if (linkageAreas.contains(a) && !externalAreas.containsKey(a)) usingAreas.add(a);
        for (Ir.File fd : files.values()) {
            String area = "aF_" + sanitize(fd.name);
            areaSize.putIfAbsent(area, Math.max(fd.maxRecord, 1));
            fileVar.put(fd.name, "file" + camelUpper(fd.name));
        }
    }

    /**
     * OCCURS DEPENDING ON (GnuCOBOL -std=ibm). A group that holds an ODO table is as long as its fixed part plus the
     * current number of occurrences, computed when the group is referenced (MOVE, compare, DISPLAY, WRITE, LENGTH OF);
     * subscripts are not checked against the count. Lowered: one ODO table per group, not inside another table, its
     * object outside any table. Abstained: complex ODO (nested or several tables) and items after the table, whose
     * position slides with the count (odoslide).
     */
    private void planOdo() {
        for (Ir.Field t : ir.dictionary) {
            if (t.dependingOnId == null || t.occurs == null || t.occurs.isEmpty()) continue;
            Ir.Field obj = byId.get(t.dependingOnId);
            String complex = null;
            if (t.dims != null && t.dims.size() > 1) complex = "OCCURS DEPENDING ON table " + t.field + " inside another table (complex ODO) is not lowered";
            else if (obj == null) complex = "OCCURS DEPENDING ON object of " + t.field + " is unresolved";
            else if (obj.dims != null && !obj.dims.isEmpty()) complex = "OCCURS DEPENDING ON object " + obj.field + " is a table element";
            for (Ir.Field d : descendants(t)) if (d != t && d.dependingOnId != null) complex = "nested OCCURS DEPENDING ON in " + t.field + " (complex ODO) is not lowered";
            if (complex != null) {
                for (Ir.Field f : ir.dictionary) if (f.recordId == t.recordId) odoAbstain.putIfAbsent(f.id, complex);
                continue;
            }
            for (Ir.Field a = parentOf(t); a != null; a = parentOf(a)) {
                if (odoTable.containsKey(a.id) && odoTable.get(a.id) != t) {
                    odoTable.remove(a.id);
                    odoAbstain.put(a.id, "group " + a.field + " holds several OCCURS DEPENDING ON tables (complex ODO)");
                } else if (!odoAbstain.containsKey(a.id)) odoTable.put(a.id, t);
            }
            int end = t.offset + t.length * Integer.parseInt(t.occurs);
            for (Ir.Field f : ir.dictionary)
                if (f.recordId == t.recordId && f.lvl() != 88 && f.offset >= end && !odoTable.containsKey(f.id) && f.id != t.id)
                    odoAbstain.putIfAbsent(f.id, f.field + " follows the OCCURS DEPENDING ON table " + t.field + " (its position slides with the count)");
        }
    }

    private Ir.Field parentOf(Ir.Field f) {
        return f.parentId == null ? null : byId.get(f.parentId);
    }

    private List<Ir.Field> descendants(Ir.Field f) {
        List<Ir.Field> out = new ArrayList<>();
        out.add(f);
        for (Ir.Field c : children.getOrDefault(f.id, List.of())) out.addAll(descendants(c));
        return out;
    }

    /** The current-length view of a group that holds the ODO table {@code t}. */
    private String odoView(String groupExpr, Ir.Field t) {
        return "CobRt.odo(" + groupExpr + ", " + field(refTo(t.dependingOnId)) + ", " + t.length + ", " + t.occurs + ")";
    }

    private Ir.Node refTo(int id) {
        Ir.Node n = new Ir.Node();
        n.k = "ref";
        n.id = id;
        n.v = byId.get(id).field;
        return n;
    }

    static String camelUpper(String s) {
        String c = camel(s);
        return c.isEmpty() ? c : Character.toUpperCase(c.charAt(0)) + c.substring(1);
    }

    private boolean isReturnCode(Ir.Field f) {
        return f.parentId == null && f.line == 0 && "RETURN-CODE".equals(f.field);
    }

    private boolean isSortReturn(Ir.Field f) {
        return f.parentId == null && f.line == 0 && "SORT-RETURN".equals(f.field);
    }

    private boolean isTally(Ir.Field f) {
        return f.parentId == null && f.line == 0 && "TALLY".equals(f.field);
    }

    private void emitDeclarations(StringBuilder b) {
        b.append("    // ---- storage areas\n");
        for (Map.Entry<String, Integer> e : areaSize.entrySet()) {
            if (externalAreas.containsKey(e.getKey())) {
                b.append("    final Area ").append(e.getKey()).append(" = CobRt.externalArea(").append(jstr(externalAreas.get(e.getKey()))).append(", ")
                        .append(e.getValue()).append("); // EXTERNAL\n");
                continue;
            }
            if (usingAreas.contains(e.getKey()))
                b.append("    final Area ").append(e.getKey()).append(" = Area.linkage(").append(e.getValue()).append("); // LINKAGE (USING)\n");
            else
                b.append("    final Area ").append(e.getKey()).append(" = new Area(").append(e.getValue()).append(");").append(linkageAreas.contains(e.getKey()) ? " // LINKAGE" : "").append("\n");
        }
        b.append("\n    // ---- data items\n");
        List<Ir.Field> items = new ArrayList<>();
        for (Ir.Field f : ir.dictionary) if (f.lvl() != 88) items.add(f);
        if (items.size() <= ITEMS_PER_METHOD) {
            for (Ir.Field f : items)
                b.append("    final CobField ").append(jname.get(f.id)).append(" = ").append(fieldCtor(f)).append(";").append(itemComment(f)).append("\n");
            b.append("\n");
            return;
        }
        // A large data division: the field views are built by methods of at most ITEMS_PER_METHOD items each, called from an
        // instance initializer in declaration order (one constructor holding them all would pass the JVM's 64 KB per method).
        for (Ir.Field f : items) b.append("    CobField ").append(jname.get(f.id)).append(";").append(itemComment(f)).append("\n");
        int chunks = (items.size() + ITEMS_PER_METHOD - 1) / ITEMS_PER_METHOD;
        b.append("\n    {\n");
        for (int c = 0; c < chunks; c++) b.append("        _items").append(c).append("();\n");
        b.append("    }\n\n");
        for (int c = 0; c < chunks; c++) {
            b.append("    private void _items").append(c).append("() {\n");
            for (Ir.Field f : items.subList(c * ITEMS_PER_METHOD, Math.min(items.size(), (c + 1) * ITEMS_PER_METHOD)))
                b.append("        ").append(jname.get(f.id)).append(" = ").append(fieldCtor(f)).append(";\n");
            b.append("    }\n\n");
        }
    }

    /**
     * Statements per generated helper where one method could pass the JVM's limit of 64 KB of bytecode: data items (about 80
     * bytes each at most), VALUE statements, ProgramState entries, paragraph names and dispatch cases. A program within
     * these limits is emitted exactly as before.
     */
    static final int ITEMS_PER_METHOD = 600;
    static final int STATEMENTS_PER_METHOD = 1000;

    private static String itemComment(Ir.Field f) {
        return " // " + f.level + " " + f.field + (f.pic != null && !f.pic.isEmpty() ? " PIC " + f.pic : "") + (!"DISPLAY".equals(f.usage) ? " " + f.usage : "");
    }

    /** Areas of the LINKAGE records in PROCEDURE DIVISION USING order. */
    private List<String> usingLinkageAreas() {
        List<String> using = ir.program.using == null ? List.of() : ir.program.using;
        List<String> lk = new ArrayList<>();
        for (String u : using)
            for (Ir.Field f : ir.dictionary)
                if (f.parentId == null && f.field.equals(u) && "Linkage".equals(f.section)) { lk.add(areaOf.get(f.id)); break; }
        return lk;
    }

    private String fieldCtor(Ir.Field f) {
        if (isReturnCode(f)) return "CobRt.RETURN_CODE";
        if (isSortReturn(f)) return "CobRt.SORT_RETURN";
        if (isTally(f)) return "CobRt.TALLY";
        String area = areaOf.get(f.id);
        String dims = dims(f);
        String nm = "\"" + esc(f.field) + "\"";
        return switch (f.category) {
            case "Group" -> "CobField.group(" + nm + ", " + area + ", " + f.offset + ", " + f.length + ", " + dims + ")";
            case "Alphanumeric", "Alphabetic" -> "CobField.alnum(" + nm + ", " + area + ", " + f.offset + ", " + f.length + ", " + f.justified + ", " + dims + ")";
            case "NumericEdited" -> "CobField.edited(" + nm + ", CobField.Cat.NUM_EDITED, " + area + ", " + f.offset + ", " + f.length + ", " + f.digits + ", "
                    + f.impliedDecimals + ", " + f.blankWhenZero + ", Edit.expand(\"" + esc(f.pic) + "\"), " + dims + ")";
            case "AlphanumericEdited" -> "CobField.edited(" + nm + ", CobField.Cat.ALNUM_EDITED, " + area + ", " + f.offset + ", " + f.length + ", 0, 0, false, Edit.expand(\""
                    + esc(f.pic) + "\"), " + dims + ")";
            case "Index" -> "new CobField(" + nm + ", CobField.Cat.INDEX, CobField.Use.INDEX, " + area + ", " + f.offset + ", 4, 9, 0, true, false, false, false, false, null, " + dims + ")";
            case "Pointer" -> "new CobField(" + nm + ", CobField.Cat.POINTER, CobField.Use.POINTER, " + area + ", " + f.offset + ", " + f.length + ", 9, 0, false, false, false, false, false, null, " + dims + ")";
            default -> {
                String use = switch (f.usage) {
                    case "COMP-3" -> "PACKED";
                    case "COMP" -> "BINARY";
                    case "COMP-5" -> "COMP5";
                    case "COMP-1" -> "FLOAT";
                    case "COMP-2" -> "DOUBLE";
                    default -> "DISPLAY";
                };
                int scale = f.impliedDecimals;
                yield "CobField.num(" + nm + ", CobField.Use." + use + ", " + area + ", " + f.offset + ", " + f.length + ", " + f.digits + ", " + scale + ", "
                        + f.signed + ", " + f.signSeparate + ", " + f.signLeading + ", " + dims + ")";
            }
        };
    }

    private String fieldRef(int id) {
        return (composeStorage ? "_storage." : "") + jname.get(id);
    }

    private String areaRef(String area) {
        return (composeStorage ? "_storage." : "") + area;
    }

    private String dims(Ir.Field f) {
        if (f.dims == null || f.dims.isEmpty()) return "null, null";
        StringBuilder c = new StringBuilder("new int[]{"), s = new StringBuilder("new int[]{");
        for (int i = 0; i < f.dims.size(); i++) {
            if (i > 0) { c.append(", "); s.append(", "); }
            c.append(f.dims.get(i));
            s.append(f.strides.get(i));
        }
        return c.append("}") + ", " + s.append("}");
    }

    private void emitFiles(StringBuilder b) {
        if (files.isEmpty()) return;
        b.append("    // ---- files\n");
        for (Ir.File fd : files.values()) {
            String org = switch (fd.organization) {
                case "INDEXED" -> "INDEXED";
                case "LINE SEQUENTIAL" -> "LINE_SEQUENTIAL";
                case "RELATIVE" -> "RELATIVE";
                default -> "SEQUENTIAL";
            };
            String acc = switch (fd.access) {
                case "RANDOM" -> "RANDOM";
                case "DYNAMIC" -> "DYNAMIC";
                default -> "SEQUENTIAL";
            };
            StringBuilder keys = new StringBuilder("null"), dups = new StringBuilder("null");
            if (fd.recordKey != null) {
                keys = new StringBuilder("new CobField[]{" + fieldRef(fd.recordKey));
                dups = new StringBuilder("new boolean[]{false");
                if (fd.alternateKeys != null) for (Ir.AltKey a : fd.alternateKeys) {
                    keys.append(", ").append(fieldRef(a.key));
                    dups.append(", ").append(a.duplicates);
                }
                keys.append("}");
                dups.append("}");
            }
            boolean variable = "V".equals(fd.recordingMode) || (fd.minRecord > 0 && fd.minRecord != fd.maxRecord);
            b.append("    final CobFile ").append(fileVar.get(fd.name)).append(" = new CobFile(\"").append(fd.name).append("\", \"").append(fd.assign)
                    .append("\", CobFile.Org.").append(org).append(", CobFile.Acc.").append(acc).append(", ").append(areaRef("aF_" + sanitize(fd.name))).append(", ")
                    .append(fd.minRecord).append(", ").append(Math.max(fd.maxRecord, 1)).append(", ")
                    .append(fd.fileStatus != null ? fieldRef(fd.fileStatus) : "null").append(", ").append(keys).append(", ").append(dups).append(", ")
                    .append(fd.recordDependingOn != null ? fieldRef(fd.recordDependingOn) : "null").append(", ").append(variable).append(");\n");
        }
        b.append("\n");
    }

    /**
     * A method whose body is one statement per line. Up to STATEMENTS_PER_METHOD lines it is written as is; a longer body
     * goes into helper methods (helper0, helper1, ...) of that many lines each, called in order, so no method passes the
     * JVM's 64 KB. {@code params} / {@code args} pass a local (such as a map being filled) to the helpers.
     */
    private void chunkedMethod(StringBuilder b, String header, List<String> lines, String tail, String helper, String params, String args) {
        b.append(header);
        if (lines.size() <= STATEMENTS_PER_METHOD) {
            for (String l : lines) b.append("        ").append(l).append("\n");
            b.append(tail).append("    }\n\n");
            return;
        }
        int chunks = (lines.size() + STATEMENTS_PER_METHOD - 1) / STATEMENTS_PER_METHOD;
        for (int c = 0; c < chunks; c++) b.append("        ").append(helper).append(c).append("(").append(args).append(");\n");
        b.append(tail).append("    }\n\n");
        for (int c = 0; c < chunks; c++) {
            b.append("    private void ").append(helper).append(c).append("(").append(params).append(") {\n");
            for (String l : lines.subList(c * STATEMENTS_PER_METHOD, Math.min(lines.size(), (c + 1) * STATEMENTS_PER_METHOD)))
                b.append("        ").append(l).append("\n");
            b.append("    }\n\n");
        }
    }

    private void emitInit(StringBuilder b) {
        List<String> init = new ArrayList<>();
        // EXTERNAL areas belong to the run unit: zeroed once when first allocated, never re-filled by a program. A USING
        // record has no storage of its own (it is the caller's): initializing the program never writes it.
        for (String a : areaSize.keySet())
            if (!externalAreas.containsKey(a) && !usingAreas.contains(a)) init.add(areaRef(a) + ".fill(CobConfig.DEFAULT_BYTE);");
        StringBuilder values = new StringBuilder();
        emitValues(values, "WorkingStorage");
        emitValues(values, "LocalStorage");
        for (String l : values.toString().split("\n")) if (!l.isBlank()) init.add(l.strip());
        chunkedMethod(b, "    @Override\n    protected void _init() {\n", init, "", "_init", "", "");
        // LOCAL-STORAGE: its areas, initialized again for every invocation (CobolBase.call)
        List<String> local = new ArrayList<>();
        for (Ir.Field f : ir.dictionary)
            if (f.parentId == null && f.lvl() != 88 && "LocalStorage".equals(f.section) && !local.contains(areaOf.get(f.id))) local.add(areaOf.get(f.id));
        if (!local.isEmpty()) {
            b.append("    @Override\n    protected Area[] _localAreas() { return new Area[]{").append(String.join(", ", local.stream().map(this::areaRef).toList())).append("}; }\n\n");
            b.append("    @Override\n    protected void _initLocal() {\n");
            for (String a : local) b.append("        ").append(areaRef(a)).append(".fill(CobConfig.DEFAULT_BYTE);\n");
            emitValues(b, "LocalStorage");
            b.append("    }\n\n");
        }
        if (ir.program.initial) {
            b.append("    @Override\n    protected boolean _initial() { return true; } // PROGRAM-ID ... IS INITIAL\n\n");
            b.append("    @Override\n    protected CobFile[] _files() { return new CobFile[]{").append(String.join(", ", fileVar.values())).append("}; }\n\n");
        }
    }

    /** VALUE clauses of one section's items. */
    private void emitValues(StringBuilder b, String section) {
        for (Ir.Field f : ir.dictionary) {
            if (f.lvl() == 88 || f.values == null || f.values.isEmpty()) continue;
            if (!section.equals(f.section)) continue;
            if (underRedefines(f) || isReturnCode(f)) continue;
            String lit;
            try {
                lit = f.valueAll ? "new AllLit(" + valueLiteral(f.values.get(0)) + ")" : valueLiteral(f.values.get(0));
            } catch (Abstain e) {
                b.append("        // TODO(frontier: VALUE cause=TRANSPILER fix=transpiler) ").append(f.field).append(": ").append(e.getMessage()).append("\n");
                continue;
            }
            if (f.dims == null || f.dims.isEmpty()) {
                b.append("        CobRt.move(").append(lit).append(", ").append(fieldRef(f.id)).append(");\n");
            } else {
                b.append("        ").append(loops(f.dims, 0, "CobRt.move(" + lit + ", " + fieldRef(f.id) + ".at(" + loopVars(f.dims.size(), 0) + "));")).append("\n");
            }
        }
    }

    private boolean underRedefines(Ir.Field f) {
        for (Ir.Field x = f; x != null; x = x.parentId == null ? null : byId.get(x.parentId))
            if (x.redefines != null && !x.redefines.isEmpty() && x.parentId != null) return true;
        return false;
    }

    private String loops(List<Integer> dims, int from, String inner) {
        StringBuilder sb = new StringBuilder();
        for (int i = from; i < dims.size(); i++) sb.append("for (int _i").append(i).append(" = 1; _i").append(i).append(" <= ").append(dims.get(i)).append("; _i").append(i).append("++) ");
        return sb.append(inner).toString();
    }

    private String loopVars(int n, int from) {
        StringBuilder sb = new StringBuilder();
        for (int i = from; i < n; i++) { if (i > from) sb.append(", "); sb.append("_i").append(i); }
        return sb.toString();
    }

    private String valueLiteral(Ir.Value v) {
        if (v.q) return jstr(v.v);
        String u = v.v.toUpperCase();
        String fig = figurative(u);
        if (fig != null) return fig;
        if (NUMERIC_LIT.matcher(v.v).matches()) return num(v.v);
        throw new Abstain("unsupported VALUE " + v.v);
    }

    static String figurative(String u) {
        return switch (u) {
            case "SPACE", "SPACES" -> "Fig.SPACES";
            case "ZERO", "ZEROS", "ZEROES" -> "Fig.ZEROS";
            case "HIGH-VALUE", "HIGH-VALUES" -> "Fig.HIGH_VALUES";
            case "LOW-VALUE", "LOW-VALUES" -> "Fig.LOW_VALUES";
            case "QUOTE", "QUOTES" -> "Fig.QUOTES";
            case "NULL", "NULLS" -> "Fig.NULLS";
            default -> null;
        };
    }

    private String num(String text) {
        String t = text.startsWith(".") ? "0" + text : text;
        if (t.endsWith(".")) t = t.substring(0, t.length() - 1);
        return numConsts.computeIfAbsent(t, k -> "N" + numConsts.size());
    }

    static String jstr(String s) {
        StringBuilder sb = new StringBuilder("\"");
        for (char c : s.toCharArray()) {
            if (c == '"' || c == '\\') sb.append('\\').append(c);
            // Octal escapes, not \\uXXXX: Java translates unicode escapes before lexing, so \\u000a would end the literal.
            else if (c < 0x20 || (c > 0x7E && c <= 0xFF)) sb.append(String.format("\\%03o", (int) c));
            else if (c > 0xFF) sb.append(String.format("\\u%04x", (int) c));
            else sb.append(c);
        }
        return sb.append('"').toString();
    }

    static String esc(String s) {
        return jstr(s).substring(1, jstr(s).length() - 1);
    }

    // ------------------------------------------------------------------ driver

    private void emitDriver(StringBuilder b) {
        int n = paras.size();
        if (n <= STATEMENTS_PER_METHOD) {
            b.append("    @Override\n    protected void _dispatch(int pc) {\n        switch (pc) {\n");
            for (int i = 0; i < n; i++) b.append("            case ").append(i).append(" -> ").append(methodName(i)).append("();\n");
            b.append("            default -> throw new IllegalStateException(\"bad paragraph index \" + pc);\n        }\n    }\n\n");
        } else {
            // Many paragraphs: one switch per STATEMENTS_PER_METHOD of them (a single one would pass the JVM's 64 KB).
            int chunks = (n + STATEMENTS_PER_METHOD - 1) / STATEMENTS_PER_METHOD;
            b.append("    @Override\n    protected void _dispatch(int pc) {\n        switch (pc / ").append(STATEMENTS_PER_METHOD).append(") {\n");
            for (int c = 0; c < chunks; c++) b.append("            case ").append(c).append(" -> _dispatch").append(c).append("(pc);\n");
            b.append("            default -> throw new IllegalStateException(\"bad paragraph index \" + pc);\n        }\n    }\n\n");
            for (int c = 0; c < chunks; c++) {
                b.append("    private void _dispatch").append(c).append("(int pc) {\n        switch (pc) {\n");
                for (int i = c * STATEMENTS_PER_METHOD; i < Math.min(n, (c + 1) * STATEMENTS_PER_METHOD); i++)
                    b.append("            case ").append(i).append(" -> ").append(methodName(i)).append("();\n");
                b.append("            default -> throw new IllegalStateException(\"bad paragraph index \" + pc);\n        }\n    }\n\n");
            }
        }
        b.append("    @Override\n    protected int _count() { return ").append(n).append("; }\n\n");
        int start = 0;
        while (start < n && paras.get(start).declarative) start++;
        if (start > 0) b.append("    @Override\n    protected int _start() { return ").append(start).append("; } // after DECLARATIVES\n\n");
        b.append("    @Override\n    protected Area[] _linkage() { return new Area[]{");
        b.append(String.join(", ", usingLinkageAreas().stream().map(this::areaRef).toList())).append("}; }\n\n");
        b.append("    @Override\n    public String programId() { return \"").append(ir.program.id).append("\"; }\n\n");
        if (n <= STATEMENTS_PER_METHOD) {
            b.append("    @Override\n    protected String[] _paragraphNames() { return new String[]{");
            for (int i = 0; i < n; i++) b.append(i > 0 ? ", " : "").append(jstr(paras.get(i).name.toUpperCase()));
            b.append("}; }\n\n");
        } else {
            List<String> names = new ArrayList<>();
            for (int i = 0; i < n; i++) names.add("n[" + i + "] = " + jstr(paras.get(i).name.toUpperCase()) + ";");
            chunkedMethod(b, "    @Override\n    protected String[] _paragraphNames() {\n        String[] n = new String[" + n + "];\n", names,
                    "        return n;\n", "_paragraphNames", "String[] n", "n");
        }
        List<String> puts = new ArrayList<>();
        Map<String, Integer> count = new HashMap<>();
        for (Ir.Field f : ir.dictionary) if (f.lvl() != 88) count.merge(f.field, 1, Integer::sum);
        for (Ir.Field f : ir.dictionary) {
            if (f.lvl() == 88 || "FILLER".equals(f.field) || count.get(f.field) > 1 || (f.dims != null && !f.dims.isEmpty())) continue;
            if (children.containsKey(f.id) && children.get(f.id).stream().anyMatch(c -> c.lvl() != 88)) continue;
            puts.add("m.put(\"" + esc(f.field) + "\", " + fieldRef(f.id) + ");");
        }
        StringBuilder fields = new StringBuilder();
        chunkedMethod(fields, "    @Override\n    protected java.util.Map<String, CobField> _fields() {\n        java.util.Map<String, CobField> m = new java.util.LinkedHashMap<>();\n",
                puts, "        return m;\n", "_fields", "java.util.Map<String, CobField> m", "m");
        // as before: the last method of the class closes without a blank line
        b.append(fields, 0, fields.length() - 1);
    }

    private String methodName(int i) {
        return "p" + i + "_" + sanitize(paras.get(i).name);
    }

    private int endIndex(String name) {
        Integer i = paraIndex.get(name.toUpperCase());
        if (i == null) throw new Abstain("unresolved paragraph " + name);
        Ir.SemParagraph p = paras.get(i);
        if (!p.isSection) return i;
        int j = i;
        while (j + 1 < paras.size() && name.equalsIgnoreCase(paras.get(j + 1).section) && !paras.get(j + 1).isSection) j++;
        return j;
    }

    private int startIndex(String name) {
        Integer i = paraIndex.get(name.toUpperCase());
        if (i == null) throw new Abstain("unresolved paragraph " + name);
        return i;
    }

    // ------------------------------------------------------------------ paragraphs and statements

    /**
     * A paragraph whose Java passes this many lines may pass the JVM's 64 KB per method (or per try statement): it is emitted
     * again with its large blocks outlined (see {@link #emitBlock}). Below it, the paragraph is emitted exactly as before.
     */
    static final int PARAGRAPH_MAX_LINES = 2500;
    /** In an outlined paragraph, a block of statements of at least this many lines becomes a method of its own. */
    static final int OUTLINE_MIN_LINES = 40;
    private boolean outlining;
    private final StringBuilder outlined = new StringBuilder();
    private int outlineSeq;
    /** arithmetic-osvs: binary operations of the current program whose intermediate result GnuCOBOL truncates, and to what scale. */
    private final java.util.IdentityHashMap<Ir.Node, int[]> osvsTrunc = new java.util.IdentityHashMap<>();

    private void emitParagraph(StringBuilder out, int i) {
        int[] counters = {tmp, lowered, abstainedCount, abstainedList.size()};
        int[] ruleMark = ruleMark();
        StringBuilder pb = new StringBuilder();
        emitParagraphBody(pb, i);
        if (pb.chars().filter(c -> c == '\n').count() > PARAGRAPH_MAX_LINES) {
            // Emit it again, outlining large blocks into methods; the first emission is discarded with its counts.
            tmp = counters[0]; lowered = counters[1]; abstainedCount = counters[2];
            abstainedList.subList(counters[3], abstainedList.size()).clear();
            ruleReset(ruleMark);
            pb.setLength(0);
            outlined.setLength(0);
            outlineSeq = 0;
            outlining = true;
            try {
                emitParagraphBody(pb, i);
            } finally {
                outlining = false;
            }
            pb.append(outlined);
            outlined.setLength(0);
        }
        out.append(pb);
    }

    private void emitParagraphBody(StringBuilder out, int i) {
        currentPara = i;
        Ir.SemParagraph p = paras.get(i);
        out.append("    // ").append(p.isSection ? "SECTION " : "PARAGRAPH ").append(p.name).append(" (L").append(p.line).append(")\n");
        out.append("    private void ").append(methodName(i)).append("() {\n");
        List<Ir.Stmt> stmts = p.statements == null ? List.of() : p.statements;
        int k = 0;
        while (k < stmts.size()) {
            int sentence = stmts.get(k).sentence;
            int end = k;
            while (end < stmts.size() && stmts.get(end).sentence == sentence) end++;
            List<Ir.Stmt> sent = stmts.subList(k, end);
            boolean needsLabel = sent.stream().anyMatch(this::hasNextSentence);
            if (needsLabel) {
                sentenceLabel = "s" + sentence;
                out.append("        ").append(sentenceLabel).append(": {\n");
                emitBlock(out, sent, 3);
                out.append("        }\n");
                sentenceLabel = null;
            } else emitBlock(out, sent, 2);
            k = end;
        }
        out.append("    }\n\n");
    }

    private boolean hasNextSentence(Ir.Stmt s) {
        if ("NEXT SENTENCE".equals(s.verb)) return true;
        if (s.blocks != null) for (List<Ir.Stmt> b : s.blocks.values()) for (Ir.Stmt c : b) if (hasNextSentence(c)) return true;
        return false;
    }

    private void emitBlock(StringBuilder out, List<Ir.Stmt> stmts, int indent) {
        if (stmts == null) return;
        if (!outlining) {
            for (Ir.Stmt s : stmts) emitStmt(out, s, indent);
            return;
        }
        StringBuilder blk = new StringBuilder();
        for (Ir.Stmt s : stmts) emitStmt(blk, s, indent);
        if (blk.chars().filter(c -> c == '\n').count() < OUTLINE_MIN_LINES || !outlineable(blk.toString())) {
            out.append(blk);
            return;
        }
        // The block becomes a method: a GO TO, EXIT PARAGRAPH or EXIT SECTION inside it (the paragraph's only returns) leaves
        // the paragraph through the method's result. Inner blocks were outlined first, so their call sites move along.
        String name = methodName(currentPara) + "_b" + outlineSeq++;
        String body = RETURN.matcher(blk.toString()).replaceAll("return true;");
        outlined.append("    private boolean ").append(name).append("() {\n");
        int shift = indent - 2;
        for (String line : body.split("\n", -1)) {
            if (line.isEmpty()) continue;
            outlined.append(line.startsWith("    ".repeat(shift)) ? line.substring(4 * shift) : line).append("\n");
        }
        outlined.append("        return false;\n    }\n\n");
        out.append(ind(indent)).append("if (").append(name).append("()) return;\n");
    }

    private static final java.util.regex.Pattern RETURN = java.util.regex.Pattern.compile("\\breturn;");
    private static final java.util.regex.Pattern JUMP = java.util.regex.Pattern.compile("\\b(break|continue)(\\s+(\\w+))?\\s*;");
    private static final java.util.regex.Pattern LABEL = java.util.regex.Pattern.compile("(?m)^\\s*(\\w+)\\s*:\\s*(\\{|for\\b|while\\b|do\\b)");
    private static final java.util.regex.Pattern TEMP = java.util.regex.Pattern.compile("\\b(_[A-Za-z]+\\d+)\\b");

    /**
     * A block can become a method when control and data stay inside it: every labelled break / continue names a label the
     * block declares (a NEXT SENTENCE or EXIT PERFORM out of it cannot be outlined), an unlabelled one has its loop inside
     * the block, and every emitter temporary it uses is declared in it.
     */
    private static boolean outlineable(String code) {
        Set<String> labels = new HashSet<>();
        java.util.regex.Matcher m = LABEL.matcher(code);
        while (m.find()) labels.add(m.group(1));
        boolean loops = code.contains("for (") || code.contains("while (") || code.contains("do {") || code.contains("switch (");
        m = JUMP.matcher(code);
        while (m.find()) {
            if (m.group(3) == null ? !loops : !labels.contains(m.group(3))) return false;
        }
        Set<String> temps = new HashSet<>();
        m = TEMP.matcher(code);
        while (m.find()) temps.add(m.group(1));
        for (String t : temps)
            if (!java.util.regex.Pattern.compile("[\\w>\\]]\\s+" + java.util.regex.Pattern.quote(t) + "\\s*[=;:)]").matcher(code).find()) return false;
        return true;
    }

    private static String ind(int n) {
        return "    ".repeat(n);
    }

    /** The statement being lowered (its step id names it in runtime checks). */
    private String currentStep = "";

    private void emitStmt(StringBuilder out, Ir.Stmt s, int indent) {
        String pad = ind(indent);
        currentStep = s.stepId;
        out.append(pad).append("// [").append(s.stepId).append("] ").append(oneLine(s.text)).append("\n");
        if (s.frontier != null) { frontier(out, s, s.frontier, indent); return; }
        StringBuilder sb = new StringBuilder();
        List<String> savedLoops = new ArrayList<>(loopLabels);
        int savedOutlined = outlined.length();
        int[] ruleMark = ruleMark();
        try {
            lower(sb, s, indent);
            out.append(sb);
            lowered++;
        } catch (Abstain e) {
            loopLabels.clear();
            loopLabels.addAll(savedLoops);
            outlined.setLength(savedOutlined); // blocks outlined for the abandoned lowering
            ruleReset(ruleMark); // rule sites of the abandoned lowering
            frontier(out, s, e.getMessage(), indent);
        }
    }

    private void frontier(StringBuilder out, Ir.Stmt s, String reason, int indent) {
        abstainedCount++;
        abstainedList.add(s.stepId + ": " + reason);
        out.append(ind(indent)).append("// TODO(frontier: ").append(s.verb).append(" cause=TRANSPILER fix=transpiler) COBOL: ").append(oneLine(s.text)).append("\n");
        out.append(ind(indent)).append("CobRt.frontier(\"").append(esc(s.stepId)).append("\", ").append(jstr(reason)).append(");\n");
    }

    static String oneLine(String t) {
        return t == null ? "" : t.replace('\n', ' ').replace('\r', ' ').replace("*/", "* /").replace("\\u", "\\ u");
    }

    private String t(String prefix) {
        return prefix + (++tmp);
    }

    private void lower(StringBuilder o, Ir.Stmt s, int in) {
        String p = ind(in);
        switch (s.verb) {
            case "MOVE" -> lowerMove(o, s, p);
            case "COMPUTE" -> lowerCompute(o, s, in);
            case "ADD", "SUBTRACT", "MULTIPLY", "DIVIDE" -> lowerArith(o, s, in);
            case "IF" -> {
                o.append(p).append("if (").append(topCond(s, null, s.n("cond"))).append(") {\n");
                emitBlock(o, s.block("TRUE"), in + 1);
                o.append(p).append("}");
                List<Ir.Stmt> f = s.block("FALSE");
                if (f != null && !f.isEmpty()) {
                    o.append(" else {\n");
                    emitBlock(o, f, in + 1);
                    o.append(p).append("}");
                }
                o.append("\n");
            }
            case "EVALUATE" -> lowerEvaluate(o, s, in);
            case "SEARCH" -> lowerSearch(o, s, in);
            case "SEARCH ALL" -> lowerSearchAll(o, s, in);
            case "PERFORM" -> lowerPerform(o, s, in);
            case "GO TO" -> lowerGoTo(o, s, p);
            case "ALTER" -> lowerAlter(o, s, p);
            case "SORT", "MERGE" -> lowerSort(o, s, in);
            case "RELEASE" -> {
                Ir.Field rf = byId.get(s.n("record").id);
                Ir.Field root = byId.get(rf.recordId);
                String sd = root == null ? null : root.file;
                if (sd == null || !files.containsKey(sd) || !files.get(sd).sort) throw new Abstain("RELEASE of a record that is not a sort file's");
                if (s.n("from") != null) o.append(p).append("CobRt.move(").append(operand(s.n("from"))).append(", ").append(field(s.n("record"))).append(");\n");
                o.append(p).append("CobSort.active(").append(sortVar(sd)).append(", \"").append(sd).append("\", \"RELEASE\").release();\n");
            }
            case "RETURN" -> {
                String sd = s.s("file");
                if (!files.containsKey(sd) || !files.get(sd).sort) throw new Abstain("RETURN from a file that is not a sort file");
                String r = t("_rt");
                o.append(p).append("int ").append(r).append(" = CobSort.active(").append(sortVar(sd)).append(", \"").append(sd).append("\", \"RETURN\").returnNext();\n");
                if (s.n("into") != null) o.append(p).append("if (").append(r).append(" == 0) CobRt.move(").append(sdRecord(sd)).append(", ").append(field(s.n("into"))).append(");\n");
                branch(o, s, in, "AT-END", r + " == 10");
                branch(o, s, in, "NOT-AT-END", r + " == 0");
            }
            case "DISPLAY" -> {
                List<String> items = new ArrayList<>();
                for (Ir.Node n : s.l("items")) {
                    if (numericFn(n)) throw new Abstain("DISPLAY of FUNCTION " + n.v + ": GnuCOBOL shows its result field's own digits and sign");
                    items.add(operand(n));
                }
                o.append(p).append("CobRt.display(").append("true".equals(s.s("noAdvancing"))).append(items.isEmpty() ? "" : ", ").append(String.join(", ", items)).append(");\n");
            }
            case "ACCEPT" -> o.append(p).append("CobRt.accept(").append(field(s.n("dst"))).append(", ").append(s.s("from") == null ? "null" : jstr(s.s("from"))).append(");\n");
            case "OPEN" -> {
                for (Ir.Node f : s.l("files")) ioStatus(o, in, f.v, fv(f.v) + ".open(\"" + f.op + "\")", t("_op"), false, false);
            }
            case "CLOSE" -> {
                for (Ir.Node f : s.l("files")) ioStatus(o, in, f.v, fv(f.v) + ".close()", t("_cl"), false, false);
            }
            case "READ" -> lowerRead(o, s, in);
            case "WRITE", "REWRITE" -> lowerWrite(o, s, in);
            case "DELETE" -> ioWithInvalid(o, s, in, fv(s.s("file")) + ".delete()");
            case "START" -> {
                String key = s.n("key") == null ? "null" : field(s.n("key"));
                ioWithInvalid(o, s, in, fv(s.s("file")) + ".start(\"" + (s.s("keyOp") == null ? "eq" : s.s("keyOp")) + "\", " + key + ")");
            }
            case "STRING" -> lowerString(o, s, in);
            case "UNSTRING" -> lowerUnstring(o, s, in);
            case "INSPECT" -> lowerInspect(o, s, p);
            case "INITIALIZE" -> {
                boolean options = s.s("filler") != null || s.s("toValue") != null || !s.l("replacing").isEmpty() || s.s("toDefault") != null;
                for (Ir.Node d : s.l("dst")) {
                    if (options) lowerInitializeWith(o, s, d, p);
                    else lowerInitialize(o, d, p);
                }
            }
            case "SET" -> lowerSet(o, s, p);
            case "CALL" -> lowerCall(o, s, in);
            case "GOBACK" -> o.append(p).append("if (CobRt.alwaysTrue()) throw CobRt.PROGRAM_EXIT;\n");
            case "STOP RUN" -> o.append(p).append("if (CobRt.alwaysTrue()) throw new CobRt.StopRun();\n");
            case "EXIT PROGRAM" -> o.append(p).append("exitProgram();\n");
            case "EXIT", "CONTINUE" -> o.append(p).append("// no operation\n");
            case "EXIT PARAGRAPH" -> o.append(p).append("if (CobRt.alwaysTrue()) return;\n");
            case "EXIT SECTION" -> {
                String sec = paras.get(currentPara).isSection ? paras.get(currentPara).name : paras.get(currentPara).section;
                if (sec == null) throw new Abstain("EXIT SECTION outside a section");
                o.append(p).append("_goto = ").append(endIndex(sec) + 1).append("; if (CobRt.alwaysTrue()) return;\n");
            }
            case "EXIT PERFORM", "EXIT PERFORM CYCLE" -> {
                if (loopLabels.isEmpty()) throw new Abstain("EXIT PERFORM outside an inline PERFORM");
                if (loopLabels.get(loopLabels.size() - 1).startsWith("!")) throw new Abstain("EXIT PERFORM inside PERFORM VARYING ... AFTER");
                o.append(p).append("if (CobRt.alwaysTrue()) ").append(s.verb.endsWith("CYCLE") ? "continue " : "break ").append(loopLabels.get(loopLabels.size() - 1)).append(";\n");
            }
            case "NEXT SENTENCE" -> {
                if (sentenceLabel == null) throw new Abstain("NEXT SENTENCE without an enclosing sentence");
                o.append(p).append("if (CobRt.alwaysTrue()) break ").append(sentenceLabel).append(";\n");
            }
            default -> throw new Abstain(s.verb + " is not lowered");
        }
    }

    private String fv(String file) {
        String v = fileVar.get(file);
        if (v == null) throw new Abstain("unknown file " + file);
        return v;
    }

    private void lowerMove(StringBuilder o, Ir.Stmt s, String p) {
        Ir.Node src = s.n("src");
        if ("true".equals(s.s("corr"))) {
            if (!"ref".equals(src.k)) throw new Abstain("MOVE CORRESPONDING needs a group sender");
            for (Ir.Node d : s.l("dst")) {
                List<String[]> pairs = new ArrayList<>();
                corresponding(src.id, d.id, pairs);
                if (pairs.isEmpty()) throw new Abstain("MOVE CORRESPONDING found no pairs");
                if (src.args != null || d.args != null) throw new Abstain("subscripted MOVE CORRESPONDING");
                for (String[] pr : pairs) o.append(p).append("CobRt.move(").append(pr[0]).append(", ").append(pr[1]).append(");\n");
            }
            return;
        }
        String sv = operand(src);
        List<Ir.Node> dsts = s.l("dst");
        if (numericFn(src))
            for (Ir.Node d : dsts) {
                Ir.Field df = d.id == null ? null : byId.get(d.id);
                if (df == null || d.start != null || !("Numeric".equals(df.category) || "NumericEdited".equals(df.category)))
                    throw new Abstain("MOVE of FUNCTION " + src.v + " to a non-numeric item: its digits are those of GnuCOBOL's result field");
            }
        if (dsts.size() > 1 && "fn".equals(src.k)) {
            String tv = t("_m");
            o.append(p).append("Object ").append(tv).append(" = ").append(sv).append(";\n");
            sv = tv;
        }
        for (Ir.Node d : dsts) o.append(p).append("CobRt.move(").append(sv).append(", ").append(field(d)).append(");\n");
    }

    private void corresponding(int srcGroup, int dstGroup, List<String[]> pairs) {
        for (Ir.Field sc : children.getOrDefault(srcGroup, List.of())) {
            if (sc.lvl() == 88 || "FILLER".equals(sc.field) || (sc.redefines != null && !sc.redefines.isEmpty()) || sc.dims != null && sc.occurs != null && !sc.occurs.isEmpty()) continue;
            for (Ir.Field dc : children.getOrDefault(dstGroup, List.of())) {
                if (!dc.field.equals(sc.field) || dc.lvl() == 88 || (dc.redefines != null && !dc.redefines.isEmpty())) continue;
                boolean sg = "Group".equals(sc.category), dg = "Group".equals(dc.category);
                if (sg && dg) corresponding(sc.id, dc.id, pairs);
                else if (!sg || !dg) pairs.add(new String[]{fieldRef(sc.id), fieldRef(dc.id)});
            }
        }
    }

    private void lowerCompute(StringBuilder o, Ir.Stmt s, int in) {
        String p = ind(in);
        boolean phrase = s.block("SIZE-ERROR") != null || s.block("NOT-SIZE-ERROR") != null;
        String v = t("_v"), se = t("_se");
        o.append(p).append("boolean ").append(se).append(" = false;\n");
        o.append(p).append("try {\n");
        Ir.Node expr = s.n("expr");
        osvs(s.l("dst"), expr, (char) 0);
        Integer sc = storeScale(s.l("dst"));
        String value = sc != null && "bin".equals(expr.k) && "/".equals(expr.op)
                ? "CobRt.divTo(" + num(expr.l) + ", " + num(expr.r) + ", " + (sc + 1) + ")" : num(expr);
        String typed = calc(s, expr, "Compute", s.l("dst"));
        if (typed != null) value = typed;
        o.append(p).append("    BigDecimal ").append(v).append(" = ").append(value).append(";\n");
        for (Ir.Node d : s.l("dst"))
            o.append(p).append("    ").append(se).append(" |= CobRt.store(").append(field(d)).append(", ").append(v).append(", ").append(Boolean.TRUE.equals(d.rounded)).append(", ").append(phrase).append(");\n");
        o.append(p).append("} catch (CobRt.SizeError ").append(t("_e")).append(") { ").append(se).append(" = true; }\n");
        sizeBlocks(o, s, in, se);
    }

    /** The largest scale among numeric receivers, or null when one is not numeric (its digits come from the value). */
    private Integer storeScale(List<Ir.Node> dsts) {
        int sc = 0;
        for (Ir.Node d : dsts) {
            Ir.Field f = d.id == null ? null : byId.get(d.id);
            if (f == null || d.start != null || !("Numeric".equals(f.category) || "NumericEdited".equals(f.category))) return null;
            sc = Math.max(sc, f.impliedDecimals);
        }
        return sc;
    }

    private void sizeBlocks(StringBuilder o, Ir.Stmt s, int in, String se) {
        String p = ind(in);
        List<Ir.Stmt> err = s.block("SIZE-ERROR"), ok = s.block("NOT-SIZE-ERROR");
        if (err != null) { o.append(p).append("if (").append(se).append(") {\n"); emitBlock(o, err, in + 1); o.append(p).append("}\n"); }
        if (ok != null) { o.append(p).append("if (!").append(se).append(") {\n"); emitBlock(o, ok, in + 1); o.append(p).append("}\n"); }
    }

    private void lowerArith(StringBuilder o, Ir.Stmt s, int in) {
        String p = ind(in);
        boolean phrase = s.block("SIZE-ERROR") != null || s.block("NOT-SIZE-ERROR") != null;
        String se = t("_se");
        String form = s.s("form");
        List<Ir.Node> ops = s.l("ops");
        o.append(p).append("boolean ").append(se).append(" = false;\n");
        o.append(p).append("try {\n");
        String q = p + "    ";
        switch (s.verb + ":" + form) {
            case "ADD:TO", "SUBTRACT:FROM" -> {
                Ir.Node list = binaryList(ops);
                if (ops.size() > 1) osvs(s.l("dst"), list, s.verb.equals("ADD") ? '+' : '-');
                String sum = num(list);
                List<Ir.Node> dsts = s.l("dst");
                if (ruleSet != null && dsts.size() == 1) {
                    // One receiver: its new value is receiver (+|-) operands, read before the store as the faithful lowering does.
                    Ir.Node d = dsts.get(0);
                    String value = calc(s, bin(s.verb.equals("ADD") ? "+" : "-", d, list), s.verb.equals("ADD") ? "AddTo" : "SubtractFrom", dsts);
                    if (value != null) {
                        o.append(q).append(se).append(" |= CobRt.store(").append(field(d)).append(", ").append(value).append(", ")
                                .append(Boolean.TRUE.equals(d.rounded)).append(", ").append(phrase).append(");\n");
                        break;
                    }
                } else if (ruleSet != null)
                    skipCalc(s, "several receivers: each adds to its own current value in turn (receivers may alias)");
                String v = t("_s");
                o.append(q).append("BigDecimal ").append(v).append(" = ").append(sum).append(";\n");
                for (Ir.Node d : s.l("dst"))
                    o.append(q).append(se).append(" |= CobRt.store(").append(field(d)).append(", CobRt.").append(s.verb.equals("ADD") ? "add" : "sub").append("(")
                            .append(field(d)).append(".dec(), ").append(v).append("), ").append(Boolean.TRUE.equals(d.rounded)).append(", ").append(phrase).append(");\n");
            }
            case "ADD:GIVING" -> {
                Ir.Node list = binaryList(ops);
                osvs(s.l("giving"), list, (char) 0);
                String value = num(list);
                String typed = calc(s, list, "AddGiving", s.l("giving"));
                storeGiving(o, q, s, se, typed != null ? typed : value, phrase);
            }
            case "SUBTRACT:GIVING" -> {
                Ir.Node value = bin("-", s.n("minuend"), binaryList(ops));
                osvs(s.l("giving"), value, (char) 0);
                String faithful = num(value);
                String typed = calc(s, value, "SubtractGiving", s.l("giving"));
                storeGiving(o, q, s, se, typed != null ? typed : faithful, phrase);
            }
            case "MULTIPLY:BY" -> {
                List<Ir.Node> dsts = s.l("dst");
                if (ruleSet != null && dsts.size() == 1) {
                    Ir.Node d = dsts.get(0);
                    String faithful = num(ops.get(0));
                    String typed = calc(s, bin("*", ops.get(0), d), "MultiplyBy", dsts);
                    if (typed != null) {
                        o.append(q).append(se).append(" |= CobRt.store(").append(field(d)).append(", ").append(typed).append(", ")
                                .append(Boolean.TRUE.equals(d.rounded)).append(", ").append(phrase).append(");\n");
                        break;
                    }
                } else if (ruleSet != null)
                    skipCalc(s, "several receivers: each is multiplied by its own current value in turn (receivers may alias)");
                for (Ir.Node d : dsts)
                    o.append(q).append(se).append(" |= CobRt.store(").append(field(d)).append(", CobRt.mul(").append(num(ops.get(0))).append(", ").append(field(d)).append(".dec()), ")
                            .append(Boolean.TRUE.equals(d.rounded)).append(", ").append(phrase).append(");\n");
            }
            case "MULTIPLY:GIVING" -> {
                String faithful = "CobRt.mul(" + num(ops.get(0)) + ", " + num(s.n("by")) + ")";
                String typed = calc(s, bin("*", ops.get(0), s.n("by")), "MultiplyGiving", s.l("giving"));
                storeGiving(o, q, s, se, typed != null ? typed : faithful, phrase);
            }
            case "DIVIDE:INTO" -> {
                if (ruleSet != null) skipCalc(s, "division: the runtime's truncated quotient and SIZE ERROR on a zero divisor stay in the adapter");
                for (Ir.Node d : s.l("dst")) {
                    Integer dsc = storeScale(List.of(d));
                    o.append(q).append(se).append(" |= CobRt.store(").append(field(d)).append(", CobRt.div").append(dsc == null ? "(" : "To(").append(field(d)).append(".dec(), ").append(num(ops.get(0)))
                            .append(dsc == null ? "" : ", " + (dsc + 1)).append("), ")
                            .append(Boolean.TRUE.equals(d.rounded)).append(", ").append(phrase).append(");\n");
                }
            }
            case "DIVIDE:INTO-GIVING", "DIVIDE:BY-GIVING" -> {
                if (ruleSet != null) skipCalc(s, "division: the runtime's truncated quotient and SIZE ERROR on a zero divisor stay in the adapter");
                String dividend = form.equals("INTO-GIVING") ? num(s.n("by")) : num(ops.get(0));
                String divisor = form.equals("INTO-GIVING") ? num(ops.get(0)) : num(s.n("by"));
                String dv = t("_dd"), ds = t("_dv");
                o.append(q).append("BigDecimal ").append(dv).append(" = ").append(dividend).append(", ").append(ds).append(" = ").append(divisor).append(";\n");
                List<Ir.Node> giving = s.l("giving");
                for (Ir.Node g : giving) {
                    Integer gsc = storeScale(List.of(g));
                    o.append(q).append(se).append(" |= CobRt.store(").append(field(g)).append(", CobRt.div").append(gsc == null ? "(" : "To(").append(dv).append(", ").append(ds)
                            .append(gsc == null ? "" : ", " + (gsc + 1)).append("), ")
                            .append(Boolean.TRUE.equals(g.rounded)).append(", ").append(phrase).append(");\n");
                }
                if (s.n("rem") != null) {
                    // REMAINDER = dividend − (stored quotient × divisor): the quotient keeps the GIVING item's decimals (fixes D4).
                    if (giving.size() != 1) throw new Abstain("DIVIDE REMAINDER with several GIVING items");
                    Ir.Node g = giving.get(0);
                    String qv = t("_q");
                    o.append(q).append("BigDecimal ").append(qv).append(" = CobRt.div(").append(dv).append(", ").append(ds).append(").setScale(Math.max(").append(field(g))
                            .append(".scale, 0), java.math.RoundingMode.DOWN);\n");
                    o.append(q).append(se).append(" |= CobRt.store(").append(field(s.n("rem"))).append(", CobRt.sub(").append(dv).append(", CobRt.mul(").append(qv).append(", ")
                            .append(ds).append(")), false, ").append(phrase).append(");\n");
                }
            }
            default -> throw new Abstain(s.verb + " " + form + " is not lowered");
        }
        o.append(p).append("} catch (CobRt.SizeError ").append(t("_e")).append(") { ").append(se).append(" = true; }\n");
        sizeBlocks(o, s, in, se);
    }

    private void storeGiving(StringBuilder o, String q, Ir.Stmt s, String se, String value, boolean phrase) {
        String v = t("_g");
        o.append(q).append("BigDecimal ").append(v).append(" = ").append(value).append(";\n");
        for (Ir.Node g : s.l("giving"))
            o.append(q).append(se).append(" |= CobRt.store(").append(field(g)).append(", ").append(v).append(", ").append(Boolean.TRUE.equals(g.rounded)).append(", ").append(phrase).append(");\n");
    }

    /** The operands of ADD / SUBTRACT as cobc combines them (cb_build_binary_list): ((a + b) + c) ... */
    private static Ir.Node binaryList(List<Ir.Node> ops) {
        if (ops.isEmpty()) throw new Abstain("arithmetic without operands");
        Ir.Node acc = ops.get(0);
        for (int i = 1; i < ops.size(); i++) acc = bin("+", acc, ops.get(i));
        return acc;
    }

    private static Ir.Node bin(String op, Ir.Node l, Ir.Node r) {
        Ir.Node n = new Ir.Node();
        n.k = "bin";
        n.op = op;
        n.l = l;
        n.r = r;
        return n;
    }

    /**
     * arithmetic-osvs (GnuCOBOL -std=ibm): which intermediate results of the statement cobc truncates
     * ({@link OsvsPlanner}). A GnuCOBOL defect (an intermediate scaled down instead of up) is a frontier.
     */
    private void osvs(List<Ir.Node> receivers, Ir.Node value, char op) {
        try {
            osvsTrunc.putAll(OsvsPlanner.plan(this::osvsFacts, receivers == null ? List.of() : receivers, value, op));
        } catch (OsvsPlanner.Defect e) {
            throw new Abstain(e.getMessage());
        } catch (IllegalArgumentException e) {
            throw new Abstain("arithmetic-osvs: " + e.getMessage());
        }
    }

    private OsvsPlanner.FieldFacts osvsFacts(int id) {
        Ir.Field f = byId.get(id);
        if (f == null) return null;
        boolean binary = "COMP".equals(f.usage) || "COMP-5".equals(f.usage) || "Index".equals(f.category);
        return new OsvsPlanner.FieldFacts(f.impliedDecimals, binary && f.impliedDecimals == 0 && (f.length == 1 || f.length == 2 || f.length == 4 || f.length == 8),
                f.length);
    }

    private void lowerEvaluate(StringBuilder o, Ir.Stmt s, int in) {
        String p = ind(in);
        List<Ir.Stmt> whens = s.block("WHEN");
        if (whens == null || whens.isEmpty()) { o.append(p).append("// EVALUATE without WHEN\n"); return; }
        boolean first = true;
        for (Ir.Stmt w : whens) {
            boolean other = "true".equals(w.s("other"));
            String c = other ? null : topCond(w, null, w.n("cond"));
            if (first) o.append(p).append("if (").append(c == null ? "true" : c).append(") { // [").append(w.stepId).append("] ").append(oneLine(w.text)).append("\n");
            else if (other) o.append(p).append("} else { // [").append(w.stepId).append("] WHEN OTHER\n");
            else o.append(p).append("} else if (").append(c).append(") { // [").append(w.stepId).append("] ").append(oneLine(w.text)).append("\n");
            emitBlock(o, w.block("TRUE"), in + 1);
            first = false;
            lowered++;
        }
        o.append(p).append("}\n");
    }

    /** The sort file's current SORT (an instance field, set while its SORT or MERGE runs). */
    private String sortVar(String sd) {
        sortFiles.add(sd);
        return "_sort_" + sanitize(sd);
    }

    /** The SD's record area as a group item. */
    private String sdRecord(String sd) {
        return "CobField.group(\"" + sd + "\", aF_" + sanitize(sd) + ", 0, " + Math.max(files.get(sd).maxRecord, 1) + ", null, null)";
    }

    /**
     * SORT / MERGE (CobSort): records from the USING files (opened, read and closed by the statement) or from RELEASE in
     * the INPUT PROCEDURE, sorted stably on the keys, to the GIVING files or to RETURN in the OUTPUT PROCEDURE; then
     * SORT-RETURN is 0. SORT of a table reorders its occurrences (equal keys stop the run: GnuCOBOL leaves their order
     * to qsort).
     */
    private void lowerSort(StringBuilder o, Ir.Stmt s, int in) {
        String p = ind(in), q = ind(in + 1);
        List<String> keys = new ArrayList<>(), asc = new ArrayList<>();
        if (s.s("file") == null) {
            Ir.Node tn = s.n("table");
            Ir.Field t = tn == null ? null : byId.get(tn.id);
            if (t == null || t.occurs == null || t.occurs.isEmpty()) throw new Abstain("SORT of an item that is not a table");
            if (t.dims != null && t.dims.size() > 1) throw new Abstain("SORT of a table inside a table");
            for (Ir.Node k : s.l("keys")) { keys.add(firstOccurrence(byId.get(k.l.id))); asc.add(String.valueOf("ASC".equals(k.op))); }
            if (keys.isEmpty()) throw new Abstain("SORT of a table without KEY");
            o.append(p).append("CobSort.table(").append(firstOccurrence(t)).append(", ").append(searchSize(t)).append(", new CobField[]{").append(String.join(", ", keys))
                    .append("}, new boolean[]{").append(String.join(", ", asc)).append("});\n");
            return;
        }
        String sd = s.s("file");
        Ir.File fd = files.get(sd);
        if (fd == null || !fd.sort) throw new Abstain(s.verb + " of " + sd + ", which is not an SD file");
        for (Ir.Node k : s.l("keys")) { keys.add(fieldRef(k.l.id)); asc.add(String.valueOf("ASC".equals(k.op))); }
        if (keys.isEmpty()) throw new Abstain(s.verb + " without KEY");
        String v = sortVar(sd), srt = t("_srt");
        o.append(p).append("{\n");
        o.append(q).append("CobSort ").append(srt).append(" = new CobSort(\"").append(sd).append("\", ").append(areaRef("aF_" + sanitize(sd))).append(", ").append(Math.max(fd.maxRecord, 1))
                .append(", new CobField[]{").append(String.join(", ", keys)).append("}, new boolean[]{").append(String.join(", ", asc)).append("});\n");
        o.append(q).append(v).append(" = ").append(srt).append(";\n");
        o.append(q).append("try {\n");
        String r = ind(in + 2);
        if (s.s("inputProc") != null) {
            if ("MERGE".equals(s.verb)) throw new Abstain("MERGE with an INPUT PROCEDURE");
            o.append(r).append("perform(").append(startIndex(s.s("inputProc"))).append(", ").append(endIndex(s.s("inputThru") != null ? s.s("inputThru") : s.s("inputProc"))).append(");\n");
        } else for (Ir.Node f : s.l("using")) o.append(r).append(srt).append(".using(").append(fv(f.v)).append(");\n");
        o.append(r).append(srt).append(".sort();\n");
        if (s.s("outputProc") != null)
            o.append(r).append("perform(").append(startIndex(s.s("outputProc"))).append(", ").append(endIndex(s.s("outputThru") != null ? s.s("outputThru") : s.s("outputProc"))).append(");\n");
        else for (Ir.Node f : s.l("giving")) o.append(r).append(srt).append(".giving(").append(fv(f.v)).append(");\n");
        o.append(q).append("} finally {\n").append(r).append(v).append(" = null;\n").append(q).append("}\n");
        o.append(q).append("CobRt.SORT_RETURN.setDec(BigDecimal.ZERO, false);\n");
        o.append(p).append("}\n");
    }

    /** The number of occurrences a SEARCH covers: the OCCURS DEPENDING ON value at the start, else the maximum. */
    private String searchSize(Ir.Field t) {
        if (t.occurs == null || t.occurs.isEmpty()) throw new Abstain(t.field + " is not a table");
        return t.dependingOnId != null ? field(refTo(t.dependingOnId)) + ".intValue()" : t.occurs;
    }

    /**
     * Serial SEARCH as GnuCOBOL generates it: while the index is not past the table's size, the WHENs are tested in
     * order (the first true one runs and ends the search); otherwise the index steps by one and a VARYING item that is
     * not the table's own index takes the index's value. Past the end, AT END runs. The index is not checked below 1.
     */
    private void lowerSearch(StringBuilder o, Ir.Stmt s, int in) {
        String p = ind(in), q = ind(in + 1), r = ind(in + 2);
        Ir.Field t = byId.get(s.n("table").id);
        String idx = field(s.n("index"));
        Ir.Node vary = s.n("varying");
        boolean other = vary != null && !vary.id.equals(s.n("index").id);
        String label = t("L"), max = t("_max");
        o.append(p).append(label).append(": {\n");
        o.append(q).append("final int ").append(max).append(" = ").append(searchSize(t)).append(";\n");
        String lc = t("_lc");
        o.append(q).append("long ").append(lc).append(" = 0;\n");
        o.append(q).append("for (;;) {\n");
        o.append(r).append("CobRt.loopTick(\"").append(esc(s.stepId)).append("\", ++").append(lc).append(");\n");
        o.append(r).append("if (").append(idx).append(".intValue() > ").append(max).append(") {\n");
        emitBlock(o, s.block("AT-END"), in + 3);
        o.append(r).append("    break ").append(label).append(";\n").append(r).append("}\n");
        for (Ir.Stmt w : s.block("WHEN")) {
            o.append(r).append("if (").append(cond(w.n("cond"))).append(") { // [").append(w.stepId).append("] ").append(oneLine(w.text)).append("\n");
            emitBlock(o, w.block("TRUE"), in + 3);
            o.append(r).append("    break ").append(label).append(";\n").append(r).append("}\n");
            lowered++;
        }
        o.append(r).append("CobRt.store(").append(idx).append(", BigDecimal.valueOf(").append(idx).append(".intValue() + 1L), false, false);\n");
        if (other) o.append(r).append("CobRt.store(").append(field(vary)).append(", ").append(idx).append(".dec(), false, false);\n");
        o.append(q).append("}\n").append(p).append("}\n");
    }

    /**
     * SEARCH ALL as GnuCOBOL generates it: a binary search with head = 0 and tail = size + 1; the index probes
     * (head + tail) / 2; the WHEN's key conditions are compared in the table's KEY order (DESCENDING keys negated), the
     * first unequal one deciding the direction (below: head = index, else tail = index); AT END when head >= tail - 1.
     * The WHEN must be KEY = value (either side) or a key's single-valued condition-name, joined by AND.
     */
    private void lowerSearchAll(StringBuilder o, Ir.Stmt s, int in) {
        String p = ind(in), q = ind(in + 1), r = ind(in + 2);
        Ir.Field t = byId.get(s.n("table").id);
        if (t.keys == null || t.keys.isEmpty()) throw new Abstain("SEARCH ALL of " + t.field + ", which has no ASCENDING/DESCENDING KEY");
        String idx = field(s.n("index"));
        Ir.Stmt w = s.block("WHEN").get(0);
        Map<Integer, Ir.Node[]> terms = new LinkedHashMap<>();
        searchAllTerms(w.n("cond"), t, terms);
        List<String> cmps = new ArrayList<>();
        for (Ir.Key k : t.keys) {
            Ir.Node[] kv = terms.remove(k.id);
            if (kv == null) continue;
            String c = "CobRt.compare(" + operand(kv[0]) + ", " + operand(kv[1]) + ")";
            cmps.add(k.ascending ? c : "-" + c);
        }
        if (!terms.isEmpty() || cmps.isEmpty()) throw new Abstain("SEARCH ALL condition is not KEY = value AND ...");
        String label = t("L"), head = t("_head"), tail = t("_tail"), mid = t("_mid"), ret = t("_ret");
        o.append(p).append(label).append(": {\n");
        o.append(q).append("int ").append(head).append(" = 0, ").append(tail).append(" = ").append(searchSize(t)).append(" + 1;\n");
        String lc = t("_lc");
        o.append(q).append("long ").append(lc).append(" = 0;\n");
        o.append(q).append("for (;;) {\n");
        o.append(r).append("CobRt.loopTick(\"").append(esc(s.stepId)).append("\", ++").append(lc).append(");\n");
        o.append(r).append("if (").append(head).append(" >= ").append(tail).append(" - 1) {\n");
        emitBlock(o, s.block("AT-END"), in + 3);
        o.append(r).append("    break ").append(label).append(";\n").append(r).append("}\n");
        o.append(r).append("int ").append(mid).append(" = (").append(head).append(" + ").append(tail).append(") / 2;\n");
        o.append(r).append("CobRt.store(").append(idx).append(", BigDecimal.valueOf(").append(mid).append("), false, false);\n");
        o.append(r).append("int ").append(ret).append(";\n");
        StringBuilder all = new StringBuilder();
        for (int i = 0; i < cmps.size(); i++) all.append(i > 0 ? " && " : "").append("(").append(ret).append(" = ").append(cmps.get(i)).append(") == 0");
        o.append(r).append("if (").append(all).append(") { // [").append(w.stepId).append("] ").append(oneLine(w.text)).append("\n");
        emitBlock(o, w.block("TRUE"), in + 3);
        o.append(r).append("    break ").append(label).append(";\n").append(r).append("}\n");
        lowered++;
        o.append(r).append("if (").append(ret).append(" < 0) ").append(head).append(" = ").append(mid).append("; else ").append(tail).append(" = ").append(mid).append(";\n");
        o.append(q).append("}\n").append(p).append("}\n");
    }

    private void searchAllTerms(Ir.Node c, Ir.Field t, Map<Integer, Ir.Node[]> terms) {
        if (c == null) throw new Abstain("SEARCH ALL without a condition");
        Set<Integer> keyIds = new HashSet<>();
        for (Ir.Key k : t.keys) keyIds.add(k.id);
        switch (c.k) {
            case "and" -> { searchAllTerms(c.l, t, terms); searchAllTerms(c.r, t, terms); }
            case "rel" -> {
                if (!"eq".equals(c.op)) throw new Abstain("SEARCH ALL condition with " + c.op);
                if ("ref".equals(c.l.k) && keyIds.contains(c.l.id)) terms.put(c.l.id, new Ir.Node[]{c.l, c.r});
                else if ("ref".equals(c.r.k) && keyIds.contains(c.r.id)) terms.put(c.r.id, new Ir.Node[]{c.r, c.l});
                else throw new Abstain("SEARCH ALL condition on an item that is not a KEY of " + t.field);
            }
            case "c88" -> {
                Ir.Field cn = byId.get(c.id);
                if (cn.parentId == null || !keyIds.contains(cn.parentId) || cn.values == null || cn.values.size() != 1 || cn.values.get(0).hi != null)
                    throw new Abstain("SEARCH ALL condition-name that is not a single value of a KEY");
                Ir.Node key = new Ir.Node();
                key.k = "ref";
                key.id = cn.parentId;
                key.v = byId.get(cn.parentId).field;
                key.args = c.args;
                terms.put(cn.parentId, new Ir.Node[]{key, valueNode(cn.values.get(0))});
            }
            default -> throw new Abstain("SEARCH ALL condition " + c.k);
        }
    }

    /** A VALUE literal as an operand node. */
    private static Ir.Node valueNode(Ir.Value v) {
        Ir.Node n = new Ir.Node();
        if (v.q) { n.k = "str"; n.v = v.v; return n; }
        String fig = figurative(v.v.toUpperCase());
        if (fig != null) { n.k = "fig"; n.v = v.v.toUpperCase(); return n; }
        n.k = "num";
        n.v = v.v;
        return n;
    }

    private void lowerPerform(StringBuilder o, Ir.Stmt s, int in) {
        String p = ind(in);
        boolean inline = "true".equals(s.s("inline"));
        String body;
        StringBuilder bodyBuf = new StringBuilder();
        String label = t("L");
        if (!s.l("after").isEmpty()) {
            lowerVaryingAfter(o, s, in, inline, label);
            return;
        }
        if (inline) {
            loopLabels.add(label);
            emitBlock(bodyBuf, s.block("LOOP"), in + 1);
            loopLabels.remove(loopLabels.size() - 1);
            body = bodyBuf.toString();
        } else {
            String target = s.s("target");
            int lo = startIndex(target);
            int hi = s.s("thru") != null ? endIndex(s.s("thru")) : endIndex(target);
            body = ind(in + 1) + "perform(" + lo + ", " + hi + ");\n";
        }
        // The runaway-loop guard counts this loop's iterations from its entry (a long batch legitimately runs billions of
        // iterations over all its loops; one loop that never ends is what the guard is for).
        String lc = t("_lc");
        String tick = ind(in + 1) + "CobRt.loopTick(\"" + esc(s.stepId) + "\", ++" + lc + ");\n";
        Ir.Node until = s.n("until");
        Ir.Node times = s.n("times");
        Ir.Node var = s.n("var");
        if (var != null || until != null) o.append(ind(in)).append("long ").append(lc).append(" = 0;\n");
        boolean after = "AFTER".equals(s.s("test"));
        if (var != null && after) {
            // WITH TEST AFTER: the condition is evaluated after each execution and before the increment, so the body also
            // runs with the value that first satisfies it (UNTIL I > 12 runs I = 1..13).
            String f = field(var);
            o.append(p).append(label).append(": for (CobRt.store(").append(f).append(", ").append(num(s.n("from"))).append(", false, false); ; CobRt.store(")
                    .append(f).append(", CobRt.add(").append(f).append(".dec(), ").append(num(s.n("by"))).append("), false, false)) {\n")
                    .append(tick).append(body).append(ind(in + 1)).append("if (").append(topCond(s, "UNTIL", until)).append(") break ").append(label).append(";\n").append(p).append("}\n");
        } else if (var != null) {
            String f = field(var);
            o.append(p).append(label).append(": for (CobRt.store(").append(f).append(", ").append(num(s.n("from"))).append(", false, false); !(").append(topCond(s, "UNTIL", until))
                    .append("); CobRt.store(").append(f).append(", CobRt.add(").append(f).append(".dec(), ").append(num(s.n("by"))).append("), false, false)) {\n")
                    .append(tick).append(body).append(p).append("}\n");
        } else if (until != null) {
            if (after) o.append(p).append(label).append(": do {\n").append(tick).append(body).append(p).append("} while (!(").append(topCond(s, "UNTIL", until)).append("));\n");
            else o.append(p).append(label).append(": while (!(").append(topCond(s, "UNTIL", until)).append(")) {\n").append(tick).append(body).append(p).append("}\n");
        } else if (times != null) {
            String tv = t("_t");
            o.append(p).append(label).append(": for (long ").append(tv).append(" = ").append(num(times)).append(".longValue(); ").append(tv).append(" > 0; ").append(tv).append("--) {\n")
                    .append(body).append(p).append("}\n");
        } else if (inline) {
            o.append(p).append(label).append(": do {\n").append(body).append(p).append("} while (false);\n");
        } else {
            o.append(body.substring(4));
        }
    }

    /**
     * PERFORM ... VARYING ... AFTER ... as GnuCOBOL generates it. Each level is a loop; a level sets the next level's
     * variable to its FROM value at the top of each of its rounds (before its own test, so FROM may use outer
     * variables). WITH TEST BEFORE a level tests its UNTIL before running the inner level or the body; WITH TEST AFTER
     * after it. Then the level's variable steps by its BY. EXIT PERFORM [CYCLE] inside is not lowered.
     */
    private void lowerVaryingAfter(StringBuilder o, Ir.Stmt s, int in, boolean inline, String label) {
        List<Ir.Node[]> lv = new ArrayList<>();
        lv.add(new Ir.Node[]{s.n("var"), s.n("from"), s.n("by"), s.n("until")});
        for (Ir.Node a : s.l("after")) lv.add(new Ir.Node[]{a.args.get(0), a.args.get(1), a.args.get(2), a.args.get(3)});
        boolean after = "AFTER".equals(s.s("test"));
        String body;
        int depth = in + lv.size();
        if (inline) {
            loopLabels.add("!VARYING-AFTER");
            StringBuilder b = new StringBuilder();
            emitBlock(b, s.block("LOOP"), depth + 1);
            loopLabels.remove(loopLabels.size() - 1);
            body = b.toString();
        } else {
            String target = s.s("target");
            int lo = startIndex(target);
            int hi = s.s("thru") != null ? endIndex(s.s("thru")) : endIndex(target);
            body = ind(depth + 1) + "perform(" + lo + ", " + hi + ");\n";
        }
        List<String> labels = new ArrayList<>();
        labels.add(label);
        for (int k = 1; k < lv.size(); k++) labels.add(t("L"));
        o.append(ind(in)).append("CobRt.store(").append(field(lv.get(0)[0])).append(", ").append(num(lv.get(0)[1])).append(", false, false);\n");
        varyingLevel(o, s, lv, labels, 0, in, after, body);
    }

    private void varyingLevel(StringBuilder o, Ir.Stmt s, List<Ir.Node[]> lv, List<String> labels, int k, int in, boolean after, String body) {
        String p = ind(in), q = ind(in + 1);
        Ir.Node[] me = lv.get(k);
        boolean last = k == lv.size() - 1;
        String lc = t("_lc");
        o.append(p).append("long ").append(lc).append(" = 0;\n");
        o.append(p).append(labels.get(k)).append(": for (;;) {\n");
        o.append(q).append("CobRt.loopTick(\"").append(esc(s.stepId)).append("\", ++").append(lc).append(");\n");
        String reset = last ? "" : q + "CobRt.store(" + field(lv.get(k + 1)[0]) + ", " + num(lv.get(k + 1)[1]) + ", false, false);\n";
        String test = q + "if (" + topCond(s, k == 0 ? "UNTIL" : "UNTIL" + k, me[3]) + ") break " + labels.get(k) + ";\n";
        if (!after) o.append(reset).append(test);
        else o.append(reset);
        if (last) o.append(body);
        else varyingLevel(o, s, lv, labels, k + 1, in + 1, after, body);
        if (after) o.append(test);
        String f = field(me[0]);
        o.append(q).append("CobRt.store(").append(f).append(", CobRt.add(").append(f).append(".dec(), ").append(num(me[2])).append("), false, false);\n");
        o.append(p).append("}\n");
    }

    /**
     * ALTER: every altered paragraph must be a lone GO TO sentence (the standard's rule); its GO TO then jumps to an
     * instance field that ALTER sets, so the change persists as COBOL's does, PERFORM ranges included.
     */
    private void planAlter() {
        Set<String> altered = new java.util.LinkedHashSet<>();
        for (Ir.SemParagraph p : paras) collectAlters(p.statements, altered);
        for (String name : altered) {
            Integer pi = paraIndex.get(name);
            if (pi == null) { alterProblem = "ALTER of an unknown paragraph " + name; return; }
            List<Ir.Stmt> st = paras.get(pi).statements;
            if (st == null || st.size() != 1 || !"GO TO".equals(st.get(0).verb) || st.get(0).n("depending") != null || st.get(0).l("targets").size() > 1) {
                alterProblem = "ALTERed paragraph " + name + " is not a single GO TO sentence";
                return;
            }
            alterVar.put(pi, "_alter" + pi);
        }
    }

    private void collectAlters(List<Ir.Stmt> list, Set<String> out) {
        if (list == null) return;
        for (Ir.Stmt s : list) {
            if ("ALTER".equals(s.verb)) for (Ir.Node pr : s.l("pairs")) out.add(pr.v);
            if (s.blocks != null) for (List<Ir.Stmt> b : s.blocks.values()) collectAlters(b, out);
        }
    }

    private void lowerAlter(StringBuilder o, Ir.Stmt s, String p) {
        if (alterProblem != null) throw new Abstain(alterProblem);
        for (Ir.Node pr : s.l("pairs")) {
            String v = alterVar.get(paraIndex.get(pr.v));
            if (v == null) throw new Abstain("ALTER of " + pr.v + " is not planned");
            o.append(p).append(v).append(" = ").append(startIndex(pr.op)).append("; // ALTER ").append(pr.v).append(" TO PROCEED TO ").append(pr.op).append("\n");
        }
    }

    private void lowerGoTo(StringBuilder o, Ir.Stmt s, String p) {
        String av = alterVar.get(currentPara);
        if (av != null && alterProblem == null) {
            o.append(p).append("if (").append(av).append(" < 0) throw new CobRt.RuntimeCheck(\"GO TO in ").append(esc(paras.get(currentPara).name))
                    .append(" has no target: no ALTER has run\");\n");
            o.append(p).append("_goto = ").append(av).append("; if (CobRt.alwaysTrue()) return; // GO TO (ALTERable)\n");
            return;
        }
        List<Ir.Node> targets = s.l("targets");
        if (targets.isEmpty()) throw new Abstain("GO TO without a target in a paragraph no ALTER changes");
        Ir.Node dep = s.n("depending");
        if (dep == null) {
            o.append(p).append("_goto = ").append(startIndex(targets.get(0).v)).append("; if (CobRt.alwaysTrue()) return; // GO TO ").append(targets.get(0).v).append("\n");
            return;
        }
        o.append(p).append("switch (").append(field(dep)).append(".intValue()) {\n");
        for (int i = 0; i < targets.size(); i++)
            o.append(p).append("    case ").append(i + 1).append(" -> { _goto = ").append(startIndex(targets.get(i).v)).append("; if (CobRt.alwaysTrue()) return; }\n");
        o.append(p).append("    default -> { }\n").append(p).append("}\n");
    }

    private void lowerRead(StringBuilder o, Ir.Stmt s, int in) {
        String p = ind(in);
        String file = s.s("file");
        Ir.File fd = files.get(file);
        String f = fv(file);
        boolean seq = "NEXT".equals(s.s("direction")) || fd == null || !"INDEXED".equals(fd.organization) || "SEQUENTIAL".equals(fd.access);
        if ("PREVIOUS".equals(s.s("direction"))) throw new Abstain("READ PREVIOUS is not lowered");
        String call = seq ? f + ".readNext()" : f + ".readKey(" + (s.n("key") == null ? "null" : field(s.n("key"))) + ")";
        String r = t("_r");
        ioStatus(o, in, file, call, r, s.block("AT-END") != null, s.block("INVALID") != null);
        if (s.n("into") != null) o.append(p).append("if (").append(r).append(" < 10) CobRt.move(").append(f).append(".record(), ").append(field(s.n("into"))).append(");\n");
        // GnuCOBOL routes only status 10 to AT END (46, a read past the end, is an error, not an AT END condition)
        branch(o, s, in, "AT-END", r + " == 10");
        branch(o, s, in, "NOT-AT-END", r + " < 10");
        branch(o, s, in, "INVALID", INVALID_KEY.formatted(r, r));
        branch(o, s, in, "NOT-INVALID", r + " < 10");
    }

    /** GnuCOBOL's INVALID KEY condition: status 21 to 24. */
    private static final String INVALID_KEY = "(%s >= 21 && %s <= 24)";

    /**
     * The status of an I/O statement, as GnuCOBOL routes it: a phrase of the statement takes its condition (AT END:
     * status 10; INVALID KEY: 21-24); any other exception (status 10 or more) runs the file's USE AFTER ERROR
     * procedure, else the USE procedure for the file's last OPEN mode, else, without FILE STATUS, stops the run.
     * Control then continues after the statement.
     */
    private void ioStatus(StringBuilder o, int in, String file, String call, String r, boolean atEnd, boolean invalid) {
        String p = ind(in);
        String f = fv(file);
        o.append(p).append("int ").append(r).append(" = ").append(call).append(";\n");
        List<String> phrase = new ArrayList<>();
        if (atEnd) phrase.add(r + " == 10");
        if (invalid) phrase.add(INVALID_KEY.formatted(r, r));
        String taken = phrase.isEmpty() ? "false" : String.join(" || ", phrase);
        Integer use = useFor.get(file);
        if (use != null) {
            o.append(p).append("if (").append(r).append(" >= 10 && !(").append(taken).append(")) perform(").append(use).append(", ").append(endIndex(paras.get(use).name)).append(");\n");
            return;
        }
        if (!useForMode.isEmpty()) {
            o.append(p).append("if (").append(r).append(" >= 10 && !(").append(taken).append(")) {\n");
            o.append(p).append("    switch (").append(f).append(".lastOpenMode()) {\n");
            for (Map.Entry<String, Integer> e : useForMode.entrySet())
                o.append(p).append("        case \"").append(e.getKey()).append("\" -> perform(").append(e.getValue()).append(", ").append(endIndex(paras.get(e.getValue()).name)).append(");\n");
            o.append(p).append("        default -> ").append(f).append(".unhandled(").append(r).append(", false);\n");
            o.append(p).append("    }\n").append(p).append("}\n");
            return;
        }
        o.append(p).append(f).append(".unhandled(").append(r).append(", ").append(taken).append(");\n");
    }

    private void branch(StringBuilder o, Ir.Stmt s, int in, String key, String cond) {
        List<Ir.Stmt> b = s.block(key);
        if (b == null) return;
        o.append(ind(in)).append("if (").append(cond).append(") {\n");
        emitBlock(o, b, in + 1);
        o.append(ind(in)).append("}\n");
    }

    private void lowerWrite(StringBuilder o, Ir.Stmt s, int in) {
        String p = ind(in);
        Ir.Node rec = s.n("record");
        if (rec == null || rec.id == null) throw new Abstain("WRITE without a record");
        Ir.Field rf = byId.get(rec.id);
        String file = rf.file;
        if (file == null) {
            Ir.Field root = byId.get(rf.recordId);
            file = root == null ? null : root.file;
        }
        if (file == null) throw new Abstain("record " + rf.field + " belongs to no file");
        // ADVANCING: CobFile.write(len, 1 = BEFORE | 2 = AFTER, lines | -1 = PAGE); GnuCOBOL's control bytes.
        String adv = "";
        if (s.s("advWhen") != null) {
            if (!s.verb.equals("WRITE")) throw new Abstain("ADVANCING on " + s.verb);
            String lines = "true".equals(s.s("advPage")) ? "-1" : intOf(s.n("advLines"));
            adv = ", " + ("BEFORE".equals(s.s("advWhen")) ? 1 : 2) + ", " + lines;
        }
        String f = fv(file);
        // An OCCURS DEPENDING ON record is re-measured at each use: the FROM move fills its current length, then the
        // WRITE takes the length after the move (GnuCOBOL computes both).
        String recVar = field(rec);
        if (s.n("from") != null) o.append(p).append("CobRt.move(").append(operand(s.n("from"))).append(", ").append(recVar).append(");\n");
        String r = t("_w");
        ioStatus(o, in, file, f + "." + (s.verb.equals("WRITE") ? "write" : "rewrite") + "(" + recVar + ".len" + adv + ")", r, false, s.block("INVALID") != null);
        branch(o, s, in, "INVALID", INVALID_KEY.formatted(r, r));
        branch(o, s, in, "NOT-INVALID", r + " < 10");
        branch(o, s, in, "EOP", "false");
        branch(o, s, in, "NOT-EOP", r + " < 10");
    }

    private void ioWithInvalid(StringBuilder o, Ir.Stmt s, int in, String call) {
        String r = t("_io");
        ioStatus(o, in, s.s("file"), call, r, false, s.block("INVALID") != null);
        branch(o, s, in, "INVALID", INVALID_KEY.formatted(r, r));
        branch(o, s, in, "NOT-INVALID", r + " < 10");
    }

    private void lowerString(StringBuilder o, Ir.Stmt s, int in) {
        List<String> srcs = new ArrayList<>(), dels = new ArrayList<>();
        for (Ir.Node piece : s.l("pieces")) {
            srcs.add(operand(piece.l));
            dels.add("size".equals(piece.r.k) ? "null" : operand(piece.r));
        }
        String ov = t("_ov");
        o.append(ind(in)).append("boolean ").append(ov).append(" = CobRt.stringInto(").append(field(s.n("into"))).append(", ")
                .append(s.n("pointer") == null ? "null" : field(s.n("pointer"))).append(", new Object[]{").append(String.join(", ", srcs)).append("}, new Object[]{")
                .append(String.join(", ", dels)).append("});\n");
        overflowBlocks(o, s, in, ov);
    }

    private void overflowBlocks(StringBuilder o, Ir.Stmt s, int in, String ov) {
        branch(o, s, in, "OVERFLOW", ov);
        branch(o, s, in, "NOT-OVERFLOW", "!" + ov);
    }

    private void lowerUnstring(StringBuilder o, Ir.Stmt s, int in) {
        List<String> dels = new ArrayList<>(), all = new ArrayList<>(), tg = new ArrayList<>(), di = new ArrayList<>(), ci = new ArrayList<>();
        for (Ir.Node d : s.l("delims")) {
            boolean isAll = "all".equals(d.k);
            dels.add(operand(isAll ? d.l : d));
            all.add(String.valueOf(isAll));
        }
        for (Ir.Node t : s.l("targets")) {
            tg.add(field(t.l));
            di.add(t.start == null ? "null" : field(t.start));
            ci.add(t.len == null ? "null" : field(t.len));
        }
        String ov = t("_ov");
        o.append(ind(in)).append("boolean ").append(ov).append(" = CobRt.unstring(").append(field(s.n("src"))).append(", new Object[]{").append(String.join(", ", dels))
                .append("}, new boolean[]{").append(String.join(", ", all)).append("}, new CobField[]{").append(String.join(", ", tg)).append("}, new CobField[]{")
                .append(String.join(", ", di)).append("}, new CobField[]{").append(String.join(", ", ci)).append("}, ")
                .append(s.n("pointer") == null ? "null" : field(s.n("pointer"))).append(", ").append(s.n("tallying") == null ? "null" : field(s.n("tallying"))).append(");\n");
        overflowBlocks(o, s, in, ov);
    }

    private void lowerInspect(StringBuilder o, Ir.Stmt s, String p) {
        String target = field(s.n("target"));
        for (Ir.Node c : s.l("clauses")) {
            String before = c.start == null ? "null" : operand(c.start);
            String after = c.len == null ? "null" : operand(c.len);
            switch (c.k) {
                case "tally" -> {
                    String counter = field(c.args.get(0));
                    o.append(p).append("CobRt.store(").append(counter).append(", ").append(counter).append(".dec().add(BigDecimal.valueOf(CobRt.inspectTally(").append(target)
                            .append(", \"").append(c.op).append("\", ").append(c.l == null ? "null" : operand(c.l)).append(", ").append(before).append(", ").append(after)
                            .append("))), false, false);\n");
                }
                case "replace" -> o.append(p).append("CobRt.inspectReplace(").append(target).append(", \"").append(c.op).append("\", ")
                        .append(c.l == null ? "null" : operand(c.l)).append(", ").append(operand(c.r)).append(", ").append(before).append(", ").append(after).append(");\n");
                case "convert" -> o.append(p).append("CobRt.inspectConvert(").append(target).append(", ").append(operand(c.l)).append(", ").append(operand(c.r))
                        .append(", ").append(before).append(", ").append(after).append(");\n");
                default -> throw new Abstain("INSPECT clause " + c.k);
            }
        }
    }

    private void lowerInitialize(StringBuilder o, Ir.Node d, String p) {
        if (d.id == null) throw new Abstain("INITIALIZE target unresolved");
        if (d.start != null) throw new Abstain("INITIALIZE of a reference modification");
        Ir.Field target = byId.get(d.id);
        String subs = d.args == null ? "" : subscripts(d.args);
        int targetDims = target.dims == null ? 0 : target.dims.size();
        if (!"Group".equals(target.category)) {
            o.append(p).append("CobRt.initLeaf(").append(field(d)).append(");\n");
            return;
        }
        if (d.args == null && targetDims > 0) throw new Abstain("INITIALIZE of a whole table without subscripts");
        if (odoAbstain.containsKey(target.id)) throw new Abstain(odoAbstain.get(target.id));
        if (odoTable.containsKey(target.id)) {
            initializeOdo(o, d, target, p);
            return;
        }
        List<Ir.Field> leaves = new ArrayList<>();
        initLeaves(target, leaves);
        for (Ir.Field leaf : leaves) {
            int ld = leaf.dims == null ? 0 : leaf.dims.size();
            String fixed = subs;
            String expr;
            if (ld == targetDims) expr = fieldRef(leaf.id) + (ld > 0 ? ".at(" + fixed + ")" : "");
            else {
                String vars = loopVars(ld, targetDims);
                expr = fieldRef(leaf.id) + ".at(" + (fixed.isEmpty() ? "" : fixed + ", ") + vars + ")";
            }
            String stmt = "CobRt.initLeaf(" + expr + ");";
            o.append(p).append(ld > targetDims ? loops(leaf.dims, targetDims, stmt) : stmt).append("\n");
        }
    }

    /**
     * INITIALIZE of a group holding an OCCURS DEPENDING ON table, as GnuCOBOL 3.x generates it. A group whose members
     * all initialize to one byte is filled at its current length; otherwise each member in turn: a uniform table is
     * filled at its maximum, another table has its first occurrence initialized and copied over occurrences 2..n (n the
     * ODO object's value at that moment, after earlier members were initialized), FILLER and REDEFINES are skipped.
     */
    private void initializeOdo(StringBuilder o, Ir.Node d, Ir.Field target, String p) {
        Character u = uniformChar(target);
        if (u != null) {
            o.append(p).append(field(d)).append(".fill((byte) ").append((int) u).append(");\n");
            return;
        }
        initCompound(o, target, p);
    }

    private void initCompound(StringBuilder o, Ir.Field g, String p) {
        for (Ir.Field c : children.getOrDefault(g.id, List.of())) {
            if (c.lvl() == 88 || (c.redefines != null && !c.redefines.isEmpty()) || "FILLER".equals(c.field)) continue;
            if (odoAbstain.containsKey(c.id)) throw new Abstain(odoAbstain.get(c.id));
            boolean table = c.occurs != null && !c.occurs.isEmpty();
            boolean group = children.getOrDefault(c.id, List.of()).stream().anyMatch(x -> x.lvl() != 88);
            Character u = uniformChar(c);
            if (table) {
                int max = Integer.parseInt(c.occurs);
                if (u != null) {
                    o.append(p).append("CobRt.fillBytes(").append(firstOccurrence(c)).append(", ").append(c.length * max).append(", (byte) ").append((int) u).append(");\n");
                    continue;
                }
                if (c.dependingOnId != null && !group)
                    throw new Abstain("INITIALIZE of the elementary OCCURS DEPENDING ON table " + c.field + " (GnuCOBOL sizes its first occurrence as the whole table)");
                if (group) initCompound(o, c, p);
                else o.append(p).append("CobRt.initLeaf(").append(firstOccurrence(c)).append(");\n");
                String count = c.dependingOnId != null ? field(refTo(c.dependingOnId)) + ".intValue()" : String.valueOf(max);
                o.append(p).append("CobRt.initTable(").append(firstOccurrence(c)).append(", ").append(count).append(", ").append(max).append(");\n");
                continue;
            }
            if (group) {
                if (u != null) {
                    String view = odoTable.containsKey(c.id) ? odoView(fieldRef(c.id), odoTable.get(c.id)) : firstOccurrence(c);
                    o.append(p).append(view).append(".fill((byte) ").append((int) u).append(");\n");
                } else initCompound(o, c, p);
                continue;
            }
            o.append(p).append("CobRt.initLeaf(").append(firstOccurrence(c)).append(");\n");
        }
    }

    /** The item at subscript 1 of every table it belongs to (INITIALIZE works on first occurrences, then copies). */
    private String firstOccurrence(Ir.Field f) {
        int n = f.dims == null ? 0 : f.dims.size();
        return fieldRef(f.id) + (n == 0 ? "" : ".at(" + String.join(", ", java.util.Collections.nCopies(n, "1")) + ")");
    }

    /** The byte every member of {@code f} initializes to, when they all agree (GnuCOBOL's uniform INITIALIZE), else null. */
    private Character uniformChar(Ir.Field f) {
        List<Ir.Field> kids = children.getOrDefault(f.id, List.of()).stream().filter(x -> x.lvl() != 88).toList();
        if (kids.isEmpty()) {
            return switch (f.category) {
                case "Alphanumeric", "Alphabetic" -> ' ';
                case "Numeric" -> "DISPLAY".equals(f.usage) && !f.signSeparate ? Character.valueOf('0')
                        : "COMP".equals(f.usage) || "COMP-5".equals(f.usage) ? Character.valueOf((char) 0) : null;
                default -> null;
            };
        }
        Character c = null;
        for (Ir.Field k : kids) {
            if ("FILLER".equals(k.field) || (k.redefines != null && !k.redefines.isEmpty())) return null;
            Character x = uniformChar(k);
            if (x == null || (c != null && !c.equals(x))) return null;
            c = x;
        }
        return c;
    }

    /**
     * INITIALIZE with options (COBOL 2002 / IBM): each elementary item (FILLER only WITH FILLER, nothing under a
     * REDEFINES) is set to its VALUE when ALL TO VALUE applies and it has one, else to the REPLACING value of its
     * category, else to its default when THEN TO DEFAULT is given or neither TO VALUE nor REPLACING is; otherwise left
     * alone. Abstained where GnuCOBOL differs from that: a category TO VALUE (GnuCOBOL applies every VALUE), a group
     * VALUE, and a table of groups whose entry is written in part (GnuCOBOL initializes the first occurrence and
     * copies its bytes, unwritten ones included, over the others).
     */
    private void lowerInitializeWith(StringBuilder o, Ir.Stmt s, Ir.Node d, String p) {
        if (d.id == null) throw new Abstain("INITIALIZE target unresolved");
        if (d.start != null) throw new Abstain("INITIALIZE of a reference modification");
        boolean filler = "true".equals(s.s("filler"));
        String toValue = s.s("toValue");
        if (toValue != null && !"ALL".equals(toValue))
            throw new Abstain("INITIALIZE " + toValue + " TO VALUE (GnuCOBOL applies every VALUE whatever its category)");
        Map<String, Ir.Node> repl = new LinkedHashMap<>();
        for (Ir.Node r : s.l("replacing")) repl.put(r.v, r.l);
        boolean toDefault = "true".equals(s.s("toDefault")) || (toValue == null && repl.isEmpty());
        Ir.Field target = byId.get(d.id);
        if (odoTable.containsKey(target.id) || odoAbstain.containsKey(target.id))
            throw new Abstain("INITIALIZE with options of a group holding OCCURS DEPENDING ON");
        java.util.function.Function<Ir.Field, String> action = leaf -> {
            String cat = switch (leaf.category) {
                case "Alphanumeric" -> "ALPHANUMERIC";
                case "Alphabetic" -> "ALPHABETIC";
                case "Numeric" -> "NUMERIC";
                case "NumericEdited" -> "NUMERIC-EDITED";
                case "AlphanumericEdited" -> "ALPHANUMERIC-EDITED";
                default -> null;
            };
            if (cat == null) return null;
            if (toValue != null && leaf.values != null && !leaf.values.isEmpty()) {
                String lit = valueLiteral(leaf.values.get(0));
                return "CobRt.move(" + (leaf.valueAll ? "new AllLit(" + lit + ")" : lit) + ", %s);";
            }
            if (repl.containsKey(cat)) return "CobRt.move(" + operand(repl.get(cat)) + ", %s);";
            if (toDefault) return "CobRt.initLeaf(%s);";
            return null;
        };
        if (!"Group".equals(target.category)) {
            String a = action.apply(target);
            if (a != null) o.append(p).append(String.format(a, field(d))).append("\n");
            return;
        }
        int targetDims = target.dims == null ? 0 : target.dims.size();
        if (d.args == null && targetDims > 0) throw new Abstain("INITIALIZE of a whole table without subscripts");
        String subs = d.args == null ? "" : subscripts(d.args);
        if (toValue != null)
            for (Ir.Field g : descendants(target))
                if (g.values != null && !g.values.isEmpty() && children.getOrDefault(g.id, List.of()).stream().anyMatch(x -> x.lvl() != 88))
                    throw new Abstain("INITIALIZE TO VALUE of a group with a VALUE clause");
        for (Ir.Field t : descendants(target)) {
            if (t == target || t.occurs == null || t.occurs.isEmpty() || children.getOrDefault(t.id, List.of()).stream().noneMatch(x -> x.lvl() != 88)) continue;
            List<Ir.Field> all = new ArrayList<>();
            initLeaves(t, all, true);
            long written = all.stream().filter(leaf -> (filler || !"FILLER".equals(leaf.field)) && action.apply(leaf) != null).count();
            if (written > 0 && written < all.size())
                throw new Abstain("INITIALIZE leaves part of the table entry " + t.field + " unwritten (GnuCOBOL copies the first occurrence over the others)");
        }
        List<Ir.Field> leaves = new ArrayList<>();
        initLeaves(target, leaves, filler);
        for (Ir.Field leaf : leaves) {
            String a = action.apply(leaf);
            if (a == null) continue;
            int ld = leaf.dims == null ? 0 : leaf.dims.size();
            String expr;
            if (ld == targetDims) expr = fieldRef(leaf.id) + (ld > 0 ? ".at(" + subs + ")" : "");
            else expr = fieldRef(leaf.id) + ".at(" + (subs.isEmpty() ? "" : subs + ", ") + loopVars(ld, targetDims) + ")";
            String stmt = String.format(a, expr);
            o.append(p).append(ld > targetDims ? loops(leaf.dims, targetDims, stmt) : stmt).append("\n");
        }
    }

    private void initLeaves(Ir.Field g, List<Ir.Field> out) {
        initLeaves(g, out, false);
    }

    /** Elementary items under {@code g} in order, skipping condition-names and REDEFINES; FILLER only when asked. */
    private void initLeaves(Ir.Field g, List<Ir.Field> out, boolean filler) {
        for (Ir.Field c : children.getOrDefault(g.id, List.of())) {
            if (c.lvl() == 88 || (c.redefines != null && !c.redefines.isEmpty())) continue;
            boolean group = children.containsKey(c.id) && children.get(c.id).stream().anyMatch(x -> x.lvl() != 88);
            if (group) initLeaves(c, out, filler);
            else if (filler || !"FILLER".equals(c.field)) out.add(c);
        }
    }

    private void lowerSet(StringBuilder o, Ir.Stmt s, String p) {
        String mode = s.s("mode");
        for (Ir.Node d : s.l("dst")) {
            switch (mode) {
                case "TRUE" -> {
                    if (!"c88".equals(d.k)) throw new Abstain("SET TO TRUE on a non-condition");
                    Ir.Field c = byId.get(d.id);
                    Ir.Value v = c.values.get(0);
                    o.append(p).append("CobRt.move(").append(valueLiteral(v)).append(", ").append(parentOf88(d)).append(");\n");
                }
                case "FALSE" -> {
                    // SET condition-name TO FALSE moves the WHEN SET TO FALSE literal to the conditional variable.
                    if (!"c88".equals(d.k)) throw new Abstain("SET TO FALSE on a non-condition");
                    Ir.Field c = byId.get(d.id);
                    if (c.falseValue == null) throw new Abstain("SET " + c.field + " TO FALSE: the condition-name has no WHEN SET TO FALSE value");
                    o.append(p).append("CobRt.move(").append(valueLiteral(c.falseValue)).append(", ").append(parentOf88(d)).append(");\n");
                }
                case "TO" -> {
                    Ir.Field f = byId.get(d.id);
                    if ("Index".equals(f.category) || "Numeric".equals(f.category))
                        o.append(p).append("CobRt.store(").append(field(d)).append(", ").append(num(s.n("value"))).append(", false, false);\n");
                    else o.append(p).append("CobRt.move(").append(operand(s.n("value"))).append(", ").append(field(d)).append(");\n");
                }
                case "UP", "DOWN" -> o.append(p).append("CobRt.store(").append(field(d)).append(", CobRt.").append(mode.equals("UP") ? "add" : "sub").append("(").append(field(d))
                        .append(".dec(), ").append(num(s.n("value"))).append("), false, false);\n");
                default -> throw new Abstain("SET " + mode + " is not lowered");
            }
        }
    }

    private void lowerCall(StringBuilder o, Ir.Stmt s, int in) {
        String p = ind(in);
        Ir.Node target = s.n("target");
        String name = "str".equals(target.k) ? jstr(target.v) : "ref".equals(target.k) ? field(target) + ".str()" : null;
        if (name == null) throw new Abstain("CALL target form " + target.k);
        Ir.Node ret = s.n("returning");
        List<String> args = new ArrayList<>();
        for (Ir.Node a : s.l("args")) {
            if ("omitted".equals(a.k)) { args.add("null"); continue; }
            boolean byRef = "REFERENCE".equals(a.op) && ("ref".equals(a.l.k));
            args.add(byRef ? field(a.l) : "CobRt.byContent(" + operand(a.l) + ")");
        }
        String call = "CobRt.call(" + name + ", new CobField[]{" + String.join(", ", args) + "});";
        // CALL ... RETURNING x: x receives the called program's return value (its RETURN-CODE); the caller's
        // RETURN-CODE is left as it was, as GnuCOBOL does.
        if (ret != null) call = "CobRt.callReturning(" + name + ", new CobField[]{" + String.join(", ", args) + "}, " + field(ret) + ");";
        List<Ir.Stmt> exc = s.block("EXCEPTION"), ok = s.block("NOT-EXCEPTION");
        if (exc == null && ok == null) { o.append(p).append(call).append("\n"); return; }
        String flag = t("_cx");
        o.append(p).append("boolean ").append(flag).append(" = false;\n");
        o.append(p).append("try { ").append(call).append(" } catch (CobRt.CallNotFound ").append(t("_e")).append(") { ").append(flag).append(" = true; }\n");
        branch(o, s, in, "EXCEPTION", flag);
        branch(o, s, in, "NOT-EXCEPTION", "!" + flag);
    }

    // ------------------------------------------------------------------ expressions

    private String parentOf88(Ir.Node c88) {
        Ir.Field c = byId.get(c88.id);
        if (c.parentId == null) throw new Abstain("level-88 without a parent");
        Ir.Node parent = new Ir.Node();
        parent.k = "ref";
        parent.id = c.parentId;
        parent.args = c88.args;
        return field(parent);
    }

    private String subscripts(List<Ir.Node> args) {
        List<String> s = new ArrayList<>();
        for (Ir.Node a : args) s.add(intOf(a));
        return String.join(", ", s);
    }

    private String intOf(Ir.Node n) {
        if ("num".equals(n.k)) {
            try { return String.valueOf(Integer.parseInt(n.v.startsWith("+") ? n.v.substring(1) : n.v)); }
            catch (NumberFormatException e) { return "CobRt.toInt(" + num(n) + ")"; }
        }
        if ("ref".equals(n.k)) return field(n) + ".intValue()";
        return "CobRt.toInt(" + num(n) + ")";
    }

    /** A field reference expression (CobField). */
    private String field(Ir.Node n) {
        if (n == null) throw new Abstain("missing operand");
        if ("c88".equals(n.k)) throw new Abstain("condition-name used as data");
        if (!"ref".equals(n.k)) throw new Abstain("expected a data item, found " + n.k);
        Ir.Field f = byId.get(n.id);
        if (f == null) throw new Abstain("unknown data item " + n.v);
        String why = odoAbstain.get(f.id);
        if (why != null) throw new Abstain(why);
        String e = fieldRef(n.id);
        if (n.args != null && !n.args.isEmpty()) {
            if (f.dims == null || f.dims.isEmpty()) throw new Abstain("subscript on a non-table item " + f.field);
            e += ".at(" + subscripts(n.args) + ")";
        } else if (f.dims != null && !f.dims.isEmpty()) {
            throw new Abstain("table item " + f.field + " referenced without subscripts");
        }
        Ir.Field odo = odoTable.get(f.id);
        if (odo != null && n.start != null) {
            // GnuCOBOL 3.3 gives (start:) of an ODO group the whole current length from the start position, past the
            // group's end: the oracle is defective there, so the transpiler abstains. (start:length) is not checked
            // against the current length, so its bounds are those of the declared maximum.
            if (n.len == null) throw new Abstain("reference modification (start:) of the OCCURS DEPENDING ON group " + f.field + " (GnuCOBOL takes the whole current length from the start)");
            return e + ".ref(" + intOf(n.start) + ", " + intOf(n.len) + ")";
        }
        if (odo != null) e = odoView(e, odo);
        if (n.start != null) e += ".ref(" + intOf(n.start) + ", " + (n.len == null ? "-1" : intOf(n.len)) + ")";
        return e;
    }

    /** An operand expression (CobField | String | NumLit | BigDecimal | Fig | AllLit). */
    private String operand(Ir.Node n) {
        if (n == null) throw new Abstain("missing operand");
        return switch (n.k) {
            case "ref" -> field(n);
            case "num" -> num(n.v);
            case "str" -> jstr(n.v);
            case "fig" -> {
                String f = figurative(n.v);
                if (f == null) throw new Abstain("figurative " + n.v);
                yield f;
            }
            case "all" -> {
                Ir.Node inner = n.l;
                yield "new AllLit(" + ("fig".equals(inner.k) ? figurative(inner.v) : operand(inner)) + ")";
            }
            case "fn" -> function(n);
            case "len" -> "new NumLit(BigDecimal.valueOf(" + field(n.l) + ".len))";
            case "bin", "neg" -> "new NumLit(" + num(n) + ")";
            case "c88" -> throw new Abstain("condition-name used as an operand");
            default -> throw new Abstain("operand kind " + n.k);
        };
    }

    private String function(Ir.Node n) {
        String a0 = n.args != null && !n.args.isEmpty() ? null : "";
        String call = switch (n.v) {
            case "CURRENT-DATE" -> "CobFn.currentDate()";
            case "MOD" -> "CobFn.mod(" + num(n.args.get(0)) + ", " + num(n.args.get(1)) + ")";
            case "REM" -> "CobFn.rem(" + num(n.args.get(0)) + ", " + num(n.args.get(1)) + ")";
            case "INTEGER" -> "CobFn.integer(" + num(n.args.get(0)) + ")";
            case "INTEGER-PART" -> "CobFn.integerPart(" + num(n.args.get(0)) + ")";
            case "ABS" -> "CobFn.abs(" + num(n.args.get(0)) + ")";
            case "MAX", "MIN" -> {
                List<String> a = new ArrayList<>();
                for (Ir.Node x : n.args) a.add(num(x));
                yield "CobFn." + n.v.toLowerCase() + "(" + String.join(", ", a) + ")";
            }
            case "NUMVAL" -> "CobFn.numval(" + operand(n.args.get(0)) + ")";
            case "NUMVAL-C" -> "CobFn.numvalC(" + operand(n.args.get(0)) + (n.args.size() > 1 ? ", " + operand(n.args.get(1)) : "") + ")";
            case "TEST-NUMVAL" -> "CobFn.testNumval(" + operand(n.args.get(0)) + ", false)";
            case "TEST-NUMVAL-C" -> {
                // GnuCOBOL 3.3 mis-scans a currency string argument (TEST-NUMVAL-C ('EUR1.5' 'EUR') is 2): abstain on it.
                if (n.args.size() > 1) throw new Abstain("FUNCTION TEST-NUMVAL-C with a currency argument");
                yield "CobFn.testNumval(" + operand(n.args.get(0)) + ", true)";
            }
            case "ORD" -> "CobFn.ord(" + operand(n.args.get(0)) + ")";
            case "CHAR" -> "CobFn.chr(" + num(n.args.get(0)) + ")";
            case "ORD-MAX", "ORD-MIN", "SUM", "MEAN", "MEDIAN", "RANGE" -> {
                List<String> a = new ArrayList<>();
                for (Ir.Node x : n.args) a.add(numericArg(x, n.v));
                yield "CobFn." + camel(n.v.toLowerCase()) + "(" + String.join(", ", a) + ")";
            }
            case "SQRT" -> "CobFn.sqrt(" + num(n.args.get(0)) + ")";
            case "FACTORIAL" -> "CobFn.factorial(" + num(n.args.get(0)) + ")";
            case "INTEGER-OF-DAY" -> "CobFn.integerOfDay(" + num(n.args.get(0)) + ")";
            case "DAY-OF-INTEGER" -> "CobFn.dayOfInteger(" + num(n.args.get(0)) + ")";
            case "TEST-DATE-YYYYMMDD" -> "CobFn.testDateYyyymmdd(" + num(n.args.get(0)) + ")";
            case "YEAR-TO-YYYY", "DATE-TO-YYYYMMDD" -> "CobFn." + camel(n.v.toLowerCase()) + "(" + num(n.args.get(0)) + ", "
                    + (n.args.size() > 1 ? num(n.args.get(1)) : "null") + ", " + (n.args.size() > 2 ? num(n.args.get(2)) : "null") + ")";
            case "WHEN-COMPILED" -> "CobFn.whenCompiled(" + jstr(java.time.LocalDateTime.now().format(java.time.format.DateTimeFormatter.ofPattern("yyyyMMddHHmmss")) + "0000000") + ")";
            case "LENGTH" -> "CobFn.length(" + operand(n.args.get(0)) + ")";
            case "INTEGER-OF-DATE" -> "CobFn.integerOfDate(" + num(n.args.get(0)) + ")";
            case "DATE-OF-INTEGER" -> "CobFn.dateOfInteger(" + num(n.args.get(0)) + ")";
            case "UPPER-CASE" -> "CobFn.upperCase(" + operand(n.args.get(0)) + ")";
            case "LOWER-CASE" -> "CobFn.lowerCase(" + operand(n.args.get(0)) + ")";
            case "TRIM" -> "CobFn.trim(" + operand(n.args.get(0)) + ")";
            case "REVERSE" -> "CobFn.reverse(" + operand(n.args.get(0)) + ")";
            default -> throw new Abstain("FUNCTION " + n.v + " is not lowered");
        };
        if (a0 == null && n.args.isEmpty() && !"WHEN-COMPILED".equals(n.v)) throw new Abstain("FUNCTION " + n.v + " needs arguments");
        if (n.start != null) return "CobRt.substr(" + call + ", " + intOf(n.start) + ", " + (n.len == null ? "-1" : intOf(n.len)) + ")";
        return call;
    }

    /** A numeric argument of a statistical function; an alphanumeric one (compared as text by GnuCOBOL) is not lowered. */
    private String numericArg(Ir.Node x, String fn) {
        if ("ref".equals(x.k)) {
            Ir.Field f = byId.get(x.id);
            if (f != null && !"Numeric".equals(f.category)) throw new Abstain("FUNCTION " + fn + " with a non-numeric argument " + f.field);
        }
        if ("str".equals(x.k)) throw new Abstain("FUNCTION " + fn + " with an alphanumeric argument");
        return num(x);
    }

    /** True for an intrinsic function whose value is numeric. */
    private static boolean numericFn(Ir.Node n) {
        return n != null && "fn".equals(n.k) && NUMERIC_FNS.contains(n.v) && n.start == null;
    }

    /** A numeric expression (BigDecimal). */
    private String num(Ir.Node n) {
        if (n == null) throw new Abstain("missing numeric operand");
        return switch (n.k) {
            case "ref" -> field(n) + ".dec()";
            case "num" -> num(n.v) + ".v";
            case "bin" -> {
                String e = switch (n.op) {
                    case "+" -> "CobRt.add(" + num(n.l) + ", " + num(n.r) + ")";
                    case "-" -> "CobRt.sub(" + num(n.l) + ", " + num(n.r) + ")";
                    case "*" -> "CobRt.mul(" + num(n.l) + ", " + num(n.r) + ")";
                    case "/" -> "CobRt.div(" + num(n.l) + ", " + num(n.r) + ")";
                    case "**" -> "CobRt.pow(" + num(n.l) + ", " + num(n.r) + ")";
                    default -> throw new Abstain("operator " + n.op);
                };
                yield osvsWrap(n, e);
            }
            case "neg" -> osvsWrap(n, num(n.l) + ".negate()"); // cobc: 0 - x
            case "fig" -> {
                if (!"ZEROS".equals(n.v)) throw new Abstain("figurative " + n.v + " in arithmetic");
                yield "BigDecimal.ZERO";
            }
            case "fn" -> NUMERIC_FNS.contains(n.v) && n.start == null ? function(n) : "CobRt.dec(" + function(n) + ")";
            case "len" -> "BigDecimal.valueOf(" + field(n.l) + ".len)";
            default -> throw new Abstain("numeric operand kind " + n.k);
        };
    }

    /**
     * arithmetic-osvs (GnuCOBOL -std=ibm): an intermediate cobc aligns is truncated to a[0] decimals; with a[1] (its own
     * scale, below a[0]) GnuCOBOL's defect applies, which the runtime allows only where it changes nothing.
     */
    private String osvsWrap(Ir.Node n, String e) {
        int[] a = osvsTrunc.get(n);
        if (a == null) return e;
        return a.length == 1 ? "CobRt.align(" + e + ", " + a[0] + ")" : "CobRt.alignUp(" + e + ", " + a[1] + ", " + a[0] + ", " + jstr(currentStep) + ")";
    }

    private boolean isArithmetic(Ir.Node n) {
        return "bin".equals(n.k) || "neg".equals(n.k) || ("fn".equals(n.k) && NUMERIC_FNS.contains(n.v) && n.start == null);
    }

    // ------------------------------------------------------------------ typed rule slices (extracting only)

    /**
     * A statement's whole condition. Extracting, an equivalent condition runs as its generated predicate on a snapshot of
     * the items it reads; an alphanumeric equality keeps the faithful test under an EBCDIC collating sequence (whose
     * runtime compare treats two bytes as equal). Anything else stays faithful and is recorded as SKIPPED.
     */
    private String topCond(Ir.Stmt s, String role, Ir.Node n) {
        String faithful = cond(n);
        if (ruleSet == null) return faithful;
        String site = role == null ? s.stepId : s.stepId + "#" + role;
        RulePlanner planner = planner();
        RuleSet.Rule rule;
        try {
            rule = ruleSet.intern("predicate", planner.predicate(n), planner, n, null);
        } catch (RulePlanner.Skip e) {
            ruleSet.skip(site, "predicate", e.getMessage(), paragraphName(), s.line, s.verb);
            return faithful;
        }
        ruleSet.use(site, rule, paragraphName(), s.line, s.verb, null);
        String call = "_rules." + rule.accessor + "().evaluate(" + ruleInput(rule, planner) + ")";
        return rule.requiresLatin1 ? "(RecordCharset.isEbcdic() ? (" + faithful + ") : " + call + ")" : call;
    }

    /**
     * The value of an arithmetic statement as its generated calculation, or null (SKIPPED: the faithful value stays). The
     * calculation returns the exact intermediate; storing it (ROUNDED, truncation, SIZE ERROR) stays with CobRt.store.
     */
    private String calc(Ir.Stmt s, Ir.Node expr, String verbName, List<Ir.Node> receivers) {
        if (ruleSet == null) return null;
        RulePlanner planner = planner();
        RuleSet.Rule rule;
        List<String> names = new ArrayList<>();
        try {
            if (receivers.isEmpty()) throw new RulePlanner.Skip("no receiver");
            for (Ir.Node d : receivers) {
                Ir.Field f = d.id == null ? null : byId.get(d.id);
                if (f == null || d.start != null || !("Numeric".equals(f.category) || "NumericEdited".equals(f.category)))
                    throw new RulePlanner.Skip("receiver " + (f == null ? "?" : f.field) + " is not numeric: its digits would follow the value's scale");
                names.add(f.field);
            }
            if (touchesOsvs(expr)) throw new RulePlanner.Skip("arithmetic-osvs truncates an intermediate result (dialect-configured runtime behaviour)");
            RulePlanner.Num v = planner.calculation(expr);
            rule = ruleSet.intern("calculation", v, planner, expr, verbName + camelUpper(names.get(0)));
        } catch (RulePlanner.Skip e) {
            ruleSet.skip(s.stepId, "calculation", e.getMessage(), paragraphName(), s.line, s.verb);
            return null;
        }
        ruleSet.use(s.stepId, rule, paragraphName(), s.line, s.verb, names);
        return "_rules." + rule.accessor + "().evaluate(" + ruleInput(rule, planner) + ").value()";
    }

    private void skipCalc(Ir.Stmt s, String reason) {
        ruleSet.skip(s.stepId, "calculation", reason, paragraphName(), s.line, s.verb);
    }

    private RulePlanner planner() {
        return new RulePlanner(new RulePlanner.Facts() {
            public Ir.Field field(int id) { return byId.get(id); }
            public boolean odo(int id) { return odoTable.containsKey(id) || odoAbstain.containsKey(id); }
        });
    }

    /** The rule's input record over snapshots of this site's items, taken where the faithful lowering reads them. */
    private String ruleInput(RuleSet.Rule rule, RulePlanner site) {
        List<String> values = new ArrayList<>();
        for (int i = 0; i < site.inputs.size(); i++)
            // Text is read through bytes() as the faithful compare reads it (same failure, if any, on an unbound item).
            values.add(site.isNumericInput(i) ? field(refTo(site.inputs.get(i).id)) + ".dec()"
                    : "new String(" + field(refTo(site.inputs.get(i).id)) + ".bytes(), java.nio.charset.StandardCharsets.ISO_8859_1)");
        return "new " + ruleSet.pkg + "." + rule.inputType() + "(" + String.join(", ", values) + ")";
    }

    private boolean touchesOsvs(Ir.Node n) {
        if (n == null) return false;
        if (osvsTrunc.containsKey(n)) return true;
        if (touchesOsvs(n.l) || touchesOsvs(n.r)) return true;
        if (n.args != null) for (Ir.Node a : n.args) if (touchesOsvs(a)) return true;
        return false;
    }

    private String paragraphName() {
        return paras.isEmpty() ? null : paras.get(currentPara).name;
    }

    private int[] ruleMark() {
        return ruleSet == null ? null : new int[]{ruleSet.uses.size(), ruleSet.skips.size()};
    }

    private void ruleReset(int[] mark) {
        if (mark == null) return;
        ruleSet.uses.subList(mark[0], ruleSet.uses.size()).clear();
        ruleSet.skips.subList(mark[1], ruleSet.skips.size()).clear();
    }

    /** A boolean condition expression. */
    private String cond(Ir.Node n) {
        if (n == null) throw new Abstain("missing condition");
        return switch (n.k) {
            case "rel" -> {
                String op = switch (n.op) {
                    case "eq" -> "==";
                    case "ne" -> "!=";
                    case "lt" -> "<";
                    case "le" -> "<=";
                    case "gt" -> ">";
                    case "ge" -> ">=";
                    default -> throw new Abstain("relational operator " + n.op);
                };
                if ("condval".equals(n.l.k)) yield "((" + cond(n.l.l) + ") " + op + " (" + cond(n.r.l) + "))";
                if (isArithmetic(n.l) || isArithmetic(n.r)) yield num(n.l) + ".compareTo(" + num(n.r) + ") " + op + " 0";
                yield "CobRt.compare(" + operand(n.l) + ", " + operand(n.r) + ") " + op + " 0";
            }
            case "and" -> "(" + cond(n.l) + " && " + cond(n.r) + ")";
            case "or" -> "(" + cond(n.l) + " || " + cond(n.r) + ")";
            case "not" -> "!(" + cond(n.l) + ")";
            case "c88" -> "CobRt.is88(" + parentOf88(n) + ", " + c88Values(n.id) + ")";
            case "class" -> {
                String t = switch (n.v) {
                    case "NUMERIC" -> "CobRt.isNumeric(" + operand(n.l) + ")";
                    default -> "CobRt.isAlphabetic(" + operand(n.l) + ", \"" + n.v + "\")";
                };
                yield "not".equals(n.op) ? "!" + t : t;
            }
            case "sign" -> {
                String t = num(n.l) + ".signum() " + switch (n.v) {
                    case "POSITIVE" -> "> 0";
                    case "NEGATIVE" -> "< 0";
                    default -> "== 0";
                };
                yield "not".equals(n.op) ? "!(" + t + ")" : "(" + t + ")";
            }
            case "bool" -> "TRUE".equals(n.v) ? "true" : "false";
            case "any" -> "true";
            default -> throw new Abstain("condition kind " + n.k);
        };
    }

    private final Map<Integer, String> c88Consts = new HashMap<>();

    private String c88Values(int id) {
        return c88Consts.computeIfAbsent(id, k -> {
            Ir.Field c = byId.get(id);
            String name = "C88_" + id;
            StringBuilder sb = new StringBuilder("    private static final Object[][] ").append(name).append(" = {");
            List<String> vals = new ArrayList<>();
            for (Ir.Value v : c.values) {
                String lo = valueLiteral(v);
                String hi = v.hi == null ? "null" : valueLiteral(hiValue(v));
                vals.add("{" + lo + ", " + hi + "}");
            }
            sb.append(String.join(", ", vals)).append("}; // ").append(c.field).append("\n");
            statics.append(sb);
            return name;
        });
    }

    private static Ir.Value hiValue(Ir.Value v) {
        Ir.Value h = new Ir.Value();
        h.v = v.hi;
        h.q = Boolean.TRUE.equals(v.hq);
        return h;
    }
}
