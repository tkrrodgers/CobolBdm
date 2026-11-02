package com.mlcpu.jobol;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.net.URISyntaxException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Set;
import java.util.stream.Collectors;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

class CompareTest {
    private static Path fixture(String name) throws URISyntaxException {
        return Path.of(CompareTest.class.getResource("/fixtures/compare/" + name).toURI());
    }

    private static CompareReport compare() throws Exception {
        return new Comparer(Config.defaults()).compare("fixture", List.of(fixture("original")), fixture("modern"), fixture("cobol"), null);
    }

    private static Set<String> codes(CompareReport r) {
        return r.gaps.stream().map(CompareReport.Gap::pattern).collect(Collectors.toSet());
    }

    private static CompareReport.Gap gap(CompareReport r, String code, String original) {
        return r.gaps.stream().filter(g -> g.pattern().equals(code) && g.original().equals(original)).findFirst()
            .orElseThrow(() -> new AssertionError("no " + code + " for " + original + " in " + r.gaps));
    }

    // ---- truncation detector and name classes

    @Test
    void truncationIsAStrictPrefixOfAtLeastTwoCharacters() {
        assertTrue(Names.isTruncation("disbursementDueDate", "disbursementDueDat"));
        assertTrue(Names.isTruncation("totalOutstandingInterestPaymentDueToGrace", "totalOutstandingInterestPaymen"));
        assertTrue(Names.isTruncation("EARLY_PAYMENT_AMOUNT_IS_NEGATIVE", "EARLY_PAYMENT_AMOUNT_IS"));
        assertTrue(Names.isTruncation("AmountToAdd", "amountToAd"), "first letter case-insensitive");
        assertFalse(Names.isTruncation("amount", "amount"), "equal is not truncated");
        assertFalse(Names.isTruncation("amount", "a"), "one character is too short to say");
        assertFalse(Names.isTruncation("amount", "amountX"), "longer is not truncated");
        assertFalse(Names.isTruncation("principalPortion", "prinPortion"));
    }

    @Test
    void nameClasses() {
        assertEquals(Names.Kind.EXACT, Names.classify("amount", "amount"));
        assertEquals(Names.Kind.CASE, Names.classify("calculatedEMI", "calculatedEmi"));
        assertEquals(Names.Kind.CASE, Names.classify("intToEnumMap", "INT_TO_ENUM_MAP"));
        assertEquals(Names.Kind.TRUNCATED, Names.classify("disbursementDueDate", "disbursementDueDat"));
        assertEquals(Names.Kind.ABBREVIATED, Names.classify("principalPortion", "prinPortion"));
        assertEquals(Names.Kind.ABBREVIATED, Names.classify("outstandingBalance", "outstBal"));
        assertEquals(Names.Kind.ABBREVIATED, Names.classify("downPaymentPercentage", "downPaymentPct"), "letters kept in order");
        assertEquals(Names.Kind.ABBREVIATED, Names.classify("disbursedAmountPercentageForDownPayment", "disbursedAmountPctF"));
        assertEquals(Names.Kind.TRUNCATED_NUMBERED, Names.classify("totalCumulativeInterest", "totalCumula2"));
        assertEquals(Names.Kind.TRUNCATED_NUMBERED, Names.classify("scheduleModelCopy", "scheduleModel2"));
        assertEquals(Names.Kind.NUMBERED, Names.classify("monthlyRate", "bigDecimal2", "BigDecimal"));
        assertEquals(Names.Kind.NUMBERED, Names.classify("amount", "money2", "Money"));
        assertEquals(Names.Kind.NUMBERED, Names.classify("installment", "t20_"));
        assertEquals(Names.Kind.NUMBERED, Names.classify("countryCode", "countryCode3"), "a counter on the right name");
        assertEquals(Names.Kind.ACCESSOR_STYLE, Names.classify("installments", "getInstallments"));
        assertEquals(Names.Kind.ACCESSOR_STYLE, Names.classify("getScale", "scale"));
        assertEquals(Names.Kind.RENAMED, Names.classify("unpaidRepaymentPeriods", "list"));
        assertEquals(Names.Kind.RENAMED, Names.classify("type", "strategy"));
        assertTrue(Names.Kind.TRUNCATED.damaged() && Names.Kind.ABBREVIATED.damaged() && !Names.Kind.NUMBERED.damaged());
    }

