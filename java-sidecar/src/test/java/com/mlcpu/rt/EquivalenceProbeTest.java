package com.mlcpu.rt;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.math.BigDecimal;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.LocalDate;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Random;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * The library-equivalence probe on synthetic kernels whose function is known: traces are made by calling the kernel, the
 * probe sees only the layout, the 88-level names and the traces, and must name the JDK operation, solve the code maps and
 * verify the statement on generated CALLs through the same kernel.
 */
class EquivalenceProbeTest {

    // ------------------------------------------------------------------ a rounding kernel

    /** RD-PARMS: RD-VALUE S9(15) COMP-3, RD-FN X, RD-PLACES S9(4) COMP, RD-MODE X, RD-RC S9(4) COMP (14 bytes). */
    static final String ROUNDING_LAYOUT = """
        {"schema": "mlcpu.record-layouts.v1", "programId": "RNDKRN", "kind": "subprogram", "linkage": ["RD-PARMS"],
         "layouts": [{"name": "RD-PARMS", "program": "RNDKRN", "length": 14, "fields": [
           {"name": "RD-VALUE", "offset": 0, "length": 8, "kind": "packed", "pic": "S9(15)", "digits": 15, "scale": 0, "signed": true},
           {"name": "RD-FN", "offset": 8, "length": 1, "kind": "text", "pic": "X", "digits": 0, "scale": 0, "signed": false},
           {"name": "RD-PLACES", "offset": 9, "length": 2, "kind": "binary", "pic": "S9(4)", "digits": 4, "scale": 0, "signed": true},
           {"name": "RD-MODE", "offset": 11, "length": 1, "kind": "text", "pic": "X", "digits": 0, "scale": 0, "signed": false},
           {"name": "RD-RC", "offset": 12, "length": 2, "kind": "binary", "pic": "S9(4)", "digits": 4, "scale": 0, "signed": true}]}]}
        """;

    static final String ROUNDING_CONDITIONS = """
        {"schema": "mlcpu.condition-names.v1", "fields": {
          "RD-FN": [{"name": "RD-FN-ROUND-PLACES", "values": ["R"]}, {"name": "RD-FN-SIGNIFICANT", "values": ["S"]},
                    {"name": "RD-FN-SCRAMBLE", "values": ["X"]}],
          "RD-MODE": [{"name": "RD-MODE-UP", "values": ["U"]}, {"name": "RD-MODE-DOWN", "values": ["D"]},
                      {"name": "RD-MODE-HALF-UP", "values": ["H"]}, {"name": "RD-MODE-HALF-EVEN", "values": ["E"]}]}}
        """;

    static CobField field(byte[] b, int off, int len, CobField.Use use, int digits) {
        Area a = new Area(b.length);
        a.b = b;
        return CobField.num("F", use, a, off, len, digits, 0, true, false, false, null, null);
    }

    static byte[] rdParms(long value, char fn, int places, char mode) {
        byte[] b = new byte[14];
        field(b, 0, 8, CobField.Use.PACKED, 15).setDec(BigDecimal.valueOf(value), false);
        b[8] = (byte) fn;
        field(b, 9, 2, CobField.Use.BINARY, 4).setDec(BigDecimal.valueOf(places), false);
        b[11] = (byte) mode;
        return b;
    }

    /**
     * The kernel, written the way COBOL kernels are: integer digit arithmetic, no BigDecimal. 'R' drops RD-PLACES digits
     * (rounds to a multiple of 10^places), 'S' keeps RD-PLACES significant digits, 'X' is not a library operation.
     */
    static void roundingKernel(byte[][] params) {
        byte[] b = params[0];
        long v = field(b, 0, 8, CobField.Use.PACKED, 15).dec().longValue();
        int places = field(b, 9, 2, CobField.Use.BINARY, 4).dec().intValue();
        char fn = (char) b[8], mode = (char) b[11];
        CobField rc = field(b, 12, 2, CobField.Use.BINARY, 4);
        rc.setDec(BigDecimal.ZERO, false);
        long abs = Math.abs(v);
        int drop;
        switch (fn) {
            case 'R' -> drop = places;
            case 'S' -> drop = String.valueOf(abs).length() - places;
            case 'X' -> {
                field(b, 0, 8, CobField.Use.PACKED, 15).setDec(BigDecimal.valueOf(v / 3 * 7 + 1), false);
                return;
            }
            default -> {
                rc.setDec(BigDecimal.valueOf(12), false);
                return;
            }
        }
        if (drop <= 0) return;
        if (drop > 14) {
            rc.setDec(BigDecimal.valueOf(12), false);
            return;
        }
        long pow = 1;
        for (int i = 0; i < drop; i++) pow *= 10;
        long q = abs / pow, r = abs % pow, half = pow / 2;
        boolean up = switch (mode) {
            case 'U' -> r != 0;
            case 'H' -> r >= half;
            case 'E' -> r > half || (r == half && q % 2 == 1);
            default -> false;
        };
        if (up) q++;
        long out = q * pow * (v < 0 ? -1 : 1);
        field(b, 0, 8, CobField.Use.PACKED, 15).setDec(BigDecimal.valueOf(out), false);
    }

