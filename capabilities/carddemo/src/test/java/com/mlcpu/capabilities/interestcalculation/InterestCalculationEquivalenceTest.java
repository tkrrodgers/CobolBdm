package com.mlcpu.capabilities.interestcalculation;

import static org.junit.jupiter.api.Assertions.assertEquals;

import com.mlcpu.carddemo.platform.records.Fields;
import com.mlcpu.rt.JobEngine;
import com.mlcpu.rt.JobEngine.Dd;
import com.mlcpu.rt.StepEquivalence;
import java.io.IOException;
import java.math.BigDecimal;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;
import java.util.function.UnaryOperator;
import java.util.stream.Stream;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.Arguments;
import org.junit.jupiter.params.provider.MethodSource;

/** CBACT04C: the normalized InterestCalculationStep against the faithful program and the GnuCOBOL oracle. */
class InterestCalculationEquivalenceTest {
    static final String Q = "AWS.M2.CARDDEMO.";
    static final String TCAT = Q + "TCATBALF.VSAM.KSDS", XREF = Q + "CARDXREF.VSAM.KSDS", ACCT = Q + "ACCTDATA.VSAM.KSDS", DISC = Q + "DISCGRP.VSAM.KSDS";
    static final String PARM = "2022071800";

    @BeforeAll
    static void oracleEnvironment() {
        StepEquivalence.pinOracleEnvironment("2022-07-05T10:11:12-07:00");
    }

    static JobEngine.Step step(String parm) {
        return new JobEngine.Step("STEP15", "CBACT04C", parm, null, com.mlcpu.core.Prog_CBACT04C::new, InterestCalculationStep::new,
                List.of(new Dd("SYSOUT", null, null, null, false, true, null, null),
                        new Dd("TCATBALF", TCAT, null, null, false, false, null, null),
                        new Dd("XREFFILE", XREF, null, null, false, false, null, null),
                        new Dd("XREFFIL1", Q + "CARDXREF.VSAM.AIX.PATH", null, null, false, false, null, null),
                        new Dd("ACCTFILE", ACCT, null, null, false, false, null, null),
                        new Dd("DISCGRP", DISC, null, null, false, false, null, null),
                        new Dd("TRANSACT", Q + "SYSTRAN", null, "+1", false, false, null, 350)));
    }

    static Map<String, byte[]> month() throws IOException {
        return StepEquivalence.resources(InterestCalculationEquivalenceTest.class, "carddemo/in", Q,
                "TCATBALF.VSAM.KSDS", "CARDXREF.VSAM.KSDS", "ACCTDATA.VSAM.KSDS", "DISCGRP.VSAM.KSDS");
    }

    static Map<String, byte[]> with(String dsn, UnaryOperator<byte[]> change) throws IOException {
        Map<String, byte[]> in = new TreeMap<>(month());
        in.put(dsn, change.apply(in.get(dsn)));
        return in;
    }

    static byte[] records(byte[] file, int lrecl, java.util.function.BiFunction<Integer, byte[], byte[]> edit) {
        return StepEquivalence.editRecords(file, lrecl, edit);
    }

    static byte[] money(byte[] r, int off, int len, String value) {
        Fields.putZoned(r, off, len, 2, true, new BigDecimal(value));
        return r;
    }

    static boolean isDefault(byte[] r) {
        return Fields.text(r, 0, 10).equals("DEFAULT");
    }

