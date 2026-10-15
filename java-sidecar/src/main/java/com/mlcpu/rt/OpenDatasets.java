package com.mlcpu.rt;

import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;

/**
 * The datasets a normalized step has open, registered by the platform layer's readers and writers. When the step ends, normally
 * or by an abend, {@link JobEngine} closes the ones still open, as GnuCOBOL closes a run unit's open files when the CEE3ABD stub
 * ends it with STOP RUN or a runtime error ends it ({@link CobFile#closeAllOpen()} does the same for the faithful programs): the
 * records written before an abend are in the output whichever implementation ran. Per thread, since a step runs on its caller's.
 */
public final class OpenDatasets {
    private static final ThreadLocal<Set<AutoCloseable>> OPEN = ThreadLocal.withInitial(LinkedHashSet::new);

    private OpenDatasets() {}

    public static void opened(AutoCloseable dataset) {
        OPEN.get().add(dataset);
    }

    public static void closed(AutoCloseable dataset) {
        OPEN.get().remove(dataset);
    }

    /** Implicit CLOSE of every dataset left open, in the order they were opened; a failing close does not stop the others. */
    public static void closeAll() {
        Set<AutoCloseable> open = OPEN.get();
        List<AutoCloseable> left = new ArrayList<>(open);
        open.clear();
        for (AutoCloseable d : left) {
            try {
                d.close();
            } catch (Exception ignored) {
                // As GnuCOBOL's implicit CLOSE: the run unit is ending, there is no one left to report the status to.
            }
        }
    }
}