    static String traceJson(String program, List<byte[][]> before, List<byte[][]> after) {
        StringBuilder sb = new StringBuilder("{\"schema\":\"mlcpu.call-trace.v1\",\"program\":\"HOST\",\"traced\":\"" + program + "\",\"calls\":[");
        for (int i = 0; i < before.size(); i++) {
            if (i > 0) sb.append(',');
            sb.append("{\"seq\":").append(i + 1).append(",\"before\":[").append(CallTrace.q(new String(before.get(i)[0], StandardCharsets.ISO_8859_1)))
              .append("],\"after\":[").append(CallTrace.q(new String(after.get(i)[0], StandardCharsets.ISO_8859_1))).append("]}");
        }
        return sb.append("]}\n").toString();
    }

    static EquivalenceProbe.GroupResult group(EquivalenceProbe.Report r, String code) {
        for (EquivalenceProbe.GroupResult g : r.groups)
            if (g.g.key.values().iterator().next().equals(code)) return g;
        throw new AssertionError("no group " + code + " in\n" + r.summary());
    }

    @Test
    void namesTheRoundingOperationsSolvesTheModeCodesAndRejectsWhatIsNotALibraryOperation(@TempDir Path dir) throws Exception {
        Random rnd = new Random(7);
        List<byte[][]> before = new ArrayList<>(), after = new ArrayList<>();
        char[] fns = {'R', 'S', 'X'}, modes = {'U', 'D', 'H', 'E'};
        for (int i = 0; i < 400; i++) {
            long v = (long) (rnd.nextDouble() * 1_000_000_000L) * (rnd.nextInt(5) == 0 ? -1 : 1);
            if (rnd.nextInt(4) == 0) v = v / 1000 * 1000 + 500; // ties
            char fn = fns[rnd.nextInt(3)];
            // Zero significant digits is not MathContext(0) (which means unlimited precision): the caller here never asks
            // for it, so every traced CALL agrees with the statement.
            int places = fn == 'S' ? 1 + rnd.nextInt(6) : rnd.nextInt(7);
            byte[] b = rdParms(v, fn, places, modes[rnd.nextInt(4)]);
            before.add(new byte[][]{b.clone()});
            byte[][] p = {b};
            roundingKernel(p);
            after.add(p);
        }
        Files.writeString(dir.resolve("layouts.json"), ROUNDING_LAYOUT);
        Files.writeString(dir.resolve("conditions.json"), ROUNDING_CONDITIONS);
        Files.createDirectories(dir.resolve("traces/scenario1"));
        Files.writeString(dir.resolve("traces/scenario1/trace.json"), traceJson("RNDKRN", before, after));

        EquivalenceProbe.Options o = new EquivalenceProbe.Options();
        o.program = "RNDKRN";
        o.layouts = dir.resolve("layouts.json");
        o.conditions = dir.resolve("conditions.json");
        o.traces = List.of(dir.resolve("traces"));
        o.generate = 5_000;
        o.learn = 1_000;
        EquivalenceProbe.Report r = EquivalenceProbe.run(o, EquivalenceProbeTest::roundingKernel);

        assertEquals(400, r.traced);
        assertEquals("RD-FN", r.key.name);
        EquivalenceProbe.GroupResult places = group(r, "R");
        assertEquals("verified", places.status(), r.summary());
        assertEquals("rdValue.setScale(-rdPlaces, mode)", places.model.h.ex.java());
        assertEquals(5_000, places.generatedOk);
        Map<String, Object> map = EquivalenceProbe.Report.enumJson(places.model);
        assertEquals(Map.of("U", "UP", "D", "DOWN", "H", "HALF_UP", "E", "HALF_EVEN"), map.get("map"));

        // The traced CALLs never ask 'S' for 0 digits; the generated ones explore RD-PLACES down to 0, where this kernel
        // (zero significant digits) and MathContext(0) (unlimited precision) differ: the statement holds with that condition.
        EquivalenceProbe.GroupResult sig = group(r, "S");
        assertEquals("verified-with-condition", sig.status(), r.summary());
        assertEquals("rdValue.round(new MathContext(rdPlaces, mode))", sig.model.h.ex.java());
        assertEquals(sig.g.traced.size(), sig.model.tracedOk);
        assertTrue(sig.regions.stream().allMatch(fr -> fr.words.contains("RD-PLACES = 0")), r.summary());

        assertEquals("unexplained", group(r, "X").status(), r.summary());

        String md = r.markdown();
        assertTrue(md.contains("## RD-FN = 'R' (RD-FN-ROUND-PLACES)"), md);
        assertTrue(md.contains("`r = rdValue.setScale(-rdPlaces, mode)`"), md);
        assertTrue(md.contains("'E' HALF_EVEN"), md);
        assertTrue(md.contains("## Not explained by a library operation") && md.contains("RD-FN = 'X'"), md);
        Object json = MiniJson.parse(r.json());
        assertNotNull(json);
        assertEquals(2, r.verifiedGroups());
    }

