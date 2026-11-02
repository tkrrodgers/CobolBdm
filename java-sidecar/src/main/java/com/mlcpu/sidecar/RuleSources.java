package com.mlcpu.sidecar;

import com.mlcpu.sidecar.RulePlanner.*;
import java.math.BigDecimal;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * Renders a program's typed rule slices as public Java sources that depend on the JDK only (no runtime, no program,
 * no storage types): per rule a contract interface, an immutable input record, a result record (calculations), the
 * generated implementation and faithful-runtime witness cases; one binding per program; the shared selection SPI. Also
 * renders the rule descriptors and the APPLIED/SKIPPED extraction ledger.
 */
final class RuleSources {

    static final String CONTRACT_SCHEMA = "mlcpu.rule-contract.v1";
    static final String LEDGER_SCHEMA = "mlcpu.rule-extraction-ledger.v1";
    static final String CLAIM = "Individual predicate and calculation slices only. Control flow, I/O, MOVE/store semantics, CALL and "
            + "every SKIPPED site remain in the compatibility adapter; no capability or program is claimed as modernized.";

    record Output(List<Map<String, Object>> additionalSources, List<Map<String, Object>> ruleContracts, Map<String, Object> ledger) {}

    private RuleSources() {}

    static Output render(RuleSet set, java.util.function.IntFunction<Ir.Field> dictionary, String programClassFqn) {
        List<RuleSet.Rule> rules = set.usedRules();
        List<Map<String, Object>> sources = new ArrayList<>();
        List<Map<String, Object>> contracts = new ArrayList<>();
        if (!rules.isEmpty()) {
            sources.add(source(RuleSet.SPI_PACKAGE, "RuleImplementation", "spi", List.of(), SPI_IMPLEMENTATION));
            sources.add(source(RuleSet.SPI_PACKAGE, "RuleSelector", "spi", List.of(), SPI_SELECTOR));
        }
        for (RuleSet.Rule r : rules) {
            List<RuleCases.Case> cases = RuleCases.cases(r, dictionary);
            List<String> ids = List.of(r.ruleId);
            sources.add(source(set.pkg, r.name, "contract", ids, contract(set, r)));
            sources.add(source(set.pkg, r.inputType(), "contract", ids, input(set, r)));
            if (r.kind.equals("calculation")) sources.add(source(set.pkg, r.resultType(), "contract", ids, result(set, r)));
            sources.add(source(set.pkg, r.generatedType(), "generated-implementation", ids, generated(set, r)));
            sources.add(source(set.pkg, r.casesType(), "cases", ids, cases(set, r, cases)));
            contracts.add(descriptor(set, r, cases.size(), programClassFqn));
        }
        if (!rules.isEmpty())
            sources.add(source(set.pkg, set.bindingType, "binding", rules.stream().map(r -> r.ruleId).toList(), binding(set, rules)));
        StringBuilder all = new StringBuilder();
        for (Map<String, Object> s : sources) all.append(s.get("path")).append('\n').append(s.get("content")).append('\n');
        Map<String, Object> ledger = new LinkedHashMap<>();
        ledger.put("schema", LEDGER_SCHEMA);
        ledger.put("programId", set.programId);
        ledger.put("irSha256", null);
        ledger.put("contractSetSha256", RuleSet.sha256(all.toString()));
        ledger.put("claim", CLAIM);
        List<Map<String, Object>> applied = new ArrayList<>();
        for (RuleSet.Use u : set.uses) {
            Map<String, Object> a = new LinkedHashMap<>();
            a.put("ruleId", u.rule().ruleId);
            a.put("site", u.site());
            a.put("kind", u.kind());
            a.put("hash", u.rule().hash);
            a.put("paragraph", u.paragraph());
            a.put("line", u.line());
            a.put("verb", u.verb());
            a.put("receivers", u.receivers());
            applied.add(a);
        }
        List<Map<String, Object>> skipped = new ArrayList<>();
        for (RuleSet.Skipped s : set.skips) {
            Map<String, Object> k = new LinkedHashMap<>();
            k.put("site", s.site());
            k.put("kind", s.kind());
            k.put("reason", s.reason());
            k.put("hash", RuleSet.sha256(s.site() + "|" + s.kind() + "|" + s.reason()));
            k.put("paragraph", s.paragraph());
            k.put("line", s.line());
            k.put("verb", s.verb());
            skipped.add(k);
        }
        ledger.put("applied", applied);
        ledger.put("skipped", skipped);
        return new Output(sources, contracts, ledger);
    }

