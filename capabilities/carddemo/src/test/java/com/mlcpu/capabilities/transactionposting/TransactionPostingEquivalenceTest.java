package com.mlcpu.capabilities.transactionposting;

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

/**
 * CBTRN02C: the normalized TransactionPostingStep against the faithful program (every scenario, byte for byte, all
 * datasets and SYSOUT) and against the GnuCOBOL oracle (the CardDemo day).
 */
class TransactionPostingEquivalenceTest {
    static final String Q = "AWS.M2.CARDDEMO.";
    static final String DALY = Q + "DALYTRAN.PS", TRAN = Q + "TRANSACT.VSAM.KSDS", XREF = Q + "CARDXREF.VSAM.KSDS", ACCT = Q + "ACCTDATA.VSAM.KSDS",
            TCAT = Q + "TCATBALF.VSAM.KSDS";

    @BeforeAll
    static void oracleEnvironment() {
        StepEquivalence.pinOracleEnvironment("2022-07-05T10:11:12-07:00");
    }

    static JobEngine.Step step() {
        return new JobEngine.Step("STEP15", "CBTRN02C", null, null, com.mlcpu.core.Prog_CBTRN02C::new, TransactionPostingStep::new,
                List.of(new Dd("SYSOUT", null, null, null, false, true, null, null),
                        new Dd("TRANFILE", TRAN, null, null, false, false, null, null),
                        new Dd("DALYTRAN", DALY, null, null, false, false, null, null),
                        new Dd("XREFFILE", XREF, null, null, false, false, null, null),
                        new Dd("DALYREJS", Q + "DALYREJS", null, "+1", false, false, null, 430),
                        new Dd("ACCTFILE", ACCT, null, null, false, false, null, null),
                        new Dd("TCATBALF", TCAT, null, null, false, false, null, null)));
    }

    static Map<String, byte[]> day() throws IOException {
        return StepEquivalence.resources(TransactionPostingEquivalenceTest.class, "carddemo/in", Q,
                "DALYTRAN.PS", "TRANSACT.VSAM.KSDS", "CARDXREF.VSAM.KSDS", "ACCTDATA.VSAM.KSDS", "TCATBALF.VSAM.KSDS");
    }