    @Test
    void nameAlignmentPrefersExactThenDamagedThenTheSingleNameOfAType() {
        List<int[]> p = Comparer.alignNames(
            List.of("principal", "disbursementDueDate", "monthlyRate", "count"),
            List.of("BigDecimal", "LocalDate", "BigDecimal", "int"),
            List.of("bigDecimal2", "principal", "disbursementDueDat", "x", "y"),
            List.of("BigDecimal", "BigDecimal", "LocalDate", "int", "int"));
        assertEquals(3, p.size(), "count has two int candidates and stays unaligned");
        assertEquals(List.of("0-1", "1-2", "2-0"), p.stream().map(q -> q[0] + "-" + q[1]).toList());
    }

    @Test
    void truncationCandidatesThatTieAreNotGuessed() {
        List<int[]> p = Comparer.alignNames(List.of("totalPrincipalPaid"), List.of("Money"),
            List.of("totalPrin", "totalPrinc"), List.of("Long", "Long"));
        assertEquals(1, p.size(), "the longer shared prefix wins");
        assertEquals(1, p.get(0)[1]);
        List<int[]> tie = Comparer.alignNames(List.of("amountDue"), List.of("Money"), List.of("amountD", "amountD"), List.of("Money", "Money"));
        assertEquals(0, tie.size());
    }

    @Test
    void typeDeltas() {
        assertEquals("GENERIC_TO_OBJECT", Comparer.typeDelta("Map<LocalDate,Money>", "Map<Object,Object>"));
        assertEquals("GENERIC_DROPPED", Comparer.typeDelta("List<Money>", "List"));
        assertEquals("TO_OBJECT", Comparer.typeDelta("LoanCharge", "Object"));
        assertEquals("OPTIONAL_UNWRAPPED", Comparer.typeDelta("Optional<LocalDate>", "LocalDate"));
        assertEquals("BOXING", Comparer.typeDelta("Integer", "int"));
        assertEquals("COLLECTION_CHANGED", Comparer.typeDelta("Collection<Money>", "List<Money>"));
        assertEquals("VARARGS_TO_ARRAY", Comparer.typeDelta("Object...", "Object[]"));
        assertEquals("NUMERIC_CHANGED", Comparer.typeDelta("short", "int"));
        assertEquals("Map<LocalDate,Money>", JavaModel.norm("java.util.Map<java.time.LocalDate, org.x.Money>"));
        assertEquals("LoanBuilder", JavaModel.norm("Loan.LoanBuilder"));
        assertEquals("Map", JavaModel.erase("Map<LocalDate,Map<LocalDate,Money>>"));
    }

    @Test
    void framesGiveReachedMethodsWithTheirParameterTypes() throws Exception {
        Comparer.Reach r = Comparer.readCobol(fixture("cobol"));
        assertEquals(Set.of("com.acme.Loan", "com.acme.util.Calendar", "com.acme.Lost"), r.classes);
        assertTrue(r.methods.get("Loan").contains("interest(BigDecimal)"), r.methods.toString());
        assertTrue(r.methods.get("Builder").contains("build()"));
        assertTrue(r.methods.get("Calendar").contains("nextBusinessDay(LocalDate)"));
        assertTrue(Comparer.frameMatches("BigDecimal>", "Map<LocalDate,BigDecimal>"));
        assertTrue(Comparer.frameMatches("BigDecimal[]", "BigDecimal..."));
        assertFalse(Comparer.frameMatches("BigDecimal", "BigMoneyProvider"));
        assertEquals(List.of("BigDecimal", "BigDecimal>", "int"), Comparer.frameParams("BigDecimal, BigDecimal>, int"));
    }

    // ---- type and member alignment over the fixture estate

