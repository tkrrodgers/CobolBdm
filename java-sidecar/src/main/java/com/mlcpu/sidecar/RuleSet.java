package com.mlcpu.sidecar;

import com.mlcpu.sidecar.RulePlanner.*;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.HexFormat;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

/**
 * The typed rule slices of one program: interned rules (identical canonical semantics share one contract), the sites
 * that use them (APPLIED) and the sites that keep the faithful lowering (SKIPPED). Rule ids and names are deterministic:
 * ids hash the canonical semantics plus input layouts; names are provisional, derived from COBOL names.
 */
final class RuleSet {

    static final String SPI_PACKAGE = "com.mlcpu.rules.spi";

    static final class Rule {
        String kind;
        Object model;
        List<Ir.Field> inputs;
        List<Boolean> numeric;
        String canonical;
        String hash;
        String ruleId;
        String name;
        String accessor;
        List<String> components;
        Ir.Node source;
        String expression;
        boolean requiresLatin1;

        String inputType() { return name + "Input"; }
        String resultType() { return name + "Result"; }
        String generatedType() { return name + "Generated"; }
        String casesType() { return name + "Cases"; }
    }

    record Use(String site, String kind, Rule rule, String paragraph, int line, String verb, List<String> receivers) {}

    record Skipped(String site, String kind, String reason, String paragraph, int line, String verb) {}

    private static final Set<String> RESERVED_TYPES = Set.of("Object", "String", "Record", "Override", "Integer", "Long", "Boolean", "Character",
            "Number", "Math", "System", "Class", "Enum", "Thread", "Runtime", "Process", "Void", "Iterable", "Comparable", "Deprecated",
            "FunctionalInterface", "SuppressWarnings", "SafeVarargs", "Error", "Exception", "BigDecimal", "List", "Objects", "Case",
            "RuleSelector", "RuleImplementation", "Short", "Byte", "Double", "Float");
    private static final Set<String> JAVA_KEYWORDS = Set.of("abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class",
            "const", "continue", "default", "do", "double", "else", "enum", "extends", "final", "finally", "float", "for", "goto", "if",
            "implements", "import", "instanceof", "int", "interface", "long", "native", "new", "package", "private", "protected", "public",
            "return", "short", "static", "strictfp", "super", "switch", "synchronized", "this", "throw", "throws", "transient", "try", "void",
            "volatile", "while", "true", "false", "null", "var", "record", "yield", "sealed", "permits", "non", "when", "module", "open", "exports",
            "requires", "uses", "provides", "to", "with", "transitive", "opens");
    private static final Set<String> RESERVED_MEMBERS = Set.of("clone", "finalize", "getClass", "hashCode", "notify", "notifyAll", "toString",
            "wait", "equals", "selected", "generated", "evaluate", "value", "input", "expected", "id", "cases", "rules");

    final String programId;
    final String pkg;
    final String bindingType;
    private final Map<String, Rule> byHash = new LinkedHashMap<>();
    private final Set<String> typeNames = new HashSet<>();
    private final Set<String> accessors = new HashSet<>();
    final List<Use> uses = new ArrayList<>();
    final List<Skipped> skips = new ArrayList<>();

    RuleSet(String programId) {
        this.programId = programId;
        this.pkg = "com.mlcpu.rules." + packageSegment(programId);
        String b = typeIdent(SkeletonEmitter.camelUpper(programId.toLowerCase(Locale.ROOT).replaceAll("[^a-z0-9]+", "-"))) + "Rules";
        this.bindingType = b;
        typeNames.add(b);
    }

    static String packageSegment(String programId) {
        String s = programId.toLowerCase(Locale.ROOT).replaceAll("[^a-z0-9]", "_");
        if (s.isEmpty() || !Character.isLetter(s.charAt(0))) s = "p" + s;
        if (JAVA_KEYWORDS.contains(s)) s = s + "_";
        return s;
    }

    String bindingFqn() {
        return pkg + "." + bindingType;
    }

    // ---- interning -------------------------------------------------------------------------------------------------

