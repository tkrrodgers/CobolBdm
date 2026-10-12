package com.mlcpu.rt;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;

/**
 * COBOL file handler: SEQUENTIAL (fixed or variable), LINE SEQUENTIAL and INDEXED (primary + alternate keys).
 * File status codes follow the IBM/ISO table. An INDEXED file's physical form is its records, fixed length,
 * in primary-key order — the same image an IDCAMS REPRO unload produces, so outputs compare byte for byte.
 */
public final class CobFile {
    public enum Org { SEQUENTIAL, LINE_SEQUENTIAL, INDEXED, RELATIVE }
    public enum Acc { SEQUENTIAL, RANDOM, DYNAMIC }

    /** DD name → path, set by the step runner (takes precedence over the environment). */
    public static final Map<String, String> DD = new HashMap<>();

    /** Files open in the current run unit, closed implicitly when the run unit ends normally (GnuCOBOL's implicit CLOSE). */
    private static final java.util.Set<CobFile> OPEN = java.util.Collections.newSetFromMap(new java.util.IdentityHashMap<>());

    /**
     * Implicit CLOSE of every file the run unit left open: at a normal end (GOBACK / STOP RUN of the main program) and at
     * an abnormal one too. GnuCOBOL closes open files when the CEE3ABD stub ends the run with STOP RUN and when a runtime
     * error ends it ("libcob: warning: implicit CLOSE"), so the records written before the abend are in the output.
     */
    public static synchronized void closeAllOpen() {
        for (CobFile f : new ArrayList<>(OPEN)) f.close();
        OPEN.clear();
    }

    /** Forgets the open files without closing them (only for resetting a run unit whose files are gone). */
    public static synchronized void forgetOpen() {
        OPEN.clear();
    }

    /** One I/O statement of a traced run: operation, file, DD, file status, ADVANCING, and the record bytes it read or wrote. */
    public record IoEvent(String op, String file, String dd, int status, String advancing, byte[] record) {}

    /** When set, every READ, WRITE, REWRITE and DELETE is reported here (com.mlcpu.rt.IoTrace). */
    public static volatile java.util.function.Consumer<IoEvent> TRACE;

    private int lastLen = -1;

    private void trace(String op, int st, String advancing) {
        java.util.function.Consumer<IoEvent> t = TRACE;
        if (t == null) return;
        int len = op.startsWith("READ") ? (st < 10 ? (variable && lastLen >= 0 ? lastLen : maxLen) : 0) : (lastLen >= 0 ? lastLen : maxLen);
        if (op.equals("REWRITE") || op.equals("DELETE")) len = maxLen;
        byte[] r = new byte[Math.max(0, Math.min(len, maxLen))];
        System.arraycopy(area.b, area.base, r, 0, r.length);
        t.accept(new IoEvent(op, name, assign, st, advancing, r));
    }

    /** WRITE ADVANCING (GnuCOBOL): AFTER writes the control before the record, BEFORE after it; CLOSE ends a pending line. */
    public static final int ADV_BEFORE = 1, ADV_AFTER = 2, ADV_PAGE = -1;
    private boolean needsNl;

    public final String name;
    public final String assign;
    public final Org org;
    public final Acc acc;
    public final Area area;
    public final int maxLen;
    public final int minLen;
    public final CobField status;
    final CobField[] keys;
    final boolean[] dups;
    final CobField depending;
    final boolean variable;

    private String mode;
    private Path path;
    // sequential
    private InputStream in;
    private OutputStream out;
    private boolean atEof;
    // indexed
    private TreeMap<String, byte[]> data;
    private List<TreeMap<String, List<String>>> alt;
    private boolean dirty;
    private int refKey;
    private String posKey;
    private int posDup;
    private boolean posInclusive;
    private String lastReadKey;
    private long seq;

    public CobFile(String name, String assign, Org org, Acc acc, Area area, int minLen, int maxLen, CobField status,
                   CobField[] keys, boolean[] dups, CobField depending, boolean variable) {
        this.name = name;
        this.assign = assign;
        this.org = org;
        this.acc = acc;
        this.area = area;
        this.minLen = minLen;
        this.maxLen = maxLen;
        this.status = status;
        this.keys = keys == null ? new CobField[0] : keys;
        this.dups = dups == null ? new boolean[this.keys.length] : dups;
        this.depending = depending;
        this.variable = variable;
    }