    private static Map<String, Object> source(String pkg, String type, String role, List<String> ruleIds, String content) {
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("path", pkg.replace('.', '/') + "/" + type + ".java");
        m.put("className", pkg + "." + type);
        m.put("sourceSet", "main");
        m.put("role", role);
        m.put("ruleIds", ruleIds);
        m.put("content", content);
        return m;
    }

    // ---- descriptors -----------------------------------------------------------------------------------------------

    private static Map<String, Object> descriptor(RuleSet set, RuleSet.Rule r, int caseCount, String programClassFqn) {
        Map<String, Object> d = new LinkedHashMap<>();
        d.put("schema", CONTRACT_SCHEMA);
        d.put("ruleId", r.ruleId);
        d.put("programId", set.programId);
        d.put("kind", r.kind);
        d.put("contract", set.pkg + "." + r.name);
        d.put("method", "evaluate");
        d.put("input", set.pkg + "." + r.inputType());
        d.put("output", r.kind.equals("predicate") ? "boolean" : set.pkg + "." + r.resultType());
        d.put("generatedImplementation", set.pkg + "." + r.generatedType());
        d.put("binding", set.bindingFqn());
        d.put("bindingAccessor", r.accessor);
        d.put("adapter", programClassFqn);
        d.put("cases", set.pkg + "." + r.casesType());
        d.put("caseCount", caseCount);
        List<Map<String, Object>> inputs = new ArrayList<>();
        for (int i = 0; i < r.inputs.size(); i++) {
            Ir.Field f = r.inputs.get(i);
            Map<String, Object> m = new LinkedHashMap<>();
            m.put("name", r.components.get(i));
            m.put("javaType", r.numeric.get(i) ? "java.math.BigDecimal" : "java.lang.String");
            m.put("cobolName", f.field);
            m.put("picture", f.pic);
            m.put("usage", f.usage);
            m.put("fieldId", f.id);
            m.put("category", f.category);
            m.put("length", f.length);
            m.put("section", f.section);
            inputs.add(m);
        }
        d.put("inputs", inputs);
        Map<String, Object> out = new LinkedHashMap<>();
        out.put("name", "value");
        out.put("javaType", r.kind.equals("predicate") ? "boolean" : "java.math.BigDecimal");
        out.put("cobolName", null);
        out.put("picture", null);
        out.put("usage", null);
        out.put("fieldId", null);
        out.put("note", r.kind.equals("predicate") ? "the condition's truth value"
                : "the exact unrounded intermediate; the adapter stores it with the receiver's ROUNDED/truncation/SIZE ERROR rules");
        d.put("outputs", List.of(out));
        Set<String> paragraphs = new LinkedHashSet<>();
        List<String> sites = new ArrayList<>();
        List<Integer> lines = new ArrayList<>();
        for (RuleSet.Use u : set.uses)
            if (u.rule() == r) {
                paragraphs.add(u.paragraph());
                sites.add(u.site());
                lines.add(u.line());
            }
        Map<String, Object> prov = new LinkedHashMap<>();
        prov.put("paragraph", paragraphs.isEmpty() ? null : paragraphs.iterator().next());
        prov.put("paragraphs", new ArrayList<>(paragraphs));
        prov.put("statementIds", sites);
        prov.put("cobolLines", lines);
        d.put("provenance", prov);
        d.put("evidence", List.of("faithful-runtime-cases"));
        d.put("level", "L2");
        d.put("scope", "slice");
        d.put("hash", r.hash);
        d.put("canonical", r.canonical);
        d.put("provisionalName", true);
        d.put("expression", r.expression);
        d.put("requires", r.requiresLatin1 ? List.of("recordCharset=LATIN1") : List.of());
        return d;
    }

