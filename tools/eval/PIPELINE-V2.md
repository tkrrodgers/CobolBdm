# Pipeline v2: generated record kit and sharper feedback

**Why.** Gemma's arm-A run (2026-10-10, `PROTOCOL.md`) needed 31 implementation attempts for five programs. 7 did not
compile. 19 ran but differed from the faithful program, mostly in report layout (CUSTRPT, TRANREPT, SYSOUT) and record
encoding (ACCTDATA, TCATBALF, CUSTOUT). SAM1 sat at 0/13 for six attempts on one report-layout issue. The pipeline already
knows every layout, the faithful program and the oracle, so it should produce these parts itself and tell the model
precisely what differs. That should cut more attempts than another model would.

## What changes

| # | Change | Where | Targets |
|---|---|---|---|
| 1 | **Record kit, generated at publish.** Per capability, `<package>.records`: a typed class per record area the program does I/O with (FD records; WORKING-STORAGE records used by READ INTO, WRITE/REWRITE FROM, MOVE to or from an FD record, CALL USING; LINKAGE records), with VALUE clauses applied, typed accessors with COBOL MOVE semantics (padding, truncation, sign, zoned/packed/binary, editing) and OCCURS without bound checks. Report lines are the same classes, so a line is VALUE-initialised and formats its edited fields. A class per FD carries DD, record length, keys and the `WRITE … FROM` image. `Pic` holds the picture helpers (`fit`, `edit`, `display`). A `RecordKitTest` proves every class against the faithful program's storage. | `MlCpu.CobolKb.Knowledge/RecordKit*.cs`, `JavaProjectAssembler` | report layout, record encoding (most of the 19 runtime misses) |
| 2 | **Field-level feedback.** The probe dumps both runs' outputs; the harness splits them into records, matches each to its layout (by length, or for print lines by their constant FILLER text) and names the fields that differ, with both values decoded. SYSOUT differences show whole lines. Up to three failing scenarios with different symptoms are probed in one run. | `ImplementStep`, `FeedbackRenderer`, `normalization/<PID>/record-layouts.json` | every runtime miss |
| 3 | **Where the logic diverges.** The probe traces the faithful program on the failing scenario: every READ, WRITE, REWRITE, DELETE, DISPLAY and CALL with the COBOL statement and PERFORM chain that executed it. Feedback names the statement and rule (packet `rules[].statements[].stepId`) that wrote the first differing record, the input records it had just read, and which statement would have written the record the model produced instead. Subprograms get each CALL's arguments field by field after the call. | probe + `FeedbackRenderer` | logic misses (SAM1 record order, CBACT04C fallback, abend paths) |
| 4 | **Smaller repairs.** After the first attempt the model may answer with SEARCH/REPLACE edit blocks instead of whole files. Compiler errors come with the offending source lines. The contract check no longer lets a model reach the faithful program through `<package>.legacy`. | `ImplementStep` | the 7 compile failures; shorter replies |
| 5 | **Pattern library.** A program that passes leaves a pattern (its features, design summary, verified code and the fixes that got it there). Later programs retrieve the most similar patterns from other programs (BM25 over the packet features; an OpenAI-compatible embeddings endpoint such as EmbeddingGemma when configured). Off unless `--patterns <dir>` is given. | `PatternLibrary` | first-attempt quality on later programs |

The requirements packet is unchanged (its hash pins the CardDemo designs and evidence); the kit sits beside it like the
AST outline.

## Measurement

Same protocol as `PROTOCOL.md` arm A (same programs, budgets, seed, llama.cpp build, sampling, no fallback). Two things
differ, and both are the treatment: the harness commit (pipeline v2) and the published estates, re-published by v2 from
the same frozen sources and oracle runs into `workspace-v2` (the v1 workspace stays untouched for the baseline).
The pattern library is off, so every program is solved on its own and no model sees another model's code.

Order (owner, 2026-10-10): gpt-oss-120b first (MXFP4, OpenAI's sampling: temp 1.0, top-p 1.0; reasoning high for
implementation, medium for design), then decide on the others. Compare with Gemma's v1 arm A: programs passed,
implementation attempts (31), minutes, and the services' tests and job outputs (17/17).

