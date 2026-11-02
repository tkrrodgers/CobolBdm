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
        IllegalStateException failure = null;
        for (AutoCloseable d : left) {
            try {
                d.close();
            } catch (Exception e) {
                if (failure == null) failure = new IllegalStateException("implicit dataset CLOSE failed", e);
                else failure.addSuppressed(e);
            }
        }
        if (failure != null) throw failure;
    }

    record State(Set<AutoCloseable> datasets) {
        void restore() { OPEN.set(new LinkedHashSet<>(datasets)); }
    }
    static State capture() { return new State(new LinkedHashSet<>(OPEN.get())); }
    static State fresh() { return new State(Set.of()); }
}
