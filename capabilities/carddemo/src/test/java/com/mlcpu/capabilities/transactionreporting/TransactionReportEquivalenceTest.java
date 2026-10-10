package com.mlcpu.capabilities.transactionreporting;

import static org.junit.jupiter.api.Assertions.assertEquals;

import com.mlcpu.carddemo.platform.records.Fields;
import com.mlcpu.rt.JobEngine;
import com.mlcpu.rt.JobEngine.Dd;
import com.mlcpu.rt.StepEquivalence;
import java.io.IOException;
import java.math.BigDecimal;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
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

/** CBTRN03C: the normalized TransactionReportStep against the faithful program and the GnuCOBOL oracle. */
class TransactionReportEquivalenceTest {
    static final String Q = "AWS.M2.CARDDEMO.";
    static final String TRAN = Q + "TRANSACT.DALY", XREF = Q + "CARDXREF.VSAM.KSDS", TYPE = Q + "TRANTYPE.VSAM.KSDS", CATG = Q + "TRANCATG.VSAM.KSDS",
            DATES = Q + "DATEPARM";

    @BeforeAll
    static void oracleEnvironment() {
        StepEquivalence.pinOracleEnvironment("2022-07-05T10:11:12-07:00");
    }

    static JobEngine.Step step() {
        return new JobEngine.Step("STEP10R", "CBTRN03C", null, null, com.mlcpu.core.Prog_CBTRN03C::new, TransactionReportStep::new,
                List.of(new Dd("SYSOUT", null, null, null, false, true, null, null),
                        new Dd("TRANFILE", TRAN, null, "+1", false, false, null, null),
                        new Dd("CARDXREF", XREF, null, null, false, false, null, null),
                        new Dd("TRANTYPE", TYPE, null, null, false, false, null, null),
                        new Dd("TRANCATG", CATG, null, null, false, false, null, null),
                        new Dd("DATEPARM", DATES, null, null, false, false, null, null),
                        new Dd("TRANREPT", Q + "TRANREPT", null, "+1", false, false, null, 133)));
    }

    static Map<String, byte[]> sorted() throws IOException {
        return StepEquivalence.resources(TransactionReportEquivalenceTest.class, "carddemo/in", Q,
                "TRANSACT.DALY", "CARDXREF.VSAM.KSDS", "TRANTYPE.VSAM.KSDS", "TRANCATG.VSAM.KSDS", "DATEPARM");
    }

    static Map<String, byte[]> with(String dsn, UnaryOperator<byte[]> change) throws IOException {
        Map<String, byte[]> in = new TreeMap<>(sorted());
        in.put(dsn, change.apply(in.get(dsn)));
        return in;
    }

    static byte[] records(byte[] file, java.util.function.BiFunction<Integer, byte[], byte[]> edit) {
        return StepEquivalence.editRecords(file, 350, edit);
    }

    static byte[] first(int n) throws IOException {
        byte[] all = sorted().get(TRAN);
        return Arrays.copyOfRange(all, 0, Math.min(all.length, n * 350));
    }

    static byte[] period(String from, String to) {
        byte[] r = new byte[80];
        Arrays.fill(r, (byte) ' ');
        byte[] t = (from + " " + to).getBytes(StandardCharsets.ISO_8859_1);
        System.arraycopy(t, 0, r, 0, t.length);
        return r;
    }

