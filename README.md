# COBOL BDM — COBOL to Java

Implementation of `CobolToJavaProcess.md`, guided by `CobolToJavaAnswers.md`, proven end to end on
[AWS CardDemo](https://github.com/aws-samples/aws-mainframe-modernization-carddemo).

## Result

The CardDemo batch flow converted to Java produces output **byte-identical** to the original COBOL, as run by GnuCOBOL:
24 of 24 comparisons match (`work/parity/parity-report.md`).

| Step | Program | Compared |
|---|---|---|
| S1–S3 | CBACT02C, CBACT03C, CBCUS01C | SYSOUT, return code |
| S4 | CBTRN01C | SYSOUT (1,808 lines), return code |
| S5 POSTTRAN | CBTRN02C | TRANSACT, DALYREJS, ACCTDATA, TCATBAL, SYSOUT, return code 4 |
| S6 INTCALC | CBACT04C (PARM) | SYSTRAN, ACCTDATA (alternate-key reads), SYSOUT |
| S7 COMBTRAN | sort + reload | TRANSACT |
| S8 TRANREPT | sort/INCLUDE + CBTRN03C | TRANDALY, TRANREPT (519-line report), SYSOUT |

All 12 CardDemo batch programs transpile and compile. CBSTM03A has 9 frontiers: z/OS control blocks via `SET ADDRESS OF`, and `ALTER`.
CBACT01C needs the assembler routine COBDATFT, so it is not in the parity run.

The exported **Spring Boot** jobs (`<job>-spring`), driven by the harvested JCL, also match the oracle byte for byte:
11 of 11 outputs (`tools/carddemo/Invoke-SpringJobParity.ps1`).

| Job | Steps run by the generated `JobEngine` | Outputs matched |
|---|---|---|
| POSTTRAN | CBTRN02C (MAXCC 4, as on the oracle) | TRANSACT, DALYREJS, ACCTDATA, TCATBALF, SYSOUT |
| INTCALC | CBACT04C with PARM from JCL | SYSTRAN(+1), ACCTDATA, SYSOUT |
| TRANREPT | PROC REPROC → IDCAMS REPRO, SORT with SYMNAMES/INCLUDE, CBTRN03C | TRANSACT.DALY, TRANREPT, SYSOUT |

SYSOUT is compared line by line, because GnuCOBOL on Windows ends lines with CRLF and the JVM uses LF.

Projections 2–10 are applied to every exported project. The projected code still matches the oracle byte for byte: 11/11 from the
per-job projects, and 11/11 from the domain-partitioned estate project (`carddemo-batch-spring`: 10 jobs, 11 programs, 7 Gradle
modules, 141 tests). With the wall clock instead of a pinned one, `RecordCompare` driven by `replay/ReplayPlan.json` reports
`EQUIVALENT_EXCEPT_VOLATILE`: every differing byte lies in the plan's clock-derived fields (POSTTRAN TRAN-PROC-TS 262/262;
INTCALC TRAN-ORIG-TS and TRAN-PROC-TS 50/50).

Conformance gate (`dotnet run --project MlCpu/src/CobolConformance`): 131 checks, all pass. Lanes: parser acceptance, JCL,
job resolution, DD binding, replay plan, partitioning, lowering, truth tables (hand-computed expected values), GnuCOBOL
parity, and projections end to end. `PARITY03`/`LOWER05` cover WRITE ADVANCING and `PERFORM … WITH TEST AFTER`.

### A second estate: IBM Z Open Editor sample (SAM1, SAM2)

Onboarded from `zopeneditor-sample-main` with no estate-specific code: SAM1 (a sequential master-file update over a
variable-length customer file, a 132-byte print report written with `WRITE … ADVANCING`) and SAM2 (a subprogram SAM1 CALLs
through a data-name). The published estate passes 61/61 tests, and job ZDERUN matches GnuCOBOL on 6/6 outputs (CUSTOUT,
CUSTRPT, SYSOUT) over two scenarios: the sample data and `all-transactions`, an authored scenario that reaches every
SAM1/SAM2 path. It found three runtime/transpiler defects, now fixed: `WRITE … ADVANCING` was a frontier (now GnuCOBOL's
exact control bytes), `PERFORM VARYING … WITH TEST AFTER` ran one pass too few, and files left open at GOBACK were not
closed implicitly. The conformance case for them found a fourth: control characters in literals were emitted as `\u000a`
escapes, which Java turns into a line break before lexing.

## Workbench: several COBOL projects (`MlCpu.Host`)

`dotnet run --project MlCpu\src\MlCpu.Host --urls http://localhost:5100`. The header shows the active project on every page.

| Page | What it does |
|---|---|
| **Settings** | Onboard a project (git clone, or copy, into `projects/<id>/source`), select the active one, see its layout, programs (deduplicated by PROGRAM-ID; kinds batch / subprogram / online / unreferenced), datasets and jobs; build &amp; run the GnuCOBOL oracle; Gemma model and llama.cpp paths. |
| **Conversion** | *Transpile all* (progress per program, Java viewer with frontiers), then *Publish* (default `projects/<id>/publish`) the normalized Spring estate with the capabilities layer and the modernization kit, then *Build &amp; test*: `gradle test` and every job against the GnuCOBOL oracle. |
| **Graphs** | *Rebuild all graphs*: COBOL-85 AST (`graphs/ast/<PID>.ast.json` + outline), DDG (`graphs/ddg/<PID>.ddg.json`), call graph (`graphs/callgraph.json`). Cytoscape views of the estate call graph and a program's paragraph graph; the PDG of a paragraph is built in memory on click. |
| **Gemma 4 Modernization** | Enabled once the project is published. Choose N programs, *Design* (Gemma's designs rendered), *Publish design to service* (Gemma implements each design against its equivalence test; its code replaces the capability classes in a copy of the published project), *Test with GnuCOBOL*. |
| **View Gemma 4's Code** | Enabled once Gemma has written code for the active project. A VS Code–style explorer of `projects/<id>/gemma/<PID>/` (its classes under `java/`, the design, the implementation prompt, every attempt with its reasoning; each program badged with its acceptance result). Files open in tabs in a read-only Monaco editor (BlazorMonaco's bundle); each tab keeps its scroll position and cursor. |
| Jobs, Parity | As before; Parity runs the CardDemo harness for the CardDemo project. |
| **Forward Engineering** | Enabled once the forward-engineering evaluation has results (`tools/eval/PROTOCOL-forward.md`; `MlCpu:ForwardRoot`, default `..\modelEval\forward`). Shows:<br>• the target service (`forward/cas`: the Customer Account Service and its Phase 0 gate);<br>• the decision;<br>• the design round: automated checks, blind SME rubric, peer reviews, chosen designs;<br>• Gemma's implementation arms S (its own design) and X (gpt-oss-120b's design) per seed;<br>• the code in the same explorer and Monaco viewer. |

Every long action is a background operation with a progress bar and a live log (navigating away does not stop it); the same
actions are headless under `/api` (see Run).

| Project | Programs | GnuCOBOL oracle | Published estate: tests · job outputs vs GnuCOBOL |
|---|---|---|---|
| carddemo (profile `tools/profiles/carddemo.profile.json`) | 31 (11 batch, 2 subprogram, 17 online, 1 unreferenced) | CardDemo harness: 25 executables, 24/24 comparisons | 116/116 · 11/11 |
| zopeneditor-sample (derived, no profile) | 2 (SAM1 batch, SAM2 subprogram); SAM1LIB and the `multiroot` copies excluded as duplicate PROGRAM-IDs | generic: 1 executable, job ZDERUN, 2 scenarios | 61/61 · 6/6 |

**Gemma 4 through the workbench** (gemma-4-31B-it BF16 with its MTP draft model, llama.cpp; every design valid on the first attempt):

