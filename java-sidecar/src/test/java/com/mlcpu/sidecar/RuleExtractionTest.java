package com.mlcpu.sidecar;

import static org.junit.jupiter.api.Assertions.*;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import com.mlcpu.rt.Area;
import com.mlcpu.rt.CobConfig;
import com.mlcpu.rt.CobField;
import com.mlcpu.rt.CobRt;
import com.mlcpu.rt.CobolBase;
import com.mlcpu.rt.ProgramRegistry;
import java.io.InputStream;
import java.lang.reflect.Constructor;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.math.BigDecimal;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HexFormat;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Random;
import java.util.concurrent.atomic.AtomicInteger;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

/** Typed rule extraction: contracts, equivalence with the faithful lane, injection, fail-closed metadata, real IR shapes. */
class RuleExtractionTest {
    private static final Gson GSON = new Gson();
    private final byte savedDefault = CobConfig.DEFAULT_BYTE;

    @AfterEach
    void reset() {
        CobConfig.DEFAULT_BYTE = savedDefault;
        CobRt.resetRunUnit();
        ProgramRegistry.reset();
    }

    // ---- default lanes stay as they were ---------------------------------------------------------------------------

    @Test
    void extractionIsOptInAndLeavesExistingEmissionsUnchanged() {
        Ir ir = program();
        String faithful = new SkeletonEmitter().emit(ir).source;
        String composed = new SkeletonEmitter().emit(ir, true).source;
        SkeletonEmitter.Emission notExtracting = new SkeletonEmitter().emit(ir, true, false);
        assertEquals(composed, notExtracting.source);
        assertEquals(faithful, new SkeletonEmitter().emit(ir, false, false).source);
        assertTrue(notExtracting.additionalSources.isEmpty());
        assertNull(notExtracting.extractionLedger);
        assertFalse(composed.contains("_rules"));
        assertThrows(IllegalArgumentException.class, () -> new SkeletonEmitter().emit(ir, false, true));
        SkeletonEmitter.Emission a = new SkeletonEmitter().emit(ir, true, true), b = new SkeletonEmitter().emit(ir, true, true);
        assertEquals(a.source, b.source);
        assertEquals(GSON.toJson(a.additionalSources), GSON.toJson(b.additionalSources));
        assertEquals(GSON.toJson(a.ruleContracts), GSON.toJson(b.ruleContracts));
    }

    // ---- equivalence -----------------------------------------------------------------------------------------------

    @Test
    void extractedLaneMatchesTheFaithfulLaneByteForByte() throws Exception {
        Ir ir = program();
        CobolBase faithful = compile(new SkeletonEmitter().emit(ir));
        SkeletonEmitter.Emission em = new SkeletonEmitter().emit(ir, true, true);
        CobolBase modern = compile(em);
        assertTrue(em.source.contains("_rules."), "no rule was applied");
        Random random = new Random(20261031);
        for (int run = 0; run < 400; run++) {
            byte[] record = input(random, run);
            byte[] x = record.clone(), y = record.clone();
            CobRt.resetRunUnit();
            faithful.call(new CobField[]{CobField.temp(x)}, true);
            CobRt.resetRunUnit();
            modern.call(new CobField[]{CobField.temp(y)}, true);
            assertEquals(HexFormat.of().formatHex(x), HexFormat.of().formatHex(y), "input " + HexFormat.of().formatHex(record));
        }
    }

    // ---- injection -------------------------------------------------------------------------------------------------

