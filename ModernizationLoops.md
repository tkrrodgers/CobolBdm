# Modernization loops: plan and ledger

Lead: Claude (desktop session, architect role on the JOBOL hub). Executor: the VS Code pipeline session (GPT Sol 6.1 generates, Claude Opus 5.5 checks).
Mandate (Tomrodgers, 2026-10-31): up to 5 modernization loops over complex COBOL estates that were originally Java. The lead approves each loop's completion and hands off the next. Each new loop must show no regression on a baseline.

## 1. Definition of "modernized" (the loop's exit gate)

A loop is approved only when all of these hold for the estate, all produced by the generator (MlCpu.Modernize) with no hand edits to generated Java:

| Gate | Evidence |
|---|---|
| JOBOL | `audit_run` under ruleset 1.1.0 (`0018a27e11dbcfd2`): gate PASS (estate index ≤ 25, no BLOCKER, no modern type graded F), compared with the loop's iteration-0 baseline, with no lane or rule change. |
| Build | The generated estate compiles: `javac --release 21 -Xlint:all` or `gradle --offline build`, exit 0. |
| Parity | GN4 SHADOW passes every scenario of the estate: 0 dataset differences, equal return codes, and SYSOUT equal less only an approved, pinned exclusion. Error-path cases are run where the estate has a harness for them. |
| No regression | The same generator, re-run on the regression baselines (§3), keeps their JOBOL gate PASS with change ≤ 0 and their GN4 parity green. |
| Review | The architect has read the worst types' code and accepted the review on the hub. |

Approved deviations so far (decisions log, JavaJOBOLAuditor.md §10):
- SYSOUT exclusion v2 for the J3S END statistics block.
- The 31-digit decimal limit is preserved (abend 37).

## 2. Loop sequence (complexity rises; every estate began as Java)

| Loop | Estate (CobolBdm project) | Origin | Size | Why it is next |
|---|---|---|---|---|
| 0 (in progress) | `fineract-xl` | J3S (Java subset compiler) over Fineract's progressive loan | 146 programs, 211K lines, 46 scenarios | The current round: the batch track is done; the lift track (31-digit guard, labeled blocks, temporaries, numbered locals, truncated names) is in progress. |
| 1 | `fineract-cobol` | Tier-1 generator J1–J5 (`gen lower`) over Fineract | 18 programs, 5.7K lines, 58 scenarios | Not J3S: a different COBOL shape (no J3S frames or pools). It tests whether MlCpu.Modernize generalizes beyond the J3S template, the main risk named in modernizeJavaCode.md §2. |
| 2 | `fineract-legacy` | J8 "legacy" estate over Fineract | 21 programs, 7.9K lines, 58 scenarios | COBOL written in legacy idioms on purpose: the most JOBOL-prone input. |
| (1b) | `fineract-cobol-cr` | Tier-1 clean-room build | 18 programs, 5.2K lines, 58 scenarios | **Folded into loop 1's exit gate (decision, 21:40).** The dry run gives numbers identical to fineract-cobol (34 functions, residue 87, 31 javac errors, 93.2). As a separate loop it adds almost nothing; as a check it proves the generator does not depend on authoring style. |
| 3–5 | New J3S targets, prepared by the lead during loops 1–2 | J3S-T (docs/J3S.md §17) | TBD | Candidates by fit (research note 2026-10-31): Fineract savings interest posting and accruals (source already vendored), Joda-Money, `strata-basics` date logic. Not ready: jmortgage (Guava and Joda-Time have no emulation, and it is double-based). |

Each loop's iteration 0 is the current generator's output on the estate, audited under 1.1.0. That report is the loop's baseline.

## 3. Regression baselines

| Baseline | Report | Re-run when |
|---|---|---|
| loanamorti | `20261101-063821-d01037` (1.6 A; earlier 049263, e020cf, 3ae043, bbd188) | Every loop |
| fineract-xl final | `20261101-063822-989c7b` (15.0 B; earlier 1858b3, f58389) | Every loop |
| fineract-cobol final | `20261101-063819-195b1b` (12.3 B) | Every loop |
| fineract-cobol-cr final | `20261101-063820-ae8237` (12.3 B) | Every loop |
| fineract-legacy final | `20261101-063817-9435ca` (14.4 B) | Every loop |
| Each approved loop's final report | Added on approval | Every later loop |

