namespace MlCpu.Modernize;

/// <summary>
/// The small runtime the generated application layer uses (SemanticModernization.md, ports and adapters): codecs of
/// fixed-width record images, the datasets of a batch step, the legacy site of an exception (the reject mapping), and
/// the abend. Emitted into the output project in package &lt;base&gt;.batch.platform; generated, never edited.
/// </summary>
static class Platform
{
    public static IEnumerable<(string Name, string Text)> Files(string pkg)
    {
        yield return ("Records.java", Records.Replace("PKG", pkg));
        yield return ("Datasets.java", Datasets.Replace("PKG", pkg));
        yield return ("LegacySites.java", LegacySites.Replace("PKG", pkg));
        yield return ("Abend.java", Abend.Replace("PKG", pkg));
    }

    const string Records = """
package PKG;

import java.math.BigDecimal;
import java.math.BigInteger;
import java.math.RoundingMode;
import java.nio.charset.StandardCharsets;
import java.time.LocalDate;
import java.util.Arrays;

/**
 * Codecs of fixed-width record images (ISO-8859-1, one byte per character): text, unsigned and signed zoned decimals
 * (SIGN LEADING SEPARATE), binary and packed fields, and yyyyMMdd dates; the comparisons and edits COBOL applies to them.
 */
public final class Records {
    private Records() {}

    /** A record image a dataset reads into and writes from: a record class of the record package. */
    public interface Image {
        /** The record image (a copy). */
        byte[] bytes();

        /** Replaces the image (a shorter one is padded with spaces). */
        void bytes(byte[] image);
    }

    /** A record image of spaces. */
    public static byte[] blank(int length) {
        byte[] r = new byte[length];
        Arrays.fill(r, (byte) ' ');
        return r;
    }

    /** The field's characters, trailing spaces included. */
    public static String text(byte[] r, int off, int len) {
        return new String(r, off, len, StandardCharsets.ISO_8859_1);
    }

    /** The field's characters without trailing spaces (what COBOL compares, padding the shorter operand). */
    public static String trimmed(byte[] r, int off, int len) {
        int end = off + len;
        while (end > off && r[end - 1] == ' ') end--;
        return new String(r, off, end - off, StandardCharsets.ISO_8859_1);
    }

    /** Text left-justified, space padded, truncated on the right. */
    public static void putText(byte[] r, int off, int len, String value) {
        byte[] v = (value == null ? "" : value).getBytes(StandardCharsets.ISO_8859_1);
        int n = Math.min(len, v.length);
        System.arraycopy(v, 0, r, off, n);
        Arrays.fill(r, off + n, off + len, (byte) ' ');
    }

    /** An unsigned zoned field; a non-digit byte counts its low nibble, as the mainframe does. */
    public static long unsigned(byte[] r, int off, int len) {
        long v = 0;
        for (int i = 0; i < len; i++) {
            int c = r[off + i] & 0xFF;
            int d = c >= '0' && c <= '9' ? c - '0' : (c & 0x0F) > 9 ? 0 : c & 0x0F;
            v = v * 10 + d;
        }
        return v;
    }

    /** Stores the low-order digits of the value's magnitude (a MOVE truncates high-order digits). */
    public static void putUnsigned(byte[] r, int off, int len, long value) {
        long v = Math.abs(value);
        for (int i = len - 1; i >= 0; i--) {
            r[off + i] = (byte) ('0' + (int) (v % 10));
            v /= 10;
        }
    }

    /** A signed zoned decimal with a leading separate sign ('+' or '-'): len counts the sign. */
    public static BigDecimal signLeading(byte[] r, int off, int len, int scale) {
        boolean negative = r[off] == '-';
        BigDecimal v = BigDecimal.valueOf(unsigned(r, off + 1, len - 1), scale);
        return negative ? v.negate() : v;
    }

    /** Stores a decimal as a leading separate sign and its low-order digits (a MOVE truncates high-order digits). */
    public static void putSignLeading(byte[] r, int off, int len, int scale, BigDecimal value) {
        BigInteger unscaled = value.setScale(scale, RoundingMode.DOWN).unscaledValue();
        r[off] = (byte) (unscaled.signum() < 0 ? '-' : '+');
        String digits = unscaled.abs().toString();
        int n = len - 1;
        if (digits.length() > n) digits = digits.substring(digits.length() - n);
        digits = "0".repeat(n - digits.length()) + digits;
        System.arraycopy(digits.getBytes(StandardCharsets.US_ASCII), 0, r, off + 1, n);
    }

    /** A big-endian binary (COMP) field. */
    public static long binary(byte[] r, int off, int len) {
        long v = r[off];
        for (int i = 1; i < len; i++) v = (v << 8) | (r[off + i] & 0xFF);
        return v;
    }

    /** Stores a big-endian binary (COMP) field. */
    public static void putBinary(byte[] r, int off, int len, long value) {
        for (int i = len - 1; i >= 0; i--) {
            r[off + i] = (byte) value;
            value >>= 8;
        }
    }

    /** A yyyyMMdd date in an unsigned zoned field. */
    public static LocalDate date(byte[] r, int off, int len) {
        long v = unsigned(r, off, len);
        return LocalDate.of((int) (v / 10000), (int) (v / 100 % 100), (int) (v % 100));
    }

    /** Stores a date as yyyyMMdd in an unsigned zoned field. */
    public static void putDate(byte[] r, int off, int len, LocalDate value) {
        putUnsigned(r, off, len, value.getYear() * 10000L + value.getMonthValue() * 100L + value.getDayOfMonth());
    }

    /** NUMERIC: every character a digit (an empty field is not numeric). */
    public static boolean isNumeric(String s) {
        if (s.isEmpty()) return false;
        for (int i = 0; i < s.length(); i++) if (s.charAt(i) < '0' || s.charAt(i) > '9') return false;
        return true;
    }

    /** A valid Y/N indicator: exactly "Y" or "N" (a record field's validation, x NOT = 'Y' AND x NOT = 'N'). */
    public static boolean isYesNo(String s) {
        return s.equals("Y") || s.equals("N");
    }

    /** Text that fits a field of n characters: truncated on the right, without trailing spaces. */
    public static String fit(String s, int n) {
        if (s.length() > n) s = s.substring(0, n);
        return s.stripTrailing();
    }

    /** Text padded with spaces to n characters (the field's image). */
    public static String pad(String s, int n) {
        return s.length() >= n ? s.substring(0, n) : s + " ".repeat(n - s.length());
    }

    /** Alphanumeric comparison: the shorter operand padded with spaces. */
    public static int compare(String a, String b) {
        int n = Math.max(a.length(), b.length());
        return pad(a, n).compareTo(pad(b, n));
    }

    /** The low-order digits of a number that fit n digits (a numeric MOVE). */
    public static long truncate(long v, int digits) {
        long m = 1;
        for (int i = 0; i < digits && i < 18; i++) m *= 10;
        return digits >= 19 ? v : v % m;
    }

    /** Whether a value fits n digits (ON SIZE ERROR). */
    public static boolean fits(long v, int digits) {
        return digits >= 19 || Math.abs(v) < BigInteger.TEN.pow(digits).longValueExact();
    }

    /** A numeric-edited picture (9, Z, comma, period, sign) applied to an integer. */
    public static String edit(long value, String picture) {
        String digits = Long.toString(Math.abs(value));
        StringBuilder out = new StringBuilder();
        int positions = 0;
        for (char c : picture.toCharArray()) if (c == '9' || c == 'Z') positions++;
        if (digits.length() > positions) digits = digits.substring(digits.length() - positions);
        digits = "0".repeat(positions - digits.length()) + digits;
        int k = 0;
        boolean significant = false;
        for (char c : picture.toCharArray()) {
            switch (c) {
                case '9' -> { out.append(digits.charAt(k++)); significant = true; }
                case 'Z' -> {
                    char d = digits.charAt(k++);
                    if (d != '0') significant = true;
                    out.append(significant ? d : ' ');
                }
                case ',' -> out.append(significant ? ',' : ' ');
                case '-' -> out.append(value < 0 ? '-' : ' ');
                case '+' -> out.append(value < 0 ? '-' : '+');
                default -> out.append(c);
            }
        }
        return out.toString();
    }
}
""";