    @Test
    void injectedImplementationIsCalledThroughTheTypedConstructor() throws Exception {
        Ir ir = program();
        SkeletonEmitter.Emission em = new SkeletonEmitter().emit(ir, true, true);
        ClassLoader loader = load(em);
        Class<?> prog = Class.forName(em.className, true, loader);
        Map<String, Object> rule = contract(em, "LK-TMP > LK-BAL");
        Class<?> binding = Class.forName((String) rule.get("binding"), true, loader);
        Class<?> contract = Class.forName((String) rule.get("contract"), true, loader);
        AtomicInteger calls = new AtomicInteger();
        List<Object> seen = new ArrayList<>();
        Object always = Proxy.newProxyInstance(loader, new Class<?>[]{contract}, (proxy, m, args) -> {
            if (!m.getName().equals("evaluate")) return m.invoke(new Object(), args);
            calls.incrementAndGet();
            seen.add(args[0]);
            return true;
        });
        Object generated = binding.getMethod("generated").invoke(null);
        String accessor = (String) rule.get("bindingAccessor");
        String wither = "with" + Character.toUpperCase(accessor.charAt(0)) + accessor.substring(1);
        Object injected = binding.getMethod(wither, contract).invoke(generated, always);
        assertSame(always, binding.getMethod(accessor).invoke(injected));
        Constructor<?> typed = prog.getConstructor(binding);
        CobolBase withRule = (CobolBase) typed.newInstance(injected);
        CobolBase noArg = (CobolBase) prog.getConstructor().newInstance();
        byte[] record = input(new Random(7), 0);
        setDecimal(record, 0, 7, 2, true, BigDecimal.ONE);
        setDecimal(record, 7, 7, 2, true, BigDecimal.TEN);
        put(record, 28, "000");
        byte[] a = record.clone(), b = record.clone();
        noArg.call(new CobField[]{CobField.temp(a)}, true);
        CobRt.resetRunUnit();
        withRule.call(new CobField[]{CobField.temp(b)}, true);
        assertEquals(2, calls.get(), "both IF LK-TMP > LK-BAL sites call the injected rule");
        Object in = seen.get(0);
        assertEquals(0, new BigDecimal("1.00").compareTo((BigDecimal) in.getClass().getMethod("lkTmp").invoke(in)));
        assertEquals(0, new BigDecimal("10.00").compareTo((BigDecimal) in.getClass().getMethod("lkBal").invoke(in)));
        assertEquals(0, cnt(a), "1 > 10 is false: the generated rule adds nothing");
        assertEquals(2, cnt(b), "the injected rule says true at both sites");
    }

    @Test
    void alphanumericRulesYieldToTheFaithfulTestUnderEbcdicCollation() throws Exception {
        SkeletonEmitter.Emission em = new SkeletonEmitter().emit(program(), true, true);
        ClassLoader loader = load(em);
        Map<String, Object> rule = contract(em, "FLAG-ON (LK-FLAG IN 'Y')");
        Class<?> binding = Class.forName((String) rule.get("binding"), true, loader);
        Class<?> contract = Class.forName((String) rule.get("contract"), true, loader);
        AtomicInteger calls = new AtomicInteger();
        Object counting = Proxy.newProxyInstance(loader, new Class<?>[]{contract}, (proxy, m, args) -> {
            calls.incrementAndGet();
            return false;
        });
        String accessor = (String) rule.get("bindingAccessor");
        Object injected = binding.getMethod("with" + Character.toUpperCase(accessor.charAt(0)) + accessor.substring(1), contract)
                .invoke(binding.getMethod("generated").invoke(null), counting);
        CobolBase p = (CobolBase) Class.forName(em.className, true, loader).getConstructor(binding).newInstance(injected);
        byte[] record = input(new Random(3), 1);
        try {
            com.mlcpu.rt.RecordCharset.configure("CP037");
            p.call(new CobField[]{CobField.temp(record.clone())}, true);
            assertEquals(0, calls.get(), "EBCDIC collation runs the faithful comparison");
        } finally {
            com.mlcpu.rt.RecordCharset.configure("LATIN1");
        }
        CobRt.resetRunUnit();
        p.call(new CobField[]{CobField.temp(record.clone())}, true);
        assertEquals(1, calls.get());
    }

