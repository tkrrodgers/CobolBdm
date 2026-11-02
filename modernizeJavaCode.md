# Modernizing pipeline-generated Java, then enabling forward engineering

Status: implementation plan, source-grounded and reviewed with Claude Opus 5.5 at high reasoning; not an assertion that the proposed features already exist.
Date: 2026-10-31.

## Implementation progress

Storage composition, scoped compatibility execution, typed rule slices and the implementation handoff are now
implemented. **Individual L2 slices are not complete modernized capabilities**, and the JOBOL/readability, whole-workflow,
business approval and live rollout gates remain open.

| Work area | Delivered | Still required |
|---|---|---|
| M0 inventory/provenance | Published source/baseline hashes, resolved layouts, CALL-site inventory and frontier metadata in `modernization/storage-composition.json`. | Complete semantic/effect/alias extraction ledger and all scenario/runtime/dialect evidence hashes. |
| M1 storage boundary | Composed storage, independent baseline, scoped `RunUnit` state, recursive binding/control restoration and resource/input isolation; compatibility execution is serialized. | Parallel legacy execution is not claimed; provider caches and unrelated platform runtimes are not captured by this bridge. |
| M2-M4 compatibility | JDK-only predicate/calculation contracts for eligible sites, existing storage conversion at assignment boundaries, live alias-aware CALL views, private CONTENT buffers and tested factory cancellation. | Whole-workflow extraction, native BY VALUE/unsupported lowering, omitted legacy argument semantics and uncertified old-ABI callbacks remain gated. |
| M5-M6 publishing | Contracts, cases, source hashes, human-owned implementation root, locked generated modules and actual LEGACY/MODERN/SHADOW wiring in both export layouts. | Conservative provider checks are not a formal purity proof; custom/external-port shadow adapters need certification. |
| Forward engineering | A human implementation compiles without program state and integrates through the same typed seam; existing loan API assets remain unchanged. | F0-F3 business decisions, intentional semantic changes and live service/persistence rollout require reviewed contracts and evidence. |