    static Stream<Arguments> scenarios() throws IOException {
        return Stream.of(
                Arguments.of("CardDemo report", sorted()),
                Arguments.of("one transaction", with(TRAN, f -> unchecked(() -> first(1)))),
                Arguments.of("seven transactions", with(TRAN, f -> unchecked(() -> first(7)))),
                Arguments.of("nineteen transactions", with(TRAN, f -> unchecked(() -> first(19)))),
                Arguments.of("twenty transactions", with(TRAN, f -> unchecked(() -> first(20)))),
                Arguments.of("twenty-one transactions", with(TRAN, f -> unchecked(() -> first(21)))),
                Arguments.of("forty transactions", with(TRAN, f -> unchecked(() -> first(40)))),
                Arguments.of("no transactions", with(TRAN, f -> new byte[0])),
                Arguments.of("no date parameters", with(DATES, f -> new byte[0])),
                Arguments.of("period ends mid-file (Q-01 stops early)", with(DATES, f -> period("2022-01-01", "2022-06-20"))),
                Arguments.of("period before every transaction", with(DATES, f -> period("1999-01-01", "1999-12-31"))),
                Arguments.of("negative amounts", with(TRAN, f -> records(f, (i, r) -> {
                    if (i % 3 == 0) Fields.putZoned(r, 132, 11, 2, true, Fields.zoned(r, 132, 11, 2).negate());
                    return r;
                }))),
                Arguments.of("totals overflow their pictures", with(TRAN, f -> records(f, (i, r) -> {
                    Fields.putZoned(r, 132, 11, 2, true, new BigDecimal(i % 2 == 0 ? "999999999.99" : "-999999990.01"));
                    return r;
                }))),
                Arguments.of("zero amounts", with(TRAN, f -> records(f, (i, r) -> {
                    Fields.putZoned(r, 132, 11, 2, true, BigDecimal.ZERO);
                    return r;
                }))),
                Arguments.of("period boundaries are inclusive", periodBoundaries()),
                Arguments.of("blank processed timestamp ends the report (Q-01)", with(TRAN, f -> records(f, (i, r) -> {
                    if (i == 4) Fields.putText(r, 304, 26, "");
                    return r;
                }))),
                Arguments.of("amounts stored unsigned (F-sign)", with(TRAN, f -> records(f, (i, r) -> {
                    int last = 142;
                    if (r[last] == '{') r[last] = '0';
                    else if (r[last] >= 'A' && r[last] <= 'I') r[last] = (byte) ('1' + r[last] - 'A');
                    return r;
                }))),
                Arguments.of("unknown card (abend)", with(TRAN, f -> records(f, (i, r) -> {
                    if (i == 5) Fields.putText(r, 262, 16, "9999999999999999");
                    return r;
                }))),
                Arguments.of("unknown transaction type (abend)", with(TRAN, f -> records(f, (i, r) -> {
                    if (i == 3) Fields.putText(r, 16, 2, "99");
                    return r;
                }))),
                Arguments.of("unknown category (abend)", with(TRAN, f -> records(f, (i, r) -> {
                    if (i == 2) Fields.putUnsigned(r, 18, 4, 9999);
                    return r;
                }))),
                Arguments.of("date parameter file missing (abend)", without(DATES)));
    }

    interface IoSupplier {
        byte[] get() throws IOException;
    }

    static byte[] unchecked(IoSupplier s) {
        try {
            return s.get();
        } catch (IOException e) {
            throw new java.io.UncheckedIOException(e);
        }
    }

    static Map<String, byte[]> without(String dsn) throws IOException {
        Map<String, byte[]> in = new TreeMap<>(sorted());
        in.remove(dsn);
        return in;
    }

    /** Transactions dated exactly on the first and the last day of the DATEPARM period. */
    static Map<String, byte[]> periodBoundaries() throws IOException {
        Map<String, byte[]> in = new TreeMap<>(sorted());
        String parm = new String(in.get(DATES), 0, 21, StandardCharsets.ISO_8859_1);
        byte[] start = parm.substring(0, 10).getBytes(StandardCharsets.ISO_8859_1), end = parm.substring(11, 21).getBytes(StandardCharsets.ISO_8859_1);
        in.put(TRAN, records(in.get(TRAN), (i, r) -> {
            System.arraycopy(i % 2 == 0 ? start : end, 0, r, 304, 10);
            return r;
        }));
        return in;
    }

    @Test
    void normalizedEqualsFaithfulWithSpaceStorageDefault() throws IOException {
        byte saved = com.mlcpu.rt.CobConfig.DEFAULT_BYTE;
        com.mlcpu.rt.CobConfig.DEFAULT_BYTE = ' ';
        try {
            assertEquals(List.of(), StepEquivalence.compare(step(), sorted()));
        } finally {
            com.mlcpu.rt.CobConfig.DEFAULT_BYTE = saved;
        }
    }

    @ParameterizedTest(name = "{0}")
    @MethodSource("scenarios")
    void normalizedEqualsFaithful(String scenario, Map<String, byte[]> inputs) throws IOException {
        assertEquals(List.of(), StepEquivalence.compare(step(), inputs), scenario);
    }

    @Test
    void normalizedEqualsGnuCobolOracle() throws IOException {
        var expected = new TreeMap<String, byte[]>();
        StepEquivalence.resources(getClass(), "carddemo/oracle", "", "TRANREPT", "SYSOUT.txt")
                .forEach((k, v) -> expected.put(k.equals("SYSOUT.txt") ? "sysout/STEP10R.SYSOUT.txt" : "data/" + Q + k, v));
        var actual = StepEquivalence.run(step(), sorted(), "normalized");
        assertEquals(List.of(), StepEquivalence.againstOracle(actual, expected));
        assertEquals(0, actual.returnCode());
    }
}