    @Test
    void typesAlignByNameThenByUniqueSimpleName() throws Exception {
        CompareReport r = compare();
        assertEquals("reached", r.scope);
        Set<String> matched = r.matched.stream().map(d -> d.original + "=" + d.modern + "/" + d.alignment).collect(Collectors.toSet());
        assertEquals(Set.of("com.acme.Loan=com.acme.Loan/fqn", "com.acme.Loan.Builder=com.acme.Loan.Builder/fqn",
            "com.acme.util.Calendar=com.acme.time.Calendar/moved"), matched);
        assertEquals(List.of("com.acme.Lost"), r.originalOnly.stream().map(CompareReport.Missing::fqn).toList());
        assertTrue(r.originalOnly.get(0).reachedByJ3s());
        assertEquals(List.of("com.acme.Rate", "com.acme.Unreached"), r.outOfScope);
        assertEquals(1, r.summary.modernOnlyInfrastructure);
        assertEquals(1, r.summary.modernOnlyOther);
        CompareReport.ModernOnly abend = r.modernOnly.stream().filter(m -> m.fqn().endsWith("Abend")).findFirst().orElseThrow();
        assertEquals("pipeline-infrastructure", abend.category());
        assertEquals("lane:compat", abend.reason());
    }

    @Test
    void memberDifferencesBecomeGaps() throws Exception {
        CompareReport r = compare();
        Set<String> codes = codes(r);
        for (String c : List.of("TYPE.FINAL_LOST", "TYPE.MOVED", "TYPE.MISSING_CLASS", "SUPERTYPE.JDK_INTERFACE_LOST", "FIELD.MISSING_CONSTANT",
            "NAME.TRUNCATED.NONE", "NAME.NUMBERED", "TYPEREF.GENERIC_TO_OBJECT_KNOWN", "TYPEREF.OPTIONAL_UNWRAPPED", "METHOD.UNREACHED_API",
            "METHOD.EXTRA_PRIVATE", "IDIOM.STREAM_LOST", "IDIOM.STREAM_TO_LOOP", "IDIOM.EXCEPTION_MESSAGE_LOST", "TYPE.MODERN_ONLY_CLASS")) {
            assertTrue(codes.contains(c), "expected " + c + " in " + new java.util.TreeSet<>(codes));
        }
        assertFalse(codes.contains("OVERLOAD.COLLAPSED"), "interest(BigDecimal,int) was never compiled, so nothing collapsed");
        assertFalse(codes.stream().anyMatch(c -> c.startsWith("METHOD.MISSING_")), "every reached method is present");
        assertTrue(r.gaps.stream().anyMatch(g -> g.pattern().equals("NAME.TRUNCATED.NONE") && g.member().equals("disbursementsByDueDate")
            && g.modern().equals("disbursementsByDue") && g.detail().equals("field, completion none")), "the field");
        assertEquals("termInMont", gap(r, "NAME.TRUNCATED.UNIQUE_CORRECT", "termInMonths").modern(),
            "the modern field termInMonths completes the truncated parameter");
        assertEquals("bigDecimal2", gap(r, "NAME.NUMBERED", "monthlyRate").modern());
        assertEquals("<init>(BigDecimal,Map,int)", gap(r, "NAME.TRUNCATED.UNIQUE_CORRECT", "termInMonths").member());

        CompareReport.TypeDiff loan = r.matched.stream().filter(d -> d.original.equals("com.acme.Loan")).findFirst().orElseThrow();
        assertEquals(List.of("Comparable<Loan>"), loan.supertypesLost);
        assertTrue(loan.methods.missing.contains("void neverCalled()"));
        assertTrue(loan.methods.extra.contains("BigDecimal helper()"));
        assertEquals(2, loan.idioms.get("stream").original, "only the compiled methods count on the original side");
        assertEquals(2, loan.idioms.get("stream").originalAll);
        assertEquals(0, loan.idioms.get("stream").modern);
        assertTrue(loan.distance > 0);
    }

    @Test
    void ratiosSeparateWhatJ3sNeverCompiled() throws Exception {
        CompareReport.Summary s = compare().summary;
        assertTrue(s.apiReached.total < s.api.total);
        assertTrue(s.apiReached.pct > s.api.pct);
        assertTrue(s.signaturesReached.total < s.signatures.total);
        assertTrue(s.names.total > s.names.kept);
        assertEquals(2, s.nameClasses.get("param").get("truncated"), "termInMont and disbursementsByDue");
        assertEquals(1, s.nameClasses.get("field").get("truncated"));
    }