    // ---- sources ---------------------------------------------------------------------------------------------------

    private static String header(RuleSet set) {
        return "package " + set.pkg + ";\n\n";
    }

    private static String banner(RuleSet set, RuleSet.Rule r) {
        return " * Rule {@code " + r.ruleId + "} (" + r.kind + ") of COBOL program " + doc(set.programId) + ": " + doc(r.expression) + ".\n"
                + " * Generated deterministically from the resolved semantic model; the name is provisional (review it before adopting it as a\n"
                + " * business term). Scope: this slice only, not a capability.\n";
    }

    private static String contract(RuleSet set, RuleSet.Rule r) {
        String out = r.kind.equals("predicate") ? "boolean" : r.resultType();
        return header(set) + "/**\n" + banner(set, r) + " */\n@FunctionalInterface\npublic interface " + r.name + " {\n"
                + "    /** The stable rule id (hashes the canonical semantics and input layouts). */\n"
                + "    String RULE_ID = \"" + r.ruleId + "\";\n\n"
                + "    " + out + " evaluate(" + r.inputType() + " input);\n}\n";
    }

    private static String input(RuleSet set, RuleSet.Rule r) {
        StringBuilder b = new StringBuilder(header(set));
        boolean dec = r.numeric.contains(true);
        if (dec) b.append("import java.math.BigDecimal;\n");
        b.append("import java.util.Objects;\n\n");
        b.append("/**\n * Input of {@link ").append(r.name).append("}: the COBOL items' values read at the evaluation point.\n *\n");
        List<String> params = new ArrayList<>();
        for (int i = 0; i < r.inputs.size(); i++) {
            Ir.Field f = r.inputs.get(i);
            String c = r.components.get(i);
            b.append(" * @param ").append(c).append(" ").append(doc(f.field)).append(f.pic != null && !f.pic.isEmpty() ? " PIC " + doc(f.pic) : "")
                    .append(f.usage != null && !"DISPLAY".equals(f.usage) ? " " + doc(f.usage) : "")
                    .append(r.numeric.get(i) ? ": its numeric value" : ": its exact " + f.length + "-character image (ISO-8859-1, trailing spaces included)")
                    .append("\n");
            params.add((r.numeric.get(i) ? "BigDecimal " : "String ") + c);
        }
        b.append(" */\npublic record ").append(r.inputType()).append("(").append(String.join(", ", params)).append(") {\n");
        b.append("    public ").append(r.inputType()).append(" {\n");
        for (String c : r.components) b.append("        Objects.requireNonNull(").append(c).append(", \"").append(c).append("\");\n");
        b.append("    }\n}\n");
        return b.toString();
    }

    private static String result(RuleSet set, RuleSet.Rule r) {
        return header(set) + "import java.math.BigDecimal;\nimport java.util.Objects;\n\n"
                + "/**\n * Result of {@link " + r.name + "}: the exact, unrounded value; equality compares numeric value, not scale.\n *\n"
                + " * @param value the calculated value\n */\n"
                + "public record " + r.resultType() + "(BigDecimal value) {\n"
                + "    public " + r.resultType() + " {\n        Objects.requireNonNull(value, \"value\");\n    }\n\n"
                + "    @Override\n    public boolean equals(Object other) {\n"
                + "        return other instanceof " + r.resultType() + " that && value.compareTo(that.value) == 0;\n    }\n\n"
                + "    @Override\n    public int hashCode() {\n        return value.stripTrailingZeros().hashCode();\n    }\n}\n";
    }

