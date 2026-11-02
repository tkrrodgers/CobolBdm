package com.mlcpu.rt;

import java.util.Objects;
import java.util.concurrent.locks.ReentrantLock;

/**
 * An explicit COBOL run unit. The compatibility runtime's static ABI is installed and restored under one lock;
 * independent units retain their own registers, programs, external storage, DDs, clock and output sinks.
 * Compatibility execution is serialized, not parallel. Plain typed domain rules need no run-unit scope.
 */
public final class RunUnit implements AutoCloseable {
    private static final ReentrantLock LOCK = new ReentrantLock(true);
    private static final ThreadLocal<RunUnit> CURRENT = new ThreadLocal<>();
    private static final ThreadLocal<Scope> SCOPES = new ThreadLocal<>();
    private State state;
    private boolean closed;
    private int activeCalls;

    public RunUnit() {
        LOCK.lock();
        try {
            state = State.fresh();
        } finally {
            LOCK.unlock();
        }
    }

    public static RunUnit current() {
        return CURRENT.get();
    }

    public Scope enter() {
        LOCK.lock();
        try {
            if (closed) throw new IllegalStateException("run unit is closed");
            RunUnit previous = CURRENT.get();
            State saved = State.capture();
            if (previous != this) state.restore();
            CURRENT.set(this);
            Scope scope = new Scope(this, previous, saved, SCOPES.get());
            SCOPES.set(scope);
            return scope;
        } catch (RuntimeException e) {
            LOCK.unlock();
            throw e;
        }
    }

    /** Serializes callers of the original runtime API without changing their process-default state. */
    static Guard guard() {
        LOCK.lock();
        return new Guard();
    }

    static void enteringCall() {
        RunUnit unit = current();
        if (unit != null) unit.activeCalls++;
    }

    static void leavingCall() {
        RunUnit unit = current();
        if (unit != null) unit.activeCalls--;
    }

    @Override
    public void close() {
        LOCK.lock();
        try {
            if (closed) return;
            if (activeCalls != 0) throw new IllegalStateException("cannot close an executing run unit");
            try (Scope scope = enter()) {
                closeResources();
                ProgramRegistry.reset();
                CobRt.resetRunUnit();
            } finally {
                closed = true;
            }

        } finally {
            LOCK.unlock();
        }
    }

    static void closeResources() {
        RuntimeException failure = null;
        try { CobFile.closeAllOpen(); } catch (RuntimeException e) { failure = e; }
        try { OpenDatasets.closeAll(); } catch (RuntimeException e) {
            if (failure == null) failure = e; else failure.addSuppressed(e);
        }
        if (failure != null) throw failure;
    }

    public static final class Scope implements AutoCloseable {
        private final RunUnit unit;
        private final RunUnit previous;
        private final State saved;
        private final Scope previousScope;
        private final Thread thread = Thread.currentThread();
        private boolean closed;

        private Scope(RunUnit unit, RunUnit previous, State saved, Scope previousScope) {
            this.unit = unit;
            this.previous = previous;
            this.saved = saved;
            this.previousScope = previousScope;
        }

        @Override
        public void close() {
            if (closed) return;
            if (Thread.currentThread() != thread || CURRENT.get() != unit || SCOPES.get() != this)
                throw new IllegalStateException("run-unit scopes must close on their entering thread in stack order");
            try {
                unit.state = State.capture();
                if (previous != unit) saved.restore();
                CURRENT.set(previous);
                SCOPES.set(previousScope);
                closed = true;
            } finally {
                LOCK.unlock();
            }
        }
    }

    static final class Guard implements AutoCloseable {
        private boolean closed;
        @Override public void close() {
            if (!closed) {
                closed = true;
                LOCK.unlock();
            }
        }
    }

    private record State(CobConfig.State config, CobClock.State clock, CobRt.State runtime,
                         CobFile.State files, ProgramRegistry.State programs, FrontierHits.State frontiers,
                         OpenDatasets.State datasets, JobEngine.State implementations,
                         DisplaySink display, java.io.BufferedReader input, boolean ebcdic) {
        static State capture() {
            return new State(CobConfig.capture(), CobClock.capture(), CobRt.capture(), CobFile.capture(),
                    ProgramRegistry.capture(), FrontierHits.capture(), OpenDatasets.capture(), JobEngine.capture(),
                    DisplaySink.current(), SysIn.current(), RecordCharset.isEbcdic());
        }

        static State fresh() {
            return new State(CobConfig.capture(), CobClock.fresh(), CobRt.fresh(), CobFile.fresh(),
                    ProgramRegistry.fresh(), FrontierHits.fresh(), OpenDatasets.fresh(), JobEngine.capture(),
                    new DisplaySink.Capturing(), new java.io.BufferedReader(new java.io.StringReader("")), RecordCharset.isEbcdic());
        }

        void restore() {
            config.restore();
            clock.restore();
            runtime.restore();
            files.restore();
            programs.restore();
            frontiers.restore();
            datasets.restore();
            implementations.restore();
            DisplaySink.set(Objects.requireNonNull(display));
            SysIn.set(Objects.requireNonNull(input));
            RecordCharset.restore(ebcdic);
        }
    }
}
