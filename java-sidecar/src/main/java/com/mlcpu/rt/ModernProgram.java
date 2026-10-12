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
     * the CALL returns. A whole level-01 record is passed as the caller's own array (its length is the record's), so the
     * caller's storage changes as the method writes, as in COBOL. Throw {@link ModernStep.Abend} for an abnormal end.
     */
    void call(byte[][] parameters) throws Exception;

    /**
     * {@code CALL 'program' USING ...} made by a normalized subprogram: runs another program of the job (its faithful
     * translation, or its normalized implementation when the job selects one) with the arguments BY REFERENCE: each array is
     * the argument's storage (a record kit's {@code bytes()}), and what the called program writes into it is visible here when
     * the call returns. An abnormal end of the called program ends this one too. The same CALL a step makes with
     * {@link ModernStep.Context#call}.
     */
    static void callProgram(String program, byte[]... parameters) {
        CobField[] args = new CobField[parameters.length];
        for (int i = 0; i < parameters.length; i++) args[i] = CobField.temp(parameters[i]);
        CobRt.call(program, args);
    }
}