    private static String generated(RuleSet set, RuleSet.Rule r) {
        Gen g = new Gen(r.components);
        String body = r.model instanceof Cond c ? g.cond(c) : "new " + r.resultType() + "(" + g.num((Num) r.model) + ")";
        StringBuilder b = new StringBuilder(header(set));
        if (g.decimals || r.kind.equals("calculation") || r.numeric.contains(true)) b.append("import java.math.BigDecimal;\n\n");
        b.append("/**\n * Generated implementation of {@link ").append(r.name).append("}, equivalent to the faithful COBOL runtime for every input\n")
                .append(" * the contract describes. Plain JDK code: no program, storage or runtime types.\n */\n");
        b.append("public final class ").append(r.generatedType()).append(" implements ").append(r.name).append(" {\n");
        for (Map.Entry<String, String> e : g.constants.entrySet()) b.append("    private static final ").append(e.getValue()).append(";\n");
        if (!g.constants.isEmpty()) b.append("\n");
        b.append("    public ").append(r.generatedType()).append("() {\n    }\n\n");
        b.append("    @Override\n    public ").append(r.kind.equals("predicate") ? "boolean" : r.resultType()).append(" evaluate(").append(r.inputType())
                .append(" input) {\n        return ").append(body).append(";\n    }\n");
        for (String h : g.helpers) b.append("\n").append(HELPERS.get(h));
        b.append("}\n");
        return b.toString();
    }

    private static String cases(RuleSet set, RuleSet.Rule r, List<RuleCases.Case> cases) {
        String expectedType = r.kind.equals("predicate") ? "boolean" : r.resultType();
        StringBuilder b = new StringBuilder(header(set));
        if (r.numeric.contains(true) || r.kind.equals("calculation")) b.append("import java.math.BigDecimal;\n");
        b.append("import java.util.List;\n\n");
        b.append("/**\n * Witness cases for {@link ").append(r.name).append("}. Every input is a value its COBOL item can hold; every expected value\n")
                .append(" * was computed by the faithful COBOL runtime from the semantic model, not by {@link ").append(r.generatedType()).append("}.\n */\n");
        b.append("public final class ").append(r.casesType()).append(" {\n    private ").append(r.casesType()).append("() {\n    }\n\n");
        b.append("    /** One witness: an input and the faithful runtime's result for it. */\n");
        b.append("    public record Case(String id, ").append(r.inputType()).append(" input, ").append(expectedType).append(" expected) {\n    }\n\n");
        b.append("    public static List<Case> cases() {\n        return List.of(");
        for (int i = 0; i < cases.size(); i++) {
            RuleCases.Case c = cases.get(i);
            List<String> args = new ArrayList<>();
            for (Object v : c.inputs()) args.add(v instanceof BigDecimal d ? decimal(d) : SkeletonEmitter.jstr((String) v));
            String exp = c.expected() instanceof Boolean t ? t.toString() : "new " + r.resultType() + "(" + decimal((BigDecimal) c.expected()) + ")";
            b.append(i == 0 ? "\n" : ",\n").append("                new Case(\"c").append(String.format("%03d", i + 1)).append("\", new ").append(r.inputType())
                    .append("(").append(String.join(", ", args)).append("), ").append(exp).append(")");
        }
        b.append(");\n    }\n}\n");
        return b.toString();
    }

    private static String binding(RuleSet set, List<RuleSet.Rule> rules) {
        StringBuilder b = new StringBuilder(header(set));
        b.append("import ").append(RuleSet.SPI_PACKAGE).append(".RuleSelector;\nimport java.util.Objects;\n\n");
        b.append("/**\n * The typed rule implementations COBOL program ").append(doc(set.programId)).append(" uses: immutable, injected through the\n")
                .append(" * program adapter's constructor. {@link #selected()} honours the system property {@code mlcpu.rule.<ruleId>} through\n")
                .append(" * {@link RuleSelector} (no silent fallback; the choice is pinned for the whole process); {@link #generated()} uses the\n")
                .append(" * generated implementations only. Choose per request or run unit by passing an explicit binding of stateless\n")
                .append(" * implementations to the program's constructor.\n */\n");
        b.append("public final class ").append(set.bindingType).append(" {\n");
        for (RuleSet.Rule r : rules) b.append("    private final ").append(r.name).append(" ").append(r.accessor).append(";\n");
        b.append("\n    private ").append(set.bindingType).append("(");
        b.append(String.join(", ", rules.stream().map(r -> r.name + " " + r.accessor).toList())).append(") {\n");
        for (RuleSet.Rule r : rules)
            b.append("        this.").append(r.accessor).append(" = Objects.requireNonNull(").append(r.accessor).append(", \"").append(r.accessor).append("\");\n");
        b.append("    }\n\n");
        b.append("    /** The implementations chosen by {@link RuleSelector} (the generated one unless a property selects another). */\n");
        b.append("    public static ").append(set.bindingType).append(" selected() {\n        return new ").append(set.bindingType).append("(");
        b.append(String.join(",\n                ", rules.stream().map(r -> "RuleSelector.select(" + r.name + ".RULE_ID, " + r.name + ".class, " + r.generatedType() + "::new)").toList()));
        b.append(");\n    }\n\n");
        b.append("    /** The generated implementations only. */\n");
        b.append("    public static ").append(set.bindingType).append(" generated() {\n        return new ").append(set.bindingType).append("(");
        b.append(String.join(", ", rules.stream().map(r -> "new " + r.generatedType() + "()").toList()));
        b.append(");\n    }\n");
        for (RuleSet.Rule r : rules) {
            b.append("\n    public ").append(r.name).append(" ").append(r.accessor).append("() {\n        return ").append(r.accessor).append(";\n    }\n");
            String up = Character.toUpperCase(r.accessor.charAt(0)) + r.accessor.substring(1);
            b.append("\n    public ").append(set.bindingType).append(" with").append(up).append("(").append(r.name).append(" implementation) {\n");
            b.append("        return new ").append(set.bindingType).append("(");
            b.append(String.join(", ", rules.stream().map(o -> o == r ? "implementation" : o.accessor).toList()));
            b.append(");\n    }\n");
        }
        b.append("}\n");
        return b.toString();
    }