    Rule intern(String kind, Object model, RulePlanner planner, Ir.Node source, String nameHint) {
        List<Boolean> numeric = new ArrayList<>();
        for (int i = 0; i < planner.inputs.size(); i++) numeric.add(planner.isNumericInput(i));
        StringBuilder canon = new StringBuilder(kind).append('|');
        for (int i = 0; i < planner.inputs.size(); i++) canon.append(layout(planner.inputs.get(i), numeric.get(i))).append(';');
        canon.append('|').append(model instanceof Cond c ? canonical(c) : canonical((Num) model));
        String canonical = canon.toString();
        String hash = sha256(canonical);
        Rule existing = byHash.get(hash);
        if (existing != null) return existing;
        Rule r = new Rule();
        r.kind = kind;
        r.model = model;
        r.inputs = List.copyOf(planner.inputs);
        r.numeric = List.copyOf(numeric);
        r.canonical = canonical;
        r.hash = hash;
        String prefix = kind.equals("predicate") ? "PRED" : "CALC";
        String id = programId + "." + prefix + "-" + hash.substring(0, 8);
        for (Rule o : byHash.values()) if (o.ruleId.equals(id)) id = programId + "." + prefix + "-" + hash.substring(0, 16);
        r.ruleId = id;
        r.source = source;
        r.expression = model instanceof Cond c ? display(c, r.inputs) : display((Num) model, r.inputs);
        r.requiresLatin1 = model instanceof Cond c && textEquality(c);
        r.name = uniqueType(nameHint != null ? nameHint : model instanceof Cond c ? name(c, r.inputs) : "Calc", hash);
        r.accessor = uniqueAccessor(Character.toLowerCase(r.name.charAt(0)) + r.name.substring(1));
        r.components = components(r.inputs);
        byHash.put(hash, r);
        return r;
    }

    void use(String site, Rule r, String paragraph, int line, String verb, List<String> receivers) {
        uses.add(new Use(site, r.kind, r, paragraph, line, verb, receivers == null ? List.of() : List.copyOf(receivers)));
    }

    void skip(String site, String kind, String reason, String paragraph, int line, String verb) {
        skips.add(new Skipped(site, kind, reason, paragraph, line, verb));
    }

    /** The rules some retained site uses, in first-use order. */
    List<Rule> usedRules() {
        List<Rule> out = new ArrayList<>();
        Set<Rule> seen = new HashSet<>();
        for (Use u : uses) if (seen.add(u.rule())) out.add(u.rule());
        return out;
    }

    // ---- canonical form and display ---------------------------------------------------------------------------------

    private static String layout(Ir.Field f, boolean numeric) {
        return (numeric ? "N" : "X") + ":" + qualified(f) + ":" + f.category + ":" + nz(f.pic) + ":" + nz(f.usage) + ":len" + f.length + ":d" + f.digits
                + ":s" + f.impliedDecimals + ":" + (f.signed ? "S" : "U") + (f.signSeparate ? "sep" : "") + (f.signLeading ? "lead" : "")
                + ":" + nz(f.section);
    }

    /** The item's name and position within its record (distinguishes same-named items of different groups). */
    static String qualified(Ir.Field f) {
        return f.field + "@" + f.offset;
    }

    private static String nz(String s) {
        return s == null ? "" : s;
    }

    static String canonical(Cond c) {
        return switch (c) {
            case CNum n -> n.op() + "(" + canonical(n.l()) + "," + canonical(n.r()) + ")";
            case CStrEq e -> (e.equal() ? "seq" : "sne") + "(" + canonical(e.l()) + "," + canonical(e.r()) + ")";
            case C88Num n -> {
                StringBuilder b = new StringBuilder("in88n(i").append(n.parent());
                for (Num[] v : n.values()) b.append(",").append(v.length == 1 ? canonical(v[0]) : canonical(v[0]) + ".." + canonical(v[1]));
                yield b.append(")").toString();
            }
            case C88Str n -> {
                StringBuilder b = new StringBuilder("in88s(i").append(n.parent());
                for (Str v : n.values()) b.append(",").append(canonical(v));
                yield b.append(")").toString();
            }
            case CClass k -> (k.negated() ? "not-" : "") + "class-" + k.kind() + "(i" + k.input() + ")";
            case CSign s -> (s.negated() ? "not-" : "") + "sign-" + s.kind() + "(" + canonical(s.x()) + ")";
            case CAnd a -> "and(" + canonical(a.l()) + "," + canonical(a.r()) + ")";
            case COr o -> "or(" + canonical(o.l()) + "," + canonical(o.r()) + ")";
            case CNot x -> "not(" + canonical(x.x()) + ")";
        };
    }

