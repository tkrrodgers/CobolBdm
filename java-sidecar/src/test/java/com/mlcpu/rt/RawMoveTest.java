package com.mlcpu.rt;

import static org.junit.jupiter.api.Assertions.*;

import java.nio.charset.StandardCharsets;
import java.util.List;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

class RawMoveTest {
    private static final String SITE = MoveSite.encode("MOVEOVL", "MAIN", "MAIN:2:MOVE:L12", 12, 0);

    @AfterEach
    void defaultPolicy() {
        CobConfig.RAW_MOVE_PROFILE = "";
        FrontierHits.clear();
    }

    private static CobField buffer(String text) {
        return CobField.temp(text.getBytes(StandardCharsets.ISO_8859_1));
    }

    @Test
    void geometryClassifiesIdentityJustifiedAliasRippleAndPadding() {
        assertEquals(RawMove.Overlap.DISJOINT, new RawMove.Geometry(0, 4, 4, 4, false).overlap());
        assertEquals(RawMove.Overlap.IDENTICAL, new RawMove.Geometry(0, 4, 0, 6, false).overlap());
        assertEquals(RawMove.Overlap.IDENTICAL, new RawMove.Geometry(0, 4, 2, 2, true).overlap());
        assertEquals(RawMove.Overlap.ORDER_INDEPENDENT, new RawMove.Geometry(0, 4, 0, 2, true).overlap()); // MOVEALIAS
        assertEquals(RawMove.Overlap.ORDER_INDEPENDENT, new RawMove.Geometry(0, 8, 6, 3, false).overlap());
        assertEquals(RawMove.Overlap.SHIFTED, new RawMove.Geometry(0, 4, 1, 3, false).overlap());
        assertEquals(RawMove.Overlap.SHIFTED, new RawMove.Geometry(1, 4, 0, 3, false).overlap());
        // copy ranges are disjoint, but padding overwrites the bytes read (GnuCOBOL order captured as P1, not profiled)
        assertEquals(RawMove.Overlap.SHIFTED, new RawMove.Geometry(5, 3, 0, 10, false).overlap());
    }

    @Test
    void defaultPolicyStopsAShiftedMoveBeforeWritingAndNamesTheIrStep() {
        CobField buf = buffer("ABCDEF");
        CobRt.FrontierException e = assertThrows(CobRt.FrontierException.class, () -> CobRt.move(buf.ref(1, 4), buf.ref(2, 3), SITE));
        assertEquals("ABCDEF", buf.str());
        assertTrue(e.getMessage().startsWith("frontier reached at MAIN:2:MOVE:L12:"), e.getMessage());
        assertTrue(e.getMessage().contains("line 12") && e.getMessage().contains("shift=1") && e.getMessage().contains("default FRONTIER"), e.getMessage());
        assertEquals(1, FrontierHits.hits().size());
        assertTrue(FrontierHits.hits().get(0).startsWith("MAIN:2:MOVE:L12: "));
    }

    @Test
    void identicalAndOrderIndependentOverlapsRunUnderTheDefaultPolicy() {
        CobField buf = buffer("ABCDEFGH");
        CobRt.move(buf.ref(1, 4), buf.ref(1, 4), SITE);
        assertEquals("ABCDEFGH", buf.str());
        CobField justified = CobField.alnum("J", buf.area, 0, 2, true, null, null);
        CobRt.move(buf.ref(1, 4), justified, SITE);
        assertEquals("CDCDEFGH", buf.str());
        CobField wide = buffer("ABCDEFGHIJK");
        CobRt.move(wide.ref(1, 8), wide.ref(7, 3), SITE);
        assertEquals("ABCDEFABCJK", wide.str()); // MOVESAFE O1
    }

    @Test
    void gnuProfileRipplesOnlyItsCapturedShapes() {
        CobConfig.RAW_MOVE_PROFILE = RawMoveProfile.GNUCOBOL_33_STD_IBM.id();
        CobField buf = buffer("ABCDEF");
        CobRt.move(buf.ref(1, 4), buf.ref(2, 3), SITE);
        assertEquals("AAAAEF", buf.str()); // N1
        CobField back = buffer("ABCDEFG");
        CobRt.move(back.ref(2, 4), back.ref(1, 3), SITE);
        assertEquals("BCDDEFG", back.str()); // N8
        CobField padded = buffer("ABCDEFG");
        assertThrows(CobRt.FrontierException.class, () -> CobRt.move(padded.ref(1, 3), padded.ref(2, 4), SITE)); // N6
        assertEquals("ABCDEFG", padded.str());
        CobField just = buffer("ABCDEFGH");
        CobField j2 = CobField.alnum("J2", just.area, 1, 5, true, null, null);
        assertThrows(CobRt.FrontierException.class, () -> CobRt.move(just.ref(1, 4), j2, SITE)); // J2
        CobField longer = CobField.temp(new byte[1100]);
        assertThrows(CobRt.FrontierException.class, () -> CobRt.move(longer.ref(1, 1025), longer.ref(2, 1024), SITE));
    }

    @Test
    void unknownProfileIsAFrontierNotAGuess() {
        CobConfig.RAW_MOVE_PROFILE = "ibm-enterprise-6.4";
        CobField buf = buffer("ABCDEF");
        CobRt.FrontierException e = assertThrows(CobRt.FrontierException.class, () -> CobRt.move(buf.ref(1, 4), buf.ref(2, 3), SITE));
        assertTrue(e.getMessage().contains("unknown raw-move profile 'ibm-enterprise-6.4'"), e.getMessage());
    }

    @Test
    void profileReplaysEveryProbeAndRejectsEvidenceItsOrderDoesNotReproduce() {
        for (RawMoveProfile.Probe p : RawMoveProfile.GNUCOBOL_33_STD_IBM.probes())
            assertEquals(p.captured(), RawMoveProfile.GNUCOBOL_33_STD_IBM.replay(p, RawMove.Order.LEFT_TO_RIGHT_BYTEWISE), p.id());
        RawMoveProfile.Probe snapshot = new RawMoveProfile.Probe("S1", "field", RawMove.Kind.ALNUM, RawMove.Kind.ALNUM, 0, 4, 1, 3,
                new int[]{1, 6}, "AABCEF");
        assertThrows(IllegalArgumentException.class, () -> RawMoveProfile.define("bad", "cc", "d", "capture", 72, List.of(snapshot),
                List.of(new RawMoveProfile.Rule("r", RawMove.Order.LEFT_TO_RIGHT_BYTEWISE, RawMoveProfile.Direction.FORWARD, List.of("S1")))));
        assertThrows(IllegalArgumentException.class, () -> RawMoveProfile.define("bad", "", "d", "capture", 72, List.of(), List.of()));
    }

    @Test
    void runUnitKeepsItsOwnProfileSelection() {
        CobConfig.RAW_MOVE_PROFILE = "";
        try (RunUnit unit = new RunUnit()) {
            try (RunUnit.Scope scope = unit.enter()) {
                CobConfig.RAW_MOVE_PROFILE = RawMoveProfile.GNUCOBOL_33_STD_IBM.id();
            }
            assertEquals("", CobConfig.RAW_MOVE_PROFILE);
            try (RunUnit.Scope scope = unit.enter()) {
                assertEquals(RawMoveProfile.GNUCOBOL_33_STD_IBM.id(), CobConfig.RAW_MOVE_PROFILE);
            }
        }
    }
}
