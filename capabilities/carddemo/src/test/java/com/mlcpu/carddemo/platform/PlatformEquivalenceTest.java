package com.mlcpu.carddemo.platform;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.mlcpu.carddemo.platform.numeric.Decimals;
import com.mlcpu.carddemo.platform.records.DatasetException;
import com.mlcpu.carddemo.platform.records.Fields;
import com.mlcpu.carddemo.platform.records.IndexedDataset;
import com.mlcpu.carddemo.platform.records.SequentialDataset;
import com.mlcpu.carddemo.platform.text.Formats;
import com.mlcpu.carddemo.platform.time.BatchTimestamps;
import com.mlcpu.rt.Area;
import com.mlcpu.rt.CobConfig;
import com.mlcpu.rt.CobField;
import com.mlcpu.rt.Edit;
import java.io.IOException;
import java.math.BigDecimal;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.OffsetDateTime;
import java.util.ArrayList;
import java.util.List;
import java.util.Random;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/** The normalized platform (codecs, decimal storage, edits, datasets) checked against the faithful runtime it replaces. */
class PlatformEquivalenceTest {

    @BeforeAll
    static void ebcdicSigns() {
        CobConfig.ASCII_SIGN = false;
    }

    /** Values around every boundary: zero, cents, digit-count edges, overflow of the picture, both signs. */
    static List<BigDecimal> values() {
        List<BigDecimal> v = new ArrayList<>();
        for (String s : new String[]{"0", "0.01", "0.10", "0.99", "1", "9.99", "10", "999.99", "1000", "999999.99", "1000000",
                "999999999.99", "1000000000", "1234567890.12", "99999999999.99", "123.456", "0.009", "100000000000.00"}) {
            v.add(new BigDecimal(s));
            v.add(new BigDecimal(s).negate());
        }
        Random random = new Random(20220705);
        for (int i = 0; i < 400; i++) v.add(BigDecimal.valueOf(random.nextLong() % 10_000_000_000_000L, random.nextInt(4)));
        return v;
    }

    static CobField zonedField(int digits, int scale, boolean signed) {
        return CobField.num("F", CobField.Use.DISPLAY, new Area(digits), 0, digits, digits, scale, signed, false, false, null, null);
    }

    @Test
    void zonedEncodingMatchesTheRuntime() {
        int[][] shapes = {{12, 2, 1}, {11, 2, 1}, {6, 2, 1}, {11, 0, 0}, {9, 0, 0}, {4, 0, 0}};
        for (int[] s : shapes)
            for (BigDecimal v : values()) {
                boolean signed = s[2] == 1;
                CobField f = zonedField(s[0], s[1], signed);
                f.setDec(v, false);
                byte[] mine = new byte[s[0]];
                Fields.putZoned(mine, 0, s[0], s[1], signed, Decimals.fit(v, s[0] - s[1], s[1], signed));
                assertArrayEquals(f.bytes(), mine, () -> "encode " + v + " into " + s[0] + "," + s[1] + "," + signed);
                BigDecimal decoded = Fields.zoned(mine, 0, s[0], s[1]);
                assertEquals(0, f.dec().compareTo(signed ? decoded : decoded.abs()), () -> "decode " + v);
                assertEquals(0, f.dec().compareTo(Decimals.fit(v, s[0] - s[1], s[1], signed)), () -> "fit " + v + " into " + s[0] + "," + s[1]);
            }
    }

    @Test
    void zonedDecodingAcceptsEveryStoredForm() {
        byte[] r = "0000000001{0000000001J000000000p0000000000}".getBytes(StandardCharsets.ISO_8859_1);
        assertEquals(new BigDecimal("0.10"), Fields.zoned(r, 0, 11, 2));
        assertEquals(new BigDecimal("-0.11"), Fields.zoned(r, 11, 11, 2));
        assertEquals(new BigDecimal("0.00"), Fields.zoned(r, 22, 10, 2).abs());
        assertEquals(new BigDecimal("0.00"), Fields.zoned(r, 32, 11, 2).abs());
        assertEquals(42, Fields.unsigned("00042".getBytes(StandardCharsets.ISO_8859_1), 0, 5));
        assertEquals(new BigDecimal("-0.01"), Fields.zoned("000000000q".getBytes(StandardCharsets.ISO_8859_1), 0, 10, 2), "ASCII negative sign");
        assertEquals(new BigDecimal("-0.19"), Fields.zoned("000000001y".getBytes(StandardCharsets.ISO_8859_1), 0, 10, 2), "ASCII negative sign");
        assertEquals(new BigDecimal("0.09"), Fields.zoned("000000000I".getBytes(StandardCharsets.ISO_8859_1), 0, 10, 2));
        assertEquals(new BigDecimal("0.01"), Fields.zoned("000000000A".getBytes(StandardCharsets.ISO_8859_1), 0, 10, 2));
        assertEquals(new BigDecimal("-0.09"), Fields.zoned("000000000R".getBytes(StandardCharsets.ISO_8859_1), 0, 10, 2));
        assertEquals(new BigDecimal("0.00"), Fields.zoned("         0".getBytes(StandardCharsets.ISO_8859_1), 0, 10, 2), "spaces read as zero digits");
    }

    @Test
    void signedAmountMatchesNumericEditing() {
        for (BigDecimal v : values()) {
            assertEquals(Edit.format(v, "-ZZZ,ZZZ,ZZZ.ZZ", 2, false), Formats.signedAmount(v, '-'), () -> "-ZZZ,ZZZ,ZZZ.ZZ " + v);
            assertEquals(Edit.format(v, "+ZZZ,ZZZ,ZZZ.ZZ", 2, false), Formats.signedAmount(v, '+'), () -> "+ZZZ,ZZZ,ZZZ.ZZ " + v);
        }
    }