    /** The whole record area as a group item (source of READ … INTO). */
    public CobField record() {
        return CobField.group(name, area, 0, maxLen, null, null);
    }

    public boolean isOpen() { return mode != null; }

    public static Path resolve(String assign) {
        String p = DD.get(assign);
        if (p == null) p = System.getenv("DD_" + assign);
        if (p == null) p = System.getenv("dd_" + assign);
        if (p == null) p = System.getenv(assign);
        if (p == null) p = System.getProperty("mlcpu.dd." + assign);
        return Path.of(p == null ? assign : p);
    }

    private int done(int st) {
        if (status != null) {
            byte[] b = String.format("%02d", st).getBytes(StandardCharsets.ISO_8859_1);
            CobRt.moveAlnumBytes(b, status);
        }
        return st;
    }

    /** Fatal I/O conditions without FILE STATUS and without a handler stop the run unit (IBM/GnuCOBOL behaviour). */
    public int check(int st, boolean handled) {
        if (st >= 30 || (!handled && status == null && st >= 10)) {
            if (status == null && !handled)
                throw new CobRt.RuntimeCheck("I/O error on " + name + " (" + assign + "): status " + String.format("%02d", st));
        }
        return st;
    }

    // ------------------------------------------------------------------ OPEN / CLOSE

    /** The mode of the last OPEN attempted, successful or not (GnuCOBOL's last_open_mode, which picks a USE ON mode procedure). */
    private String lastOpenMode;

    /** The length of the record last read (variable-length files), else the record area's length. */
    public int lastRecordLength() {
        return variable && lastLen >= 0 ? lastLen : maxLen;
    }

    public String lastOpenMode() {
        return lastOpenMode == null ? "" : lastOpenMode;
    }

    /**
     * GnuCOBOL's default error handler after an I/O statement whose exception (status 10 or more) no phrase and no USE
     * procedure took: without FILE STATUS the run stops.
     */
    public void unhandled(int st, boolean handled) {
        if (st >= 10 && !handled && status == null)
            throw new CobRt.RuntimeCheck("I/O error on " + name + " (" + assign + "): status " + String.format("%02d", st));
    }

    public int open(String openMode) {
        lastOpenMode = openMode;
        if (mode != null) return done(41);
        path = resolve(assign);
        try {
            switch (org) {
                case INDEXED -> {
                    data = new TreeMap<>();
                    alt = new ArrayList<>();
                    for (int i = 1; i < keys.length; i++) alt.add(new TreeMap<>());
                    boolean exists = Files.exists(path);
                    if (openMode.equals("OUTPUT")) {
                        dirty = true;
                    } else {
                        if (!exists) {
                            if (openMode.equals("EXTEND")) dirty = true;
                            else return done(35);
                        } else load();
                    }
                    refKey = 0;
                    posKey = null;
                    posInclusive = true;
                    posDup = 0;
                    lastReadKey = null;
                }
                default -> {
                    switch (openMode) {
                        case "INPUT" -> {
                            if (!Files.exists(path)) return done(35);
                            in = new BufferedInputStream(Files.newInputStream(path), 1 << 16);
                            atEof = false;
                        }
                        case "OUTPUT" -> {
                            if (path.getParent() != null) Files.createDirectories(path.getParent());
                            out = new BufferedOutputStream(Files.newOutputStream(path), 1 << 16);
                        }
                        case "EXTEND" -> out = new BufferedOutputStream(Files.newOutputStream(path, StandardOpenOption.CREATE, StandardOpenOption.APPEND), 1 << 16);
                        case "I-O" -> {
                            if (!Files.exists(path)) return done(35);
                            return done(37);
                        }
                        default -> { return done(37); }
                    }
                }
            }
        } catch (IOException e) {
            return done(30);
        }
        mode = openMode;
        needsNl = false;
        synchronized (CobFile.class) { OPEN.add(this); }
        return done(0);
    }