    // ------------------------------------------------------------------ a date kernel

    /** DT: DT-FN X(2), DT-UNIT X, DT-DAY S9(9) COMP, DT-N S9(9) COMP, DT-RESULT S9(9) COMP, DT-YMD 9(8) (25 bytes). */
    static final String DATE_LAYOUT = """
        {"schema": "mlcpu.record-layouts.v1", "programId": "DTKRN", "kind": "subprogram", "linkage": ["DT"],
         "layouts": [{"name": "DT", "program": "DTKRN", "length": 25, "fields": [
           {"name": "DT-FN", "offset": 0, "length": 2, "kind": "text", "pic": "X(2)", "digits": 0, "scale": 0, "signed": false},
           {"name": "DT-UNIT", "offset": 2, "length": 1, "kind": "text", "pic": "X", "digits": 0, "scale": 0, "signed": false},
           {"name": "DT-DAY", "offset": 3, "length": 4, "kind": "binary", "pic": "S9(9)", "digits": 9, "scale": 0, "signed": true},
           {"name": "DT-N", "offset": 7, "length": 4, "kind": "binary", "pic": "S9(9)", "digits": 9, "scale": 0, "signed": true},
           {"name": "DT-RESULT", "offset": 11, "length": 4, "kind": "binary", "pic": "S9(9)", "digits": 9, "scale": 0, "signed": true},
           {"name": "DT-YMD", "offset": 15, "length": 8, "kind": "zoned", "pic": "9(8)", "digits": 8, "scale": 0, "signed": false},
           {"name": "DT-RC", "offset": 23, "length": 2, "kind": "binary", "pic": "S9(4)", "digits": 4, "scale": 0, "signed": true}]}]}
        """;

    static final String DATE_CONDITIONS = """
        {"schema": "mlcpu.condition-names.v1", "fields": {
          "DT-FN": [{"name": "DT-FN-PLUS", "values": ["PL"]}, {"name": "DT-FN-DATE-OF-DAY", "values": ["YM"]}],
          "DT-UNIT": [{"name": "DT-UNIT-DAYS", "values": ["D"]}, {"name": "DT-UNIT-MONTHS", "values": ["M"]}]}}
        """;

    static final LocalDate DAY_ZERO = LocalDate.of(1600, 12, 31);

    static CobField dtField(byte[] b, int off, int len, CobField.Use use, int digits, boolean signed) {
        Area a = new Area(b.length);
        a.b = b;
        return CobField.num("F", use, a, off, len, digits, 0, signed, false, false, null, null);
    }

