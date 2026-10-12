package com.mlcpu.rt;

import com.mlcpu.rt.EquivalenceCatalog.DSrc;
import com.mlcpu.rt.EquivalenceCatalog.Env;
import com.mlcpu.rt.EquivalenceCatalog.Epoch;
import com.mlcpu.rt.EquivalenceCatalog.Hyp;
import com.mlcpu.rt.EquivalenceCatalog.IntP;
import com.mlcpu.rt.EquivalenceCatalog.Invalid;
import com.mlcpu.rt.EquivalenceCatalog.Shape;
import com.mlcpu.rt.EquivalenceCatalog.Src;
import com.mlcpu.rt.EquivalenceRules.Learned;
import com.mlcpu.rt.EquivalenceRules.Obs;
import com.mlcpu.rt.EquivalenceRules.Pred;
import com.mlcpu.rt.EquivalenceRules.Rule;

import java.io.IOException;
import java.io.OutputStream;
import java.math.BigDecimal;
import java.math.BigInteger;
import java.math.RoundingMode;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.DateTimeException;
import java.time.LocalDate;
import java.time.temporal.ChronoUnit;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Random;
import java.util.Set;
import java.util.TreeMap;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.stream.Stream;

/**
 * Library-equivalence probe: establishes, from evidence, that a COBOL subprogram (or one function of it) computes a
 * JDK operation, so a model modernizing it can be told so instead of re-deriving a digit or calendar algorithm.
 *
 * <p>It decodes every traced CALL's linkage fields with the runtime's codecs, classifies fields (outputs change in some
 * CALL; selectors are small-alphabet codes), groups CALLs by the function selector, pairs decimal operands
 * (coefficient + exponent fields, scaled numerics) and searches a catalog of JDK candidates ({@link EquivalenceCatalog})
 * per group. A RoundingMode or ChronoUnit coded by a selector is solved per code value (one constant per code for all
 * its CALLs; 88-level names break ties). The output representation is part of the hypothesis. A hypothesis is accepted
 * only if it reproduces every traced CALL of its group byte for byte (all output fields and the return code, with the
 * other fields' behaviour learned: {@link EquivalenceRules}); it is then run against {@code --generate N} CALLs whose
 * inputs are sampled from the observed distributions, through the faithful translation. Where it stops holding, the
 * failing CALLs are described by a region of input features.
 *
 * <pre>EquivalenceProbe --program PID --layouts record-layouts.json [--linkage REC[,REC...]] --traces dir|file... [--conditions file]
 *     [--generate 100000] [--learn 2000] [--seed 42] --out dir</pre>
 *
 * Writes equivalences.json (machine-readable) and equivalences.md (for the model's prompt) to --out.
 */
public final class EquivalenceProbe {
    private EquivalenceProbe() {}

    /** The program hypotheses are checked against: the faithful translation, or a stand-in in tests. */
    public interface Faithful {
        void call(byte[][] params);

        /** After an abend: a fresh run unit. */
        default void reset() {}
    }

    public static final class Options {
        public String program;
        public Path layouts;
        public List<String> linkage = new ArrayList<>();
        public List<Path> traces = new ArrayList<>();
        public Path conditions;
        public int generate = 100_000;
        public int learn = 2_000;
        public long seed = 42;
        public Path out;
        public int maxFields = 160;
        public int maxBytes = 8_192;
        /** Groups for 88-level codes of the function selector that no traced CALL used (generated CALLs only). */
        public boolean explore = true;
        /** Time budget in seconds (0: none); groups not reached are reported as such. */
        public int maxSeconds = 0;
    }

    public static void main(String[] args) throws IOException {
        Options o = new Options();
        for (int i = 0; i < args.length; i++) {
            switch (args[i]) {
                case "--program" -> o.program = args[++i].toUpperCase(Locale.ROOT);
                case "--layouts" -> o.layouts = Path.of(args[++i]);
                case "--linkage" -> o.linkage.addAll(Arrays.asList(args[++i].toUpperCase(Locale.ROOT).split(",")));
                case "--traces" -> {
                    while (i + 1 < args.length && !args[i + 1].startsWith("--")) o.traces.add(Path.of(args[++i]));
                }
                case "--conditions" -> o.conditions = Path.of(args[++i]);
                case "--generate" -> o.generate = Integer.parseInt(args[++i]);
                case "--learn" -> o.learn = Integer.parseInt(args[++i]);
                case "--seed" -> o.seed = Long.parseLong(args[++i]);
                case "--out" -> o.out = Path.of(args[++i]);
                case "--max-fields" -> o.maxFields = Integer.parseInt(args[++i]);
                case "--no-explore" -> o.explore = false;
                case "--max-seconds" -> o.maxSeconds = Integer.parseInt(args[++i]);
                default -> throw new IllegalArgumentException("unknown argument " + args[i]);
            }
        }
        if (o.program == null || o.layouts == null || o.out == null || o.traces.isEmpty())
            throw new IllegalArgumentException("--program, --layouts, --traces and --out are required");
        DisplaySink.set(new DisplaySink.ToStream(OutputStream.nullOutputStream()));
        CobConfig.LOOP_LIMIT = Math.min(CobConfig.LOOP_LIMIT, 20_000_000L);
        CobRt.resetRunUnit();
        ProgramRegistry.reset();
        final String pid = o.program;
        Faithful faithful = new Faithful() {
            public void call(byte[][] params) { ProgramRegistry.callFaithful(pid, params); }

            public void reset() {
                ProgramRegistry.reset();
                CobRt.resetRunUnit();
            }
        };
        long t0 = System.nanoTime();
        Report r = run(o, faithful);
        Files.createDirectories(o.out);
        Files.writeString(o.out.resolve("equivalences.json"), r.json(), StandardCharsets.UTF_8);
        Files.writeString(o.out.resolve("equivalences.md"), r.markdown(), StandardCharsets.UTF_8);
        System.out.println(r.summary() + String.format(Locale.ROOT, " (%.1f s)", (System.nanoTime() - t0) / 1e9));
        System.exit(0);
    }

    // ================================================================== the linkage model

    /** An elementary linkage field with the runtime's codecs. */
    static final class PField {
        final int id;
        final String name;
        final int arg, off, len;
        final String kind;
        final int digits, scale;
        final boolean signed, signSep, signLead;
        final Area area;
        final CobField view;
        /** 88-level condition names: the code (as {@link #code} renders it) to the name. */
        final Map<String, String> conditions = new LinkedHashMap<>();

        PField(int id, String name, int arg, int off, int len, String kind, int digits, int scale, boolean signed, boolean signSep,
               boolean signLead, int argLen) {
            this.id = id;
            this.name = name;
            this.arg = arg;
            this.off = off;
            this.len = len;
            this.kind = kind;
            this.digits = digits;
            this.scale = scale;
            this.signed = signed;
            this.signSep = signSep;
            this.signLead = signLead;
            this.area = new Area(Math.max(argLen, off + len));
            CobField.Use use = switch (kind) {
                case "packed" -> CobField.Use.PACKED;
                case "binary" -> CobField.Use.BINARY;
                default -> CobField.Use.DISPLAY;
            };
            this.view = numeric() ? CobField.num(name, use, area, off, len, digits, scale, signed, signSep, signLead, null, null)
                : CobField.alnum(name, area, off, len, false, null, null);
        }

        boolean numeric() { return kind.equals("packed") || kind.equals("binary") || kind.equals("zoned"); }

        boolean integer() { return numeric() && scale == 0; }

        /** The value, or null when the bytes are not valid for the usage (or the argument is too short). */
        BigDecimal num(byte[][] a) {
            byte[] b = a[arg];
            if (b == null || b.length < off + len || !valid(b)) return null;
            area.b = b;
            return view.dec();
        }

        private boolean valid(byte[] b) {
            switch (kind) {
                case "packed" -> {
                    for (int i = off; i < off + len; i++) {
                        int hi = (b[i] >> 4) & 15, lo = b[i] & 15;
                        if (hi > 9) return false;
                        if (i < off + len - 1 ? lo > 9 : lo < 10) return false;
                    }
                    return true;
                }
                case "zoned" -> {
                    int s = off, e = off + len;
                    if (signed && signSep) {
                        int sc = signLead ? b[off] : b[off + len - 1];
                        if (sc != '+' && sc != '-') return false;
                        if (signLead) s++; else e--;
                    }
                    for (int i = s; i < e; i++) {
                        int c = b[i] & 0xFF;
                        boolean signPos = signed && !signSep && (signLead ? i == s : i == e - 1);
                        if (c >= '0' && c <= '9') continue;
                        if (signPos && Overpunch.isSign(c)) continue;
                        return false;
                    }
                    return true;
                }
                default -> {
                    return true;
                }
            }
        }

        /** A selector code: the field's text, or a number's plain value. */
        String code(byte[][] a) {
            if (numeric()) {
                BigDecimal v = num(a);
                return v == null ? "?" : v.stripTrailingZeros().toPlainString();
            }
            byte[] b = a[arg];
            if (b == null || b.length < off + len) return "";
            return new String(b, off, len, StandardCharsets.ISO_8859_1);
        }

        boolean storeCode(byte[][] a, String code) {
            if (numeric()) {
                try {
                    return store(a, new BigDecimal(code));
                } catch (NumberFormatException e) {
                    return false;
                }
            }
            byte[] v = new byte[len];
            Arrays.fill(v, (byte) ' ');
            byte[] c = code.getBytes(StandardCharsets.ISO_8859_1);
            System.arraycopy(c, 0, v, 0, Math.min(len, c.length));
            System.arraycopy(v, 0, a[arg], off, len);
            return true;
        }

        /** Stores a value exactly: false when it needs rounding, truncation or more digits than the PICTURE has. */
        boolean store(byte[][] a, BigDecimal v) {
            if (!numeric()) return false;
            BigDecimal s;
            try {
                s = v.setScale(scale, RoundingMode.UNNECESSARY);
            } catch (ArithmeticException e) {
                return false;
            }
            BigInteger u = s.unscaledValue();
            if (u.abs().compareTo(Pow10.big(digits)) >= 0) return false;
            if (!signed && u.signum() < 0) return false;
            byte[] b = a[arg];
            if (b.length < off + len) return false;
            area.b = b;
            view.setDec(s, false);
            return true;
        }

        boolean changed(byte[][] before, byte[][] after) {
            return !Arrays.equals(before[arg], off, off + len, after[arg], off, off + len);
        }

        public String toString() { return name; }
    }

    /** One CALL: its arguments before and after (after is null when the faithful program ended abnormally). */
    record PCall(String source, long seq, byte[][] before, byte[][] after, String abend, boolean generated) {}

    /** The program's linkage: arguments in USING order and their elementary fields. */
    static final class Prog {
        final String id;
        final List<String> records = new ArrayList<>();
        final int[] argLens;
        final List<PField> fields = new ArrayList<>();
        final List<Src> pairs = new ArrayList<>();
        final List<Src> singles = new ArrayList<>();
        final Set<PField> pairFields = new HashSet<>();

        Prog(String id, int args) {
            this.id = id;
            this.argLens = new int[args];
        }

        PField field(String name) {
            for (PField f : fields) if (f.name.equals(name)) return f;
            return null;
        }
    }

    // ================================================================== loading

    @SuppressWarnings("unchecked")
    static Prog loadLayouts(Path file, List<String> linkage, String program) throws IOException {
        Map<String, Object> root = (Map<String, Object>) MiniJson.parse(Files.readString(file, StandardCharsets.UTF_8));
        List<String> names = new ArrayList<>(linkage);
        if (names.isEmpty() && root.get("linkage") instanceof List<?> l) for (Object x : l) names.add(String.valueOf(x).toUpperCase(Locale.ROOT));
        if (names.isEmpty()) throw new IllegalArgumentException("no linkage records: pass --linkage");
        String pid = program != null ? program : String.valueOf(root.get("programId"));
        List<Map<String, Object>> layouts = (List<Map<String, Object>>) root.get("layouts");
        Prog p = new Prog(pid, names.size());
        int id = 0;
        for (int a = 0; a < names.size(); a++) {
            Map<String, Object> lay = null;
            for (Map<String, Object> l : layouts) {
                if (!names.get(a).equalsIgnoreCase(String.valueOf(l.get("name")))) continue;
                if (lay == null || pid.equalsIgnoreCase(String.valueOf(l.get("program")))) lay = l;
            }
            if (lay == null) throw new IllegalArgumentException("no layout for linkage record " + names.get(a));
            p.records.add(names.get(a));
            p.argLens[a] = ((Number) lay.get("length")).intValue();
            for (Map<String, Object> f : (List<Map<String, Object>>) lay.get("fields")) {
                String kind = String.valueOf(f.get("kind"));
                if (kind.equals("group") || Boolean.TRUE.equals(f.get("group")) || f.get("occurs") != null) continue;
                p.fields.add(new PField(id++, String.valueOf(f.get("name")), a, ((Number) f.get("offset")).intValue(), ((Number) f.get("length")).intValue(),
                    kind, num(f.get("digits")), num(f.get("scale")), Boolean.TRUE.equals(f.get("signed")), Boolean.TRUE.equals(f.get("signSeparate")),
                    Boolean.TRUE.equals(f.get("signLeading")), p.argLens[a]));
                if (f.get("conditions") instanceof Map<?, ?> m)
                    for (Map.Entry<?, ?> e : m.entrySet()) p.fields.get(p.fields.size() - 1).conditions.put(String.valueOf(e.getKey()), String.valueOf(e.getValue()));
            }
        }
        return p;
    }

    private static int num(Object o) { return o instanceof Number n ? n.intValue() : 0; }

    static final Pattern OUTLINE_ITEM = Pattern.compile("^\\s*L\\d+\\s+(\\d\\d)\\s+([A-Z0-9][A-Z0-9-]*)(.*)$");
    static final Pattern LITERAL = Pattern.compile("'([^']*)'|\"([^\"]*)\"|([+-]?\\d+(?:\\.\\d+)?)");

    /**
     * 88-level condition names: mlcpu.condition-names.v1 JSON ({"fields": {"DK-MODE": [{"name": "DK-MODE-UP", "values":
     * ["U"]}, ...]}}) or an AST outline (normalization/PID/ast-outline.txt).
     */
    @SuppressWarnings("unchecked")
    static void loadConditions(Path file, Prog p) throws IOException {
        String text = Files.readString(file, StandardCharsets.UTF_8);
        if (text.trim().startsWith("{")) {
            Map<String, Object> root = (Map<String, Object>) MiniJson.parse(text);
            Map<String, Object> fields = (Map<String, Object>) root.getOrDefault("fields", root);
            for (Map.Entry<String, Object> e : fields.entrySet()) {
                PField f = p.field(e.getKey());
                if (f == null || !(e.getValue() instanceof List<?> l)) continue;
                for (Object o : l) {
                    Map<String, Object> c = (Map<String, Object>) o;
                    for (Object v : (List<Object>) c.get("values")) f.conditions.put(codeOf(f, String.valueOf(v)), String.valueOf(c.get("name")));
                }
            }
            return;
        }
        PField current = null;
        for (String line : text.split("\\R")) {
            Matcher m = OUTLINE_ITEM.matcher(line);
            if (!m.matches()) continue;
            if (!m.group(1).equals("88")) {
                current = p.field(m.group(2));
                continue;
            }
            if (current == null) continue;
            String rest = m.group(3);
            int v = rest.toUpperCase(Locale.ROOT).indexOf("VALUE");
            if (v < 0) continue;
            Matcher lit = LITERAL.matcher(rest.substring(v + 5));
            while (lit.find()) {
                String val = lit.group(1) != null ? lit.group(1) : lit.group(2) != null ? lit.group(2) : lit.group(3);
                current.conditions.put(codeOf(current, val), m.group(2));
            }
        }
    }

