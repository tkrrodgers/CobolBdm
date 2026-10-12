package com.mlcpu.rt;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.nio.charset.StandardCharsets;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

/**
 * Storage passed BY REFERENCE at scale (phase 5, the J3S estate passes a 477 MB heap record in every CALL): a LINKAGE
 * record owns no storage until it is bound, and a CALL to a normalized subprogram passes a whole record as the caller's
 * own array instead of copying it in and out.
 */
class ByReferenceStorageTest {

    @AfterEach
    void reset() {
        ProgramRegistry.reset();
    }

    @Test
    void linkageAreaOwnsNoStorageUntilBoundOrUsedUnbound() {
        Area lk = Area.linkage(477_107_239);
        assertEquals(0, lk.b.length, "no 477 MB array per program instance");
        byte[] caller = "ABCDEFGH".getBytes(StandardCharsets.ISO_8859_1);
        Area small = Area.linkage(4);
        small.bind(caller, 2);
        small.materialize((byte) 0); // bound: stays the caller's storage
        assertSame(caller, small.b);
        assertEquals(2, small.base);
        Area unbound = Area.linkage(3);
        unbound.materialize((byte) ' ');
        assertArrayEquals("   ".getBytes(StandardCharsets.ISO_8859_1), unbound.b);
        Area ws = new Area(5);
        ws.materialize((byte) 'X'); // WORKING-STORAGE keeps its own bytes untouched
        assertArrayEquals(new byte[5], ws.b);
    }

    @Test
    void normalizedCallGetsWholeRecordsByReferenceAndSubFieldsCopiedBack() {
        Area heap = new Area(64);
        Area other = new Area(10);
        CobField whole = CobField.group("HEAP", heap, 0, 64, null, null);
        CobField part = CobField.alnum("PART", other, 2, 4, false, null, null);
        java.util.Arrays.fill(heap.b, (byte) '.');
        java.util.Arrays.fill(other.b, (byte) '-');
        byte[][] seen = new byte[2][];
        ProgramRegistry.registerModern("MODREF", params -> {
            seen[0] = params[0];
            seen[1] = params[1];
            params[0][0] = 'H';
            // the caller's record is the callee's: a write is visible at once, not only after the CALL
            assertEquals('H', heap.b[0]);
            assertEquals(4, params[1].length);
            params[1][1] = 'P';
        });
        CobRt.call("MODREF", new CobField[]{whole, part});
        assertSame(heap.b, seen[0], "a whole record is passed as the caller's array");
        assertTrue(seen[1] != other.b, "a part of a record is copied");
        assertEquals('H', heap.b[0]);
        assertEquals("---P------", new String(other.b, StandardCharsets.ISO_8859_1));
    }
}