    static String canonical(Num n) {
        return switch (n) {
            case NIn i -> "i" + i.input();
            case NLit l -> "n" + l.text();
            case NBin b -> "(" + canonical(b.l()) + b.op() + canonical(b.r()) + ")";
            case NNeg x -> "neg(" + canonical(x.x()) + ")";
        };
    }

    static String canonical(Str s) {
        return switch (s) {
            case SIn i -> "i" + i.input();
            case SLit l -> "'" + HexFormat.of().formatHex(l.text().getBytes(StandardCharsets.ISO_8859_1)) + "'";
            case SFill f -> "fill" + Integer.toHexString(f.c());
        };
    }

    private static boolean textEquality(Cond c) {
        return switch (c) {
            case CStrEq e -> true;
            case C88Str s -> true;
            case CAnd a -> textEquality(a.l()) || textEquality(a.r());
            case COr o -> textEquality(o.l()) || textEquality(o.r());
            case CNot x -> textEquality(x.x());
            default -> false;
        };
    }

    private static final Map<String, String> OPS = Map.of("eq", "=", "ne", "NOT =", "lt", "<", "le", "<=", "gt", ">", "ge", ">=");

    static String display(Cond c, List<Ir.Field> in) {
        return switch (c) {
            case CNum n -> display(n.l(), in) + " " + OPS.get(n.op()) + " " + display(n.r(), in);
            case CStrEq e -> display(e.l(), in) + (e.equal() ? " = " : " NOT = ") + display(e.r(), in);
            case C88Num n -> {
                List<String> v = new ArrayList<>();
                for (Num[] x : n.values()) v.add(x.length == 1 ? display(x[0], in) : display(x[0], in) + " THRU " + display(x[1], in));
                yield n.name() + " (" + in.get(n.parent()).field + " IN " + String.join(", ", v) + ")";
            }
            case C88Str n -> {
                List<String> v = new ArrayList<>();
                for (Str x : n.values()) v.add(display(x, in));
                yield n.name() + " (" + in.get(n.parent()).field + " IN " + String.join(", ", v) + ")";
            }
            case CClass k -> in.get(k.input()).field + " IS " + (k.negated() ? "NOT " : "") + k.kind();
            case CSign s -> display(s.x(), in) + " IS " + (s.negated() ? "NOT " : "") + s.kind();
            case CAnd a -> "(" + display(a.l(), in) + " AND " + display(a.r(), in) + ")";
            case COr o -> "(" + display(o.l(), in) + " OR " + display(o.r(), in) + ")";
            case CNot x -> "NOT (" + display(x.x(), in) + ")";
        };
    }

    static String display(Num n, List<Ir.Field> in) {
        return switch (n) {
            case NIn i -> in.get(i.input()).field;
            case NLit l -> l.text();
            case NBin b -> "(" + display(b.l(), in) + " " + b.op() + " " + display(b.r(), in) + ")";
            case NNeg x -> "-" + display(x.x(), in);
        };
    }

    static String display(Str s, List<Ir.Field> in) {
        return switch (s) {
            case SIn i -> in.get(i.input()).field;
            case SLit l -> "'" + l.text().replace("'", "''") + "'";
            case SFill f -> f.figurative();
        };
    }

    // ---- provisional names ------------------------------------------------------------------------------------------

    private static final Map<String, String> OP_NAMES = Map.of("eq", "Eq", "ne", "Ne", "lt", "Lt", "le", "Le", "gt", "Gt", "ge", "Ge");

    static String name(Cond c, List<Ir.Field> in) {
        return switch (c) {
            case CNum n -> name(n.l(), in) + OP_NAMES.get(n.op()) + name(n.r(), in);
            case CStrEq e -> name(e.l(), in) + (e.equal() ? "Eq" : "Ne") + name(e.r(), in);
            case C88Num n -> SkeletonEmitter.camelUpper(n.name());
            case C88Str n -> SkeletonEmitter.camelUpper(n.name());
            case CClass k -> SkeletonEmitter.camelUpper(in.get(k.input()).field) + "Is" + (k.negated() ? "Not" : "") + SkeletonEmitter.camelUpper(k.kind());
            case CSign s -> name(s.x(), in) + "Is" + (s.negated() ? "Not" : "") + SkeletonEmitter.camelUpper(s.kind());
            case CAnd a -> name(a.l(), in) + "And" + name(a.r(), in);
            case COr o -> name(o.l(), in) + "Or" + name(o.r(), in);
            case CNot x -> "Not" + name(x.x(), in);
        };
    }

