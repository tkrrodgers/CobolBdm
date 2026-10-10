package com.mlcpu.rt;

import java.math.BigDecimal;
import java.util.Map;

/**
 * Base of every generated program: the paragraph driver (fall-through, GO TO, PERFORM ranges), CALL binding of
 * linkage areas, and the ProgramState seed/collect boundary.
 */
public abstract class CobolBase implements MigratedProgram {
    protected int _goto = -1;
    protected boolean _called;
    private boolean _initialized;

    /** Allocate storage, apply VALUE clauses, open nothing. */
    protected abstract void _init();

    protected abstract void _dispatch(int pc);

    protected abstract int _count();

    /** Linkage areas in PROCEDURE DIVISION USING order. */
    protected abstract Area[] _linkage();

    /** Elementary, uniquely named fields for the ProgramState boundary. */
    protected abstract Map<String, CobField> _fields();

    public abstract String programId();

    /** Paragraph names in dispatch order. */
    protected String[] _paragraphNames() {
        return new String[0];
    }

    /**
     * Characterization entry point: seed fields by COBOL name, PERFORM one paragraph (and whatever it performs), then
     * collect every seedable field, the DISPLAY lines and the outcome. Values are strings so scale differences compare exactly.
     */
    public ProgramState runParagraph(String paragraph, Map<String, String> inputs) {
        String[] names = _paragraphNames();
        int idx = java.util.Arrays.asList(names).indexOf(paragraph.toUpperCase());
        if (idx < 0) throw new IllegalArgumentException(programId() + ": no paragraph " + paragraph);
        return runRange(idx, idx, inputs);
    }

    /** Truth-table entry point: seed fields as {@link #runParagraph} does, then run the whole program from its first paragraph. */
    public ProgramState runProgram(Map<String, String> inputs) {
        return runRange(0, _count() - 1, inputs);
    }

    private ProgramState runRange(int lo, int hi, Map<String, String> inputs) {
        ensureInit();
        Map<String, CobField> fields = _fields();
        for (Map.Entry<String, String> e : inputs.entrySet()) {
            CobField f = fields.get(e.getKey());
            if (f == null) continue;
            if (f.isNumeric()) CobRt.move(new NumLit(e.getValue()), f);
            else CobRt.move(e.getValue(), f);
        }
        DisplaySink prev = DisplaySink.current();
        DisplaySink.Capturing cap = new DisplaySink.Capturing();
        DisplaySink.set(cap);
        String outcome = "COMPLETED";
        try {
            perform(lo, hi);
        } catch (CobRt.ProgramExit e) {
            outcome = "GOBACK";
        } catch (CobRt.StopRun e) {
            outcome = "STOP_RUN";
        } catch (CobRt.Abend e) {
            outcome = "ABEND:" + e.code;
        } catch (CobRt.LoopLimit e) {
            outcome = "LOOP_LIMIT";
        } catch (CobRt.FrontierException e) {
            outcome = "FRONTIER";
        } catch (CobRt.RuntimeCheck | CobRt.SizeError e) {
            outcome = "RUNTIME_CHECK:" + e.getMessage();
        } finally {
            DisplaySink.set(prev);
        }
        ProgramState out = new ProgramState();
        for (Map.Entry<String, CobField> e : fields.entrySet()) {
            CobField f = e.getValue();
            out.set(e.getKey(), f.isNumeric() ? f.dec().toPlainString() : f.str());
        }
        out.set("@DISPLAY", String.join("\n", cap.lines()));
        out.set("@OUTCOME", outcome);
        return out;
    }

    /** Run paragraphs lo..hi: fall through, honour GO TO; returns when control leaves hi. */
    protected final void perform(int lo, int hi) {
        int pc = lo;
        int n = _count();
        while (true) {
            _goto = -1;
            _dispatch(pc);
            if (_goto >= 0) {
                pc = _goto;
                continue;
            }
            if (pc == hi) return;
            pc++;
            if (pc >= n) return;
        }
    }

    public final void ensureInit() {
        if (!_initialized) {
            _init();
            _initialized = true;
        }
    }

    /** Enter the program (main or CALLed). Linkage areas are bound to the arguments BY REFERENCE. */
    public final void call(CobField[] args, boolean called) {
        ensureInit();
        Area[] lk = _linkage();
        for (int i = 0; i < Math.min(args.length, lk.length); i++) {
            if (args[i] != null) lk[i].bind(args[i].buf(), args[i].pos());
        }
        boolean prev = _called;
        _called = called;
        try {
            perform(0, _count() - 1);
        } catch (CobRt.ProgramExit e) {
            // GOBACK / EXIT PROGRAM
        } finally {
            _called = prev;
        }
    }

    /** EXIT PROGRAM in a main program has no effect. */
    protected final void exitProgram() {
        if (_called) throw CobRt.PROGRAM_EXIT;
    }

    @Override
    public ProgramState run(ProgramState state) {
        ensureInit();
        Map<String, CobField> fields = _fields();
        for (Map.Entry<String, Object> e : state.fields().entrySet()) {
            CobField f = fields.get(e.getKey());
            if (f == null || e.getKey().startsWith("@")) continue;
            Object v = e.getValue();
            if (v instanceof Number num) CobRt.move(new NumLit(new BigDecimal(num.toString())), f);
            else CobRt.move(String.valueOf(v), f);
        }
        DisplaySink prevSink = DisplaySink.current();
        DisplaySink.Capturing cap = new DisplaySink.Capturing();
        DisplaySink.set(cap);
        ProgramState out = new ProgramState();
        try {
            call(new CobField[0], false);
        } catch (CobRt.StopRun s) {
            // run unit ended
        } finally {
            DisplaySink.set(prevSink);
        }
        for (Map.Entry<String, CobField> e : fields.entrySet()) {
            CobField f = e.getValue();
            out.set(e.getKey(), f.isNumeric() ? f.dec() : f.str());
        }
        out.set("@DISPLAY", cap.lines());
        out.set("@FRONTIER_HITS", FrontierHits.hits());
        out.set("@RETURN_CODE", CobRt.RETURN_CODE.dec().intValue());
        return out;
    }

    @Override
    public ProgramState resumeFrom(String paragraph, ProgramState state) {
        return state;
    }
}