    static byte[] dtParms(String fn, char unit, long day, long n) {
        byte[] b = new byte[25];
        b[0] = (byte) fn.charAt(0);
        b[1] = (byte) fn.charAt(1);
        b[2] = (byte) unit;
        dtField(b, 3, 4, CobField.Use.BINARY, 9, true).setDec(BigDecimal.valueOf(day), false);
        dtField(b, 7, 4, CobField.Use.BINARY, 9, true).setDec(BigDecimal.valueOf(n), false);
        dtField(b, 15, 8, CobField.Use.DISPLAY, 8, false).setDec(BigDecimal.ZERO, false);
        return b;
    }

    /** Day numbers count from 1601-01-01 = 1 (COBOL INTEGER-OF-DATE). PL adds DT-N days or months; YM writes the date. */
    static void dateKernel(byte[][] params) {
        byte[] b = params[0];
        String fn = new String(b, 0, 2, StandardCharsets.ISO_8859_1);
        LocalDate d = DAY_ZERO.plusDays(dtField(b, 3, 4, CobField.Use.BINARY, 9, true).dec().longValue());
        long n = dtField(b, 7, 4, CobField.Use.BINARY, 9, true).dec().longValue();
        if (fn.equals("PL")) {
            LocalDate r = b[2] == 'M' ? d.plusMonths(n) : d.plusDays(n);
            dtField(b, 11, 4, CobField.Use.BINARY, 9, true).setDec(BigDecimal.valueOf(r.toEpochDay() - DAY_ZERO.toEpochDay()), false);
        } else {
            dtField(b, 15, 8, CobField.Use.DISPLAY, 8, false)
                .setDec(BigDecimal.valueOf(d.getYear() * 10000L + d.getMonthValue() * 100L + d.getDayOfMonth()), false);
        }
    }

    @Test
    void solvesTheDayNumberEpochAndTheUnitCodes(@TempDir Path dir) throws Exception {
        Random rnd = new Random(11);
        List<byte[][]> before = new ArrayList<>(), after = new ArrayList<>();
        for (int i = 0; i < 300; i++) {
            long day = 153_000 + rnd.nextInt(3_000);
            byte[] b = dtParms(rnd.nextBoolean() ? "PL" : "YM", rnd.nextBoolean() ? 'D' : 'M', day, 1 + rnd.nextInt(40));
            before.add(new byte[][]{b.clone()});
            byte[][] p = {b};
            dateKernel(p);
            after.add(p);
        }
        Files.writeString(dir.resolve("layouts.json"), DATE_LAYOUT);
        Files.writeString(dir.resolve("conditions.json"), DATE_CONDITIONS);
        Files.writeString(dir.resolve("trace.json"), traceJson("DTKRN", before, after));
        EquivalenceProbe.Options o = new EquivalenceProbe.Options();
        o.program = "DTKRN";
        o.layouts = dir.resolve("layouts.json");
        o.conditions = dir.resolve("conditions.json");
        o.traces = List.of(dir.resolve("trace.json"));
        o.generate = 3_000;
        o.learn = 500;
        EquivalenceProbe.Report r = EquivalenceProbe.run(o, EquivalenceProbeTest::dateKernel);

        assertTrue(r.epochs.contains("INTEGER-OF-DATE"), r.epochs);
        EquivalenceProbe.GroupResult plus = group(r, "PL");
        assertEquals("verified", plus.status(), r.summary());
        assertEquals("dtDayDate.plus(dtN, unit)", plus.model.h.ex.java());
        assertEquals(Map.of("D", "DAYS", "M", "MONTHS"), EquivalenceProbe.Report.enumJson(plus.model).get("map"));
        EquivalenceProbe.GroupResult ymd = group(r, "YM");
        assertEquals("verified", ymd.status(), r.summary());
        assertEquals("DT-YMD", ymd.model.h.target.fields().get(0).name);
    }

    // ------------------------------------------------------------------ a money kernel with a reject path

