package com.mlcpu.jobol;

import java.util.EnumSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * The JOBOL rule catalog. Each rule names a COBOL construct that survived translation, why it is not modern Java,
 * and the modern replacement the generator (or the model driving it) should emit instead.
 */
public final class Rules {
    private Rules() {
    }

    /** A rule: default severity, the zones it applies to, the per-type cap on scored occurrences, rationale and fix. */
    public record Spec(String id, String name, Severity severity, Set<Zone> zones, int cap, String summary, String fix) {
    }

    private static final Set<Zone> M = EnumSet.of(Zone.MODERN);
    private static final Set<Zone> MC = EnumSet.of(Zone.MODERN, Zone.COMPAT);

    public static final List<Spec> ALL = List.of(
        new Spec("JOB001", "legacy-runtime-coupling", Severity.BLOCKER, M, 3,
            "Modern code depends on the COBOL emulation runtime (CobolBase, CobField, Area, CobRt, com.mlcpu.rt).",
            "Keep the runtime behind the strangler/compat adapter. Modern classes take typed records or domain objects and never see Area/CobField storage."),
        new Spec("JOB002", "perform-dispatch", Severity.BLOCKER, M, 3,
            "Control flow is a PERFORM/GO TO emulation: perform(n, m) calls or an integer switch that dispatches paragraphs.",
            "Call named methods directly; turn PERFORM UNTIL into a loop over an iterator/stream and PERFORM THRU ranges into one method."),
        new Spec("JOB003", "paragraph-names", Severity.MAJOR, M, 5,
            "Method names carry paragraph numbers or labels (p0_MAINLINE, para1000, x2100ReadFile).",
            "Name methods for the business action they perform (readNextTransaction, postInterest). Keep the paragraph id in Javadoc for traceability."),
        new Spec("JOB010", "cobol-prefix-names", Severity.MAJOR, M, 5,
            "Identifiers keep COBOL storage-section prefixes (ws, dw, ls, lk, fd, wk, sw, ds...).",
            "Drop the storage prefix: Java scope (field, local, parameter) already says where a value lives. Name the business meaning."),
        new Spec("JOB011", "cobol-case-names", Severity.MAJOR, M, 5,
            "Identifiers use COBOL hyphen-style casing (ACCT_ID, acct_id, _se8) instead of lowerCamelCase.",
            "Use lowerCamelCase for fields, locals and methods; UPPER_SNAKE only for static final constants."),
        new Spec("JOB012", "numbered-series", Severity.MAJOR, M, 5,
            "Numbered names (fields dwNum1..dwNum23, a24/a31, accountData_2; locals flag, flag2, flag3) mirror COBOL storage or translator temporaries.",
            "Replace the series with a collection, an array-backed value type, or distinct well-named fields. Disambiguate _2 duplicates by meaning."),
        new Spec("JOB013", "filler-fields", Severity.MINOR, M, 3,
            "FILLER areas survive as fields in modern code.",
            "Filler belongs to the record codec in the compat lane; the domain model has no filler."),
        new Spec("JOB020", "working-storage-class", Severity.MAJOR, M, 1,
            "The class holds WORKING-STORAGE: many mutable fields written by several methods as shared scratch state.",
            "Pass values as parameters and return results; hold per-run state in a small immutable context or a local variable; split the class by responsibility."),
        new Spec("JOB021", "paragraph-methods", Severity.MAJOR, M, 1,
            "Most methods are paragraph-shaped: void, no parameters, communicating only by mutating fields.",
            "Give methods inputs and return values (or records), so data flow is visible in signatures and the methods are testable in isolation."),
        new Spec("JOB022", "mutable-static-state", Severity.MAJOR, MC, 5,
            "Mutable static fields act as global WORKING-STORAGE shared across run units.",
            "Make static fields final and immutable, or move the state into an instance owned by the caller/run context."),
        new Spec("JOB030", "yn-string-flags", Severity.MAJOR, M, 5,
            "Conditions use 'Y'/'N' string switches (COBOL 88-level flags) instead of boolean or enum.",
            "Use boolean for two-valued switches and an enum for 88-level condition-name sets; convert at the record boundary."),
        new Spec("JOB031", "file-status-codes", Severity.MAJOR, M, 5,
            "Logic compares COBOL FILE STATUS strings ('00', '10', '23', '35') in modern code.",
            "Let the dataset adapter translate status codes into return values (Optional, end-of-stream) and exceptions; modern code never sees '00'."),
        new Spec("JOB033", "string-code-sets", Severity.MAJOR, M, 3,
            "Short string codes ('H'/'P'/'E', 'S'/'R', 'UP'/'DOWN'/...) are switched on or compared in chains: 88-level condition names kept as text.",
            "Decode the code once at the record boundary into an enum (or a JDK enum such as RoundingMode.valueOf) and switch on the enum."),
        new Spec("JOB032", "status-flag-fields", Severity.MINOR, M, 1,
            "Several fields hold dataset status strings or end-of-file switches (dsX, eof, firstTime) that steer control flow.",
            "Iterate input with an Iterator/Stream/reader that signals the end itself; return results instead of setting status fields."),
        new Spec("JOB040", "picture-literals", Severity.MAJOR, M, 5,
            "PICTURE clauses or edit masks (ZZZ,ZZ9.99, 9(5), X(10), S9(7)V99) drive formatting or validation in modern code.",
            "Use typed values (BigDecimal with scale, LocalDate) and java.text/java.time formatters; fixed-width editing belongs to a report/record codec."),
        new Spec("JOB041", "floating-point-money", Severity.MAJOR, MC, 5,
            "Monetary values (amount, balance, rate, interest, fee...) are double/float.",
            "Use BigDecimal (or a Money value type) with an explicit scale and RoundingMode matching the COBOL ROUNDED/truncation rule."),
        new Spec("JOB042", "positional-strings", Severity.MAJOR, M, 3,
            "Records are read or built by byte/character position in modern code (substring(12, 20), raw(13, 1), raw(244 + k * 240, 240), %-20s padding).",
            "Decode fixed-width records once in a compat codec into a Java record; the domain uses the typed fields."),
        new Spec("JOB043", "string-dates", Severity.MINOR, M, 5,
            "Dates and timestamps are carried as String (PIC X(10)/9(8) residue) instead of java.time types.",
            "Decode once at the record boundary into LocalDate/LocalDateTime/Instant; keep the text form only in the codec."),
        new Spec("JOB050", "priming-read", Severity.MINOR, M, 3,
            "COBOL priming-read loop: read(); while (!eof) { ...; read(); }.",
            "Iterate a reader/stream: for (var rec : input) { ... } or input.lines().forEach(...)."),
        new Spec("JOB051", "trailing-return", Severity.MINOR, M, 3,
            "A void method ends with an explicit 'return;' (EXIT / GOBACK residue).",
            "Remove the redundant return."),
        new Spec("JOB052", "labeled-jumps", Severity.MAJOR, MC, 5,
            "Labeled statements and labeled break/continue emulate GO TO.",
            "Restructure into methods with early return, or loops with plain conditions."),
        new Spec("JOB053", "abend-exit", Severity.MINOR, M, 3,
            "System.exit outside main ends the JVM like an ABEND/STOP RUN.",
            "Throw a domain exception and let the entry point map it to an exit code."),
        new Spec("JOB054", "long-method", Severity.MINOR, M, 5,
            "The method is long (over 60 lines, or over 150 for MAJOR), as a translated paragraph or section usually is.",
            "Extract cohesive steps into named methods; separate validation, calculation and output."),
        new Spec("JOB055", "display-io", Severity.MINOR, M, 3,
            "Business code writes to System.out/err (DISPLAY) instead of a logger or a report writer.",
            "Use a logger for diagnostics and a report/output port for business output."),
        new Spec("JOB056", "god-class", Severity.MAJOR, M, 1,
            "One class holds a whole program: over 800 lines with shared mutable state or paragraph methods (or over 2500 lines).",
            "Split by capability: input adapter, validation, calculation, output; keep each class small and focused."),
        new Spec("JOB057", "statement-translation-comments", Severity.MINOR, M, 3,
            "Comments replay COBOL statements line by line (MOVE X TO Y, PERFORM P, [PARA:1:MOVE:L181]).",
            "Translate intent, not statements: one Javadoc citing the source paragraph is enough."),
        new Spec("JOB058", "repeated-size-guards", Severity.MAJOR, M, 1,
            "PIC 9(n) ON SIZE ERROR emulation (if (!fits(x + 1, 9)) overflow()) is repeated at three or more call sites.",
            "Put the capacity rule in the value type itself (a counter record whose increment throws at its limit), so call sites just count.")
    );

    public static final Map<String, Spec> BY_ID;

    static {
        Map<String, Spec> m = new LinkedHashMap<>();
        for (Spec s : ALL) m.put(s.id(), s);
        BY_ID = m;
    }
}