## 4. Protocol on the hub

1. Lead → pipeline: a `feedback` message with kind "loop handoff", carrying the estate, its project directory, scenarios, legacy binaries, known risks and the gates.
2. Pipeline: generate iteration 0, then `audit_run` with label "loopN iter0". That report is the baseline.
3. Pipeline iterates in the generator, auditing each step against the baseline and keeping parity green, then sends a `review_request` with the reports and evidence.
4. Lead reviews the code and the evidence, then accepts or returns a change list.
5. On acceptance the lead runs or verifies the regression baselines, records the loop in §5, and hands off the next estate.

Session continuity: the pipeline session must keep a watcher on its hub inbox and not end its session. If it ends or stops responding, the lead continues the work itself (the generator and the gates run from this machine).

## 5. Ledger

| Loop | Estate | Iteration-0 report | Final report | Regression check | Approved |
|---|---|---|---|---|---|
| 0 | fineract-xl | `20261031-200550-7bab1a` (36.0 C) | `20261031-220149-f58389` (15.0 B, PASS; GN4 46/46, gn4err 21/21) | loanamorti `20261031-220151-e020cf` 1.6 A, change 0 | **Approved 22:05** (lead verified the audit and the lane diff) |
| 1 | fineract-cobol (+cr) | `20261031-221108-aa8538` (93.2 F) | `20261101-013204-d1d187` (12.4 B, PASS; GN4 58/58); cr `a11124` 12.4 B, GN4 58/58. Kernels replaced by java.time / RoundingMode behind differential gates (21.1M and 2.08M cases, 0 differences, mutants caught) | loanamorti `049263` 1.6, fineract-xl `1858b3` 15.0: change 0 | **Approved 01:55** (lead verified the audit and the compat-lane logic) |
| 2 | fineract-legacy | `20261101-015654-c489dc` (42.9, after a one-line crash fix) | `20261101-063817-9435ca` (14.4 B, PASS; strict parity 58/58 + 13/13 + 640/640 under the runtime-diagnostics profile sha256 14f228c5, the user's option A; no exclusion) | all 4 green (cobol/cr -0.1, others 0) | **Final 06:40** (approved 04:55) with residual risk: about 10 input-dependent rewrites not exercised, 79 partly covered (carry-over: domain inputs) |
| 3 | kontor-zins (J3S-T, new) | `20261101-071428-591197` (8.0 A) | `20261101-093251-b2dcf1` (0.0 A; GN4 56/56 strict under v2.1; exact FSJBDEC store emulation, 31 capacity boundaries, 20 mutants) | 5 anchors held (loanamorti 0.0, xl 14.5, others 0.0) | **Approved 10:30** |
| 4 | Joda-Money (J3S-T, new) | `20261101-105614-b6304d` (11.4 B, but residue 11 and 51 javac errors) | `20261101-132556-203e70` (1.5 A; strict 41/41 under joda-statistics v1; real JDK regex/maps/resources, no emulation) | 6 anchors 0.0 | **Approved 13:30** |

Forward backlog: FSJBDEC decimal capacity abend 37 (2223 stored limbs / 2225 register limbs / scale +-999,999,999 / 2500 stored values a loan). Measured boundaries: loanamorti term 1331/1332; store 312/313 CD events.

Scope note (02:00): loop 1's kernel substitution is approved as **estate-scoped reachable equivalence**. RoundingKernel is not claimed equivalent for arbitrary inputs: for S/P with a digit count below -9968, legacy continues with the retained WS-SHIFT, which is defined, call-history-dependent behaviour. Loop 2 adds a fail-closed frontier and negative tests. Rule for all kernel replacements: "equivalent on the gated domain, frontier elsewhere".

Loop 3 prepared (03:40): kontor-zins J3S estate, G2 6/6 byte-identical, packaged (loops/L3-kontor-zins.md). Loop 4 in preparation: Joda-Money with J3S emulations on feature/l3-target.

Loop 4 prepared (05:20): Joda-Money J3S estate, G2 6/6 byte-identical, 65 programs / 65.8K lines (loops/L4-joda-money.md). J3S emulations on feature/l3-target 21735cd (G0 569,713 records with 0 differences, 11/11 mutants caught). **Open J3S bug for Tomrodgers:** Object `==` ignores the value for boxed values; reachability in Fineract not yet analysed.

Loop 2 residual coverage (07:15): an isolated FNEM0800 harness (native driver against the frozen source, modern harness against the iter6 classes) gives 5/5 exact and executes LF230, LF227, LF228, LF250 and LF251. This is isolation evidence, not batch reachability. Remaining residuals: LF161/172, LF180/184, LF218, LF070/154/179.

Forward backlog addition: kontor-zins pow store capacity, 228th MP at 5.25%/360 and 193rd at 24%/600 (abend 37 AT JBDO:67).

**Correction (10:10):** fineract-xl's GN4 46/46 (loop 0 and the regressions since) checked SYSOUT only by ordered inclusion, plus datasets and RC, not line for line. Under the strict check, all 46 fail on 353 J3S FSGN0100 heap-statistics lines (prefix FSGN0100, hyphenated pool names) that v2/v2.1 do not cover. Datasets and RC match in every case. An FSGN0100-only statistics rule is pending Tomrodgers's approval. Until then, fineract-xl's SYSOUT parity is NOT proven. v2.1 (statistics-only, sha256 7d7905e2) is validated: KZ 50/50, loanamorti 7/7 + 21/21 with 0 exclusions, FN* 58/58 each.

Loop 3 clarification (10:38): DecimalLimit.doRound implements the full native carry-retry loop. The surviving pow-doround-once mutant is a test-coverage gap (no fixture reaches a second pass), not a runtime frontier. FSGN0100 statistics policy pinned, sha256 972f9d8e; its policy tests pass; the fineract-xl 46 strict re-run is in progress.

fineract-xl strict SYSOUT (10:52): the 30 schedule cases PASS strict under the FSGN rule (972f9d8e); the 16 event cases await the user-approved FSLE rule. Loop 4 onboarded: fresh oracle 6/6 (12,845 records, 811 rejects, 65 programs). Joda-Money's HJC-2 pool scope is NOT authorized yet and will need a user decision if it arises.

**Closed (11:03): fineract-xl strict SYSOUT proven, 46/46** (30 FSLS under FSGN rule 972f9d8e, 16 FSLE under FSLE rule 5a1159de; exactly 353 approved statistics lines each; the rest of SYSOUT exact; datasets 0 differences; RC equal). The v2.1/profile migration is verified on all estates: loanamorti 7/7 + 21/21 (implicit-CLOSE 2/2 compared, 0 excluded), FN* 58/58 each, KZ 50/50 and 56/56; the profile-off negative control fails as expected. Loop 4 (Joda-Money): onboarded 6/6; generator ownership handed to the loop 4 owner. HJC-2 pool scope not authorized (needs a user decision if needed).

## Programme status (13:30): COMPLETE, 5 loops (0-4), 7 estates modernized
| Estate | Final | Score | Strict parity |
|---|---|---|---|
| fineract-xl | f36f0c | 14.5 B | 46/46 (FSLS/FSLE statistics rules) |
| loanamorti | 7edea1 | 0.0 A | 7/7 + 21/21 |
| fineract-cobol | a3fb54 | 12.3 B | 58/58 |
| fineract-cobol-cr | be10bb | 12.3 B | 58/58 |
| fineract-legacy | e49f5b | 14.4 B | 58 + 13 + 640 |
| kontor-zins | c2e23d | 0.0 A | 56/56 |
| joda-money | 203e70 | 1.5 A | 41/41 |

Disclosed risks: fineract-xl has 123 unchecked decimal ops and 106 unpreserved after-call JOBFAILs; loop 2 residual coverage; the joda replay gaps. Next estate: Tomrodgers decides (candidate: CardDemo, native COBOL, deferred).

**Correction of scope (18:20, A0 ablation):** the J3S estates (fineract-xl, loanamorti, kontor-zins, joda-money) were modernized through a lift path that depends on J3S provenance comments. With those comments stripped, nothing lifts or builds. Their results stand as J3S round-trip results with strict parity, but they do not show the modernizer works on legacy COBOL. Evidence that does: fineract-cobol, fineract-cobol-cr and fineract-legacy (the generic lift, no provenance). The next real tests are CardDemo and the `legacy` session's legacy-profile estates.