    /** A literal as {@link PField#code} renders the field holding it (text padded to the field's length). */
    static String codeOf(PField f, String literal) {
        if (f.numeric()) {
            try {
                return new BigDecimal(literal).stripTrailingZeros().toPlainString();
            } catch (NumberFormatException e) {
                return literal;
            }
        }
        StringBuilder sb = new StringBuilder(literal);
        while (sb.length() < f.len) sb.append(' ');
        return sb.substring(0, f.len);
    }

    /** mlcpu.call-trace.v1 files (directories are searched), CALLs to the program, each once. */
    @SuppressWarnings("unchecked")
    static List<PCall> loadTraces(List<Path> paths, String program, int args) throws IOException {
        List<Path> files = new ArrayList<>();
        for (Path p : paths) {
            if (Files.isDirectory(p)) {
                try (Stream<Path> s = Files.walk(p)) {
                    s.filter(x -> x.toString().endsWith(".json")).sorted().forEach(files::add);
                }
            } else if (Files.exists(p)) files.add(p);
        }
        List<PCall> out = new ArrayList<>();
        Set<String> seen = new HashSet<>();
        for (Path f : files) {
            String text = Files.readString(f, StandardCharsets.UTF_8);
            if (!text.contains("mlcpu.call-trace.v1")) continue;
            Map<String, Object> t = (Map<String, Object>) MiniJson.parse(text);
            if (program != null && !program.equalsIgnoreCase(String.valueOf(t.get("traced")))) continue;
            String source = f.getParent() == null ? f.toString() : f.getParent().getFileName().toString();
            for (Map<String, Object> c : (List<Map<String, Object>>) t.get("calls")) {
                long seq = ((Number) c.get("seq")).longValue();
                if (!seen.add(source + "#" + seq)) continue;
                List<Object> b = (List<Object>) c.get("before"), a = (List<Object>) c.get("after");
                if (b.size() < args || a.size() < args) continue;
                byte[][] before = new byte[args][], after = new byte[args][];
                for (int i = 0; i < args; i++) {
                    before[i] = String.valueOf(b.get(i)).getBytes(StandardCharsets.ISO_8859_1);
                    after[i] = String.valueOf(a.get(i)).getBytes(StandardCharsets.ISO_8859_1);
                }
                out.add(new PCall(source, seq, before, after, null, false));
            }
        }
        return out;
    }

    // ================================================================== entry point

    public static Report run(Options o, Faithful faithful) throws IOException {
        Prog p = loadLayouts(o.layouts, o.linkage, o.program);
        Report r = new Report(p.id, o);
        int bytes = Arrays.stream(p.argLens).sum();
        if (p.fields.size() > o.maxFields || bytes > o.maxBytes) {
            r.skipped = "linkage too large for the probe (" + p.fields.size() + " fields, " + bytes + " bytes)";
            return r;
        }
        if (o.conditions != null && Files.exists(o.conditions)) loadConditions(o.conditions, p);
        List<PCall> traced = loadTraces(o.traces, p.id, p.argLens.length);
        r.traced = traced.size();
        r.sources = (int) traced.stream().map(PCall::source).distinct().count();
        if (traced.isEmpty()) {
            r.skipped = "no traced CALLs";
            return r;
        }
        Faithful f = faithful;
        if (f != null && o.generate + o.learn > 0) {
            // The faithful program must be the one that produced the traces: replay a sample of traced CALLs through it.
            int n = Math.min(200, traced.size()), same = 0;
            String error = null;
            for (int i = 0; i < n; i++) {
                PCall c = traced.get((int) ((long) i * traced.size() / n));
                byte[][] params = copy(c.before());
                try {
                    f.call(params);
                } catch (RuntimeException e) {
                    f.reset();
                    if (error == null) error = e.getClass().getSimpleName() + ": " + e.getMessage();
                    continue;
                }
                if (Arrays.deepEquals(params, c.after())) same++;
            }
            r.replayed = n;
            r.replayedSame = same;
            if (same == 0) {
                r.faithfulNote = "the faithful program did not reproduce any of " + n + " replayed traced CALLs" + (error == null ? "" : " (" + error + ")")
                                 + "; no CALLs were generated";
                f = null;
            }
        }
        new Probe(p, traced, f, o, r).solveAll();
        return r;
    }

    // ================================================================== the probe

    static final class Probe {
        final Prog p;
        final List<PCall> traced;
        final Faithful faithful;
        final Options o;
        final Report report;
        final List<PField> selectors = new ArrayList<>();
        final List<PField> keySels = new ArrayList<>();
        List<Epoch> epochs = EquivalenceCatalog.STANDARD_EPOCHS;
        long seq;
        long deadline = Long.MAX_VALUE;

        boolean late() { return deadline != Long.MAX_VALUE && System.nanoTime() > deadline; }

        Probe(Prog p, List<PCall> traced, Faithful faithful, Options o, Report report) {
            this.p = p;
            this.traced = traced;
            this.faithful = faithful;
            this.o = o;
            this.report = report;
            if (o.maxSeconds > 0) this.deadline = System.nanoTime() + o.maxSeconds * 1_000_000_000L;
        }

        void solveAll() {
            findPairs();
            findSelectors();
            epochs = solveEpochs();
            PField key = chooseKey();
            report.key = key;
            List<Group> groups = new ArrayList<>();
            if (key == null) groups.add(new Group(new LinkedHashMap<>(), traced, false, traced));
            else {
                Map<String, List<PCall>> by = new TreeMap<>();
                for (PCall c : traced) by.computeIfAbsent(key.code(c.before()), k -> new ArrayList<>()).add(c);
                for (Map.Entry<String, List<PCall>> e : by.entrySet()) {
                    LinkedHashMap<PField, String> k = new LinkedHashMap<>();
                    k.put(key, e.getKey());
                    groups.add(new Group(k, e.getValue(), false, e.getValue()));
                }
                if (o.explore && faithful != null && o.generate > 0)
                    for (String code : key.conditions.keySet())
                        if (!by.containsKey(code)) {
                            LinkedHashMap<PField, String> k = new LinkedHashMap<>();
                            k.put(key, code);
                            groups.add(new Group(k, List.of(), true, traced));
                        }
            }
            for (Group g : groups) {
                long t0 = System.nanoTime();
                if (late()) {
                    GroupResult skip = new GroupResult(g);
                    skip.note = "not probed: the time budget (--max-seconds " + o.maxSeconds + ") ran out";
                    report.groups.add(skip);
                    continue;
                }
                List<GroupResult> res = solveWithSplits(g);
                report.groups.addAll(res);
                System.err.printf(Locale.ROOT, "  %s: %d result(s) in %.1f s%n", g.label(), res.size(), (System.nanoTime() - t0) / 1e9);
            }
            consistentCodes();
        }

        /**
         * One constant per code for all CALLs of the program: each selector's code keeps only the constants every group
         * using that selector allows; a group whose choice changes is verified again.
         */
        void consistentCodes() {
            Map<String, Map<String, Long>> global = new HashMap<>();
            for (GroupResult r : report.groups) {
                if (r.model == null) continue;
                List<Model> ms = new ArrayList<>(r.model.extras);
                ms.add(0, r.model);
                for (Model m : ms) {
                    if (m.h.sel == null || m.h.enumKind == 0) continue;
                    Map<String, Long> g = global.computeIfAbsent(m.h.sel.name + "/" + m.h.enumKind, k -> new HashMap<>());
                    for (Map.Entry<String, Long> e : m.feas.entrySet()) g.merge(e.getKey(), e.getValue(), (a, b) -> a & b);
                }
            }
            for (GroupResult r : report.groups) {
                if (r.model == null) continue;
                List<Model> ms = new ArrayList<>(r.model.extras);
                ms.add(0, r.model);
                boolean changed = false;
                for (Model m : ms) {
                    if (m.h.sel == null || m.h.enumKind == 0) continue;
                    Map<String, Integer> before = new LinkedHashMap<>(m.pick);
                    pickCodes(m, global.get(m.h.sel.name + "/" + m.h.enumKind));
                    changed |= !before.equals(m.pick);
                }
                if (changed && faithful != null && o.generate > 0 && r.smp != null) {
                    System.err.println("  " + r.g.label() + ": codes chosen again (one constant per code across groups); verifying again");
                    verify(r, r.g, r.smp, r.seed);
                }
            }
        }

        // ---------------------------------------------------------------- field roles

        static final Pattern EXP_NAME = Pattern.compile("(.*?)-?(EXP|EXPONENT|SCALE|SHIFT)$");

        /** Decimal operands: coefficient + exponent (or scale) pairs by name, and numeric fields with decimals or many digits. */
        void findPairs() {
            Set<PField> coefOnly = new HashSet<>();
            for (PField e : p.fields) {
                if (!e.integer() || e.digits > 5) continue;
                Matcher m = EXP_NAME.matcher(e.name);
                if (!m.matches()) continue;
                String stem = m.group(1);
                boolean scaleField = m.group(2).equals("SCALE");
                for (PField c : p.fields) {
                    if (c == e || !c.integer() || c.digits < 10 || c.arg != e.arg) continue;
                    if (stem.isEmpty() || !c.name.startsWith(stem + "-")) continue;
                    String rest = c.name.substring(stem.length() + 1);
                    String label = rest.matches("COEF|COEFF|COEFFICIENT|MANT|MANTISSA|UNSCALED|UNSC") ? stem : c.name + "×10^" + e.name;
                    String ident = rest.matches("COEF|COEFF|COEFFICIENT|MANT|MANTISSA|UNSCALED|UNSC") ? camel(stem) : camel(c.name) + "Pair";
                    p.pairs.add(new Src(label, ident, c, e, scaleField));
                    p.pairFields.add(c);
                    p.pairFields.add(e);
                    if (label.equals(stem)) coefOnly.add(c);
                }
            }
            for (PField f : p.fields)
                if (f.numeric() && (f.scale > 0 || f.digits >= 10) && !coefOnly.contains(f)) p.singles.add(new Src(f.name, camel(f.name), f, null, false));
        }

        void findSelectors() {
            for (PField f : p.fields) {
                boolean shape = (!f.numeric() && f.len <= 4) || (f.integer() && f.digits <= 4 && !f.conditions.isEmpty());
                if (!shape) continue;
                Set<String> codes = new HashSet<>();
                for (PCall c : traced) {
                    codes.add(f.code(c.before()));
                    if (codes.size() > 16) break;
                }
                if (codes.size() <= 16) selectors.add(f);
            }
        }

        PField chooseKey() {
            PField best = null;
            int bestScore = -1;
            for (PField f : selectors) {
                if (modeLike(f) || unitLike(f)) continue;
                Set<String> codes = new HashSet<>();
                for (PCall c : traced) codes.add(f.code(c.before()));
                if (codes.size() < 2 && f.conditions.isEmpty()) continue;
                int score = 0;
                if (f.name.matches(".*(FUNCTION|FUNC|-FN|-OP|OPER|ACTION|REQUEST|CMD|VERB).*")) score += 100;
                for (String n : f.conditions.values()) if (n.matches(".*-(FN|OP|FUNC)-.*")) { score += 50; break; }
                if (!f.conditions.isEmpty()) score += 20;
                if (f.arg == 0) score += 5;
                score += Math.min(codes.size(), 4);
                if (score > bestScore) {
                    bestScore = score;
                    best = f;
                }
            }
            return best;
        }

        static RoundingMode modePrior(String name) {
            String n = name.toUpperCase(Locale.ROOT);
            if (n.endsWith("HALF-EVEN") || n.endsWith("HALFEVEN")) return RoundingMode.HALF_EVEN;
            if (n.endsWith("HALF-UP") || n.endsWith("HALFUP")) return RoundingMode.HALF_UP;
            if (n.endsWith("HALF-DOWN") || n.endsWith("HALFDOWN")) return RoundingMode.HALF_DOWN;
            if (n.endsWith("CEILING")) return RoundingMode.CEILING;
            if (n.endsWith("FLOOR")) return RoundingMode.FLOOR;
            if (n.endsWith("-UP") || n.equals("UP")) return RoundingMode.UP;
            if (n.endsWith("-DOWN") || n.equals("DOWN")) return RoundingMode.DOWN;
            return null;
        }

        static ChronoUnit unitPrior(String name) {
            String n = name.toUpperCase(Locale.ROOT);
            if (n.endsWith("DAYS")) return ChronoUnit.DAYS;
            if (n.endsWith("WEEKS")) return ChronoUnit.WEEKS;
            if (n.endsWith("MONTHS")) return ChronoUnit.MONTHS;
            if (n.endsWith("YEARS")) return ChronoUnit.YEARS;
            if (n.endsWith("DECADES")) return ChronoUnit.DECADES;
            return null;
        }

        static boolean modeLike(PField f) {
            int n = 0;
            for (String c : f.conditions.values()) if (modePrior(c) != null) n++;
            return n >= 2 && n * 2 >= f.conditions.size();
        }

        static boolean unitLike(PField f) {
            int n = 0;
            for (String c : f.conditions.values()) if (unitPrior(c) != null) n++;
            return n >= 2 && n * 2 >= f.conditions.size();
        }

        static boolean rcLike(PField f) {
            return f.numeric() && f.name.matches(".*(^|-)(RC|RETURN-CODE|RETCODE|RET-CODE|STATUS|STAT)$");
        }

