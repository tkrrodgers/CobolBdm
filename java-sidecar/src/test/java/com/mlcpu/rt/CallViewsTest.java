package com.mlcpu.rt;

import static org.junit.jupiter.api.Assertions.*;

import java.nio.charset.StandardCharsets;
import org.junit.jupiter.api.Test;

class CallViewsTest {
    @Test
    void overlappingReferencesAreLiveAndContentIsPrivateAndOmittedIsDistinct() {
        try (RunUnit unit = new RunUnit(); RunUnit.Scope scope = unit.enter()) {
            CobField whole = CobField.temp("ABCDEFGH".getBytes(StandardCharsets.ISO_8859_1));
            CobField part = whole.ref(3, 4);
            CobField content = CobRt.byContent(part);
            ProgramRegistry.registerModern("VIEWS", (ModernProgramViews) args -> {
                assertSame(args[0].storage(), args[1].storage());
                assertSame(args[0].field().area, args[1].field().area);
                assertEquals(2, args[1].offset());
                assertEquals(CallArgument.Mode.CONTENT, args[2].mode());
                assertTrue(args[3].omitted());
                assertThrows(CobRt.RuntimeCheck.class, args[3]::field);
                CobRt.move("WXYZ", args[1].field());
                assertEquals("ABWXYZGH", args[0].field().str());
                assertEquals("ABWXYZGH", whole.str());
                CobRt.move("----", args[2].field());
            });
            CobRt.call("VIEWS", new CobField[]{whole, part, content, null});
            assertEquals("ABWXYZGH", whole.str());
            assertEquals("----", content.str());
        }
    }

    @Test
    void legacyArrayAbiRefusesUnsafeOverlapBeforeSideEffects() {
        try (RunUnit unit = new RunUnit(); RunUnit.Scope scope = unit.enter()) {
            CobField whole = CobField.temp("ABCDEFGH".getBytes(StandardCharsets.ISO_8859_1));
            ProgramRegistry.registerModern("OLD", args -> fail("unsafe target must not execute"));
            assertThrows(CobRt.RuntimeCheck.class, () -> CobRt.call("OLD", new CobField[]{whole, whole.ref(2, 3)}));
            assertEquals("ABCDEFGH", whole.str());
        }
    }

    @Test
    void writesBeforeAnExceptionAreVisibleForBothCallAbis() {
        try (RunUnit unit = new RunUnit(); RunUnit.Scope scope = unit.enter()) {
            CobField whole = CobField.temp("ABCDEFGH".getBytes(StandardCharsets.ISO_8859_1));
            ProgramRegistry.registerModern("OLD", args -> {
                args[0][0] = 'X';
                throw new ModernStep.Abend(123, "test");
            });
            assertThrows(CobRt.Abend.class, () -> CobRt.call("OLD", new CobField[]{whole.ref(2, 3)}));
            assertEquals("AXCDEFGH", whole.str());
            ProgramRegistry.registerModern("NEW", (ModernProgramViews) args -> {
                args[0].storage()[args[0].offset()] = 'Y';
                throw new ModernStep.Abend(123, "test");
            });
            assertThrows(CobRt.Abend.class, () -> CobRt.call("NEW", new CobField[]{whole.ref(3, 3)}));
            assertEquals("AXYDEFGH", whole.str());
        }
    }

    @Test
    void multiReceiverMoveRereadsAnAliasedSenderAfterEachReceiver() {
        CobField sender = CobField.temp("ABCD".getBytes(StandardCharsets.ISO_8859_1));
        CobField justified = CobField.alnum("TARGET", sender.area, 0, 2, true, null, null);
        CobField output = CobField.temp(new byte[4]);
        CobRt.move(sender, new CobField[]{justified, output});
        assertEquals("CDCD", sender.str());
        assertEquals("CDCD", output.str());
    }

    @Test
    void shiftedRawOverlapFailsClosedBeforeWritingAnyBytes() {
        CobField source = CobField.temp("ABCD".getBytes(StandardCharsets.ISO_8859_1));
        assertThrows(CobRt.FrontierException.class, () -> CobRt.move(source, source.ref(2, 3)));
        assertEquals("ABCD", source.str());
    }
}