    private void load() throws IOException {
        byte[] all = Files.readAllBytes(path);
        int n = all.length / maxLen;
        for (int i = 0; i < n; i++) {
            byte[] rec = java.util.Arrays.copyOfRange(all, i * maxLen, (i + 1) * maxLen);
            String k = keyOf(0, rec);
            data.put(k, rec);
            for (int a = 1; a < keys.length; a++) alt.get(a - 1).computeIfAbsent(keyOf(a, rec), x -> new ArrayList<>()).add(k);
        }
    }

    public int close() {
        if (mode == null) return done(42);
        try {
            if (org == Org.INDEXED) {
                if (dirty || !mode.equals("INPUT")) {
                    if (path.getParent() != null) Files.createDirectories(path.getParent());
                    try (OutputStream os = new BufferedOutputStream(Files.newOutputStream(path), 1 << 16)) {
                        for (byte[] r : data.values()) os.write(r);
                    }
                }
                data = null;
                alt = null;
            } else {
                if (in != null) in.close();
                if (out != null) {
                    if (needsNl) out.write('\n');
                    out.close();
                }
                in = null;
                out = null;
            }
        } catch (IOException e) {
            mode = null;
            synchronized (CobFile.class) { OPEN.remove(this); }
            return done(30);
        }
        mode = null;
        dirty = false;
        needsNl = false;
        synchronized (CobFile.class) { OPEN.remove(this); }
        return done(0);
    }

    private void advance(int lines) throws IOException {
        if (lines == ADV_PAGE) out.write('\f');
        else for (int i = 0; i < lines; i++) out.write('\n');
    }

    // ------------------------------------------------------------------ key helpers

    private String keyOf(int k, byte[] rec) {
        CobField f = keys[k];
        return new String(rec, f.off, f.len, StandardCharsets.ISO_8859_1);
    }

    private String keyFromArea(int k) {
        return keys[k].str();
    }

    private void toArea(byte[] rec) {
        System.arraycopy(rec, 0, area.b, area.base, Math.min(rec.length, maxLen));
    }

    private byte[] fromArea(int len) {
        byte[] r = new byte[maxLen];
        System.arraycopy(area.b, area.base, r, 0, Math.min(len, maxLen));
        if (len < maxLen) java.util.Arrays.fill(r, len, maxLen, (byte) ' ');
        return r;
    }

    private int keyIndex(CobField key) {
        if (key == null) return 0;
        for (int i = 0; i < keys.length; i++) if (keys[i].off == key.off && keys[i].len == key.len) return i;
        return 0;
    }

    // ------------------------------------------------------------------ READ

    /** Sequential READ (NEXT). */
    public int readNext() {
        int st = readNext0();
        trace("READ", st, null);
        return st;
    }

    private int readNext0() {
        if (mode == null || !(mode.equals("INPUT") || mode.equals("I-O"))) return done(47);
        if (org == Org.INDEXED) return readNextIndexed();
        if (atEof) return done(46);
        try {
            if (org == Org.LINE_SEQUENTIAL) return readLine();
            int len = maxLen;
            if (variable) {
                byte[] hdr = in.readNBytes(4);
                if (hdr.length < 4) { atEof = true; return done(10); }
                len = ((hdr[0] & 0xFF) << 8) | (hdr[1] & 0xFF);
            }
            byte[] rec = in.readNBytes(len);
            if (rec.length == 0) { atEof = true; return done(10); }
            lastLen = rec.length;
            System.arraycopy(rec, 0, area.b, area.base, Math.min(rec.length, maxLen));
            if (depending != null) depending.setDec(java.math.BigDecimal.valueOf(rec.length), false);
            if (rec.length < len) {
                java.util.Arrays.fill(area.b, area.base + rec.length, area.base + len, (byte) ' ');
                return done(4);
            }
            return done(0);
        } catch (IOException e) {
            return done(30);
        }
    }

    private int readLine() throws IOException {
        java.io.ByteArrayOutputStream line = new java.io.ByteArrayOutputStream();
        int c;
        boolean any = false;
        while ((c = in.read()) >= 0) {
            any = true;
            if (c == '\n') break;
            if (c == '\r') continue;
            line.write(c);
        }
        if (!any) { atEof = true; return done(10); }
        byte[] rec = line.toByteArray();
        int n = Math.min(rec.length, maxLen);
        System.arraycopy(rec, 0, area.b, area.base, n);
        java.util.Arrays.fill(area.b, area.base + n, area.base + maxLen, (byte) ' ');
        return done(rec.length > maxLen ? 6 : 0);
    }

