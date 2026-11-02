package com.mlcpu.rt;

/**
 * Where a MOVE (or an implicit one: READ INTO, WRITE FROM, ...) sits in the COBOL program, as the transpiler passes it to
 * {@link CobRt#move(Object, CobField, String)}: one string constant per receiver, parsed only when a frontier is reported.
 * {@code stepId} is the IR statement id, the key of the frontier ledger.
 */
public record MoveSite(String program, String paragraph, String stepId, int line, int receiver) {
    private static final char SEP = '|';

    public static String encode(String program, String paragraph, String stepId, int line, int receiver) {
        return clean(program) + SEP + clean(paragraph) + SEP + clean(stepId) + SEP + line + SEP + receiver;
    }

    /** The site, or null for a string that is not an encoded site. */
    public static MoveSite parse(String s) {
        if (s == null) return null;
        String[] p = s.split("\\|", -1);
        if (p.length != 5 || p[2].isEmpty()) return null;
        try {
            return new MoveSite(p[0], p[1], p[2], Integer.parseInt(p[3]), Integer.parseInt(p[4]));
        } catch (NumberFormatException e) {
            return null;
        }
    }

    public String describe() {
        return "program " + program + ", paragraph " + paragraph + ", statement " + stepId + ", line " + line + ", receiver #" + (receiver + 1);
    }

    private static String clean(String s) {
        return s == null ? "" : s.replace(SEP, '/');
    }
}
