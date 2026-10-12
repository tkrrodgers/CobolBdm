# Frontier burn-down (plan section 7, JavaToCOBOLToJava.md)

Branch `frontier-burndown` (worktree `CobolBdm-burndown`), not committed: the owner reviews.

**Rules followed.** The oracle is GnuCOBOL 3.3 (`cobc -std=ibm -fsign=EBCDIC`). Each construct got its conformance case
first (a `parity/` program whose DISPLAY output GnuCOBOL produces, or a `lower/` case for what must stay a frontier),
then the change. Where GnuCOBOL's behaviour was not obvious, its generated C (`cobc -C`) or preprocessed text (`cobc -E`)
was read and the rule ported as generated. Faithful or abstain: what cannot be done exactly is a frontier, never a
guess. Where GnuCOBOL itself is defective (it writes past the item), the transpiler abstains and the defect is recorded.

**Gate.** `dotnet run --project MlCpu\src\CobolConformance` against this worktree's sidecar on :8089
(`MLCPU_JAVA_SIDECAR=http://127.0.0.1:8089`). Baseline before any change: 132 pass, 0 fail.

**IR regression check.** `work/irdump` (this worktree's analyser) and `work/irdump-old` (the analyser at HEAD) dump
the IR of all 67 programs of CardDemo, the IBM sample and Fineract Tier 1; `work/irdiff.sh` lists programs whose IR
changed and `work/irdiff.py` shows how. Every difference below is accounted for.

## Changes

### P1 (a) OCCURS DEPENDING ON: silent -> lowered (standard case), frontier otherwise

Before: the table was laid out at its maximum and every reference used the maximum length (silent). A qualified
object (`DEPENDING ON VSTRING-LENGTH OF WS-DATE-TO-TEST`, CardDemo CSUTLDTC) also produced "unrecognised data clause"
diagnostics.

GnuCOBOL's rules (read from its generated C: every reference to a variable group is `COB_SET_FLD(f, fixed +
entry * cob_get_int(object), ...)` at the point of reference):

- A group holding an ODO table is as long as its fixed part plus the current occurrences, wherever it is referenced:
  MOVE from or to it, comparison, DISPLAY, `LENGTH OF`, `FUNCTION LENGTH`, reference modification, WRITE.
- A receiver that holds its own object uses the object's value *before* the MOVE (GnuCOBOL differs from IBM, which
  uses the maximum).
- `WRITE rec FROM x` moves into the record at the count before the move, then writes the record at the count after it.
- Subscripts past the count are not checked; VALUE initializes the maximum.
- An FD with an ODO record is variable-length: its minimum record counts the ODO table at its minimum occurrences.
- INITIALIZE (GnuCOBOL 3.x `output_initialize`): a target whose members all initialize to one byte is filled at its
  current length; otherwise member by member: a uniform table is filled at its *maximum*, another table has its first
  occurrence initialized and copied over occurrences 2..n with n read at that moment (after earlier members, such as
  the object itself, were initialized); FILLER and REDEFINES are skipped.

Implementation: `DataDivision.cs` resolves the object (with OF/IN qualifiers) and the FD minimum; IR fields carry
`dependingOnId` and `occursMin`. `SkeletonEmitter.planOdo` marks every group holding one ODO table; a reference to it is
`CobRt.odo(group, object, entry, max)`, a view at the current length (`CobField.withLength`). INITIALIZE of such a group
follows the rules above (`initializeOdo`, `CobRt.initTable`, `CobRt.fillBytes`).

Abstained (frontier at the statement): an ODO table inside another table or holding another ODO (complex ODO), a
group holding two ODO tables, items after the table (their position slides with the count, `odoslide`), an ODO object
in a table, a count outside 0..max at run time (`RuntimeCheck`), and two GnuCOBOL defects:

- `(start:)` of an ODO group: GnuCOBOL gives it the whole current length from the start position, past the group;
- INITIALIZE of an elementary, non-uniform ODO table (COMP-3, edited, SIGN SEPARATE): GnuCOBOL initializes the first
  occurrence with the whole table's length and copies that length per occurrence, past the table.

Cases: `parity/PARITY05.cbl` (lengths, receiver with its own object, subscripts past the count, VALUE, five
INITIALIZE shapes, variable sequential and line sequential files written and read back, WRITE FROM), `lower/LOWER06.cbl`
(the four abstentions), `manifest.tsv` (PARITY05 parses without diagnostics).

IR check: the 22 programs with ODO changed as expected: CSUTLDTC loses its 4 diagnostics; the 21 CICS programs'
`DEPENDING ON EIBCALEN` is now reported as an unresolved ODO object (EIBCALEN is a CICS register, out of scope).

### P1 (b) EJECT / SKIP1 / SKIP2 / SKIP3 / TITLE: silent -> lowered

Before: a listing directive between data entries joined the next entry's sentence, which was then dropped (it did not
start with a level number); in the procedure division it became an unrecognised statement.

Now `CobolParser.Strip` blanks a line that holds only a listing directive (optional period), in fixed and free format,
for programs and copybooks. Case: `parity/PARITY07.cbl` (each directive between data entries and between statements).

### P1 (c) Group-level USAGE and SIGN: silent -> lowered

Before: `01 COMP-VARIABLES COMP.` left its `PIC S9(4)` members DISPLAY (CardDemo CBSTM03A has two such groups:
`COMP-VARIABLES COMP` and `COMP3-VARIABLES COMP-3`).

Now a USAGE or SIGN clause on a group applies to every subordinate item without its own (`InheritGroupUsageAndSign`;
SIGN only to signed numerics); a group stays a group whatever USAGE it states. Case: `parity/PARITY06.cbl` (COMP-3,
COMP, COMP-5 and SIGN LEADING SEPARATE groups: lengths, stored bytes, arithmetic, an item's own clause winning).

IR check: CBSTM03A's 5 counters change from DISPLAY to COMP / COMP-3 (offsets and lengths).

### P1 (d) REPLACE, and COPY ... REPLACING by text words: silent -> lowered

Before: REPLACE was not implemented (silent), and COPY REPLACING replaced *substrings*, case-insensitively, in every
line of the member: `==P== BY ==R==` turned `PIC` into `RIC`, `==WS-OLD== BY ==WS-NEW==` also renamed `WS-OLD-X`,
and pseudo-text inside literals was replaced.

GnuCOBOL's rules (read from `cobc -E`): matching is by text words, case-insensitive (literals included: `'lit'`
matches `=='LIT'==`); `(`, `)` and `:` are words of their own, so `==:TAG:==` and `==(TAG)==` replace part of a word
and `==X==` matches the X of `PIC X(4)`; a comma or semicolon before a space is a space; `12,34` is one word; a period
before a space is a word. Multi-word pseudo-text matches across lines and spacing. A REPLACE replaces the previous set;
REPLACE OFF ends it; REPLACE applies after COPY expansion. LEADING / TRAILING replace the start or end of one word.

Implementation: `MlCpu.CobolKb/TextReplacing.cs` (text words, operand parsing for pseudo-text, literals and words,
LEADING/TRAILING, edits that keep line numbers), used by `CobolParser.Expand` for COPY REPLACING and by
`ExpandCopies` for REPLACE statements (a malformed REPLACE is a diagnostic). Case: `parity/PARITY07.cbl` with
`PARITY07.cpy` (the parity lane now passes `-I <program dir>` to cobc so a case can COPY).

IR check: the estates' REPLACING is all `==:TAG:==` / `==(TAG)==` style, for which words and substrings agree; no
program's IR changed from this.

### P2 SEARCH and SEARCH ALL: frontier -> lowered

Before: SEARCH was abstained whole (D1).

GnuCOBOL's rules (generated C):

- Serial SEARCH: the search index is the VARYING item when it is one of the table's indexes, else the table's first
  INDEXED BY index; the size is the ODO value (read once at the start) or the maximum. Each round: index past the size
  -> AT END and leave; else the WHENs in order, the first true one runs and leaves; else the index steps by one and a
  VARYING item that is not an own index *takes the index's value* (GnuCOBOL; the standard says it is incremented).
  The index is not checked below 1.
- SEARCH ALL: `head = 0`, `tail = size + 1`; while `head < tail - 1` the index probes `(head + tail) / 2`; the WHEN's
  `KEY = value` terms are compared in the table's KEY order (not the order written), a DESCENDING key's comparison
  negated, `&&` short-circuiting so the first unequal key decides: below -> `head = index`, else `tail = index`. A key
  may be on either side of `=`, or be named by its single-valued condition-name. AT END leaves the index at the last
  probe.

Implementation: `ProcedureParser.ParseSearch` (the semantic tree carries the table, the resolved search index and
VARYING, AT-END and WHEN blocks; `ProgramGraphBuilder` models the loop and its def/use); `SkeletonEmitter.lowerSearch`
and `lowerSearchAll` (a labelled block, `CobRt.loopTick` per round). Abstained: SEARCH ALL of a table without KEY, a
relation other than `=`, a term on a non-key item.

Also fixed: DISPLAY of a USAGE INDEX item in the GnuCOBOL dialect shows a signed 9-digit number (`+000000002`); it
showed 10 digits.

Cases: `parity/PARITY08.cbl` (16 searches: start index, AT END at once and after the last row, VARYING a data item and
an own index, NEXT SENTENCE, ODO size, a table inside a table, two keys with a DESCENDING one, reversed operands, a
condition-name key, duplicates, not found), `lower/LOWER07.cbl` (the three abstentions), `lower/LOWER01.cbl` now
expects SEARCH lowered.

### P2 INITIALIZE ... REPLACING / WITH FILLER / ALL TO VALUE / THEN TO DEFAULT: frontier -> lowered

Before: any INITIALIZE option was a frontier.

Rules (standard, checked on GnuCOBOL): each elementary item (FILLER only WITH FILLER; nothing under a REDEFINES; not
INDEX or POINTER items) is set to its VALUE when ALL TO VALUE applies and it has one, else to the REPLACING value of its
category (ALPHABETIC, ALPHANUMERIC, ALPHANUMERIC-EDITED, NUMERIC, NUMERIC-EDITED), by MOVE, else to its default when
THEN TO DEFAULT is given or neither TO VALUE nor REPLACING is; otherwise it is left alone. A VALUE applies to every
occurrence of a table.

Implementation: `ProcedureParser.ParseInitialize` (options into the semantic tree; the category words are reserved so
they are never taken for data names), `SkeletonEmitter.lowerInitializeWith`.

Abstained, because GnuCOBOL departs from the standard there (recorded as oracle defects): `category TO VALUE` other
than ALL (GnuCOBOL applies every VALUE whatever its category), a group VALUE under TO VALUE, and a table of groups
whose entry the statement writes only in part (GnuCOBOL initializes occurrence 1 and copies its *bytes*, unwritten
ones included, over the other occurrences; seen with `REPLACING NUMERIC` on entries that also hold alphanumerics).

Found on the way and fixed (silent): GnuCOBOL's default INITIALIZE of a signed zoned item (sign not separate) writes
plain `0` digits, not the overpunched `+0` a MOVE ZERO writes (`00`, not `0{`); IBM moves zero. New runtime switch
`CobConfig.GNU_INITIALIZE` (`mlcpu.initialize` / `MLCPU_INITIALIZE` = `gnucobol` | `zos`), defaulting to the GnuCOBOL
dialect whenever `MLCPU_DISPLAY_NUMERIC=gnucobol` (the setting every oracle comparison already uses), so no host change
is needed. `REPLACING NUMERIC BY ZERO` is a MOVE and keeps the sign (`0{`), as GnuCOBOL does.

Cases: `parity/PARITY09.cbl` (12 forms incl. an elementary target, a table written whole, WITH FILLER, SIGN LEADING
SEPARATE default, plain INITIALIZE of signed zoned items), `lower/LOWER08.cbl`.

### P2 SET condition-name TO FALSE: frontier -> lowered

Before: a frontier; and an 88's `WHEN SET TO FALSE IS 'N'` / `FALSE 'N'` phrase was parsed as more VALUEs (silent: the
condition also matched the words WHEN, SET, TO ...).

Now the phrase is the 88's `FalseValue` (IR `falseValue`), and SET ... TO FALSE moves it (figuratives included) to the
conditional variable, subscripted when the 88 is. Without such a phrase it stays a frontier (GnuCOBOL rejects it).
Case: `parity/PARITY10.cbl`; `lower/LOWER08.cbl` (no FALSE value).

### P2 PERFORM VARYING ... AFTER ...: frontier -> lowered

Rules (GnuCOBOL's generated C): the first variable takes its FROM; each level is a loop that, at the top of every
round, sets the next level's variable to its FROM (before its own test, so a FROM may use an outer variable); WITH TEST
BEFORE a level tests its UNTIL first, WITH TEST AFTER after the inner level (or body); then its variable steps by BY.
Implementation: `ParsePerform` (an `after` node per phrase), `SkeletonEmitter.lowerVaryingAfter` (inline and out of
line, any depth, a loop tick per level). EXIT PERFORM [CYCLE] inside such a PERFORM stays a frontier. Case:
`parity/PARITY11.cbl` (both tests, three levels with a negative BY, FROM from the outer variable, an inner UNTIL true at
once, index variables, values after the loop), `lower/LOWER09.cbl`.

### P2 Intrinsic functions: frontier -> lowered

New: TEST-NUMVAL, TEST-NUMVAL-C, ORD, CHAR, ORD-MAX, ORD-MIN, SUM, MEAN, MEDIAN, RANGE, SQRT, FACTORIAL, INTEGER-OF-DAY,
DAY-OF-INTEGER, TEST-DATE-YYYYMMDD, YEAR-TO-YYYY, DATE-TO-YYYYMMDD (with optional window and year), WHEN-COMPILED, and
NUMVAL-C with a currency argument (`CobFn`).

- NUMVAL / NUMVAL-C now follow GnuCOBOL on any argument (silent before for invalid ones): digits accumulate, the first
  sign wins, CR/DB (any case) ends the scan negative, the first point starts the fraction, everything else is skipped
  (`12A3` = 123, `1.2.3` = 1.23, `+-1` = 1).
- TEST-NUMVAL[-C]: 0 or the position of the first character that cannot be accepted (length + 1 when the argument
  ends early); NUMVAL-C takes the currency sign (also before the sign) and commas right after a digit; CR/DB upper
  case only; a C or D not followed by its R or B is reported one position later, as GnuCOBOL does. Calibrated on 50
  arguments against GnuCOBOL.
- MEAN keeps the division's 38 extra digits (as COMPUTE); SQRT is computed to 100 digits, truncated; a negative SQRT
  or FACTORIAL argument stops the run; date functions return 0 outside their domain, as GnuCOBOL does.
- WHEN-COMPILED is pinned: `MLCPU_WHEN_COMPILED` / `mlcpu.whenCompiled` when set, else the transpile time written into
  the class. (cobc honours `SOURCE_DATE_EPOCH`, but GnuCOBOL 3.3 renders it with the month and day one too low.)

Abstained: DISPLAY of a numeric function result and MOVE of one to an alphanumeric receiver (GnuCOBOL shows its own
temporary's digits and sign, e.g. `0000000066` for ORD, `65` for SUM 6.5), TEST-NUMVAL-C with a currency argument
(GnuCOBOL 3.3 returns 2 for `('EUR1.5' 'EUR')`), statistical functions over alphanumeric arguments.

Cases: `parity/PARITY12.cbl` (each function stored into a field; NUMVAL family over 50 arguments), `lower/LOWER09.cbl`.

IR/transpile check (`work/xpdiff.py`, baseline pipeline vs this one over the 67 estate programs): CardDemo COACTUPC
and COTRTUPC lose their 9 TEST-NUMVAL[-C] frontiers; no statement became a frontier.

**Oracle defect found (not reproduced):** GnuCOBOL 3.3 with `-fsign=EBCDIC` evaluates `IF X < X` as true when X is a
negative signed zoned item (also `TV (2) < TV (2)`, and so ORD-MIN over the same element twice): decoding the first
operand's overpunched sign appears to un-punch the shared byte before the second is read. The Java answers false.

### P2 ALTER: frontier -> lowered

Rules: ALTER p TO [PROCEED TO] q changes the target of the GO TO that is paragraph p's only sentence (the standard's
rule); the change persists (also across CALLs of a subprogram, as COBOL storage does); a `GO TO.` without a target
takes its target from ALTER. Implementation: `ProcedureParser.ParseAlter` (pairs), `GO TO.` parsed as ALTERable;
`SkeletonEmitter.planAlter` gives each altered paragraph an instance field (`_alterN`, initial: its own target's index
or -1); the GO TO jumps through it (inside PERFORM ... THRU ranges the driver's `perform(lo, hi)` handles the return as
for any GO TO); ALTER assigns it. A `GO TO.` reached before any ALTER stops the run (GnuCOBOL falls through; the
standard leaves it undefined). An altered paragraph that is not a lone GO TO keeps every ALTER of the program a
frontier. Cases: `parity/PARITY13.cbl` (PERFORM THRU ranges, two pairs in one ALTER, `GO TO.`, the first-time switch,
a SECTION as target), `lower/LOWER03.cbl` (now expects ALTER lowered), `lower/LOWER10.cbl` (the abstention).

**Checkpoint.** Gate: 178 pass, 0 fail, 0 blocked. `gradle test` passes.

### P3 DECLARATIVES with USE AFTER ERROR: silent -> lowered

Before: the DECLARATIVES were skipped wholesale (a diagnostic only); a USE procedure never ran (silent).

GnuCOBOL's routing (generated C): after each I/O statement with an exception (status 10 or more), the statement's own
phrase takes its condition (AT END: status 10 only; INVALID KEY: 21-24); otherwise the file's USE procedure runs
(chosen statically), else the default handler picks the USE procedure for the file's *last OPEN mode* (also when that
OPEN failed), else stops the run when the file has no FILE STATUS. The USE section is performed and control continues
after the statement. The program starts after END DECLARATIVES.

Implementation: `ProcedureParser` keeps the declarative sections (flag `Declarative`, `UseFiles`, `UseModes`; other
USE forms are diagnostics); `ProgramGraphBuilder` starts at the first non-declarative paragraph and reaches the USE
sections from the entry; `SkeletonEmitter.ioStatus` emits the routing for OPEN, CLOSE, READ, WRITE, REWRITE, DELETE
and START; `CobolBase._start()`; `CobFile.lastOpenMode()` and `unhandled()`.

Found on the way and fixed (silent, every program): a READ past the end (status 46) ran the AT END phrase and was
treated as handled; GnuCOBOL runs neither AT END nor NOT AT END for it and stops when the file has no FILE STATUS.
Likewise any phrase made every status "handled"; now only the phrase's own condition is.

Case: `parity/PARITY14.cbl` (file and mode USE procedures, a two-paragraph USE section, a failed OPEN and the READ after
it, AT END vs 46, INVALID KEY phrase vs USE on an indexed file, a file without FILE STATUS beside them).

### P3 COMP-5 byte order: partial -> lowered

Before: COMP-5 was stored big-endian; GnuCOBOL on x86-64 stores it in native (little-endian) order (COMP follows
`binary-byteorder: big-endian`). Values agreed; the bytes did not (REDEFINES, group moves, records written).

Now `CobField` reads and writes COMP-5 little-endian when `CobConfig.COMP5_LITTLE_ENDIAN` (`mlcpu.comp5` /
`MLCPU_COMP5` = `native` | `big`; native by default in the GnuCOBOL dialect, big-endian for z/OS). Case:
`parity/PARITY15.cbl` (bytes of S9(4), S9(9) and 9(4) COMP-5 beside COMP, full capacity, a value set through its
bytes, a group move to a COMP item, wrap-around). Not changed: the record kit's `Pic.java` (Knowledge) still encodes
COMP-5 big-endian; it matters only for COMP-5 fields in I/O records (CardDemo has one, in an IMS copybook).

### P3 Several programs in one source, nested programs: silent -> lowered

Before: only the first PROGRAM-ID of a source was analysed, and the text of the programs after it (a nested one, or a
separate one after END PROGRAM) was parsed as more statements of the first.

Now `CobolModelBuilder.ProgramUnits` splits the COPY-expanded source into programs (each from its IDENTIFICATION
DIVISION or PROGRAM-ID to the next program's start or its END PROGRAM, nesting tracked); `BuildGroundedFull` returns the
first one *clipped to its own text*, `BuildAllGroundedFull` all of them. The conformance parity lane and
`JavaBridge --file` transpile every program of the source and run the first. GLOBAL items of a containing program are
not shared with nested programs (a reference to one does not resolve: a frontier).

### P3 EXTERNAL, CALL ... RETURNING, IS INITIAL, RECURSIVE / LOCAL-STORAGE: silent or frontier -> lowered

- EXTERNAL (silent before: each program had its own copy): an EXTERNAL record's area comes from the run unit's
  registry by name (`CobRt.externalArea`), zeroed once when first allocated (as GnuCOBOL's `cob_external_addr`) and
  never re-filled by a program's initialization.
- CALL ... RETURNING x (frontier before): x receives the called program's return value, its RETURN-CODE; the caller's
  RETURN-CODE is left as it was (a plain CALL sets it), as GnuCOBOL does (`CobRt.callReturning`). A program's own
  PROCEDURE DIVISION RETURNING stays a frontier: GnuCOBOL 3.3 does not implement it ("program RETURNING is not
  implemented") and crashes on it (`lower/LOWER11.cbl`).
- PROGRAM-ID ... IS INITIAL (silent before): each CALL starts from the initial state and the files the program left
  open are closed on return (`CobolBase._initial`, `_files`).
- LOCAL-STORAGE (partial before: initialized once like WORKING-STORAGE): every invocation gets fresh storage,
  initialized with its VALUE clauses, and the caller's is restored on return, so a RECURSIVE program's levels keep
  their own (`_localAreas`, `_initLocal`; arguments are bound to the caller's bytes before the swap).

Found on the way and fixed (silent, GnuCOBOL dialect): DISPLAY of a zoned numeric item shows its bytes as stored with
NUL and space shown as `0` (never-set storage under default byte 0 reads `0000`, not NULs), a signed item's sign
position decoded with the sign after it, letters left as they are; RETURN-CODE and TALLY show their PICTURE's digits
(`+000000000`, `00000`), not their byte size's.

Cases: `parity/PARITY16.cbl` (a RECURSIVE program CALLing itself, LOCAL-STORAGE per level), `parity/PARITY17.cbl`
(EXTERNAL between two programs, CALL RETURNING vs a plain CALL, IS INITIAL called twice), `parity/PARITY18.cbl`,
`lower/LOWER11.cbl`.

**Checkpoint.** Gate: 195 pass, 0 fail, 0 blocked. `gradle test` passes. Estates (`work/xpdiff.py`): lowered
statements 10,862 -> 10,922, abstained 1,038 -> 1,019, every program still compiles.

### P3 SORT, MERGE, RELEASE, RETURN: frontier -> lowered

GnuCOBOL's behaviour (checked): SORT reads the USING files in turn (opening and closing them) or takes RELEASEd
records from the INPUT PROCEDURE, sorts *stably* on the KEYs (a numeric key by value, so an overpunched -1 sorts below
0; DESCENDING reversed), and writes every GIVING file or serves RETURN in the OUTPUT PROCEDURE (AT END after the last);
SORT-RETURN is then 0 (shown with 9 digits). MERGE does *not* merge: it is the same stable sort of its USING files'
records taken in file order (unsorted inputs give a sorted result, equal keys keep file order). SORT of a table
(COBOL 2002) reorders the occurrences; GnuCOBOL uses the C library's qsort, so the order of equal keys is unspecified.

Implementation: `ProcedureParser.ParseSort` / `ParseReturn` (RELEASE inline); `ProgramGraphBuilder` treats the
procedures as performed ranges; runtime `CobSort` (records as bytes, keys compared with `CobRt.compare` over views of
each record, `List.sort` stable), `CobField.over`, `CobFile.lastRecordLength`, `CobRt.SORT_RETURN`;
`SkeletonEmitter.lowerSort` (an instance field per SD holds the running SORT for RELEASE / RETURN). Abstained: a
COLLATING SEQUENCE, MERGE with an INPUT PROCEDURE, a table inside a table; RELEASE / RETURN outside a SORT and a table
SORT meeting equal keys stop the run.

Cases: `parity/PARITY19.cbl` (SORT USING two files GIVING one with an ascending alphanumeric and a descending signed
key, MERGE of unsorted files, INPUT / OUTPUT PROCEDURE with RELEASE FROM and RETURN, SORT-RETURN, table SORT up and
down), `lower/LOWER12.cbl`.

**Checkpoint.** Gate: 203 pass, 0 fail, 0 blocked.

### P4 Runtime speed (architect's request): the decimal kernel

Benchmark: `work/perf4/BENCH02.cbl` (Fineract-like: S9(13)V9(18) COMP-3 fields, COMPUTE ROUNDED with a division, ADD,
MOVE, compare and a CALL per round, 3,000,000 rounds); `work/perf4/ab.sh` runs GnuCOBOL, the baseline pipeline (HEAD
emitter + the runtime as it was before P4, `work/perf4/rt-before`) and this worktree's, interleaved, because the
machine was ~97% busy with other work and single timings were noisy. Profiles from `work/perf4/sampler/Sampler.java`
(a stack sampler; JFR recorded no execution samples on this machine).

| | before | after | GnuCOBOL |
| --- | --- | --- | --- |
| BENCH02, best of 3 interleaved rounds (JVM start included) | 9.97 s | 4.13 s | 2.02 s |

About 2.4x faster; the gap to GnuCOBOL went from ~5x to ~2x on this program. Changes, each exact:

1. `CobField.encodePacked` / `encodeDisplay` for more than 18 digits: the magnitude is split into two longs of 18
   digits (`split18`: a long when it fits, else an exact 128-bit division by 10^18 with a double estimate and exact
   remainder correction, ~2.5x faster than BigInteger division; above 10^36 the old path) and written two digits per
   byte from a table (`PACK`), instead of `BigInteger.toString` + StringBuilder. A small value into a wide field skips
   BigInteger entirely.
2. `CobField.decPacked` up to 18 bytes: two longs from the right through byte tables (`PAIR`, `HIGH`, `LOW`; a nibble
   above 9 reads as 0, as before), `BigDecimal.valueOf(long, scale)` when the high word is 0, else one 128-bit product
   into a BigInteger (`join18`) instead of BigInteger multiply/add.
3. Division at the precision the store needs: a COMPUTE whose expression is a division, DIVIDE ... INTO and DIVIDE ...
   GIVING into numeric receivers emit `CobRt.divTo(a, b, receiverScale + 1)` instead of `CobRt.div` (38 extra
   digits) followed by `setScale`: truncating or rounding half up at the receiver's scale gives the same digits (proof
   in `divTo`'s comment; a non-numeric receiver keeps the full quotient, whose digits it would show).
4. CALL: the target is resolved once per name as written (`ProgramRegistry.target`, a lock-free cache cleared on
   register / reset / loader change) instead of trim + upper-case + two synchronized lookups per CALL; a program's
   linkage areas are computed once.

Tests: `DecimalFastPathPropertyTest` (new: `divTo` vs `div` over 100,000 random divisions at random scales with both
rounding modes; `split18` / `join18` against BigInteger over 100,000 values up to 120 bits; the packed decoder against
a digit-by-digit reference over 100,000 random byte strings of 1 to 20 bytes, invalid nibbles included) and the
existing `NumericCodecPropertyTest` (200,000 stores of 1-40-digit values into 1-31-digit receivers, every encoder path)
pass; `parity/PARITY20.cbl` (BENCH02 at 20,000 rounds) matches GnuCOBOL.

Not done (next candidates from the last profile): `setScale(DOWN)` of products (scale 36 into scale 18, a BigInteger
division per store), `CobRt.compare` / `dec` decoding both operands on every compare, per-CALL argument arrays, the
`perform` / `_dispatch` loop. The Fineract edge-p03-down job itself was not re-timed here (it runs from the main tree's
host, which this work must not touch).

### Analyser defect: project.json `layout.copybookRoots` ignored: fixed

Before: the analyser found copybooks only in the program's directory and in directories named like cpy / copy /
copybooks ... (`CobolModelBuilder.CopybookDirectories`), so a project whose copybooks live elsewhere got "missing
copybook" diagnostics although `project.json` named the root (the GnuCOBOL oracle build used it via `-I`).

Now `CobolModelBuilder.ConfigureCopybookRoots(sourceTree, roots)` registers a source tree's configured roots; every
program under that tree searches them after its own directory and before the heuristics (kept as the fallback), for
every caller (`BuildGroundedFull`, the ANTLR front end, the oracle's resolver). The host registers each project's roots
when projects are loaded or saved (`ProjectService.ConfigureCopybooks`). Case: `ParserAcceptance` checks
`accept/layout/src/LAYOUT01.cbl`, whose copybook is in `accept/layout/members`: missing without configuration, found
with it.

## Summary

| Construct | Before | After | Cases |
| --- | --- | --- | --- |
| OCCURS DEPENDING ON | silent | lowered (standard case); complex ODO, odoslide, two GnuCOBOL defects: frontier | PARITY05, LOWER06 |
| EJECT / SKIP1-3 / TITLE | silent | lowered | PARITY07 |
| group-level USAGE / SIGN | silent | lowered | PARITY06 |
| REPLACE | silent | lowered | PARITY07 |
| COPY REPLACING (substring matching) | silent defect | lowered (text words) | PARITY07 |
| SEARCH, SEARCH ALL | frontier | lowered | PARITY08, LOWER07, LOWER01 |
| INITIALIZE REPLACING / FILLER / TO VALUE / TO DEFAULT | frontier | lowered; category TO VALUE, partly written table entries: frontier | PARITY09, LOWER08 |
| INITIALIZE of signed zoned items (GnuCOBOL dialect) | silent | lowered (switch) | PARITY09 |
| SET TO FALSE (and the 88 FALSE phrase) | frontier (phrase silent) | lowered | PARITY10, LOWER08 |
| PERFORM VARYING ... AFTER | frontier | lowered | PARITY11, LOWER09 |
| 18 intrinsic functions, NUMVAL on invalid input | frontier / silent | lowered; numeric DISPLAY / alphanumeric MOVE of results: frontier | PARITY12, LOWER09 |
| ALTER | frontier | lowered | PARITY13, LOWER03, LOWER10 |
| DECLARATIVES / USE AFTER ERROR | silent | lowered | PARITY14 |
| READ past the end (46) with AT END; phrase-handled statuses | silent defect | lowered | PARITY14 |
| COMP-5 byte order | partial | lowered (switch) | PARITY15 |
| several / nested programs in a source | silent | lowered (GLOBAL sharing: frontier) | PARITY16, PARITY17 |
| RECURSIVE + LOCAL-STORAGE, IS INITIAL | silent / partial | lowered | PARITY16, PARITY17 |
| EXTERNAL | silent | lowered | PARITY17 |
| CALL ... RETURNING | frontier | lowered; PROCEDURE DIVISION RETURNING: frontier (GnuCOBOL lacks it) | PARITY17, LOWER11 |
| DISPLAY of zoned items with NUL/space, of INDEX, RETURN-CODE, TALLY | silent | lowered | PARITY08, PARITY18 |
| SORT, MERGE, RELEASE, RETURN, table SORT | frontier | lowered; COLLATING SEQUENCE: frontier | PARITY19, LOWER12 |
| copybook roots from project.json | defect | fixed | ParserAcceptance |
| decimal kernel speed (P4) | - | about 2.4x faster on BENCH02 | PARITY20, DecimalFastPathPropertyTest |

**Census** (`docs/CONSTRUCT-CENSUS.md`, CardDemo + IBM sample + Fineract Tier 1, `tools/census/construct_census.py`;
rows updated with each change; it now also counts group-level USAGE/SIGN):

| | lowered | partial | frontier | silent | n/a |
| --- | --- | --- | --- | --- | --- |
| before | 19,275 (98.1%) | 26 (0.1%) | 27 (0.1%) | 26 (0.1%) | 299 (1.5%) |
| after | 19,323 (98.3%) | 25 (0.1%) | 8 (0.0%) | 0 | 299 (1.5%) |

Remaining non-lowered rows: ENTRY (frontier, 3), SET ADDRESS OF (frontier, 5), ACCEPT (partial, 24), RECORD VARYING
DEPENDING (partial, 1).

**Gate.** `dotnet run --project MlCpu\src\CobolConformance` against this worktree's sidecar: **205 pass, 0 fail,
0 blocked** (baseline 132): 16 new parity programs (PARITY05 to PARITY20), 7 new lowering cases (LOWER06 to LOWER12),
two updated (LOWER01, LOWER03), 16 new acceptance entries and the copybook-roots check. `gradle test` (java-sidecar)
passes: CobolSemanticsTest, NumericCodecPropertyTest, DecimalFastPathPropertyTest.

**Estate transpile check** (`work/xpdiff.py`, all 67 programs, baseline pipeline vs this one): lowered statements
10,862 -> 10,922, abstained 1,038 -> 1,019 (the rest are EXEC CICS / SQL / DLI in online programs), every program
still compiles, no statement became a frontier.

## Oracle (GnuCOBOL 3.3) defects found, not reproduced

- `IF X < X` is true for a negative signed zoned item (also a subscripted element compared with itself, and so ORD-MIN
  over the same element twice).
- `(start:)` of an OCCURS DEPENDING ON group takes the whole current length from the start position.
- INITIALIZE of an elementary non-uniform ODO table sizes its first occurrence as the whole table and copies past it.
- INITIALIZE of a table of groups copies occurrence 1's bytes (FILLER and unwritten items included) over the others;
  `category TO VALUE` applies every VALUE whatever its category.
- TEST-NUMVAL-C with a currency argument mis-scans it; PROCEDURE DIVISION RETURNING is not implemented (crash).
- `SOURCE_DATE_EPOCH` renders WHEN-COMPILED with the month and day one too low.
- Dialect differences reproduced behind switches or as GnuCOBOL does (GnuCOBOL vs IBM): a receiver holding its own ODO
  object is sized with the object's value before the MOVE (IBM: maximum); default INITIALIZE writes unsigned zeros
  into signed zoned items; COMP-5 is little-endian; SEARCH VARYING sets the data item to the index's value.

## Left open / half-done

- **ENTRY** stays a frontier. Sketch: start a synthetic paragraph at each ENTRY sentence and let the registry resolve
  an entry name to (owning program, start paragraph, linkage list); the open question is how `ProgramRegistry` finds
  the owning class from an entry name before that class is loaded (a generated entry index per package would do).
- **SET ADDRESS OF**, **ACCEPT** from other sources, **RECORD VARYING DEPENDING** on WRITE: unchanged.
- **GLOBAL** data shared with nested programs: not lowered (a nested program's reference does not resolve).
- **Default INITIALIZE of tables of groups** (no options) still initializes each occurrence leaf by leaf: identical to
  GnuCOBOL unless the entries hold FILLER (or REDEFINES) whose bytes differ between occurrences (GnuCOBOL copies
  occurrence 1's). Left as is; the fix would be the first-occurrence copy already used for ODO targets.
- **MOVE of an alphanumeric with non-digit bytes to a numeric item** (X'0300' to 9(4) COMP-5 is 0 in GnuCOBOL, 30 in
  the Java): seen while testing COMP-5, not investigated.
- The **record kit** (`MlCpu.CobolKb.Knowledge/RecordKit/Pic.java`) still encodes COMP-5 big-endian and knows neither
  the GnuCOBOL INITIALIZE of signed zoned items nor the zoned DISPLAY rule; kits for estates using them would disagree
  with the faithful program until it follows the runtime.
- **WHEN-COMPILED** is not yet a seed of the replay plan's volatile-field taint (`ReplayPlanBuilder`), as CURRENT-DATE
  is.
- **P4**: the next candidates are listed in its section; the Fineract job itself was not re-timed.
- The host (`MlCpu.Host`) still registers only the first program of a source when onboarding.

Scratch tools used for this work are in the git-ignored `work/` folder of the worktree: `par.sh` (one program, oracle
vs Java), `rebuild.sh`, `sidecar8089.sh` / `sidecar8097.sh` (this worktree's sidecar, and a baseline one on :8097),
`irdump`, `irdump-old`, `irdiff.sh` / `irdiff.py`, `xpdiff.py`, `perf4/` (benchmark, A/B script, sampler), `edits/`
(the scripted edits).