| Program | Kind | Acceptance (equivalence test) | Attempt | Minutes | Notes |
|---|---|---|---|---|---|
| CBTRN02C | step | 23/23 | 8 | 18.0 | one pass (the earlier CLI run in decision 16: attempt 4) |
| CBACT04C | step | 17/17 | 4 | 7.4 | one pass (earlier: attempt 7) |
| CBTRN03C | step | 23/23 | 12 | 20.4 | one pass (earlier: attempt 15) |
| SAM2 | subprogram | 13/13 | 6 | 12.2 | second pass, after the `by-reference-writes` quirk and all-scenario CALL traces; the first stalled at 11/13 |
| SAM1 | step | 13/13 | 12 | 25.1 | third pass. With the order-of-effects trace and the `test-after-overrun` quirk it reached one remaining issue (the totals lines' columns) but stayed at 0/13 for 20 attempts; the AST outline in the implementation prompt and record-level difference feedback closed it |

- The services assembled from Gemma's code pass every test and match GnuCOBOL byte for byte on every job output, run with `--mlcpu.implementation=normalized`:
  - CardDemo: 116/116 tests, 11/11 outputs over POSTTRAN, INTCALC and TRANREPT.
  - zopeneditor-sample: 61/61 tests, 6/6 outputs over ZDERUN's two scenarios. Gemma's SAM1 CALLs Gemma's SAM2 through `ctx.call`.
- Caveat: in the CardDemo runs the AST outline reached the design prompts but not the implementation prompts. A line-ending bug, since fixed, prevented it, and the corrected run records say so.
- Pass histories: `projects/zopeneditor-sample/gemma/RUNS.md`.

## Deterministic Java modernization: composed storage (first increment)

The first implementation increment of [modernizeJavaCode.md](modernizeJavaCode.md) is available without running a model.
It is opt-in; existing transpilation and published exports keep their default behavior.

- The emitter generates a private, static `Storage` aggregate owned by the program through composition. It contains
  byte areas and field views, not procedure methods, file handles or paragraph control. The program adapter still
  extends `CobolBase`; there is no `extends State_<PID>` in this lane.
- MOVE, conditions, table/ODO references, file keys/status, SORT areas, VALUE initialization and LINKAGE/LOCAL-STORAGE
  hooks use the same resolved layouts and runtime operations as the faithful lane.
- Published exports retain an independently emitted faithful `Baseline_<PID>`. The inheritance projection is explicitly
  skipped; later projections that require an inherited state class remain skipped with their existing ledger reasons.
- `modernization/storage-composition.json` records source/baseline hashes, layouts, CALL sites and frontiers. It explicitly
  says `storage-composition-only`, `businessRulesExtracted=false`, and that generation does not establish oracle parity.
- Record-kit differential tests inspect either inherited storage or the composed aggregate. Kits are generated before
  domain partitioning so their sources and tests enter the correct Gradle modules; unassigned root sources/tests reject
  a partitioned export instead of being silently ignored.

Select **Generate composed storage** on Conversion before Publish, or use `composeStorage=true` on the workspace publish,
job export or estate export API. Use a separate publish target while this lane is experimental:

```powershell
dotnet run --project MlCpu\src\MlCpu.JavaBridge -- --file <prog.cbl> --compose-storage --spill-dir work\composed
Invoke-RestMethod -Method Post "http://localhost:5100/migrate/estate-java?jobs=POSTTRAN,INTCALC,TRANREPT&partition=false&composeStorage=true&exportDir=$PWD\work\composed-export"
dotnet run --project MlCpu\src\CobolConformance -- --suite TruthTable --compose-storage
dotnet run --project MlCpu\src\CobolConformance -- --suite GnuCobolParity --compose-storage
```

Verification for this increment: 7 new composition/protocol regressions plus 8 existing storage/runtime tests pass;
28 composed truth-table checks and 26 GnuCOBOL conformance comparisons pass; the default projection suite passes 18
checks. The composed three-program CardDemo Spring export passes 122 tests with and without domain partitioning,
including all six record-kit tests; the default partitioned lane also passes 122 tests. Its composed
POSTTRAN/INTCALC/TRANREPT jobs match 11/11 existing oracle captures. Wall-clock comparisons differ only in approved
clock-derived fields.

### Scoped runtime, typed rule slices and human implementation handoff

Composed publish now also requests `extractRules` from the sidecar. Eligible predicates and calculations become public
immutable input/result contracts and stateless generated implementations under `com.mlcpu.rules`, compiled against the
JDK only. The adapter reads current record values and applies results through the existing storage operations; skipped
sites, source IDs and hashes are recorded in the extraction ledger. CBTRN02C has 25 eligible slices and CBACT04C has 19.
Collation-sensitive, mixed-category and unsafe reads remain on the compatibility path; monthly-interest division remains
there because its truncation/SIZE ERROR semantics are not yet extracted.

`RunUnit` owns scoped registers, program registrations, EXTERNAL storage, configuration, clock, DDs, input, output and
resource tracking. It installs/restores the original static runtime ABI under a serialized compatibility bridge.
Concurrent requests can be isolated, but legacy execution is **not parallel**. Recursive calls restore LINKAGE bindings,
LOCAL-STORAGE, called status and control state. `ModernProgramViews` provides live overlapping BY REFERENCE views;
the older `byte[][]` ABI rejects unsafe overlaps. BY CONTENT buffers are private, and omitted views are explicit.
Runtime CANCEL resets retained storage or a registered modern factory; unsupported CALL forms are not made safe merely
by this runtime support, and the CALL inventory records their status.

Published projects include `modern-rules`, `rules-example` and `rules-conformance` modules plus `rule-handoff.md`,
contracts/cases and generated-file locks. Human implementations live outside generated roots and compile against the
public contracts without program state. Selection is process-pinned by the generated `RuleSelector`; explicit typed
constructor bindings are required for per-request selection. Build and job-start checks reject known stateful/ambient
providers, but these conservative checks are not a formal purity proof or an arbitrary-code sandbox.

Generated jobs accept `--mlcpu.strangler=OFF|LEGACY|MODERN|SHADOW` (default OFF). LEGACY selects frozen baselines;
MODERN selects generated programs; SHADOW stages both sides in separate run units, records/replays clock values,
compares ordered effects and storage/file images, and publishes only legacy outputs. Reports explicitly say
`promoted=false`. Normalized external-port implementations and uncertified custom rule selections are rejected by
SHADOW rather than executing duplicate live effects.

Current verification: 50 Java tests pass; the human handoff and job wiring pass 52 offline/end-to-end checks. The real
domain-partitioned CardDemo export passes 1,263 tests (1,141 rule-conformance tests), matches 11/11 captured oracle outputs,
and POSTTRAN's actual SHADOW run reports matching storage, effects and files with the expected return code 4.
The composed GnuCOBOL corpus still passes 26 comparisons and the truth tables pass 28 checks.

**The full plan is still gated:** individual L2 slices are not complete modernized capabilities. Unsupported lowering,
whole-workflow extraction, approved business naming/design changes and live forward-service rollout remain separate
gates. The JOBOL loop additionally requires readable modern Java and no regression: the architect's initial
`loanamorti` baseline is 34.7/C with `Lcle0100Batch` at 100/F, so parity alone does not complete modernization.
Generator-level restructuring of that separate `MlCpu.Modernize` batch template is tracked independently.

The architect has now accepted the first measured batch-template restructuring. Under JOBOL 1.0 the estate improved
from 34.7 to 1.7 and `Lcle0100Batch` from 100/F to 0/A, with no regression and GN4 7/7. JOBOL 1.1 adds stricter checks;
its new baseline `20261031-193948-bbd188` is 22.2/B (gate PASS), with the batch at 56/D. Subsequent iterations target typed
record accessors, enums, centralized overflow policy and cohesive loan-sequence state. The two rulesets cannot be
compared directly. GN4's ordered SYSOUT inclusion is not full byte parity, and the existing error-text gaps remain
documented rather than waived.

Under the same 1.1 ruleset, iterations 5/6 now pass the loanamorti gate at 1.7 with `Lcle0100Batch` and `Fsem0100Job`
at 0/A (41 findings resolved, none added). GN4 remains 7/7, and the new native error harness passes 15 scoped cases.
The iteration's architect review and the normal-SYSOUT heap-statistics exclusion remain pending. Fineract-xl's batch
template improves as well (GN4 46/46), but its estate gate still fails on the separate lift/calculation path.

An additional native MOVE suite verifies aliased multi-receiver evaluation in both emitted lanes. Shifted overlapping
raw copies with uncertified compiler-dependent behavior now produce an explicit frontier before any bytes are changed.

Iteration 7 is architect-accepted as batch-track complete (loanamorti 1.6/A; both fineract-xl batch steps A).
Tomrodgers approved the pinned statistics-only SYSOUT exclusion v2, hash
`faf2fc2c059ed3b204ed4b8e2c04bd1f987ad7d1b07c0ef5f2952f033474f958`; changing that scope requires renewed approval.
It does not waive a real 31-digit arithmetic divergence exposed by the new long-term error scenario. The error harness
still fails that case, and the separate lift/calculation track must preserve the known-site abend before claiming the
full fineract-xl gate or parity is green.

The lead has restricted work to fineract-xl loop 0 before any next estate. Native probes corrected the earlier
precision diagnosis: the limitation is FSJBDEC big-value capacity, not an arbitrary 31-digit limit. The per-site fix
is architect-accepted with the 1331/1332 boundary, 19/19 error cases and loanamorti GN4 7/7 without regression.
Fineract-xl lift quality and the separate per-loan big-value-store capacity evidence remain loop-0 gates.

Loop 0 is now lead-approved: fineract-xl is 15.0/B (111 findings resolved, none added), GN4 46/46,
loanamorti GN4 7/7, and the error harness 21/21. Store capacity is preserved by the 312/313 boundary probe,
and unchecked big-path operations fail closed. Regression baselines are
`20261031-220149-f58389` (fineract-xl) and `20261031-220151-e020cf` (loanamorti).
The next authorized loop is fineract-cobol's non-J3S Tier-1 shape, with fineract-cobol-cr as the independence check;
it must prove generic typed subprogram lifting and GN4 58/58 without regressing either approved baseline.

Loop 1's first milestone now emits all 16 typed CALL subprogram classes, removes the initial 87 residue sites and
31 javac errors, and passes GN4 58/58 on both the main and clean-room estates. JOBOL improves from 93.2/F to
55.4/D (`20261031-231144-affd92`) under the unchanged 1.1.0 ruleset, but still **FAILS** with the schedule batch
at 100/F. Both approved regression baselines remain unchanged and green. The architect accepted progress, not
completion. Next work proves JDK date/rounding/money replacements against retained lifted references with
differential and mutant gates, then improves typed function APIs, state ownership, file/status flow and reporting.
No next estate starts before Loop 1 approval; see [modernizeJavaCode.md](modernizeJavaCode.md) for the evidence.

Loop 1 is now architect-approved: the final main and clean-room estates measure **12.4/B, PASS**, with zero
residue, clean builds and GN4 58/58 each. The main audit `20261101-013204-d1d187` improves by 80.8 points against
iteration 0 without changing rules or hiding business logic in compatibility lanes. DateKernel and RoundingKernel
use typed JDK operations, verified against retained test-lane references over 21,058,849 and 2,082,656 cases
respectively, with zero differences and all seven mutants detected within the documented gate domains.
A follow-up corrected the rounding exclusion's premise: below digit count -9968, S/P legacy operations have
defined call-history-dependent storage, not undefined behavior. Those inputs are unreachable in the approved
estate, but public kernel equivalence there is unproven and originally lacked an explicit frontier. The architect confirmed
this estate-scoped approval and requires an explicit unsupported-input failure at both typed and adapter entries,
not a JDK answer or restored mutable history in modern code. The authorized Loop 2 must implement and test that
boundary and its own caller reachability, alongside fineract-legacy's 21 programs and 58
scenarios. All four approved estates are regression baselines. Capacity types, scratch dataflow and remaining
naming/flow improvements carry forward as generic passes, not as waived completion of the entire plan.

Loop 2 milestone 1 now measures **14.4/B, PASS** (`20261101-021443-15a70d`, change -28.5), with zero residue,
clean build/lint and GN4 58/58. Unsupported rounding inputs explicitly fail before JDK arithmetic at both typed
and adapter entries; supported-domain legacy-reference gates and all four regressions remain green.
The first review's approval was conditional on coverage and expanded parity. The corrected ledger has **406**
entries, not 410; 28 removed paragraphs are unreachable and four are inlined. Coverage over 698 legacy runs records
330 exercised entries, with unexecuted/partial business and error paths explicitly retained as residual risk.
Caller precision bounds are now derived, not merely observed.

Loop 2 is now **lead-approved under the user's diagnostic-preservation choice**: all 13 former failures,
**640/640 additional scenarios and 58/58 originals** pass strict parity without a new exclusion. The named, pinned
native-diagnostics profile lives solely at the compatibility lifecycle boundary; modern domain/batch classes
remain unchanged. Oracle probes establish first-successful-OPEN ordering (reopen retains rank) and warnings at any
run-unit end with files left open, including normal STOP RUN/GOBACK.

All five final regression baselines are green at zero delta. Approved v2 and `gn4err.sh` stay unchanged; replacing
v2's existing implicit-CLOSE filter with the profile would require a separate user decision. Residual business
coverage risks remain recorded. Termination-error cleanup is now hardened and architect-accepted: all releases are
attempted and errors preserved, with twelve persistent failure-path checks passing in each of three estates and
all five regressions unchanged. Authorized Loop 3 kontor-zins onboarding is verified: all 31 programs parse (99 call edges), and a
fresh GnuCOBOL rebuild matches all six references byte for byte, covering 249,964 records and 696 'X' rejects.
Fifteen additional pinned error/boundary fixture runs also match the original Java reference's output/counters and
explicitly mapped native abend semantics, without changing the six-scenario baseline. They are not yet modern GN4
results. Generator ownership has been handed off; Loop 3 iteration-0 generation/audit and modern parity wiring
are proceeding. Five isolated native/modern query fixtures additionally match exactly and cover five residual
ledger regions, but do not prove valid batch reachability or alter the batch-coverage counts. See
[modernizeJavaCode.md](modernizeJavaCode.md) for the baseline caveat, domains and exact remaining gates.

Loop 3 milestone 1 now measures **0.0/A, PASS**, with clean Java 21 build/lint, zero residue and **25 standard/domain
parity runs passing**. Generic JDK mapping and control-flow/temporary cleanups also preserve all five regressions.
The loop is **not complete**: payment-heavy probes expose a real FSJBDEC store-capacity abend 37 that modern pow
does not yet reproduce. The architect selected exact compatibility-boundary store accounting under the standing
preservation decision. Required MP/MT boundaries include 227/228 events at 360 months and 192/193 at 600 months,
with exact partial output/return-code/SYSOUT tests and a store-model-removal negative control. No new exclusion or
generic 31-digit precision cap substitutes for that behavior; Loop 4 waits for the complete gate.

The user subsequently approved retiring v2's implicit-CLOSE exclusion. **v2.1 is statistics-only**, pinned at
`7d7905e2067af80b3950dd93b7e87b3644ecd72388c1d3f10241bfbe20f4b7c6`, with the original statistics allowlist,
guard and END-position rule unchanged. Runtime warnings are compared everywhere, including the error harness.
Kontor-zins now passes **50/50** standard and capacity-fixture runs under this policy. Disabling the diagnostics
profile makes the seven warning-bearing loanamorti error cases fail as expected. Full five-estate validation is
running; exact store-model negative controls and final Loop 3 closeout are still pending.

Loop 3 is now **lead-approved**: exact decimal-store preservation passes **56/56** runs and 31 native-measured
capacity fixtures, with the required store-model negative controls. Its final audit is 0.0/A. A surviving carry-loop
mutant is recorded as a test-coverage gap; production contains the full checked retry loop, not a runtime frontier.

The stricter XL comparison uncovered older statistics-only SYSOUT gaps. The user explicitly approved separate
353-line FSGN/schedule and FSEM/event scopes, each pinned to its named step, exact pool names and END position.
Base v2.1 and the diagnostics profile remain unchanged; every business, abend and implicit-CLOSE line is compared.
The full XL strict rerun now passes **46/46**, with exactly 353 explicitly approved statistics lines per case and
all remaining SYSOUT, datasets and return codes equal. This closes the older gap; prior inclusion-only results
are not retrospectively presented as exact parity.

Authorized Loop 4 Joda-Money onboarding is verified: 65 programs, with fresh native outputs **6/6 byte-equal**
(12,845 records, 811 rejects) and LICENSE/NOTICE retained. Its generation/audit baseline is proceeding against six
anchors. Unreached traps, unproven capacities and untested APIs remain explicit scope risks, not waived semantics.
The statistics-only migration also passes loanamorti 7/7 plus 21/21 error paths and all three FN estates 58/58;
the native diagnostics profile and base v2.1 remain pinned unchanged.

Loop 4's Joda-Money milestone now builds cleanly with zero untranslated items and audits **1.5/A**.
All **39/39 runs pass strict native/reference parity** after the user's separate Joda-only 83-line statistics
policy was pinned (`811a128d0363aaa07bc9eaa9b937fd009a9496ecfb2b5a7aa9eafc48c68bf709`).
Every business, abend and implicit-CLOSE line remains compared. The detected compact-only decimal runtime's
working/result limits and job-output range failures are preserved at compatibility sites.
At that progress milestone, the final six-anchor execution checks and startup/exception fidelity scope review were pending; unreplayed APIs,
measured-only pool capacities and disclosed older unchecked-operation paths are not claimed proven. No Loop 5 has started.

**The five JOBOL loops (0-4) are now architect-approved.** Joda's final generation is **1.5/A**, builds cleanly
and passes **41/41 strict** runs; all six earlier estates were actually rerun with zero audit delta.
The strict checker, native capacity/add counterexample, startup/resource proofs and 23 mutation controls are verified.
There is no fncobolgen emulation class in Joda's main Java sources.

This completes the bounded five-loop programme, **not every forward-engineering or unsupported-semantics gate**.
Fineract-xl's 123 unchecked decimal operations and 106 unpreserved failure paths remain disclosed, as do replay,
pool-bound and residual coverage gaps. Seven final regression anchors are recorded in
[modernizeJavaCode.md](modernizeJavaCode.md). No next estate or generator/policy change is started; the user decides
the next work, and the pipeline inbox watcher stays active. Nothing has been committed.

The user has approved **Phase M**, original-guided generic quality rules. The lead compares against original Java
and will supply rule specifications; the generator itself must never read originals or use per-estate name/structure
hints. Each rule uses COBOL/IR/estate evidence, fails closed and is implemented one per milestone, with separate
train/test deltas, strict parity, JOBOL and all seven anchors unchanged.
The first rule, **R1 API-surface visibility, was rejected** by its pre-registered quality test: widened access fell,
but narrowed access rose and API fidelity declined on TRAIN and TEST. All seven behavior gates passed; that does not
make the quality result acceptable. R1 stays opt-in only, with default generation byte-identical to the final anchors.
Recovering external API intent needs evidence outside the closed call graph, such as a future business API manifest.

An important pre-existing limit is now explicit: J3S class/package/method names come from CLASS/FRAME provenance
headers quoting original Java. J3S name/structure fidelity is therefore not proof of header-free COBOL generalization.
New rule decisions must not use those comments or original sources; this underlying model reliance remains disclosed.
**R3 final classes only is accepted as opt-in under an explicit `closedWorld: true` deployment declaration**.
G23 falls TRAIN 20 to 3 and TEST 7 to 0 with unchanged fidelity and all seven gates green, but 22 added finals are stricter
than the originals. They are permitted only when the owner declares no external subclassing/proxying; default stays
off. The explicit `project.json` deployment-profile gate is now architect-accepted: both `closedWorld: true` and
the selected rule are required, invalid/unauthorized input fails before generation, and authorization is recorded.
All 96 self-tests and 36 CLI-negative checks pass; no actual estate declaration was changed.

**R5 program-to-interfaces was rejected**: G11 improved on TRAIN only (13 to 12), TEST stayed at 3 and G13 rose.
All seven behavior gates passed, but the quality criterion failed; R5 remains opt-in/off and default output unchanged.

The key risk is broader than names: existing FRAME comments also supply parameter types and synthesized interfaces,
and generated Javadoc repeats their Java signatures. Those provenance-assisted results do not demonstrate recovery
from ordinary legacy COBOL. **A0 provenance ablation is now measuring the bias**, using scratch copies with provenance
comments stripped and active code byte-identical. No generator fix, new rule or policy change is part of A0.
Lift/build/parity/fidelity failures will be reported, not repaired or counted as success. R2/R4 and CardDemo wait
for the lead's next decision; CardDemo still requires explicit user authorization.

## Layout

```
MlCpu/                 C# (.NET 10): MlCpu.Core, MlCpu.CobolKb, MlCpu.CobolKb.Knowledge (JCL harvester, KB, DdBindingPlanner,
                       ReplayPlanBuilder, RequirementsPacketBuilder, NormalizedCapabilities), MlCpu.Cobol.Analysis (incl. Projections/),
                       MlCpu.JavaBridge (CLI), MlCpu.Host (Blazor), MlCpu.DesignModel (local models: design + implementation),
                       CobolConformance (gate + corpus/)
java-sidecar/          Java 21: com.mlcpu.sidecar (HTTP :8079, emitter, compiler) + com.mlcpu.rt (runtime, JobEngine, StepRunner, DataPrep,
                       SortReplay, RecordCompare, TruthRunner, StepEquivalence)
capabilities/carddemo/ normalized capabilities: capability-manifest.json, normalization/<PID>/ (requirements, design, design-conformance),
                       evidence/, src/ (com.mlcpu.carddemo platform/domain/data + com.mlcpu.capabilities.*)
tools/Start-Sidecar.ps1
tools/carddemo/Invoke-CardDemoParity.ps1   GnuCOBOL oracle vs generated Java (per step)
tools/carddemo/Invoke-SpringJobParity.ps1  exported Spring jobs vs the same oracle outputs (run after the above); [-Estate <dir>]
tools/Collect-CapabilityEvidence.ps1       promotion evidence for the normalized capabilities (equivalence, parity, JaCoCo, PIT)
tools/Invoke-ModelCapabilityRuns.ps1       local models design and implement each capability; judged by the equivalence tests
tools/New-WorkedExamples.ps1               normalization/<PID>/examples.md traced from the faithful program (generators in tools/examples/)
tools/Test-ModelService.ps1                a model's code for every program as one service: full test suite + jobs vs the GnuCOBOL oracle
tools/profiles/*.profile.json              overrides for a known estate, matched by detection files (CardDemo: layout, script oracle, authored capabilities)
tools/antlr/                               ANTLR 4.13.1 tool (regenerates MlCpu.Cobol.Antlr/Generated from Grammar/Cobol85.g4)
tools/Restart-Host.ps1                     stop the host on :5100 and rebuild it
MlCpu/src/MlCpu.Cobol.Antlr/               grammars-v4 COBOL-85 parser (generated C#), ProLeap-style preprocessing, AST JSON and outline
MlCpu/src/MlCpu.Host/Services/Workspace/   projects, onboarding, GnuCOBOL oracle (cached), graphs, conversion, capability kit, job parity, Gemma
capabilities/platform/                     the platform layer for generated capability kits (com.mlcpu.platform: datasets incl. variable-length
                                           and print files, field codecs incl. packed/binary) and the generic parity notes
projects/<id>/                             project.json (tracked), scenarios/ (tracked); source/, oracle/, graphs/, transpile/, capabilities/,
                                           publish/, runs/, gemma/ are generated and git-ignored
```

## Run

```powershell
powershell -ExecutionPolicy Bypass -File tools\Start-Sidecar.ps1                       # 127.0.0.1:8079
dotnet run --project MlCpu\src\MlCpu.Host --urls http://localhost:5100                 # Blazor UI + /health, /migrate/job-java/{job}
powershell -ExecutionPolicy Bypass -File tools\carddemo\Invoke-CardDemoParity.ps1      # full conversion + parity
dotnet run --project MlCpu\src\MlCpu.JavaBridge -- --file <prog.cbl> --spill-dir <dir> # one program
dotnet run --project MlCpu\src\MlCpu.JavaBridge -- --kb work\kb-site --estate ..\carddemo\app   # KB: jobs.jsonl, programs.jsonl
Invoke-RestMethod -Method Post "http://localhost:5100/migrate/job-java/TRANREPT?exportDir=$PWD\work\export"   # Spring project
Invoke-RestMethod -Method Post "http://localhost:5100/migrate/estate-java?jobs=POSTTRAN,INTCALC,TRANREPT&partition=true&exportDir=$PWD\work\export"
powershell -ExecutionPolicy Bypass -File tools\carddemo\Invoke-SpringJobParity.ps1    # run exported jobs vs oracle (+ wall-clock RecordCompare)
dotnet run --project MlCpu\src\MlCpu.JavaBridge -- --replay-plan POSTTRAN --estate ..\carddemo\app --out work\dataplan\POSTTRAN
dotnet run --project MlCpu\src\CobolConformance                                       # conformance gate (sidecar up; GnuCOBOL for parity)
cd java-sidecar; gradle test                                                            # runtime golden vectors
dotnet run --project MlCpu\src\MlCpu.JavaBridge -- --requirements POSTTRAN,INTCALC,TRANREPT --estate ..\carddemo\app --out capabilities\carddemo\normalization
Invoke-RestMethod -Method Post "http://localhost:5100/migrate/estate-java?jobs=POSTTRAN,INTCALC,TRANREPT&partition=false&name=carddemo-normalized&exportDir=$PWD\work\export"
powershell -ExecutionPolicy Bypass -File tools\Collect-CapabilityEvidence.ps1          # evidence/<PID>.evidence.json (after the export above)
powershell -ExecutionPolicy Bypass -File tools\Invoke-ModelCapabilityRuns.ps1          # local models -> ..\modelDesignsAndCodeOutput\<model>\<PID>
dotnet run --project MlCpu\src\MlCpu.DesignModel -- design --packet capabilities\carddemo\normalization\CBTRN02C\requirements.json --out work\design\CBTRN02C\design.json
powershell -ExecutionPolicy Bypass -File tools\New-WorkedExamples.ps1 -Program CBTRN03C   # worked examples (after the export above)
powershell -ExecutionPolicy Bypass -File tools\Test-ModelService.ps1 -ModelOutput ..\modelDesignsAndCodeOutput\gemma-4-31B-it-full-service
```

Workspace API (each POST starts a background operation and returns it; poll `GET /api/operations/{id}`):

```powershell
$h = "http://localhost:5100/api"
Invoke-RestMethod -Method Post "$h/projects/onboard?name=zopeneditor-sample&source=C:\Users\Tomrodgers\Source\zopeneditor-sample-main"
Invoke-RestMethod -Method Post "$h/projects/zopeneditor-sample/activate"
Invoke-RestMethod -Method Post "$h/projects/zopeneditor-sample/oracle"            # ?force=true recompiles despite the cache
Invoke-RestMethod -Method Post "$h/projects/zopeneditor-sample/graphs"
Invoke-RestMethod -Method Post "$h/projects/zopeneditor-sample/transpile"
Invoke-RestMethod -Method Post "$h/projects/zopeneditor-sample/publish"           # ?dir=<target>
Invoke-RestMethod -Method Post "$h/projects/zopeneditor-sample/test"                # ?tests=false: build and job checks only
Invoke-RestMethod -Method Post "$h/projects/zopeneditor-sample/gemma/design?programs=SAM1,SAM2&outline=true"
Invoke-RestMethod -Method Post "$h/projects/zopeneditor-sample/gemma/publish?programs=SAM1,SAM2&attempts=15"
Invoke-RestMethod -Method Post "$h/projects/zopeneditor-sample/gemma/test"
Invoke-RestMethod "$h/operations?project=zopeneditor-sample"
dotnet run --project MlCpu\src\MlCpu.JavaBridge -- --ast <file|dir> --out <dir>     # COBOL-85 parse: <PID>.ast.json + <PID>.outline.txt
```

The active project decides the estate roots (code, copybooks, JCL, PROCs, control cards); without one, `MlCpu:CorpusRoot` in
`MlCpu.Host/appsettings.json` points at the estate (default `../carddemo/app`). `MlCpu:WorkspaceRoot` (default `projects/`),
`GemmaModel` and `LlamaDir` can be set there or in `%LOCALAPPDATA%\MlCpu\settings.json` (the Settings page writes it).

## Architecture decisions (made as SME/architect; deviations from the spec are deliberate and listed)

1. **Byte-backed storage.** Every 01/77 record and FD area is a byte `Area`; data items are `CobField` views that decode and encode by USAGE.
   - The spec's typed-field model plus `image()` is replaced, because record bytes are always authoritative (I9).
   - REDEFINES, group moves, READ INTO and file images are therefore exact by construction.
   - The `State_*`/typed-facade projections (§8, steps 3–10) are **not applied**; `projection-ledger.json` in every published project says so.
2. **Semantic tree in the IR.** C# parses the COBOL once and exports IR v1.2: the flat statements with guards (§7.1), plus a resolved semantic tree (`mlcpu.semantic-tree.v1`) with typed operands.
   - The sidecar renders from that tree and never parses COBOL (I3).
   - The spec's `mlcpu.semantic-ast.v1` and the LIR route are not reproduced.
3. **Faithful or abstain.** Anything that can't be lowered becomes a frontier comment plus `CobRt.frontier(...)`, which stops the run if reached.
   - SEARCH is abstained whole, so its bodies are never emitted unguarded (fixes D1).
4. **Reference output.** CardDemo ships inputs but no captured outputs, so the parity reference is GnuCOBOL 3.3 running the original source (§5.6.5).
   - Flags: `-std=ibm -fsign=EBCDIC`, with `COB_CURRENT_DATE` pinned to the hundredth of a second.
   - The Java side runs with `MLCPU_DISPLAY_NUMERIC=gnucobol` and `MLCPU_BINARY_TRUNCATE=no`, matching the reference's dialect.
   - The runtime's defaults stay z/OS-style (Appendix A).
5. **Utilities** (IDCAMS REPRO, DFSORT SORT/INCLUDE) are `DataPrep` and `SortReplay`. Both pipelines use the same tools, so only the converted programs differ.
6. **Host.** Per your decision, `MlCpu.Host` is Blazor Server; it also exposes the headless endpoints from §10.
7. **Program graphs** (`ProgramGraphBuilder`, §5.4.4) are built over the semantic tree:
   - a statement CFG and a paragraph call graph (perform, perform-thru, goto, alter, fallthrough), with PERFORM-context-sensitive reachability;
   - control dependences, and data dependences from reaching definitions over **byte ranges per storage area**, so group, REDEFINES and FD-record overlaps are honoured;
   - dead paragraphs, CALL sites (dynamic targets are closed by reaching definitions), and the estate call graph (`EstateCallGraph`).
   - They are exported in the IR as `graph`, by `JavaBridge --graph <dir> --estate <code root>`, and on the UI's *Graphs* page. The dataflow converges in at most 5 passes on CardDemo (the cap of 50 raises a frontier).
8. **JCL harvester** (`JclHarvester`, Knowledge project):
   - handles continuations, in-stream data, `SET` symbols and `IF/THEN/ELSE` depth (steps inside are marked conditional);
   - expands in-stream and cataloged PROCs: defaults first, then caller overrides; nested PROCs; a cycle guard; `PROCSTEP.DD` overrides and added DDs; `&SYM` substitution;
   - records each DD's direction (consumer/producer/sysout/instream), GDG generation, member, DUMMY, and DCB LRECL;
   - each step keeps its citation and invocation chain (e.g. `PROC REPROC > TRANREPT.jcl:23`).
   - `KnowledgeBase.Build` writes `kb/jobs.jsonl`, `programs.jsonl` (calls, files, jobs, upstream/downstream lineage) and `meta.json`. On CardDemo it finds 38 jobs, 101 steps and 31 programs.
9. **Paragraph partitioning** (`BulkPartitionPlanner`, §8 / Q12):
   - Each paragraph gets one effect class. Impurity wins, in the order SQL > CICS > I/O > CALL > console > time > orchestration > domain.
   - Domain paragraphs are clustered by the alias groups they write. Output is `partition-plan/<PID>.json` plus `summary.csv`.
   - Finding: CardDemo's batch leaves all mix I/O with logic, so CBTRN02C, CBACT04C and CBTRN03C have **no pure-domain paragraphs**. Extracting domain services needs paragraph splitting (projection 5), not just clustering.
10. **Characterisation tests** (§8.12):
    - `CharacterizationSeedBuilder` seeds every reachable paragraph whose PERFORM closure is free of file, SQL and CICS effects.
    - Seeds are PIC-derived (zeros, ones, boundary-high, boundary-low, smallest-unit, distinct-markers), plus up to 20 reach seeds taken from condition literals. Duplicates are removed.
    - Inputs are the closure's upward-exposed reads.
    - The generated JUnit test runs the paragraph on `Baseline_<PID>` (frozen transliteration) and `Prog_<PID>` (the refactoring target) through `runParagraph`. It asserts that storage, DISPLAY output and outcome are equal, and pins them to `characterization/<PID>.golden`.
11. **Spring project** (`JavaProjectAssembler`): `<job>-spring` uses Spring Boot 3.3.4 (non-web).
    - `JobRunner` (an ApplicationRunner, whose exit code is MAXCC) runs the generated `JobDefinition`. That definition has a typed `StepId` enum, the harvested steps and DDs, and a program map with no reflection.
    - It runs on the runtime's `JobEngine`: converted programs, SORT (SYMNAMES), IDCAMS REPRO/DELETE/DEFINE, IEBGENER and IEFBR14.
    - Datasets are files named by DSN (a GDG maps to its base name). Control-card members come from `ctl/`.
    - Settings are `mlcpu.*` properties. COND is logged but not evaluated, and steps inside JCL `IF` are skipped.
    - Several jobs can go into one project (`/migrate/estate-java`); `--mlcpu.job=<JOB>` selects the job.
12. **Data layer** (§5.5.2, §5.6.4):
    - `DdBindingPlanner` binds each step's program call closure (SELECT/ASSIGN, device prefixes stripped) to its DDs. Statuses are Bound, Dummy, NoDdInStep, UnresolvedSymbolic, NoLayout, MissingCopybook and LengthMismatch (the JCL LRECL vs the FD record length, +4 for V). Each binding records the FD layout, the direction from OPEN, and citations.
    - `ReplayPlanBuilder` adds leaf layouts per file; per output, the written shapes (WRITE FROM operand, role HDR/DTL/TLR); writers; and **volatile fields**. These come from a byte-range taint pass seeded by `FUNCTION CURRENT-DATE` and `ACCEPT FROM DATE|TIME|DAY`, carried through byte-mapped MOVEs, arithmetic, STRING/UNSTRING and WRITE FROM.
    - Each project gets `DdBindingPlan.json` and `replay/ReplayPlan.json` (`-<JOB>` suffix in estate projects). `com.mlcpu.rt.RecordCompare` reads them and writes `mlcpu.record-comparison.v1`.
13. **Projections 2–10** (`MlCpu.Cobol.Analysis/Projections`, §8, Q13) are textual rewrites with brace matching (no Java AST). Each gates itself, records verbatim skip reasons, and writes its ledger (§7.11). `projections.json` summarises them.
    - **2 Composition.** Typed entity records for the shapes each program reads INTO / writes FROM (else FD records), with byte codecs that reuse the core's exact `CobField` factories, plus round-trip tests. Also `usecases/<PID>Command|Result` and a `services/<PID>Service` that runs the faithful core and refuses unbound DDs. The gate refuses OCCURS, REDEFINES, edited and floating leaves.
    - **3 State split.** `Prog_<PID> extends State_<PID>`. Areas, views, files, `_init`, `_linkage`, `_fields` and the constants move; privates become protected.
    - **4 Shared copybooks.** A copybook record with an identical layout in two or more programs becomes `core/copybook/Storage_<CPY>_<RECORD>`. The state classes alias its views and keep their own field names; conflicting revisions get a SHA suffix. On the CardDemo estate this gives 5 shared types.
    - **5 Domain clusters.** `domain-candidates/<PID>.json` with the Q12 `CandidateHash`, and a fail-closed `DomainPromotionPolicy` manifest. A cluster approved by evidence or by `CertifiedDomainClusterCatalog` moves verbatim into an inner class; a body that mentions `this` is refused.
    - **6 Certified calculations.** A closed paragraph in `CertifiedCalculationCatalog` becomes `capabilities.<pkg>.<Cap>Calculator`: typed Input/Result records, its own storage, and the generated body verbatim, so the arithmetic is the core's. A `*DifferentialTest` compares it with `Baseline_<PID>` over the catalog vectors. A `ShadowReady` slice also runs both at run time and throws on drift. A changed semantic fingerprint or contract is refused and recorded in `calculation-projection-rejections.json`.
    - **7 Orchestration.** Candidates carry the Q12 hash (Java method names excluded). An approved, representable entry (one that touches no program storage once PERFORMs and conditions become port calls) moves to `capabilities.<pkg>.<Workflow>` over a typed `Port`; the program implements the port. The policy needs scenarios, transcript and state equality, coverage, mutation and SME approval.
    - **8 Facades.** `capabilities/<pkg>/<Workflow>` over the typed service, plus tests. Names come from `ProgramSemanticNameCatalog`: CardDemo entries written in the SME role from program headers and JCL, **awaiting human confirmation**.
    - **9 Capability packages.** Implementations move to `capabilities/<pkg>/legacy/{Prog,State,Baseline}_<PID>`; `com.mlcpu.core.Prog_<PID>` stays as the compatibility entry point.
    - **10 Domain partition** (estate projects only). Gradle modules `estate-common`, `estate-app` and `domain-<id>`, assigned by the catalog's taxonomy. Shared or cross-reached programs go to common. Cross-domain CALLs stay name-resolved at run time and are recorded as seams.
    - **Outcome on CardDemo.** 2, 3, 4, 8, 9 and 10 apply. 5, 6 and 7 fail closed: there is no promotion evidence or certified catalog entry, and every CardDemo entry paragraph moves data directly. The conformance corpus (`projection/CALCDEMO.cbl`) exercises 5, 6 and 7 applied, with fixture catalogs and evidence, and runs their generated tests.
14. **Conformance gate** (§11.1, `MlCpu/src/CobolConformance`). An executable harness that prints PASS/FAIL/BLOCKED and exits 1 on FAIL or 3 when only BLOCKED. A missing sidecar, JDK or GnuCOBOL blocks its lane; it never passes.
    - The corpus has `manifest.tsv` (accept/xfail), `lower-manifest.tsv` (mustContain / mustNotFrontier / mustFrontier), `truth-manifest.tsv` (cases `in=v;…=>expected` run through `TruthRunner` on the generated Java), `parity/` (DISPLAY output vs `cobc -std=ibm -fsign=EBCDIC`), `job-control/` (conditional, in-stream DATA/DLM, nested cataloged PROCs, and a job that produces every DD-binding status) and `projection/`.
    - It found a real defect, now fixed: `DD DATA,DLM=` was not honoured, so `//` data lines ended the in-stream block.
15. **Normalized capabilities** (`capabilities/carddemo`, `NormalizedCapabilities`). Idiomatic Java for CBTRN02C, CBACT04C and CBTRN03C, next to the faithful programs.
    - `JavaBridge --requirements` writes a hashed requirements packet per program (`mlcpu.requirements-packet.v1`). A local model designs from it (`mlcpu.capability-design.v1`). The Java implements the design on a small platform layer (`com.mlcpu.carddemo.platform`).
    - `design-conformance.json` traces every design element, rule and quirk to code and test scenarios. It also records deviations, implementation defects and SME findings.
    - `*EquivalenceTest` runs the normalized step against the faithful program in every scenario: all datasets byte for byte, SYSOUT and the return code. They are also compared with the GnuCOBOL oracle.
    - Becoming the default needs evidence and an SME approval, both pinned to the requirements and source hashes. The evidence covers equivalence, oracle parity, coverage (≥ 90% line, ≥ 80% branch) and mutation score (≥ 80%). Evidence is current (2026-10-06). No approvals exist yet, so the faithful programs stay the default.
    - Defect X-01, fixed 2026-10-06: account rewrites re-encoded every numeric field, so positive amounts stored unsigned (F-sign) came back signed. Rewrites now touch only the fields the program assigns. The F-sign scenarios guard the fix in both directions.
16. **Local models** (`MlCpu.DesignModel`, `tools/Invoke-ModelCapabilityRuns.ps1`).
    - The backend is llama.cpp `llama-server`: OpenAI-compatible API, streamed, prompt cache, memory-mapped weights, MoE experts placed by `--fit`. LM-Kit is the fallback when the server cannot start or a request fails.
    - `design` validates the design against the packet: every rule, quirk and output.
    - `implement` has the model write the whole capability on the platform layer only. A harness (the export minus the authored classes) compiles it and runs the program's equivalence test. Compiler errors and failing scenarios go back to the model. The best attempt is kept.
    - `run` does both for several programs with one model load. The output goes to `..\modelDesignsAndCodeOutput\<model>\<PID>\`, with a comparison in its `README.md`.
    - Guidance (`--with-examples`, or `--examples`/`--notes`): `normalization/<PID>/examples.md` and `normalization/parity-notes.md` go into the implementation prompt.
      - The examples are traced from the faithful program on inputs that are not acceptance scenarios. The generators check every claim against the output before writing it.
      - The parity notes are general COBOL storage rules: storage default, INITIALIZE skipping FILLER, MOVE padding, REWRITE of assigned fields only, truncation, and abends skipping CLOSE.
    - Feedback after a failing run lists the failing scenarios with the first difference per output. `MlcpuDiffProbe` then adds every differing region of every output for the first failing scenario: it reuses the acceptance test's own `scenarios()`, `step()` and setup.
    - **Result:** Gemma 4 31B (BF16, MTP draft) wrote the whole normalized service, with all three designs valid on the first attempt.
      - CBTRN02C passed 23/23 on attempt 4, CBACT04C 17/17 on attempt 7, and CBTRN03C 23/23 on attempt 15.
      - Assembled by `Test-ModelService.ps1`: 116/116 tests pass and 11/11 job outputs match the GnuCOBOL oracle, with every program step in Gemma's code.
      - Without the guidance, the best local result was 3/23 (`..\modelDesignsAndCodeOutput\README.md`).

17. **Projects.** A project is a directory under the workspace (`projects/<id>/project.json`, `mlcpu.project.v1`).
    - Onboarding clones a git repository (or URL) or copies a directory into `source/`, so the oracle, graphs and publishes never touch the origin.
    - The scan classifies members by content and extension. Programs are keyed by PROGRAM-ID: the member named like it wins, then the shallower path (zopeneditor: `COBOL/SAM1.cbl` over `multiroot/sam/SAM1.cbl` and `COBOL/SAM1LIB.cbl`, which also declares SAM1).
    - Program kinds come from the harvested JCL (a step runs it: batch) and the CALL closure, dynamic targets included (only CALLed: subprogram); EXEC CICS makes it online.
    - The oracle's jobs are the JCL jobs that run an estate batch program. Their initial datasets are the DSNs a step reads before any step writes them. Each one is shaped by the reading FD (INDEXED → ksds with the RECORD KEY offsets, RECORDING V / VARYING → variable, LINE SEQUENTIAL → line, else fixed) and filled from the data file whose name qualifiers appear in the DSN (`SAMPLE.CUSTFILE.txt` → `IBMUSER.SAMPLE.CUSTFILE`).
    - A profile (`tools/profiles/*.profile.json`, matched by detection files) overrides any of it: CardDemo keeps its proven script oracle and its authored capabilities.
    - `project.json` can be edited by hand; the host re-reads it when it changes.
18. **GnuCOBOL oracle, built once** (`GnuCobolOracle`). One executable per main program (plus a PARM driver when the step passes a PARM and the program has USING), with its CALL closure and a CEE3ABD stub, compiled with the project's cobc flags and copybook roots.
    - `oracle/bin/build-manifest.json` keys each executable by a SHA-256 of its sources, every copybook it expands, the stubs, the flags and the cobc version; a run rebuilds only what changed. The KSDS load/unload utilities are cached the same way. The CardDemo harness got the same cache and a `-Work` directory per project.
    - Jobs run in order on one data directory: COBOL steps on GnuCOBOL (INDEXED datasets loaded into the file handler before the step and unloaded after it), utilities with the job engine's semantics (IEFBR14 and compile/link steps as no-ops, IEBGENER, IDCAMS REPRO/DELETE/DEFINE, SORT through `SortReplay`). Datasets are files named by DSN, SYSOUT is `<STEPID>.SYSOUT.txt`, exactly as the Java side names them.
    - Each run keeps a snapshot per job (`jobs/<JOB>/in`, `out`) and per step (`steps/<JOB>.<STEP>/in`, `out`, `step.json`). The job snapshots become the job checks; the step snapshots become the capability tests' resources. Scenarios (`scenarios/<name>/`) override source files by name.
    - Each program is compiled once to an object (`oracle/bin/obj`, cached by its own content hash, in parallel) and each executable links the objects it needs. SYSOUT is kept per job. `oracle.scenarioJobs` (pattern → jobs) lets a scenario run only the jobs it is for; `reference.scenarioMap` may name several folders separated by `;` (docs/PHASE5.md).
19. **Graphs** (`GraphService`, `MlCpu.Cobol.Antlr`).
    - The AST comes from the grammars-v4 COBOL-85 grammar (ProLeap; ANTLR 4.13.1, C# target, generated code checked in). The input is the analyser's own front end (stripping, continuations, COPY … REPLACING), adjusted to the ProLeap preprocessor's contract: comment entries tagged `*>CE`, EXEC blocks `*>EXECSQL`/`*>EXECCICS`, separator commas made spaces.
    - It parses SLL first and falls back to LL with error recovery, with a time bound. On CardDemo all 14 batch programs parse cleanly (20–1,300 ms each); the 17 online ones fail only on the IBM-supplied CICS copybooks (DFHAID, DFHBMSCA) the repository does not ship.
    - The grammar carries local extensions (`docs/AST-GRAMMAR.md`, regenerated with `tools/Generate-CobolParser.ps1`): the IBM Enterprise COBOL / COBOL 2002 constructs of legacy IBM code (88 `WHEN SET TO FALSE`, `EXIT PERFORM [CYCLE]`, `EXIT PARAGRAPH`, `EXIT SECTION`, the 2002 `INITIALIZE` phrases, `RELEASE … FROM` literal) and identifier rules SLL can predict, so every program of the reference estates parses in SLL alone (the 132K-line J3S program in about 3 s; full LL took 6 minutes). When full LL runs out of time on a program with a syntax error, SLL with error recovery supplies the tree (mode `SLL-recovery`, never Ok, the outline says so). The conformance gate's `ParserAst` suite holds it.
    - The AST JSON collapses single-child rule chains. The outline renders the data division (levels, PICTURE, USAGE, OCCURS, REDEFINES and VALUE clauses) and the procedure division (paragraphs, nested IF/ELSE, EVALUATE/WHEN, AT END, inline PERFORM), with program line numbers and literals untouched.
    - The DDG JSON (`mlcpu.ddg.v2`: statements, fields, edges as `[from, to, field]` indexes) is the reaching-definitions graph over byte ranges. The call graph JSON holds jobs → programs (EXEC), programs → programs (CALL) and, per program, paragraphs → paragraphs. The PDG is held in memory per program.
    - Two front-end defects surfaced and were fixed: `COPY 'NAME'` (quoted) and a COPY whose period is on the next line were not expanded.
20. **The capabilities layer for any estate** (`CapabilityKitBuilder`). CardDemo's capabilities are authored. For any other estate, publish generates them per program:
    - a placeholder entry class that runs the faithful program: `JobEngine.runFaithful` for a step, `ProgramRegistry.callFaithful` for a subprogram;
    - an equivalence test over every oracle scenario, plus record-level variations of each input (empty, first record only, reversed, duplicated), normalized vs faithful, and normalized vs the GnuCOBOL oracle with the return code;
    - worked examples decoded field by field from the oracle's step snapshots, or for a subprogram every CALL's arguments before and after (`com.mlcpu.rt.CallTrace`);
    - the AST outline, the manifest (kind, package, host step), the platform layer and the parity notes.
    - Subprograms are first-class: `ModernProgram` (`call(byte[][])`, arguments BY REFERENCE), registered as CALL targets when the job selects normalized (`JobEngine.MODERN_PROGRAMS`, `JobDefinition.modernPrograms()`), and judged with `StepEquivalence.compareCall`. A normalized step can CALL through `ctx.call(...)`.
    - The published project carries the whole kit: requirements packets, design schema, AST outlines, examples, parity notes, graphs and `MODERNIZATION.md`.
21. **Gemma in the workbench** (`GemmaService`, `MlCpu.DesignModel design-all | implement-all`).
    - One model load serves every chosen program. With Gemma's MTP draft model present, llama-server decodes speculatively (about 60 instead of 22 tokens/s).
    - The AST outline goes into the design and implementation prompts only when the prompt and a full reply still fit the context.
    - The implementation contract follows the manifest: step or subprogram, and the platform package. The diff probe uses a test's `subprograms()`.
    - Gemma's best attempt per program replaces the capability's classes in `gemma/service/`, which is then tested like the published estate with `--mlcpu.implementation=normalized`.
22. **Pipeline v2: generate what the data division decides, and say precisely what differs** (`tools/eval/PIPELINE-V2.md`).
    - **Record kit** (`RecordKitBuilder`, at publish, every estate): per capability, `<package>.records` holds a class per record area the program does I/O with (FD records; WORKING-STORAGE records used by READ INTO, WRITE/REWRITE FROM, a MOVE to or from an FD record, CALL USING; LINKAGE records). Each class is a view over the record's bytes with the VALUE clauses applied and typed accessors that store with COBOL MOVE rules (padding, truncation, sign, zoned/packed/binary, numeric editing); OCCURS subscripts are not bounds-checked, as in COBOL. Report lines are such classes, so a line starts VALUE-initialised and formats its edited items. A class per FD carries the DD, lengths, keys and the WRITE … FROM image. Copybooks used under several prefixes (COPY … REPLACING) share one class. `Pic` (`RecordKit/Pic.java`) holds the rules, ported from the runtime's `CobField`, `Edit` and `CobRt`.
    - `RecordKitTest` (generated) proves every class against the faithful program's own storage: each area's initial image under both storage defaults, and every accessor's MOVE and decode over the same bytes. The prompt gets `normalization/<PID>/record-kit.md`; the requirements packet and its hash are unchanged.
    - **Feedback** (`MlCpu.DesignModel`: `Probe`, `FeedbackRenderer`): the probe runs up to three failing scenarios with different symptoms and dumps both runs' outputs, the faithful run's I/O and DISPLAY effects with the paragraphs on the stack (`CobFile.TRACE`, `CobRt.DISPLAY_TRACE`), and a subprogram's CALL arguments. Records are aligned (by key for indexed files), matched to their layout (`record-layouts.json`; print lines by their VALUE text) and compared by COBOL field name with both values decoded and hints (truncation, rounding, sign encoding, unassigned bytes, editing). "Where the runs part" names the faithful statement, PERFORM chain and rule that wrote the first differing record, the input records just read, and the statement that writes what the model wrote instead.
    - **Repairs**: SEARCH/REPLACE edit blocks or whole files; a syntax error shows its numbered source lines once (cascades are summarized), and an error about a platform or kit class adds that class's public API. A reference to another subpackage of the capability (such as the faithful `legacy` program) breaks the contract.
    - **Pattern library** (`--patterns dir [--embeddings url]`): a program that passes leaves its verified code; a later program gets the most similar other program's pattern (BM25 over packet features, or cosine over an OpenAI-compatible embeddings endpoint such as EmbeddingGemma). Off in the evaluation.
    - Diagnostics: `MlCpu.DesignModel replay` (the v2 feedback for a recorded run's attempt), `probe`, `patterns`.

## Answers-document defects

| Id | Status in this implementation |
|---|---|
| D1 SEARCH bodies unguarded | fixed: SEARCH is a whole-statement frontier |
| D2 ACCEPT DATE YYYYMMDD | fixed (`CobRt.accept`) |
| D3 PIC truncated at '.' | fixed: the PICTURE is taken as a whole word |
| D4 DIVIDE REMAINDER | fixed: remainder = dividend − stored quotient × divisor |
| D5 floating insertion | fixed (`Edit.format`, golden tests) |
| D6 JUSTIFIED / BLANK WHEN ZERO | implemented in MOVE and editing |
| D7 high-order truncation | fixed: numeric stores truncate to the PICTURE |
| D8 trailing sign when de-editing | fixed (`Edit.deEdit`) |

## Not done yet

- **Spec phases not implemented:**
  - ANTLR data-division cross-check: the COBOL-85 parse now runs for every program (AST, outline), but its data division is not yet compared item by item with the analyser's layouts;
  - `SyntheticDatasetGenerator` and `MainframeBaselineCapture` (synthetic inputs, lower-region capture/replay);
  - promotion evidence producers (coverage and mutation runs), so projections 5–7 apply only from certified catalogs or supplied evidence;
  - the spec's full suite list (§11.1). The gate covers the areas above; the C# transpiler, LIR and facts suites do not apply to this architecture.
- **Reviews pending (ASK-7):** the CardDemo `ProgramSemanticNameCatalog` entries and the domain taxonomy were written in the SME role and need human confirmation; the certified catalogs are empty. The normalized capabilities have no SME approvals (`capabilities/carddemo/approvals/`), and their `design-conformance.json` records await human confirmation.
- **Graph approximations:** control dependence is structural (per block), not post-dominator based; PERFORM returns in the dataflow are context-insensitive (conservative).
- **JobEngine:** COND codes are not evaluated, GDG generations are not versioned (they map to the base name), and VSAM AIX/PATH/BLDINDEX are no-ops because alternate keys are served from the base file.
- **Online CardDemo:** the CICS programs (`CO*`) are out of scope; their EXEC CICS statements become frontiers. Their COBOL-85 parse reports the IBM CICS copybooks (DFHAID, DFHBMSCA) as missing.
- **Generic oracle limits:** JCL COND is not evaluated (as in the job engine); a CALL to a program outside the estate other than CEE3ABD fails the build; data files are matched to DSNs by name qualifiers, and a dataset without a match starts empty (noted in `project.json`). Input files are treated as text lines unless they contain no line feeds.
- **Requirements packet gap:** `RequirementsPacketBuilder` emits no VALUE clauses and omits level-01 items without subordinate fields (e.g. `TRANSACTION-HEADER-2 PIC X(133) VALUE ALL '-'`). CBTRN03C's report literals are therefore supplied outside the packet, as copybook CVTRA07Y verbatim in its `examples.md`. Fixing the builder changes every program's `requirementsHash`, so designs, conformance records and evidence must then be regenerated. The AST outline (`normalization/<PID>/ast-outline.txt`) now carries every VALUE clause beside the packet without changing its hash.
- **Abend output (unverified):** the faithful runtime buffers 64 KB per output file and the platform's `SequentialDataset` writer 8 KB. Neither closes files on an abend, so an abend after more than 8 KB of output would probably leave different partial files. No scenario covers that yet.