    @Test
    void textEqualityPadsWithAsciiSpaceOnly() throws Exception {
        SkeletonEmitter.Emission em = new SkeletonEmitter().emit(program(), true, true);
        ClassLoader loader = load(em);
        Map<String, Boolean> literal = Map.of("AB   ", true, "AB\t  ", false, "AB\u00A0  ", false, "AB\u2002  ", false, " AB  ", false);
        Map<String, Boolean> spaces = Map.of("     ", true, "\t    ", false, "\u00A0    ", false, "\u0000    ", false);
        for (Map.Entry<String, Map<String, Boolean>> rule : Map.of("LK-NAME = 'AB'", literal, "LK-NAME = SPACES", spaces).entrySet()) {
            Map<String, Object> d = contract(em, rule.getKey());
            Class<?> input = Class.forName((String) d.get("input"), true, loader);
            Object impl = Class.forName((String) d.get("generatedImplementation"), true, loader).getConstructor().newInstance();
            Method evaluate = Class.forName((String) d.get("contract"), true, loader).getMethod("evaluate", input);
            for (Map.Entry<String, Boolean> c : rule.getValue().entrySet())
                assertEquals(c.getValue(), evaluate.invoke(impl, input.getConstructors()[0].newInstance(c.getKey())), rule.getKey() + " [" + c.getKey() + "]");
            String source = em.additionalSources.stream().filter(s -> d.get("generatedImplementation").equals(s.get("className"))).map(s -> (String) s.get("content")).findFirst().orElseThrow();
            assertFalse(source.contains("trim") || source.contains("strip") || source.contains("isWhitespace"), source);
        }
    }

    // ---- contracts and evidence ------------------------------------------------------------------------------------

    @Test
    void ruleSourcesAreJdkOnlyAndPassFaithfulRuntimeCases() {
        SkeletonEmitter.Emission em = new SkeletonEmitter().emit(program(), true, true);
        for (Map<String, Object> s : em.additionalSources) {
            String content = (String) s.get("content");
            assertTrue(((String) s.get("path")).startsWith("com/mlcpu/rules/"), (String) s.get("path"));
            assertFalse(content.contains("import com.mlcpu.rt"), (String) s.get("path"));
            for (String f : RuleVerifier.FORBIDDEN) assertFalse(content.contains(f), s.get("path") + " contains " + f);
        }
        RuleVerifier.Result v = RuleVerifier.verify(em.additionalSources, em.ruleContracts);
        assertTrue(v.isolatedCompile, String.join("\n", v.diagnostics));
        assertTrue(v.forbiddenFree);
        assertTrue(v.casesPass, String.join("\n", v.diagnostics));
        assertTrue(v.casesRun > 50, "cases run: " + v.casesRun);
    }

    @Test
    void ledgerAppliesProvenSlicesAndSkipsUnsafeOnesWithReasons() {
        SkeletonEmitter.Emission em = new SkeletonEmitter().emit(program(), true, true);
        Map<String, Object> ledger = em.extractionLedger;
        assertEquals("mlcpu.rule-extraction-ledger.v1", ledger.get("schema"));
        assertTrue(String.valueOf(ledger.get("claim")).contains("no capability or program is claimed"));
        Map<String, String> skipped = new LinkedHashMap<>();
        for (Object o : (List<?>) ledger.get("skipped")) {
            Map<?, ?> m = (Map<?, ?>) o;
            skipped.put((String) m.get("site"), (String) m.get("reason"));
            assertEquals(64, ((String) m.get("hash")).length());
        }
        assertTrue(skipped.get("M:5").contains("collating sequence"), skipped.toString());
        assertTrue(skipped.get("M:6").contains("LINKAGE"), skipped.toString());
        assertTrue(skipped.get("M:7").contains("operator /"), skipped.toString());
        assertTrue(skipped.get("M:13").contains("subscripted"), skipped.toString());
        assertTrue(skipped.get("M:14").contains("mixed numeric"), skipped.toString());
        List<String> applied = new ArrayList<>();
        for (Object o : (List<?>) ledger.get("applied")) applied.add((String) ((Map<?, ?>) o).get("site"));
        assertEquals(List.of("M:1", "M:1A", "M:2", "M:3", "M:4", "M:8", "M:9", "M:9A", "M:10", "M:11", "M:12", "M:16"), applied);
        Map<String, Object> pred = contract(em, "LK-TMP > LK-BAL");
        assertTrue(((String) pred.get("ruleId")).matches("RULES1\\.PRED-[0-9a-f]{8}"), (String) pred.get("ruleId"));
        assertEquals(List.of("M:1", "M:9"), ((Map<?, ?>) pred.get("provenance")).get("statementIds"));
        assertEquals("boolean", pred.get("output"));
        assertEquals("evaluate", pred.get("method"));
        assertEquals("L2", pred.get("level"));
        assertEquals("slice", pred.get("scope"));
        Map<String, Object> calc = contract(em, "(LK-ACC + LK-INT)");
        assertEquals("calculation", calc.get("kind"));
        assertTrue(((String) calc.get("output")).endsWith("Result"));
        Map<String, Object> flag = contract(em, "FLAG-ON (LK-FLAG IN 'Y')");
        assertEquals(List.of("recordCharset=LATIN1"), flag.get("requires"));
        assertTrue(em.source.contains("RecordCharset.isEbcdic() ? ("), "alphanumeric equality keeps the faithful test under EBCDIC collation");
        // the contract never depends on the program class; only the descriptor names the adapter
        assertEquals(em.className, calc.get("adapter"));
    }