        /** Day-number encodings: from CALLs where a YYYYMMDD field and a day-number field hold the same date, else the standard ones. */
        List<Epoch> solveEpochs() {
            List<PField> ymd = new ArrayList<>(), days = new ArrayList<>();
            for (PField f : p.fields) {
                if (f.integer() && f.digits == 8) ymd.add(f);
                if (f.integer() && f.digits >= 5 && f.digits <= 10) days.add(f);
            }
            if (ymd.isEmpty() || days.isEmpty()) return EquivalenceCatalog.STANDARD_EPOCHS;
            Map<Long, Integer> offsets = new HashMap<>();
            int pairs = 0;
            for (PCall c : traced)
                for (byte[][] a : new byte[][][]{c.before(), c.after()})
                    for (byte[][] b : new byte[][][]{c.before(), c.after()})
                        for (PField y : ymd)
                            for (PField d : days) {
                                if (y == d) continue;
                                BigDecimal yv = y.num(a), dv = d.num(b);
                                if (yv == null || dv == null) continue;
                                LocalDate date;
                                try {
                                    date = EquivalenceCatalog.ymd(yv.longValue());
                                } catch (Invalid x) {
                                    continue;
                                }
                                if (date.getYear() < 1000) continue;
                                pairs++;
                                offsets.merge(dv.longValue() - date.toEpochDay(), 1, Integer::sum);
                            }
            List<Epoch> out = new ArrayList<>();
            int top = offsets.values().stream().mapToInt(Integer::intValue).max().orElse(0);
            for (Map.Entry<Long, Integer> e : offsets.entrySet()) {
                if (e.getValue() < 3 || e.getValue() * 20 < pairs || e.getValue() * 2 < top) continue;
                Epoch known = null;
                for (Epoch s : EquivalenceCatalog.STANDARD_EPOCHS) if (s.offset() == e.getKey()) known = s;
                if (known == null) {
                    LocalDate dayOne = LocalDate.ofEpochDay(1 - e.getKey());
                    known = Epoch.of("day number (day 1 = " + dayOne + ", solved from the CALLs)", dayOne, dayOne, EquivalenceCatalog.MAX_DATE);
                }
                if (!out.contains(known)) out.add(known);
            }
            report.epochs = out.isEmpty() ? "standard (none solved from the CALLs)" : out.stream().map(Epoch::label).toList().toString();
            return out.isEmpty() ? EquivalenceCatalog.STANDARD_EPOCHS : out;
        }

        // ---------------------------------------------------------------- groups

        /**
         * A group's result; when it is not verified, the group split by each other selector (DK-STICKY, say) is tried and
         * kept when its verified parts cover more CALLs.
         */
        List<GroupResult> solveWithSplits(Group g) {
            GroupResult r = solve(g);
            if (r.verified() || (!g.generatedOnly && g.traced.size() < 2)) return List.of(r);
            List<GroupResult> best = List.of(r);
            long bestScore = 0;
            int keyArg = g.key.isEmpty() ? 0 : g.key.keySet().iterator().next().arg;
            for (PField s : selectors) {
                if (g.key.containsKey(s) || modeLike(s) || unitLike(s)) continue;
                boolean named = r.regions.stream().anyMatch(fr -> fr.words.stream().anyMatch(t -> t.startsWith(s.name + " = ")));
                if (s.arg != keyArg && !named) continue;
                List<PCall> src = g.generatedOnly ? g.background : g.traced;
                Map<String, List<PCall>> by = new TreeMap<>();
                for (PCall c : src) by.computeIfAbsent(s.code(c.before()), k -> new ArrayList<>()).add(c);
                if (by.size() < 2 || by.size() > 4) continue;
                // every part needs enough CALLs to stand on its own (a split into one-CALL parts explains nothing)
                if (!g.generatedOnly && !named && by.values().stream().anyMatch(l -> l.size() < 5)) continue;
                List<GroupResult> parts = new ArrayList<>();
                long score = 0;
                for (Map.Entry<String, List<PCall>> e : by.entrySet()) {
                    LinkedHashMap<PField, String> k = new LinkedHashMap<>(g.key);
                    k.put(s, e.getKey());
                    GroupResult pr = solve(new Group(k, g.generatedOnly ? List.of() : e.getValue(), g.generatedOnly, e.getValue()));
                    parts.add(pr);
                    if (pr.verified()) score += pr.g.traced.size() + pr.generated;
                }
                if (score > bestScore) {
                    bestScore = score;
                    best = parts;
                }
            }
            return best;
        }

        GroupResult solve(Group g) {
            GroupResult r = new GroupResult(g);
            long gseed = o.seed * 31 + g.label().hashCode();
            Samplers smp = new Samplers(g);
            List<PCall> learn = new ArrayList<>(), plain = new ArrayList<>();
            if (faithful != null && o.learn > 0) {
                Random rnd = new Random(gseed);
                for (int i = 0; i < o.learn; i++) {
                    PCall c = generate(g, smp, rnd, i % 2 == 1);
                    learn.add(c);
                    if (i % 2 == 0) plain.add(c);
                }
            }
            r.learning = learn.size();
            List<PCall> t = g.traced;
            List<PCall> s = new ArrayList<>(t);
            // the search sees the traced CALLs and, for a group with few of them, plain generated ones (exploring CALLs may
            // fall where a statement does not hold; they judge it afterwards)
            if (t.size() < 5) s.addAll(plain.subList(0, Math.min(300, plain.size())));
            if (s.isEmpty()) {
                r.note = "no CALLs to learn from";
                return r;
            }
            List<PCall> all = new ArrayList<>(t);
            all.addAll(learn);
            // The other fields' rules come from the traced CALLs when there are enough (generated CALLs may fall where the
            // statement does not hold, and are judged by it afterwards); otherwise from the learning CALLs as well.
            List<PCall> ruleCalls = t.size() >= 20 ? t : all;
            Set<PField> outputs = outputs(all);
            // Result fields: changed by some CALL and holding more than one value afterwards (a field the program always sets
            // to the same value is a side effect, learned as such).
            Set<PField> changing = new LinkedHashSet<>();
            for (PField f : outputs) {
                boolean changed = false;
                Set<String> values = new HashSet<>();
                for (PCall c : all) {
                    if (c.after() == null) continue;
                    if (!changed && f.changed(c.before(), c.after())) changed = true;
                    if (values.size() < 2) values.add(new String(c.after()[f.arg], f.off, f.len, StandardCharsets.ISO_8859_1));
                    if (changed && values.size() > 1) break;
                }
                if (changed && values.size() > 1) changing.add(f);
            }
            if (changing.size() > 12) {
                r.note = "out of scope: a CALL sets " + changing.size() + " fields (a record mapping, not a library operation with at most three results)";
                return r;
            }
            if (changing.isEmpty()) {
                r.note = "no output field takes more than one value on these CALLs (the inputs they depend on never vary in the traces), so no "
                         + "operation can be told apart";
                errorContract(r, g, smp, all, gseed);
                return r;
            }
            Shape shape = shape(g, s, changing);
            int generatedInS = s.size() - t.size();
            int budget = generatedInS == 0 ? 0 : Math.max(3, generatedInS / (t.isEmpty() ? 4 : 10));
            List<Cand> cands = new ArrayList<>();
            Map<String, List<PCall>> orderCache = new HashMap<>();
            Map<String, Integer> perTarget = new HashMap<>();
            EquivalenceCatalog.enumerate(shape, h -> {
                if (perTarget.getOrDefault(h.target.key(), 0) >= 60) return true;
                Cand c = check(h, s, budget, orderCache);
                if (c != null) {
                    cands.add(c);
                    perTarget.merge(h.target.key(), 1, Integer::sum);
                }
                return cands.size() < 400 && !late();
            });
            r.candidates = cands.size();
            if (cands.isEmpty()) {
                r.note = "no catalog candidate reproduces the result fields of the traced CALLs";
                errorContract(r, g, smp, all, gseed);
                return r;
            }
            for (Cand c : cands) refine(c, learn);
            cands.sort(Comparator.comparingInt((Cand c) -> -c.score).thenComparingInt(c -> c.h.inputs().size()).thenComparingInt(c -> c.h.rank));
            Model best = null;
            int bestL = -1;
            for (Cand c : cands.subList(0, Math.min(6, cands.size()))) {
                Model m = finalizeModel(c);
                if (degenerate(m, all)) {
                    if (r.note == null) r.note = "the only candidates give the same result on every CALL (the inputs that would tell them apart never vary)";
                    continue;
                }
                learnRules(m, ruleCalls, all, outputs);
                int tOk = 0;
                for (PCall x : t) if (matches(m, x)) tOk++;
                // A program that returns more than one result: other result fields explained by other candidates.
                for (int extra = 0; extra < 2 && tOk < t.size(); extra++) {
                    Set<PField> bad = mismatched(m, t);
                    Cand x = null;
                    Model mm = m;
                    for (Cand o2 : cands) {
                        if (o2 == c || mm.extras.stream().anyMatch(e -> e.h == o2.h)) continue;
                        List<PField> tf = o2.h.target.fields();
                        if (tf.stream().noneMatch(bad::contains) || tf.stream().anyMatch(f -> mm.allTargets().contains(f))) continue;
                        x = o2;
                        break;
                    }
                    if (x == null) break;
                    Model added = finalizeModel(x);
                    m.extras.add(added);
                    learnRules(m, ruleCalls, all, outputs);
                    int before = tOk;
                    tOk = 0;
                    for (PCall y : t) if (matches(m, y)) tOk++;
                    if (tOk <= before) {
                        // a further result must explain traced CALLs the statement alone did not
                        m.extras.remove(added);
                        learnRules(m, ruleCalls, all, outputs);
                        tOk = before;
                        break;
                    }
                }
                if (tOk < t.size()) {
                    if (r.note == null) r.note = "best candidate " + c.h.ex.java() + " matched " + tOk + " of " + t.size() + " traced CALLs in every field";
                    continue;
                }
                int lOk = 0;
                for (PCall x : learn) if (matches(m, x)) lOk++;
                // With several results, the main one is the operation with the most inputs (it carries the exceptions).
                Model lead = m.extras.stream().max(Comparator.comparingInt((Model e) -> e.h.inputs().size())).orElse(null);
                if (lead != null && lead.h.inputs().size() > m.h.inputs().size()) {
                    Model sw = new Model(lead.h);
                    sw.feas.putAll(lead.feas);
                    sw.pick.putAll(lead.pick);
                    sw.ambiguous.putAll(lead.ambiguous);
                    Model old = new Model(m.h);
                    old.feas.putAll(m.feas);
                    old.pick.putAll(m.pick);
                    old.ambiguous.putAll(m.ambiguous);
                    sw.extras.add(old);
                    for (Model e : m.extras) if (e != lead) sw.extras.add(e);
                    learnRules(sw, ruleCalls, all, outputs);
                    int sT = 0, sL = 0;
                    for (PCall y : t) if (matches(sw, y)) sT++;
                    for (PCall y : learn) if (matches(sw, y)) sL++;
                    if (sT == t.size() && sL >= lOk) {
                        m = sw;
                        lOk = sL;
                    }
                }
                m.tracedOk = tOk;
                m.learnOk = lOk;
                if (lOk > bestL) {
                    bestL = lOk;
                    best = m;
                }
            }
            if (best == null) {
                errorContract(r, g, smp, all, gseed);
                return r;
            }
            // A statement needs inputs that vary: with few distinct input combinations any candidate fits.
            Set<String> tuples = new HashSet<>();
            for (PCall x : all) {
                StringBuilder sb = new StringBuilder();
                for (PField f : best.allInputs()) sb.append(new String(x.before()[f.arg], f.off, f.len, StandardCharsets.ISO_8859_1)).append('|');
                tuples.add(sb.toString());
                if (tuples.size() >= 1000) break;
            }
            r.distinctInputs = tuples.size();
            if (tuples.size() < 5) {
                r.note = "untestable: the inputs of the best candidate (" + best.h.ex.java() + ") take only " + tuples.size()
                         + " distinct combinations on these CALLs";
                return r;
            }
            r.model = best;
            r.note = null;
            r.alternatives = (int) cands.stream().filter(c -> c.score == cands.get(0).score).count() - 1;
            r.smp = smp;
            r.seed = gseed ^ 0x5DEECE66DL;
            if (faithful != null && o.generate > 0) {
                disambiguate(best, g, smp, gseed + 7);
                verify(r, g, smp, r.seed);
            }
            return r;
        }

        /**
         * A group with no library operation whose every CALL ends with a nonzero return code (an error path): its effects
         * as a contract (unchanged, constant, copied from an argument field), learned on the traced and learning CALLs and
         * verified on the generated ones.
         */
        void errorContract(GroupResult r, Group g, Samplers smp, List<PCall> all, long seed) {
            List<PField> rcs = p.fields.stream().filter(Probe::rcLike).toList();
            if (rcs.isEmpty() || all.isEmpty()) return;
            for (PCall c : all) {
                if (c.after() == null) return;
                boolean nonzero = false;
                for (PField f : rcs) {
                    BigDecimal v = f.num(c.after());
                    if (v != null && v.signum() != 0) nonzero = true;
                }
                if (!nonzero) return;
            }
            Contract k = contract(null, all, g.traced.size());
            if (!k.varies.isEmpty() || k.identical != all.size()) return;
            if (faithful != null && o.generate > 0) {
                Random rnd = new Random(seed ^ 0x5DEECE66DL);
                for (int i = 0; i < o.generate; i++) {
                    if ((i & 1023) == 1023 && late()) break;
                    PCall c = generate(g, smp, rnd, i % 2 == 1);
                    r.generated++;
                    if (c.after() == null) continue;
                    byte[][] img = copy(c.before());
                    for (Map.Entry<PField, Rule> e : k.rules.entrySet()) e.getValue().apply(e.getKey(), c.before(), img);
                    if (Arrays.deepEquals(img, c.after())) r.generatedOk++;
                }
            }
            r.errorContract = k;
            r.note = null;
        }

        /** Fields that differ from the faithful program on the CALLs the model does not reproduce. */
        Set<PField> mismatched(Model m, List<PCall> calls) {
            Set<PField> bad = new LinkedHashSet<>();
            for (PCall c : calls) {
                if (c.after() == null) continue;
                byte[][] img = predict(m, c);
                if (img == null) continue;
                for (PField f : p.fields) if (f.changed(img, c.after())) bad.add(f);
                if (bad.size() > 8) break;
            }
            return bad;
        }

        /** A model whose result is the same on every CALL says nothing (the inputs that would distinguish never vary). */
        boolean degenerate(Model m, List<PCall> calls) {
            if (calls.size() < 3) return false;
            Set<String> seen = new HashSet<>();
            for (PCall c : calls) {
                if (c.after() == null) continue;
                StringBuilder sb = new StringBuilder();
                for (PField f : m.h.target.fields())
                    sb.append(new String(c.after()[f.arg], f.off, f.len, StandardCharsets.ISO_8859_1)).append('|');
                seen.add(sb.toString());
                if (seen.size() > 1) return false;
            }
            return true;
        }

        /** Fields any CALL changed, and return-code fields. */
        Set<PField> outputs(List<PCall> calls) {
            Set<PField> out = new LinkedHashSet<>();
            for (PField f : p.fields) {
                if (rcLike(f)) {
                    out.add(f);
                    continue;
                }
                for (PCall c : calls)
                    if (c.after() != null && f.changed(c.before(), c.after())) {
                        out.add(f);
                        break;
                    }
            }
            return out;
        }

