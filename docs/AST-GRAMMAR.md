# COBOL-85 grammar (AST stage): IBM legacy constructs and large programs

Plan sections 0, 7 and 10 (`JavaToCOBOLToJava.md`). Branch `frontier-burndown` (worktree `CobolBdm-burndown`), not
committed: the owner reviews.

**What this is.** The AST stage (`GraphService`, `MlCpu.Cobol.Antlr`, README decision 19) parses each program with the
grammars-v4 COBOL-85 grammar (ProLeap; ANTLR 4.13.1, C# target, generated code checked in) and writes
`graphs/ast/<PID>.ast.json` and `<PID>.outline.txt`; the outline becomes `normalization/<PID>/ast-outline.txt` and goes
into the model prompts. A parse error degrades the outline (error recovery drops or misplaces statements), so the
outline the model reads is wrong exactly where the program is unusual. The transpiler does not use this grammar.

**Rules followed.** Every grammar change is driven by evidence (an estate program or a corpus case that failed), has a
conformance case in the parser lane that fails on the old grammar and passes on the new one, and each case compiles and
runs on GnuCOBOL 3.3 (`cobc -std=ibm -fsign=EBCDIC`). An AST that changed for a program that already parsed cleanly is
accounted for below. Errors are never hidden: a program is "clean" only with zero errors.

**Runner.** `tools/ast/ast-estates.sh [out-dir] [--timeout s]` parses every program of the five estates with
`MlCpu.JavaBridge --ast`, which now also takes a `project.json` (that project's program list and `layout.copybookRoots`,
as the host's graph stage) and prints per program the mode, time, lines, tokens, retained heap, AST and outline size
and the first errors. The J3S estate is read as a directory, with the copybook roots `j3s-build.sh` compiles it with.

## Result

Clean parses (zero errors) per estate, `tools/ast/ast-estates.sh`, the default 2-minute bound:

| Estate | Programs | Clean before | Clean after | SLL alone before | SLL alone after | Parse time, sum (max) before | after |
| --- | --- | --- | --- | --- | --- | --- | --- |
| CardDemo | 31 | 14 | **14** (17 online: missing IBM DFHAID/DFHBMSCA only, as before) | 2 | **31** | 29.5 s (7.0 s CORPT00C) | 1.7 s (0.5 s) |
| IBM z/OS sample | 2 | 2 | **2** | 0 | **2** | 1.6 s | 0.5 s |
| Fineract Tier 1 (`fineract-cobol`) | 18 | 16 | **18** | 3 | **18** | 16.6 s (7.4 s FNEM0200) | 1.3 s (0.5 s) |
| Fineract legacy (J8, `fineract-legacy`) | 21 | 3 | **21** | 3 | **21** | 15.3 s (5.7 s FNEM0200) | 1.4 s (0.6 s) |
| J3S (`build/j3s/cbl`, FSGN0100 132,608 lines) | 3 | 1 (FSGN0100 timed out: no tree) | **3** | 0 | **3** | 123.9 s | 2.5 s (2.3 s FSGN0100) |

The first program of each run carries the JIT and DFA warm-up (about 0.5 s). Every program of the conformance corpus
(accept, parity, lower, truth, job-control, projection: 54 programs with the new cases) also parses in SLL alone without
a syntax error (LAYOUT01 reports its copybook missing by design: the lane configures that root); before, PARITY09,
PARITY10 (a wrong tree, see below), PARITY19, LOWER08 and LOWER12 had errors.

## Findings

| Where | Errors before | Construct |
| --- | --- | --- |
| fineract-legacy, 18 of 21 programs | 219 × `mismatched input 'WHEN' expecting DOT_FS` (every 88 of the estate's switches) | `88 X VALUE 'Y' WHEN SET TO FALSE 'N'` (IBM Enterprise COBOL, COBOL 2002) |
| fineract-legacy FNEM0900; Tier 1 FNEM0900 (its 3 errors); J3S FSJBDEC (6) and FSGN0100 (12) | `no viable alternative at input 'EXIT PERFORM END-IF'` | `EXIT PERFORM` inside IF inside an inline PERFORM (the grammar had only `EXIT [PROGRAM]`, so `PERFORM` began a new statement) |
| Tier 1 FNDKDFO (25 errors) | `no viable alternative at input 'MOVE 20 TO DF-RC EXIT PARAGRAPH'` | `EXIT PARAGRAPH` inside IF |
| corpus PARITY09, LOWER08 | `no viable alternative at input 'G WITH'` | COBOL 2002 INITIALIZE: `WITH FILLER`, `ALL TO VALUE`, `NUMERIC TO VALUE`, `THEN TO DEFAULT` |
| corpus PARITY19, LOWER12 | `mismatched input ''ABCD''` | `RELEASE rec FROM 'literal'` |
| corpus PARITY10 | none, but a wrong tree | `88 C VALUES 1 THRU 3 FALSE 0`: FALSE (a boolean literal) was taken as a fourth condition value |
| FSGN0100 | none after the above, but 342 s of full LL: over the 2-minute bound, so no tree and the outline "no parse tree" | SLL prediction failed at the first subscript or reference modification of nearly every program (see below) |

Constructs the brief suspected but the grammar already parsed (checked, and kept by `AST07`): `GO TO … DEPENDING ON`,
`ALTER`, `SEARCH` / `SEARCH ALL`, 77-levels, sections and `PERFORM` of sections, `PERFORM … THRU`, an IF closed by a
period, `NEXT SENTENCE`, `COPY … REPLACING` (done by the front end), comment paragraphs (`AUTHOR.`, `DATE-WRITTEN.`).

### Why FSGN0100 took minutes: SLL never succeeded

`CobolAntlr.Parse` runs SLL prediction first (with bail-out) and, at the first SLL error, full LL from the start.
Before, 64 of the 72 programs of the four onboarded estates ran full LL; with the construct fixes alone still 61, each
from its first subscript or reference modification on. A diagnostic run (SLL with bail-out, the failing rule stack)
showed two decisions:

1. `identifier : qualifiedDataName | tableCall | …`. SLL prediction does not know the caller, so a `(` after a data name
   could also be what follows an identifier somewhere else (a juxtaposed subscript `T (A (I) (J))`, a condition-name
   subscript); on that conflict SLL takes the lowest alternative, `qualifiedDataName`, and `X (1)` fails at `(`.
2. `tableCall : qualifiedDataName (subscripts)* referenceModifier?`. The same conflict between entering the subscript
   loop and the reference modifier: SLL takes the loop, and `X (1:4)` fails at `:`. `functionCall` has the same shape
   (`FUNCTION CURRENT-DATE (1:8)`).

The repair orders the alternatives so that SLL's lowest-alternative choice is the right one: `tableCall` (which
subsumes `qualifiedDataName`) first, and in `tableCall` and `functionCall` the reference-modifier forms first. After it
every program of every estate parses in SLL alone. ANTLR guarantees that an SLL parse without errors is the tree full LL
builds, so this changes speed, not results; FSGN0100 went from 342 s (LL, over the bound) to 2.3 s.

Full LL was not only slow, it chose differently where the grammar was ambiguous. The reorder corrected two misparses
(every other AST of the five estates is byte-identical, `mode` and `parseMs` aside):

| Program | Source | Before | After |
| --- | --- | --- | --- |
| CardDemo COADM01C, COMEN01C, COTRN02C | `OPTIONI OF COADM1AI (WS-IDX:1)` | the reference modifier attached to the qualifier `COADM1AI` | to the qualified identifier `OPTIONI OF COADM1AI`, as COBOL defines it |
| Fineract FNEM0500, FNEM0600, FNEM0900 (both estates) | `FUNCTION LENGTH (LM-RP (1))` | two arguments, `LM-RP` and `(1)` | one argument, `LM-RP (1)` |

Their outlines are unchanged (the outline is token text), so no prompt of these programs changes.

## Grammar changes (`Grammar/Cobol85.g4`, each marked `MlCpu`)

| Rule | Change | Evidence and case |
| --- | --- | --- |
| `dataDescriptionEntryFormat3`, new `dataWhenSetToFalseClause` | `88 name VALUE … [WHEN SET TO] FALSE [IS] literal` | fineract-legacy (219 errors); `AST01`, PARITY10 |
| `dataValueIntervalFrom` | a VALUE literal is no longer TRUE or FALSE (`literal` minus `booleanLiteral`), so `VALUE 'Y' FALSE 'N'` is the FALSE clause, not three values. The JSON of every other VALUE clause is unchanged (single-child chains collapse to the same node) | PARITY10; `AST01` |
| `exitStatement`; tokens `CYCLE`, `PARAGRAPH` (added to `cobolWord`, so they remain usable as data names in COBOL-85 code) | `EXIT [PROGRAM \| PERFORM [CYCLE] \| PARAGRAPH \| SECTION]` | FNEM0900, FNDKDFO, FSJBDEC, FSGN0100; `AST02`, `AST03` |
| `initializeStatement`, new `initializeFillerPhrase`, `initializeValuePhrase`, `initializeDefaultPhrase` | `INITIALIZE x… [[WITH] FILLER] [{ALL \| category} TO VALUE] [[THEN] REPLACING …] [[THEN] TO DEFAULT]` | PARITY09, LOWER08; `AST04` |
| `releaseStatement` | `FROM identifier \| literal` (as `writeFromPhrase`) | PARITY19, LOWER12; `AST05` |
| `identifier`, `tableCall`, `functionCall` | alternatives reordered for SLL (above). The language is unchanged except that two or more subscript groups followed by a reference modifier (`X (1) (2) (1:3)`, not COBOL) no longer parse | FSGN0100 and 61 other programs in LL; `AST06` |

`tools/Generate-CobolParser.ps1` (named by the project file but missing) regenerates `Generated/` with
`java -jar tools/antlr/antlr4-4.13.1-complete.jar -Dlanguage=CSharp -package MlCpu.Cobol.Antlr.Generated -visitor
-no-listener`; on the unmodified grammar it reproduces the checked-in files exactly. `CobolAntlr.Grammar` (the label in
every AST and outline header) is unchanged, so the outlines of programs that parsed before stay byte-identical; the
grammar file's header lists the local changes.

## Large programs (`CobolAntlr.Parse`, `AstExport`)

FSGN0100 (132,608 lines; 136,222 after COPY expansion; 1,178,535 tokens), measured with the Debug build as the host
runs it:

| | Before | After |
| --- | --- | --- |
| Parse under the 2-minute bound | timeout at 120.7 s: no tree, outline "no parse tree", AST JSON without a tree | **OK, SLL, 2.3–2.9 s** (preprocess 0.4 s, lex 0.3–0.4 s, parse 1.5 s cold, 0.5 s warm) |
| Unbounded | 342 s LL, 0 errors (with the construct fixes); 367 s and 12 errors (`EXIT PERFORM`) before them | — |
| Managed heap held by the parse | 367 MB (LL) | 379 MB: about 110 MB source and tokens, 270 MB tree (with the grammar's DFA cache growth) |
| AST JSON / outline | 36.2 MB / 4.9 MB (133,708 lines) when unbounded | the same bytes |
| Peak working set of `--ast` (parse, JSON, outline) | 1,116 MB | **730 MB** (JSON streamed) |

The outline of FSGN0100 (4.9 MB) does not fit any model context; the prompt builders already include an outline only
when the prompt and a full reply fit.

What the stage does now:

- **SLL alone** for any program without syntax errors (the grammar change above): seconds at 130K lines.
- **Full LL with error recovery** when SLL reports an error, as before, under the bound.
- **SLL with error recovery as the fallback** when full LL does not finish in the bound (a very large program with a
  real syntax error; LL can take minutes there): mode `SLL-recovery`, its own bound, a tree and the errors, never Ok
  (`TimedOut` stays true and the first error says LL was stopped). The outline renders and its second line says
  `* incomplete parse (…): N error(s), first …`. SLL can only add errors (a construct full LL would accept), never hide
  one. Checked on a copy of FSGN0100 with one bad statement and `--timeout 20`: LL stopped at 20 s, SLL recovery 1.8 s
  later, 8 errors at the bad statement, the tree 920,175 nodes against 920,153 for the clean program.
- **AST JSON written by a streaming writer** (`Utf8JsonWriter`) instead of a JSON DOM: byte-identical output (checked on
  all 118 programs of the estates and the corpus), 5× faster on FSGN0100 (0.37 s against 1.8 s), and the DOM was most
  of the 1.1 GB peak.

No larger bound was needed: SLL makes the 2-minute bound ample (FSGN0100 uses 2 % of it).

## Conformance gate

Parser lane (`corpus/manifest.tsv`): seven new `accept/` programs, each also accepted by the analyser (no diagnostics,
no frontiers), compiled and run on GnuCOBOL, and failing on the old grammar (AST06 parses there only in full LL):

| Case | Construct |
| --- | --- |
| `AST01` (+ `cpy/ASTSWS.cpy`) | 88 `WHEN SET TO FALSE 'N'`, `WHEN SET TO FALSE IS`, after `VALUES ARE … THRU`, with a figurative, without `WHEN SET TO`, in an OCCURS table and in a copybook under `COPY REPLACING`; `SET … TO FALSE` |
| `AST02` | `EXIT PERFORM`, `EXIT PERFORM CYCLE` in inline `PERFORM VARYING`/`UNTIL`, inside IF and EVALUATE |
| `AST03` | `EXIT PARAGRAPH`, `EXIT SECTION`, sections PERFORMed alone and THRU |
| `AST04` | INITIALIZE `WITH FILLER`, `ALL TO VALUE`, `NUMERIC TO VALUE`, `THEN REPLACING … DATA BY`, `THEN TO DEFAULT`, combined |
| `AST05` | `RELEASE … FROM` literal and identifier |
| `AST06` | subscripts, relative subscripts, `X (I) (2:3)`, `X (I:)`, `A OF B (5:4)`, `FUNCTION CURRENT-DATE (1:8)`, `FUNCTION LENGTH (X (1, 1))`, in MOVE, COMPUTE, IF and DISPLAY: must parse in SLL |
| `AST07` | the J8 legacy constructs the grammar already parsed (77-levels, sections, PERFORM THRU, GO TO DEPENDING ON, ALTER, SEARCH, SEARCH ALL, period-closed IF, NEXT SENTENCE, ON SIZE ERROR, COPY REPLACING, comment paragraphs) |

New suite `ParserAst` (`Suites.ParserAst`): every manifest program (29) must parse with the COBOL-85 grammar without
errors and in SLL alone (an optional fourth manifest column says `ll` or `-` otherwise; none needs it), and CardDemo
(31 programs, the gate's `--carddemo` tree): no syntax errors, every program SLL, the only missing copybooks DFHAID and
DFHBMSCA.

| Gate | Result |
| --- | --- |
| `dotnet build MlCpu\MlCpu.slnx --no-incremental` | 0 errors; 1 warning, not from this work (CS8714 in `MlCpu.Host` `GnuCobolOracle.cs`, an earlier uncommitted change) |
| Conformance (`MLCPU_JAVA_SIDECAR=http://127.0.0.1:8089`, `work/sidecar8089.sh`) | **244 pass, 0 fail, 0 blocked** = 207 before + 7 ParserAcceptance (AST01–AST07) + 30 ParserAst; log `work/gate-ast-grammar.txt` |
| `java-sidecar` `gradle test` | 13 tests, 0 failures (unchanged; no Java was touched) |

## After the merge

- Rebuild the graphs of `fineract-legacy` (its `graphs/ast` in the main tree show 3/21) and `fineract-cobol`; the next
  publish refreshes `normalization/<PID>/ast-outline.txt` for the programs whose outlines change (the 18 legacy
  programs and Tier-1 FNDKDFO and FNEM0900, which had errors). CardDemo's and the IBM sample's outlines do not change.
- The ASTs of CardDemo COADM01C, COMEN01C and COTRN02C and of FNEM0500/0600/0900 change (the two corrections above).

## Files

| File | Change |
| --- | --- |
| `MlCpu/src/MlCpu.Cobol.Antlr/Grammar/Cobol85.g4` | the grammar changes above, a header listing them |
| `MlCpu/src/MlCpu.Cobol.Antlr/Generated/*.cs` | regenerated |
| `MlCpu/src/MlCpu.Cobol.Antlr/CobolAntlr.cs` | SLL-recovery fallback after an LL timeout; the tree is returned |
| `MlCpu/src/MlCpu.Cobol.Antlr/AstExport.cs` | streaming `ToJsonText`; the outline notes an incomplete parse |
| `MlCpu/src/MlCpu.Cobol.Antlr/MlCpu.Cobol.Antlr.csproj` | comment: where the local changes are documented |
| `MlCpu/src/MlCpu.JavaBridge/Program.cs` | `--ast` takes a `project.json`, `--timeout`, `--errors`; prints lines, tokens, heap, outline size, peak working set |
| `MlCpu/src/CobolConformance/Suites.cs`, `Program.cs`, `MlCpu.Cobol.Conformance.csproj` | suite `ParserAst` |
| `MlCpu/src/CobolConformance/corpus/manifest.tsv`, `accept/AST01-07.cbl`, `accept/cpy/ASTSWS.cpy` | the regression cases |
| `tools/Generate-CobolParser.ps1` | new: regenerates the parser |
| `tools/ast/ast-estates.sh` | new: the five-estate runner |
| `README.md` | decision 19: the extensions, SLL, the fallback |
