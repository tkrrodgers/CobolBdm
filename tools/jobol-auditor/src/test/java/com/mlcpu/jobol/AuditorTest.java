package com.mlcpu.jobol;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.io.IOException;
import java.net.URISyntaxException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Set;
import java.util.stream.Collectors;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

class AuditorTest {
    private static Path fixture(String name) throws URISyntaxException {
        return Path.of(AuditorTest.class.getResource("/fixtures/" + name).toURI());
    }

    private static Report audit(String name) throws Exception {
        return new Auditor(Config.defaults()).audit(fixture(name));
    }

    private static TypeReport type(Report r, String name) {
        return r.types.stream().filter(t -> t.type().equals(name)).findFirst().orElseThrow();
    }

    @Test
    void jobolSampleIsGradedFAndHitsTheCobolRules() throws Exception {
        Report r = audit("jobol");
        TypeReport t = type(r, "PostTranStep");
        Set<String> rules = t.findings().stream().map(Finding::rule).collect(Collectors.toSet());
        for (String expected : Set.of("JOB001", "JOB003", "JOB010", "JOB011", "JOB012", "JOB013", "JOB021", "JOB022",
            "JOB030", "JOB031", "JOB032", "JOB040", "JOB041", "JOB042", "JOB043", "JOB050", "JOB051", "JOB052", "JOB053",
            "JOB055", "JOB057")) {
            assertTrue(rules.contains(expected), "expected " + expected + " in " + rules);
        }
        assertEquals("F", t.grade());
        assertEquals(100, t.index());
        assertFalse(r.gate.passed);
    }

    @Test
    void modernSampleIsCleanAndPassesTheGate() throws Exception {
        Report r = audit("modern");
        for (TypeReport t : r.types) {
            assertEquals(0, t.index(), t.type() + " " + t.findings());
            assertEquals("A", t.grade());
        }
        assertTrue(r.gate.passed, String.valueOf(r.gate.reasons));
        assertEquals(0.0, r.summary.estateIndex);
        assertTrue(r.summary.signalsPerKloc.containsKey("record"));
        assertTrue(r.summary.signalsPerKloc.containsKey("optional"));
    }

    @Test
    void auditIsDeterministic() throws Exception {
        assertEquals(Writers.json(audit("jobol")), Writers.json(audit("jobol")));
    }

    @Test
    void baselineAgainstItselfShowsNoChange(@TempDir Path tmp) throws Exception {
        Report first = audit("jobol");
        Path prev = tmp.resolve("prev.json");
        Files.writeString(prev, Writers.json(first));
        Report again = audit("jobol");
        Auditor auditor = new Auditor(Config.defaults());
        auditor.compare(again, prev);
        assertTrue(again.baseline.comparable);
        assertEquals(0.0, again.baseline.change);
        assertEquals(0, again.baseline.newFindings);
        assertEquals(0, again.baseline.resolvedFindings);
    }

    @Test
    void sarifCarriesEveryAuditedFinding() throws Exception {
        Report r = audit("jobol");
        long findings = r.types.stream().mapToLong(t -> t.findings().size()).sum();
        String sarif = Writers.sarif(r);
        assertTrue(sarif.contains("\"version\": \"2.1.0\""));
        assertEquals(findings, sarif.split("\"ruleId\"", -1).length - 1);
    }

    @Test
    void cliExitCodes(@TempDir Path tmp) throws Exception {
        String out = tmp.toString();
        assertEquals(1, Main.run(new String[] {fixture("jobol").toString(), "--out", out, "--quiet"}));
        assertEquals(0, Main.run(new String[] {fixture("jobol").toString(), "--out", out, "--quiet", "--no-gate"}));
        assertEquals(0, Main.run(new String[] {fixture("modern").toString(), "--out", out, "--quiet"}));
        assertEquals(2, Main.run(new String[] {tmp.resolve("missing").toString(), "--quiet"}));
        assertTrue(Files.exists(tmp.resolve("jobol-report.md")));
    }

    @Test
    void lanesFollowTheDefaultGlobs() throws IOException {
        Config c = Config.defaults();
        assertEquals(Zone.FAITHFUL, c.zoneOf("domain-interest/src/main/java/com/mlcpu/core/Prog_CBACT04C.java"));
        assertEquals(Zone.FAITHFUL, c.zoneOf("estate-common/src/main/java/com/mlcpu/rt/CobolBase.java"));
        assertEquals(Zone.FAITHFUL, c.zoneOf("x/capabilities/interest/legacy/State_X.java"));
        assertEquals(Zone.COMPAT, c.zoneOf("src/main/java/org/apache/fineract/batch/record/EventRecord.java"));
        assertEquals(Zone.COMPAT, c.zoneOf("estate-common/src/main/java/com/mlcpu/mapping/AccountRecordCodec.java"));
        assertEquals(Zone.TEST, c.zoneOf("domain-accounts/src/test/java/com/mlcpu/core/X.java"));
        assertEquals(Zone.MODERN, c.zoneOf("src/main/java/org/apache/fineract/batch/Fsle0100Batch.java"));
        assertEquals(Zone.MODERN, c.zoneOf("Top.java"));
    }

    @Test
    void globSemantics() {
        assertTrue(Glob.compile("**/legacy/**").matcher("legacy/A.java").matches());
        assertTrue(Glob.compile("**/legacy/**").matcher("a/b/legacy/c/A.java").matches());
        assertFalse(Glob.compile("**/legacy/**").matcher("a/legacyx/A.java").matches());
        assertTrue(Glob.compile("**/Prog_*.java").matcher("Prog_X.java").matches());
        assertFalse(Glob.compile("*.java").matcher("a/X.java").matches());
    }

    @Test
    void editMasksAndTokens() {
        assertTrue(Analyzer.isEditMask("ZZZ,ZZZ,ZZ9"));
        assertTrue(Analyzer.isEditMask("ZZ9.99"));
        assertTrue(Analyzer.isEditMask("9,999.99"));
        assertFalse(Analyzer.isEditMask("0.00"));
        assertFalse(Analyzer.isEditMask("1,000"));
        assertEquals(java.util.List.of("ws", "tran", "amount"), Analyzer.tokens("wsTranAmount"));
        assertEquals(java.util.List.of("acct", "id"), Analyzer.tokens("acct_id"));
    }
}