    const string Datasets = """
package PKG;

import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;

/**
 * The datasets of a batch step, by DD name: the path is the one bound for the run (the strangler), else the system
 * property dd.NAME, else the environment variable DD_NAME (as GnuCOBOL resolves ASSIGN names), else NAME in the working
 * directory. Records are fixed length; an input with line ends is read a line at a time (space padded), as a
 * line-sequential dataset. When traced, every record written carries the request it belongs to: the number of records
 * read so far from the step's most-read input (the shadow comparison groups differences by request).
 */
public final class Datasets {
    private Datasets() {}

    private static final Map<String, Path> BOUND = new HashMap<>();
    private static final Map<String, Integer> READS = new HashMap<>();
    private static final Map<String, List<Integer>> WRITES = new LinkedHashMap<>();
    private static boolean tracing;

    /** Binds DD names to paths for this run (before the step opens its files); tracing on or off. */
    public static void bind(Map<String, Path> datasets, boolean trace) {
        BOUND.clear();
        BOUND.putAll(datasets);
        READS.clear();
        WRITES.clear();
        tracing = trace;
    }

    /** The request of every record written, by output DD name (empty unless traced). */
    public static Map<String, List<Integer>> trace() {
        return WRITES;
    }

    /** The path of a DD name. */
    public static Path path(String dd) {
        Path bound = BOUND.get(dd);
        if (bound != null) return bound;
        String p = System.getProperty("dd." + dd);
        if (p == null) p = System.getenv("DD_" + dd);
        return Path.of(p == null ? dd : p);
    }

    /** The number of requests read so far: records read from the step's most-read input (traced runs). */
    public static int requests() {
        return READS.values().stream().max(Integer::compare).orElse(0);
    }

    /** The records of a dataset: lines (space padded to lrecl) when it has line ends, else fixed lrecl-byte records. */
    public static List<byte[]> records(Path path, int lrecl) throws IOException {
        byte[] all = Files.readAllBytes(path);
        List<byte[]> out = new ArrayList<>();
        boolean lines = false;
        for (byte b : all) if (b == '\n') { lines = true; break; }
        if (lines) {
            int start = 0;
            for (int i = 0; i <= all.length; i++) {
                if (i == all.length || all[i] == '\n') {
                    int end = i > start && all[i - 1] == '\r' ? i - 1 : i;
                    if (i < all.length || end > start) {
                        byte[] r = Records.blank(lrecl);
                        System.arraycopy(all, start, r, 0, Math.min(lrecl, end - start));
                        out.add(r);
                    }
                    start = i + 1;
                }
            }
        } else {
            for (int off = 0; off + lrecl <= all.length; off += lrecl) {
                byte[] r = new byte[lrecl];
                System.arraycopy(all, off, r, 0, lrecl);
                out.add(r);
            }
        }
        return out;
    }

    /** A sequential dataset (RECFM F). */
    public static final class Sequential {
        private final String dd;
        private final int lrecl;
        private List<byte[]> input;
        private int next;
        private java.io.OutputStream output;
        private String status = "00";

        /** The dataset of a DD name, of records of lrecl bytes. */
        public Sequential(String dd, int lrecl) {
            this.dd = dd;
            this.lrecl = lrecl;
        }

        /** The file status of the last operation (00 ok, 10 end of file, 35 not found, 47/48 not open). */
        public String status() {
            return status;
        }

        /** OPEN INPUT. */
        public void openInput() {
            try {
                input = records(path(dd), lrecl);
                next = 0;
                status = "00";
            } catch (IOException e) {
                status = "35";
            }
        }

        /** OPEN OUTPUT. */
        public void openOutput() {
            try {
                output = Files.newOutputStream(path(dd));
                if (tracing) WRITES.put(dd, new ArrayList<>());
                status = "00";
            } catch (IOException e) {
                status = "30";
            }
        }

        /** READ: the next record into the record (when given); false at the end (status 10). */
        public boolean read(Records.Image into) {
            if (input == null) { status = "47"; return false; }
            if (next >= input.size()) { status = "10"; return false; }
            status = "00";
            if (into != null) into.bytes(input.get(next).clone());
            next++;
            if (tracing) READS.merge(dd, 1, Integer::sum);
            return true;
        }

        /** WRITE: the record's characters (ISO-8859-1), space padded to the record length. */
        public void write(String record) {
            write(record.getBytes(StandardCharsets.ISO_8859_1));
        }

        /** WRITE: a record image, space padded to the record length. */
        public void write(byte[] record) {
            try {
                byte[] r = Records.blank(lrecl);
                System.arraycopy(record, 0, r, 0, Math.min(lrecl, record.length));
                output.write(r);
                status = "00";
                if (tracing && WRITES.get(dd) != null) WRITES.get(dd).add(READS.values().stream().max(Integer::compare).orElse(0));
            } catch (IOException | NullPointerException e) {
                status = "48";
            }
        }

        /** CLOSE. */
        public void close() {
            try {
                if (output != null) output.close();
            } catch (IOException e) {
                throw new UncheckedIOException(e);
            }
            output = null;
            input = null;
            status = "00";
        }
    }

    /** An indexed dataset (KSDS) loaded from its records; read by key. */
    public static final class Indexed {
        private final String dd;
        private final int lrecl;
        private final int keyOffset;
        private final int keyLength;
        private TreeMap<String, byte[]> records;
        private String status = "00";

        /** The dataset of a DD name, of records of lrecl bytes keyed by keyLength bytes at keyOffset. */
        public Indexed(String dd, int lrecl, int keyOffset, int keyLength) {
            this.dd = dd;
            this.lrecl = lrecl;
            this.keyOffset = keyOffset;
            this.keyLength = keyLength;
        }

        /** The file status of the last operation (00 ok, 23 not found, 35 not found at open, 47 not open). */
        public String status() {
            return status;
        }

        /** OPEN INPUT: loads the records by key. */
        public void openInput() {
            try {
                records = new TreeMap<>();
                for (byte[] r : Datasets.records(path(dd), lrecl))
                    records.putIfAbsent(new String(r, keyOffset, keyLength, StandardCharsets.ISO_8859_1), r);
                status = "00";
            } catch (IOException e) {
                status = "35";
            }
        }

        /** READ by key into the record (when given); false when no record has the key (status 23). */
        public boolean read(String key, Records.Image into) {
            if (records == null) { status = "47"; return false; }
            byte[] r = records.get(Records.pad(key, keyLength));
            status = r == null ? "23" : "00";
            if (r != null && into != null) into.bytes(r.clone());
            return r != null;
        }

        /** CLOSE. */
        public void close() {
            records = null;
            status = "00";
        }
    }
}
""";