Expected effect (owner's estimate): a third to half fewer attempts on these five programs.

## Validation before any model run (2026-10-10)

* **Record kits** for all five programs, re-published into `workspace-v2`: `RecordKitTest` passes on both estates (every
  initial image under both storage defaults, every accessor's MOVE and decode, against the faithful program's storage). A
  mutation of `Pic` (padding byte, comma insertion) makes it fail (122 differences), so the test has teeth.
* **Harness self-test** (`Invoke-ServiceVerification -Model published -Arm selftest`): CardDemo 122/122 tests (116 + 6 kit
  tests) and 11/11 job outputs; zopeneditor 65/65 (61 + 4) and 6/6.
* **Replays** (`MlCpu.DesignModel replay`): Gemma's recorded failing attempts from today's v1 run, replayed in a v2 harness,
  get this feedback (abridged):

| Attempt (v1) | What v1 said | What v2 says |
|---|---|---|
| SAM1 6, 0/13 | first differing bytes of CUSTRPT | `RPT-NUM-TRANS (PIC ZZZ,ZZZ,ZZ9 @14+11, rptNumTrans()): legacy «          1», yours «1  ,   ,   »` (an edited PICTURE: use the kit's setter) |
| SAM1 7, 0/13 | records shifted by one byte, then more | your file holds 1596 bytes of lines, not a whole number of 132-byte lines (it divides into 133-byte ones) |
| CBACT04C 4, 2/17 | bytes 122-299 differ | `FILLER @122+178`: the program has spaces here, yours left x'00' |
| CBACT04C 6-8 | byte 12 differs in 49 records | `ACCT-CURR-BAL (PIC S9(10)V99 @12+12): legacy 666649999.81 «06666499998A», yours 6666649999.81 «66666499998A»` |
| CBACT04C 9, 16/17 | bytes 4-15 differ | `TRAN-ID: legacy «2022      000001», yours «2022000001      »`, written by `WRITE FD-TRANFILE-REC FROM TRAN-RECORD` in 1300-B-WRITE-TX |
| CBTRN02C 3, 19/23 | byte 35 differs | `ACCT-CREDIT-LIMIT: legacy 2020.00 «000000202000», yours 2020.00 «00000020200{»` (same value, different encoding: check the sign convention) |
| CBTRN03C 3, 2/23 | x'00' runs in TRANREPT | report lines matched to REPORT-NAME-HEADER, TRANSACTION-DETAIL-REPORT, …; `TRAN-REPORT-CAT-CD (PIC 9(04)): legacy «0001», yours «1   »`; numeric DISPLAY format hint for SYSOUT |
| CBTRN03C 5, 20/23 | not probed (v1 skipped scenario names ending in ")") | an extra blank console line before `DISPLAY 'FILE STATUS IS: NNNN' IO-STATUS-04` in 9910-DISPLAY-IO-STATUS, after `READ KEY CARDXREF: status 23` |
| SAM2 1, 2/13 | first differing bytes of CUSTOUT | CALL 1: `CUST-NAME: legacy «IBM-RTP», yours «IBM»`; `TRAN-OK: legacy «Y», yours «N»`; `TRAN-MSG: yours «INVALID ACTION CODE»` |
| CBACT04C 1 (28 compile errors) | 28 errors | the first syntax error with numbered source lines; "27 further errors follow from this one" |

* **Edits**: a SEARCH/REPLACE block (inside a ```java fence) is applied; one whose SEARCH text is not in the file is reported as
  a contract problem.

## Result: gpt-oss-120b, arm A, pipeline v2 (2026-10-10, harness 29257f9)

Three earlier v2 runs were stopped and voided for harness defects found on the live model (edit-block format, edits versus
complete files, an unhandled server error); see `DEVIATIONS.md`. The run below is complete, on one commit.

| Program | gpt-oss-120b, v2 | Attempts (C = compile errors, x/y = scenarios) | Gemma, v1 | gpt-oss-120b, 2026-10-06 (older harness) |
|---|---|---|---|---|
| CBTRN02C | 23/23, attempt 12 | 18/23 C2 C2 C1 19/23 22/23 17/23 22/23 17/23 17/23 22/23 **23/23** | 23/23, attempt 5 | 0/23 |
| CBACT04C | 17/17, attempt 8 | C1 1/17 C7 C99 13/17 C1 15/17 **17/17** | 17/17, attempt 10 | 1/17 |
| CBTRN03C | 23/23, attempt 8 | C1 C3 (contract) C3 3/23 4/23 18/23 **23/23** | 23/23, attempt 6 | 3/23 |
| SAM1 | 9/13 after 15 | C36 C2 0/13 2/13 2/13 C1 2/13 (server) 2/13 C1 2/13 2/13 1/13 3/13 9/13 | 13/13, attempt 8 | not run |
| SAM2 | 13/13, attempt 3 | 11/13 11/13 **13/13** | 13/13, attempt 2 | not run |

* **4/5 programs; 46 attempts; 49 minutes** (design and implementation; 61 min wall clock with model load and verification).
* **Services:** CardDemo with gpt-oss's code for all three programs, 122/122 tests and 11/11 job outputs; zopeneditor with
  its SAM2 (SAM1 keeps the published classes), 65/65 and 6/6.
* **Against Gemma v1 on the four programs both pass:** 31 vs 23 attempts (+35%), 31.9 vs 44.5 minutes (-28%).
* **First attempts are much better:** CBTRN02C 18/23 and SAM2 11/13 on attempt 1 (Gemma v1: 2/23 and 2/13). Under the
  older harness gpt-oss passed nothing; under v2 it passes every CardDemo program.
* **Where attempts still go:** 14 compile failures (mostly the model's own API misuse; 10 missing imports were added by
  the harness), 26 runs with differences, 1 contract problem, 1 reply the server could not parse.
* **Run-to-run variance:** the voided third run reproduced the first four CBTRN02C replies token for token, then diverged
  at attempt 5 on an identical prompt (GPU nondeterminism in llama.cpp); it passed CBTRN02C at attempt 7, this run at 12.
  The protocol's seed rule (re-run close cells with seeds 7 and 1234) applies before conclusions.
* **Not yet measured:** v2 against v1 for the same model. That needs Gemma on v2 (about an hour of GPU).

v2.1 candidates seen in this run (not applied, to keep the measured harness fixed): cite the packet's legacy quirk (Q-01
test-after overrun) when the divergence falls in its paragraph; give the "one unit apart: truncation, not rounding" hint
only for items with decimals.

## Result: Gemma 4, arm A, pipeline v2 (2026-10-10, harness e9c1e61)

The owner's direction: re-test only Gemma on v2 and compare it with Gemma on v1. One run, seed 42, no voided runs.

| Program | Gemma, v2 | Attempts (C = compile errors, x/y = scenarios) | Gemma, v1 | Minutes v2 / v1 |
|---|---|---|---|---|
| CBTRN02C | 23/23, attempt 5 | C6 C1 3/23 22/23 **23/23** | 23/23, attempt 5 | 7.9 / 11.3 |
| CBACT04C | 17/17, attempt 6 | C1 C1 3/17 15/17 16/17 **17/17** | 17/17, attempt 10 | 7.7 / 19.2 |
| CBTRN03C | 23/23, attempt 11 | C19 C19 C1 3/23 C1 4/23 18/23 C1 20/23 20/23 **23/23** | 23/23, attempt 6 | 13.2 / 9.9 |
| SAM1 | 13/13, attempt 5 | C14 C1 C3 0/13 **13/13** | 13/13, attempt 8 | 9.0 / 20.1 |
| SAM2 | 13/13, attempt 1 | **13/13** | 13/13, attempt 2 | 3.1 / 4.1 |

* **5/5 programs on both pipelines.**
  * Attempts: 28 on v2 against 31 on v1 (−10%).
  * Minutes (design and implementation): 40.9 against 64.6 (−37%).
  * Wall clock with model load and verification: 47.6 against 72.9 minutes.
* **Services:**
  * CardDemo with Gemma's code for all three programs: 122/122 tests, 11/11 job outputs.
  * zopeneditor: 65/65 tests, 6/6 job outputs.
  * v1 had 116/116 and 61/61. The v2 estates add `RecordKitTest`.
* **Where v2 helped:**
  * SAM1 passed at attempt 5 against 8. Its first compiled run scored 0/13, then 13/13 after the field-level feedback.
  * CBACT04C passed at attempt 6 against 10.
  * Minutes fell more than attempts, because SEARCH/REPLACE edits shorten the replies: 19 edits applied.
* **Where it did not:** CBTRN03C took 11 attempts against 6. Five of them were compile failures (two of 19 errors, from
  the model's own API misuse), and one seed cannot tell this from variance.
* **Against gpt-oss-120b on v2:** Gemma passed 5/5 against 4/5, in 28 attempts against 46. gpt-oss generates 2.8 times
  faster (about 140 against 50 tokens/s).
* **Conclusion so far:**
  * v2 cuts Gemma's time by a third with the same pass rate.
  * The attempt difference (−10%) is within the protocol's "close" band (under 30%). Seeds 7 and 1234 would be needed
    before claiming v2 cuts attempts. That is about an hour of GPU each, and the owner decides.
* **Caveats:**
  * During this run the SME built the forward-engineering package on the same machine (Gradle builds, CPU only). This may
    have lengthened the compile and test steps somewhat; the GPU work was not shared.
  * The service verification waited 16 minutes on an isolated host it had started (see `DEVIATIONS.md`). That wait is
    not in the run's minutes.
