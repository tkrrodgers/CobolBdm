package com.mlcpu.rt;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;
import java.util.List;

/**
 * DFSORT subset (§6.6.6): {@code SORT FIELDS=(p,l,CH,A|D,...)} and
 * {@code INCLUDE COND=(p,l,CH,op,C'lit'[,AND|OR,...])} over fixed records. Anything else is refused (exit 3).
 * The sort is stable (EQUALS).
 * <pre>SortReplay --card "SORT FIELDS=(1,16,CH,A)" --sortin a.dat[,b.dat] --lrecl 350 --out sorted.dat</pre>
 */
public final class SortReplay {
    private SortReplay() {}

    record Key(int pos, int len, boolean asc, boolean zd) {}

    record Cond(int pos, int len, String op, byte[] lit) {}

    public static void main(String[] args) throws IOException {
        List<String> cards = new ArrayList<>();
        String sortin = null, out = null;
        int lrecl = 0;
        for (int i = 0; i < args.length; i++) {
            switch (args[i]) {
                case "--card" -> cards.add(args[++i]);
                case "--sortin" -> sortin = args[++i];
                case "--lrecl" -> lrecl = Integer.parseInt(args[++i]);
                case "--out" -> out = args[++i];
                default -> throw new IllegalArgumentException(args[i]);
            }
        }
        List<Path> inputs = new ArrayList<>();
        for (String f : sortin.split(",")) inputs.add(Path.of(f));
        try {
            int[] n = run(cards, inputs, lrecl, Path.of(out));
            System.out.println("SORTOUT " + out + ": " + n[1] + " of " + n[0] + " records");
        } catch (Refused r) {
            System.err.println(r.getMessage());
            System.exit(3);
        }
    }

    public static final class Refused extends RuntimeException {
        public Refused(String m) { super("SortReplay refuses unsupported " + m); }
    }

    /** Join SYSIN lines into control statements (a line not starting with a statement keyword continues the previous one). */
    public static List<String> statements(String sysin) {
        List<String> out = new ArrayList<>();
        for (String raw : sysin.split("\n")) {
            String line = raw.length() > 71 ? raw.substring(0, 71) : raw;
            if (line.isBlank() || line.startsWith("*")) continue;
            String t = line.trim();
            boolean starts = t.matches("^(SORT|MERGE|INCLUDE|OMIT|OPTION|INREC|OUTREC|SUM|END|RECORD)\\b.*");
            if (starts || out.isEmpty()) out.add(t);
            else out.set(out.size() - 1, out.get(out.size() - 1) + t);
        }
        return out;
    }

    /** Replace DFSORT SYMNAMES symbols (NAME,p,l,f or NAME,C'lit') in control statements. */
    public static List<String> resolveSymbols(List<String> cards, String symnames) {
        java.util.Map<String, String> sym = new java.util.LinkedHashMap<>();
        for (String raw : symnames.split("\n")) {
            String line = raw.trim();
            if (line.isEmpty() || line.startsWith("*")) continue;
            int end = 0;
            boolean q = false;
            while (end < line.length() && (q || line.charAt(end) != ' ')) { if (line.charAt(end) == '\'') q = !q; end++; }
            String def = line.substring(0, end);
            int comma = def.indexOf(',');
            if (comma > 0) sym.put(def.substring(0, comma), def.substring(comma + 1));
        }
        List<String> out = new ArrayList<>();
        for (String c : cards) {
            String r = c;
            for (var e : sym.entrySet()) r = r.replaceAll("(?<![A-Z0-9#@$-])" + java.util.regex.Pattern.quote(e.getKey()) + "(?![A-Z0-9#@$-])", java.util.regex.Matcher.quoteReplacement(e.getValue()));
            out.add(r);
        }
        return out;
    }

