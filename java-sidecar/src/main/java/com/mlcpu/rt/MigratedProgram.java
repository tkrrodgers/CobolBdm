package com.mlcpu.rt;

public interface MigratedProgram {
    ProgramState run(ProgramState state);

    ProgramState resumeFrom(String paragraph, ProgramState state);
}