    @Test
    void compareIsDeterministic() throws Exception {
        assertEquals(CompareWriters.json(compare()), CompareWriters.json(compare()));
        assertEquals(CompareWriters.markdown(compare(), 3), CompareWriters.markdown(compare(), 3));
    }

    @Test
    void lombokMembersAreSynthesisedOnTheOriginalSide() {
        String src = """
            package p;
            import lombok.Builder;
            import lombok.Data;
            import lombok.Getter;
            @Data
            @Builder
            public class Cur {
                private final String code;
                private int digits;
                @Getter private boolean active;
            }
            """;
        var cu = new com.github.javaparser.JavaParser().parse(src).getResult().orElseThrow();
        JavaModel.TypeModel t = JavaModel.extract(cu, "p/Cur.java", src).get(0);
        Set<String> sigs = t.methods.stream().map(m -> m.signature() + "/" + m.origin).collect(Collectors.toSet());
        for (String s : List.of("String getCode()/lombok", "int getDigits()/lombok", "void setDigits(int)/lombok", "boolean isActive()/lombok",
            "<init>(String)/lombok", "CurBuilder builder()/lombok", "boolean equals(Object)/lombok", "String toString()/lombok")) {
            assertTrue(sigs.contains(s), s + " in " + sigs);
        }
        assertFalse(sigs.contains("void setCode(String)/lombok"), "final fields get no setter");
        assertTrue(t.builder);
    }

    @Test
    void recordComponentsBecomeFieldsAccessorsAndCanonicalConstructor() {
        String src = "package p; public record Terms(java.math.BigDecimal principal, int months) { public Terms { } }";
        var cu = new com.github.javaparser.JavaParser(new com.github.javaparser.ParserConfiguration()
            .setLanguageLevel(com.github.javaparser.ParserConfiguration.LanguageLevel.JAVA_21)).parse(src).getResult().orElseThrow();
        JavaModel.TypeModel t = JavaModel.extract(cu, "p/Terms.java", src).get(0);
        assertEquals("record", t.kind);
        assertEquals(List.of("principal", "months"), t.fields.stream().map(f -> f.name).toList());
        Set<String> sigs = t.methods.stream().map(m -> m.signature() + "/" + m.origin).collect(Collectors.toSet());
        assertTrue(sigs.contains("<init>(BigDecimal,int)/source"), sigs.toString());
        assertTrue(sigs.contains("BigDecimal principal()/record"));
        assertTrue(sigs.contains("int hashCode()/record"));
    }

    @Test
    void cliWritesReportsAndAggregatesGaps(@TempDir Path tmp) throws Exception {
        Path out = tmp.resolve("fx");
        assertEquals(0, Main.run(new String[] {"compare", "--original", fixture("original").toString(), "--modern",
            fixture("modern").toString(), "--cobol", fixture("cobol").toString(), "--out", out.toString(), "--quiet"}));
        assertTrue(Files.exists(out.resolve("compare-report.json")));
        assertTrue(Files.readString(out.resolve("compare-report.md")).contains("## Top 3 types by distance"));
        Path cat = tmp.resolve("cat.json");
        Files.writeString(cat, """
            {"title": "T", "patterns": [
              {"id": "G1", "title": "Truncated", "closability": "a", "matches": ["NAME.TRUNCATED*"]},
              {"id": "G2", "title": "Names", "closability": "b", "matches": ["NAME.*"]}]}
            """);
        assertEquals(0, Main.run(new String[] {"compare-gaps", "--report", out.resolve("compare-report.json").toString(),
            "--catalogue", cat.toString(), "--out", tmp.toString()}));
        Gaps.Result res = new com.google.gson.Gson().fromJson(Files.readString(tmp.resolve("compare-gaps.json")), Gaps.Result.class);
        assertEquals(3, res.patterns.get(0).total, "a gap code belongs to the first pattern that matches it");
        assertEquals(1, res.patterns.get(1).total);
        assertTrue(res.unclassified.containsKey("TYPE.MOVED"));
        assertEquals(2, Main.run(new String[] {"compare", "--modern", fixture("modern").toString(), "--quiet"}));
        assertEquals(2, Main.run(new String[] {"compare-gaps"}));
    }

    // ---- structural alignment (--align structural, DT-deep-thinker.md 2.5)