    private static String decimal(BigDecimal d) {
        return "new BigDecimal(\"" + d.toString() + "\")";
    }

    /** Text safe inside a Javadoc comment: printable ASCII, no comment terminator, no unicode escape, no HTML. */
    static String doc(String s) {
        if (s == null) return "";
        StringBuilder b = new StringBuilder();
        for (char ch : s.toCharArray()) b.append(ch < 0x20 || ch > 0x7E ? '?' : ch);
        return b.toString().replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace("*/", "*&#47;").replace("\\", "&#92;")
                .replace("@", "&#64;").replace("{", "&#123;").replace("}", "&#125;");
    }

    // ---- expression rendering --------------------------------------------------------------------------------------

    private static final Map<String, String> HELPERS = Map.of(
            "padEq", """
                    /** Alphanumeric equality: the shorter operand is padded with spaces. */
                    private static boolean padEq(String a, String b) {
                        int n = Math.max(a.length(), b.length());
                        for (int i = 0; i < n; i++) {
                            char x = i < a.length() ? a.charAt(i) : ' ';
                            char y = i < b.length() ? b.charAt(i) : ' ';
                            if (x != y) return false;
                        }
                        return true;
                    }
                """,
            "allChar", """
                    /** Equality with a figurative constant: every character is {@code c}. */
                    private static boolean allChar(String a, char c) {
                        for (int i = 0; i < a.length(); i++) if (a.charAt(i) != c) return false;
                        return true;
                    }
                """,
            "digitsOnly", """
                    /** NUMERIC class test of alphanumeric text. */
                    private static boolean digitsOnly(String a) {
                        for (int i = 0; i < a.length(); i++) if (a.charAt(i) < '0' || a.charAt(i) > '9') return false;
                        return true;
                    }
                """,
            "alphabetic", """
                    /** ALPHABETIC class test: letters and spaces. */
                    private static boolean alphabetic(String a) {
                        for (int i = 0; i < a.length(); i++) {
                            char c = a.charAt(i);
                            if (c != ' ' && (c < 'a' || c > 'z') && (c < 'A' || c > 'Z')) return false;
                        }
                        return true;
                    }
                """,
            "alphabeticLower", """
                    /** ALPHABETIC-LOWER class test: lower-case letters and spaces. */
                    private static boolean alphabeticLower(String a) {
                        for (int i = 0; i < a.length(); i++) if (a.charAt(i) != ' ' && (a.charAt(i) < 'a' || a.charAt(i) > 'z')) return false;
                        return true;
                    }
                """,
            "alphabeticUpper", """
                    /** ALPHABETIC-UPPER class test: upper-case letters and spaces. */
                    private static boolean alphabeticUpper(String a) {
                        for (int i = 0; i < a.length(); i++) if (a.charAt(i) != ' ' && (a.charAt(i) < 'A' || a.charAt(i) > 'Z')) return false;
                        return true;
                    }
                """);