    @Test
    void protocolPublishesRuleArtifactsAndRejectsExtractionOutsideTheComposedLane() throws Exception {
        JsonObject request = new JsonObject();
        request.add("ir", GSON.toJsonTree(program()));
        request.addProperty("composeStorage", true);
        request.addProperty("extractRules", true);
        request.addProperty("transpileOnly", true);
        Map<?, ?> response = assertInstanceOf(Map.class, Sidecar.transpileVerify(GSON.toJson(request)));
        assertEquals(true, response.get("compiled"), String.valueOf(response.get("diagnostics")));
        assertEquals("TRANSPILED", response.get("verdict"), String.valueOf(response.get("ruleDiagnostics")));
        Map<?, ?> checks = (Map<?, ?>) response.get("checks");
        assertEquals(true, checks.get("ruleSourcesJdkOnly"));
        assertEquals(true, checks.get("ruleCasesPass"));
        assertFalse(((List<?>) response.get("additionalSources")).isEmpty());
        Map<?, ?> ledger = (Map<?, ?>) response.get("extractionLedger");
        assertEquals(64, ((String) ledger.get("irSha256")).length());
        for (Object c : (List<?>) response.get("ruleContracts"))
            assertTrue(((List<?>) ((Map<?, ?>) c).get("evidence")).contains("generated-passes-cases"));
        assertFalse(String.valueOf(response.get("faithfulJavaSource")).contains("_rules"));
        JsonObject bad = request.deepCopy();
        bad.addProperty("composeStorage", false);
        Object rejected = Sidecar.transpileVerify(GSON.toJson(bad));
        assertTrue(GSON.toJson(rejected).contains("extractRules requires composeStorage"), GSON.toJson(rejected));
    }

    // ---- real source shapes ----------------------------------------------------------------------------------------