    static String name(Num n, List<Ir.Field> in) {
        return switch (n) {
            case NIn i -> SkeletonEmitter.camelUpper(in.get(i.input()).field);
            case NLit l -> l.text().equals("0") ? "Zero" : l.text().replace("-", "Minus").replace(".", "p");
            case NBin b -> name(b.l(), in) + (b.op() == '+' ? "Plus" : b.op() == '-' ? "Minus" : "Times") + name(b.r(), in);
            case NNeg x -> "Neg" + name(x.x(), in);
        };
    }

    static String name(Str s, List<Ir.Field> in) {
        return switch (s) {
            case SIn i -> SkeletonEmitter.camelUpper(in.get(i.input()).field);
            case SLit l -> {
                StringBuilder b = new StringBuilder();
                for (char ch : l.text().toCharArray()) if (ch < 0x80 && Character.isLetterOrDigit(ch)) b.append(ch);
                String t = b.length() > 16 ? b.substring(0, 16) : b.toString();
                yield t.isEmpty() ? "Literal" : Character.toUpperCase(t.charAt(0)) + t.substring(1).toLowerCase(Locale.ROOT);
            }
            case SFill f -> SkeletonEmitter.camelUpper(f.figurative());
        };
    }

    static String typeIdent(String s) {
        StringBuilder b = new StringBuilder();
        for (char ch : s.toCharArray()) if (ch < 0x80 && (Character.isLetterOrDigit(ch) || ch == '_')) b.append(ch);
        if (b.length() == 0 || !Character.isLetter(b.charAt(0))) b.insert(0, 'R');
        b.setCharAt(0, Character.toUpperCase(b.charAt(0)));
        return b.toString();
    }

    private String uniqueType(String hint, String hash) {
        String base = typeIdent(hint);
        if (base.length() > 48) base = base.substring(0, 40) + "_" + hash.substring(0, 6);
        if (RESERVED_TYPES.contains(base)) base = base + "Rule";
        String n = base;
        for (int k = 2; taken(n); k++) n = base + k;
        for (String suffix : List.of("", "Input", "Result", "Generated", "Cases")) typeNames.add(n + suffix);
        return n;
    }

    private boolean taken(String n) {
        for (String suffix : List.of("", "Input", "Result", "Generated", "Cases"))
            if (typeNames.contains(n + suffix) || RESERVED_TYPES.contains(n + suffix)) return true;
        return false;
    }

    private String uniqueAccessor(String base) {
        if (JAVA_KEYWORDS.contains(base) || RESERVED_MEMBERS.contains(base) || base.startsWith("with")) base = base + "Rule";
        String n = base;
        for (int k = 2; !accessors.add(n); k++) n = base + k;
        return n;
    }

    static List<String> components(List<Ir.Field> inputs) {
        List<String> out = new ArrayList<>();
        Set<String> used = new HashSet<>();
        for (Ir.Field f : inputs) {
            String c = SkeletonEmitter.camel(f.field);
            StringBuilder b = new StringBuilder();
            for (char ch : c.toCharArray()) if (ch < 0x80 && (Character.isLetterOrDigit(ch) || ch == '_')) b.append(ch);
            c = b.toString();
            if (c.isEmpty() || !Character.isLetter(c.charAt(0))) c = "v" + c;
            if (JAVA_KEYWORDS.contains(c) || RESERVED_MEMBERS.contains(c)) c = c + "Value";
            String n = c;
            for (int k = 2; !used.add(n); k++) n = c + k;
            out.add(n);
        }
        return out;
    }

    static String sha256(String s) {
        try {
            return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(s.getBytes(StandardCharsets.UTF_8)));
        } catch (NoSuchAlgorithmException e) {
            throw new IllegalStateException(e);
        }
    }
}