    private static final Map<String, String> CMP = Map.of("eq", "== 0", "ne", "!= 0", "lt", "< 0", "le", "<= 0", "gt", "> 0", "ge", ">= 0");

    /** Plain-Java rendering of a typed model. */
    private static final class Gen {
        final List<String> components;
        final Map<String, String> constants = new LinkedHashMap<>();
        final Set<String> helpers = new java.util.TreeSet<>();
        boolean decimals;

        Gen(List<String> components) {
            this.components = components;
        }

        String cond(Cond c) {
            return switch (c) {
                case CNum n -> num(n.l()) + ".compareTo(" + num(n.r()) + ") " + CMP.get(n.op());
                case CStrEq e -> (e.equal() ? "" : "!") + textEq(e.l(), e.r());
                case C88Num n -> {
                    String p = "input." + components.get(n.parent()) + "()";
                    List<String> t = new ArrayList<>();
                    for (Num[] v : n.values())
                        t.add(v.length == 1 ? p + ".compareTo(" + num(v[0]) + ") == 0"
                                : "(" + p + ".compareTo(" + num(v[0]) + ") >= 0 && " + p + ".compareTo(" + num(v[1]) + ") <= 0)");
                    yield t.size() == 1 ? t.get(0) : "(" + String.join(" || ", t) + ")";
                }
                case C88Str n -> {
                    List<String> t = new ArrayList<>();
                    for (Str v : n.values()) t.add(textEq(new SIn(n.parent()), v));
                    yield t.size() == 1 ? t.get(0) : "(" + String.join(" || ", t) + ")";
                }
                case CClass k -> {
                    String h = switch (k.kind()) {
                        case "NUMERIC" -> "digitsOnly";
                        case "ALPHABETIC-LOWER" -> "alphabeticLower";
                        case "ALPHABETIC-UPPER" -> "alphabeticUpper";
                        default -> "alphabetic";
                    };
                    helpers.add(h);
                    yield (k.negated() ? "!" : "") + h + "(input." + components.get(k.input()) + "())";
                }
                case CSign s -> {
                    String t = num(s.x()) + ".signum() " + switch (s.kind()) {
                        case "POSITIVE" -> "> 0";
                        case "NEGATIVE" -> "< 0";
                        default -> "== 0";
                    };
                    yield s.negated() ? "!(" + t + ")" : t;
                }
                case CAnd a -> "(" + cond(a.l()) + " && " + cond(a.r()) + ")";
                case COr o -> "(" + cond(o.l()) + " || " + cond(o.r()) + ")";
                case CNot x -> "!(" + cond(x.x()) + ")";
            };
        }

        private String textEq(Str l, Str r) {
            if (l instanceof SFill f) return fillEq(r, f);
            if (r instanceof SFill f) return fillEq(l, f);
            helpers.add("padEq");
            return "padEq(" + text(l) + ", " + text(r) + ")";
        }

        private String fillEq(Str other, SFill f) {
            helpers.add("allChar");
            return "allChar(" + text(other) + ", (char) " + (int) f.c() + ")";
        }

        private String text(Str s) {
            return switch (s) {
                case SIn i -> "input." + components.get(i.input()) + "()";
                case SLit l -> constant("String", SkeletonEmitter.jstr(l.text()), "T");
                case SFill f -> throw new IllegalStateException("figurative rendered as text");
            };
        }

        String num(Num n) {
            decimals = true;
            return switch (n) {
                case NIn i -> "input." + components.get(i.input()) + "()";
                case NLit l -> constant("BigDecimal", "new BigDecimal(\"" + l.text() + "\")", "N");
                case NBin b -> num(b.l()) + (b.op() == '+' ? ".add(" : b.op() == '-' ? ".subtract(" : ".multiply(") + num(b.r()) + ")";
                case NNeg x -> num(x.x()) + ".negate()";
            };
        }

