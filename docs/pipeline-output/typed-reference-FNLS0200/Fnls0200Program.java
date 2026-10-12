package com.mlcpu.capabilities.fnls0200;

import com.mlcpu.rt.ModernProgram;

/**
 * FNLS0200: every CALL 'FNLS0200' reaches call(parameters); the arguments are viewed as typed records and the
 * program's logic (Fnls0200Logic) runs on them. One instance serves every CALL of the run, so WORKING-STORAGE persists.
 */
public final class Fnls0200Program implements ModernProgram {
    private final Fnls0200Logic logic = new Fnls0200Logic();

    @Override
    public void call(byte[][] parameters) {
        logic.call(LoanRequest.over(parameters[0]), CurrencyRec.over(parameters[1]), MathContextRecord.over(parameters[2]), LoanTerms.over(parameters[3]), Status.over(parameters[4]));
    }
}