    private int readNextIndexed() {
        if (refKey == 0) {
            String k = posKey == null ? (data.isEmpty() ? null : data.firstKey())
                    : posInclusive ? data.ceilingKey(posKey) : data.higherKey(posKey);
            if (k == null) { posKey = null; posInclusive = false; posKey = data.isEmpty() ? null : data.lastKey(); posKey = "\uffff"; return done(10); }
            toArea(data.get(k));
            posKey = k;
            posInclusive = false;
            lastReadKey = k;
            return done(0);
        }
        TreeMap<String, List<String>> idx = alt.get(refKey - 1);
        String av = posKey == null ? (idx.isEmpty() ? null : idx.firstKey()) : posKey;
        int d = posKey == null ? 0 : posDup;
        while (av != null) {
            List<String> list = idx.get(av);
            if (list != null && d < list.size() && (posInclusive || posKey == null || d >= 0)) {
                String pk = list.get(d);
                toArea(data.get(pk));
                posKey = av;
                posDup = d + 1;
                posInclusive = true;
                lastReadKey = pk;
                boolean moreDup = d + 1 < list.size();
                return done(moreDup && dups[refKey] ? 2 : 0);
            }
            av = idx.higherKey(av);
            d = 0;
        }
        posKey = "\uffff";
        return done(10);
    }

    /** Random READ by the key of reference ({@code key} null = primary). */
    public int readKey(CobField key) {
        int st = readKey0(key);
        trace("READ KEY", st, null);
        return st;
    }

    private int readKey0(CobField key) {
        if (mode == null || !(mode.equals("INPUT") || mode.equals("I-O"))) return done(47);
        if (org != Org.INDEXED) return readNext();
        int k = keyIndex(key);
        String kv = keyFromArea(k);
        if (k == 0) {
            byte[] rec = data.get(kv);
            if (rec == null) return done(23);
            toArea(rec);
            refKey = 0;
            posKey = kv;
            posInclusive = false;
            lastReadKey = kv;
            return done(0);
        }
        List<String> list = alt.get(k - 1).get(kv);
        if (list == null || list.isEmpty()) return done(23);
        String pk = list.get(0);
        toArea(data.get(pk));
        refKey = k;
        posKey = kv;
        posDup = 1;
        posInclusive = true;
        lastReadKey = pk;
        return done(list.size() > 1 && dups[k] ? 2 : 0);
    }

    /** READ for a file in RANDOM access, or DYNAMIC without NEXT. */
    public int read(CobField key) {
        if (org == Org.INDEXED && acc != Acc.SEQUENTIAL) return readKey(key);
        return readNext();
    }

    // ------------------------------------------------------------------ START

    public int start(String op, CobField key) {
        if (mode == null || !(mode.equals("INPUT") || mode.equals("I-O"))) return done(47);
        if (org != Org.INDEXED) return done(30);
        int k = keyIndex(key);
        String kv = key == null ? keyFromArea(0) : key.str();
        int klen = kv.length();
        String full = keyFromArea(k);
        if (klen < full.length()) kv = kv + full.substring(klen);
        String found;
        if (k == 0) {
            found = switch (op) {
                case "eq" -> data.containsKey(kv) ? kv : prefixMatch(data, kv, klen);
                case "gt" -> data.higherKey(kv);
                case "ge" -> data.ceilingKey(kv);
                default -> null;
            };
        } else {
            TreeMap<String, List<String>> idx = alt.get(k - 1);
            found = switch (op) {
                case "eq" -> idx.containsKey(kv) ? kv : null;
                case "gt" -> idx.higherKey(kv);
                case "ge" -> idx.ceilingKey(kv);
                default -> null;
            };
        }
        if (found == null) return done(23);
        refKey = k;
        posKey = found;
        posDup = 0;
        posInclusive = true;
        return done(0);
    }

    private static String prefixMatch(TreeMap<String, byte[]> m, String kv, int klen) {
        String c = m.ceilingKey(kv.substring(0, klen));
        return c != null && c.startsWith(kv.substring(0, klen)) ? c : null;
    }

    // ------------------------------------------------------------------ WRITE / REWRITE / DELETE

    public int write(int recLen) {
        return write(recLen, 0, 0);
    }

