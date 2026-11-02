package com.mlcpu.rt;

/** A modern compatibility subprogram that preserves overlapping BY REFERENCE arguments during execution. */
@FunctionalInterface
public interface ModernProgramViews extends ModernProgram {
    void callViews(CallArgument[] arguments) throws Exception;

    @Override
    default void call(byte[][] parameters) throws Exception {
        CobField[] fields = new CobField[parameters.length];
        for (int i = 0; i < parameters.length; i++)
            fields[i] = parameters[i] == null ? null : CobField.temp(parameters[i]);
        callViews(CallArgument.snapshot(fields));
    }
}