    @Test
    void realProgramsExtractEligibleSlicesCompileAndPassCases() throws Exception {
        Map<String, Integer> minimum = Map.of("PARITY10", 2, "CALCDEMO", 1, "CBTRN02C", 3, "CBACT04C", 3);
        for (Map.Entry<String, Integer> e : minimum.entrySet()) {
            Ir ir = resource(e.getKey());
            SkeletonEmitter.Emission em = new SkeletonEmitter().emit(ir, true, true);
            assertTrue(em.ruleContracts.size() >= e.getValue(), e.getKey() + " rules: " + em.ruleContracts.size() + " skipped: " + em.extractionLedger.get("skipped"));
            Map<String, String> all = new LinkedHashMap<>();
            all.put(em.className, em.source);
            for (Map<String, Object> s : em.additionalSources) all.put((String) s.get("className"), (String) s.get("content"));
            JavaCompilerService.Result r = new JavaCompilerService().compileAll(all);
            assertTrue(r.compiled, e.getKey() + ": " + String.join("\n", r.diagnostics));
            RuleVerifier.Result v = RuleVerifier.verify(em.additionalSources, em.ruleContracts);
            assertTrue(v.isolatedCompile && v.casesPass && v.forbiddenFree, e.getKey() + ": " + String.join("\n", v.diagnostics));
            assertFalse(new SkeletonEmitter().emit(ir).source.contains("com.mlcpu.rules"), e.getKey());
            assertEquals(new SkeletonEmitter().emit(ir, true).source, new SkeletonEmitter().emit(ir, true, false).source, e.getKey());
            if (e.getKey().startsWith("CB"))
                assertFalse(((List<?>) em.extractionLedger.get("skipped")).isEmpty(), e.getKey() + " should keep unsupported sites faithful");
        }
        // CardDemo's posting slices: the credit-limit check and the balance calculation become typed rules
        SkeletonEmitter.Emission posting = new SkeletonEmitter().emit(resource("CBTRN02C"), true, true);
        Map<String, Object> limit = contract(posting, "ACCT-CREDIT-LIMIT >= WS-TEMP-BAL");
        assertEquals(List.of("1500-B-LOOKUP-ACCT"), ((Map<?, ?>) limit.get("provenance")).get("paragraphs"));
        assertEquals("calculation", contract(posting, "((ACCT-CURR-CYC-CREDIT - ACCT-CURR-CYC-DEBIT) + DALYTRAN-AMT)").get("kind"));
        assertTrue(GSON.toJson(posting.extractionLedger.get("skipped")).contains("collating sequence"));
    }

    @Test
    void corpusProgramsDisplayTheSameWithTypedRules() throws Exception {
        for (String name : List.of("PARITY10", "CALCDEMO")) {
            Ir ir = resource(name);
            SkeletonEmitter.Emission em = new SkeletonEmitter().emit(ir, true, true);
            assertFalse(em.ruleContracts.isEmpty(), name);
            assertEquals(display(compile(new SkeletonEmitter().emit(ir))), display(compile(em)), name);
        }
        List<String> expressions = new SkeletonEmitter().emit(resource("PARITY10"), true, true).ruleContracts.stream().map(c -> (String) c.get("expression")).toList();
        assertTrue(expressions.stream().anyMatch(x -> x.startsWith("F1-ON")), expressions.toString());
        assertTrue(expressions.stream().anyMatch(x -> x.startsWith("F3-ON")), expressions.toString());
    }

    // ---- fixtures --------------------------------------------------------------------------------------------------

    private static String display(CobolBase p) {
        StringBuilder out = new StringBuilder();
        java.util.function.Consumer<String> saved = CobRt.DISPLAY_TRACE;
        CobRt.DISPLAY_TRACE = out::append;
        try {
            CobRt.resetRunUnit();
            p.call(new CobField[0], false);
        } catch (RuntimeException e) {
            if (!e.getClass().getSimpleName().contains("Exit") && e != CobRt.PROGRAM_EXIT) throw e;
        } finally {
            CobRt.DISPLAY_TRACE = saved;
        }
        assertFalse(out.isEmpty());
        return out.toString();
    }

    private static Ir resource(String name) throws Exception {
        try (InputStream in = RuleExtractionTest.class.getResourceAsStream("/rules/" + name + ".ir.json")) {
            assertNotNull(in, name);
            return GSON.fromJson(new String(in.readAllBytes(), StandardCharsets.UTF_8), Ir.class);
        }
    }

    private static Map<String, Object> contract(SkeletonEmitter.Emission em, String expression) {
        return em.ruleContracts.stream().filter(c -> expression.equals(c.get("expression"))).findFirst()
                .orElseThrow(() -> new AssertionError("no rule " + expression + " in " + em.ruleContracts.stream().map(c -> c.get("expression")).toList()));
    }

    private static int cnt(byte[] record) {
        return Integer.parseInt(new String(record, 28, 3, StandardCharsets.ISO_8859_1));
    }

