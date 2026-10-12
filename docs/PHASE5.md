# Phase 5: CobolBdm on the 216,217-line J3S estate (plan section 7, JavaToCOBOLToJava.md)

Branch `frontier-burndown` (worktree `CobolBdm-burndown`), not committed: the owner reviews. No model work.

**Estate.** `JavaToCOBOLTest/fineract-cobol-gen/out/j3s-m2-2026-10-11-xl`, the final package of build `j3s-m2h`
(re-packaged 2026-10-12 00:17; 216,217 lines): 146 programs (131 class programs, 7 runtime programs FSJR*, FSLIT01, two
job programs FSGN0100 / FSEM0100, FSJBDEC, the kernels FNDKRND / FNDTKRN, the drivers FSLS0100 / FSLE0100), 15
copybooks, 2 jobs (FJSCHGEN: schedules; FJEMIEVT: events over the ProgressiveEMICalculator API), 131 scenarios with
oracle A (the Fineract jar). Every class and runtime program is CALLed `USING FS-HEAP FS-CTX DT-PARMS JB FS-FRAMES`:
FS-HEAP is a **477,107,239-byte record passed BY REFERENCE** through 3,750 CALL statements; the largest programs have
15,064 (FSJJHM02) and 13,681 (FSPEC09) lines, 9,000 statements and 5,200 data items each.