    /** WRITE with ADVANCING: {@code when} 0 (none), {@link #ADV_BEFORE} or {@link #ADV_AFTER}; {@code lines} a count or {@link #ADV_PAGE}. */
    public int write(int recLen, int when, int lines) {
        int st = write0(recLen, when, lines);
        trace("WRITE", st, when == 0 ? null : (when == ADV_BEFORE ? "BEFORE " : "AFTER ") + (lines == ADV_PAGE ? "PAGE" : lines + (lines == 1 ? " LINE" : " LINES")));
        return st;
    }

    private int write0(int recLen, int when, int lines) {
        if (mode == null || mode.equals("INPUT")) return done(48);
        try {
            if (org == Org.INDEXED) {
                byte[] rec = fromArea(recLen);
                String k = keyOf(0, rec);
                if (data.containsKey(k)) return done(22);
                if (acc == Acc.SEQUENTIAL && !data.isEmpty() && k.compareTo(data.lastKey()) < 0 && mode.equals("OUTPUT")) return done(21);
                for (int a = 1; a < keys.length; a++) {
                    List<String> l = alt.get(a - 1).get(keyOf(a, rec));
                    if (l != null && !l.isEmpty() && !dups[a]) return done(22);
                }
                data.put(k, rec);
                for (int a = 1; a < keys.length; a++) alt.get(a - 1).computeIfAbsent(keyOf(a, rec), x -> new ArrayList<>()).add(k);
                dirty = true;
                return done(0);
            }
            if (mode.equals("I-O")) return done(48);
            int len = variable ? recLen : maxLen;
            lastLen = len;
            if (when == ADV_AFTER) {
                advance(lines);
                needsNl = true;
            }
            if (org == Org.LINE_SEQUENTIAL) {
                int end = len;
                while (end > 0 && area.b[area.base + end - 1] == ' ') end--;
                out.write(area.b, area.base, end);
                if (when == 0) out.write('\n');
            } else {
                if (variable) {
                    out.write((len >> 8) & 0xFF);
                    out.write(len & 0xFF);
                    out.write(0);
                    out.write(0);
                }
                out.write(area.b, area.base, len);
            }
            if (when == ADV_BEFORE) {
                advance(lines);
                needsNl = false;
            }
            return done(0);
        } catch (IOException e) {
            return done(30);
        }
    }

    public int rewrite(int recLen) {
        int st = rewrite0(recLen);
        trace("REWRITE", st, null);
        return st;
    }

    private int rewrite0(int recLen) {
        if (mode == null || !mode.equals("I-O")) return done(49);
        if (org != Org.INDEXED) return done(30);
        byte[] rec = fromArea(recLen);
        String k = keyOf(0, rec);
        if (acc == Acc.SEQUENTIAL && (lastReadKey == null || !lastReadKey.equals(k))) return done(lastReadKey == null ? 43 : 21);
        byte[] old = data.get(k);
        if (old == null) return done(23);
        for (int a = 1; a < keys.length; a++) {
            String oldAlt = keyOf(a, old), newAlt = keyOf(a, rec);
            if (oldAlt.equals(newAlt)) continue;
            List<String> l = alt.get(a - 1).get(newAlt);
            if (l != null && !l.isEmpty() && !dups[a]) return done(22);
            alt.get(a - 1).get(oldAlt).remove(k);
            alt.get(a - 1).computeIfAbsent(newAlt, x -> new ArrayList<>()).add(k);
        }
        data.put(k, rec);
        dirty = true;
        lastReadKey = null;
        return done(0);
    }

    public int delete() {
        int st = delete0();
        trace("DELETE", st, null);
        return st;
    }

    private int delete0() {
        if (mode == null || !mode.equals("I-O")) return done(49);
        if (org != Org.INDEXED) return done(30);
        String k = acc == Acc.SEQUENTIAL ? lastReadKey : keyFromArea(0);
        if (k == null) return done(43);
        byte[] old = data.remove(k);
        if (old == null) return done(23);
        for (int a = 1; a < keys.length; a++) {
            List<String> l = alt.get(a - 1).get(keyOf(a, old));
            if (l != null) l.remove(k);
        }
        dirty = true;
        lastReadKey = null;
        return done(0);
    }
}