        Shape shape(Group g, List<PCall> s, Set<PField> outputs) {
            Shape sh = new Shape();
            for (Src x : p.pairs) if (validShare(x, s) >= 0.5) sh.srcs.add(x);
            for (Src x : p.singles) if (validShare(x, s) >= 0.5) sh.srcs.add(x);
            for (PField f : p.fields) {
                if (!f.integer() || f.digits > 9 || g.key.containsKey(f)) continue;
                long lo = Long.MAX_VALUE, hi = Long.MIN_VALUE;
                int valid = 0;
                for (PCall c : s) {
                    BigDecimal v = f.num(c.before());
                    if (v == null) continue;
                    valid++;
                    lo = Math.min(lo, v.longValue());
                    hi = Math.max(hi, v.longValue());
                }
                if (valid * 2 < s.size()) continue;
                IntP ip = new IntP(f);
                sh.counts.add(ip);
                if (p.pairFields.contains(f) || selectors.contains(f)) continue;
                if (lo >= 0 && hi <= 99) sh.precs.add(ip);
                if (lo >= -99 && hi <= 99) sh.params.add(ip);
            }
            List<PField> modes = new ArrayList<>(), units = new ArrayList<>();
            for (PField f : selectors) {
                if (g.key.containsKey(f)) continue;
                if (modeLike(f)) modes.add(f);
                if (unitLike(f)) units.add(f);
            }
            if (modes.isEmpty())
                for (PField f : selectors) if (!g.key.containsKey(f) && !unitLike(f) && f.len == 1 && !f.numeric()) modes.add(f);
            if (units.isEmpty())
                for (PField f : selectors) if (!g.key.containsKey(f) && !modeLike(f) && f.len == 1 && !f.numeric()) units.add(f);
            sh.modeSels.addAll(modes);
            sh.modeSels.add(null);
            sh.unitSels.addAll(units);
            sh.unitSels.add(null);
            for (Src x : p.pairs) {
                if (!outputs.contains(x.coef) && !outputs.contains(x.exp)) continue;
                sh.decTargets.add(new EquivalenceCatalog.PairT(x.coef, x.exp, x.scaleField, EquivalenceCatalog.STRIPPED, null, null));
                sh.decTargets.add(new EquivalenceCatalog.PairT(x.coef, x.exp, x.scaleField, EquivalenceCatalog.EXACT, null, null));
                sh.pairTargets.add(new PField[]{x.coef, x.exp});
                sh.pairScale.add(x.scaleField);
            }
            for (PField f : outputs) {
                if (!f.numeric()) continue;
                if (!rcLike(f) || f.digits > 4) sh.decTargets.add(new EquivalenceCatalog.NumT(f));
                if (f.integer() && f.digits <= 18) sh.intTargets.add(new EquivalenceCatalog.NumT(f));
            }
            for (PField f : p.fields) {
                if (!f.integer()) continue;
                if (f.digits == 8) {
                    DSrc d = new DSrc(f, null);
                    if (dateShare(d, s) >= 0.5) sh.dsrcs.add(d);
                    if (outputs.contains(f)) sh.dateTargets.add(new EquivalenceCatalog.YmdT(f));
                }
                if (f.digits >= 5 && f.digits <= 10)
                    for (Epoch e : epochs) {
                        DSrc d = new DSrc(f, e);
                        if (dateShare(d, s) >= 0.5) sh.dsrcs.add(d);
                        if (outputs.contains(f)) sh.dateTargets.add(new EquivalenceCatalog.DayT(f, e));
                    }
            }
            return sh;
        }

        double validShare(Src x, List<PCall> s) {
            int ok = 0;
            for (PCall c : s) {
                Env e = new Env(c.before(), p.fields.size());
                try {
                    x.value(e);
                    ok++;
                } catch (RuntimeException ignored) {
                    // invalid
                }
            }
            return s.isEmpty() ? 0 : ok / (double) s.size();
        }

        double dateShare(DSrc d, List<PCall> s) {
            int ok = 0;
            for (PCall c : s) {
                Env e = new Env(c.before(), p.fields.size());
                try {
                    d.value(e);
                    ok++;
                } catch (RuntimeException ignored) {
                    // not a date
                }
            }
            return s.isEmpty() ? 0 : ok / (double) s.size();
        }

        // ---------------------------------------------------------------- evaluation

        record Outcome(String kind, byte[][] img) {}

        Outcome evaluate(Hyp h, Env env, Object k) {
            env.mode = k instanceof RoundingMode m ? m : null;
            env.unit = k instanceof ChronoUnit u ? u : null;
            byte[][] img = copy(env.args);
            try {
                Object r = h.ex.eval(env);
                if (!h.target.encode(r, env, img)) return new Outcome("the result does not fit " + fieldNames(h.target.fields()), copy(env.args));
                return new Outcome("", img);
            } catch (Invalid x) {
                return new Outcome(x.getMessage(), copy(env.args));
            } catch (ArithmeticException x) {
                // 0/0 ("Division undefined") and n/0 ("Division by zero") are the same condition: a zero divisor
                String msg = "Division undefined".equals(x.getMessage()) ? "Division by zero" : x.getMessage();
                return new Outcome("ArithmeticException: " + msg, copy(env.args));
            } catch (DateTimeException x) {
                return new Outcome("DateTimeException", copy(env.args));
            } catch (IllegalArgumentException x) {
                return new Outcome("IllegalArgumentException", copy(env.args));
            }
        }

        static boolean targetMatches(Hyp h, byte[][] img, PCall c) {
            if (c.after() == null) return false;
            for (PField f : h.target.fields()) if (f.changed(img, c.after())) return false;
            return true;
        }

        String code(Hyp h, byte[][] before) { return h.enumKind == 0 || h.sel == null ? "" : h.sel.code(before); }

        /** CALLs whose target fields changed first (they decide), traced before generated. */
        List<PCall> order(Hyp h, List<PCall> calls, Map<String, List<PCall>> cache) {
            return cache.computeIfAbsent(h.target.key(), k -> {
                List<PCall> l = new ArrayList<>(calls);
                l.sort(Comparator.comparingInt((PCall c) -> c.generated() ? 1 : 0).thenComparingInt(c -> {
                    if (c.after() == null) return 2;
                    for (PField f : h.target.fields()) if (f.changed(c.before(), c.after())) return 0;
                    return 1;
                }));
                return l;
            });
        }

        static final class Cand {
            final Hyp h;
            final Map<String, Long> feas;
            int score;

            Cand(Hyp h, Map<String, Long> feas) {
                this.h = h;
                this.feas = feas;
            }
        }

        /** The result fields over the CALLs: traced CALLs must all agree; generated ones may miss within the budget. */
        Cand check(Hyp h, List<PCall> calls, int budget, Map<String, List<PCall>> cache) {
            Object[] ks = h.constants();
            long all = (1L << ks.length) - 1;
            Map<String, Long> feas = new HashMap<>();
            int normal = 0, n = 0, misses = 0;
            for (PCall c : order(h, calls, cache)) {
                String code = code(h, c.before());
                long fs = feas.getOrDefault(code, all);
                long ok = 0;
                boolean anyNormal = false;
                Env env = new Env(c.before(), p.fields.size());
                for (int i = 0; i < ks.length; i++) {
                    if ((fs & (1L << i)) == 0) continue;
                    Outcome out = evaluate(h, env, ks[i]);
                    if (out.kind().isEmpty()) {
                        if (targetMatches(h, out.img(), c)) {
                            ok |= 1L << i;
                            anyNormal = true;
                        }
                    } else ok |= 1L << i;
                }
                if (ok == 0) {
                    if (!c.generated() || ++misses > budget) return null;
                    continue;
                }
                feas.put(code, ok);
                n++;
                if (anyNormal) normal++;
            }
            if (normal == 0 || normal * 2 < n) return null;
            return new Cand(h, feas);
        }

        /** Scores a candidate on the learning CALLs (those that change its result fields) and narrows each code's constants. */
        void refine(Cand c, List<PCall> calls) {
            Object[] ks = c.h.constants();
            long all = (1L << ks.length) - 1;
            int score = 0;
            for (PCall x : calls) {
                String code = code(c.h, x.before());
                long fs = c.feas.getOrDefault(code, all);
                long ok = 0;
                Env env = new Env(x.before(), p.fields.size());
                for (int i = 0; i < ks.length; i++) {
                    if ((fs & (1L << i)) == 0) continue;
                    Outcome out = evaluate(c.h, env, ks[i]);
                    if (out.kind().isEmpty() ? targetMatches(c.h, out.img(), x) : true) ok |= 1L << i;
                }
                if (ok == 0) continue;
                c.feas.put(code, ok);
                // only CALLs that change the result count: a candidate that agrees where the field stays 0 says nothing
                if (x.after() != null && c.h.target.fields().stream().anyMatch(f -> f.changed(x.before(), x.after()))) score++;
            }
            c.score = score;
        }

        Model finalizeModel(Cand c) {
            Model m = new Model(c.h);
            if (c.h.enumKind == 0) {
                m.pick.put("", 0);
                return m;
            }
            m.feas.putAll(c.feas);
            pickCodes(m, null);
            return m;
        }

        /**
         * Each code's constant: among those the CALLs allow (narrowed by the other groups' when given), the one the
         * 88-level name says, else the first; codes still allowing several are recorded as indistinguishable.
         */
        void pickCodes(Model m, Map<String, Long> global) {
            Object[] ks = m.ks;
            m.pick.clear();
            m.ambiguous.clear();
            for (Map.Entry<String, Long> e : new TreeMap<>(m.feas).entrySet()) {
                long bits = e.getValue();
                if (global != null && global.containsKey(e.getKey()) && (bits & global.get(e.getKey())) != 0) bits &= global.get(e.getKey());
                int pick = Long.numberOfTrailingZeros(bits);
                if (m.h.sel != null && m.h.sel.conditions.get(e.getKey()) instanceof String name) {
                    Object prior = m.h.enumKind == 'M' ? modePrior(name) : unitPrior(name);
                    for (int i = 0; i < ks.length; i++) if (ks[i] == prior && (bits & (1L << i)) != 0) pick = i;
                }
                m.pick.put(e.getKey(), pick);
                if (Long.bitCount(bits) > 1) {
                    List<Object> alts = new ArrayList<>();
                    for (int i = 0; i < ks.length; i++) if ((bits & (1L << i)) != 0) alts.add(ks[i]);
                    m.ambiguous.put(e.getKey(), alts);
                }
            }
        }

        /** While a code still allows several constants, more generated CALLs (up to 20,000) narrow it. */
        void disambiguate(Model m, Group g, Samplers smp, long seed) {
            if (m.ambiguous.isEmpty()) return;
            Random rnd = new Random(seed);
            Object[] ks = m.ks;
            for (int batch = 0; batch < 10 && !m.ambiguous.isEmpty(); batch++) {
                for (int i = 0; i < 2000; i++) {
                    PCall x = generate(g, smp, rnd, i % 2 == 1);
                    String code = code(m.h, x.before());
                    Long fs = m.feas.get(code);
                    if (fs == null || Long.bitCount(fs) < 2) continue;
                    long ok = 0;
                    Env env = new Env(x.before(), p.fields.size());
                    for (int k = 0; k < ks.length; k++) {
                        if ((fs & (1L << k)) == 0) continue;
                        Outcome out = evaluate(m.h, env, ks[k]);
                        if (out.kind().isEmpty() ? targetMatches(m.h, out.img(), x) : true) ok |= 1L << k;
                    }
                    if ((fs & ok) != 0) m.feas.put(code, fs & ok);
                }
                pickCodes(m, null);
            }
        }

        /** Learns, per outcome (the operation's result, or each exception it throws), what every other output field does. */
        void learnRules(Model m, List<PCall> calls, Set<PField> outputs) { learnRules(m, calls, calls, outputs); }

        /** The normal outcome's rules from {@code normal}; each exception's (and abend's) from all {@code calls}. */
        void learnRules(Model m, List<PCall> normal, List<PCall> calls, Set<PField> outputs) {
            Set<PCall> normalSet = normal == calls ? null : Collections.newSetFromMap(new java.util.IdentityHashMap<>());
            if (normalSet != null) normalSet.addAll(normal);
            m.rules.clear();
            m.abends.clear();
            m.kinds.clear();
            Map<String, List<Obs>> obs = new LinkedHashMap<>();
            Map<String, List<String>> abends = new LinkedHashMap<>();
            List<Obs> alt = new ArrayList<>();
            List<byte[][]> sample = new ArrayList<>();
            for (PCall c : calls) {
                Integer ki = m.pick.get(code(m.h, c.before()));
                if (ki == null) continue;
                Outcome out = evaluate(m.h, new Env(c.before(), p.fields.size()), m.ks[ki]);
                m.kinds.merge(out.kind(), 1, Integer::sum);
                if (c.after() == null) {
                    abends.computeIfAbsent(out.kind(), k -> new ArrayList<>()).add(c.abend());
                    continue;
                }
                if (out.kind().isEmpty() && !targetMatches(m.h, out.img(), c)) continue;
                if (out.kind().isEmpty()) applyExtras(m, c.before(), out.img());
                if (out.kind().isEmpty() && normalSet != null && !normalSet.contains(c)) {
                    alt.add(new Obs(c.before(), out.img(), c.after()));
                    continue;
                }
                obs.computeIfAbsent(out.kind(), k -> new ArrayList<>()).add(new Obs(c.before(), out.img(), c.after()));
                if (sample.size() < 64) sample.add(c.before());
            }
            List<Pred> preds = EquivalenceRules.predicates(p.fields, sample);
            Set<String> kinds = new LinkedHashSet<>(obs.keySet());
            kinds.addAll(abends.keySet());
            for (String k : kinds) {
                List<Obs> os = obs.getOrDefault(k, List.of());
                List<String> ab = abends.getOrDefault(k, List.of());
                if (!k.isEmpty() && ab.size() > os.size() && ab.stream().distinct().count() == 1) {
                    m.abends.put(k, ab.get(0));
                    continue;
                }
                if (os.isEmpty()) continue;
                Map<PField, Rule> rules = new LinkedHashMap<>();
                List<PField> hard = new ArrayList<>();
                for (PField f : outputs) {
                    if (k.isEmpty() && m.allTargets().contains(f)) continue;
                    Learned l = k.isEmpty() ? EquivalenceRules.choose(f, os, alt, p.fields, rcLike(f)) : EquivalenceRules.simple(f, os, p.fields, rcLike(f));
                    rules.put(f, l.rule());
                    if (l.agree() < os.size()) hard.add(f);
                }
                // Conditional rules (a swap of operands, say) only for a few fields: a candidate that leaves many fields
                // unexplained is wrong, and searching conditions for it would only cost time.
                if (!hard.isEmpty() && hard.size() <= 4 && os.size() >= 4) {
                    boolean[][] vals = EquivalenceRules.predicateValues(preds, os, p.fields);
                    for (PField f : hard) {
                        Learned l = EquivalenceRules.learn(f, os, p.fields, preds, vals, rcLike(f));
                        if (l.agree() == os.size()) rules.put(f, l.rule());
                    }
                }
                m.rules.put(k, rules);
            }
        }