**Rules.** As in the frontier burn-down: every fix has a conformance case, run on GnuCOBOL 3.3 (`-std=ibm
-fsign=EBCDIC`) where it is about behaviour, and checked to fail on the pipeline before the fix (`work/phase5/par-old.sh`:
sidecar :8098 on the main tree's compiled runtime and emitter) and to pass after it. Gates: `java-sidecar` `gradle test`,
then the conformance gate against this worktree's sidecar on :8089.

**Hosts.** Baseline on the merged host :5200 (project `fineract-xl` in the main workspace, onboarded 2026-10-11 23:50 from
the package as it was then, build `j3s-m2g`). After the fixes: this worktree's host on :5300 (`work/phase5/host5300.sh
--build`) with sidecar :8089, workspace `work/phase5/ws` (onboarded from the final package; source and scenarios checked
identical to it). The machine was shared and loaded (CPU 43-99 %: a J3S G2 pool of 32 jobs and a Gemma A/B run), so
absolute times carry that noise; ratios were measured interleaved.

## Result

| Stage | Before (:5200, merged build) | After (:5300, this worktree) |
| --- | --- | --- |
| Onboard (copy, scan, classify, oracle derivation) | 55 s | **13 s** |
| Oracle build (GnuCOBOL) | 46 s preparation + 2 units compiled one after the other (37.9 s + 36.1 s, every program twice) | 6 s preparation, **147 objects in 7 s** (8 in parallel, 57 s CPU), 2 links of 0.3 s |
| Oracle run | base 71 s + 35 scenarios 318 s (each scenario ran both jobs) | base 33 s + 35 scenarios **65 s** (each runs its own job) |
| Oracle total | 523 s; 72 job checks; reference 65/108 MATCH, 43 NOT COVERED | **112 s**; 37 job checks; reference **66/66 MATCH**, 0 not covered |
| Graphs (AST, DDG, call graph) | 51 s; DDG files 2.5 GB (FSPEC09 684 MB); host retained ~10 GB | **9 s** (4 in parallel); DDG **295 MB** (FSPEC09 81 MB) |
| Transpile | 374 s; **3/146** transpiled; 141 FAIL (`code too large`), 2 ERROR (HTTP timeout at 120 s), 80 statements abstained; javac of the estate failed | **35 s** (18 s + javac 17 s); **146/146**, **0 frontiers** |
| Publish (146 capabilities) | not reachable; with the transpile fixes only: 1,769 s, 3.3 GB project, compile out of memory | **787 s**, 1.5 GB; compiles (main and tests of all 146: 186 s) |
| Publish (5 capabilities, the test configuration) | — | 839 s (traces of FNDKRND, FNDTKRN, FSJBDEC: 6 job runs of the faithful Java) |
| Faithful Java vs GnuCOBOL, event job on base (20 loans) | 152 s vs 19 s (8x) | 88-96 s vs 21-24 s (**4x**), interleaved |
| G4 / G5 | not reachable | see "G4 and G5" |

## What failed, and the fixes

### 1. Words with consecutive hyphens (lexer): 80 statements abstained, 3 programs

`M1128-J3-S--EVENT--HEAD`, `M1139-J3-S--STRING--LITERALS`: the lexer ended a word at the first hyphen not followed by a
letter or digit, so `--` split the paragraph name; the paragraph was not recognised, its header became an unrecognised
statement and `PERFORM ... THRU` an inline PERFORM (FSEM0100 69, FSGN0100 8, FSLIT01 3 statements; `STRING` inside a
paragraph name became a STRING statement). A COBOL word may hold hyphens anywhere but at its ends.
Fix: `Lexer.Tokenize`. Case: `parity/PARITY22.cbl` (paragraph and data names with runs of hyphens, PERFORM THRU, GO TO,
a paragraph named with a verb); the pipeline before abstains on 13 of its statements.

### 2. JVM limits: 141 of 146 programs did not compile (`code too large`)

Every J3S program declares FS-HEAP and FS-CTX (5,226 items in a 76-line program): all field views were initialized in the
constructor, past 64 KB of bytecode. FSJRGC's paragraph JG-SWEEP (one EVALUATE of 128 WHENs, 9,000 lines of Java) passed
64 KB in one method (and in one `try`).
Fix (`SkeletonEmitter`), only where a limit would be reached, so smaller programs are emitted exactly as before:
- more than 600 data items: non-final field views built by `_items0..n()` from an instance initializer;
- more than 1,000 statements in `_init`, `_fields`, `_paragraphNames` or more than 1,000 paragraphs in `_dispatch`:
  helper methods of 1,000 each (`chunkedMethod`);
- a paragraph whose Java passes 2,500 lines is emitted again with every block of at least 40 lines outlined into a
  `boolean` method (the GO TO / EXIT PARAGRAPH / EXIT SECTION inside it leave the paragraph through the result; a block
  with a NEXT SENTENCE, EXIT PERFORM or loop label outside it, or a temporary declared outside it, stays inline).
- the state projection (`ProgramStateProjection`, P3) moves the `_init` / `_fields` helpers with their callers (the
  first published compile failed with `cannot find symbol _fields0`).
Cases: `parity/PARITY23.cbl` (generated by `tools/conformance/gen_parity23.py`: 2,400 items with VALUE clauses, 1,050
paragraphs, a paragraph of 160 WHENs with GO TO, EXIT PARAGRAPH, NEXT SENTENCE and EXIT PERFORM; the pipeline before:
`code too large`), `ProjectionRegression` "P3 state split of a large program" (moves and compiles).

### 3. LINKAGE records owned a copy of the caller's storage; an INITIAL program wrote its caller's record

Each program allocated its own byte array for every LINKAGE record and filled it in `_init` before the first CALL bound it
to the caller: 144 programs x 477 MB for FS-HEAP. The same `_init` ran again on every CALL of a `PROGRAM-ID ... IS
INITIAL` program while the record was still bound to the previous caller's storage, so it overwrote the caller's
argument with the storage default (silent; GnuCOBOL leaves LINKAGE alone).
Fix: `Area.linkage(size)` (no storage until bound; an unbound record used anyway gets storage of its own, as before:
`materialize`), `_init` no longer fills USING records, `CobolBase` materializes only unbound records. Case:
`parity/PARITY24.cbl` (an INITIAL program CALLed twice with one argument; a 2 MB record BY REFERENCE down two CALL
levels): the pipeline before shows `SEES      000` where GnuCOBOL shows `SEES HELLO008`. Unit test `ByReferenceStorageTest`.

### 4. A data item named like an emitter member (A2, A5) did not compile

Storage areas are named `a<id>`, so items named A2 or A5 (camel case `a2`) collided: `variable a2 is already defined`.
Fix: `planNames` reserves the area and file member names. Case: `parity/PARITY25.cbl` declares A2 and A5 (the pipeline
before does not compile it).

### 5. Transpile requests of 300 MB and a 120 s timeout

The IR sent to the sidecar carried the program graph's data dependences, which the sidecar never reads: 5.2 million for
FSPEC09 (every CALL is a definition and use of the whole heap and context records), a 300 MB JSON parsed back into a DOM
and serialised again. Fix: `IrExporter.TranspileRequest` / `ToTransportJson` (no data dependences, no DOM round trip),
used by the host, JavaBridge and the conformance runner; FSPEC09 now transpiles in 7 s. The graph builder computes data
dependences only where they are read (`ProgramGraphBuilder.Build(model, dataDeps)`: the DDG and the PDG view);
reaching definitions run elsewhere only when a dynamic CALL needs them.

### 6. ProgramGraphBuilder once per program per run (onboarding, oracle, publish)

CALL closures rebuilt every program's full graph for every question: the oracle built 292 graphs to look for CEE3ABD,
the kit planned the job (146 model builds) once per capability (21,000 model builds for 146 capabilities, 3.5 s each).
Fix: `ProgramGraphBuilder.Calls(model)` (CALL sites only, cached per model), used by `DdBindingPlanner.CallClosure`,
onboarding, the oracle and the estate service; the kit plans each job once.

### 7. Oracle: SYSOUT of two jobs in one file; a job run on data it does not use; build

- Both jobs have a step STEP20; the oracle appended the second job's SYSOUT to the first job's file, so FJSCHGEN's
  SYSOUT check held FJEMIEVT's lines (a job check run alone writes only its own). Fix: SYSOUT per job
  (`oracle/runs/<s>/sysout/<JOB>/`, also in the reference sweep).
- New `oracle.scenarioJobs` (project.json): scenario name pattern -> jobs it runs (first match; others run every job).
  For this estate `ev*` runs FJEMIEVT and the schedule sets FJSCHGEN: the oracle no longer runs the event job on every
  schedule scenario and the other way round (72 -> 37 job checks), and the reference sweep skips outputs only the
  skipped jobs write.
- `reference.scenarioMap` may name several folders (`base` -> `fineract-unit-p12-half_even;evmini-01-p12-half_even`):
  each output is taken from the first folder that has it.
- The oracle compiles every program once to an object (`oracle/bin/obj`, cached by the program's own content hash,
  `MLCPU_COBC_PARALLEL` in parallel) and links each unit inside `obj/` with bare names.

### 8. Graphs at scale

DDG files are `mlcpu.ddg.v2`, streamed: the statements as before, each field once, each edge `[from, to, field]`
(2.5 GB -> 295 MB). Programs are analysed 4 at a time (`MLCPU_GRAPH_PARALLEL`), each keeping only its CALL sites for the
call graph; the in-memory PDG cache is no longer filled with every program. DDGs over 16 MB stay out of the published
kit (MODERNIZATION.md lists them).

### 9. Publish and test at scale

- CALLs of a normalized subprogram passed each argument as a copy in and out (`ProgramRegistry.callModern`): 477 MB
  twice per CALL. A whole record is now passed as the caller's own array (BY REFERENCE; other arguments are still
  copied). Unit test `ByReferenceStorageTest`.
- CALL traces (worked examples) record every argument before and after each kept CALL: subprograms whose linkage passes
  16 MB are not traced (examples.md says so; their tests still run). The equivalence probe already skipped them.
- The record kit gives no typed class to a record of more than 2,000 items (FS-HEAP: 4,540 items, a 3.1 MB class and a
  2.2 MB test per capability); FS-CTX (632 items) keeps its class.
- Test resources: one copy of each step's datasets per scenario (`com/mlcpu/platform/steps/<JOB>.<STEP>/`), shared by
  the capabilities the step hosts (794 MB -> 6 MB for 146 capabilities).
- The published build compiles in its own JVM (`-Pmlcpu.javacHeap`, default 8g; the Gradle daemon ran out of memory on
  the estate's 6.7 million lines of Java) and tests in parallel JVMs (`mlcpu.testHeap` 6g, `mlcpu.testForks` default a
  core in eight).
- Transpile runs 4 programs at a time (`MLCPU_TRANSPILE_PARALLEL`, the sidecar's request pool).

### 10. GnuCOBOL's arithmetic-osvs: intermediate results (found by PARITY23)

GnuCOBOL `-std=ibm` sets `arithmetic-osvs: yes` (ibm-strict.conf): cobc aligns intermediate results of an arithmetic
statement to a scale it chooses at compile time (`cob_decimal_align`). `COMPUTE R0 = 4793 - T / 7` gives 800 on GnuCOBOL
(T / 7 truncated to 0 decimals) and gave 799 in the Java. The rule was read from cobc 3.2's typeck.c
(`build_decimal_assign`, `decimal_expand`, `decimal_compute`, `decimal_align`, `push_expr_dec`, `cb_walk_cond`) and
tree.c (`cb_build_binary_op` constant folding) and ported exactly (`OsvsPlanner`): the emitter wraps each intermediate
cobc truncates in `CobRt.align` (dialect switch `MLCPU_ARITHMETIC`, GnuCOBOL whenever `MLCPU_DISPLAY_NUMERIC=gnucobol`).
COMPUTE, ADD / SUBTRACT with several operands and the GIVING forms.

**GnuCOBOL defect found:** `cob_decimal_align` to a scale above the intermediate's own shifts the wrong way (divides by
the power of ten): `COMPUTE R3 = 1 + K * 66` (V999 receiver) gives 4001 instead of 4357, and `ADD A B TO C` with COMP A,
B (123, 456) and a V99 C (1.50) gives 501.50 instead of 580.50 (checked on cobc 3.3-dev). Not reproduced: the Java
computes the exact intermediate and stops the run (`CobRt.alignUp`, a frontier at run time) where the defect would
change it; where the dropped digits are zeros it goes on (GnuCOBOL and COBOL agree there). An aligned intermediate whose
scale is unknown at compile time (most intrinsic functions) is a frontier.
Cases: `parity/PARITY25.cbl` (22 statements: nested divisions and products, several receivers, folded literals, unary
minus, parentheses, ADD/SUBTRACT GIVING, three harmless defect cases); the pipeline before differs from GnuCOBOL on 7 of
them. `lower/LOWER13.cbl` (ADD A B TO C lowered with the run-time check; the unknown-scale case abstained).
Effect on the estates (IR of all programs, old and new emitter, `work/phase5/xpdiff5.py`): CardDemo, the IBM sample,
Fineract Tier 1 and the J8 legacy estate: no truncation and no defect site; the J3S estate: none (its 194 alignment sites
are integer expressions whose scale already equals the target).

### 11. Runtime speed (the faithful Java of J3S code)

Profile of the event job (`work/phase5/prof/Sampler.java`): 38 % in `CobField.dec`, 10 % `BigInteger.compareTo`, 8 %
`setDec`: every binary item was decoded through a copy, a BigInteger and a BigDecimal, and stored through BigInteger.
Exact long paths, each checked against the algorithm it replaces on random values and random storage bytes:
- binary decode and store up to 8 bytes (`BinaryFastPathPropertyTest`, 200,000 stores and decodes);
- numeric MOVE between items that fit a long, with rescaling (`NumericMoveFastPathPropertyTest`, 300,000 MOVEs);
- numeric comparison, `intValue` (subscripts) and MOVE of a literal through longs (300,000 cases);
- `NumLit.digits` computed once; no array allocated per CALL for programs without LOCAL-STORAGE.
Event job, base data: 152 s -> 88-96 s against GnuCOBOL's 19-24 s (8x -> 4x), same output.

## G4 and G5 (job outputs = GnuCOBOL; = the Fineract jar)

Published estate (all 146 programs faithful; capabilities for the two job steps FSLS0100 / FSLE0100 and the kernels
FNDKRND, FNDTKRN, FSJBDEC: see "Capabilities at scale"), :5300.

| Run | Scenarios | Job checks | G4 (= GnuCOBOL) | G5 (= Fineract jar) | Time |
| --- | --- | --- | --- | --- | --- |
| Oracle (GnuCOBOL vs the jar) | base + 28 mini + 7 evmini | 37 | — | 66/66 | 112 s |
| `test` (gradle test + job checks) | the same | 37 | **103/103** | **66/66** | 1,770 s (gradle 26 min: 3,157/3,157 tests in 296 suites) |
| Oracle, extended | + fineract-unit-p12-half_up, rejects-p12-half_even, events-01..07 (200 loans each), evbad-01..02 (100 loans, damaged records) | 48 | — | 79/79 | 218 s |
| `test?tests=false` (job checks after the build) | 47 scenarios | 48 | **127/127** | **79/79** | 674 s |
| Reference sweep (`filter=evmini-*`) | 7 | — | — | 7/7 | 2.0 min |

Per job: FJSCHGEN 93/93 outputs = GnuCOBOL (SCHEDULE, REJECTS, SYSOUT of 31 scenarios), 62/62 = the jar; FJEMIEVT 34/34
(EMIOUT and SYSOUT of 17 scenarios: 1,740 loans in evmini, events and evbad), 17/17 = the jar. The scenario sets not run
here are sample-*, edge-* and envelope-* (63 to 10,000 long loans per scenario): the J3S COBOL itself needs hours on them
(sample-p03-down: 4.0 h on GnuCOBOL, J3S.md), and the faithful Java about 4 times that.

## Capabilities at scale

Publishing every program as a capability works: 146 capabilities, 787 s, 1.5 GB, 6.7 million lines of Java, and it
compiles, tests included (`compileJava compileTestJava installDist`: 186 s; after the P3 fix above and fix 12 below).
The two largest record kit tests run green in it: FSJJHM02 30/30 (5 nested classes), FSEM0100 15/15. Testing them is O(capabilities x job runtime): each capability's acceptance test runs its host
step about 14 times (two scenarios, record mutations of each input, the storage default, the oracle comparison), and 144
of the 146 programs are hosted by a job whose faithful run takes minutes. For the test configuration the project names
the modernization candidates (`publish.capabilities`: FSLE0100, FSLS0100, FNDKRND, FNDTKRN, FSJBDEC); the other 141
programs are faithful translations, covered by the 135 characterization suites, the record kits and every job check.

### 12. The record kit's test of a large record did not compile

With every program a capability, FSEM0100's `RecordKitTest.accessorsStoreAndReadLikeTheProgram` (FS-CTX: 632 items, two
checks per table) passed 64 KB. The accessor checks are now split over methods of at most 150 checks (a kit within that
keeps its single method, as before). The next compile stopped at FSJJHM02's kit test: `too many constants` (4,200
checks; each adds about 16 constant-pool entries, measured with `javap`: 50,158 for 3,204 checks, so one class holds
about 4,000). A kit of more than 6 methods now puts them in `@Nested` classes of 6 (900 checks, about 15,000 constants;
the outer class keeps the helpers and `@AfterEach`); 45 of the 146 kits are split, the others are emitted as before.
Conformance suite `RecordKitScale` (5 checks): a record over 2,000 items gets no class and a note; a 400-item record's
checks span several methods; a small record keeps its class; a kit of 4,800 checks (three 1,600-item tables) goes into
`@Nested` classes and compiles (javac against the runtime, JUnit stubbed). With the nesting disabled the last two fail
(`too many constants`, as on FSJJHM02).

## Construct census (`tools/census/construct_census.py`, `docs/CONSTRUCT-CENSUS.md`)

The census gained the constructs this estate uses that it did not count: ON SIZE ERROR (2,019), NOT ON SIZE ERROR,
ROUNDED, reference modification (250), words with consecutive hyphens (80), COMPUTE with intermediate results (180);
a `:TAG:-FUNCTION` data name is no longer counted as an intrinsic function.

| J3S estate (216,831 lines in programs and copybooks, 45 constructs) | Before phase 5 | After |
| --- | --- | --- |
| Status table | 40 constructs, all "lowered" (153,388 occurrences) | 45 constructs, all **lowered** (155,874 occurrences) |
| What the table did not show | 80 words with consecutive hyphens: frontiers in the transpile; 180 COMPUTEs with intermediates: silent (GnuCOBOL truncates them, the Java did not); 141 programs did not compile | transpile 146/146, 0 frontiers; no GnuCOBOL truncation or defect site in the estate (xpdiff5) |

All five estates (CardDemo, IBM sample, Fineract Tier 1, J8 legacy, J3S; 274,000 lines): lowered 182,029 (99.8 %),
partial 25 (ACCEPT from other sources, RECORD VARYING DEPENDING on WRITE: CardDemo and the IBM sample), frontier 10 (ENTRY
5, SET ADDRESS OF 5: CardDemo and the J8 legacy T11 set), n/a 299 (CICS/SQL/DLI).

## Gates

| Gate | Before | After |
| --- | --- | --- |
| `java-sidecar` `gradle test` | 5 classes, 16 tests | **7 classes, 19 tests, 0 failures** (+ `ByReferenceStorageTest`, `BinaryFastPathPropertyTest`, `NumericMoveFastPathPropertyTest`: 1.1 million generated cases) |
| Conformance gate (`dotnet run --project MlCpu\src\CobolConformance`, sidecar :8089) | 259 pass, 0 fail | **283 pass, 0 fail, 0 blocked** (`work/phase5/gate-4.txt`) |

New conformance cases: `parity/PARITY22` (consecutive hyphens), `parity/PARITY23` (JVM limits; generated by
`tools/conformance/gen_parity23.py`), `parity/PARITY24` (LINKAGE without storage, INITIAL), `parity/PARITY25`
(arithmetic-osvs; data names A2/A5), `lower/LOWER13` (the osvs defect check and the unknown-scale frontier), their
`ParserAcceptance` / `ParserAst` entries, `ProjectionRegression` "P3 state split of a large program" (2 checks), suite
`RecordKitScale` (5 checks). Each case was run on the pipeline before the fix and failed there (PARITY22: 13 statements
abstained; PARITY23: code too large; PARITY24: the second CALL sees `     000`; PARITY25: does not compile, and with the
names changed differs from GnuCOBOL on 7 of 22 lines).

Both gates were run again after the architect copied six files from the main tree over this worktree (2026-10-12 03:40:
`ModernProgram.java`, `JobEngine.java`, `ImplementStep.cs`, DesignModel `Program.cs`, `KitFeatures.cs`, `KitExamples.cs`).
Of these only `ModernProgram.java` held a phase-5 edit (one sentence of the `call` documentation: a whole record is the
caller's own array); it was reapplied on top. The new `ModernProgram.callProgram` (`CobField.temp` over each array, then
`CobRt.call`) composes with the BY REFERENCE change: a temp field covers its whole array (position 0, the array's
length), so `callModern` passes that array itself to a normalized target, and a faithful target binds its LINKAGE to it.
Sidecar `gradle cleanTest test` 19/19, gate 283/0/0 with them in place.

## Files changed in phase 5

Analysis: `Lexer.cs` (1), `JavaBridge/IrExporter.cs` (5), `ProgramGraphBuilder.cs` (5, 6),
`Projections/ProgramStateProjection.cs` (2). Knowledge: `DdBindingPlanner.cs` (6), `RecordKit/RecordKitBuilder.cs` (9, 12).
Host: `Program.cs`, `Services/EstateService.cs`, `JavaProjectAssembler.cs`, `JavaSidecarService.cs`, `Workspace/`
`CapabilityKitBuilder.cs`, `ConversionService.cs`, `GnuCobolOracle.cs`, `GraphService.cs`, `OnboardingService.cs`,
`ProjectModel.cs`, `ReferenceOracle.cs`, `ReferenceSweepService.cs`, `WorkspaceActions.cs` (5-9). JavaBridge `Program.cs`
(5). Runtime (`java-sidecar/src/main/java/com/mlcpu/rt`): `Area.java`, `CobolBase.java` (3), `CobConfig.java`,
`CobRt.java` (10, 11), `CobField.java`, `NumLit.java` (11), `ProgramRegistry.java`, `ModernProgram.java` (9). Emitter:
`sidecar/SkeletonEmitter.java` (2, 4, 10), new `sidecar/OsvsPlanner.java` (10). Tests: new `ByReferenceStorageTest`,
`BinaryFastPathPropertyTest`, `NumericMoveFastPathPropertyTest`. Conformance: `Program.cs`, `Suites.cs` (ProjectionRegression,
RecordKitScale), corpus `parity/PARITY22..25.cbl`, `lower/LOWER13.cbl`, `manifest.tsv`, `lower-manifest.tsv`;
`tools/conformance/gen_parity23.py`. Census: `tools/census/construct_census.py`, `docs/CONSTRUCT-CENSUS.md`. Docs:
`README.md` (decisions 18 and 19, `test?tests=false`), this file. Work files (scripts, logs, workspace) under
`work/phase5/`.

## Open items

- **:5200.** Its project `fineract-xl` was onboarded from the package before the final re-packaging (build `j3s-m2g`):
  after the merge, rebuild the host and onboard the estate again from `out/j3s-m2-2026-10-11-xl` (and set
  `oracle.scenarioJobs`, `reference.scenarioMap` and `publish.capabilities` as in
  `work/phase5/ws/fineract-xl/project.json`).
- **Scenario sets not run:** sample-*, edge-*, envelope-* (hours per scenario on GnuCOBOL itself, about 4 times that in
  the faithful Java). G4/G5 hold on the 47 scenarios run (127/127, 79/79).
- **Runtime speed:** the faithful Java runs the event job 4 times slower than GnuCOBOL (was 8). Next in the profile:
  `CobField.dec` for packed and display items that fit a long, and BigDecimal arithmetic in COMPUTE.
- **Testing 146 capabilities** is O(capabilities x job runtime); the test configuration names 5 candidates
  (`publish.capabilities`), the published project of all 146 compiles but its full `test` was not run.
- **GnuCOBOL arithmetic-osvs defect** (fix 10) is reported here, not reproduced: the Java stops (`CobRt.alignUp`) where it
  would change a result. No such site in any of the five estates.
- The host runs `gradle -q`, so the test progress bar stays at 0 until the task ends (pre-existing).
