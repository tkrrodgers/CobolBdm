package com.mlcpu.rt;

import java.math.BigDecimal;
import java.util.Map;

/**
 * Base of every generated program: the paragraph driver (fall-through, GO TO, PERFORM ranges), CALL binding of
 * linkage areas, and the ProgramState seed/collect boundary.
 */
public abstract class CobolBase implements MigratedProgram {
    protected int _goto = -1;
    private Area[] linkage;
    protected boolean _called;
    private boolean _initialized;

    /** Allocate storage, apply VALUE clauses, open nothing. */
    protected abstract void _init();

    protected abstract void _dispatch(int pc);

    protected abstract int _count();

    /** PROGRAM-ID ... IS INITIAL: each CALL starts from the initial state and closes the files the program left open. */
    protected boolean _initial() {
        return false;
    }

    /** LOCAL-STORAGE areas: allocated and initialized afresh for each invocation (recursive ones included). */
    private static final Area[] NO_AREAS = new Area[0];
    private static final byte[][] NO_SAVED = new byte[0][];

    protected Area[] _localAreas() {
        return NO_AREAS;
    }

    /** Applies LOCAL-STORAGE's default byte and VALUE clauses to the current invocation's areas. */
    protected void _initLocal() {
    }

    /** The program's files (an INITIAL program closes those left open when it returns). */
    protected CobFile[] _files() {
        return new CobFile[0];
    }

    /** The first paragraph the program runs: the one after DECLARATIVES. */
    protected int _start() {
        return 0;
    }

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
        return runRange(_start(), _count() - 1, inputs);
    }

    private ProgramState runRange(int lo, int hi, Map<String, String> inputs) {
        ensureInit();
        materializeLinkage();
        Map<String, CobField> fields = _fields();
        // Seeds in the program's declaration order (_fields() is ordered), not the map's: a Map.of / Map.ofEntries iterates
        // in an order salted per JVM, and seeds of overlapping fields (REDEFINES, a date over its year/month/day) leave
        // different bytes in different orders, so a characterization golden pinned by one JVM drifted in the next
        // (J3S's LCLE0100 and the date kernel FNDTKRN, 47 tests).
        for (Map.Entry<String, CobField> fe : fields.entrySet()) {
            String v = inputs.get(fe.getKey());
            if (v == null) continue;
            CobField f = fe.getValue();
            if (f.isNumeric()) CobRt.move(new NumLit(v), f);
            else CobRt.move(v, f);
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
        } catch (IndexOutOfBoundsException e) {
            // A subscript or reference modification outside its record: undefined storage in COBOL (an abend with
            // SSRANGE). Both versions of a paragraph must reach it at the same point, so it is an outcome, not an error.
            outcome = "RUNTIME_CHECK:storage outside the record";
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
        long back = 0; // GO TO jumps back to an earlier paragraph of this range: a GO TO loop
        while (true) {
            _goto = -1;
            _dispatch(pc);
            if (_goto >= 0) {
                if (_goto <= pc && ++back > CobConfig.LOOP_LIMIT)
                    CobRt.loopTick("GO TO loop in " + programId() + " (paragraph " + _paragraphNames()[_goto] + ")", back);
                pc = _goto;
                continue;
            }
            if (pc == hi) return;
            pc++;
            if (pc >= n) return;
        }
    }

    /** LINKAGE records seeded or read without a CALL (characterization): storage of their own, as WORKING-STORAGE has. */
    private void materializeLinkage() {
        Area[] lk = linkage != null ? linkage : (linkage = _linkage());
        for (Area a : lk) a.materialize(CobConfig.DEFAULT_BYTE);
    }

    public final void ensureInit() {
        if (!_initialized) {
            _init();
            _initialized = true;
        }
    }

    /** Enter the program (main or CALLed). Linkage areas are bound to the arguments BY REFERENCE. */
    public final void call(CobField[] args, boolean called) {
        if (_initial() && _initialized) _init();
        ensureInit();
        Area[] lk = linkage != null ? linkage : (linkage = _linkage());
        for (int i = 0; i < Math.min(args.length, lk.length); i++) {
            if (args[i] != null) lk[i].bind(args[i].buf(), args[i].pos());
        }
        for (Area a : lk) a.materialize(CobConfig.DEFAULT_BYTE); // a USING record no argument was bound to
        // LOCAL-STORAGE: fresh storage per invocation (after the arguments are bound, so an argument in the caller's
        // LOCAL-STORAGE keeps pointing at the caller's bytes), the caller's restored on return.
        Area[] local = _localAreas();
        byte[][] saved = local.length == 0 ? NO_SAVED : new byte[local.length][];
        for (int i = 0; i < local.length; i++) {
            saved[i] = local[i].b;
            local[i].b = new byte[local[i].size];
        }
        if (local.length > 0) _initLocal();
        boolean prev = _called;
        _called = called;
        try {
            perform(_start(), _count() - 1);
        } catch (CobRt.ProgramExit e) {
            // GOBACK / EXIT PROGRAM
        } finally {
            _called = prev;
            for (int i = 0; i < local.length; i++) local[i].b = saved[i];
            if (_initial()) for (CobFile f : _files()) if (f.isOpen()) f.close();
        }
    }

    /** EXIT PROGRAM in a main program has no effect. */
    protected final void exitProgram() {
        if (_called) throw CobRt.PROGRAM_EXIT;
    }

    @Override
    public ProgramState run(ProgramState state) {
        ensureInit();
        materializeLinkage();
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