Usage and verification commands are in the [README's composed-storage section](README.md#deterministic-java-modernization-composed-storage-first-increment).

Verified without model inference:

- 7 new composition/protocol/naming regressions and 8 existing storage/runtime tests pass.
- 28 composed truth-table checks pass.
- 26 composed GnuCOBOL conformance comparisons pass, including the large-program and SORT cases.
- 18 default projection checks pass, including the new guard against converting composition back into inheritance.
- The three-program CardDemo composed Spring export compiles and passes 122 tests with and without domain partitioning, including all six record-kit initialization/accessor tests. The default partitioned export also passes 122 tests.
- Its POSTTRAN, INTCALC and TRANREPT jobs match 11/11 existing oracle captures. These are captured pilot references, not a newly rebuilt CardDemo oracle. Freshly executed GnuCOBOL parity is established separately by the conformance corpus.
- Wall-clock pilot replay differs only in the existing approved timestamp fields.

The published metadata intentionally does not claim these test results or promote an implementation: evidence collection and approval remain separate, hash-pinned gates.

### Subsequent verification and JOBOL loop

- 50 Java tests pass, including context isolation, recursive frames, live aliases, exceptional writes, clock replay and typed extraction.
- 52 handoff checks pass (16 offline and 36 end to end): real rule selection, mutation detection, independent compilation,
  provider-state checks, generated-file locks, preserved human files, frozen routing, actual shadow reports and explicit
  refusal of uncertified custom shadow selection.
- The real domain-partitioned CardDemo export passes 1,263 tests, including 1,141 rule-conformance tests and all six record-kit tests.
- Its generated programs match 11/11 captured oracle outputs; POSTTRAN's real shadow report has `equivalent=true`,
  `authoritative=LEGACY`, `promoted=false`, both return codes 4 and no differences.
- Freshly executed composed conformance remains 26 GnuCOBOL comparisons and 28 truth-table checks.
- The generated selector caches process-default choices. Per-run/per-request selection must use explicit typed
  constructor bindings of stateless implementations; `RunUnit` does not capture provider caches or arbitrary Java state.

The verified architect inbox message `20261031T183118-976b6780` was read and acknowledged through a fresh local hub
process using the moved store, without enabling or changing hub configuration. Its baseline report
`20261031-183026-a1619d` is **FAIL**: `loanamorti` 34.7/C, `Lcle0100Batch` 100/F. That is a separate
`MlCpu.Modernize` generated batch template, not the sidecar's newly extracted slices. Its emitter-level dataflow,
iterable datasets, validation, reporting and naming are being restructured under the JOBOL audit/parity loop.
A tool-level readability pass is not treated as full-plan completion: baseline comparability, parity scope,
remaining reviewer obligations and approved rollout decisions remain explicit gates.

#### Measured JOBOL iteration and ruleset transition

The architect accepted iteration 4 as genuine generator restructuring: immutable `RunTotals`, explicit parameters and
results, iterable datasets with adapter exceptions, numeric predicates and calendar-library logic. Only PICTURE
formatting moved to the compatibility formatter; there was no rule disabling or lane dodge.

- Under ruleset 1.0, report `20261031-193300-9868c5` has estate index 1.7, with the original 100/F batch reduced to 0/A.
  The baseline-comparable first restructuring resolved 69 findings with no new findings; GN4 passed 7/7.
- Ruleset 1.1 introduces stronger positional-access, string-code-set and repeated-overflow checks. Its new baseline is
  `20261031-193948-bbd188`, hash `0018a27e11dbcfd2`: estate 22.2/B, gate PASS, `Lcle0100Batch` 56/D and `Fsem0100Job` 32/C.
  Version-1.0 and version-1.1 reports are not comparable.
- The next measured changes are typed record accessors, enums, overflow policy inside `RunTotals`, a cohesive
  `LoanSequence`, and `validate(EventRecord)` returning `Optional<RejectCode>`.
- The architect accepts a small cohesive domain object owning sequence state; this is not a generic WORKING-STORAGE
  bag. The program's byte storage still stays separate from business logic.
- GN4's normal SYSOUT check is ordered inclusion, not full byte equality. Extra error scenarios have equal return codes
  and dataset images without regression, but existing legacy error-text/padding gaps remain explicitly unverified.
  These gates must not be advertised as full SYSOUT byte parity.
- The same batch template must be checked on fineract-xl. Its separate lift-path calculator findings are not fixed by
  the batch pass.

All verified architect messages for the review and new baseline were acknowledged. Further iterations use the new
baseline without weakening its rules or changing lanes; a full-plan completion claim remains gated.

#### Ruleset 1.1 iterations 5 and 6

Report `20261031-200507-51f9df` retains the 1.1 ruleset and passes the loanamorti gate at 1.7, with
`Lcle0100Batch` and `Fsem0100Job` at 0/A. Typed record accessors replace positional calls, code sets are enums,
overflow policy lives in `RunTotals`, and `EventSequence` owns cohesive sequence state. Relative to the new 1.1 baseline,
the restructuring resolves 41 findings with no new findings. The architect review of this iteration remains pending.

GN4 passes 7/7 for loanamorti and 46/46 for the parallel fineract-xl template run. The new `gn4err.sh` verifies 15
native error scenarios with equal return codes, matching dataset images and exact scoped SYSOUT. Normal SYSOUT still
excludes the obsolete emulated heap statistics; the scope requires explicit review and is not full byte parity.
Fineract-xl improves from 36.0 to 28.8 but still fails its estate gate because of the separate calculation/lift path.

The remaining batch change is consolidating validation with function selection kept in its original order and adopting
reviewed business-operation names. The independent sidecar MOVE correction is also oracle-grounded: a justified aliased
sender is re-read for each receiver (`CDCD` in both destinations), with three native/faithful/composed conformance checks.
Uncertified shifted overlapping raw copies now fail before writes with an explicit frontier instead of assuming Java
snapshot semantics. This does not change the byte-compatible MOVE behavior established by the existing corpus.

#### Architect acceptance and parity decision

The architect accepted iteration 7 as **batch track DONE**: loanamorti is 1.6/A, both fineract-xl batch steps are A,
and the generator is lint-clean/build-tested. The fineract-xl estate still fails on the separate lift/calculation track.

Tomrodgers explicitly approved the technically reviewed statistics-only SYSOUT exclusion v2 on 2026-10-31.
The approved SHA-256 is `faf2fc2c059ed3b204ed4b8e2c04bd1f987ad7d1b07c0ef5f2952f033474f958`.
It excludes only the position-scoped, allowlisted obsolete heap/GC statistics block and implicit-CLOSE warnings,
with asserted counts, retained excluded lines and never-exclude guards for business/error/abend output.
The harness checks the approved hash before changing outputs. Changing the patterns or scope requires renewed approval.

This approval does **not** waive the newly exposed 31-digit arithmetic defect. The legacy program abends 37 at a known
lifted decimal site for long terms, while the modern program succeeds; the 16-case error harness therefore remains red.
The next track preserves that limit at known sites before improving lifted labels, temporary/versioned names and method
structure. No long-term input rejection or global arbitrary precision cap is substituted for the legacy behavior.

The independent raw-MOVE track retains its fail-closed default until a target-compiler capture pins overlap semantics,
and adds precise provenance/census rather than claiming that one GnuCOBOL probe establishes IBM behavior.

#### Current loop 0 and corrected decimal-capacity premise

The lead's loop plan now limits active work to fineract-xl loop 0 until review/approval; later estates are handed off
one at a time. The exit requires the unchanged JOBOL 1.1 ruleset, build, all GN4/error scenarios under approved
exclusions, no loanamorti regression, and architect acceptance.

The earlier "31-digit" diagnosis was disproved by source inspection and native probes. The actual legacy limitation
is **FSJBDEC big-value capacity**: 2,223 stored limbs, 2,225 operation-register limbs and bounded scale. The measured
non-abending/abending term boundary is 1,331/1,332, predicted by the model before the oracle confirmed it.
The per-site capacity correction was architect-accepted in report `20261031-212633-6584bb`: 19/19 error cases,
GN4 7/7, loanamorti 1.6/A with zero regression. Other BigDecimal operations remain ordinary Java.
The preservation decision stands; the forward backlog must name this capacity limitation rather than impose an
invented global 31-digit cap.

Loop 0 still requires fineract-xl lift quality to pass and evidence for the per-loan FSJBDEC store's 2,500-big-value
capacity: either prove exhaustion unreachable under valid input and probe the maximum case, or preserve the counter
and reset semantics with a native negative test. Unguarded operations may not receive tainted big-path values.
The raw-MOVE task is paused at its compiled/tested checkpoint until loop 0 approval; no new estate has started.
An attached pipeline-inbox watcher is used while the current-loop work continues.

#### Loop 0 approved; Loop 1 handed off

The lead independently verified and approved fineract-xl loop 0 in message `20261031T220334-fec2f4db`.
Final report `20261031-220149-f58389` is 15.0/B, gate PASS against the loop's iteration-0 baseline, change -21.0,
with 111 findings resolved and none added. The modern lane retained its code; the added compatibility code was
reviewed as record views, enums and reporting boundaries, not a lane dodge.
GN4 is 46/46 on fineract-xl and 7/7 on loanamorti, the error harness is 21/21 under the unchanged approved v2 exclusion,
residue is zero, and store-capacity behavior is preserved with the 312/313 probe and negative control.
The unchecked big-path taint gap fails closed.

New regression baselines are loanamorti `20261031-220151-e020cf` (1.6/A) and fineract-xl `20261031-220149-f58389`
(15.0/B). Every later shared-generator change must retain non-positive JOBOL deltas and green parity on both.
Generic numbered-local, long-method and truncated-name improvements remain recorded rather than silently dropped.

Loop 1 is now authorized for fineract-cobol's Tier-1, non-J3S shape, with fineract-cobol-cr as an independence check.
It starts by generating and auditing the current generator's iteration 0 before modifications. Its required output
is a real typed LINKAGE/subprogram lift, PICTURE-driven values and record-boundary dates, zero residue/build errors,
JOBOL PASS, GN4 58/58 on both variants, both regression baselines green, and lead review.
No Loop 2 or new target starts until Loop 1 is approved.

#### Loop 1 milestone: generic lift works; quality gate remains open

Iteration 0, `20261031-221108-aa8538`, measured 93.2/F, 87 residue sites and 31 javac errors, with no domain
classes for the 16 CALL subprograms. Iteration 1, `20261031-231144-affd92`, emits all 16 typed subprogram classes,
has zero residue and a clean Java 21 build/lint, and passes GN4 58/58. Its JOBOL index is 55.4/D, change -37.8
under the same 1.1.0 ruleset (`0018a27e11dbcfd2`), but the gate still **FAILS**: `Fnls0100Batch` remains 100/F.
The report records 33 resolved and 152 new findings as the previously unlifted subprograms enter the modern lane;
the lower estate score is not a claim that those newly visible violations have been resolved.

The independent fineract-cobol-cr export also builds cleanly, passes GN4 58/58 and measures 55.4/D
(`20261031-233347-568dc7`). Loanamorti `20261031-233332-451078` and fineract-xl `20261031-233338-b40619`
retain their approved scores with zero regression and green GN4. The architect accepted this as progress,
not loop completion, in message `20261031T233834-d063b4b8`.

The next increment prioritizes proven date/rounding/money kernel substitution with JDK operations. Retain the
lifted reference, run counted differential cases (exhaustive where feasible, otherwise fixed-seed and edge cases),
and require a deliberate mutant to fail before substitution. Preserve range and return-code guards; no substitution
is accepted with a semantic difference. Remaining work includes typed named methods for proven literal function
codes, header-evidenced names with provenance, generalized iterable/status and 88-level transformations, and report
ports for subprogram output. Main, clean-room and approved regression gates remain mandatory throughout.
The raw-MOVE provenance/census track remains paused while this authorized loop is active.

#### Loop 1 approved; Loop 2 authorized

The architect independently approved Loop 1 in verified message `20261101T015338-1a9c059f`.
Final main report `20261101-013204-d1d187` measures **12.4/B, PASS**, change -80.8 against iteration 0,
under the unchanged ruleset. Both main and clean-room exports build without errors or warnings, have zero residue,
and pass GN4 58/58. The lead reviewed the lanes and confirmed that business logic remains in modern Java:
18 modern files contain 5,269 lines; compatibility record classes are accessor-only, with platform and shadow
execution infrastructure at the boundary.

The generated build's check runs the retained-reference kernel gates:

| Kernel | Differential cases | Differences | Mutants detected |
|---|---:|---:|---:|
| DateKernel | 21,058,849 | 0 | 4/4 |
| RoundingKernel | 2,082,656 | 0 | 3/3 |

The lead accepted a documented rounding exclusion below digit count -9968. A subsequent bounded review corrected
the original "undefined" premise: for S/P operations, an ON SIZE ERROR branch continues with **defined, retained
WORKING-STORAGE**, so results depend on call history. The JDK implementation can differ even on the first call
(sticky 'Y': legacy return code 8 versus JDK 12). At Loop 1 approval, the public typed methods and adapter did not
explicitly reject this semantic frontier; Loop 2 milestone 1 adds that rejection, as recorded below.

Loop 1 estate parity is scoped to reachable calls: its only P caller uses precision validated to 1..12, S has no
callers, and the money caller uses D with 1..30. The counted rounding gate samples digit counts -2..33, -9968 and
9999; it is not exhaustive over arbitrary digit counts or call histories. In verified feedback
`20261101T010105-eb315c95`, the architect agreed to the correction and explicitly scoped the approval to reachable
estate inputs, not public arbitrary-input kernel equivalence.

Loop 2 must inspect its own callers and add an explicit unsupported-input failure naming the frontier at every
affected typed/adapter entry. No JDK answer may be computed outside the declared supported domain, and retained
WORKING-STORAGE must not be reintroduced into modern code. Compatibility history emulation is reserved for a future
estate that actually reaches this region, with a dedicated oracle capture. The -9968/-9969 boundary, first-call
sticky behavior and two-call history counterexample must fire the frontier; supported-domain differential/mutant
gates and all four regressions must then pass. Validation must precede arithmetic that could overflow for arbitrary
public inputs. This requirement is implemented and gate-tested in Loop 2 milestone 1; it is not a general
precision waiver.

Proven calls use typed JDK APIs; header-derived domain names retain provenance, and lifted date/rounding references
remain only in the test lane. Every future emulation-kernel substitution requires the same differential/mutant
method with an explicit supported domain and a fail-closed frontier elsewhere, recorded in its ledger and docs.

All four regression baselines now apply to every later shared-generator change:

| Estate | Approved report | JOBOL | Required parity |
|---|---|---|---|
| loanamorti | `20261101-013207-049263` | 1.6/A | GN4 7/7 and gn4err 21/21 |
| fineract-xl | `20261101-013209-1858b3` | 15.0/B | GN4 46/46 |
| fineract-cobol | `20261101-013204-d1d187` | 12.4/B | GN4 58/58 |
| fineract-cobol-cr | `20261101-013206-a11124` | 12.4/B | GN4 58/58 |

Loop 2 is authorized for fineract-legacy's deliberately legacy-shaped input: 21 programs, 7,915 lines and
58 scenarios. Its iteration-0 baseline must be generated with the approved Loop 1 generator before changes.
Inventory the three additional utilities, then address proven GO TO/PERFORM THRU, REDEFINES, 88-level and
file/status shapes through generic passes. Carry-over includes real size-guard capacity types, numbered series,
working-storage scratch dataflow, find-first early returns and work-code enums. Loop exit requires zero residue,
clean build, JOBOL PASS versus its own baseline, GN4 58/58, all four regressions non-positive and green, and lead
acceptance. No Loop 3 or new target starts before that approval.

Loop 2 housekeeping corrected `tools/modernize/gn4.sh`: an absolute estate argument had been appended to
`MODERN_ROOT`, creating a doubled drive-path scratch tree. The harness now distinguishes estate names from
absolute Windows/POSIX paths, normalizes Windows separators, preserves `GN4_EST` overrides and rejects nonexistent
estates before cleanup. Seven path cases and shell syntax checks pass; a real absolute-path invocation passes both
FNLS0100 and FNRP0100 with zero dataset differences and return codes 0/0. Only the inspected erroneous scratch
subtree and the temporary verification outputs were removed.

#### Loop 2 milestone 1: gate met; approval remains conditional

Iteration 0 is `20261101-015654-c489dc`: 42.9/C, gate FAIL, 265 residue items and 66 javac error lines.
The approved Loop 1 generator initially crashed on a copybook alias lookup. A one-line canonical-name correction
was necessary before this baseline could be generated and was verified byte-neutral on the four regression estates;
the baseline is therefore not represented as output from an entirely unchanged generator.
The three extra utilities are FNEM04XA, FNEM07XA and FNLS01XA, extracted caller paragraphs carrying abend fields.

Iteration 1, `20261101-021443-15a70d`, measures **14.4/B, PASS**, change -28.5, with 94 resolved and 19 new findings
under the unchanged ruleset. The generated estate has zero residue, a clean Java 21 build/lint and GN4 58/58.
The lead checked the modern/compatibility lanes and accepted the measured gate in verified feedback
`20261101T024323-e9028e03`, but final Loop 2 approval is **conditional**, not granted.

Generic `LegacyFlow` transformations produce 406 ledger entries for section/THRU structuring, control flow,
dispatch, flags, search/string lowering, record aliases and related transformations. The initial reported total
of 410 was a summation error. Of 32 removed source paragraphs, 28 are unreachable and four are inlined, not dead.
A previously silently omitted PERFORM VARYING ... AFTER now becomes explicit residue unless its
semantics can be lowered. The legacy inlined date-kernel copy is recognized; signed zoned fields use an
oracle-probed boundary codec. No generated Java was hand-edited.

The date and rounding gates were rerun against this estate's lifted legacy references: 21,058,849 and 2,082,656
cases respectively, zero differences and all seven mutants detected. The rounding frontier now rejects unsupported
inputs at both typed methods and the CALL adapter, before arithmetic, with an explicit `SemanticFrontier`:
S/P precision -9968..9999, D digit count -9999..9999 and values at most 31 digits are the declared contract.
Boundary/extreme-int/value-size tests pass. The reference counterexample returns 8 on a first call and 20 after
a prior call, versus the old JDK answer 12. These precision intervals are not exhaustively enumerated by the
unchanged sampled gate. At milestone 1, legacy-estate caller bounds were observed, not numerically proven;
milestone 2 supplies the bounds below. No tested batch scenario reaches the frontier.

| Regression | Milestone report | JOBOL | Change | Parity |
|---|---|---|---:|---|
| fineract-cobol | `20261101-021445-861bb6` | 12.3/B | -0.1 | GN4 58/58 |
| fineract-cobol-cr | `20261101-021447-9a942d` | 12.3/B | -0.1 | GN4 58/58 |
| loanamorti | `20261101-021448-ba83a3` | 1.6/A | 0.0 | GN4 7/7; error paths 21/21 |
| fineract-xl | `20261101-021449-6c83a5` | 15.0/B | 0.0 | GN4 46/46 |

J3S generated sources remain byte-identical; the other regressions change only the rounding kernel and its gate.
The approved SYSOUT exclusion hash is unchanged. These milestone reports do not replace the approved baselines.

Final approval additionally requires:

1. Join every legacy-flow ledger entry to **legacy execution** from the 58 scenarios, reporting exercised and
   unexercised counts by rewrite kind. Conditional subregions need matching execution evidence, not just an unrelated
   paragraph hit. Tracing must use separate scratch binaries/trace output, preserving frozen oracle binaries and
   business SYSOUT.
2. Cover each unexercised rewrite with targeted native/modern scenarios, including reject/abend paths, or a per-entry
   valid-input reachability proof. All 32 dropped paragraphs need explicit arguments, especially ALTER's 9900-9930 group.
3. Pass at least **200 additional reproducible seeded scenarios**, including damaged-record and edge domains, with
   per-case dataset/return-code/SYSOUT results and seed manifests; no new exclusions.
4. Record whether this estate's rounding caller bounds are proven or only observed. Rerun all four regressions
   after any shared-generator change.

Coverage and randomized-parity results follow below. Remaining size-guard types, long methods, numbered series,
scratch dataflow and code enums stay on the generic-pass backlog.

#### Loop 2 coverage and expanded parity; user-directed diagnostic closeout

Milestone 2 audited report `20261101-044113-9a46c2` remains 14.4/B, PASS, with clean build, zero residue,
GN4 58/58 and both kernel gates green. Generator changes add region-line/reference provenance to the ledger;
the emitted Java is byte-identical to milestone 1. Legacy execution exposed four incorrectly "unreachable"
FNDKRND ledger classifications; these are corrected to inlined-source entries.

Coverage uses separate scratch native binaries with an in-memory statement-trace hook, leaving frozen oracle
binaries intact and keeping trace data out of business SYSOUT. Initially 398 trace runs were checked against frozen
runs: 385 exact matches and 13 matches after removing trace-only source locations from libcob warning diagnostics.
That normalization is solely an instrumentation cross-check, never a native/modern parity exclusion.
An additional 300 crossover scenarios bring the coverage sample to 698 legacy runs:

| Ledger status | Entries |
|---|---:|
| Exercised, all lines | 183 |
| Exercised, partly | 145 |
| LINKAGE record, program entered | 2 |
| Not exercised | 47 |
| Unreachable, with graph arguments | 28 |
| Inlined, no traceable statement | 1 |
| Total | 406 |

The lead accepted graph arguments for the FNEM0600 ALTER group, LF323's absent 'RD' setter and LF053/LF058's
between-dates path reached only through the inline copy. The 39 abend/size-error entries are accepted on structural
construction, **not execution-proven**. Approximately ten input-dependent business targets and 79 partly covered
entries with business gaps remain recorded residual risks. Crossover cases reduced unexecuted business lines from
264 to 239 without changing entry-status counts. Lack of execution is not a reachability proof; focused cases
remain carry-over, and any parity failure there reopens the loop.

Caller bounds are now derived: P precision is 1..12 through FNLS0100's envelope check (zero bypasses the call);
D fits PIC S9(4) because both writes abend on size error; S has no caller. These bounds are distinct from the
sampled public-kernel proof domain.

| Additional scenario set | Seed | Cases | PASS | FAIL |
|---|---:|---:|---:|---:|
| Mixed random/damaged records | 20261101 | 220 | 208 | 12 |
| Targeted feature combinations | 20261102 | 120 | 119 | 1 |
| Valid-field crossovers | 20261103 | 300 | 300 | 0 |
| Total | | 640 | 627 | 13 |

All 13 failures are abend 14 ("request out of sequence"): datasets match and return codes are 16/16, but native
SYSOUT includes four implicit-CLOSE warnings absent from Java. They remain failures under strict parity.
The four regression reports `20261101-044115-fa4b8d`, `20261101-044116-02514a`,
`20261101-044117-48e0fa` and `20261101-044118-21a4ba` remain green at 12.3/B, 12.3/B, 1.6/A and 15.0/B,
with changes -0.1, -0.1, 0.0 and 0.0 and the unchanged required GN4/error counts.

The lead's message `20261101T045330-1032e3a3` accepted the restructuring with residual risk and proposed a pinned v3
exclusion for these diagnostics. A concurrent, **explicit Tomrodgers decision** instead selected preservation at
the compatibility boundary with oracle-backed tests. That current decision supersedes the overnight exclusion
proposal: **no v3 is implemented or user-approved**.

The architect subsequently confirmed the direct user decision in verified message `20261101T051744-3c25c12f`:
**v3 is withdrawn**, v2 stays unchanged, and the Loop 3 hold is accepted. The implementation must remain entirely
in the compatibility lane, with the modern lane unchanged and no new JOB055. It must expose a named, explicit
native-runtime diagnostics profile enabled for parity runs, recording its identity/hash and compiler/flags in
the evidence. Production disabling that profile later is a separate explicit business decision.

The selected fix derives warnings from actually opened/not explicitly closed FD/DD handles at run-unit termination
under the pinned native compatibility contract, preserving exact formatting/first-successful-open order and closing
Java resources. Native probes corrected the original abend-only premise, as recorded below.
It must not insert canned abend-14 text or native-runtime state into modern business logic. Explicit closes,
failed opens and unopened files need negative tests. Rerun the 13 failures, all 58 original and 640 additional
scenarios, kernel gates and all five lead-nominated baselines with no new parity exclusion; v2 remains unchanged.
At this checkpoint, strict diagnostic closeout and acceptance were pending; final approval follows below.
The lead-nominated baseline IDs below apply to the closeout tests; their nomination is not proof that this
user-directed diagnostic change has passed:

| Estate | Closeout baseline | JOBOL |
|---|---|---|
| loanamorti | `20261101-044117-48e0fa` | 1.6/A |
| fineract-xl | `20261101-044118-21a4ba` | 15.0/B |
| fineract-cobol | `20261101-044115-fa4b8d` | 12.3/B |
| fineract-cobol-cr | `20261101-044116-02514a` | 12.3/B |
| fineract-legacy | `20261101-044113-9a46c2` | 14.4/B |

#### Loop 2 final approval; Loop 3 onboarding released

The lead independently verified and gave final approval in message `20261101T063955-5e8b522d`.
The user-directed native-diagnostics implementation passes **13/13 former failures, 640/640 additional scenarios
and 58/58 originals**, with full dataset/return-code/SYSOUT comparison and no new exclusion or normalization.
Final fineract-legacy audit `20261101-063817-9435ca` remains 14.4/B, PASS, change 0.0 and no new findings.
The modern domain/batch lane is byte-identical; only compatibility dataset/step/shadow bindings change.
Kernel gates remain green, and all five regressions retain their nominated scores.

The named profile is `gnucobol-3.3-dev.0-std-ibm-sysout-diagnostics`, selected by `mlcpu.runtimeProfile`
and enabled by GN4. Contract SHA-256:
`14f228c5817a45ff6c4c3d0d19925ffd545dcbf17ec2d7cc290c64ed8bd895f0`.
Shadow reports record profile identity/hash. No profile emits no native-runtime diagnostics; an unknown profile
fails explicitly. Production defaults remain unchanged.

Oracle captures using cobc 3.3-dev.0 and pinned flags establish the actual lifecycle:

- Warnings arise at **any run-unit end with files still open**, including normal STOP RUN or main GOBACK, not only abend.
- Order is newest first by **first successful OPEN**; closing and reopening a file preserves its original rank.
- Explicitly closed, never-opened and failed-OPEN files emit no warning.
- Native captures and tests cover abend, both normal-end forms, open order, reopen, closed/failed/unopened files,
  disabled profile and unknown profile. Legacy file lifecycle is separate from actual Java stream ownership.

The existing J3S error harness already removes implicit-CLOSE diagnostics from the legacy side under approved v2.
Enabling this profile there would conflict with that asymmetric filter. Accordingly GN4 enables the profile,
while `gn4err.sh`/v2 remain unchanged and pass 21/21. Retiring that part of v2 is a separate user decision,
not inferred from the current preservation choice.

| Estate | Final regression baseline | JOBOL | Required parity |
|---|---|---|---|
| fineract-legacy | `20261101-063817-9435ca` | 14.4/B | GN4 58/58; seeded set 640/640 |
| fineract-cobol | `20261101-063819-195b1b` | 12.3/B | GN4 58/58 |
| fineract-cobol-cr | `20261101-063820-ae8237` | 12.3/B | GN4 58/58 |
| loanamorti | `20261101-063821-d01037` | 1.6/A | GN4 7/7; error paths 21/21 |
| fineract-xl | `20261101-063822-989c7b` | 15.0/B | GN4 46/46 |

Residual coverage risk remains explicit: about ten unexercised input-dependent business targets and 79 partially
covered entries are not claimed execution-proven. A failure found by future targeted tests reopens Loop 2.

A tightly coupled failure-path cleanup issue was found after approval: emitted dataset termination could throw during
profile validation, SELECT lookup or resource release before attempting all remaining releases. The existing
unknown-profile negative test had no open resources. The accepted milestone 6 correction below attempts all
releases, preserves primary/suppressed errors and clears lifecycle state without hiding failures.

Loop 3 is released for kontor-zins: exact BigDecimal annuity logic, packaged as 31 COBOL programs/10 copybooks,
KZANNUIT and six scenarios. Its required gates are JOBOL PASS, clean build, zero residue, GN4 6/6 plus damaged/reject
paths, all five baselines green and lead review. No Loop 4 begins before its handoff.

#### Loop 3 onboarding verified before generator handoff

Host onboarding created project `kontor-zins`, deriving KZLE0100 as the KZANNUIT batch step, 30 subprograms and
99 call edges, with all 31 programs parsed. Twelve scenario input files are hash-identical to the packaged estate.
The project records all six oracle scenarios, the reference mapping and accept-01 as the acceptance scenario.
Publication capabilities remain the complete derived set of 31 programs; narrowing a forward-publication surface
is not used to hide estate code from this baseline.

The forced native rebuild compiled 32 objects from scratch with no reuse, using cobc 3.3-dev.0 and
`-std=ibm -fsign=EBCDIC -fstatic-call`. Base plus six scenario runs have return code zero and match their references;
base duplicates accept-01 and is not counted as a seventh distinct scenario. Independent dataset hash comparisons
confirm **6/6 byte-equal**, totaling **249,964 records and 696 'X' rejects**:

| Scenario | Records | X rejects |
|---|---:|---:|
| accept-01 | 6,139 | 21 |
| replay-01 | 48,153 | 134 |
| replay-02 | 49,655 | 134 |
| replay-03 | 47,228 | 142 |
| replay-04 | 51,352 | 126 |
| replay-05 | 47,437 | 139 |

No host/generator/parity-script fix was required for onboarding. The private onboarding host was stopped by its
specific owned PID. At the onboarding checkpoint, iteration-0 generation/audit had not started: it awaited the
bounded termination-cleanup hardening task. The handoff is now accepted, as recorded below.

#### Loop 3 error-path fixtures pinned

The independent fixture inventory identifies 689 IllegalArgumentException rejects and seven damaged-record rejects
in the original six scenarios (696 total). Additional fixtures cover missing categories, rather than treating that
aggregate reject count as proof of every exception path.

Fifteen separate native-versus-oracle-A runs pass output-byte/counter/abend checks and reproduce byte-identically
across two regenerations: one record-level run (31 loans, 26 rejects), eight run-control failures, five order/structure
failures after a complete loan, and empty input. Cases include malformed header/offer/event fields and flags,
invalid event types, invalid principal/rate/term, and positive principal 0.0049 rounding to zero before MT/SC/YS
monthly-payment validation. Valid boundary controls include 600 months, principal 0.0050, rate 5E-13, SY 50 years
and valid processing after earlier rejects.

These are **not yet native-versus-modern GN4 results**. Oracle A returns the domain abend code 11..15, whereas the
native CEE3ABD stub returns 16 and prints that domain code; the fixture comparator explicitly validates this mapping,
not raw exit-code equality. Five business counters and output bytes are compared. Native stderr is pinned separately,
including implicit-CLOSE ordering, and an accept-01 self-check matches the existing frozen outputs.

KZANNUIT STEP20 runs KZLE0100 with RUNCTL (FB80), CASES (FB100) and KZOUT (FB200).
KZOUT maps to KONTZINS.RESULTS.DATA; it exists in the job/reference checks despite onboarding deriving only the two
input datasets. SYSOUT is captured; STEPLIB is not an opened dataset.

The reproducible fixture suite and checksum/results inventory reside under
`projects/kontor-zins/fixtures/error-paths`; they are separate from `oracle.scenarios`, official scenario files and
frozen native/reference outputs. No generator or parity script was changed. The temporary private host copy was
removed once no longer needed. This fixture work preceded generator handoff.

#### Termination hardening accepted; Loop 3 iteration 0 proceeding

Verified architect feedback `20261101T071017-210d2839` accepts the generator-level termination correction:
unknown-profile and missing-SELECT errors no longer prevent cleanup, every open resource release is attempted,
and lifecycle state is cleared. The first failure is retained as primary and subsequent failures are suppressed;
no error is silently swallowed or converted to success.

Twelve persistent runtime-profile checks pass in each of fineract-legacy, fineract-cobol and fineract-cobol-cr.
They are emitted into the existing generated `kernelGates`/`gradle check` runner, including unknown profile with
open resources, multiple unmapped DDs, multiple release faults, and profile failure plus release fault.
J3S estates without that runner retain the independently run scratch diagnostics tests; no new test framework
was added. Native warning format/order, profile hash, v2 hash and modern-lane code are unchanged.

Milestone check `20261101-070933-ff062f` remains 14.4/B, PASS, zero delta and no new findings. Native abend cases
pass 13/13, fineract-legacy GN4 passes 58/58, kernel gates remain green, and all five regressions are unchanged:
checks `20261101-070934-55a1e4`, `20261101-070935-52fb2b`, `20261101-070937-ad81d8` and
`20261101-070938-3a8f0b` retain the required GN4/error results. These checks do not replace the final score anchors.

Generator/parity-wiring ownership is now exclusively handed to the Loop 3 owner. Fresh iteration-0 generation/audit
proceeds before any Loop 3 generator changes, followed by KZLE0100 GN4 wiring and six original plus fifteen
error-fixture comparisons against the exit gate. Isolated Loop 2 residual-query harness work continues at lower
priority without modifying the shared generator, parity scripts or emitted business classes.

#### Residual query isolation accepted; batch reachability remains open

In verified feedback `20261101T071449-7b6df671`, the lead accepts five FNEM0800 direct native/modern cases:
containing-period match, exact match, no match, non-empty EMI model and empty EMI model. All three returned record
images, the printed result and return code match exactly in 5/5 cases.
LF230, LF227, LF228, LF250 and LF251 gain complete traceable-line evidence in isolation; LF372 additionally gains
lines 192/476 but retains gaps at 389/427.

This is **isolated subprogram execution, not batch-valid reachability**. The containing-period path needs the model
to lack the exact dates queried by the batch, and the non-empty check needs a populated EMI model; suitable valid
batch inputs remain unproven. The original 330/406 batch-exercised count is not increased by these direct fixtures.
The remaining eight input-dependent business targets and partially exercised entries remain recorded risks.
No generator, parity script or business source changed; further residual work is parked at low priority while
Loop 3's iteration-0 audit and modernization gates take precedence.

#### Loop 3 milestone 1: modern Java gate met; exact capacity preservation pending

The unchanged-generator iteration-0 report `20261101-071428-591197` measures 8.0/A, but its estate fails compilation
with 12 javac errors despite reporting zero residue. Four J3S JRE stand-ins lacked JDK mappings and an unread job
object was assigned without a declaration. The baseline is frozen before corrections; score alone is not completion.

Generic generator changes map List.of, List.copyOf, Math.multiplyExact and BigDecimal.pow, expose any still-unmapped
emulation as residue, replace proven interface-dispatch chains with virtual calls, hoist identical branch tails,
inline single-use returned temporaries, derive temporary names from accessors and remove unread job/work fields.
No generated Java, rules, thresholds or lane configuration was hand-edited.

Iteration 1, `20261101-081528-274242`, is **0.0/A, PASS**, change -8.0, five resolved findings and none added.
The Java 21 build/lint is clean with zero residue. Remaining JOB022 findings are compatibility statics, not modern
business-code findings. KZLE0100 GN4 wiring passes **25 runs**: base plus six scenarios (7), fifteen error fixtures
and three domain sweeps. Base duplicates accept-01 and is not an additional distinct reference scenario.
Datasets and return codes match exactly; SYSOUT matches except the position-scoped 49-line END statistics block
under the unchanged, hash-verified approved v2 rules. Implicit-CLOSE diagnostics are preserved and compared,
not excluded. A cross-check also matches the pinned native evidence.

Six observable mutants are detected. Plain multiplication versus multiplyExact and an unmodifiable view versus
copyOf are indistinguishable on the proven reachable inputs. A pow HALF_UP/HALF_EVEN tie at digit 20 is erased by
cent rounding and is not caught by this domain sweep; pow rounding equivalence relies on the earlier J3S unit-level
G0 differential, not an overclaim that GN4 distinguishes every mutant. Modern code has no Object-slot value equality;
the latent upstream reference-equality defect remains a documented scope limitation.

All five regressions are green. Loanamorti improves to 0.8/A (change -0.8) and fineract-xl to 14.5/B (change -0.5);
the other three retain their scores. Diff review attributes these improvements to dead-code/temporary cleanup.
The fineract-xl finding pair is a relocated existing finding, and its eight lint warnings predate this increment.
No unrelated warning repair or baseline replacement is inferred.

**Reachable blocker:** seven payment-heavy native probes abend 37 at JBDO:67 when FSJBDEC's per-loan store of 2,500
large values exhausts, while modern Java continues with return code zero. Five non-pow probes, including up to
9,000 SC/QI/SY/YS events, remain green. The ordinary 25 runs do not reach the capacity boundary.
Observed MP boundaries are 227/228 at 5.25% or 3.33% over 360 months, and 192/193 at 24% over 600 months;
MT equivalents are part of the required preservation gate. This is a storage-accounting limit, not a generic
31-digit precision cap.

Verified architect message `20261101T085506-bc4f4e1d` accepts milestone 1 **as progress** and selects exact capacity
emulation under the user's standing preservation decision. The active next increment must:

1. Mirror JBigDecimalOps.pow allocation accounting in compatibility DecimalLimit.pow, including Java int wrap and
   internal big-path products, using native coefficient/scale behavior rather than a guessed magnitude cap.
2. Taint pow outputs and check dependent multiplication, subtraction/alignment and division at their native sites;
   any unchecked tainted operation becomes generation residue. Untainted operations entering the big path require
   a PICTURE-derived bound proof or a checked guard. Reset the ledger at the actual BZ/new-loan boundary.
3. Match MP/MT before/at-boundary fixtures on exact return code, JBDO:67 abend SYSOUT and partial KZOUT.
   Removing the store model must make those fixtures fail.
4. Keep the non-pow probes, original 25 runs, kernel proofs and all five regressions green with unchanged rules,
   diagnostics profile and v2. Keep mutable capacity bookkeeping out of modern business state.

Exact preservation is in progress; Loop 3 has not exited and Loop 4 has not started. The architect's conditional
approval/release applies only after every required capacity and regression gate is demonstrated green.

#### User-approved SYSOUT policy migration: v2.1 statistics only

Verified lead message `20261101T092855-3ef381ee` records the user's separate decision to retire v2's implicit-CLOSE
exclusion. The new active policy is **sysout-exclusion v2.1 (statistics-only)**, SHA-256:
`7d7905e2067af80b3950dd93b7e87b3644ecd72388c1d3f10241bfbe20f4b7c6`.
Its STATS_RE, GUARD_RE and contiguous END-position rule are unchanged; the hash covers the two unchanged expressions,
each followed by LF. The old v2 hash remains historical, not an active warning waiver. This is not the withdrawn v3.

A shared `tools/modernize/sysout-stats.sh` supplies the identical policy/filter to GN4 and its error harness,
verifies the pin before execution/cleanup and writes parity evidence. Both harnesses now compare implicit-CLOSE
lines in full under the unchanged native diagnostics profile, including `gn4err.sh`. Warning counts are asserted
as **compared**, never counted as exclusions. Stats-looking lines outside the END block, business/abend output and
all warnings remain compared. Persistent policy tests pass for these boundaries and reject a changed expression
with exit 2; the approved expressions also compare byte-identically to the pre-change snapshot.

The new policy already passes **50/50 kontor-zins runs** (25 earlier standard/domain runs plus 25 measured capacity
fixtures) with the frozen-native cross-check. This also confirms the implemented exact pow/store model's positive
boundary, reset, mixed-operation and partial-output cases under v2.1; store-model mutation controls and the final
combined milestone still remain required.
The loanamorti decimal-store abend's RESULT-FILE and EVENT-FILE warning lines now match native/modern exactly,
with zero warning exclusions. With the profile disabled, all seven formerly masked warning-bearing error cases
fail as required: the other fourteen cases pass, data and return codes still match, but native 1/2 warning lines
versus modern zero are detected.

Full five-estate build/regression validation is running under v2.1. The parent temporarily owns the shared policy
and parity harness files during that migration; the Loop 3 owner retains generator/store-model ownership.
No profile format/hash, statistics allowlist, JOBOL rules or frozen reference/native binaries changed.

#### Loop 3 approved; carry coverage stated accurately

The lead approved Loop 3 in verified message `20261101T102852-51967aa0`. Final report
`20261101-093251-b2dcf1` is 0.0/A, PASS, change -8.0 against iteration 0, with no new findings.
The exact compatibility store model preserves native pow int-wrap/working-precision operations, native sites,
interprocedural taint and new-loan reset. Checked arithmetic covers dependent operands; the remaining unchecked
rate setScale has a 15-digit PICTURE-derived bound. The event adapter also preserves native abend 38 for a stored
value reaching an output slot.

Native-measured fixtures cover **31 capacity configurations/runs**; all **56 runs** pass under v2.1 (7 oracle/base,
15 error fixtures, 3 domain runs and 31 capacity runs), plus the 12 earlier payment/non-pow probes.
In addition to 227/228 and 192/193 MP boundaries, precision 11 at 1,000 months reaches 178/179.
MT event ordinals include the opening NT event, so 228/229 and 193/194 are not interchangeable blanket payment limits.
Mixed operations and reset/rejected-header fixtures preserve partial output. One-entry configurations measure
2,500/2,501 exactly at A:61, and a 32-digit-product case measures 312/313.

Of 20 mutants, 15 are caught, three are equivalent on reachable inputs and two have explicitly recorded verification
limits. The mandatory store-count/reset/pow-accounting negative controls fail the boundary gate as required.
The pow HALF_UP/HALF_EVEN cent-rounding limitation remains unit-proven separately.
The doRound one-pass mutant is **not a runtime frontier**: production contains the full native carry-retry loop,
with every pass checked at JBDO:80. No test yet triggers its second-pass carry; 112,000 configurations without one
do not prove unreachability. This is a coverage gap, not missing production logic or a claim that input is rejected.
The architect was notified of that wording correction.

All five regression audits retain/improve their anchors; the six next-loop anchors are:

| Estate | Anchor | JOBOL |
|---|---|---|
| fineract-legacy | `20261101-093310-4ddb81` | 14.4/B |
| fineract-cobol | `20261101-093312-461e13` | 12.3/B |
| fineract-cobol-cr | `20261101-093314-497769` | 12.3/B |
| loanamorti | `20261101-093315-ad48d1` | 0.0/A |
| fineract-xl | `20261101-093317-ab9894` | 14.5/B |
| kontor-zins | `20261101-093251-b2dcf1` | 0.0/A |

The lead kept the newly exposed fineract-xl strict-SYSOUT gap separate from Loop 3 approval. Previous ordered
inclusion checks were not full SYSOUT proof; this gap must not be hidden behind the green dataset/return-code results.

#### Explicitly approved XL statistics scopes; strict verification in progress

Stricter GN4 comparison exposed 353 native J3S statistics lines absent from modern output in every XL case.
Thirty schedule cases use FSGN0100 before FSLS0100 REQUESTS READ. Sixteen event cases use FSEM0100 before
FSLE0100 RECORDS READ. In each block, 349 lines match the old statistical grammar after the appropriate prefix,
and four use the previously unallowlisted exact pool names HLSP-2 and HM-2.

Tomrodgers explicitly approved **separate**, narrowly scoped policies for both cases via ask_user:

| Scope | Pin | Required block |
|---|---|---|
| fsls-statistics v1, FSGN0100 only | `972f9d8e5cdd28aca6dc70194ced352bf390eaf289ff571b414d677bf86d388c` | 353 contiguous lines before FSLS0100 REQUESTS READ |
| fsle-statistics v1, XL FSEM0100 only | `5a1159ded50c69ef69e77a4c6799da578d1e027ed6eb6913697598a86839f81a` | 353 contiguous lines before FSLE0100 RECORDS READ |

Each pin covers its exact allowlist, guard, position and count, each followed by LF. Pool names are alphanumeric
or those two exact observed hyphenated names, not a generic hyphen wildcard. Base v2.1's expressions/hash remain
unchanged. No prefix normalization, business/abend exclusion or implicit-CLOSE mask is introduced.
The shared helper chooses the policy only for its named step, asserts the exact count and records evidence.
Persistent tests reject wrong-step application, changed pins and incomplete blocks, while retaining protected output.
The first separate scope verified all 30 schedule cases. After the independently approved event scope was added,
the final combined rerun passes **46/46 strict**: 30 schedules and 16 event cases. Each case excludes exactly 353
statistics lines under its correct pinned step policy; remaining SYSOUT matches exactly (CR normalization only),
dataset differences are zero and return codes equal. All business, abend and implicit-CLOSE lines remain compared.
This closes the previously unproven XL SYSOUT item without pretending the earlier inclusion-only checks were exact.

#### Loop 4 Joda-Money onboarding verified; generator baseline released

Loop 4 is released by the same lead message. Host onboarding created `joda-money` from the Apache-2.0 packaged
estate, retaining LICENSE/NOTICE and all 65 program capabilities. The official six scenario inputs and source copies
are unchanged. Two independent native rebuild/runs (66 fresh objects, no reuse) match all six reference datasets
byte for byte, totaling **12,845 records and 811 rejects**; base repeats accept-01 and is not a seventh distinct scenario.

The inventory has 65 programs, 10 copybooks, JMMONEY and JMLE0100; RUNCTL is FB80, CASES FB100 and JMOUT FB120.
Twelve programs are application code, forty are JRE emulations, and the remainder are runtime, kernels, literals,
dispatcher and driver. All CALL targets are static; CEE3ABD is the sole external target.
All 27 event types occur. The private onboarding host was stopped by its owned PID and its copy removed.

Open risks include 46 unreached abend-31 traps in 16 programs, measured-not-proven pool capacities, not-replayed
registerCurrency/Iterable total/numeric3 behavior and upstream Object-slot equality outside the proven corpus.
Joda's HJC-2 statistics pool is **not authorized** by the new XL policies; it needs its own scoped decision if modern
strict comparison exposes that block.

Generator ownership is exclusively handed to the Loop 4 owner for a fresh `loop4 iter0` audit before generator
changes. The XL verification is complete and parity-wiring ownership is also released, with the approved statistics
policies immutable unless a further explicit decision authorizes a new scope. JDK emulation replacement requires retained-reference differential/mutant
gates and explicit supported domains, not lifting emulation internals into modern business code.
The exit requires clean build, zero residue, JOBOL PASS, strict six/error-path parity and all six anchors green.
No Loop 5 starts before approval/handoff.

The v2.1 migration validation now covers all five prior estates: loanamorti GN4 7/7 and error paths 21/21,
fineract-xl strict 46/46, and each of the three FN estates 58/58. All five generated builds succeed; the three
FN estates retain their kernel/profile checks. The initial aggregate run correctly failed on the then-unapproved
XL statistics gap; the approved scoped rerun closes it. The profile-off negative control remains seven expected
warning-mismatch failures, not a success-shaped fallback. Native diagnostics profile identity/hash and base v2.1
remain unchanged; only the explicit XL-scoped policies supplement it.

#### Loop 4 first modernization milestone; strict Joda statistics scope verified

Iteration 0, `20261101-105614-b6304d`, measures 11.4/B, but that standalone score hides 51 javac errors,
11 untranslated items and silent translation deficits. It was generated before Loop 4 changes.
The generic corrections restore typed JDK/registry mappings, exception and try-with-resources regions,
throw propagation, fixed-data/resource provenance, reflection/initializer handling and job-output range checks.
No emulation internals are moved into modern business code to improve the audit.

Final milestone estate joda-money-loop4-iter5 audits as `20261101-123426-c5f22d`: **1.5/A, PASS**,
change -9.9 versus iteration 0, with a clean Java 21 build/lint, zero untranslated items and reproducible output.
Remaining naming findings include the J3S-truncated country-code temporary series; they are not claimed fixed.

Native probes identify a different decimal variant here: it has no large-value store and represents values as
unscaled S9(31) integers. The compatibility model checks all 15 Joda arithmetic sites, preserving working/result
overflow abend 37. MU's 36-digit product, DR's 32-digit 10Q and SS's scale-31 shift fail at their native sites;
31-digit/exact-quotient/scale-30 controls pass. This is a detected source-variant limit, not an invented global
BigDecimal precision restriction. Job-output range guards also preserve abend 38, including the native warning
lines and partial outputs.

Data and return codes initially matched **39/39 runs**, with exact SYSOUT in 20 abend cases and an unapproved
HJC-2 statistics mismatch in 19 normal cases. Tomrodgers then explicitly approved a **separate Joda-only policy**:

- `joda-statistics v1`, selected only for JMLE0100;
- exact 83-line contiguous FSEM statistics block before JMLE0100 RECORDS READ;
- alphanumeric pool names or the exact observed HJC-2, with business/abend/implicit-CLOSE lines never excluded;
- SHA-256 `811a128d0363aaa07bc9eaa9b937fd009a9496ecfb2b5a7aa9eafc48c68bf709`, covering allowlist, guard, position and count.

The shared policy and persistent wrong-step/protected-output tests pass. Fresh parent GN4 plus pinned-native/reference
checks now pass **39/39 strict**: seven oracle/base runs, four domain sweeps, fifteen error fixtures, seven native
frontier runs and six decimal-boundary runs. Each normal case excludes exactly 83 statistics lines; each abend
excludes zero. Remaining SYSOUT is exact (CR normalization only), as are data and return codes. Base v2.1,
the two XL-scoped pins and the diagnostics profile are unchanged.
An initial verification-wrapper failure was PowerShell's nested-array pipeline shape, not a parity mismatch;
the corrected parsed-array check verifies all 39 captured cases and pins.

Twenty mutants yield 15 detected breaks and five explicitly explained test limits: lookup-only map order,
forced put versus putIfAbsent, input-independent initialization, bounded division remainder and bounded addition.
These are not runtime frontiers merely because no fixture distinguishes them; their specific bounds/construction
arguments remain review evidence.

Open fidelity disclosures are not waved away by the green data-domain corpus. Joda lists 221 after-call JOBFAIL
entries in startup/literal initialization where an exception would propagate instead of reproducing native abend 38.
The owner must distinguish an input-independent, pinned-resource proof from an actually enforced frontier.
Fineract-xl additionally exposes previously silent unchecked decimal forms and after-call/decimal-op abend-38
differences; they remain explicit preservation risks, not newly certified behavior. Object-slot equality outside
the proven corpus, unreplayed registration/Iterable total/numeric3 APIs and measured-only pool capacities remain
outside the proven scope.

The changed regression estates already retain their scores and pass their native suites. The three byte-identical
FN estates are being explicitly rerun rather than treating their hash equality as the required parity execution.
The final checker is also being tightened to fail on any strict-SYSOUT mismatch instead of its former data-only
diagnostic exit status. Loop 4 is still a progress milestone: all six anchor executions, accurate frontier proof
scope and architect review are pending. No Loop 5 has started.

#### Five JOBOL modernization loops approved; no new estate started

Verified lead message `20261101T132826-3c06554d` approves Loop 4 and closes the requested **five-loop programme
(loops 0-4)**. It does not declare every remaining modernization/forward-engineering work package complete.
No new loop is started; the next estate, including the deferred native-COBOL CardDemo candidate, awaits an explicit
user decision. The pipeline inbox watcher remains active. No commits were made.

Joda final estate joda-money-loop4-iter6 is reproducible, builds without errors/warnings or untranslated items,
and audits as `20261101-132556-203e70`: **1.5/A, PASS**, change -9.9 against iteration 0.
The 1,520-line modern lane uses real regex, concurrent collections and class-path resources; no fncobolgen
emulation class remains in its main sources.

Strict parity passes **41/41**: seven oracle/base runs, four domain runs, fifteen error fixtures, seven native-frontier
runs and eight decimal-boundary runs. The added native cap-ad counterexample corrects the earlier false claim
that valid input could never reach the add-capacity guard. Normal runs exclude exactly 83 approved statistics lines
(20 cases); abends exclude none (21 cases). All other bytes/effects/return codes are compared.
The final checker fails on missing cases/sets, incorrect evidence/counts, any strict-SYSOUT mismatch and data/RC
differences. Its superseded data-only version remains historical, not the acceptance path.

Policy/result negative controls are green: tampered pattern/count/pin copies are refused or fail; profile-off
misses exactly 14 native-warning cases; four tampered result trees make the strict checker fail with the offending
assertion. The current source reproduces all six regression outputs and reruns their real native suites:

| Final anchor | Report | JOBOL | Verified parity |
|---|---|---|---|
| fineract-legacy | `20261101-132559-e49f5b` | 14.4/B | 58/58 |
| fineract-cobol | `20261101-132601-a3fb54` | 12.3/B | 58/58 |
| fineract-cobol-cr | `20261101-132602-be10bb` | 12.3/B | 58/58 |
| loanamorti | `20261101-132603-7edea1` | 0.0/A | 7/7; error paths 21/21 |
| fineract-xl | `20261101-132605-f36f0c` | 14.5/B | 46/46 strict |
| kontor-zins | `20261101-132606-c2e23d` | 0.0/A | 56/56 |
| joda-money | `20261101-132556-203e70` | 1.5/A | 41/41 strict |

All six regression audit deltas are 0.0 under unchanged JOBOL hash `0018a27e11dbcfd2`.
Fineract-xl retains its eight pre-existing build warnings and a relocated finding pair; these are not new repairs
or meaningful regressions.

Startup handling is now classified by static RaiseAnalysis rather than zero observed failures. Joda proves 225
callee-cannot-raise cases and proves the remaining five startup initializers deterministic under the pinned binary,
reset state and resource manifest (N1-N4/M1-M2). Generated verifyTargetData rejects altered/missing resources.
A controlled missing-resource runtime test maps to the native CU:83 unsupported trap, abend 31/return code 16.
These proofs do not certify arbitrary deployments or unsupported APIs.

Twenty-three mutants yield eighteen detected runtime breaks, two build-time refusals and three specifically
proved survivors. The inline decimal model is fingerprinted to its runtime; a different runtime becomes residue,
not an optimistic reuse of that model.

**Disclosed, not preserved/proven:** fineract-xl still has 123 unchecked decimal operations and 106 unpreserved
after-call/decimal JOBFAIL paths (15 at startup), recorded in its ledgers. Joda's registerCurrency/Iterable total/
numeric3 replay gaps, observed-only pool capacities, Object-slot equality outside the proven corpus, the Loop 2
residual valid-batch paths and earlier carry-loop coverage gap remain explicit.
These risks are not hidden by the programme approval. Low-priority coverage/proof carry-over may be reviewed
separately; no generator change or new policy is made without a review request/appropriate approval.
Forward domain/transaction decisions, unsupported lowering, remaining raw-MOVE provenance/census work and live
service promotion still follow the plan's own gates.

#### Phase M announced: generic quality rules, awaiting specifications

Verified lead note `20261101T134345-9187f883` records user approval for Phase M: move from passing the JOBOL gate
to better Java structure, signatures, APIs, naming, idioms and method shape. The lead performs the original-Java
comparison and supplies generic rule specifications; no rule is implemented from this announcement alone.

The generator must never read original Java, original names or original structure, and no per-estate naming hints
are introduced. Every transformation derives its trigger/evidence from COBOL, resolved IR or the estate and fails
closed when evidence is absent or ambiguous. Implement one specified rule per milestone.
The fixed initial split derives rules from fineract-xl/kontor-zins and validates on joda-money/loanamorti without
consulting their originals during rule design; any adjusted split must be fixed before implementation.

Milestones report train/test distance deltas separately, JOBOL results, strict parity under the existing approved
policies and all seven regression anchors. A rule helping only the train estates is rejected; a distance improvement
does not count when behavior differs. CardDemo's native-COBOL generalization test remains deferred until an explicit
user decision. The pipeline is awaiting the first rule specifications, with no new generator or policy changes.

#### Phase M batch 1: R1 visibility milestone started

Verified feedback `20261101T142505-ae4da724` delivers the first specifications and starts **R1 only**.
The later sequence is R3 (final/value-record facts), R5 (interface declarations), R2 (Object-slot type recovery)
and R4 (flow-linked identifier completion); none is implemented as part of R1.

The evidence restriction explicitly excludes J3S CLASS/FRAME provenance comments and Java-name/type citations,
as well as original sources. The original-path arguments are passed only to the measuring tool, never used as
generator input or per-estate hints. Rule design does not consult held-out test originals or their detailed examples.

R1 uses the generated reference graph and external entry surface: direct job/replay dispatch members, step/strangler
ports and resource/reflective registrations. Proven entry members and cross-package uses remain public;
same-type-only references may be private, and same-package-only references package-private.
Reflection/string lookup, unknown callers, ambiguous resolution and public override/extension obligations must
preserve required visibility or keep the existing decision and log why. Overload/field identity uses real symbols,
not bare-name heuristics. A per-member per-rule ledger records evidence, changes and skips.

A dedicated rule implementation is in progress with a frozen baseline, focused visibility/override/unknown-caller
tests and a provenance-independent decision check. Measurement reports TRAIN (fineract-xl/kontor-zins) and TEST
(joda-money/loanamorti) separately for G21/G22 and all other gap codes/API/name/signature metrics.
Acceptance requires improvement on TEST as well as TRAIN, no other gap-code increase, clean builds, strict parity
and all seven unchanged JOBOL anchors. No gate or approved policy is weakened to obtain a better distance score.
The R1 milestone stops for review before any subsequent rule or the deferred CardDemo test.

#### R1 rejected by its pre-registered metric; default remains unchanged

Verified lead feedback `20261101T152912-e226fcde` rejects R1 as a measured negative result, attributing the failure
to the specification: a closed estate's reference graph cannot recover authors' external API intent.
R1 remains **opt-in only**, via `--rule api-visibility`, for possible future external API-manifest work.
No such manifest is invented now, and default generation remains byte-identical to the seven final anchors.
It is not promoted as an accepted fidelity improvement.

| Target gap | TRAIN: fineract-xl + kontor-zins | TEST: joda-money + loanamorti |
|---|---:|---:|
| G21 widened visibility | 72 -> 10 | 25 -> 5 |
| G22 narrowed visibility | 58 -> 108 | 2 -> 25 |
| Other gap codes | unchanged | unchanged |

Reached-API fidelity falls on all four estates; names/signatures are unchanged. Public members can legitimately
serve consumers absent from the closed replay graph, so making them private/package-private is not proof of
preserved original API. The lead reclassifies G21/G22 as requiring external intent such as an API manifest.
No per-member original visibility hints or metric-driven variant tuning are introduced.

All seven R1-on builds, strict parity suites and JOBOL checks pass with zero audit delta; the existing eight XL
warnings remain. Twenty decision/permutation/poison checks pass, and five estate poison runs preserve decisions.
These behavior/non-leakage results do not override the failed quality criterion. The interface-implementation
keep obligation missed in the first attempted output was fixed before the measured iter2 gates.
The unchanged legacy source did not rerun its extra 640 seeded cases; its 58-scenario suite did run.

**Pre-existing provenance reliance, now explicit:** J3S model classes, packages and method names are populated from
CLASS/FRAME headers that quote original Java names/types. This behavior predates Phase M and is not changed here.
It inflates J3S name/structure fidelity relative to header-free legacy/native COBOL; it is not evidence that these
names can be recovered generically for CardDemo. R1's new decision uses opaque graph facts rather than those
comment contents. Every later milestone must disclose the underlying model reliance while keeping its own new
inference independent of original sources and quoted provenance. The separate legacy-profile evaluation will
measure this bias; it is not silently resolved by passing poison tests.

#### R3 narrowed before implementation: final classes only

The same verified feedback pre-registers the next milestone as **R3 final classes only (G23)**. Record conversion
is removed: G19 occurs only in TRAIN and would increase G20. No R1 visibility changes, records, type recovery,
interface declarations or renaming are bundled into this rule.

Eligible class facts are: no estate subclass via superclass links, not abstract/interface, and not a reflective,
provider, proxy or extension target protected by a keep obligation. Ambiguous hierarchy/keep evidence fails closed.
New decisions use opaque identities and structural facts, with per-class ledger evidence and poison/permutation tests.
Original Java and CLASS/FRAME name/type comments remain forbidden as new inference inputs; the baseline model's
pre-existing provenance reliance above remains disclosed and unchanged.

The fixed acceptance differs from the original batch's blanket no-rise criterion:

- G23 must fall on both TRAIN (20 baseline instances) and TEST (7).
- No other class (a)/(b) gap code may rise. Class (c) codes G20, G24, G25, G26, G27 and G28 are excluded from that
  no-rise test but still reported explicitly.
- API, name and signature fidelity must not decline.
- All seven build/strict-parity/JOBOL anchors and unchanged approved policies remain mandatory.

A dedicated R3 implementation is active, with R1 disabled and a frozen baseline. It stops for measured review
before R5 or any later rule. No new estate or CardDemo test has been authorized.

#### R3 accepted only with an explicit closed-world deployment declaration

Verified lead feedback `20261101T161924-99acd999` accepts R3 as **opt-in behind `closedWorld: true`**, never as a
default assumption or a fact inferred from a closed reference graph. The declaration means nothing outside the
estate subclasses or proxies its classes. A library owner or deployment owner must make that decision; no existing
estate manifest is silently marked true.

| Metric | TRAIN | TEST |
|---|---:|---:|
| G23 lost final | 20 -> 3 | 7 -> 0 |
| Unclassified TYPE.FINAL_ADDED | 0 -> 17 | 0 -> 5 |

Other catalogued gaps and API/name/signature fidelity are unchanged. The rule marks 50 classes final; 24 match
original final declarations, 22 are stricter than the originals and four are not counted by that comparison.
This is not 98%-precision recovery of author intent. The lead classifies FINAL_ADDED as class (c) **only under
the explicit closed-world contract**, where final-by-default is a permitted design choice.
Without that declaration, external library subclasses, Spring/CGLIB proxies, JPA proxies and mocks may break.

All 48 self-tests, seven estate poison/permutation checks, builds, strict parity suites and JOBOL anchors pass;
the only Java change is insertion of final on eligible class declarations. Unknown hierarchies, providers,
reflection/proxy targets, existing subclasses, abstract classes, records and enum bodies remain protected.
Default generation remains byte-identical to FINAL7, with R1 and R3 disabled.
The existing Model.cs CLASS/FRAME reliance and Shapes' `$`-based enum-body recognition remain disclosed;
the new final decision does not consume quoted original names/types.

The bounded R3 follow-up now wires the explicit manifest declaration into rule authorization and records its
identity/hash/value in the ledger/output metadata. Missing/false authorization leaves the default unchanged;
explicit selection cannot silently bypass it, and malformed/non-boolean configuration must fail clearly.
Tests use isolated profile fixtures, not owner-unapproved edits to existing deployment manifests. This wiring is
pending verification before generator ownership passes to R5.

#### R5 pre-registered: declaration interfaces, independent of R1/R3

The same message starts R5 with fixed acceptance: G11 must fall on TRAIN (13) and TEST (3), API/name/signature
fidelity must not decline, no class (a)/(b) or unclassified gap may rise without a lead ruling, and all seven
existing build/strict-parity/JOBOL gates remain. Measure with **R1 and R3 both off**.

The rule may generalize concrete collection declaration types to appropriate interfaces only when all uses
support it; construction keeps its concrete implementation and ordering/concurrency semantics. Implementation-only
methods, uncertain escapes, external/reflection contracts and overload-binding ambiguity keep the current type.
Compilation alone does not prove binding unchanged: a broader declared argument type can select another overload,
so binding/alias/escape cases need focused tests and per-rule ledger evidence.

Independent R5 preparation is active while the R3 owner finishes profile wiring. It does not edit the shared
generator before handoff, consult original Java or introduce R2/renaming/record conversions. The original-reading
measurement tool remains the only consumer of held-out originals; existing provenance bias is disclosed in the
new milestone as well. No CardDemo or further rule starts before the required review.

#### R3 profile gate verified and closed; R5 implementation handed off

Verified lead feedback `20261101T163028-45f2c375` accepts the explicit authorization gate and closes R3.
The existing estate `project.json` accepts the strict boolean section
`"deploymentProfile": { "closedWorld": true }`; there is no separate profile file or implicit batch/library inference.
R3 requires **both** that declaration and `--rule final-classes`. The declaration alone enables nothing.
An explicit R3 request without a true declaration, including a plain source folder, exits 2 before generation.
Malformed/non-boolean/null/unknown-field profiles fail every lift, and unknown rules fail with a clear exit-2 error.

Generated `modernize/deployment-profile.json` and the final-classes ledger record the manifest/section hashes,
estate identity, value, scope claim and applied rules. Positive test fixtures reproduce all seven accepted R3
outputs exactly except the added authorization metadata; default output remains byte-identical to FINAL7.
All **96 self-tests and 36 command-line negative checks** pass, and all 13 real project-manifest hashes remain
unchanged. No owner has opted an actual estate into closed-world deployment.
The previous seven positive behavior gates carry over by verified source/artifact equivalence, not a claim that
the configuration-only follow-up reran them all.

Generator ownership is now handed exclusively to R5. Freeze its baseline after this gate and run R5 with R1/R3
off; it must not require closedWorld authorization. R5 implementation can proceed with the pre-registered
TRAIN/TEST, fidelity, unclassified-gap and seven-anchor gates. Later rules and CardDemo remain deferred.

#### R5 rejected; provenance dependence is now the primary Phase M risk

Verified lead feedback `20261101T173122-6d269832` rejects R5 under its pre-registered acceptance:
G11 improves on TRAIN only (13 -> 12), stays unchanged on TEST (3 -> 3), and TRAIN G13 rises 6 -> 7.
API/name/signature fidelity is unchanged and no other gap code rises, but that does not rescue the failed criterion.
R5 remains **opt-in/off by default**, via `--rule program-interfaces`; normal generation remains byte-identical
to FINAL7. No metric-driven variant is tuned to the held-out originals.

The measured change widens six fineract-xl declarations (four locals, one parameter, one result); Joda's 19
candidates all keep their types. The other five estates have no candidates in the lifted rule surface.
The remaining measured G11 gaps concern estate interfaces, not concrete JDK collection declarations in business IR.
Five of six recovered XL interfaces are empty, limiting which uses can safely be widened. Changing declarations
alone cannot recover missing interface bodies or author intent.

Ninety-two R5 self-tests pass, including four Java overload counterexamples where widening would silently rebind
the call. R1's 20 checks and R3's 96 checks remain green. All seven actual builds, strict native suites and JOBOL
anchors pass with zero audit delta; the eight existing XL warnings remain. These tests prove the bounded
implementation did not change replay behavior, not that the rejected quality rule should become default.

**Additional pre-existing provenance dependence:** Types.cs takes parameter types from FRAME type names and
synthesizes estate interfaces from them. JavaWriter's generated Javadoc repeats FRAME signatures.
Together with Model.cs CLASS/FRAME class/package/method names and Shapes' `$` enum-body convention, this supplies
much of the apparent J3S name, signature and interface fidelity from comments quoting original Java.
Poisoning facts after model/type construction proves only that the new rule decision is insensitive to those
display values; it does not demonstrate that the upstream reconstruction works without provenance.
The coding context's six per-member comparison-row exposure and a generated signature quotation were disclosed
in the R5 evidence; no tuning used them.

The lead records signature Javadoc as output leakage and defers any replacement with COBOL program/paragraph
citations to a later rule. Nothing is fixed or hidden as part of the next measurement.

#### A0 provenance ablation started: measurement only, before R2/R4

The same verified feedback orders A0 before either type recovery or identifier completion. It introduces **no
generator rule or fix**. A dedicated measurement task freezes the current default generator (all optional rules off)
and copies the four J3S estates into isolated scratch projects. It removes provenance comments, including
CLASS/FRAME headers, quoted Java signatures/types and PEC/source-line citations, while preserving all active COBOL
code bytes. Comment-only diffs, removal categories, hashes and source-line maps must prove the treatment;
actual projects, manifests and frozen native/reference binaries remain unchanged.

Normal and stripped copies are regenerated with the same binary. Report per estate: lift/output structure,
residue, javac results, actual strict native parity where runnable, JOBOL and original-comparison fidelity/gap
codes (especially G01-G15). If stripping prevents lift/build/entry-point recovery, record that failure and mark
parity not runnable, not zero tests passed. Empty output or a low audit score is not a successful modernized estate.
Original Java is read only inside the measurement tool. Any loss of the compare tool's matching metadata and
remaining non-comment provenance channels must be distinguished from actual Java reconstruction quality.

A0 is not allowed to repair what it exposes, modify a policy, supply original-derived hints or silently relax
the comparison. Its result will establish how much must actually be inferred from code/layout/flow alone, which
is the prerequisite for the lead's next R2/R4 decision and the deferred native-COBOL generalization test.

## 1. Goal and order of work

Generate maintainable Java deterministically from the pipeline's resolved COBOL model. Separate business decisions from program storage, COBOL execution machinery, dataset handling and dynamic program resolution. Publish stable, typed integration contracts so a human or an LLM can implement a forward-engineered design without understanding the generated program's internal fields.

The product pipeline must not require Gemma 4 or any other model: no model server, inference, prompts, model-produced code or automated model approval is needed to generate, compile, test or publish the modernized implementation. An LLM may later consume the same integration kit as a human. Model assistance used to prepare this plan is not a runtime or build dependency.

**Delivery order:**

1. Establish an independently testable modernization lane alongside the faithful lane.
2. Decouple state and execution; preserve MOVE, conditions and CALL semantics.
3. Extract actual business rules and workflows behind typed contracts.
4. Prove equivalence and operate the strangler safely.
5. Only then integrate a forward-engineered domain design and new delivery adapters.

Renaming paragraphs, introducing getters on the entire state, or moving fields into a superclass does not satisfy the objective. A faithful implementation behind a new interface is a useful first seam, but is not a completed modernization.

## 2. Existing implementation and what to reuse

The [README](README.md) describes the parity results, capability kits and known limits. The current source contains additional deterministic modernization work; use source and freshly generated evidence, not historical counts alone, when establishing the baseline.

| Existing surface | Useful foundation | Limitation relevant to this plan |
|---|---|---|
| [ProgramStateProjection](MlCpu/src/MlCpu.Cobol.Analysis/Projections/ProgramStateProjection.cs) | Separates generated storage declarations from procedure text. | Emits `Prog_<PID> extends State_<PID>` with protected storage. Business code remains directly coupled to every field and to `CobolBase`. |
| [ProjectionPipeline](MlCpu/src/MlCpu.Cobol.Analysis/Projections/ProjectionPipeline.cs) | Ordered, ledgered projections with explicit skip reasons. | Broad source-text rewrites are unsuitable as the primary mechanism for new alias-sensitive transformations. |
| [SkeletonEmitter](java-sidecar/src/main/java/com/mlcpu/sidecar/SkeletonEmitter.java) | Lowers the resolved semantic tree, including MOVE, conditions and CALL. | Generated procedures use runtime fields, numeric helpers and paragraph dispatch directly. |
| [CobolBase](java-sidecar/src/main/java/com/mlcpu/rt/CobolBase.java) | Invocation lifecycle, LOCAL-STORAGE, LINKAGE binding, paragraph execution and characterization entry points. | Storage, invocation control and test access are intertwined. Decoded `ProgramState` results do not prove byte or alias equivalence. |
| [RecordKitBuilder](MlCpu/src/MlCpu.CobolKb.Knowledge/RecordKit/RecordKitBuilder.cs) | Generated record views, codecs, initialization and accessor differential tests. | A byte-backed record is a compatibility type, not automatically a domain entity. Large records can be omitted with notes. |
| [CapabilityKitBuilder](MlCpu/src/MlCpu.Host/Services/Workspace/CapabilityKitBuilder.cs) | Step/subprogram contracts, scenarios, CALL traces, record layouts and acceptance resources. | Generic entries initially delegate to faithful code; generation must stop depending on someone replacing placeholders. |
| [ProgramRegistry](java-sidecar/src/main/java/com/mlcpu/rt/ProgramRegistry.java) and [JobEngine](java-sidecar/src/main/java/com/mlcpu/rt/JobEngine.java) | Per-program implementation selection, modern subprogram registration and faithful bridges. | Registry/configuration state is process-wide. Some modern BY REFERENCE arguments are copied in/out, which is insufficient for arbitrary overlapping aliases. |
| [StepEquivalence](java-sidecar/src/main/java/com/mlcpu/rt/StepEquivalence.java) | Faithful/normalized replay and byte comparisons. | Explicitly not thread-safe; shadowing must isolate run units and compare the complete observable output set. |
| [MlCpu.Modernize](MlCpu/src/MlCpu.Modernize/Modernizer.cs), [AppWriter](MlCpu/src/MlCpu.Modernize/AppWriter.cs), [LiftCalls](MlCpu/src/MlCpu.Modernize/LiftCalls.cs) | Typed Java IR, deterministic lifting and generated application classes. | Much of the lifting recognizes J3S frames, pools and known literal entries. Do not assume it modernizes arbitrary COBOL estates. Application classes also retain records/files as internal mutable fields. |
| [Strangler](MlCpu/src/MlCpu.Modernize/Strangler.cs) | Existing generated step port and LEGACY/MODERN/SHADOW implementations. | Generalize and connect this seam to published estates instead of creating a competing routing mechanism. |

Preserve the faithful emitter and existing exports. Add an opt-in modernization mode with explicit manifest versioning and rollback. Reuse the typed Java IR where applicable, but add a generic semantic-model path rather than extending J3S-specific heuristics to unrelated programs.

## 3. Target architecture

```text
Resolved COBOL model + layouts + CFG/DDG + CALL graph + JCL/DD plans
                         |
              deterministic modernization planner
                         |
      +------------------+-----------------------+
      |                  |                       |
 frozen faithful    generated compatibility   generated modern Java
 implementation     storage/codecs/adapters   contracts/rules/workflows
      |                  |                       |
      +---------- capability strangler ---------+
                         |
                published job/service entry

Later: forward-engineered domain implementation
                         |
           same typed capability contract
                         |
               capability strangler
```

Proposed logical boundaries, using the estate's existing package/module taxonomy where possible:

| Boundary | Owns | Must not own or expose |
|---|---|---|
| Compatibility storage | Byte images, typed record views, alias topology, PIC/USAGE codecs and field-level updates. | Business decisions, workflow execution, Spring dependencies. |
| Execution adapter | Program counter/PERFORM machinery, invocation frames, file handles, run-unit services and faithful bridges. | Domain entities masquerading as storage records. |
| Contracts | Immutable commands/results, domain-relevant identifiers, explicit outcomes and narrow ports. | `Area`, `CobField`, `CobFile`, entire program-state objects or mutable byte arrays. |
| Business rules | Typed calculations, validations and policy decisions with explicit inputs/results. | Storage inheritance, global registers, file opening, reflective program lookup. |
| Workflow | Ordering of rule calls and side effects through typed ports. | Arbitrary access to compatibility state. |
| Adapters | Mapping records to contracts, dirty-field patching, dataset access and legacy CALL bridges. | Hidden business rule changes or full-record re-encoding during a partial update. |
| Strangler | Implementation routing, isolated shadow execution, evidence and rollback. | A second implementation of business logic. |

Initially these can be packages within existing projects. Enforce dependencies using existing build/test mechanisms; create separate Gradle modules only where needed to enforce the boundary or where estate partitioning already provides them.

### Three explicit modernization levels

- **L1: isolated compatibility execution.** State uses composition, and a typed capability entry exists. Legacy-shaped logic may remain inside the adapter. Useful for safe migration, but not a clean domain implementation.
- **L2: extracted modern rules/workflows.** Selected business slices use narrow typed contracts and injected ports. Compatibility behavior remains at the boundary. This is the first level eligible for forward-engineering integration.
- **L3: proven semantic simplification.** Selected COBOL operations become ordinary Java operations only where their equivalence is demonstrated. Unsupported or alias-sensitive operations stay in compatibility code.

Publish levels per capability/slice, not one misleading estate-wide "modernized" flag. Every retained legacy dependency and blocked extraction has a source-linked reason.

---

# Part I - Deterministic Java modernization

## 4. M0 - Baseline and modernization inventory

**Work**

1. Regenerate representative faithful exports and run their existing checks.
2. Inventory program kind, state roots, record layouts, aliases, file effects, paragraph control flow, dynamic CALL sites, external targets and executed frontiers.
3. Pin source/copybook, semantic model, runtime, compiler/dialect, scenario and expected-output hashes. Pin the clock for exact parity.
4. Record the currently supported behavior for every MOVE/condition/CALL form. Distinguish implemented, tested, unverified and abstained behavior.
5. Produce a modernization ledger with stable program/statement/slice identities, inputs, writes, effects, alias closure, proposed contracts and skip reasons.
6. Separate generated output from human-owned implementations. Regeneration must not delete user classes or existing approval evidence.

**Gate**

The faithful lane passes freshly run tests and oracle comparisons. Missing tools/resources are BLOCKED, never PASS. Existing frontier programs remain visible but cannot be promoted as complete replacements.

**Pilot sequence**

- CBTRN02C: validation, indexed lookups, partial account updates and return-code behavior.
- CBACT04C: numeric calculation and record formatting.
- CBTRN03C: report images, VALUE text, editing and output order.
- SAM2, then SAM1: data-name CALL, linkage updates, variable records, TEST AFTER and print advancement.
- Small authored conformance programs: overlapping aliases, multi-target MOVE, recursive calls, retained working storage and unresolved CALLs.
- Large J3S records: scalability regression after the generic contract path works; no mandatory whole-heap snapshots or copies.

## 5. M1 - Replace storage inheritance with composition

**Work**

1. Generate a storage aggregate from layout metadata. It owns byte areas and record views; it contains no procedure methods and does not extend `CobolBase`.
2. Keep aliasing exact: REDEFINES, groups, FD records, tables and LINKAGE views refer to the same underlying image where COBOL does.
3. Let the compatibility program adapter own storage plus execution control. During the transition it may still extend `CobolBase`, delegating its storage hooks to the aggregate; the modern business rule never does.
4. Move file handles and paragraph/control state out of the storage aggregate into the execution adapter. Keep COBOL FILE STATUS bytes in their declared storage.
5. Model lifetimes explicitly:
   - WORKING-STORAGE persists according to run-unit/program lifecycle.
   - LOCAL-STORAGE is invocation-local, including recursive calls.
   - LINKAGE is a scoped binding to caller storage, not a copied domain object.
   - EXTERNAL storage is shared within its run unit.
   - INITIAL programs reset as the supported dialect requires.
   - Reentrant invocation must restore prior bindings/control state, not leave a parent's execution pointing at a child's linkage.
6. Introduce an explicit run-unit context for registry instances, shared storage, return code, clock, dialect settings and tracing. Bridge existing globals only inside serialized compatibility execution.
7. Until globals are eliminated or verified context-scoped, run independent jobs/shadow sides in separate processes. Do not claim concurrent service safety merely because a registry method is synchronized.

**Gate**

Raw area bytes, alias visibility, lifecycle effects and paragraph outcomes match the faithful lane. Storage aggregates have zero references to business-rule classes. Modern contracts/rules have zero imports of faithful state or byte-storage types.

## 6. M2 - Preserve MOVE and assignment behavior at the boundary

Reuse [CobRt MOVE behavior](java-sidecar/src/main/java/com/mlcpu/rt/CobRt.java) and the record kit as the compatibility authority. Do not duplicate conversion rules independently in every generated service.

**Work**

1. Carry sender/receiver categories, group status, offsets, dimensions, PICTURE, USAGE, scale, sign, encoding, JUSTIFIED and editing metadata through the planner.
2. Emit named record/field operations in adapters. Prefer domain-oriented operations such as applying an account-balance patch over exposing a generic field map to business logic.
3. Preserve group byte moves, elementary category conversion, padding/truncation, figurative constants, ALL literals, de-editing, initialization and untouched FILLER bytes.
4. For multi-receiver MOVE, establish sender and receiver/subscript evaluation rules against the target compiler, especially if destinations alias the sender. Preserve the required source capture and write order; do not substitute simultaneous or sequential Java assignments on assumption.
5. For MOVE CORRESPONDING, generate the qualified correspondence mapping from the semantic model. Preserve exclusions and conversion rules; unsupported forms remain explicitly blocked.
6. Preserve table/subscript/reference-modification behavior, including the supported bounds/ODO rules. A "safer" bounds check can be a behavior change and needs its own contract/version.
7. Generate patches from the actual assigned fields. On REWRITE, preserve unassigned bytes, original numeric sign encodings and overlapping storage effects. A dirty flag is not proof that re-encoding the whole record is safe.
8. Use immutable domain values in extracted rules. Apply COBOL storage constraints at each legacy-observable assignment boundary, not just at final output. Arithmetic intermediates, rounding, truncation, overflow and SIZE ERROR cannot be deferred indiscriminately.
9. Replace a compatibility operation with ordinary assignment only when range, scale, alias and observability analysis plus differential tests establish equivalence.

**Gate**

An explicit MOVE matrix passes for numeric/alphanumeric/edited/group forms, short/long receivers, signed/unsigned/packed/binary fields, repeated/overlapping receivers, tables and partial updates. Compare complete images including bytes outside the assigned fields.

## 7. M3 - Extract named conditions and business rules

**Work**

1. Generate named predicates from resolved condition expressions, retaining stable source IDs.
2. Keep level-88 conditions as views of the parent value, not independently stored booleans. Preserve multiple values, THRU ranges and SET TRUE/FALSE behavior.
3. Preserve numeric versus alphanumeric comparison, padding, collating sequence, edited values, class/sign tests and boolean grouping. Do not replace decimal comparisons with `double` or padded comparisons with `trim().equals(...)`.
4. Resolve abbreviated COBOL relations before Java generation. Preserve EVALUATE ordering, WHEN OTHER, ALSO tuples and condition operand evaluation; reorder nothing without evidence.
5. Find extraction slices using byte-range data/effect dependencies, not whole-paragraph labels alone. Mixed I/O/business paragraphs are expected in CardDemo.
6. Extract a slice only if inputs, assigned outputs, alias closure, error behavior and side-effect sequence can be represented. Conditions reading file status or shared linkage must receive current values, not stale snapshots.
7. Generate immutable input/result types for the slice and explicit status/outcome variants. Keep synthetic paragraph IDs as provenance, not public business names.
8. Generate reviewable proposed names from existing catalogs/source metadata. Names or domain groupings without approval remain provisional; unknown business intent is a review item, not an invented rule.
9. Preserve the faithful control driver for irreducible GO TO/PERFORM THRU/ALTER behavior. Structure reducible control flow using the typed IR; do not convert every paragraph into a public method or arbitrary lambda.

**Gate**

Truth tables and differential slice tests cover every supported condition form and every extracted branch. Business rules compile and run in-memory with typed values, without initializing a COBOL program or opening a dataset.

## 8. M4 - Handle static and dynamic CALL through one resolution seam

Reuse existing step/subprogram boundaries and registry bridges, but make their lifecycle and parameter contract explicit.

**Work**

1. Generate a CALL-site descriptor: source ID, static/data-name target, proven candidate set, parameter order/modes, layouts, RETURNING semantics and exception branches.
2. Use typed ports for targets with compatible known contracts. A data-name CALL remains runtime-selected; static analysis narrows candidates but does not remove runtime resolution.
3. Resolve through the run-unit context and generated registrations. Keep implementation choice outside business logic. No arbitrary `Class.forName` in modern rules.
4. At startup distinguish external/unavailable targets from registered faithful and modern implementations. At execution, unknown targets produce the existing CALL-not-found/ON EXCEPTION behavior; unsupported forms produce explicit frontiers. Do not return a success-shaped default.
5. Preserve caller/callee state lifetime across repeated calls and mixed faithful/modern routes. An initial rollout route is fixed for the run unit; changing implementations halfway through a stateful call sequence requires an explicit state-transfer design.
6. Use shared buffer views with offset/length/shape for BY REFERENCE. Whole-record and overlapping subfield arguments must observe each other's writes during the call, not just copy back at its end.
7. Generate an alias-aware internal call ABI where the existing `ModernProgram.call(byte[][])` cannot express shared overlapping views. Preserve that public ABI for compatible cases; refuse unsafe routing to it when alias semantics would be lost.
8. BY CONTENT is isolated from the caller. Distinguish BY VALUE and OMITTED explicitly; do not assume all non-reference arguments are BY CONTENT. Add dialect/interop support and oracle cases where required, otherwise fail closed.
9. Preserve RETURNING, caller return-code restoration, GOBACK/EXIT PROGRAM, STOP RUN, abends and exception timing. Exceptional writes to aliased arguments must be characterized too.
10. Inventory CANCEL support. If absent, leave it blocked until reset/unload semantics and retained state are implemented and tested. Registry reset is not automatically COBOL CANCEL.
11. Test caller-modern/callee-faithful, caller-faithful/callee-modern, both-modern and both-faithful through the same resolver.

**Gate**

Dynamic CALL tests exercise each registered candidate, padded names, changed target names, missing targets, repeated calls, INITIAL/LOCAL/EXTERNAL lifetime, overlapping parameters, nested/recursive calls and exception paths. Unsupported/unobserved candidate sets prevent whole-capability promotion.

## 9. M5 - Generate workflows and make the strangler operational

**Work**

1. Extract workflow orchestration after rule and effect seams exist. Workflows use command/result contracts and injected repository, call, clock and output ports.
2. Implement dataset adapters using existing DD plans and runtime utilities. Retain record formatting and ordering as compatibility responsibilities.
3. Connect the existing generated strangler to published jobs and subprogram resolution, with per-capability LEGACY, MODERN and SHADOW routing. Map existing `legacy`/`normalized` selectors rather than silently redefining them.
4. Start in LEGACY. Modern code is selected explicitly only when its evidence and approvals permit it.
5. In SHADOW, run both implementations from equivalent inputs and initial state in isolated contexts/processes and separate dataset directories. Legacy remains authoritative.
6. Preserve alias topology inside each shadow side without sharing mutable storage across sides. Snapshot only the necessary state; use chunked comparison/hashes for large images.
7. Modern shadow side effects never reach live files, repositories, events or callers. A future database/event adapter needs rollback or a sandbox plus an authoritative publication/outbox strategy.
8. Compare return/outcome, SYSOUT, complete dataset file sets, record bytes, deletion/creation, CALL argument effects, required retained state and ordered interaction transcripts.
9. On mismatch, publish a source-linked report and retain LEGACY. Do not rerun a failed modern implementation against live inputs as an implicit fallback: partial writes may already have happened.
10. Roll back by changing routing for the next safe run unit. Stateful in-flight rollback needs an explicit drain/checkpoint/state migration policy.

**Gate**

Shadow has zero externally visible duplicate side effects. Deliberately incorrect rule, condition, MOVE and CALL mutations produce differences and block promotion. Routing and rollback tests include stateful subprograms and partial failures.

## 10. M6 - Publish an integration-ready Java kit

Generate the following as part of normal publish, without a model-specific action:

- Typed capability commands/results, port interfaces and an implementation registration seam.
- Modern rules/workflows for eligible slices, compatibility adapters for remaining behavior and a frozen faithful baseline.
- Record kits/codecs and alias-aware call support restricted to compatibility packages.
- Source maps from generated rules, predicates, assignments and CALL sites to COBOL statements/copybooks.
- Contract/layout/dialect manifests, allowed dependencies, routing defaults and modernization levels.
- Equivalence tests, oracle fixtures, truth tables, storage/CALL tests and fake port implementations.
- A runnable minimal human-owned implementation example against the same contracts, not a scaffold that imports program state.
- A human-readable behavior specification: business decisions, preserved quirks, errors, state lifetime, I/O ordering, dynamic targets and known gaps.
- Evidence hashes and explicit APPLIED/SKIPPED/BLOCKED decisions. A faithful bridge must identify itself as L1, not an extracted business rule.

Extend [ConversionService](MlCpu/src/MlCpu.Host/Services/Workspace/ConversionService.cs) and [JavaProjectAssembler](MlCpu/src/MlCpu.Host/Services/JavaProjectAssembler.cs) so CLI/headless publish and the workbench publish the same result. Introduce a generic planner over the existing resolved semantic model and typed Java IR; do not reparse generated Java as the authoritative semantics.

Keep existing capability imports usable. If new contracts require a manifest/API version, generate adapters and explicit migration diagnostics. Hash changes invalidate affected evidence/designs/approvals; never carry forward an approval for a different contract.

Use stable semantic IDs for provenance and rejection mapping, not Java stack-frame names or line numbers that change during refactoring. Keep legacy names in compatibility metadata/source maps, not in the forward domain API.

The requirements packet currently omits some VALUE information, as the [README's known gaps](README.md#not-done-yet) notes. Use canonical layout/AST information now; fixing the packet must version it and regenerate downstream hashes instead of quietly changing the old packet.

**Modernization completion gate**

1. Offline generation, Java compilation and tests succeed with no model service configured.
2. Every pilot capability has an executable typed seam; extracted L2 business code has zero byte-storage/runtime/state dependencies.
3. No inheritance from generated storage, no whole-state parameter and no generic field bag appears in L2 contracts.
4. Every extracted rule has explicit input/output/effect metadata, branch witnesses and passing differential tests.
5. Every supported MOVE/condition/CALL form used by the pilots has a tested behavior; unhandled cases are visible and block the affected promotion.
6. Fresh oracle parity passes for the complete required outputs. Approved volatile masks apply only to proven clock-derived fields; SYSOUT normalization is limited to the existing CRLF/LF exception.
7. The strangler isolates shadow runs, reports drift and rolls back at a defined run-unit boundary.
8. Regeneration is deterministic and leaves human-owned implementation files unchanged.

Do not relax existing promotion policies. In particular, [OrchestrationPromotionPolicy](MlCpu/src/MlCpu.Cobol.Analysis/Projections/OrchestrationProjection.cs) requires complete coverage/mutation evidence for its promoted candidate. Existing normalized-capability thresholds and SME approval requirements remain applicable; the new lane records which policy governs each slice.

---

# Part II - Forward engineering after modernization

## Existing forward-engineering assets to reuse

The repository already has a substantial forward-engineering lane. Do not rebuild it as another prompt-driven modernization path. The [forward pipeline documentation](docs/FORWARD-PIPELINE.md) separates deterministic generation/integration stages from model design, implementation and host-hole authoring.

| Existing artifact | Planned reuse |
|---|---|
| [Forward package descriptor](forward/loans/forward.json), schema `mlcpu.forward-package.v1` | Keep the package/application/module/slice/rule/pool/host contract as the stage-two package seam. Generalize through descriptors rather than hard-coded loan paths. |
| [Fixed Loans API](forward/loans/service/loans-api/src/main/java/com/mlcpu/loans/api) | Reuse the typed API and version it deliberately. It was authored in the SME role; it is not proof that COBOL analysis alone discovered the target domain. |
| [LoansPorts](forward/loans/service/loans-api/src/main/java/com/mlcpu/loans/api/ports/LoansPorts.java) | Reference pattern for explicit IDs, loan book, ledger, billing, financial accounts and clock ports. Mutable servicing state belongs behind ports, not in global domain state. |
| [Design rules](forward/loans/RULES.md) | Reuse executable architecture constraints: immutable domain fields, no mutable statics, no direct I/O/randomness/clock, no frameworks, typed rejections and no source-system names. Apply corresponding checks to L2 code too. |
| [Implementation source seam](forward/loans/service/build.gradle.kts) | Reuse `-PimplDir` for a supplied implementation without editing generated sources. Use the existing `IMPL` and `HOLES` selections for engine/host implementations. |
| [Legacy mapping](forward/loans/legacy/legacy-mapping.yaml) | Generate the anti-corruption codec/inverse codec from reviewed mapping metadata. Do not hand-copy record offsets into the forward implementation. Preserve generated-file hash locks. |
| [Host mapping](forward/loans/host/host-mapping.yaml) | Preserve the explicit generated-versus-hole distinction for host ports. User-owned holes implement reviewed contracts; publishing must never silently overwrite them. |
| [Reference engine](forward/loans/reference) and [reference host implementations](forward/loans/host/reference) | Model-free loan pilot and regression fixtures using the same integration seams. They are authored reference implementations, not a general generator for every estate. |
| [ReconciliationOutcome](forward/loans/service/loans-api/src/main/java/com/mlcpu/loans/api/migration/ReconciliationOutcome.java) and [decision catalog](forward/loans/SPEC.md) | Distinguish MATCH, EXPLAINED, DIFF and LEGACY_REJECT with reviewed decision IDs. |
| [Forward gate](forward/loans/tools/Test-ForwardGate.ps1) | Preserve package architecture/scenario/traceability/leak/cross-check tests and the existing per-module mutation threshold of at least 85%. Use identical gates for human and optional LLM implementations. |

Recorded reference results show that a model-free integration path exists; rerun it rather than treating stored gate reports as current evidence. Explicitly select the reference engine and host implementations for the loan pilot. Keep model-evaluation queues, prompts, sampling configurations, embeddings and repair loops out of the product generation/build/publish dependency graph.

The trust chain must remain explicit:

```text
GnuCOBOL == faithful Java == deterministically modernized Java
                                      |
                         reviewed anti-corruption mapping
                                      |
                  forward domain implementation + reconciliation
```

The first two equality checks require exact preserved behavior. Forward reconciliation can report EXPLAINED only for a specific approved decision and its defined differing fields/effects; it must not turn arbitrary mismatches into acceptable results. Decisions are not volatile-field masks.

Reuse the existing deterministic rendering, locked-output, host integration, reconciliation/shadow and host-regression gates (TG1/TG3, G10/G11/G12) where the package defines them. G11 shadow is mandatory for the forward pilot: scenario suites alone previously missed a rounding defect. Package-specific thresholds do not replace or weaken the existing modernization promotion policies.

## 11. F0 - Specify the target domain and compatibility obligations

Begin only when the selected capability passes the modernization gate at L2 or above. A forward design can be reviewed earlier, but it must not drive speculative changes to COBOL semantics.

1. Establish domain vocabulary, bounded context, use cases, identities, invariants, ownership and transaction boundaries with a human/domain expert.
2. Map each target use case to modern capability contracts and preserved rule IDs. Distinguish confirmed rules from inherited storage quirks and unverified assumptions.
3. Separate immutable domain values from legacy record views. Do not copy every WORKING-STORAGE field into a domain entity.
4. Define repository, clock, identifier, external-program and event ports in business language.
5. Classify each requirement as preserved behavior, intentionally changed behavior, new behavior or unresolved. Changed behavior needs a version/flag and new acceptance criteria; it is not presented as byte-equivalent.
6. Decide how stateful subprogram memory maps to explicit domain/session state. Request-scoped beans cannot silently replace persistent run-unit state.
7. Preserve legacy interfaces with anti-corruption adapters. New domain code does not see PIC, DD names, paragraph PCs or dynamic program strings.
8. Reuse reviewed state-lifetime decisions where applicable. The loan catalog's D-9 moves a run-level calculation context to one calculation per loan; apply that pattern only after checking the selected use case, never as an automatic rewrite of retained WORKING-STORAGE.

**Gate:** a reviewed contract/rule mapping and explicit state/transaction policy exist. Unknown rules or ambiguous ownership block the affected use case, not the entire modernization pipeline.

### Preserved decimal-capacity obligations and forward decisions

These are inherited implementation limits, not new business term/event limits or a global BigDecimal precision cap.
Modernization preserves their observable abend; a forward design may change them only through a specific reviewed
decision with versioned acceptance criteria and reconciliation evidence.

| Estate/probe | Last non-abending / first abending | Legacy obligation |
|---|---|---|
| Loop 0 loanamorti terms | 1,331 / 1,332 | FSJBDEC stored/register limb capacity; abend 37 at the known decimal sites |
| Loop 0 loanamorti event store | 312 / 313 events | Per-loan 2,500 large-value store and new-loan reset; preservation verified |
| Loop 3 MP, 5.25% or 3.33%, 360 months | 227 / 228 events | Pow/dependent-operation store accounting, JBDO:67; exact preservation in progress |
| Loop 3 MP, 24%, 600 months | 192 / 193 events | Same store limit with different allocation path; MT equivalents required |

The big-store source variants have limits of 2,223 stored limbs, 2,225 operation-register limbs, scale +/-999,999,999
and 2,500 stored large values per loan. Other detected variants, including Joda's compact-only FSJBDEC, instead
bound the unscaled value and arithmetic working steps to S9(31), with no large-value store. Variant/source provenance
must therefore accompany the compatibility contract; never apply either model globally to ordinary BigDecimal.
Event thresholds depend on the arithmetic path, input values and scale; never
replace them with an unproven blanket "maximum events" rule. Before relaxing a limit, decide how former abend inputs
should calculate or reject, who owns the per-loan lifecycle, and how partial outputs and return codes reconcile.

## 12. F1 - Integrate a user- or LLM-authored design

1. Publish the same compact integration kit to either consumer: typed API, rule catalog, manifests, examples, test resources, allowed dependencies and implementation skeleton.
2. Author the domain implementation in a separate human-owned source root/module. Generation owns designated generated contracts/adapters only; preserve the existing reviewed API and generated-file locks. Do not use source replacement or SEARCH/REPLACE as the integration mechanism.
3. Compile the new implementation against the public contracts and fake ports without the faithful implementation on its compile classpath.
4. Register it through the existing capability selector/strangler. Bridge any contract mismatch in a named adapter, not by reaching into program state.
5. Run domain invariant tests, contract tests and differential replay. Compare all legacy-visible effects for behavior marked preserved.
6. Require human review for business intent and behavior changes. Passing generated tests alone is not approval of a new domain model.
7. Preserve independent/hidden acceptance cases where the existing evaluation supplies them. Share the public integration contract and worked examples, not the hidden acceptance answers.

**Gate:** a manually authored representative implementation integrates without editing regenerated Java, importing storage classes or requiring a model to run the acceptance suite.

## 13. F2 - Add service delivery and persistence deliberately

1. Add HTTP, messaging, scheduling or batch adapters only for chosen use cases; do not assume every COBOL program should become an endpoint or microservice.
2. Place Spring concerns in delivery/wiring adapters. Keep domain rules usable as plain Java.
3. Map domain errors to explicit service outcomes while preserving legacy return codes in the legacy-facing adapter.
4. Introduce persistence schema and migration/reconciliation rules independently of COBOL byte layout. Preserve original images where exact legacy round trips are required.
5. Define idempotency, transaction boundaries, retry rules, concurrency, ordering and event publication before connecting live side effects.
6. Preserve dynamic CALL behavior through approved target adapters; do not expose an unrestricted reflective "execute program" API.
7. Run service tests separately from batch parity. Intentionally changed APIs and behavior have their own versioned tests and approvals.

**Gate:** end-to-end tests establish transaction/ordering/error behavior, and shadow execution remains side-effect-safe with the selected delivery adapters.

## 14. F3 - Promote and retire legacy slices incrementally

1. Promote one capability/slice at a time after current evidence, coverage/mutation checks and human approval.
2. Start with replay, then shadow/canary where operationally safe, then explicit MODERN routing.
3. Observe drift, latency, resource use, outcome distribution and route decisions with contract/source/evidence identifiers.
4. Keep rollback artifacts and the faithful implementation while dependent callers still use legacy contracts or retained-state behavior.
5. Remove a legacy slice only after dependency/call-graph checks, migration validation and the agreed operational observation period.
6. Retain replay fixtures, business rule provenance and compatibility adapters still needed by remaining consumers.

## 15. Work packages and dependencies

| Work package | Depends on | Main deliverable | Acceptance evidence |
|---|---|---|---|
| M0 baseline/inventory | None | Fresh baseline and semantic modernization ledger. | Oracle/conformance results with pinned hashes. |
| M1 state composition/lifetimes | M0 | Storage aggregate, invocation adapter and run-unit isolation. | Byte/alias/lifecycle tests; dependency checks. |
| M2 MOVE semantics | M1 | Generated conversion/patch operations. | MOVE matrix and untouched-byte assertions. |
| M3 predicates/rules | M1, M2 for assignments in selected slices | Typed rule implementations and condition descriptors. | Truth tables, branch witnesses, differential slice tests. |
| M4 CALL resolution/ABI | M1; M2 for argument conversion | Context-scoped routing and alias-safe adapters. | Mixed-route, lifecycle and dynamic CALL corpus. |
| M5 workflow/strangler | M3, M4 for called workflows | Isolated shadow and operational selection. | Transcript/side-effect/rollback tests. |
| M6 publish kit | M2-M5 | Deterministic integration-ready estate export. | Offline generation, architecture and full pilot parity gates. |
| F0 target contracts/domain | M6 for selected capability | Reviewed rule/domain/transaction mapping. | Human/domain approval and traceability. |
| F1 design integration | F0 | Human-owned domain implementation. | Independent compile, contracts and equivalence. |
| F2 delivery/persistence | F1 | Selected service adapters and migration. | Service, persistence and side-effect isolation tests. |
| F3 promotion/retirement | F1; F2 if live service delivery is involved | Incremental rollout and retirement evidence. | Current policy approvals and operational evidence. |

Do not estimate completion from lines moved or classes generated. Track eligible/extracted/blocked rules, surviving storage dependencies, tested CALL sites, parity failures and integration effort.

## 16. Validation strategy and scope limits

- Extend existing [CobolSemanticsTest](java-sidecar/src/test/java/com/mlcpu/rt/CobolSemanticsTest.java), [NumericMoveFastPathPropertyTest](java-sidecar/src/test/java/com/mlcpu/rt/NumericMoveFastPathPropertyTest.java) and [ByReferenceStorageTest](java-sidecar/src/test/java/com/mlcpu/rt/ByReferenceStorageTest.java) rather than starting another semantic test framework.
- Extend the conformance corpus for generation, truth tables, lifecycle/alias behavior and oracle parity. Add negative fixtures proving that a blocked extraction never becomes a promoted implementation.
- Reuse generated record-kit tests and capability acceptance scenarios, but add hand-computed expectations and oracle checks: two Java lanes can share the same bug.
- Run narrow affected tests first, then the selected pilot export's tests and job parity. Run broader gates when generator/runtime changes affect other programs.
- Verify file creation/deletion and unexpected outputs, not only files named in expected fixtures. Verify trace ordering and state bytes where decoded values hide differences.
- Benchmark representative large records and repeated dynamic calls against the pinned baseline. Establish numerical budgets before promotion; no full-image copy per CALL or unbounded tracing is accepted by design.
- Exercise concurrent invocations only after isolation is implemented; until then verify process separation/serialization and advertise that limitation.
- Once run-unit isolation is implemented, require repeated/interleaved runs in one JVM to match isolated-process runs, including stateful CALLs and exceptional exits.
- Invoke deterministic stages through Windows-compatible commands/wrappers in this environment. Existing shell orchestration is reuse evidence, not an assumption that a shell script runs unchanged in PowerShell.
- Preserve current abstention on unsupported constructs. CICS/SQL modernization, arbitrary foreign-language BY VALUE interop, JCL COND/GDG changes and complete mainframe emulation are separate work unless required by a selected pilot.
- Validate partial outputs and exceptional CLOSE/flush behavior with new oracle scenarios; neither historical documentation nor a cleanup helper alone proves abend parity.

## 17. Recommended first implementation increment

Start with a CBTRN02C compatibility export plus a small alias-sensitive fixture:

1. Freeze the faithful output and its acceptance resources.
2. Generate composed storage and a typed posting capability entry.
3. Extract one validation slice with immutable input/result types and no storage imports.
4. Apply its result through a field-specific record patch.
5. Add one data-name CALL fixture with overlapping BY REFERENCE arguments and a faithful/modern target switch.
6. Run both through isolated SHADOW and prove that deliberately introduced differences are rejected.
7. Publish a kit and integrate a small handwritten replacement without modifying generated files.

This increment proves the core separation and integration seam before attempting an estate-wide domain redesign.