    const string LegacySites = """
package PKG;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.Map;

/**
 * The legacy site of an exception: the first frame of the modernized code whose line the source map ties to a legacy
 * statement (RJ-SOURCE of a reject, the COBOL's JX-SITE). The map is generated with the code (legacy-sites.tsv).
 */
public final class LegacySites {
    private static final Map<String, String> SITES = load();
    private static boolean warned;

    private LegacySites() {}

    private static Map<String, String> load() {
        Map<String, String> m = new HashMap<>();
        try (InputStream in = LegacySites.class.getResourceAsStream("/legacy-sites.tsv")) {
            if (in == null) return m;
            BufferedReader r = new BufferedReader(new InputStreamReader(in, StandardCharsets.UTF_8));
            for (String line; (line = r.readLine()) != null; ) {
                String[] f = line.split("\t");
                if (f.length == 3) m.put(f[0] + ":" + f[1], f[2]);
            }
        } catch (IOException e) {
            throw new IllegalStateException(e);
        }
        return m;
    }

    /** The site of the throwable (or of its cause chain); empty when no frame maps. */
    public static String site(Throwable e) {
        // -Dlegacy.trace=true: the exception behind every reject (diagnosis of a mapping)
        if (Boolean.getBoolean("legacy.trace")) e.printStackTrace();
        // HotSpot drops the stack trace of an exception thrown often at one place unless -XX:-OmitStackTraceInFastThrow
        if (e.getStackTrace().length == 0 && !warned) {
            warned = true;
            System.err.println("LegacySites: an exception without a stack trace; run with -XX:-OmitStackTraceInFastThrow");
        }
        for (Throwable t = e; t != null; t = t.getCause()) {
            for (StackTraceElement f : t.getStackTrace()) {
                if (f.getFileName() == null) continue;
                String cls = f.getClassName();
                int dot = cls.lastIndexOf('.');
                String file = (dot < 0 ? "" : cls.substring(0, dot).replace('.', '/') + "/") + f.getFileName();
                String s = SITES.get(file + ":" + f.getLineNumber());
                if (s != null) return s;
            }
        }
        return "";
    }
}
""";

    const string Abend = """
package PKG;

/** An abnormal end of the step (CEE3ABD, a job adapter failure): not a business exception, never a reject. */
public class Abend extends Error {
    private final int code;

    /** An abend with its code (the step ends with return code 16). */
    public Abend(int code, String message) {
        super(message);
        this.code = code;
    }

    /** The abend code (CEE3ABD's first argument). */
    public int code() {
        return code;
    }
}
""";
}