        /** The whole CALL: every byte of every argument after it, or the same abend. */
        boolean matches(Model m, PCall c) {
            Integer ki = m.pick.get(code(m.h, c.before()));
            if (ki == null) return false;
            Outcome out = evaluate(m.h, new Env(c.before(), p.fields.size()), m.ks[ki]);
            String ab = m.abends.get(out.kind());
            if (ab != null) return ab.equals(c.abend());
            if (c.after() == null) return false;
            byte[][] img = predict(m, c, out);
            if (img == null) return false;
            for (int i = 0; i < img.length; i++) if (!Arrays.equals(img[i], c.after()[i])) return false;
            return true;
        }

        byte[][] predict(Model m, PCall c) {
            Integer ki = m.pick.get(code(m.h, c.before()));
            if (ki == null) return null;
            return predict(m, c, evaluate(m.h, new Env(c.before(), p.fields.size()), m.ks[ki]));
        }

        /** The arguments after the CALL as the model has them: the result(s), then every other output field's rule. */
        byte[][] predict(Model m, PCall c, Outcome out) {
            Map<PField, Rule> rules = m.rules.get(out.kind());
            if (rules == null || m.abends.containsKey(out.kind())) return null;
            byte[][] img = out.img();
            if (out.kind().isEmpty()) applyExtras(m, c.before(), img);
            for (Map.Entry<PField, Rule> e : rules.entrySet()) e.getValue().apply(e.getKey(), c.before(), img);
            return img;
        }

        /** Further results: each written where its own operation returns normally. */
        void applyExtras(Model m, byte[][] before, byte[][] img) {
            for (Model e : m.extras) {
                Integer k = e.pick.get(code(e.h, before));
                if (k == null) continue;
                Outcome eo = evaluate(e.h, new Env(before, p.fields.size()), e.ks[k]);
                if (!eo.kind().isEmpty()) continue;
                for (PField f : e.h.target.fields()) System.arraycopy(eo.img()[f.arg], f.off, img[f.arg], f.off, f.len);
            }
        }

        // ---------------------------------------------------------------- generated CALLs

        /** Per field, values drawn from what the traced CALLs passed (codes, ranges, digit counts, signs, ties and carries). */
        final class Samplers {
            final List<Sampler> list = new ArrayList<>();
            final List<PCall> background;

            Samplers(Group g) { this(g, false); }

            /** wide: text inputs drawn from every traced CALL of the program, not only the group's. */
            Samplers(Group g, boolean wide) {
                background = g.background.isEmpty() ? traced : g.background;
                for (PField f : p.fields) {
                    if (g.key.containsKey(f)) continue;
                    Sampler s = new Sampler(f, wide && !f.numeric() ? traced : background, (modeLike(f) || unitLike(f)) ? f.conditions.keySet() : Set.of(),
                        explorable(f) ? trimmedCodes(f) : Set.of(), explorable(f));
                    if (s.varies()) list.add(s);
                }
            }
        }

        Set<PField> tracedOutputs;

        /**
         * Inputs explore their wider domain; a field the program writes (changed in some traced CALL) or a return-code or
         * status field is a result, whose value passed in only stands for an earlier CALL's leftovers.
         */
        boolean explorable(PField f) {
            if (rcLike(f)) return false;
            if (tracedOutputs == null) {
                tracedOutputs = new HashSet<>();
                for (PField x : p.fields)
                    for (PCall c : traced)
                        if (x.changed(c.before(), c.after())) {
                            tracedOutputs.add(x);
                            break;
                        }
            }
            return !tracedOutputs.contains(f);
        }

        /** The 88-level values of a field as codes (text ones as written, without the padding). */
        static Set<String> trimmedCodes(PField f) {
            Set<String> out = new LinkedHashSet<>();
            for (String c : f.conditions.keySet()) out.add(f.numeric() ? c : c.replaceAll(" +$", ""));
            return out;
        }

        PCall generate(Group g, Samplers s, Random r) { return generate(g, s, r, false); }

        /** A generated CALL: a traced CALL's arguments with the varying inputs drawn again (exploring: from the wider domains). */
        PCall generate(Group g, Samplers s, Random r, boolean exploring) {
            byte[][] b = copy(s.background.get(r.nextInt(s.background.size())).before());
            for (Sampler x : s.list) x.sample(r, b, exploring);
            for (Map.Entry<PField, String> e : g.key.entrySet()) e.getKey().storeCode(b, e.getValue());
            return runFaithful(b);
        }

        PCall runFaithful(byte[][] before) {
            byte[][] params = copy(before);
            try {
                faithful.call(params);
                return new PCall("generated", ++seq, before, params, null, true);
            } catch (CobRt.Abend a) {
                faithful.reset();
                return new PCall("generated", ++seq, before, null, "ABEND " + a.code, true);
            } catch (CobRt.StopRun s) {
                faithful.reset();
                return new PCall("generated", ++seq, before, null, "STOP RUN", true);
            } catch (RuntimeException e) {
                faithful.reset();
                return new PCall("generated", ++seq, before, null, "ERROR " + e.getClass().getSimpleName(), true);
            }
        }

        // ---------------------------------------------------------------- verification

        void verify(GroupResult r, Group g, Samplers smp, long seed) {
            Model m = r.model;
            r.generated = 0;
            r.generatedOk = 0;
            r.regions.clear();
            r.unclustered = 0;
            r.outside = List.of();
            r.examples.clear();
            Random rnd = new Random(seed);
            List<Feature> feats = features(m);
            List<long[]> fail = new ArrayList<>(), ok = new ArrayList<>();
            List<PCall> failCalls = new ArrayList<>(), okCalls = new ArrayList<>();
            int okSeen = 0;
            Random res = new Random(seed + 1);
            Domain dom = new Domain(m);
            for (int i = 0; i < o.generate; i++) {
                if ((i & 1023) == 1023 && late()) break;
                PCall c = generate(g, smp, rnd, i % 2 == 1);
                r.generated++;
                dom.add(c.before());
                if (matches(m, c)) {
                    r.generatedOk++;
                    okSeen++;
                    if (ok.size() < 4000) {
                        ok.add(featureValues(feats, c));
                        okCalls.add(c);
                    } else {
                        int j = res.nextInt(okSeen);
                        if (j < ok.size()) {
                            ok.set(j, featureValues(feats, c));
                            okCalls.set(j, c);
                        }
                    }
                } else if (fail.size() < 2000) {
                    fail.add(featureValues(feats, c));
                    failCalls.add(c);
                }
            }
            r.checkedInputs = dom.describe();
            if (fail.isEmpty()) return;
            r.outside = outcomeOf(failCalls, m);
            for (PCall c : failCalls.subList(0, Math.min(3, failCalls.size()))) r.examples.add(example(m, c));
            boolean[] categorical = new boolean[feats.size()];
            int[] rank = new int[feats.size()];
            for (int i = 0; i < categorical.length; i++) {
                categorical[i] = feats.get(i).codes() != null;
                rank[i] = feats.get(i).rank();
            }
            // The failures, grouped by what the faithful program did instead (an abend, or which fields differ and the return
            // codes): each group gets its own region of the inputs and its own contract.
            Map<String, List<Integer>> clusters = new LinkedHashMap<>();
            for (int i = 0; i < failCalls.size(); i++) clusters.computeIfAbsent(signature(m, failCalls.get(i)), k -> new ArrayList<>()).add(i);
            // An abend ends the CALL before anything else it would have done, so abend regions come first; a CALL belongs to
            // the first region whose terms it meets.
            List<String> keys = new ArrayList<>(clusters.keySet());
            keys.sort(Comparator.comparingInt((String k) -> k.startsWith("abend") ? 0 : 1).thenComparingInt(k -> -clusters.get(k).size()));
            for (String key : keys) {
                List<Integer> cl = clusters.get(key);
                if (cl.size() < 3 || r.regions.size() >= 4) {
                    r.unclustered += cl.size();
                    continue;
                }
                List<long[]> mine = new ArrayList<>();
                for (int i : cl) mine.add(fail.get(i));
                EquivalenceRules.Region reg = EquivalenceRules.region(mine, ok, feats.size(), categorical, rank);
                // the shortest prefix of the terms whose contract explains every CALL in it (failing or not)
                List<EquivalenceRules.Term> chosen = reg.terms();
                for (int j = 1; j < reg.terms().size(); j++) {
                    FailRegion probe = new FailRegion();
                    probe.terms.addAll(reg.terms().subList(0, j));
                    List<PCall> calls = new ArrayList<>();
                    for (int i : cl) calls.add(failCalls.get(i));
                    for (PCall c : okCalls) if (inside(probe, feats, c.before()) && earlier(r.regions, feats, c.before()) == null) calls.add(c);
                    if (contract(m, calls, 0).holds()) {
                        chosen = probe.terms;
                        break;
                    }
                }
                FailRegion fr = new FailRegion();
                fr.terms.addAll(chosen);
                List<EquivalenceRules.Term> sorted = new ArrayList<>(chosen);
                sorted.sort(Comparator.comparingInt(t -> feats.get(t.feature()).rank()));
                for (EquivalenceRules.Term t : sorted) {
                    Feature f = feats.get(t.feature());
                    if (f.codes() != null) fr.words.add(f.name() + " = " + renderCode(p.field(f.name()), f.codes().get((int) t.lo())));
                    else fr.words.add(EquivalenceRules.describe(t, f.name()));
                }
                fr.failures = cl.size();
                // (nearly) exact: at most 2% of the sampled CALLs inside it are ones the statement reproduces (by coincidence)
                int okIn = EquivalenceRules.inside(chosen, ok);
                fr.exact = okIn * 50 <= cl.size() + okIn;
                for (int i : cl.subList(0, Math.min(2, cl.size()))) fr.examples.add(example(m, failCalls.get(i)));
                for (int i : cl) fr.calls.add(failCalls.get(i));
                r.regions.add(fr);
            }
            // Each region's contract: learned and checked on the CALLs inside it (by its terms), the failing ones and more
            // generated with the text inputs of the whole program (so that a copied input is told apart from a constant).
            if (r.regions.isEmpty()) return;
            for (PCall c : g.traced)
                if (earlier(r.regions, feats, c.before()) instanceof FailRegion fr) {
                    fr.calls.add(c);
                    fr.traced++;
                }
            for (PCall c : okCalls)
                if (earlier(r.regions, feats, c.before()) instanceof FailRegion fr) fr.calls.add(c);
            Samplers wide = new Samplers(g, true);
            Random wr = new Random(seed + 2);
            for (int i = 0; i < 300_000; i++) {
                if ((i & 1023) == 1023 && late()) break;
                if (r.regions.stream().allMatch(fr -> fr.calls.size() >= fr.failures + 1_000)) break;
                PCall c = generate(g, wide, wr, i % 2 == 1);
                if (earlier(r.regions, feats, c.before()) instanceof FailRegion fr && fr.calls.size() < fr.failures + 1_000) fr.calls.add(c);
            }
            for (FailRegion fr : r.regions) {
                fr.contract = contract(m, fr.calls, fr.traced);
                fr.calls.clear();
            }
        }

        /** The first of the regions the CALL lies in, or null. */
        static FailRegion earlier(List<FailRegion> regions, List<Feature> feats, byte[][] before) {
            for (FailRegion fr : regions) if (inside(fr, feats, before)) return fr;
            return null;
        }

        /** Does the CALL lie in the region (every term holds for its inputs)? */
        static boolean inside(FailRegion fr, List<Feature> feats, byte[][] before) {
            for (EquivalenceRules.Term t : fr.terms)
                if (!t.holds(feats.get(t.feature()).f().applyAsLong(before))) return false;
            return true;
        }

        /**
         * What the faithful program reported instead of the statement's outcome: its abend, or its return and status codes
         * (the fields that differ are not used: a result that happens to equal the value passed in would split one behaviour).
         */
        String signature(Model m, PCall c) {
            if (c.after() == null) return "abend " + c.abend();
            StringBuilder sb = new StringBuilder("codes ");
            for (PField f : p.fields)
                if (rcLike(f)) sb.append(f.name).append('=').append(f.code(c.after())).append(';');
            return sb.toString();
        }

        /** The CALL's arguments as the verified statement leaves them (its result fields written), or null when it throws. */
        byte[][] statementImage(Model m, byte[][] before) {
            Integer ki = m.pick.get(code(m.h, before));
            if (ki == null) return null;
            Outcome out = evaluate(m.h, new Env(before, p.fields.size()), m.ks[ki]);
            if (!out.kind().isEmpty()) return null;
            byte[][] img = out.img();
            applyExtras(m, before, img);
            return img;
        }

        /**
         * The effects of CALLs a statement does not cover, field by field: unchanged, a constant, the statement's value, or
         * a copy of an argument field as the caller passed it; each relation must hold on every CALL given, and the
         * whole argument images are compared.
         */
        Contract contract(Model m, List<PCall> calls, int traced) {
            Contract k = new Contract();
            k.calls = calls.size();
            k.traced = traced;
            List<String> abends = calls.stream().filter(c -> c.after() == null).map(PCall::abend).distinct().toList();
            List<PCall> done = calls.stream().filter(c -> c.after() != null).toList();
            k.abends = calls.size() - done.size();
            if (!abends.isEmpty()) k.abend = String.join(", ", abends);
            if (done.isEmpty()) return k;
            java.util.function.Function<byte[][], byte[][]> image = m == null ? b -> null : b -> statementImage(m, b);
            List<byte[][]> stated = new ArrayList<>();
            for (PCall c : done) stated.add(image.apply(c.before()));
            for (PField f : p.fields) {
                boolean keep = true, fromStatement = m != null, constant = true;
                byte[] first = Arrays.copyOfRange(done.get(0).after()[f.arg], f.off, f.off + f.len);
                for (int i = 0; i < done.size(); i++) {
                    PCall c = done.get(i);
                    if (keep && f.changed(c.before(), c.after())) keep = false;
                    if (fromStatement && (stated.get(i) == null || f.changed(stated.get(i), c.after()))) fromStatement = false;
                    if (constant && !Arrays.equals(c.after()[f.arg], f.off, f.off + f.len, first, 0, f.len)) constant = false;
                    if (!keep && !fromStatement && !constant) break;
                }
                if (keep) continue;
                if (constant) {
                    k.rules.put(f, new EquivalenceRules.Const(first));
                    continue;
                }
                if (fromStatement) {
                    k.rules.put(f, new EquivalenceRules.FromStatement(image));
                    continue;
                }
                PField from = null;
                for (PField g : p.fields) {
                    if (g == f || g.len != f.len || g.numeric() != f.numeric()) continue;
                    boolean all = true;
                    for (PCall c : done) if (!EquivalenceRules.sameAs(f, c.after(), g, c.before())) { all = false; break; }
                    if (all) { from = g; break; }
                }
                if (from != null) k.rules.put(f, new EquivalenceRules.Copy(from));
                else k.varies.add(f.name);
            }
            for (PCall c : done) {
                byte[][] img = copy(c.before());
                for (Map.Entry<PField, Rule> e : k.rules.entrySet()) e.getValue().apply(e.getKey(), c.before(), img);
                if (Arrays.deepEquals(img, c.after())) k.identical++;
            }
            return k;
        }