    static Stream<Arguments> scenarios() throws IOException {
        return Stream.of(
                Arguments.of("CardDemo month", PARM, month()),
                Arguments.of("group rates missing: DEFAULT used", PARM, with(DISC, f -> records(f, 50, (i, r) -> isDefault(r) || i % 2 == 1 ? r : null))),
                Arguments.of("only DEFAULT rates", PARM, with(DISC, f -> records(f, 50, (i, r) -> isDefault(r) ? r : null))),
                Arguments.of("no rates at all (abend)", PARM, with(DISC, f -> new byte[0])),
                Arguments.of("zero rates", PARM, with(DISC, f -> records(f, 50, (i, r) -> money(r, 16, 6, "0")))),
                Arguments.of("negative rates", PARM, with(DISC, f -> records(f, 50, (i, r) -> money(r, 16, 6, "-12.75")))),
                Arguments.of("negative category balances", PARM, with(TCAT, f -> records(f, 50, (i, r) -> {
                    BigDecimal b = Fields.zoned(r, 17, 11, 2);
                    Fields.putZoned(r, 17, 11, 2, true, b.negate());
                    return r;
                }))),
                Arguments.of("interest overflows its picture", PARM, overflow()),
                Arguments.of("one account only (Q-01)", PARM, with(TCAT, f -> java.util.Arrays.copyOfRange(f, 0, 50))),
                Arguments.of("no category balances", PARM, with(TCAT, f -> new byte[0])),
                Arguments.of("account missing (abend)", PARM, with(ACCT, f -> records(f, 300, (i, r) -> i == 0 ? null : r))),
                Arguments.of("cross-reference missing (abend)", PARM, with(XREF, f -> new byte[0])),
                Arguments.of("another run date", "2023123101", month()),
                Arguments.of("positive numbers stored unsigned (F-sign)", PARM, fSigned()),
                Arguments.of("short PARM", "2022", month()));
    }

    static Map<String, byte[]> overflow() throws IOException {
        Map<String, byte[]> in = new TreeMap<>(month());
        in.put(TCAT, records(in.get(TCAT), 50, (i, r) -> money(r, 17, 11, "999999999.99")));
        in.put(DISC, records(in.get(DISC), 50, (i, r) -> money(r, 16, 6, "9999.99")));
        in.put(ACCT, records(in.get(ACCT), 300, (i, r) -> money(r, 12, 12, "9999999999.99")));
        return in;
    }

    static Map<String, byte[]> fSigned() throws IOException {
        Map<String, byte[]> in = new TreeMap<>(month());
        in.put(ACCT, records(in.get(ACCT), 300, (i, r) -> unsignedZone(r, 12, 12, 24, 12, 36, 12, 78, 12, 90, 12)));
        in.put(TCAT, records(in.get(TCAT), 50, (i, r) -> unsignedZone(r, 17, 11)));
        in.put(DISC, records(in.get(DISC), 50, (i, r) -> unsignedZone(r, 16, 6)));
        return in;
    }

    static byte[] unsignedZone(byte[] r, int... fields) {
        for (int k = 0; k < fields.length; k += 2) {
            int last = fields[k] + fields[k + 1] - 1;
            if (r[last] == '{') r[last] = '0';
            else if (r[last] >= 'A' && r[last] <= 'I') r[last] = (byte) ('1' + r[last] - 'A');
        }
        return r;
    }

    @Test
    void normalizedEqualsFaithfulWithSpaceStorageDefault() throws IOException {
        byte saved = com.mlcpu.rt.CobConfig.DEFAULT_BYTE;
        com.mlcpu.rt.CobConfig.DEFAULT_BYTE = ' ';
        try {
            assertEquals(List.of(), StepEquivalence.compare(step(PARM), month()));
        } finally {
            com.mlcpu.rt.CobConfig.DEFAULT_BYTE = saved;
        }
    }

    @ParameterizedTest(name = "{0}")
    @MethodSource("scenarios")
    void normalizedEqualsFaithful(String scenario, String parm, Map<String, byte[]> inputs) throws IOException {
        assertEquals(List.of(), StepEquivalence.compare(step(parm), inputs), scenario);
    }

    @Test
    void normalizedEqualsGnuCobolOracle() throws IOException {
        var expected = new TreeMap<String, byte[]>();
        StepEquivalence.resources(getClass(), "carddemo/oracle", "", "SYSTRAN", "ACCTDATA.VSAM.KSDS", "SYSOUT.txt")
                .forEach((k, v) -> expected.put(k.equals("SYSOUT.txt") ? "sysout/STEP15.SYSOUT.txt" : "data/" + Q + k, v));
        var actual = StepEquivalence.run(step(PARM), month(), "normalized");
        assertEquals(List.of(), StepEquivalence.againstOracle(actual, expected));
        assertEquals(0, actual.returnCode());
    }
}
