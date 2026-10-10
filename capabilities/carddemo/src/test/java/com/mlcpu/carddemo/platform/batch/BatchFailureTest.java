package com.mlcpu.carddemo.platform.batch;

import static org.junit.jupiter.api.Assertions.assertEquals;

import com.mlcpu.carddemo.platform.records.DatasetException;
import com.mlcpu.rt.ModernStep;
import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
import org.junit.jupiter.api.Test;

/** The CardDemo failure convention: message, FILE STATUS line (Z-DISPLAY-IO-STATUS), ABENDING PROGRAM, user abend 999. */
class BatchFailureTest {

    @Test
    void numericStatusesPrintAsFourDigits() {
        assertEquals("0023", BatchFailure.statusDigits("23"));
        assertEquals("0035", BatchFailure.statusDigits("35"));
        assertEquals("0040", BatchFailure.statusDigits("4"));
    }

    @Test
    void implementorAndNonNumericStatusesPrintTheSecondByteAsBinary() {
        assertEquals("9065", BatchFailure.statusDigits("9A"));
        assertEquals("9048", BatchFailure.statusDigits("90"));
        assertEquals("A066", BatchFailure.statusDigits("AB"));
        assertEquals("1088", BatchFailure.statusDigits("1X"));
    }

    @Test
    void abendDisplaysTheConventionAndCarriesCode999() {
        List<String> console = new ArrayList<>();
        ModernStep.Abend a = BatchFailure.abend(console::add, "ERROR READING XREF FILE", new DatasetException("x", "23"));
        assertEquals(List.of("ERROR READING XREF FILE", "FILE STATUS IS: NNNN0023", "ABENDING PROGRAM"), console);
        assertEquals(999, a.code);
        console.clear();
        BatchFailure.abend(console::add, "ERROR", new IOException("disk"));
        assertEquals("FILE STATUS IS: NNNN0030", console.get(1));
    }

    @Test
    void openAndCloseFailuresAbend() {
        List<String> console = new ArrayList<>();
        try {
            BatchFailure.open(console::add, "ERROR OPENING X", () -> { throw new DatasetException("missing", "35"); });
        } catch (ModernStep.Abend expected) {
            assertEquals(List.of("ERROR OPENING X", "FILE STATUS IS: NNNN0035", "ABENDING PROGRAM"), console);
        }
        console.clear();
        try {
            BatchFailure.close(console::add, "ERROR CLOSING Y", () -> { throw new IllegalStateException("x"); });
        } catch (ModernStep.Abend expected) {
            assertEquals("FILE STATUS IS: NNNN0030", console.get(1));
        }
        assertEquals("ok", BatchFailure.open(console::add, "unused", () -> "ok"));
    }
}