    private static void write(Path root, String rel, String text) throws Exception {
        Path f = root.resolve(rel);
        Files.createDirectories(f.getParent());
        Files.writeString(f, text);
    }

    private static CompareReport shapes(Path tmp, boolean structural) throws Exception {
        Path o = tmp.resolve("o");
        Path m = tmp.resolve("m");
        write(o, "a/LoanTerms.java", "package a; import java.math.BigDecimal; import java.time.LocalDate;\n"
            + "public record LoanTerms(BigDecimal principal, int months, LocalDate start) { public BigDecimal monthlyRate() { return principal; } }\n");
        write(o, "a/Mode.java", "package a; public enum Mode { ANNUITY, LINEAR, BULLET }\n");
        write(m, "modern/batch/Fsem0100Terms.java", "package modern.batch; import java.math.BigDecimal; import java.time.LocalDate;\n"
            + "public record Fsem0100Terms(BigDecimal wsPrin, int wsMonths, LocalDate wsStart) { public BigDecimal wsRate() { return wsPrin; } }\n");
        write(m, "modern/batch/ParmsMode.java", "package modern.batch; public enum ParmsMode { A, L, B }\n");
        // plumbing with the same shapes is never a partner
        write(m, "modern/batch/platform/Terms.java", "package modern.batch.platform; import java.math.BigDecimal; import java.time.LocalDate;\n"
            + "public record Terms(BigDecimal a, int b, LocalDate c) { public BigDecimal d() { return a; } }\n");
        write(m, "modern/batch/platform/Mode3.java", "package modern.batch.platform; public enum Mode3 { X, Y, Z }\n");
        return new Comparer(Config.defaults()).structural(structural).compare("shapes", List.of(o), m, null, "all");
    }

    @Test
    void structuralAlignmentPairsByShapeAndScoresNamesSeparately(@TempDir Path tmp) throws Exception {
        CompareReport byName = shapes(tmp.resolve("n"), false);
        assertEquals(0, byName.summary.matched);
        assertEquals(0, byName.summary.matchedStructural);
        assertTrue(byName.summary.typeNameClasses.isEmpty(), "name mode reports no type names");

        CompareReport r = shapes(tmp.resolve("s"), true);
        assertEquals(2, r.summary.matchedStructural);
        var pairs = r.matched.stream().collect(Collectors.toMap(d -> d.original, d -> d.modern));
        assertEquals("modern.batch.Fsem0100Terms", pairs.get("a.LoanTerms"));
        assertEquals("modern.batch.ParmsMode", pairs.get("a.Mode"));
        assertTrue(r.matched.stream().allMatch(d -> d.alignment.equals("structural") && d.structuralScore >= Comparer.STRUCTURAL_THRESHOLD));
        assertEquals(0, r.summary.typeNames.kept);
        assertEquals(2, r.summary.typeNames.total);
        assertEquals(2, r.summary.typeNameClasses.get("renamed"));
        // the record's members pair by shape too: one accessor-like method with a unique shape
        var terms = r.matched.stream().filter(d -> d.original.equals("a.LoanTerms")).findFirst().orElseThrow();
        assertEquals(3, terms.fields.matched);
        assertEquals(CompareWriters.json(r), CompareWriters.json(shapes(tmp.resolve("s2"), true)).replace(
            tmp.resolve("s2").toAbsolutePath().normalize().toString().replace('\\', '/'),
            tmp.resolve("s").toAbsolutePath().normalize().toString().replace('\\', '/')), "deterministic");
    }

    @Test
    void shapeCategoriesIgnoreNames() {
        Set<String> domain = Set.of("LoanTerms", "Fsem0100Terms");
        assertEquals("decimal", Comparer.category("java.math.BigDecimal", domain));
        assertEquals("integral", Comparer.category("long", domain));
        assertEquals("coll", Comparer.category("List<Installment>", domain));
        assertEquals("D", Comparer.category("LoanTerms", domain));
        assertEquals("D", Comparer.category("Fsem0100Terms", domain));
        assertEquals("other", Comparer.category("org.joda.money.Money", domain));
        assertEquals(50.0, Math.round(100 * Comparer.tokenDice("GeneralLedger", "LedgerEntry")) * 1.0);
    }
}