    /** Run the cards over fixed records; returns {records in, records out}. */
    public static int[] run(List<String> cards, List<Path> inputs, int lrecl, Path out) throws IOException {
        List<Key> keys = new ArrayList<>();
        List<Cond> conds = new ArrayList<>();
        List<String> joins = new ArrayList<>();
        boolean copy = false;
        for (String card : cards) {
            String c = card.trim().replaceAll("\\s+", " ");
            if (c.startsWith("SORT FIELDS=COPY") || c.startsWith("OPTION COPY")) { copy = true; continue; }
            if (c.startsWith("SORT FIELDS=(")) {
                String[] p = inner(c).split(",");
                for (int i = 0; i + 3 < p.length; i += 4) {
                    String fmt = p[i + 2].trim();
                    if (!fmt.equals("CH") && !fmt.equals("ZD")) refuse("format " + fmt);
                    keys.add(new Key(Integer.parseInt(p[i].trim()), Integer.parseInt(p[i + 1].trim()), p[i + 3].trim().equals("A"), fmt.equals("ZD")));
                }
                continue;
            }
            if (c.startsWith("INCLUDE COND=(")) {
                List<String> p = splitTopLevel(inner(c));
                for (int i = 0; i < p.size(); ) {
                    if (!p.get(i + 2).equals("CH")) refuse("format " + p.get(i + 2));
                    String lit = p.get(i + 4);
                    if (!lit.startsWith("C'")) refuse("constant " + lit);
                    conds.add(new Cond(Integer.parseInt(p.get(i)), Integer.parseInt(p.get(i + 1)), p.get(i + 3),
                            lit.substring(2, lit.length() - 1).getBytes(StandardCharsets.ISO_8859_1)));
                    i += 5;
                    if (i < p.size()) joins.add(p.get(i++));
                }
                continue;
            }
            refuse("statement " + c);
        }
        List<byte[]> recs = new ArrayList<>();
        for (Path f : inputs) {
            byte[] all = Files.readAllBytes(f);
            for (int i = 0; i + lrecl <= all.length; i += lrecl) recs.add(Arrays.copyOfRange(all, i, i + lrecl));
        }
        List<byte[]> kept = new ArrayList<>();
        for (byte[] r : recs) if (include(r, conds, joins)) kept.add(r);
        if (!copy && !keys.isEmpty()) {
            Comparator<byte[]> cmp = (a, b) -> {
                for (Key k : keys) {
                    int o = k.pos - 1;
                    int c = k.zd ? zoned(a, o, k.len).compareTo(zoned(b, o, k.len)) : Arrays.compareUnsigned(a, o, o + k.len, b, o, o + k.len);
                    if (c != 0) return k.asc ? c : -c;
                }
                return 0;
            };
            kept.sort(cmp);
        }
        Path o = out;
        if (o.getParent() != null) Files.createDirectories(o.getParent());
        DataPrep.write(o, kept);
        return new int[]{recs.size(), kept.size()};
    }

    /** Zoned decimal key value (sign in the last byte's zone/overpunch). */
    static java.math.BigInteger zoned(byte[] r, int off, int len) {
        java.math.BigInteger v = java.math.BigInteger.ZERO;
        boolean neg = false;
        for (int i = 0; i < len; i++) {
            int[] d = Overpunch.decode(r[off + i] & 0xFF);
            if (i == len - 1 && d[1] < 0) neg = true;
            v = v.multiply(java.math.BigInteger.TEN).add(java.math.BigInteger.valueOf(d[0]));
        }
        return neg ? v.negate() : v;
    }

    static boolean include(byte[] r, List<Cond> conds, List<String> joins) {
        if (conds.isEmpty()) return true;
        boolean acc = test(r, conds.get(0));
        for (int i = 1; i < conds.size(); i++) {
            boolean t = test(r, conds.get(i));
            acc = joins.get(i - 1).equals("AND") ? acc && t : acc || t;
        }
        return acc;
    }

    static boolean test(byte[] r, Cond c) {
        byte[] lit = Arrays.copyOf(c.lit, c.len);
        for (int i = c.lit.length; i < c.len; i++) lit[i] = ' ';
        int cmp = Arrays.compareUnsigned(r, c.pos - 1, c.pos - 1 + c.len, lit, 0, c.len);
        return switch (c.op) {
            case "EQ" -> cmp == 0;
            case "NE" -> cmp != 0;
            case "GT" -> cmp > 0;
            case "GE" -> cmp >= 0;
            case "LT" -> cmp < 0;
            case "LE" -> cmp <= 0;
            default -> { refuse("operator " + c.op); yield false; }
        };
    }

    static String inner(String c) {
        return c.substring(c.indexOf('(') + 1, c.lastIndexOf(')'));
    }

    static List<String> splitTopLevel(String s) {
        List<String> r = new ArrayList<>();
        StringBuilder cur = new StringBuilder();
        boolean q = false;
        for (char ch : s.toCharArray()) {
            if (ch == '\'') q = !q;
            if (ch == ',' && !q) { r.add(cur.toString().trim()); cur.setLength(0); continue; }
            cur.append(ch);
        }
        if (cur.length() > 0) r.add(cur.toString().trim());
        return r;
    }

    static void refuse(String what) {
        throw new Refused(what);
    }
}