        /** The ranges of the inputs a verification ran over: integer fields, operand digits, selector codes. */
        final class Domain {
            final Map<String, long[]> nums = new LinkedHashMap<>();
            final Map<String, Set<String>> codes = new LinkedHashMap<>();
            final List<PField> ints = new ArrayList<>(), sels = new ArrayList<>();
            final List<Src> srcs = new ArrayList<>();

            Domain(Model m) {
                for (PField f : m.allInputs()) {
                    if (f.integer() && f.digits <= 9) ints.add(f);
                    else if (!f.numeric() || selectors.contains(f)) sels.add(f);
                }
                Set<Src> s = new LinkedHashSet<>();
                List<Src> l = new ArrayList<>();
                m.h.ex.sources(l);
                for (Model e : m.extras) e.h.ex.sources(l);
                s.addAll(l);
                srcs.addAll(s);
            }

            void add(byte[][] b) {
                for (PField f : ints) {
                    BigDecimal v = f.num(b);
                    if (v != null) range(f.name, v.longValue());
                }
                for (PField f : sels) {
                    Set<String> c = codes.computeIfAbsent(f.name, k -> new java.util.TreeSet<>());
                    if (c.size() < 20) c.add(renderCode(f, f.code(b)));
                }
                for (Src x : srcs) {
                    try {
                        BigDecimal v = x.value(new Env(b, p.fields.size()));
                        range(x.ident + " digits", v.precision());
                        if (v.signum() < 0) range(x.ident + " negative", 1);
                    } catch (RuntimeException ignored) {
                        // not a valid operand in this CALL
                    }
                }
            }

            void range(String k, long v) {
                long[] r = nums.computeIfAbsent(k, x -> new long[]{v, v});
                r[0] = Math.min(r[0], v);
                r[1] = Math.max(r[1], v);
            }

            List<String> describe() {
                List<String> out = new ArrayList<>();
                for (Map.Entry<String, long[]> e : nums.entrySet()) {
                    if (e.getKey().endsWith(" negative")) continue;
                    String sign = e.getKey().endsWith(" digits") && nums.containsKey(e.getKey().replace(" digits", " negative")) ? " (both signs)" : "";
                    out.add(e.getKey() + " " + (e.getValue()[0] == e.getValue()[1] ? String.valueOf(e.getValue()[0]) : e.getValue()[0] + ".." + e.getValue()[1]) + sign);
                }
                for (Map.Entry<String, Set<String>> e : codes.entrySet()) out.add(e.getKey() + " " + String.join(" ", e.getValue()));
                return out;
            }
        }

        /** What the faithful program did on the failing CALLs, field by field, when it did the same thing every time. */
        List<String> outcomeOf(List<PCall> calls, Model m) {
            List<String> out = new ArrayList<>();
            List<String> abends = calls.stream().filter(c -> c.after() == null).map(PCall::abend).distinct().toList();
            List<Obs> obs = new ArrayList<>();
            for (PCall c : calls) if (c.after() != null) obs.add(new Obs(c.before(), c.before(), c.after()));
            if (!abends.isEmpty()) out.add((obs.isEmpty() ? "" : "on " + (calls.size() - obs.size()) + " of them ") + "the program ends abnormally (" + String.join(", ", abends) + ")");
            if (obs.isEmpty()) return out;
            Set<PField> outs = new LinkedHashSet<>(m.h.target.fields());
            for (Map<PField, Rule> rs : m.rules.values()) outs.addAll(rs.keySet());
            for (PField f : outs) {
                Learned l = EquivalenceRules.simple(f, obs, p.fields, rcLike(f));
                if (l.agree() != obs.size()) {
                    if (m.h.target.fields().contains(f)) out.add(f.name + " varies");
                    continue;
                }
                String d = l.rule().describe(f);
                out.add(d == null ? f.name + " unchanged" : d);
            }
            return out;
        }

        String example(Model m, PCall c) {
            StringBuilder sb = new StringBuilder();
            sb.append("inputs ");
            List<String> in = new ArrayList<>();
            for (PField f : m.allInputs()) in.add(f.name + "=" + render(f, c.before()));
            for (PField f : m.allTargets()) if (!m.allInputs().contains(f)) in.add(f.name + "=" + render(f, c.before()));
            sb.append(String.join(", ", in));
            if (c.after() == null) return sb.append(": the faithful program ").append(c.abend()).toString();
            Integer ki = m.pick.get(code(m.h, c.before()));
            Outcome out = ki == null ? null : evaluate(m.h, new Env(c.before(), p.fields.size()), m.ks[ki]);
            List<String> diffs = new ArrayList<>();
            for (PField f : p.fields) {
                boolean changed = f.changed(c.before(), c.after());
                boolean predicted = out != null && out.kind().isEmpty() && m.h.target.fields().contains(f);
                if (changed || predicted)
                    diffs.add(f.name + "=" + render(f, c.after()) + (predicted ? " (statement: " + render(f, out.img()) + ")" : ""));
            }
            sb.append("; faithful after: ").append(diffs.isEmpty() ? "nothing changed" : String.join(", ", diffs));
            if (out != null && !out.kind().isEmpty()) sb.append("; the statement gives ").append(out.kind());
            return sb.toString();
        }

        /** Input features for describing where a statement fails: field values, operand digits and signs, differences. */
        /** rank: 0 field value, 1 selector code, 2 sign, 3 digits, 4 magnitude, 5 exact intermediate, 6 difference (simpler first). */
        record Feature(String name, java.util.function.ToLongFunction<byte[][]> f, List<String> codes, int rank) {
            Feature(String name, java.util.function.ToLongFunction<byte[][]> f, int rank) { this(name, f, null, rank); }
        }

        List<Feature> features(Model m) {
            List<Feature> base = new ArrayList<>();
            List<Feature> signs = new ArrayList<>();
            for (PField f : p.fields)
                if (f.integer() && f.digits <= 9)
                    base.add(new Feature(f.name, b -> {
                        BigDecimal v = f.num(b);
                        return v == null ? EquivalenceRules.NA : v.longValue();
                    }, 0));
            List<Src> srcs = new ArrayList<>();
            m.h.ex.sources(srcs);
            Set<Src> seen = new LinkedHashSet<>(srcs);
            for (Src s : seen) {
                base.add(new Feature("digits(" + s.ident + ")", b -> {
                    try {
                        return s.value(new Env(b, p.fields.size())).precision();
                    } catch (RuntimeException e) {
                        return EquivalenceRules.NA;
                    }
                }, 3));
                base.add(new Feature("magnitude(" + s.ident + ")", b -> {
                    try {
                        BigDecimal v = s.value(new Env(b, p.fields.size()));
                        return v.signum() == 0 ? EquivalenceRules.NA : v.precision() - v.scale() - 1;
                    } catch (RuntimeException e) {
                        return EquivalenceRules.NA;
                    }
                }, 4));
                signs.add(new Feature("signum(" + s.ident + ")", b -> {
                    try {
                        return s.value(new Env(b, p.fields.size())).signum();
                    } catch (RuntimeException e) {
                        return EquivalenceRules.NA;
                    }
                }, 2));
            }
            if (m.h.ex.exactIntermediateJava() != null) {
                String n = "digits(" + m.h.ex.exactIntermediateJava() + ")";
                base.add(new Feature(n, b -> {
                    try {
                        BigDecimal v = m.h.ex.exactIntermediate(new Env(b, p.fields.size()));
                        return v == null ? EquivalenceRules.NA : v.precision();
                    } catch (RuntimeException e) {
                        return EquivalenceRules.NA;
                    }
                }, 5));
            }
            List<Feature> out = new ArrayList<>(base);
            out.addAll(signs);
            for (PField f : selectors) {
                List<String> codes = new ArrayList<>();
                out.add(new Feature(f.name, b -> {
                    String c = f.code(b);
                    int i = codes.indexOf(c);
                    if (i < 0) {
                        codes.add(c);
                        i = codes.size() - 1;
                    }
                    return i;
                }, codes, 1));
            }
            int lim = Math.min(base.size(), 40);
            for (int i = 0; i < lim; i++)
                for (int j = 0; j < lim; j++) {
                    if (i == j) continue;
                    Feature a = base.get(i), b = base.get(j);
                    out.add(new Feature(a.name() + " - " + b.name(), x -> {
                        long u = a.f().applyAsLong(x), v = b.f().applyAsLong(x);
                        return u == EquivalenceRules.NA || v == EquivalenceRules.NA ? EquivalenceRules.NA : u - v;
                    }, 6));
                }
            return out;
        }

        static long[] featureValues(List<Feature> fs, PCall c) {
            long[] v = new long[fs.size()];
            for (int i = 0; i < v.length; i++) v[i] = fs.get(i).f().applyAsLong(c.before());
            return v;
        }
    }

    /** One field's sampler. */
    static final class Sampler {
        final PField f;
        final List<byte[]> codes = new ArrayList<>();
        final List<BigDecimal> values = new ArrayList<>();
        final List<byte[]> invalid = new ArrayList<>();
        final Set<String> distinctRaw = new HashSet<>();
        BigInteger umin, umax;
        int dmin = Integer.MAX_VALUE, dmax = 0, negatives;
        LocalDate dmin8, dmax8;
        int dates;
        int observed;
        int distinctValues;
        /**
         * Exploration: every 88-level value of the field, and for a small integer field (at most 12 values in the traces)
         * its plausible domain: 0 to max(largest traced value, 4), or 0 to 4 and the traced values when those are large.
         * Generated CALLs that explore draw from here uniformly, so a condition no trace reaches (a flag 'Y' with 0
         * decimals) is still run through the faithful program.
         */
        final List<byte[]> exploreCodes = new ArrayList<>();
        final List<BigDecimal> exploreValues = new ArrayList<>();

        Sampler(PField f, List<PCall> calls, Set<String> declared) { this(f, calls, declared, Set.of(), false); }

        Sampler(PField f, List<PCall> calls, Set<String> declared, Set<String> conditionValues, boolean explorable) {
            this.f = f;
            Set<String> seen = new LinkedHashSet<>();
            for (PCall c : calls) {
                byte[] raw = Arrays.copyOfRange(c.before()[f.arg], f.off, f.off + f.len);
                distinctRaw.add(new String(raw, StandardCharsets.ISO_8859_1));
                observed++;
                if (!f.numeric()) {
                    if (seen.add(new String(raw, StandardCharsets.ISO_8859_1))) codes.add(raw);
                    continue;
                }
                BigDecimal v = f.num(c.before());
                if (v == null) {
                    invalid.add(raw);
                    continue;
                }
                values.add(v);
                BigInteger u = v.setScale(f.scale, RoundingMode.DOWN).unscaledValue();
                umin = umin == null || u.compareTo(umin) < 0 ? u : umin;
                umax = umax == null || u.compareTo(umax) > 0 ? u : umax;
                int d = u.abs().toString().length();
                dmin = Math.min(dmin, d);
                dmax = Math.max(dmax, d);
                if (u.signum() < 0) negatives++;
                if (f.integer() && f.digits == 8) {
                    try {
                        LocalDate x = EquivalenceCatalog.ymd(v.longValue());
                        dates++;
                        dmin8 = dmin8 == null || x.isBefore(dmin8) ? x : dmin8;
                        dmax8 = dmax8 == null || x.isAfter(dmax8) ? x : dmax8;
                    } catch (Invalid ignored) {
                        // not a date
                    }
                }
            }
            for (String d : declared) {
                if (f.numeric()) {
                    try {
                        values.add(new BigDecimal(d));
                        distinctRaw.add("declared:" + d);
                    } catch (NumberFormatException ignored) {
                        // not a number
                    }
                } else if (seen.add(d)) {
                    codes.add(EquivalenceProbe.padded(d, f.len));
                    distinctRaw.add(d);
                }
            }
            if (explorable) explore(seen, conditionValues);
        }

        private void explore(Set<String> seenCodes, Set<String> conditionValues) {
            if (!f.numeric()) {
                if (conditionValues.isEmpty()) return;
                Set<String> all = new LinkedHashSet<>();
                for (String c : seenCodes) all.add(new String(EquivalenceProbe.padded(c, f.len), StandardCharsets.ISO_8859_1));
                for (String c : conditionValues) all.add(new String(EquivalenceProbe.padded(c, f.len), StandardCharsets.ISO_8859_1));
                for (String c : all) exploreCodes.add(c.getBytes(StandardCharsets.ISO_8859_1));
                return;
            }
            if (!f.integer() || f.digits > 9) return;
            java.util.TreeSet<BigDecimal> dom = new java.util.TreeSet<>();
            for (BigDecimal v : new HashSet<>(values)) dom.add(v.stripTrailingZeros());
            for (String c : conditionValues) {
                try {
                    dom.add(new BigDecimal(c).stripTrailingZeros());
                } catch (NumberFormatException ignored) {
                    // not a number
                }
            }
            boolean small = !values.isEmpty() && new HashSet<>(values).size() <= 12 && umax != null
                            && umax.abs().compareTo(BigInteger.valueOf(99_999)) <= 0 && umin.abs().compareTo(BigInteger.valueOf(99_999)) <= 0;
            if (small && conditionValues.isEmpty()) {
                long lo = Math.min(0, umin.longValue()), hi = umax.longValue();
                if (!f.signed) lo = Math.max(lo, 0);
                if (hi <= 30) {
                    hi = Math.max(hi, 4);
                    for (long v = lo; v <= hi; v++) dom.add(BigDecimal.valueOf(v));
                } else for (long v = Math.max(lo, 0); v <= 4; v++) dom.add(BigDecimal.valueOf(v));
            }
            if (dom.size() > 1 && (small || !conditionValues.isEmpty())) exploreValues.addAll(dom);
        }

        boolean varies() {
            distinctValues = new HashSet<>(values).size();
            return distinctRaw.size() > 1 || exploreCodes.size() > 1 || exploreValues.size() > 1;
        }