    /**
     * MN-PARMS: MN-FN X ('O' amount of, 'Z' a function that always fails), MN-SITE X(8), MN-AMOUNT S9(13)V9(4) COMP-3,
     * MN-PLACES S9(4) COMP, MN-MODE X, MN-MULT-IND X, MN-RESULT S9(13)V9(4) COMP-3, MN-RC S9(4) COMP, MN-REJ-CODE X(8),
     * MN-REJ-SRC X(8) (55 bytes).
     */
    static final String MONEY_LAYOUT = """
        {"schema": "mlcpu.record-layouts.v1", "programId": "MNKRN", "kind": "subprogram", "linkage": ["MN-PARMS"],
         "layouts": [{"name": "MN-PARMS", "program": "MNKRN", "length": 55, "fields": [
           {"name": "MN-FN", "offset": 0, "length": 1, "kind": "text", "pic": "X", "digits": 0, "scale": 0, "signed": false},
           {"name": "MN-SITE", "offset": 1, "length": 8, "kind": "text", "pic": "X(8)", "digits": 0, "scale": 0, "signed": false},
           {"name": "MN-AMOUNT", "offset": 9, "length": 9, "kind": "packed", "pic": "S9(13)V9(4)", "digits": 17, "scale": 4, "signed": true},
           {"name": "MN-PLACES", "offset": 18, "length": 2, "kind": "binary", "pic": "S9(4)", "digits": 4, "scale": 0, "signed": true},
           {"name": "MN-MODE", "offset": 20, "length": 1, "kind": "text", "pic": "X", "digits": 0, "scale": 0, "signed": false},
           {"name": "MN-MULT-IND", "offset": 21, "length": 1, "kind": "text", "pic": "X", "digits": 0, "scale": 0, "signed": false},
           {"name": "MN-RESULT", "offset": 22, "length": 9, "kind": "packed", "pic": "S9(13)V9(4)", "digits": 17, "scale": 4, "signed": true},
           {"name": "MN-RC", "offset": 31, "length": 2, "kind": "binary", "pic": "S9(4)", "digits": 4, "scale": 0, "signed": true},
           {"name": "MN-REJ-CODE", "offset": 33, "length": 8, "kind": "text", "pic": "X(8)", "digits": 0, "scale": 0, "signed": false},
           {"name": "MN-REJ-SRC", "offset": 41, "length": 8, "kind": "text", "pic": "X(8)", "digits": 0, "scale": 0, "signed": false},
           {"name": "FILLER@49", "offset": 49, "length": 6, "kind": "text", "pic": "X(6)", "digits": 0, "scale": 0, "signed": false}]}]}
        """;

    static final String MONEY_CONDITIONS = """
        {"schema": "mlcpu.condition-names.v1", "fields": {
          "MN-FN": [{"name": "MN-FN-AMOUNT-OF", "values": ["O"]}, {"name": "MN-FN-BROKEN", "values": ["Z"]}],
          "MN-MODE": [{"name": "MN-MODE-UP", "values": ["U"]}, {"name": "MN-MODE-DOWN", "values": ["D"]},
                      {"name": "MN-MODE-HALF-EVEN", "values": ["E"]}]}}
        """;

    static CobField money(byte[] b, int off) {
        Area a = new Area(b.length);
        a.b = b;
        return CobField.num("M", CobField.Use.PACKED, a, off, 9, 17, 4, true, false, false, null, null);
    }

    static void text(byte[] b, int off, int len, String v) {
        byte[] t = String.format("%-" + len + "s", v).getBytes(StandardCharsets.ISO_8859_1);
        System.arraycopy(t, 0, b, off, len);
    }

    /**
     * 'O': MN-RESULT = MN-AMOUNT rounded to MN-PLACES decimals (mode by MN-MODE), except the tenant-rounding reject:
     * with MN-PLACES = 0 and MN-MULT-IND = 'Y' it sets MN-RC = 4, MN-REJ-CODE = 'REJ00001' and MN-REJ-SRC = the caller's
     * MN-SITE, and leaves MN-RESULT alone. 'Z': MN-RC = 12, nothing else.
     */
    static void moneyKernel(byte[][] params) {
        byte[] b = params[0];
        CobField rc = field(b, 31, 2, CobField.Use.BINARY, 4);
        if (b[0] == 'Z') {
            rc.setDec(BigDecimal.valueOf(12), false);
            return;
        }
        rc.setDec(BigDecimal.ZERO, false);
        int places = field(b, 18, 2, CobField.Use.BINARY, 4).dec().intValue();
        if (places == 0 && b[21] == 'Y') {
            rc.setDec(BigDecimal.valueOf(4), false);
            text(b, 33, 8, "REJ00001");
            System.arraycopy(b, 1, b, 41, 8);
            return;
        }
        java.math.RoundingMode m = b[20] == 'U' ? java.math.RoundingMode.UP : b[20] == 'D' ? java.math.RoundingMode.DOWN : java.math.RoundingMode.HALF_EVEN;
        money(b, 22).setDec(money(b, 9).dec().setScale(places, m), false);
    }

