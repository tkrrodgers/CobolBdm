package com.mlcpu.rt;

/**
 * A normalized (non-transliterated) implementation of a CALLed subprogram. When the job selects
 * {@code mlcpu.implementation=normalized} for the program, {@code CALL 'PGM' USING a b c} reaches {@link #call} instead of
 * the faithful {@code Prog_*} class.
 */
@FunctionalInterface
public interface ModernProgram {
    /**
     * One CALL. {@code parameters[i]} holds the bytes of the i-th USING argument (its full length, in the caller's record
     * layout). Arguments are passed BY REFERENCE: whatever this method writes into the arrays is what the caller sees after
     * the CALL returns. Throw {@link ModernStep.Abend} for an abnormal end.
     */
    void call(byte[][] parameters) throws Exception;
}