    /** Record layout of {@link #program()}'s LINKAGE item (offsets). */
    private static byte[] input(Random random, int run) {
        byte[] r = new byte[LEN];
        java.util.Arrays.fill(r, (byte) ' ');
        int mode = run % 10;
        BigDecimal tmp = decimal(random, 5, 2, mode), bal = mode == 3 ? null : decimal(random, 5, 2, mode);
        setDecimal(r, 0, 7, 2, true, tmp);
        setDecimal(r, 7, 7, 2, true, bal == null ? tmp : bal);
        String[] names = {"     ", "ALICE", "bob  ", "A1B2C", "\u0000\u0000\u0000\u0000\u0000", "ZZZZZ", "AB   ", "AB\t  ", "AB\u00A0  ", "\t    ", "\u00A0    ", "AB"};
        put(r, 14, names[random.nextInt(names.length)]);
        put(r, 19, String.valueOf("AMNZ 9y".charAt(random.nextInt(7))));
        put(r, 20, String.valueOf("YN X".charAt(random.nextInt(4))));
        setDecimal(r, 21, 7, 2, true, decimal(random, 5, 2, mode));
        put(r, 28, String.format("%03d", random.nextInt(1000)));
        setDecimal(r, 31, 9, 2, true, decimal(random, 7, 2, mode));
        setDecimal(r, 40, 7, 2, true, decimal(random, 5, 2, mode));
        for (int i = 0; i < 3; i++) setDecimal(r, 47 + 2 * i, 2, 0, false, BigDecimal.valueOf(random.nextInt(100)));
        if (mode == 9) put(r, 0, "  1 . ");
        return r;
    }

    private static final int LEN = 60;

    private static BigDecimal decimal(Random random, int intDigits, int scale, int mode) {
        if (mode == 0) return BigDecimal.ZERO;
        long bound = (long) Math.pow(10, intDigits + scale);
        long u = (long) (random.nextDouble() * bound);
        if (random.nextBoolean()) u = -u;
        return BigDecimal.valueOf(u, scale);
    }

    private static void setDecimal(byte[] r, int off, int len, int scale, boolean signed, BigDecimal v) {
        Area a = new Area(0);
        a.b = r;
        CobField.num("x", CobField.Use.DISPLAY, a, off, len, len, scale, signed, false, false, null, null).setDec(v, false);
    }

    private static void put(byte[] r, int off, String s) {
        byte[] b = s.getBytes(StandardCharsets.ISO_8859_1);
        System.arraycopy(b, 0, r, off, b.length);
    }

