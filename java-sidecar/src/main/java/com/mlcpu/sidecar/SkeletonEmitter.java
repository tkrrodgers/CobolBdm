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
            "NUMVAL-C", "LENGTH", "INTEGER-OF-DATE", "DATE-OF-INTEGER");

    // per-emission state
    private Ir ir;
    private final Map<Integer, Ir.Field> byId = new HashMap<>();
    private final Map<Integer, List<Ir.Field>> children = new HashMap<>();
    private final Map<Integer, String> jname = new HashMap<>();
    private final Map<Integer, String> areaOf = new LinkedHashMap<>();
    private final Map<String, Integer> areaSize = new LinkedHashMap<>();
    private final Set<String> linkageAreas = new HashSet<>();
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

    public Emission emit(Ir in) {
        reset();
        ir = in;
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
        paras.addAll(ir.semantics.paragraphs);
        for (int i = 0; i < paras.size(); i++) paraIndex.putIfAbsent(paras.get(i).name.toUpperCase(), i);

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
        emitDeclarations(body);
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
        return em;
    }

    private void reset() {
        byId.clear(); children.clear(); jname.clear(); areaOf.clear(); areaSize.clear(); linkageAreas.clear(); fileVar.clear();
        files.clear(); paraIndex.clear(); paras.clear(); numConsts.clear(); statics.setLength(0); tmp = 0; lowered = 0;
        abstainedCount = 0; abstainedList.clear(); sentenceLabel = null; loopLabels.clear();
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
        for (Ir.Field f : ir.dictionary) {
            if (f.lvl() == 88) continue;
            String base = "FILLER".equals(f.field) ? "filler" + f.id : modern.getOrDefault(f.field, camel(f.field));
            if (base.isEmpty() || !Character.isJavaIdentifierStart(base.charAt(0))) base = "f" + base;
            if (JAVA_KEYWORDS.contains(base)) base = base + "_";
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
            }
            areaOf.put(f.id, area);
        }
        for (Ir.Field f : ir.dictionary) if (!areaOf.containsKey(f.id)) areaOf.put(f.id, areaOf.get(f.recordId));
        for (Ir.File fd : files.values()) {
            String area = "aF_" + sanitize(fd.name);
            areaSize.putIfAbsent(area, Math.max(fd.maxRecord, 1));
            fileVar.put(fd.name, "file" + camelUpper(fd.name));
        }
    }

    static String camelUpper(String s) {
        String c = camel(s);
        return c.isEmpty() ? c : Character.toUpperCase(c.charAt(0)) + c.substring(1);
    }

    private boolean isReturnCode(Ir.Field f) {
        return f.parentId == null && f.line == 0 && "RETURN-CODE".equals(f.field);
    }

    private boolean isTally(Ir.Field f) {
        return f.parentId == null && f.line == 0 && "TALLY".equals(f.field);
    }

    private void emitDeclarations(StringBuilder b) {
        b.append("    // ---- storage areas\n");
        for (Map.Entry<String, Integer> e : areaSize.entrySet())
            b.append("    final Area ").append(e.getKey()).append(" = new Area(").append(e.getValue()).append(");").append(linkageAreas.contains(e.getKey()) ? " // LINKAGE" : "").append("\n");
        b.append("\n    // ---- data items\n");
        for (Ir.Field f : ir.dictionary) {
            if (f.lvl() == 88) continue;
            b.append("    final CobField ").append(jname.get(f.id)).append(" = ").append(fieldCtor(f)).append(";")
                    .append(" // ").append(f.level).append(" ").append(f.field).append(f.pic != null && !f.pic.isEmpty() ? " PIC " + f.pic : "")
                    .append(!"DISPLAY".equals(f.usage) ? " " + f.usage : "").append("\n");
        }
        b.append("\n");
    }

    private String fieldCtor(Ir.Field f) {
        if (isReturnCode(f)) return "CobRt.RETURN_CODE";
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
                keys = new StringBuilder("new CobField[]{" + jname.get(fd.recordKey));
                dups = new StringBuilder("new boolean[]{false");
                if (fd.alternateKeys != null) for (Ir.AltKey a : fd.alternateKeys) {
                    keys.append(", ").append(jname.get(a.key));
                    dups.append(", ").append(a.duplicates);
                }
                keys.append("}");
                dups.append("}");
            }
            boolean variable = "V".equals(fd.recordingMode) || (fd.minRecord > 0 && fd.minRecord != fd.maxRecord);
            b.append("    final CobFile ").append(fileVar.get(fd.name)).append(" = new CobFile(\"").append(fd.name).append("\", \"").append(fd.assign)
                    .append("\", CobFile.Org.").append(org).append(", CobFile.Acc.").append(acc).append(", aF_").append(sanitize(fd.name)).append(", ")
                    .append(fd.minRecord).append(", ").append(Math.max(fd.maxRecord, 1)).append(", ")
                    .append(fd.fileStatus != null ? jname.get(fd.fileStatus) : "null").append(", ").append(keys).append(", ").append(dups).append(", ")
                    .append(fd.recordDependingOn != null ? jname.get(fd.recordDependingOn) : "null").append(", ").append(variable).append(");\n");
        }
        b.append("\n");
    }

    private void emitInit(StringBuilder b) {
        b.append("    @Override\n    protected void _init() {\n");
        for (String a : areaSize.keySet()) b.append("        ").append(a).append(".fill(CobConfig.DEFAULT_BYTE);\n");
        for (Ir.Field f : ir.dictionary) {
            if (f.lvl() == 88 || f.values == null || f.values.isEmpty()) continue;
            if (!"WorkingStorage".equals(f.section) && !"LocalStorage".equals(f.section)) continue;
            if (underRedefines(f) || isReturnCode(f)) continue;
            String lit;
            try {
                lit = f.valueAll ? "new AllLit(" + valueLiteral(f.values.get(0)) + ")" : valueLiteral(f.values.get(0));
            } catch (Abstain e) {
                b.append("        // TODO(frontier: VALUE cause=TRANSPILER fix=transpiler) ").append(f.field).append(": ").append(e.getMessage()).append("\n");
                continue;
            }
            if (f.dims == null || f.dims.isEmpty()) {
                b.append("        CobRt.move(").append(lit).append(", ").append(jname.get(f.id)).append(");\n");
            } else {
                b.append("        ").append(loops(f.dims, 0, "CobRt.move(" + lit + ", " + jname.get(f.id) + ".at(" + loopVars(f.dims.size(), 0) + "));")).append("\n");
            }
        }
        b.append("    }\n\n");
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
        b.append("    @Override\n    protected void _dispatch(int pc) {\n        switch (pc) {\n");
        for (int i = 0; i < paras.size(); i++) b.append("            case ").append(i).append(" -> ").append(methodName(i)).append("();\n");
        b.append("            default -> throw new IllegalStateException(\"bad paragraph index \" + pc);\n        }\n    }\n\n");
        b.append("    @Override\n    protected int _count() { return ").append(paras.size()).append("; }\n\n");
        b.append("    @Override\n    protected Area[] _linkage() { return new Area[]{");
        List<String> using = ir.program.using == null ? List.of() : ir.program.using;
        List<String> lk = new ArrayList<>();
        for (String u : using)
            for (Ir.Field f : ir.dictionary)
                if (f.parentId == null && f.field.equals(u) && "Linkage".equals(f.section)) { lk.add(areaOf.get(f.id)); break; }
        b.append(String.join(", ", lk)).append("}; }\n\n");
        b.append("    @Override\n    public String programId() { return \"").append(ir.program.id).append("\"; }\n\n");
        b.append("    @Override\n    protected String[] _paragraphNames() { return new String[]{");
        for (int i = 0; i < paras.size(); i++) b.append(i > 0 ? ", " : "").append(jstr(paras.get(i).name.toUpperCase()));
        b.append("}; }\n\n");
        b.append("    @Override\n    protected java.util.Map<String, CobField> _fields() {\n        java.util.Map<String, CobField> m = new java.util.LinkedHashMap<>();\n");
        Map<String, Integer> count = new HashMap<>();
        for (Ir.Field f : ir.dictionary) if (f.lvl() != 88) count.merge(f.field, 1, Integer::sum);
        for (Ir.Field f : ir.dictionary) {
            if (f.lvl() == 88 || "FILLER".equals(f.field) || count.get(f.field) > 1 || (f.dims != null && !f.dims.isEmpty())) continue;
            if (children.containsKey(f.id) && children.get(f.id).stream().anyMatch(c -> c.lvl() != 88)) continue;
            b.append("        m.put(\"").append(esc(f.field)).append("\", ").append(jname.get(f.id)).append(");\n");
        }
        b.append("        return m;\n    }\n");
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

    private void emitParagraph(StringBuilder out, int i) {
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
        for (Ir.Stmt s : stmts) emitStmt(out, s, indent);
    }

    private static String ind(int n) {
        return "    ".repeat(n);
    }

    private void emitStmt(StringBuilder out, Ir.Stmt s, int indent) {
        String pad = ind(indent);
        out.append(pad).append("// [").append(s.stepId).append("] ").append(oneLine(s.text)).append("\n");
        if (s.frontier != null) { frontier(out, s, s.frontier, indent); return; }
        StringBuilder sb = new StringBuilder();
        List<String> savedLoops = new ArrayList<>(loopLabels);
        try {
            lower(sb, s, indent);
            out.append(sb);
            lowered++;
        } catch (Abstain e) {
            loopLabels.clear();
            loopLabels.addAll(savedLoops);
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
                o.append(p).append("if (").append(cond(s.n("cond"))).append(") {\n");
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
            case "PERFORM" -> lowerPerform(o, s, in);
            case "GO TO" -> lowerGoTo(o, s, p);
            case "DISPLAY" -> {
                List<String> items = new ArrayList<>();
                for (Ir.Node n : s.l("items")) items.add(operand(n));
                o.append(p).append("CobRt.display(").append("true".equals(s.s("noAdvancing"))).append(items.isEmpty() ? "" : ", ").append(String.join(", ", items)).append(");\n");
            }
            case "ACCEPT" -> o.append(p).append("CobRt.accept(").append(field(s.n("dst"))).append(", ").append(s.s("from") == null ? "null" : jstr(s.s("from"))).append(");\n");
            case "OPEN" -> {
                for (Ir.Node f : s.l("files")) o.append(p).append(fv(f.v)).append(".check(").append(fv(f.v)).append(".open(\"").append(f.op).append("\"), false);\n");
            }
            case "CLOSE" -> {
                for (Ir.Node f : s.l("files")) o.append(p).append(fv(f.v)).append(".check(").append(fv(f.v)).append(".close(), false);\n");
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
                for (Ir.Node d : s.l("dst")) lowerInitialize(o, d, p);
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
                else if (!sg || !dg) pairs.add(new String[]{jname.get(sc.id), jname.get(dc.id)});
            }
        }
    }

    private void lowerCompute(StringBuilder o, Ir.Stmt s, int in) {
        String p = ind(in);
        boolean phrase = s.block("SIZE-ERROR") != null || s.block("NOT-SIZE-ERROR") != null;
        String v = t("_v"), se = t("_se");
        o.append(p).append("boolean ").append(se).append(" = false;\n");
        o.append(p).append("try {\n");
        o.append(p).append("    BigDecimal ").append(v).append(" = ").append(num(s.n("expr"))).append(";\n");
        for (Ir.Node d : s.l("dst"))
            o.append(p).append("    ").append(se).append(" |= CobRt.store(").append(field(d)).append(", ").append(v).append(", ").append(Boolean.TRUE.equals(d.rounded)).append(", ").append(phrase).append(");\n");
        o.append(p).append("} catch (CobRt.SizeError ").append(t("_e")).append(") { ").append(se).append(" = true; }\n");
        sizeBlocks(o, s, in, se);
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
                String sum = sum(ops);
                String v = t("_s");
                o.append(q).append("BigDecimal ").append(v).append(" = ").append(sum).append(";\n");
                for (Ir.Node d : s.l("dst"))
                    o.append(q).append(se).append(" |= CobRt.store(").append(field(d)).append(", CobRt.").append(s.verb.equals("ADD") ? "add" : "sub").append("(")
                            .append(field(d)).append(".dec(), ").append(v).append("), ").append(Boolean.TRUE.equals(d.rounded)).append(", ").append(phrase).append(");\n");
            }
            case "ADD:GIVING" -> storeGiving(o, q, s, se, sum(ops), phrase);
            case "SUBTRACT:GIVING" -> storeGiving(o, q, s, se, "CobRt.sub(" + num(s.n("minuend")) + ", " + sum(ops) + ")", phrase);
            case "MULTIPLY:BY" -> {
                for (Ir.Node d : s.l("dst"))
                    o.append(q).append(se).append(" |= CobRt.store(").append(field(d)).append(", CobRt.mul(").append(num(ops.get(0))).append(", ").append(field(d)).append(".dec()), ")
                            .append(Boolean.TRUE.equals(d.rounded)).append(", ").append(phrase).append(");\n");
            }
            case "MULTIPLY:GIVING" -> storeGiving(o, q, s, se, "CobRt.mul(" + num(ops.get(0)) + ", " + num(s.n("by")) + ")", phrase);
            case "DIVIDE:INTO" -> {
                for (Ir.Node d : s.l("dst"))
                    o.append(q).append(se).append(" |= CobRt.store(").append(field(d)).append(", CobRt.div(").append(field(d)).append(".dec(), ").append(num(ops.get(0))).append("), ")
                            .append(Boolean.TRUE.equals(d.rounded)).append(", ").append(phrase).append(");\n");
            }
            case "DIVIDE:INTO-GIVING", "DIVIDE:BY-GIVING" -> {
                String dividend = form.equals("INTO-GIVING") ? num(s.n("by")) : num(ops.get(0));
                String divisor = form.equals("INTO-GIVING") ? num(ops.get(0)) : num(s.n("by"));
                String dv = t("_dd"), ds = t("_dv");
                o.append(q).append("BigDecimal ").append(dv).append(" = ").append(dividend).append(", ").append(ds).append(" = ").append(divisor).append(";\n");
                List<Ir.Node> giving = s.l("giving");
                for (Ir.Node g : giving)
                    o.append(q).append(se).append(" |= CobRt.store(").append(field(g)).append(", CobRt.div(").append(dv).append(", ").append(ds).append("), ")
                            .append(Boolean.TRUE.equals(g.rounded)).append(", ").append(phrase).append(");\n");
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

    private String sum(List<Ir.Node> ops) {
        if (ops.isEmpty()) throw new Abstain("arithmetic without operands");
        String acc = num(ops.get(0));
        for (int i = 1; i < ops.size(); i++) acc = "CobRt.add(" + acc + ", " + num(ops.get(i)) + ")";
        return acc;
    }

    private void lowerEvaluate(StringBuilder o, Ir.Stmt s, int in) {
        String p = ind(in);
        List<Ir.Stmt> whens = s.block("WHEN");
        if (whens == null || whens.isEmpty()) { o.append(p).append("// EVALUATE without WHEN\n"); return; }
        boolean first = true;
        for (Ir.Stmt w : whens) {
            boolean other = "true".equals(w.s("other"));
            String c = other ? null : cond(w.n("cond"));
            if (first) o.append(p).append("if (").append(c == null ? "true" : c).append(") { // [").append(w.stepId).append("] ").append(oneLine(w.text)).append("\n");
            else if (other) o.append(p).append("} else { // [").append(w.stepId).append("] WHEN OTHER\n");
            else o.append(p).append("} else if (").append(c).append(") { // [").append(w.stepId).append("] ").append(oneLine(w.text)).append("\n");
            emitBlock(o, w.block("TRUE"), in + 1);
            first = false;
            lowered++;
        }
        o.append(p).append("}\n");
    }

    private void lowerPerform(StringBuilder o, Ir.Stmt s, int in) {
        String p = ind(in);
        boolean inline = "true".equals(s.s("inline"));
        String body;
        StringBuilder bodyBuf = new StringBuilder();
        String label = t("L");
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
        String tick = ind(in + 1) + "CobRt.loopTick(\"" + esc(s.stepId) + "\");\n";
        Ir.Node until = s.n("until");
        Ir.Node times = s.n("times");
        Ir.Node var = s.n("var");
        boolean after = "AFTER".equals(s.s("test"));
        if (var != null && after) {
            // WITH TEST AFTER: the condition is evaluated after each execution and before the increment, so the body also
            // runs with the value that first satisfies it (UNTIL I > 12 runs I = 1..13).
            String f = field(var);
            o.append(p).append(label).append(": for (CobRt.store(").append(f).append(", ").append(num(s.n("from"))).append(", false, false); ; CobRt.store(")
                    .append(f).append(", CobRt.add(").append(f).append(".dec(), ").append(num(s.n("by"))).append("), false, false)) {\n")
                    .append(tick).append(body).append(ind(in + 1)).append("if (").append(cond(until)).append(") break ").append(label).append(";\n").append(p).append("}\n");
        } else if (var != null) {
            String f = field(var);
            o.append(p).append(label).append(": for (CobRt.store(").append(f).append(", ").append(num(s.n("from"))).append(", false, false); !(").append(cond(until))
                    .append("); CobRt.store(").append(f).append(", CobRt.add(").append(f).append(".dec(), ").append(num(s.n("by"))).append("), false, false)) {\n")
                    .append(tick).append(body).append(p).append("}\n");
        } else if (until != null) {
            if (after) o.append(p).append(label).append(": do {\n").append(tick).append(body).append(p).append("} while (!(").append(cond(until)).append("));\n");
            else o.append(p).append(label).append(": while (!(").append(cond(until)).append(")) {\n").append(tick).append(body).append(p).append("}\n");
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

    private void lowerGoTo(StringBuilder o, Ir.Stmt s, String p) {
        List<Ir.Node> targets = s.l("targets");
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
        boolean handled = s.blocks != null && !s.blocks.isEmpty();
        String r = t("_r");
        o.append(p).append("int ").append(r).append(" = ").append(f).append(".check(").append(call).append(", ").append(handled).append(");\n");
        if (s.n("into") != null) o.append(p).append("if (").append(r).append(" < 10) CobRt.move(").append(f).append(".record(), ").append(field(s.n("into"))).append(");\n");
        branch(o, s, in, "AT-END", "(" + r + " == 10 || " + r + " == 46)");
        branch(o, s, in, "NOT-AT-END", r + " < 10");
        branch(o, s, in, "INVALID", r + " / 10 == 2");
        branch(o, s, in, "NOT-INVALID", r + " < 10");
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
        String recVar = jname.get(rec.id);
        if (s.n("from") != null) o.append(p).append("CobRt.move(").append(operand(s.n("from"))).append(", ").append(recVar).append(");\n");
        boolean handled = s.blocks != null && !s.blocks.isEmpty();
        String r = t("_w");
        o.append(p).append("int ").append(r).append(" = ").append(f).append(".check(").append(f).append(".").append(s.verb.equals("WRITE") ? "write" : "rewrite").append("(")
                .append(recVar).append(".len").append(adv).append("), ").append(handled).append(");\n");
        branch(o, s, in, "INVALID", r + " / 10 == 2");
        branch(o, s, in, "NOT-INVALID", r + " < 10");
        branch(o, s, in, "EOP", "false");
        branch(o, s, in, "NOT-EOP", r + " < 10");
    }

    private void ioWithInvalid(StringBuilder o, Ir.Stmt s, int in, String call) {
        boolean handled = s.blocks != null && !s.blocks.isEmpty();
        String r = t("_io");
        String f = fv(s.s("file"));
        o.append(ind(in)).append("int ").append(r).append(" = ").append(f).append(".check(").append(call).append(", ").append(handled).append(");\n");
        branch(o, s, in, "INVALID", r + " / 10 == 2");
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
        List<Ir.Field> leaves = new ArrayList<>();
        initLeaves(target, leaves);
        for (Ir.Field leaf : leaves) {
            int ld = leaf.dims == null ? 0 : leaf.dims.size();
            String fixed = subs;
            String expr;
            if (ld == targetDims) expr = jname.get(leaf.id) + (ld > 0 ? ".at(" + fixed + ")" : "");
            else {
                String vars = loopVars(ld, targetDims);
                expr = jname.get(leaf.id) + ".at(" + (fixed.isEmpty() ? "" : fixed + ", ") + vars + ")";
            }
            String stmt = "CobRt.initLeaf(" + expr + ");";
            o.append(p).append(ld > targetDims ? loops(leaf.dims, targetDims, stmt) : stmt).append("\n");
        }
    }

    private void initLeaves(Ir.Field g, List<Ir.Field> out) {
        for (Ir.Field c : children.getOrDefault(g.id, List.of())) {
            if (c.lvl() == 88 || (c.redefines != null && !c.redefines.isEmpty())) continue;
            boolean group = children.containsKey(c.id) && children.get(c.id).stream().anyMatch(x -> x.lvl() != 88);
            if (group) initLeaves(c, out);
            else if (!"FILLER".equals(c.field)) out.add(c);
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
        if (s.n("returning") != null) throw new Abstain("CALL RETURNING is not lowered");
        List<String> args = new ArrayList<>();
        for (Ir.Node a : s.l("args")) {
            if ("omitted".equals(a.k)) { args.add("null"); continue; }
            boolean byRef = "REFERENCE".equals(a.op) && ("ref".equals(a.l.k));
            args.add(byRef ? field(a.l) : "CobRt.byContent(" + operand(a.l) + ")");
        }
        String call = "CobRt.call(" + name + ", new CobField[]{" + String.join(", ", args) + "});";
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
        String e = jname.get(n.id);
        if (n.args != null && !n.args.isEmpty()) {
            if (f.dims == null || f.dims.isEmpty()) throw new Abstain("subscript on a non-table item " + f.field);
            e += ".at(" + subscripts(n.args) + ")";
        } else if (f.dims != null && !f.dims.isEmpty()) {
            throw new Abstain("table item " + f.field + " referenced without subscripts");
        }
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
            case "NUMVAL-C" -> "CobFn.numvalC(" + operand(n.args.get(0)) + ")";
            case "LENGTH" -> "CobFn.length(" + operand(n.args.get(0)) + ")";
            case "INTEGER-OF-DATE" -> "CobFn.integerOfDate(" + num(n.args.get(0)) + ")";
            case "DATE-OF-INTEGER" -> "CobFn.dateOfInteger(" + num(n.args.get(0)) + ")";
            case "UPPER-CASE" -> "CobFn.upperCase(" + operand(n.args.get(0)) + ")";
            case "LOWER-CASE" -> "CobFn.lowerCase(" + operand(n.args.get(0)) + ")";
            case "TRIM" -> "CobFn.trim(" + operand(n.args.get(0)) + ")";
            case "REVERSE" -> "CobFn.reverse(" + operand(n.args.get(0)) + ")";
            default -> throw new Abstain("FUNCTION " + n.v + " is not lowered");
        };
        if (a0 == null && n.args.isEmpty()) throw new Abstain("FUNCTION " + n.v + " needs arguments");
        if (n.start != null) return "CobRt.substr(" + call + ", " + intOf(n.start) + ", " + (n.len == null ? "-1" : intOf(n.len)) + ")";
        return call;
    }

    /** A numeric expression (BigDecimal). */
    private String num(Ir.Node n) {
        if (n == null) throw new Abstain("missing numeric operand");
        return switch (n.k) {
            case "ref" -> field(n) + ".dec()";
            case "num" -> num(n.v) + ".v";
            case "bin" -> switch (n.op) {
                case "+" -> "CobRt.add(" + num(n.l) + ", " + num(n.r) + ")";
                case "-" -> "CobRt.sub(" + num(n.l) + ", " + num(n.r) + ")";
                case "*" -> "CobRt.mul(" + num(n.l) + ", " + num(n.r) + ")";
                case "/" -> "CobRt.div(" + num(n.l) + ", " + num(n.r) + ")";
                case "**" -> "CobRt.pow(" + num(n.l) + ", " + num(n.r) + ")";
                default -> throw new Abstain("operator " + n.op);
            };
            case "neg" -> num(n.l) + ".negate()";
            case "fig" -> {
                if (!"ZEROS".equals(n.v)) throw new Abstain("figurative " + n.v + " in arithmetic");
                yield "BigDecimal.ZERO";
            }
            case "fn" -> NUMERIC_FNS.contains(n.v) && n.start == null ? function(n) : "CobRt.dec(" + function(n) + ")";
            case "len" -> "BigDecimal.valueOf(" + field(n.l) + ".len)";
            default -> throw new Abstain("numeric operand kind " + n.k);
        };
    }

    private boolean isArithmetic(Ir.Node n) {
        return "bin".equals(n.k) || "neg".equals(n.k) || ("fn".equals(n.k) && NUMERIC_FNS.contains(n.v) && n.start == null);
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