    @Test
    void learnsTheContractOfTheRejectPathAndOfAnErrorFunction(@TempDir Path dir) throws Exception {
        Random rnd = new Random(5);
        List<byte[][]> before = new ArrayList<>(), after = new ArrayList<>();
        String[] sites = {"LAT:580", "LAT:331", "RP:287", "EA:35"};
        char[] modes = {'U', 'D', 'E'};
        for (int i = 0; i < 400; i++) {
            byte[] b = new byte[55];
            text(b, 0, 55, "");
            b[0] = (byte) (rnd.nextInt(8) == 0 ? 'Z' : 'O');
            text(b, 1, 8, sites[rnd.nextInt(sites.length)]);
            money(b, 9).setDec(BigDecimal.valueOf(rnd.nextInt(100_000_000), 4).multiply(BigDecimal.valueOf(rnd.nextInt(5) == 0 ? -1 : 1)), false);
            field(b, 18, 2, CobField.Use.BINARY, 4).setDec(BigDecimal.valueOf(rnd.nextInt(4)), false);
            b[20] = (byte) modes[rnd.nextInt(3)];
            b[21] = (byte) (rnd.nextInt(3) == 0 ? 'Y' : 'N');
            money(b, 22).setDec(BigDecimal.ZERO, false);
            field(b, 31, 2, CobField.Use.BINARY, 4).setDec(BigDecimal.ZERO, false);
            // the caller never passes MN-PLACES = 0 with MN-MULT-IND = 'Y': the traces hold no reject
            if (b[0] == 'O' && b[18] == 0 && b[19] == 0 && b[21] == 'Y') b[21] = 'N';
            before.add(new byte[][]{b.clone()});
            byte[][] p = {b};
            moneyKernel(p);
            after.add(p);
        }
        Files.writeString(dir.resolve("layouts.json"), MONEY_LAYOUT);
        Files.writeString(dir.resolve("conditions.json"), MONEY_CONDITIONS);
        Files.writeString(dir.resolve("trace.json"), traceJson("MNKRN", before, after));
        EquivalenceProbe.Options o = new EquivalenceProbe.Options();
        o.program = "MNKRN";
        o.layouts = dir.resolve("layouts.json");
        o.conditions = dir.resolve("conditions.json");
        o.traces = List.of(dir.resolve("trace.json"));
        o.generate = 20_000;
        o.learn = 1_000;
        EquivalenceProbe.Report r = EquivalenceProbe.run(o, EquivalenceProbeTest::moneyKernel);

        EquivalenceProbe.GroupResult amount = group(r, "O");
        assertEquals("verified-with-condition", amount.status(), r.summary());
        assertEquals("mnAmount.setScale(mnPlaces, mode)", amount.model.h.ex.java());
        assertEquals(1, amount.regions.size(), r.summary());
        List<String> when = amount.regions.get(0).words;
        assertTrue(when.contains("MN-PLACES = 0") && when.contains("MN-MULT-IND = 'Y'"), String.valueOf(when));
        EquivalenceProbe.Contract k = amount.regions.get(0).contract;
        assertNotNull(k);
        assertTrue(k.holds(), k.effects().toString());
        assertTrue(k.calls >= 1_000, String.valueOf(k.calls));
        assertEquals(List.of("MN-RC = 4", "MN-REJ-CODE = 'REJ00001'", "MN-REJ-SRC = MN-SITE (the value the caller passed)", "every other field unchanged"),
            k.effects());

        EquivalenceProbe.GroupResult broken = group(r, "Z");
        assertEquals("verified-contract", broken.status(), r.summary());
        assertEquals(List.of("MN-RC = 12", "every other field unchanged"), broken.errorContract.effects());
        assertEquals(broken.generated, broken.generatedOk);

        String md = r.markdown();
        assertTrue(md.contains("where it does not hold, when MN-PLACES = 0 and MN-MULT-IND = 'Y': MN-RC = 4; MN-REJ-CODE = 'REJ00001'; "
                               + "MN-REJ-SRC = MN-SITE (the value the caller passed); every other field unchanged"), md);
        assertTrue(md.contains("## MN-FN = 'Z' (MN-FN-BROKEN)") && md.contains("MN-RC = 12; every other field unchanged"), md);
    }
}
