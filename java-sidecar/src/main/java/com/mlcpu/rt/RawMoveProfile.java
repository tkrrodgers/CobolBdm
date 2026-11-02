package com.mlcpu.rt;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * An explicit, immutable statement of how one target compiler and dialect executes shifted overlapping raw MOVEs, pinned
 * to captured oracle evidence. A profile only covers the exact shapes its probes exercised: sender/receiver kind pairs,
 * shift direction, left alignment without padding, and copy lengths up to the longest probe. Every probe is replayed
 * through the profile's order when the profile is defined; a profile whose order does not reproduce its capture cannot
 * exist. Profiles are selected by id ({@link CobConfig#RAW_MOVE_PROFILE}); none is selected by default.
 *
 * <p>No IBM Enterprise COBOL profile exists: there is no target capture, and IBM semantics are not inferred.
 */
public final class RawMoveProfile {
    public enum Direction { FORWARD, BACKWARD }

    /**
     * One captured oracle case over a buffer holding {@link #PATTERN} then spaces: the MOVE of {@code sender} bytes at
     * {@code senderPos} to the receiver at {@code receiverPos} (0-based), and the DISPLAY of 1-based {@code slices}
     * (from, length pairs) the oracle printed as {@code captured}.
     */
    public record Probe(String id, String form, RawMove.Kind sender, RawMove.Kind receiver, int senderPos, int senderLen,
                        int receiverPos, int receiverLen, int[] slices, String captured) {
        public RawMove.Geometry geometry() { return new RawMove.Geometry(senderPos, senderLen, receiverPos, receiverLen, false); }
        public String line() { return id + ":" + captured; }
    }

    /** A copy order for one shift direction, with the probes that evidence it. Coverage is derived from the evidence only. */
    public record Rule(String id, RawMove.Order order, Direction direction, List<String> evidence) {}

    private record Coverage(Rule rule, Set<String> kindPairs, int maxCopied) {}

    public static final String PATTERN = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789<>?;:[]{}=";

    private final String id;
    private final String targetCompiler;
    private final String dialect;
    private final String captureProvenance;
    private final int bufferLength;
    private final List<Probe> probes;
    private final List<Coverage> coverage;

    private RawMoveProfile(String id, String targetCompiler, String dialect, String captureProvenance, int bufferLength,
                           List<Probe> probes, List<Rule> rules) {
        if (id == null || id.isBlank() || targetCompiler == null || targetCompiler.isBlank() || dialect == null || dialect.isBlank()
                || captureProvenance == null || captureProvenance.isBlank())
            throw new IllegalArgumentException("a raw-move profile needs an id, target compiler, dialect and capture provenance");
        this.id = id;
        this.targetCompiler = targetCompiler;
        this.dialect = dialect;
        this.captureProvenance = captureProvenance;
        this.bufferLength = bufferLength;
        this.probes = List.copyOf(probes);
        Map<String, Probe> byId = new LinkedHashMap<>();
        for (Probe p : probes) if (byId.put(p.id(), p) != null) throw new IllegalArgumentException(id + ": duplicate probe " + p.id());
        List<Coverage> cov = new ArrayList<>();
        Set<String> used = new java.util.HashSet<>();
        for (Rule r : rules) {
            if (r.evidence().isEmpty()) throw new IllegalArgumentException(id + "/" + r.id() + ": a rule needs captured evidence");
            Set<String> pairs = new java.util.TreeSet<>();
            int max = 0;
            for (String e : r.evidence()) {
                Probe p = byId.get(e);
                if (p == null) throw new IllegalArgumentException(id + "/" + r.id() + ": unknown probe " + e);
                RawMove.Geometry g = p.geometry();
                if (g.overlap() != RawMove.Overlap.SHIFTED || g.padded() || direction(g) != r.direction())
                    throw new IllegalArgumentException(id + "/" + r.id() + ": probe " + e + " (" + g + ") is not a " + r.direction() + " unpadded shifted move");
                String replay = replay(p, r.order());
                if (!replay.equals(p.captured()))
                    throw new IllegalArgumentException(id + "/" + r.id() + ": " + r.order() + " gives " + p.id() + ":" + replay + ", the oracle captured " + p.line());
                pairs.add(p.sender() + ">" + p.receiver());
                max = Math.max(max, g.copied());
                used.add(e);
            }
            cov.add(new Coverage(r, Set.copyOf(pairs), max));
        }
        for (Probe p : probes) if (!used.contains(p.id())) throw new IllegalArgumentException(id + ": probe " + p.id() + " evidences no rule");
        this.coverage = List.copyOf(cov);
    }

    /** Package-private: tests define profiles to prove the verification; production code selects built-ins only. */
    static RawMoveProfile define(String id, String targetCompiler, String dialect, String captureProvenance, int bufferLength,
                                 List<Probe> probes, List<Rule> rules) {
        return new RawMoveProfile(id, targetCompiler, dialect, captureProvenance, bufferLength, probes, rules);
    }

    public String id() { return id; }
    public String targetCompiler() { return targetCompiler; }
    public String dialect() { return dialect; }
    public String captureProvenance() { return captureProvenance; }
    public List<Probe> probes() { return probes; }
    public List<Rule> rules() { return coverage.stream().map(Coverage::rule).toList(); }

    static Direction direction(RawMove.Geometry g) {
        return g.shift() > 0 ? Direction.FORWARD : Direction.BACKWARD;
    }

    /** The rule covering a shifted move of this shape, or null (the move stops at a frontier). */
    public Rule rule(RawMove.Kind sender, RawMove.Kind receiver, RawMove.Geometry g) {
        if (g.right() || g.padded() || g.overlap() != RawMove.Overlap.SHIFTED) return null;
        String pair = sender + ">" + receiver;
        for (Coverage c : coverage)
            if (c.rule().direction() == direction(g) && c.kindPairs().contains(pair) && g.copied() <= c.maxCopied()) return c.rule();
        return null;
    }

    /** Describes what the profile covers (for ledgers and reports). */
    public List<String> coverage() {
        List<String> out = new ArrayList<>();
        for (Coverage c : coverage)
            out.add(c.rule().id() + ": " + c.rule().order() + " " + c.rule().direction() + " " + c.kindPairs() + " copied<=" + c.maxCopied()
                    + " unpadded, not JUSTIFIED; evidence " + c.rule().evidence());
        return out;
    }

    /** The probe's DISPLAY after replaying it through {@code order} over the probe buffer. */
    String replay(Probe p, RawMove.Order order) {
        byte[] buf = buffer();
        order.apply(buf, p.geometry());
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < p.slices().length; i += 2)
            sb.append(new String(buf, p.slices()[i] - 1, p.slices()[i + 1], StandardCharsets.ISO_8859_1));
        return sb.toString();
    }

    private byte[] buffer() {
        byte[] buf = new byte[bufferLength];
        Arrays.fill(buf, (byte) ' ');
        byte[] pat = PATTERN.getBytes(StandardCharsets.ISO_8859_1);
        System.arraycopy(pat, 0, buf, 0, Math.min(pat.length, buf.length));
        return buf;
    }

    // ------------------------------------------------------------------ built-in profiles

    private static String rep(char c, int n) { return String.valueOf(c).repeat(n); }

    private static Probe probe(String id, String form, RawMove.Kind s, RawMove.Kind r, int sp, int sl, int rp, int rl, String captured, int... slices) {
        return new Probe(id, form, s, r, sp, sl, rp, rl, slices.length == 0 ? new int[]{1, Math.max(sp + sl, rp + rl) + 2} : slices, captured);
    }

    private static final RawMove.Kind X = RawMove.Kind.ALNUM, G = RawMove.Kind.GROUP, U = RawMove.Kind.UNSIGNED_DISPLAY_INTEGER;

    /**
     * GnuCOBOL 3.3-dev with -std=ibm. ibm-strict.conf sets {@code move-ibm: yes}; for a MOVE that neither pads nor
     * justifies, cobc emits {@code cob_move_ibm(dst, src, n)} (left to right, byte by byte), which ripples a forward
     * shift (ABCD to the 3 bytes at offset 1 gives AAAA). Moves that pad or are JUSTIFIED go through {@code cob_move}
     * with other orders (MOVEOVLX.cbl: JUSTIFIED pads before copying) and are not covered.
     */
    public static final RawMoveProfile GNUCOBOL_33_STD_IBM = new RawMoveProfile(
            "gnucobol-3.3-std-ibm",
            "cobc (GnuCOBOL) 3.3-dev.0 (Windows mingw64 build; cobc.exe sha256 2837F15B1B420D2C09F45FCEDF4B0DA98E635E660605EF6BBCA7A2B116AF8D97, "
                    + "libcob-4.dll sha256 76A7D5FF6E1905BD2E0026B4D6A3FADC03841059FF6D15A8FC4E3CE33CEC0B5B)",
            "-std=ibm -fsign=EBCDIC (ibm-strict.conf move-ibm: yes; sha256 E1EEBCDCEBAE06278A533875E20E7CAC809465DAC8BA2211D7D41DD3B1A25CE6)",
            "MlCpu/src/CobolConformance/corpus/modernization/MOVEOVL.cbl compiled with cobc -x -std=ibm -fsign=EBCDIC and run, "
                    + "2026-10-31; cobc -C shows every probed MOVE as cob_move_ibm; re-verified by the ModernizationMove conformance suite",
            1100,
            List.of(
                    probe("N1", "field", X, X, 0, 4, 1, 3, "AAAAEF"),
                    probe("N2", "field", X, X, 0, 8, 1, 7, rep('A', 8) + "IJ"),
                    probe("N3", "field", X, X, 0, 16, 1, 15, rep('A', 16) + "QR"),
                    probe("N4", "field", X, X, 0, 32, 1, 31, rep('A', 32) + "gh"),
                    probe("N5", "field", X, X, 0, 64, 1, 63, rep('A', 64) + "?;"),
                    probe("N9", "field", X, X, 0, 4, 2, 4, "ABABABGH"),
                    probe("NA", "field", X, X, 0, 8, 3, 8, "ABCABCABCABLM"),
                    probe("NB", "field", X, X, 0, 1024, 1, 1023, rep('A', 13) + "   ", 1, 8, 1020, 8),
                    probe("N8", "field", X, X, 1, 4, 0, 3, "BCDDEFG"),
                    probe("NC", "field", X, X, 1, 1023, 0, 1023, "BCDEFGHI        ", 1, 8, 1020, 8),
                    probe("G1", "group", G, G, 0, 4, 1, 3, "AAAAEF"),
                    probe("G2", "group", G, G, 0, 8, 1, 7, rep('A', 8) + "IJ"),
                    probe("G3", "group", G, G, 0, 64, 1, 63, rep('A', 64) + "?;"),
                    probe("G4", "group", G, G, 1, 4, 0, 4, "BCDEEFG"),
                    probe("GE", "group to field", G, X, 0, 4, 1, 3, "AAAAEF"),
                    probe("EG", "field to group", X, G, 0, 4, 1, 3, "AAAAEF"),
                    probe("GB", "group to field", G, X, 1, 4, 0, 3, "BCDDEFG"),
                    probe("BG", "field to group", X, G, 1, 4, 0, 3, "BCDDEFG"),
                    probe("U1", "PIC 9 DISPLAY to field", U, X, 0, 4, 1, 3, "AAAAEF"),
                    probe("U2", "PIC 9 DISPLAY to field", U, X, 1, 4, 0, 3, "BCDDEFG"),
                    probe("A1", "PIC A", X, X, 0, 4, 1, 3, "AAAAEF"),
                    probe("A2", "PIC A", X, X, 1, 4, 0, 3, "BCDDEFG"),
                    probe("R1", "reference modification", X, X, 0, 4, 1, 3, "AAAAEF", 1, 6),
                    probe("R2", "reference modification", X, X, 0, 16, 1, 15, rep('A', 16) + "QR", 1, 18),
                    probe("R3", "reference modification", X, X, 0, 64, 1, 63, rep('A', 64) + "?;", 1, 66),
                    probe("R4", "reference modification", X, X, 1, 4, 0, 4, "BCDEEF", 1, 6),
                    probe("V1", "dynamic reference modification", X, X, 0, 4, 1, 3, "AAAAEF", 1, 6),
                    probe("V2", "dynamic reference modification", X, X, 0, 64, 1, 63, rep('A', 64) + "?;", 1, 66)),
            List.of(
                    new Rule("cob_move_ibm-forward", RawMove.Order.LEFT_TO_RIGHT_BYTEWISE, Direction.FORWARD,
                            List.of("N1", "N2", "N3", "N4", "N5", "N9", "NA", "NB", "G1", "G2", "G3", "GE", "EG", "U1", "A1", "R1", "R2", "R3", "V1", "V2")),
                    new Rule("cob_move_ibm-backward", RawMove.Order.LEFT_TO_RIGHT_BYTEWISE, Direction.BACKWARD,
                            List.of("N8", "NC", "G4", "GB", "BG", "U2", "A2", "R4"))));

    private static final Map<String, RawMoveProfile> BUILT_IN = Map.of(GNUCOBOL_33_STD_IBM.id(), GNUCOBOL_33_STD_IBM);

    /** A built-in profile by id, or null. */
    public static RawMoveProfile builtIn(String id) {
        return id == null ? null : BUILT_IN.get(id.trim());
    }

    public static Set<String> builtInIds() {
        return new java.util.TreeSet<>(BUILT_IN.keySet());
    }

    /**
     * {@code --captures <id>}: the oracle lines the profile pins, in probe order (the conformance suite compares them with
     * a fresh native run). {@code --describe <id>}: identity, provenance and coverage.
     */
    public static void main(String[] args) {
        if (args.length != 2 || builtIn(args[1]) == null) {
            System.err.println("usage: RawMoveProfile --captures|--describe <id>; built-in: " + builtInIds());
            System.exit(2);
        }
        RawMoveProfile p = builtIn(args[1]);
        if ("--captures".equals(args[0])) {
            for (Probe probe : p.probes) System.out.println(probe.line());
        } else {
            System.out.println("id: " + p.id + "\ntarget: " + p.targetCompiler + "\ndialect: " + p.dialect + "\ncapture: " + p.captureProvenance);
            for (String c : p.coverage()) System.out.println("rule " + c);
        }
    }
}