    @Test
    void signedNumberShowsDigitsAndSign() {
        assertEquals("00000001234+", Formats.signedNumber(new BigDecimal("12.34"), 9, 2, "gnucobol"));
        assertEquals("00000001234-", Formats.signedNumber(new BigDecimal("-12.34"), 9, 2, "gnucobol"));
        for (BigDecimal v : values()) {
            CobField f = zonedField(11, 2, true);
            f.setDec(v, false);
            assertEquals(new String(f.bytes(), StandardCharsets.ISO_8859_1), Formats.signedNumber(v, 9, 2, "zos"), () -> "zos " + v);
        }
    }

    @Test
    void countsAndColumns() {
        assertEquals("000000300", Formats.count(300, 9));
        assertEquals("000000001", Formats.count(1_000_000_001, 9));
        assertEquals("ab   ", Formats.column("ab", 5));
        assertEquals("abcde", Formats.column("abcdefg", 5));
        assertEquals("     ", Formats.column(null, 5));
        assertEquals("00042", Fields.digits(-42, 5));
        assertEquals("345", Fields.digits(12345, 3));
    }

    @Test
    void textFieldsPadAndTrim() {
        byte[] r = new byte[8];
        Fields.putText(r, 0, 8, "ABC");
        assertEquals("ABC     ", Fields.image(r));
        assertEquals("ABC", Fields.text(r, 0, 8));
        Fields.putText(r, 0, 8, "ABCDEFGHIJ");
        assertEquals("ABCDEFGH", Fields.text(r, 0, 8));
        Fields.putText(r, 0, 8, null);
        assertEquals("", Fields.text(r, 0, 8));
        byte[] zeros = {'A', 0, 0, ' '};
        assertEquals("A\0\0", Fields.text(zeros, 0, 4), "binary zeros are not padding");
    }

    @Test
    void db2TimestampsAreHundredthsPaddedWithZeros() {
        assertEquals("2022-07-05-10.11.12.340000", BatchTimestamps.db2(OffsetDateTime.parse("2022-07-05T10:11:12.345678-07:00")));
        assertEquals("2022-01-02-03.04.05.000000", BatchTimestamps.db2(OffsetDateTime.parse("2022-01-02T03:04:05Z")));
    }

    @Test
    void indexedDatasetsKeepKeyOrderAndReportStatuses(@TempDir Path dir) throws IOException {
        Path p = dir.resolve("ksds");
        assertEquals("35", assertThrows(DatasetException.class, () -> IndexedDataset.open(p, 4, 0, 2)).status());
        Files.write(p, "BB01AA02".getBytes(StandardCharsets.ISO_8859_1));
        long written = Files.getLastModifiedTime(p).toMillis();
        try (IndexedDataset d = IndexedDataset.open(p, 4, 0, 2)) {
            assertEquals("AA02", new String(d.find("AA").orElseThrow(), StandardCharsets.ISO_8859_1));
            assertTrue(d.find("CC").isEmpty());
            assertEquals("BB01", new String(d.findBy(2, "01").orElseThrow(), StandardCharsets.ISO_8859_1));
            assertTrue(d.findBy(2, "09").isEmpty());
            assertEquals(4, d.lrecl());
        }
        assertEquals(written, Files.getLastModifiedTime(p).toMillis(), "unchanged datasets are not rewritten");
        try (IndexedDataset d = IndexedDataset.open(p, 4, 0, 2)) {
            assertEquals("22", assertThrows(DatasetException.class, () -> d.insert("AA09".getBytes(StandardCharsets.ISO_8859_1))).status());
            assertEquals("23", assertThrows(DatasetException.class, () -> d.replace("CC09".getBytes(StandardCharsets.ISO_8859_1))).status());
            d.insert("AB03".getBytes(StandardCharsets.ISO_8859_1));
            d.replace("BB04".getBytes(StandardCharsets.ISO_8859_1));
            assertEquals(3, d.inKeyOrder().size());
        }
        assertEquals("AA02AB03BB04", Files.readString(p, StandardCharsets.ISO_8859_1));
        Files.write(p, "AA0".getBytes(StandardCharsets.ISO_8859_1));
        assertEquals("39", assertThrows(DatasetException.class, () -> IndexedDataset.open(p, 4, 0, 2)).status());
        Path created = dir.resolve("new");
        IndexedDataset.create(created, 4, 0, 2).close();
        assertTrue(Files.exists(created));
        assertEquals(0, Files.size(created));
    }

    @Test
    void sequentialDatasetsReadAndWriteFixedRecords(@TempDir Path dir) throws IOException {
        Path p = dir.resolve("ps");
        assertEquals("35", assertThrows(DatasetException.class, () -> SequentialDataset.reader(p, 3)).status());
        try (SequentialDataset.Writer w = SequentialDataset.writer(p, 3)) {
            w.write("abc".getBytes(StandardCharsets.ISO_8859_1));
            assertEquals("44", assertThrows(DatasetException.class, () -> w.write("ab".getBytes(StandardCharsets.ISO_8859_1))).status());
        }
        Files.write(p, "abcde".getBytes(StandardCharsets.ISO_8859_1));
        try (SequentialDataset.Reader r = SequentialDataset.reader(p, 3)) {
            assertEquals("abc", new String(r.next().orElseThrow(), StandardCharsets.ISO_8859_1));
            assertEquals("04", assertThrows(DatasetException.class, r::next).status());
        }
        Files.write(p, new byte[0]);
        try (SequentialDataset.Reader r = SequentialDataset.reader(p, 3)) {
            assertFalse(r.next().isPresent());
        }
    }
}