        void sample(Random r, byte[][] b, boolean exploring) {
            if (exploring && !exploreCodes.isEmpty()) {
                System.arraycopy(exploreCodes.get(r.nextInt(exploreCodes.size())), 0, b[f.arg], f.off, f.len);
                return;
            }
            if (exploring && !exploreValues.isEmpty()) {
                f.store(b, exploreValues.get(r.nextInt(exploreValues.size())));
                return;
            }
            if (!f.numeric()) {
                if (codes.isEmpty()) return;
                byte[] v = codes.get(r.nextInt(codes.size()));
                System.arraycopy(v, 0, b[f.arg], f.off, f.len);
                return;
            }
            if (!invalid.isEmpty() && r.nextInt(observed) < invalid.size()) {
                byte[] v = invalid.get(r.nextInt(invalid.size()));
                System.arraycopy(v, 0, b[f.arg], f.off, f.len);
                return;
            }
            if (values.isEmpty()) return;
            BigDecimal v;
            BigInteger range = umax == null ? BigInteger.ZERO : umax.subtract(umin);
            boolean small = umax != null && range.compareTo(BigInteger.valueOf(200_000)) <= 0;
            double u = r.nextDouble();
            if (dates * 2 > observed && dmin8 != null && u < 0.8) {
                long span = dmax8.toEpochDay() - dmin8.toEpochDay();
                LocalDate d = LocalDate.ofEpochDay(dmin8.toEpochDay() + (span <= 0 ? 0 : (long) (r.nextDouble() * (span + 1))));
                v = BigDecimal.valueOf(d.getYear() * 10000L + d.getMonthValue() * 100L + d.getDayOfMonth());
            } else if (distinctValues <= 12 && (u < 0.75 || !small)) v = values.get(r.nextInt(values.size()));
            else if (small) v = u < 0.85 ? new BigDecimal(umin.add(BigInteger.valueOf((long) (r.nextDouble() * (range.longValue() + 1)))), f.scale)
                : values.get(r.nextInt(values.size()));
            else v = u < 0.2 ? values.get(r.nextInt(values.size())) : shaped(r);
            f.store(b, v);
        }

        /** A value with an observed number of digits: random, all nines (carries), trailing zeros, exact ties, near ties. */
        BigDecimal shaped(Random r) {
            int lo = Math.max(1, dmin), hi = Math.max(lo, dmax);
            int d = lo + r.nextInt(hi - lo + 1);
            char[] s = new char[d];
            int shape = r.nextInt(10);
            for (int i = 0; i < d; i++) s[i] = (char) ('0' + r.nextInt(10));
            s[0] = (char) ('1' + r.nextInt(9));
            int k = d > 1 ? 1 + r.nextInt(d - 1) : 0;
            switch (shape) {
                case 0 -> Arrays.fill(s, '9');
                case 1 -> {
                    for (int i = d - k; i < d; i++) s[i] = '0';
                }
                case 2 -> {
                    if (k > 0) {
                        s[d - k] = '5';
                        for (int i = d - k + 1; i < d; i++) s[i] = '0';
                    }
                }
                case 3 -> {
                    if (k > 0) {
                        s[d - k] = r.nextBoolean() ? '4' : '5';
                        for (int i = d - k + 1; i < d; i++) s[i] = s[d - k] == '4' ? '9' : '0';
                        if (s[d - k] == '5' && d - k + 1 < d) s[d - 1] = '1';
                    }
                }
                default -> {
                    // random digits
                }
            }
            BigInteger u = new BigInteger(new String(s));
            if (negatives > 0 && r.nextDouble() < Math.max(0.1, negatives / (double) Math.max(1, values.size()))) u = u.negate();
            if (r.nextInt(100) == 0) u = BigInteger.ZERO;
            return new BigDecimal(u, f.scale);
        }
    }

    // ================================================================== groups, models, results

    static final class Group {
        final LinkedHashMap<PField, String> key;
        final List<PCall> traced;
        final boolean generatedOnly;
        final List<PCall> background;

        Group(LinkedHashMap<PField, String> key, List<PCall> traced, boolean generatedOnly, List<PCall> background) {
            this.key = key;
            this.traced = traced;
            this.generatedOnly = generatedOnly;
            this.background = background;
        }

        String label() {
            if (key.isEmpty()) return "every CALL";
            List<String> parts = new ArrayList<>();
            for (Map.Entry<PField, String> e : key.entrySet()) {
                String n = e.getKey().conditions.get(e.getValue());
                parts.add(e.getKey().name + " = " + renderCode(e.getKey(), e.getValue()) + (n == null ? "" : " (" + n + ")"));
            }
            return String.join(" and ", parts);
        }
    }

    static final class Model {
        final Hyp h;
        final Object[] ks;
        /** Per code, the constants (bits over ks) every checked CALL allows. */
        final Map<String, Long> feas = new LinkedHashMap<>();
        /** Further results of the same CALL, each from its own operation (a program that returns more than one). */
        final List<Model> extras = new ArrayList<>();

        Set<PField> allTargets() {
            Set<PField> s = new LinkedHashSet<>(h.target.fields());
            for (Model e : extras) s.addAll(e.h.target.fields());
            return s;
        }

        Set<PField> allInputs() {
            Set<PField> s = new LinkedHashSet<>(h.inputs());
            for (Model e : extras) s.addAll(e.h.inputs());
            return s;
        }
        final Map<String, Integer> pick = new LinkedHashMap<>();
        final Map<String, List<Object>> ambiguous = new LinkedHashMap<>();
        final Map<String, Map<PField, Rule>> rules = new LinkedHashMap<>();
        final Map<String, String> abends = new LinkedHashMap<>();
        final Map<String, Integer> kinds = new LinkedHashMap<>();
        int tracedOk, learnOk;

        Model(Hyp h) {
            this.h = h;
            this.ks = h.constants();
        }
    }

    /** The effects of CALLs outside a statement: per field, a relation; the counts of the CALLs it was checked on. */
    static final class Contract {
        final Map<PField, Rule> rules = new LinkedHashMap<>();
        final List<String> varies = new ArrayList<>();
        int calls, traced, abends, identical;
        String abend;

        boolean holds() {
            if (calls == 0) return false;
            if (abends > 0) return abends == calls && abend != null && !abend.contains(",");
            return varies.isEmpty() && identical == calls;
        }

        List<String> effects() {
            List<String> out = new ArrayList<>();
            List<String> stated = new ArrayList<>();
            for (Map.Entry<PField, Rule> e : rules.entrySet())
                if (e.getValue() instanceof EquivalenceRules.FromStatement) stated.add(e.getKey().name);
            if (!stated.isEmpty()) out.add(String.join(", ", stated) + (stated.size() == 1 ? " as the statement gives it" : " as the statement gives them"));
            if (abends > 0 && abends == calls) return List.of("the program ends abnormally (" + abend + ")");
            for (Map.Entry<PField, Rule> e : rules.entrySet())
                if (!(e.getValue() instanceof EquivalenceRules.FromStatement)) out.add(e.getValue().describe(e.getKey()));
            if (!varies.isEmpty()) out.add(String.join(", ", varies) + (varies.size() == 1 ? " varies" : " vary") + " (no single relation)");
            out.add(rules.isEmpty() ? "nothing changes" : "every other field unchanged");
            if (abend != null) out.add(abends + " of them end abnormally (" + abend + ")");
            return out;
        }

        Map<String, Object> json() {
            Map<String, Object> j = new LinkedHashMap<>();
            j.put("effects", effects());
            j.put("calls", calls);
            j.put("traced", traced);
            j.put("generated", calls - traced);
            j.put("identical", identical);
            j.put("holds", holds());
            return j;
        }
    }

    /** A region of the inputs where a statement does not hold: its terms, the failures in it, and its contract. */
    static final class FailRegion {
        final List<EquivalenceRules.Term> terms = new ArrayList<>();
        final List<String> words = new ArrayList<>();
        final List<PCall> calls = new ArrayList<>();
        final List<String> examples = new ArrayList<>();
        int failures, traced;
        /** No CALL the statement reproduces lies in the region (of the sample kept). */
        boolean exact;
        Contract contract;

        /** The region accounts for its failures: it holds no matching CALL, or its contract explains every CALL in it. */
        boolean explains() { return exact || (contract != null && contract.holds()); }

        String when() { return words.isEmpty() ? "(no simple condition)" : String.join(" and ", words); }
    }

    static final class GroupResult {
        final Group g;
        Model model;
        final List<FailRegion> regions = new ArrayList<>();
        int unclustered;
        Contract errorContract;
        String note;
        int candidates, alternatives, learning;
        long generated, generatedOk;
        List<String> outside = List.of();
        List<String> checkedInputs = List.of();
        int distinctInputs;
        final List<String> examples = new ArrayList<>();
        Probe.Samplers smp;
        long seed;

        GroupResult(Group g) { this.g = g; }

        /** verified | verified-with-condition | partly-verified | traced-only | unexplained */
        String status() {
            if (model == null && errorContract != null) return generated > 0 && generatedOk < generated ? "unexplained" : "verified-contract";
            if (model == null) return "unexplained";
            if (generated == 0) return "traced-only";
            if (generatedOk == generated) return "verified";
            if (!regions.isEmpty() && unclustered == 0 && regions.stream().allMatch(FailRegion::explains)) return "verified-with-condition";
            return "partly-verified";
        }

        boolean verified() { return status().startsWith("verified"); }
    }

    // ================================================================== report

    public static final class Report {
        final String program;
        final Options o;
        String skipped;
        String faithfulNote;
        int traced, sources, replayed, replayedSame;
        PField key;
        String epochs;
        final List<GroupResult> groups = new ArrayList<>();

        Report(String program, Options o) {
            this.program = program;
            this.o = o;
        }

        public int verifiedGroups() { return (int) groups.stream().filter(GroupResult::verified).count(); }

        public String summary() {
            if (skipped != null) return program + ": skipped (" + skipped + ")";
            StringBuilder sb = new StringBuilder(program + ": " + traced + " traced CALLs, " + groups.size() + " group(s), faithful replay "
                                                 + replayedSame + "/" + replayed + (faithfulNote == null ? "" : " (" + faithfulNote + ")"));
            for (GroupResult r : groups) {
                sb.append("\n  ").append(r.g.label()).append(" [").append(r.g.traced.size()).append(" traced]: ").append(r.status());
                if (r.model != null) {
                    sb.append(": ").append(r.model.h.ex.java()).append(" -> ").append(fieldNames(r.model.h.target.fields()));
                    for (Model e : r.model.extras) sb.append(" + ").append(e.h.ex.java()).append(" -> ").append(fieldNames(e.h.target.fields()));
                    if (r.generated > 0) sb.append(String.format(Locale.ROOT, " (%,d/%,d generated)", r.generatedOk, r.generated));
                    for (FailRegion fr : r.regions) sb.append(" | fails when ").append(fr.when());
                } else if (r.note != null) sb.append(" (").append(r.note).append(")");
            }
            return sb.toString();
        }

        Map<String, Object> groupJson(GroupResult r) {
            Map<String, Object> g = new LinkedHashMap<>();
            Map<String, Object> key = new LinkedHashMap<>();
            for (Map.Entry<PField, String> e : r.g.key.entrySet()) key.put(e.getKey().name, e.getValue());
            g.put("group", key);
            g.put("label", r.g.label());
            g.put("status", r.status());
            g.put("tracedCalls", r.g.traced.size());
            g.put("generatedOnly", r.g.generatedOnly);
            if (r.model == null) {
                if (r.errorContract != null) {
                    Map<String, Object> c = r.errorContract.json();
                    c.put("generatedChecked", r.generated);
                    c.put("generatedIdentical", r.generatedOk);
                    g.put("errorContract", c);
                }
                if (r.note != null) g.put("note", r.note);
                return g;
            }
            Model m = r.model;
            g.put("family", m.h.family);
            g.put("java", m.h.ex.java());
            g.put("bindings", bindings(m));
            if (m.h.enumKind != 0) g.put("enum", enumJson(m));
            g.put("result", m.h.target.describe());
            g.put("resultFields", fieldNames(m.h.target.fields()));
            if (!m.extras.isEmpty()) {
                List<Object> more = new ArrayList<>();
                for (Model e : m.extras) {
                    Map<String, Object> x = new LinkedHashMap<>();
                    x.put("java", e.h.ex.java());
                    x.put("bindings", bindings(e));
                    x.put("result", e.h.target.describe());
                    x.put("resultFields", fieldNames(e.h.target.fields()));
                    if (e.h.enumKind != 0) x.put("enum", enumJson(e));
                    more.add(x);
                }
                g.put("moreResults", more);
            }
            g.put("inputs", m.allInputs().stream().map(f -> f.name).toList());
            List<String> side = new ArrayList<>();
            Map<PField, Rule> normal = m.rules.getOrDefault("", Map.of());
            for (Map.Entry<PField, Rule> e : normal.entrySet()) {
                String d = e.getValue().describe(e.getKey());
                if (d != null) side.add(d);
            }
            g.put("otherFields", side);
            List<Object> exc = new ArrayList<>();
            for (Map.Entry<String, Integer> k : m.kinds.entrySet()) {
                if (k.getKey().isEmpty()) continue;
                Map<String, Object> x = new LinkedHashMap<>();
                x.put("when", k.getKey());
                x.put("learningCalls", k.getValue());
                if (m.abends.containsKey(k.getKey())) x.put("effects", List.of("the program ends abnormally (" + m.abends.get(k.getKey()) + ")"));
                else if (m.rules.containsKey(k.getKey())) x.put("effects", effects(m.rules.get(k.getKey()), m));
                else x.put("effects", List.of("not learned"));
                exc.add(x);
            }
            g.put("exceptions", exc);
            Map<String, Object> checked = new LinkedHashMap<>();
            checked.put("traced", r.g.traced.size());
            checked.put("tracedMatched", m.tracedOk);
            checked.put("learning", r.learning);
            checked.put("learningMatched", m.learnOk);
            checked.put("generated", r.generated);
            checked.put("generatedMatched", r.generatedOk);
            checked.put("distinctInputCombinations", r.distinctInputs >= 1000 ? "1000 or more" : String.valueOf(r.distinctInputs));
            if (!r.checkedInputs.isEmpty()) checked.put("generatedInputRanges", r.checkedInputs);
            g.put("checked", checked);
            if (!r.regions.isEmpty() || r.unclustered > 0) {
                List<Object> regs = new ArrayList<>();
                for (FailRegion fr : r.regions) {
                    Map<String, Object> c = new LinkedHashMap<>();
                    c.put("failsWhen", fr.words);
                    c.put("failingGenerated", fr.failures);
                    c.put("exact", fr.exact);
                    if (fr.contract != null) c.put("contract", fr.contract.json());
                    c.put("examples", fr.examples);
                    regs.add(c);
                }
                Map<String, Object> c = new LinkedHashMap<>();
                c.put("failing", r.generated - r.generatedOk);
                c.put("regions", regs);
                c.put("failuresOutsideTheRegions", r.unclustered);
                g.put("condition", c);
            }
            if (!r.examples.isEmpty()) g.put("failureExamples", r.examples);
            g.put("alternativesWithEqualScore", r.alternatives);
            return g;
        }

        static Map<String, Object> enumJson(Model m) {
            Map<String, Object> e = new LinkedHashMap<>();
            e.put("field", m.h.sel == null ? null : m.h.sel.name);
            e.put("type", m.h.enumKind == 'M' ? "RoundingMode" : "ChronoUnit");
            Map<String, Object> map = new LinkedHashMap<>();
            for (Map.Entry<String, Integer> x : m.pick.entrySet()) map.put(x.getKey(), constName(m.ks[x.getValue()]));
            e.put("map", map);
            if (!m.ambiguous.isEmpty()) {
                Map<String, Object> amb = new LinkedHashMap<>();
                for (Map.Entry<String, List<Object>> x : m.ambiguous.entrySet()) amb.put(x.getKey(), x.getValue().stream().map(EquivalenceProbe::constName).toList());
                e.put("indistinguishable", amb);
            }
            return e;
        }