    /**
     * USING LK-REC: LK-TMP S9(5)V99 @0, LK-BAL S9(5)V99 @7, LK-NAME X(5) @14, LK-CODE X @19, LK-FLAG X @20 (88 FLAG-ON 'Y'),
     * LK-INT S9(5)V99 @21, LK-CNT 9(3) @28, LK-ACC S9(7)V99 @31, LK-SUM S9(5)V99 @40, LK-T 99 OCCURS 3 @47, LK-OUT X(7) @53.
     */
    static Ir program() {
        Ir ir = new Ir();
        ir.irVersion = Ir.SUPPORTED_VERSION;
        ir.program = new Ir.Program();
        ir.program.id = "RULES1";
        ir.program.path = "RULES1.cbl";
        ir.program.using = List.of("LK-REC");
        ir.program.files = List.of();
        ir.dictionary = new ArrayList<>();
        ir.dictionary.add(group(1, "LK-REC", LEN));
        ir.dictionary.add(num(2, "LK-TMP", 0, 7, 2));
        ir.dictionary.add(num(3, "LK-BAL", 7, 7, 2));
        ir.dictionary.add(alnum(4, "LK-NAME", 14, 5));
        ir.dictionary.add(alnum(5, "LK-CODE", 19, 1));
        ir.dictionary.add(alnum(6, "LK-FLAG", 20, 1));
        Ir.Field flagOn = alnum(7, "FLAG-ON", 20, 0);
        flagOn.level = "88";
        flagOn.parentId = 6;
        flagOn.values = List.of(value("Y", true));
        ir.dictionary.add(flagOn);
        ir.dictionary.add(num(8, "LK-INT", 21, 7, 2));
        Ir.Field cnt = num(9, "LK-CNT", 28, 3, 0);
        cnt.signed = false;
        cnt.pic = "9(3)";
        ir.dictionary.add(cnt);
        ir.dictionary.add(num(10, "LK-ACC", 31, 9, 2));
        ir.dictionary.add(num(11, "LK-SUM", 40, 7, 2));
        Ir.Field t = num(12, "LK-T", 47, 2, 0);
        t.signed = false;
        t.pic = "99";
        t.occurs = "3";
        t.dims = List.of(3);
        t.strides = List.of(2);
        ir.dictionary.add(t);
        ir.dictionary.add(alnum(13, "LK-OUT", 53, 7));
        List<Ir.Stmt> body = new ArrayList<>();
        body.add(ifThen("M:1", rel("gt", ref(2), ref(3)), add(ref(9), num("1"), "M:1A")));
        body.add(add(ref(10), ref(8), "M:2"));
        body.add(ifThen("M:3", c88(7), move(str("F"), refMod(13, 1), "M:3A")));
        body.add(ifThen("M:4", rel("eq", ref(4), fig("SPACES")), move(str("S"), refMod(13, 2), "M:4A")));
        body.add(ifThen("M:5", rel("lt", ref(5), str("M")), move(str("C"), refMod(13, 3), "M:5A")));
        body.add(ifThen("M:6", and(rel("gt", ref(2), num("0")), rel("eq", ref(6), str("Y"))), move(str("B"), refMod(13, 4), "M:6A")));
        body.add(compute(ref(11), bin("/", ref(2), num("7")), "M:7"));
        body.add(compute(ref(11), bin("-", bin("*", ref(2), num("2")), ref(3)), "M:8"));
        body.add(ifThen("M:9", rel("gt", ref(2), ref(3)), add(ref(9), num("1"), "M:9A")));
        body.add(ifThen("M:10", klass("ALPHABETIC", ref(4)), move(str("A"), refMod(13, 5), "M:10A")));
        body.add(ifThen("M:11", sign("POSITIVE", ref(8)), move(str("P"), refMod(13, 6), "M:11A")));
        body.add(ifThen("M:12", rel("ne", ref(4), str("ALICE")), move(str("N"), refMod(13, 7), "M:12A")));
        Ir.Node sub = ref(12);
        sub.args = List.of(num("2"));
        body.add(ifThen("M:13", rel("gt", sub, num("50")), move(str("T"), refMod(13, 1), "M:13A")));
        body.add(ifThen("M:14", rel("eq", ref(9), str("007")), move(str("7"), refMod(13, 2), "M:14A")));
        body.add(ifThen("M:16", rel("eq", ref(4), str("AB")), move(str("Q"), refMod(13, 1), "M:16A")));
        body.add(stmt("GOBACK", "M:15"));
        Ir.SemParagraph p = new Ir.SemParagraph();
        p.name = "MAIN";
        p.statements = body;
        ir.semantics = new Ir.Semantics();
        ir.semantics.schema = "mlcpu.semantic-tree.v1";
        ir.semantics.paragraphs = List.of(p);
        return ir;
    }

    private static Ir.Field group(int id, String name, int len) {
        Ir.Field f = base(id, name, "Group", 0, len);
        f.level = "01";
        f.parentId = null;
        return f;
    }

    private static Ir.Field num(int id, String name, int off, int len, int scale) {
        Ir.Field f = base(id, name, "Numeric", off, len);
        f.pic = "S9(" + (len - scale) + ")" + (scale > 0 ? "V9(" + scale + ")" : "");
        f.signed = true;
        f.digits = len;
        f.impliedDecimals = scale;
        return f;
    }

    private static Ir.Field alnum(int id, String name, int off, int len) {
        Ir.Field f = base(id, name, "Alphanumeric", off, len);
        f.pic = "X(" + len + ")";
        return f;
    }

    private static Ir.Field base(int id, String name, String category, int off, int len) {
        Ir.Field f = new Ir.Field();
        f.id = id;
        f.field = name;
        f.level = "05";
        f.category = category;
        f.offset = off;
        f.length = len;
        f.usage = "DISPLAY";
        f.section = "Linkage";
        f.parentId = 1;
        f.recordId = 1;
        return f;
    }

