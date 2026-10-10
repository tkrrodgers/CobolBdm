package com.mlcpu.rt;

import java.nio.file.Path;
import java.time.OffsetDateTime;

/**
 * A normalized (non-transliterated) implementation of a converted program, run by {@link JobEngine} in place of the
 * faithful {@code Prog_*} class when the job selects {@code mlcpu.implementation=normalized}.
 */
@FunctionalInterface
public interface ModernStep {
    /** Runs the step; returns the step return code. Throw {@link Abend} for an abnormal end. */
    int run(Context ctx) throws Exception;

    /** What a step may see of its environment: datasets by DD name, the PARM, the console, the batch clock. */
    interface Context {
        Path dataset(String dd);

        /** JCL PARM text, or null when the step has none. */
        String parm();

        void display(String line);

        /** The batch clock (pinned in tests and parity runs). */
        OffsetDateTime now();

        /** "gnucobol" (digits and a separate sign) or "zos" (zoned bytes) for numeric console output. */
        String displayNumeric();

        /** Byte that unwritten record storage holds (the oracle's storage default). */
        byte storageDefault();

        /**
         * {@code CALL 'program' USING ...}: runs another program of the job (its faithful translation, or its normalized
         * {@link ModernProgram} when the job selects one) with the arguments BY REFERENCE: each array is the argument's
         * storage, and what the called program writes into it is visible here when the call returns.
         */
        default void call(String program, byte[]... parameters) {
            throw new UnsupportedOperationException("CALL " + program + " is not available in this context");
        }
    }

    /** Abnormal end with a user abend code (the faithful programs call CEE3ABD with 999). */
    final class Abend extends RuntimeException {
        public final int code;

        public Abend(int code, String message) {
            super(message);
            this.code = code;
        }
    }
}