        static List<String> effects(Map<PField, Rule> rules, Model m) {
            List<String> out = new ArrayList<>();
            List<String> unchanged = new ArrayList<>();
            for (Map.Entry<PField, Rule> e : rules.entrySet()) {
                String d = e.getValue().describe(e.getKey());
                if (d != null) out.add(d);
                else if (m.h.target.fields().contains(e.getKey())) unchanged.add(e.getKey().name);
            }
            if (!unchanged.isEmpty()) out.add(String.join(", ", unchanged) + " unchanged");
            return out;
        }

        static List<String> bindings(Model m) {
            Set<String> out = new LinkedHashSet<>();
            List<Src> srcs = new ArrayList<>();
            m.h.ex.sources(srcs);
            for (Src s : srcs) out.add(s.binding());
            collect(m.h.ex, out);
            if (m.h.family.endsWith("Sticky"))
                for (Src s : srcs)
                    out.add(s.ident + ".add(BigDecimal.valueOf(" + s.ident + ".signum() < 0 ? -1 : 1, " + s.ident + ".scale() + 1)) is " + s.ident
                            + " with one more nonzero digit after its last: the value " + s.ident + " stands for has further nonzero digits (a sticky digit; any digit 1-9 gives the same result)");
            if (m.h.target instanceof EquivalenceCatalog.PairT pt && pt.repr == EquivalenceCatalog.DROPPED) {
                out.add(pt.base.binding());
                out.add(pt.p.ident + " = " + pt.p.f.name);
            }
            return new ArrayList<>(out);
        }

        private static void collect(EquivalenceCatalog.Ex ex, Set<String> out) {
            Set<PField> fs = new LinkedHashSet<>();
            ex.fields(fs);
            List<Src> srcs = new ArrayList<>();
            ex.sources(srcs);
            Set<PField> inSrc = new HashSet<>();
            for (Src s : srcs) s.fields(inSrc);
            for (PField f : fs) if (!inSrc.contains(f)) out.add(camel(f.name) + " = " + f.name);
            dates(ex, out);
        }

        private static void dates(EquivalenceCatalog.Ex ex, Set<String> out) {
            if (ex instanceof EquivalenceCatalog.TLeaf t) {
                out.removeIf(s -> s.startsWith(camel(t.s.f.name) + " = "));
                out.add(t.s.binding());
            } else if (ex instanceof EquivalenceCatalog.Plus x) dates(x.d, out);
            else if (ex instanceof EquivalenceCatalog.Between x) {
                dates(x.d1, out);
                dates(x.d2, out);
            } else if (ex instanceof EquivalenceCatalog.TProp x) dates(x.d, out);
            else if (ex instanceof EquivalenceCatalog.With x) dates(x.d, out);
        }

        public String json() {
            Map<String, Object> root = new LinkedHashMap<>();
            root.put("schema", "mlcpu.equivalences.v1");
            root.put("program", program);
            if (skipped != null) root.put("skipped", skipped);
            root.put("tracedCalls", traced);
            root.put("traceSources", sources);
            root.put("generatedPerGroup", o.generate);
            root.put("learningCallsPerGroup", o.learn);
            root.put("seed", o.seed);
            Map<String, Object> replay = new LinkedHashMap<>();
            replay.put("tracedCallsReplayed", replayed);
            replay.put("identical", replayedSame);
            root.put("faithfulReplay", replay);
            if (faithfulNote != null) root.put("faithfulNote", faithfulNote);
            root.put("functionSelector", key == null ? null : key.name);
            if (epochs != null) root.put("dayNumberEncodings", epochs);
            root.put("verifiedGroups", verifiedGroups());
            List<Object> gs = new ArrayList<>();
            for (GroupResult r : groups) gs.add(groupJson(r));
            root.put("groups", gs);
            return MiniJson.write(root) + "\n";
        }

        public String markdown() {
            StringBuilder sb = new StringBuilder("# Verified library equivalences: ").append(program).append("\n\n");
            if (skipped != null) return sb.append("(none: ").append(skipped).append(")\n").toString();
            sb.append("These facts were established by the pipeline, not guessed. ").append(program).append("'s CALLs, ").append(String.format(Locale.ROOT, "%,d", traced))
              .append(" traced from the faithful program over ").append(sources).append(" oracle scenario(s), and up to ")
              .append(String.format(Locale.ROOT, "%,d", o.generate)).append(" generated CALLs per group (inputs drawn from the traced ones, run through the ")
              .append("faithful program), were replayed through a catalog of JDK operations. Each statement below reproduced every byte of every ")
              .append("checked CALL (all output fields and the return code) where its condition holds. Implement these groups with the named JDK ")
              .append("operation (BigDecimal, MathContext, RoundingMode, LocalDate, ChronoUnit) and the stated field mapping; do not re-derive the ")
              .append("COBOL digit or calendar algorithm. A decimal pair is a BigDecimal: new BigDecimal(COEF, -EXP) has the value COEF × 10^EXP.\n");
            boolean any = false;
            for (GroupResult r : groups) {
                if (!r.verified()) continue;
                any = true;
                if (r.model == null) {
                    sb.append("\n## ").append(r.g.label()).append("\n\n");
                    sb.append("No library operation: an error path. Its effects (a contract): ").append(String.join("; ", r.errorContract.effects())).append(".\n");
                    sb.append(String.format(Locale.ROOT, "- checked: %,d traced CALL(s) and %,d generated CALL(s) of the faithful %s: every byte identical.%n",
                        r.g.traced.size(), r.generated, program));
                    continue;
                }
                Model m = r.model;
                sb.append("\n## ").append(r.g.label()).append("\n\n");
                sb.append("`r = ").append(m.h.ex.java()).append("`\n\n");
                List<String> names = new ArrayList<>(bindings(m));
                if (m.h.enumKind != 0) names.add(enumLine(m));
                sb.append("- where ").append(String.join("; ", names)).append('\n');
                sb.append("- result: ").append(m.h.target.describe()).append('\n');
                for (Model e : m.extras) {
                    List<String> en = new ArrayList<>(bindings(e));
                    if (e.h.enumKind != 0) en.add(enumLine(e));
                    en.removeAll(names);
                    sb.append("- and a further result: `r = ").append(e.h.ex.java()).append("`").append(en.isEmpty() ? "" : " where " + String.join("; ", en))
                      .append("; ").append(e.h.target.describe()).append('\n');
                }
                List<String> side = new ArrayList<>();
                for (Map.Entry<PField, Rule> e : m.rules.getOrDefault("", Map.of()).entrySet()) {
                    String d = e.getValue().describe(e.getKey());
                    if (d != null) side.add(d);
                }
                if (!side.isEmpty()) sb.append("- also: ").append(String.join("; ", side)).append('\n');
                for (Map.Entry<String, Integer> k : m.kinds.entrySet()) {
                    if (k.getKey().isEmpty()) continue;
                    List<String> eff = m.abends.containsKey(k.getKey()) ? List.of("the program ends abnormally (" + m.abends.get(k.getKey()) + ")")
                        : m.rules.containsKey(k.getKey()) ? effects(m.rules.get(k.getKey()), m) : null;
                    if (eff == null) continue;
                    sb.append("- when ").append(exceptionWords(k.getKey())).append(": ").append(eff.isEmpty() ? "nothing changes" : String.join("; ", eff))
                      .append(m.abends.containsKey(k.getKey()) ? "" : "; every other field unchanged").append('\n');
                }
                sb.append("- checked: ").append(checkedWords(r, program)).append('\n');
                for (int ri = 0; ri < r.regions.size(); ri++) {
                    FailRegion fr = r.regions.get(ri);
                    Contract k = fr.contract;
                    sb.append(ri == 0 ? "- where it does not hold, when " : "- otherwise, where it does not hold, when ").append(fr.when()).append(": ");
                    if (k == null || k.calls == 0) {
                        sb.append("not described\n");
                        continue;
                    }
                    if (!k.holds()) {
                        sb.append(String.format(Locale.ROOT, "%s does something no single relation per field describes (%s); the statement does not "
                                                             + "hold there (%,d CALLs checked).%n", program,
                            k.varies.isEmpty() ? String.join("; ", k.effects()) : String.join(", ", k.varies) + (k.varies.size() == 1 ? " varies" : " vary"), k.calls));
                        continue;
                    }
                    sb.append(String.join("; ", k.effects()));
                    sb.append(String.format(Locale.ROOT, ". %s on %,d CALL(s) in that region (%,d traced, %,d generated)%s.%n",
                        k.holds() ? "A contract checked" : "Checked", k.calls, k.traced, k.calls - k.traced,
                        k.holds() ? ": every byte identical" : String.format(Locale.ROOT, ": %,d identical", k.identical)));
                }
                if (!r.checkedInputs.isEmpty()) sb.append("- generated inputs covered: ").append(String.join("; ", r.checkedInputs)).append('\n');
            }
            if (!any) sb.append("\n(no group verified)\n");
            List<String> rest = new ArrayList<>();
            for (GroupResult r : groups) {
                if (r.verified()) continue;
                if (r.g.generatedOnly && r.model == null) continue;
                boolean close = r.model != null && r.generatedOk * 2 > r.generated;
                rest.add(r.g.label() + (!close ? "" : " (closest: `" + r.model.h.ex.java() + "`, " + String.format(Locale.ROOT, "%,d of %,d", r.generatedOk, r.generated)
                                                                + " generated CALLs identical)"));
            }
            if (!rest.isEmpty()) {
                sb.append("\n## Not explained by a library operation\n\n");
                for (String s : rest) sb.append("- ").append(s).append('\n');
                sb.append("\nImplement these from the requirements and the COBOL logic.\n");
            }
            return sb.toString();
        }

        static String enumLine(Model m) {
            StringBuilder sb = new StringBuilder(m.h.enumKind == 'M' ? "mode" : "unit");
            if (m.h.sel == null) {
                Integer k = m.pick.get("");
                return sb.append(" = ").append(m.h.enumKind == 'M' ? "RoundingMode." : "ChronoUnit.").append(k == null ? "?" : constName(m.ks[k])).toString();
            }
            sb.append(" by ").append(m.h.sel.name).append(": ");
            List<String> parts = new ArrayList<>();
            for (Map.Entry<String, Integer> e : m.pick.entrySet()) {
                String s = renderCode(m.h.sel, e.getKey()) + " " + constName(m.ks[e.getValue()]);
                if (m.ambiguous.containsKey(e.getKey()))
                    s += " (or " + m.ambiguous.get(e.getKey()).stream().filter(x -> x != m.ks[e.getValue()]).map(EquivalenceProbe::constName).reduce((a, b) -> a + "/" + b).orElse("")
                         + ": indistinguishable on these CALLs)";
                parts.add(s);
            }
            return sb.append(String.join(", ", parts)).toString();
        }

        static String exceptionWords(String kind) {
            if (kind.startsWith("ArithmeticException")) return "the operation throws " + kind;
            if (kind.startsWith("DateTimeException")) return "the date operation throws DateTimeException";
            if (kind.startsWith("IllegalArgumentException")) return "the operation throws IllegalArgumentException";
            if (kind.startsWith("invalid ")) return kind.substring(8) + " does not hold a valid value";
            return kind;
        }

        static String checkedWords(GroupResult r, String program) {
            StringBuilder sb = new StringBuilder();
            sb.append(String.format(Locale.ROOT, "%,d traced CALL(s)", r.g.traced.size()));
            if (r.generated > 0) sb.append(String.format(Locale.ROOT, " and %,d generated CALL(s) of the faithful %s", r.generated, program));
            String few = r.distinctInputs < 100 ? " (its inputs took only " + r.distinctInputs + " distinct combinations, the values the traces show)" : "";
            if (r.generatedOk == r.generated)
                return sb.append(": every byte identical").append(r.g.generatedOnly ? " (no traced CALL used this code)" : "").append(few).append('.').toString();
            sb.append(String.format(Locale.ROOT, ": identical on %,d; the other %,d lie where it does not hold, below", r.generatedOk,
                r.generated - r.generatedOk));
            if (r.unclustered > 0) sb.append(String.format(Locale.ROOT, " (%,d of them outside any stated condition)", r.unclustered));
            return sb.append('.').toString();
        }
    }

    // ================================================================== helpers

    static String constName(Object k) { return k instanceof Enum<?> e ? e.name() : String.valueOf(k); }

    static byte[][] copy(byte[][] a) {
        byte[][] c = new byte[a.length][];
        for (int i = 0; i < a.length; i++) c[i] = a[i] == null ? null : a[i].clone();
        return c;
    }

    static byte[] padded(String s, int len) {
        byte[] v = new byte[len];
        Arrays.fill(v, (byte) ' ');
        byte[] c = s.getBytes(StandardCharsets.ISO_8859_1);
        System.arraycopy(c, 0, v, 0, Math.min(len, c.length));
        return v;
    }

    static String fieldNames(List<PField> fs) {
        List<String> n = new ArrayList<>();
        for (PField f : fs) n.add(f.name);
        return String.join(", ", n);
    }

    /** COBOL name to a Java identifier: DF-A-COEF -> dfACoef. */
    static String camel(String name) {
        StringBuilder sb = new StringBuilder();
        boolean up = false;
        for (char ch : name.toCharArray()) {
            if (ch == '-' || ch == '_') {
                up = true;
                continue;
            }
            if (sb.length() == 0) sb.append(Character.toLowerCase(ch));
            else sb.append(up ? Character.toUpperCase(ch) : Character.toLowerCase(ch));
            up = false;
        }
        return sb.toString();
    }

    static String renderCode(PField f, String code) {
        if (f.numeric()) return code;
        StringBuilder sb = new StringBuilder();
        boolean printable = true;
        for (char c : code.toCharArray()) if (c < 0x20 || c > 0x7E) printable = false;
        if (printable) return "'" + code.replace("'", "''") + "'";
        sb.append("x'");
        for (char c : code.toCharArray()) sb.append(String.format("%02X", (int) c & 0xFF));
        return sb.append("'").toString();
    }

    static String renderBytes(PField f, byte[] v) {
        byte[][] a = new byte[f.arg + 1][];
        for (int i = 0; i < a.length; i++) a[i] = new byte[0];
        a[f.arg] = new byte[f.off + f.len];
        System.arraycopy(v, 0, a[f.arg], f.off, f.len);
        return render(f, a);
    }

    static String render(PField f, byte[][] a) {
        if (f.numeric()) {
            BigDecimal v = f.num(a);
            if (v != null) return v.toPlainString();
            byte[] raw = Arrays.copyOfRange(a[f.arg], f.off, f.off + f.len);
            StringBuilder sb = new StringBuilder("x'");
            for (byte b : raw) sb.append(String.format("%02X", b & 0xFF));
            return sb.append("'").toString();
        }
        return renderCode(f, new String(a[f.arg], f.off, f.len, StandardCharsets.ISO_8859_1));
    }

}