    private static Ir.Value value(String text, boolean quoted) {
        Ir.Value v = new Ir.Value();
        v.v = text;
        v.q = quoted;
        return v;
    }

    private static Ir.Node node(String k, String v) {
        Ir.Node n = new Ir.Node();
        n.k = k;
        n.v = v;
        return n;
    }

    private static Ir.Node ref(int id) {
        Ir.Node n = node("ref", null);
        n.id = id;
        return n;
    }

    private static Ir.Node refMod(int id, int start) {
        Ir.Node n = ref(id);
        n.start = num(String.valueOf(start));
        n.len = num("1");
        return n;
    }

    private static Ir.Node num(String v) { return node("num", v); }
    private static Ir.Node str(String v) { return node("str", v); }
    private static Ir.Node fig(String v) { return node("fig", v); }

    private static Ir.Node c88(int id) {
        Ir.Node n = node("c88", null);
        n.id = id;
        return n;
    }

    private static Ir.Node rel(String op, Ir.Node l, Ir.Node r) {
        Ir.Node n = node("rel", null);
        n.op = op;
        n.l = l;
        n.r = r;
        return n;
    }

    private static Ir.Node and(Ir.Node l, Ir.Node r) {
        Ir.Node n = node("and", null);
        n.l = l;
        n.r = r;
        return n;
    }

    private static Ir.Node bin(String op, Ir.Node l, Ir.Node r) {
        Ir.Node n = node("bin", null);
        n.op = op;
        n.l = l;
        n.r = r;
        return n;
    }

    private static Ir.Node klass(String kind, Ir.Node x) {
        Ir.Node n = node("class", kind);
        n.l = x;
        return n;
    }

    private static Ir.Node sign(String kind, Ir.Node x) {
        Ir.Node n = node("sign", kind);
        n.l = x;
        return n;
    }

    private static Ir.Stmt stmt(String verb, String id) {
        Ir.Stmt s = new Ir.Stmt();
        s.verb = verb;
        s.stepId = id;
        s.text = verb;
        return s;
    }

    private static Ir.Stmt ifThen(String id, Ir.Node cond, Ir.Stmt then) {
        Ir.Stmt s = stmt("IF", id);
        s.n = Map.of("cond", cond);
        s.blocks = Map.of("TRUE", List.of(then));
        return s;
    }

    private static Ir.Stmt move(Ir.Node from, Ir.Node to, String id) {
        Ir.Stmt s = stmt("MOVE", id);
        s.n = Map.of("src", from);
        s.l = Map.of("dst", List.of(to));
        return s;
    }

    private static Ir.Stmt add(Ir.Node to, Ir.Node value, String id) {
        Ir.Stmt s = stmt("ADD", id);
        s.s = Map.of("form", "TO");
        s.l = Map.of("ops", List.of(value), "dst", List.of(to));
        return s;
    }

    private static Ir.Stmt compute(Ir.Node to, Ir.Node expr, String id) {
        Ir.Stmt s = stmt("COMPUTE", id);
        s.n = Map.of("expr", expr);
        s.l = Map.of("dst", List.of(to));
        return s;
    }

    private static CobolBase compile(SkeletonEmitter.Emission em) throws Exception {
        return (CobolBase) Class.forName(em.className, true, load(em)).getDeclaredConstructor().newInstance();
    }

    private static ClassLoader load(SkeletonEmitter.Emission em) {
        Map<String, String> all = new LinkedHashMap<>();
        all.put(em.className, em.source);
        for (Map<String, Object> s : em.additionalSources) all.put((String) s.get("className"), (String) s.get("content"));
        JavaCompilerService.Result r = new JavaCompilerService().compileAll(all);
        assertTrue(r.compiled, String.join("\n", r.diagnostics));
        return new JavaCompilerService.MemoryLoader(r.classes, RuleExtractionTest.class.getClassLoader());
    }
}