    static Map<String, byte[]> with(String dsn, UnaryOperator<byte[]> change) throws IOException {
        Map<String, byte[]> in = new TreeMap<>(day());
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

    static Stream<Arguments> scenarios() throws IOException {
        return Stream.of(
                Arguments.of("CardDemo day", day()),
                Arguments.of("unknown cards (reason 100)", with(DALY, f -> records(f, 350, (i, r) -> {
                    if (i % 4 == 1) Fields.putText(r, 262, 16, "9999999999999999");
                    return r;
                }))),
                Arguments.of("accounts missing (reason 101)", with(ACCT, f -> records(f, 300, (i, r) -> i % 3 == 0 ? null : r))),
                Arguments.of("over limit (reason 102)", with(ACCT, f -> records(f, 300, (i, r) -> money(r, 24, 12, "50.00")))),
                Arguments.of("expired (reason 103)", with(ACCT, f -> records(f, 300, (i, r) -> {
                    if (i % 2 == 0) Fields.putText(r, 58, 10, "2000-01-01");
                    return r;
                }))),
                Arguments.of("over limit and expired: 103 wins (Q-03)", with(ACCT, f -> records(f, 300, (i, r) -> {
                    money(r, 24, 12, "1.00");
                    Fields.putText(r, 58, 10, "2000-01-01");
                    return r;
                }))),
                Arguments.of("expiry on the transaction date boundary", with(ACCT, f -> records(f, 300, (i, r) -> {
                    Fields.putText(r, 58, 10, "2022-06-15");
                    return r;
                }))),
                Arguments.of("credits and debits", with(DALY, f -> records(f, 350, (i, r) -> {
                    if (i % 2 == 0) {
                        BigDecimal amount = Fields.zoned(r, 132, 11, 2);
                        Fields.putZoned(r, 132, 11, 2, true, amount.negate());
                    }
                    return r;
                }))),
                Arguments.of("zero amounts", with(DALY, f -> records(f, 350, (i, r) -> money(r, 132, 11, "0")))),
                Arguments.of("credit limit reached exactly", limitReachedExactly()),
                Arguments.of("transaction on the expiry day", onExpiryDay()),
                Arguments.of("blank origin timestamps", with(DALY, f -> records(f, 350, (i, r) -> {
                    if (i % 5 == 0) Fields.putText(r, 278, 26, "");
                    return r;
                }))),
                Arguments.of("positive numbers stored unsigned (F-sign)", fSigned(day())),
                Arguments.of("F-sign with zero amounts", fSigned(with(DALY, f -> records(f, 350, (i, r) -> money(r, 132, 11, "0"))))),
                Arguments.of("distinct category-balance fillers (Q-04)", with(TCAT, f -> records(f, 50, (i, r) -> {
                    java.util.Arrays.fill(r, 28, 50, (byte) ('A' + i % 26));
                    return r;
                }))),
                Arguments.of("balances overflow their pictures", overflow()),
                Arguments.of("no category balances yet", with(TCAT, f -> new byte[0])),
                Arguments.of("empty day", with(DALY, f -> new byte[0])),
                Arguments.of("transaction id already posted (abend)", with(TRAN, f -> java.util.Arrays.copyOfRange(day0(), 0, 350))),
                Arguments.of("account file missing (abend)", without(ACCT)),
                Arguments.of("daily file missing (abend)", without(DALY)));
    }

    static byte[] day0() {
        try {
            return day().get(DALY);
        } catch (IOException e) {
            throw new java.io.UncheckedIOException(e);
        }
    }

    static Map<String, byte[]> overflow() throws IOException {
        Map<String, byte[]> in = new TreeMap<>(day());
        in.put(ACCT, records(in.get(ACCT), 300, (i, r) -> {
            money(r, 12, 12, "9999999990.00");
            money(r, 24, 12, "9999999999.99");
            money(r, 78, 12, "9999999995.00");
            money(r, 90, 12, "-9999999995.00");
            return r;
        }));
        in.put(DALY, records(in.get(DALY), 350, (i, r) -> money(r, 132, 11, i % 2 == 0 ? "999999999.99" : "-999999999.99")));
        in.put(TCAT, records(in.get(TCAT), 50, (i, r) -> money(r, 17, 11, "999999999.00")));
        return in;
    }

    static Map<String, byte[]> without(String dsn) throws IOException {
        Map<String, byte[]> in = new TreeMap<>(day());
        in.remove(dsn);
        return in;
    }

    /** Every account starts the cycle at zero with a 100.00 limit and every transaction is 100.00: the first fits exactly. */
    static Map<String, byte[]> limitReachedExactly() throws IOException {
        Map<String, byte[]> in = new TreeMap<>(day());
        in.put(ACCT, records(in.get(ACCT), 300, (i, r) -> {
            money(r, 24, 12, "100.00");
            money(r, 78, 12, "0");
            money(r, 90, 12, "0");
            Fields.putText(r, 58, 10, "2099-12-31");
            return r;
        }));
        in.put(DALY, records(in.get(DALY), 350, (i, r) -> money(r, 132, 11, "100.00")));
        return in;
    }

    static Map<String, byte[]> onExpiryDay() throws IOException {
        Map<String, byte[]> in = new TreeMap<>(day());
        in.put(ACCT, records(in.get(ACCT), 300, (i, r) -> {
            Fields.putText(r, 58, 10, "2022-06-15");
            money(r, 24, 12, "9999999.00");
            return r;
        }));
        in.put(DALY, records(in.get(DALY), 350, (i, r) -> {
            System.arraycopy("2022-06-15".getBytes(java.nio.charset.StandardCharsets.ISO_8859_1), 0, r, 278, 10);
            return r;
        }));
        return in;
    }

    /** Positive signed numbers stored with an unsigned zone in the last digit (as other programs often leave them). */
    static Map<String, byte[]> fSigned(Map<String, byte[]> base) {
        Map<String, byte[]> in = new TreeMap<>(base);
        in.put(ACCT, records(in.get(ACCT), 300, (i, r) -> unsignedZone(r, 12, 12, 24, 12, 36, 12, 78, 12, 90, 12)));
        in.put(TCAT, records(in.get(TCAT), 50, (i, r) -> unsignedZone(r, 17, 11)));
        in.put(DALY, records(in.get(DALY), 350, (i, r) -> unsignedZone(r, 132, 11)));
        return in;
    }

    /** For each (offset, length) pair: a positive overpunch ({, A-I) in the last byte becomes the plain digit. */
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
            assertEquals(List.of(), StepEquivalence.compare(step(), with(TCAT, f -> new byte[0])));
            assertEquals(List.of(), StepEquivalence.compare(step(), day()));
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
        var oracle = StepEquivalence.resources(getClass(), "carddemo/oracle", "", "TRANSACT.VSAM.KSDS", "DALYREJS", "ACCTDATA.VSAM.KSDS",
                "TCATBALF.VSAM.KSDS", "SYSOUT.txt");
        oracle.forEach((k, v) -> expected.put(k.equals("SYSOUT.txt") ? "sysout/STEP15.SYSOUT.txt" : "data/" + Q + k, v));
        var actual = StepEquivalence.run(step(), day(), "normalized");
        assertEquals(List.of(), StepEquivalence.againstOracle(actual, expected));
        assertEquals(4, actual.returnCode(), "rejects present: RETURN-CODE 4");
    }
}
