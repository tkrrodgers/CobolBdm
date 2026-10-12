package com.mlcpu.rt;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;
import java.util.List;

/**
 * The SORT and MERGE verbs as GnuCOBOL 3.3 runs them. Records are collected in order (USING files one after another,
 * or RELEASE in the INPUT PROCEDURE), then sorted stably on the KEYs (each compared as the IF statement compares the
 * item: numerically for numeric keys, character by character otherwise; DESCENDING reversed), and delivered to the
 * GIVING files or by RETURN in the OUTPUT PROCEDURE. MERGE is the same stable sort of its USING files' records taken in
 * file order (GnuCOBOL does not require them to be in sequence, and neither does this).
 */
public final class CobSort {
    private final String name;
    private final Area area;
    private final int len;
    private final CobField[] keys;
    private final boolean[] ascending;
    private final List<byte[]> records = new ArrayList<>();
    private int next;
    private final Area left = new Area(0), right = new Area(0);
    private final CobField[] leftKeys, rightKeys;

    /** @param area the SD record area; @param keys key items over that area; @param ascending per key */
    public CobSort(String name, Area area, int len, CobField[] keys, boolean[] ascending) {
        this.name = name;
        this.area = area;
        this.len = len;
        this.keys = keys;
        this.ascending = ascending;
        leftKeys = new CobField[keys.length];
        rightKeys = new CobField[keys.length];
        for (int i = 0; i < keys.length; i++) {
            leftKeys[i] = keys[i].over(left, keys[i].off);
            rightKeys[i] = keys[i].over(right, keys[i].off);
        }
    }

    /** RELEASE: the SD record area's current content. */
    public void release() {
        records.add(Arrays.copyOfRange(area.b, area.base, area.base + len));
    }

    /** USING: every record of the file, which the SORT opens and closes. A record shorter than the SD record is space-padded. */
    public void using(CobFile f) {
        f.check(f.open("INPUT"), false);
        while (true) {
            int st = f.check(f.readNext(), true);
            if (st >= 10) break;
            byte[] r = new byte[len];
            Arrays.fill(r, (byte) ' ');
            int n = Math.min(len, f.lastRecordLength());
            System.arraycopy(f.area.b, f.area.base, r, 0, n);
            records.add(r);
        }
        f.check(f.close(), false);
    }

    /** Sorts the records collected: stable, on the keys in order. */
    public void sort() {
        Comparator<byte[]> cmp = (a, b) -> {
            left.b = a;
            right.b = b;
            for (int i = 0; i < keys.length; i++) {
                int c = CobRt.compare(leftKeys[i], rightKeys[i]);
                if (c != 0) return ascending[i] ? c : -c;
            }
            return 0;
        };
        records.sort(cmp);
        next = 0;
    }

    /** GIVING: every sorted record written to the file, which the SORT opens and closes. */
    public void giving(CobFile f) {
        f.check(f.open("OUTPUT"), false);
        for (byte[] r : records) {
            Arrays.fill(f.area.b, f.area.base, f.area.base + f.maxLen, (byte) ' ');
            System.arraycopy(r, 0, f.area.b, f.area.base, Math.min(r.length, f.maxLen));
            f.check(f.write(f.maxLen), false);
        }
        f.check(f.close(), false);
    }

    /** RETURN: the next sorted record into the SD record area; 0, or 10 at the end. */
    public int returnNext() {
        if (next >= records.size()) return 10;
        System.arraycopy(records.get(next++), 0, area.b, area.base, len);
        return 0;
    }

    /** RELEASE / RETURN outside the SORT's procedures. */
    public static CobSort active(CobSort s, String sd, String verb) {
        if (s == null) throw new CobRt.RuntimeCheck(verb + " for " + sd + " outside a SORT or MERGE procedure");
        return s;
    }

    @Override
    public String toString() {
        return "SORT " + name + " (" + records.size() + " records)";
    }

    /**
     * SORT of a table (COBOL 2002): the occurrences reordered on the keys. GnuCOBOL sorts them with the C library's
     * qsort, whose order for equal keys is unspecified: equal keys stop the run instead of guessing that order.
     */
    public static void table(CobField first, int count, CobField[] keys, boolean[] ascending) {
        int stride = first.len;
        byte[] buf = first.buf();
        int base = first.pos();
        List<byte[]> occ = new ArrayList<>();
        for (int i = 0; i < count; i++) occ.add(Arrays.copyOfRange(buf, base + i * stride, base + (i + 1) * stride));
        Area l = new Area(0), r = new Area(0);
        CobField[] lk = new CobField[keys.length], rk = new CobField[keys.length];
        for (int i = 0; i < keys.length; i++) {
            lk[i] = keys[i].over(l, keys[i].pos() - base);
            rk[i] = keys[i].over(r, keys[i].pos() - base);
        }
        boolean[] tie = {false};
        occ.sort((a, b) -> {
            l.b = a;
            r.b = b;
            for (int i = 0; i < keys.length; i++) {
                int c = CobRt.compare(lk[i], rk[i]);
                if (c != 0) return ascending[i] ? c : -c;
            }
            tie[0] = true;
            return 0;
        });
        if (tie[0]) throw new CobRt.RuntimeCheck("SORT of table " + first.name + " with equal keys: GnuCOBOL leaves their order to qsort");
        for (int i = 0; i < count; i++) System.arraycopy(occ.get(i), 0, buf, base + i * stride, stride);
    }
}