        private String constant(String type, String init, String prefix) {
            for (Map.Entry<String, String> e : constants.entrySet())
                if (e.getValue().endsWith(" = " + init) && e.getValue().startsWith(type + " ")) return e.getKey();
            String name = prefix + constants.size();
            constants.put(name, type + " " + name + " = " + init);
            return name;
        }
    }

    // ---- the selection SPI (byte-identical for every program) ------------------------------------------------------

    static final String SPI_IMPLEMENTATION = """
            package com.mlcpu.rules.spi;

            /**
             * An alternative implementation of one generated rule contract, discovered with java.util.ServiceLoader
             * (META-INF/services/com.mlcpu.rules.spi.RuleImplementation). {@link RuleSelector} uses it only when the system property
             * {@code mlcpu.rule.<ruleId>} names its {@link #implementationId()}.
             *
             * @param <C> the rule contract
             */
            public interface RuleImplementation<C> {
                String ruleId();

                String implementationId();

                Class<C> contract();

                C create();
            }
            """;

    static final String SPI_SELECTOR = """
            package com.mlcpu.rules.spi;

            import java.util.ArrayList;
            import java.util.List;
            import java.util.Map;
            import java.util.ServiceLoader;
            import java.util.concurrent.ConcurrentHashMap;
            import java.util.function.Supplier;

            /**
             * Chooses the implementation of each rule: the generated one unless the system property {@code mlcpu.rule.<ruleId>} names
             * a {@link RuleImplementation#implementationId()}. A selection that matches no implementation, several, or one for another
             * contract fails: there is no silent fallback and no automatic promotion. Selection is process-pinned: the first choice
             * per rule id is cached for the whole JVM (not per run unit or request) until {@link #reset()}. Per-request or per-run-unit
             * selection must construct the program with an explicit rule binding (constructor injection) of stateless implementations.
             */
            public final class RuleSelector {
                public static final String GENERATED = "generated";
                public static final String PROPERTY_PREFIX = "mlcpu.rule.";

                private static final Map<String, Object> SELECTED = new ConcurrentHashMap<>();

                private RuleSelector() {
                }

                public static String selection(String ruleId) {
                    String value = System.getProperty(PROPERTY_PREFIX + ruleId);
                    return value == null || value.isBlank() ? GENERATED : value.trim();
                }

                public static <C> C select(String ruleId, Class<C> contract, Supplier<? extends C> generated) {
                    Object chosen = SELECTED.computeIfAbsent(ruleId, id -> resolve(id, contract, generated));
                    if (!contract.isInstance(chosen))
                        throw new IllegalStateException("rule " + ruleId + " is bound to " + chosen.getClass().getName() + ", not a " + contract.getName());
                    return contract.cast(chosen);
                }

                public static void reset() {
                    SELECTED.clear();
                }

                private static Object resolve(String ruleId, Class<?> contract, Supplier<?> generated) {
                    String selection = selection(ruleId);
                    if (GENERATED.equals(selection)) {
                        Object instance = generated.get();
                        if (!contract.isInstance(instance)) throw new IllegalStateException("generated implementation of " + ruleId + " is not a " + contract.getName());
                        return instance;
                    }
                    List<RuleImplementation<?>> matches = new ArrayList<>();
                    for (RuleImplementation<?> candidate : ServiceLoader.load(RuleImplementation.class, RuleSelector.class.getClassLoader()))
                        if (ruleId.equals(candidate.ruleId()) && selection.equals(candidate.implementationId())) matches.add(candidate);
                    if (matches.size() != 1)
                        throw new IllegalStateException("rule " + ruleId + " selects implementation '" + selection + "', found " + matches.size());
                    RuleImplementation<?> implementation = matches.get(0);
                    if (implementation.contract() != contract)
                        throw new IllegalStateException("implementation '" + selection + "' of " + ruleId + " declares contract " + implementation.contract().getName());
                    Object instance = implementation.create();
                    if (!contract.isInstance(instance))
                        throw new IllegalStateException("implementation '" + selection + "' of " + ruleId + " created a " + (instance == null ? "null" : instance.getClass().getName()));
                    return instance;
                }
            }
            """;
}
