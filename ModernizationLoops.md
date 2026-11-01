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
| loanamorti | `20261101-044117-48e0fa` (1.6 A; earlier 049263, e020cf, 3ae043, bbd188) | Every loop |
| fineract-xl final | `20261101-044118-21a4ba` (15.0 B; earlier 1858b3, f58389) | Every loop |
| fineract-cobol final | `20261101-044115-fa4b8d` (12.3 B) | Every loop |
| fineract-cobol-cr final | `20261101-044116-02514a` (12.3 B) | Every loop |
| fineract-legacy final | `20261101-044113-9a46c2` (14.4 B) | Every loop |
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
| 2 | fineract-legacy | `20261101-015654-c489dc` (42.9, after a one-line crash fix) | `20261101-044113-9a46c2` (14.4 B, PASS; GN4 58/58; 340 seeded cases, 327 PASS, 13 implicit-CLOSE handled by v3) | all 4 green (cobol/cr -0.1, others 0) | **Approved 04:55** with residual risk: about 10 input-dependent rewrites not exercised, 79 partly covered (carry-over: domain inputs) |
| 3 | kontor-zins (J3S-T, new) | pending ("loop3 iter0" after onboarding) | | | Handed off 04:55 |

Forward backlog: FSJBDEC decimal capacity abend 37 (2223 stored limbs / 2225 register limbs / scale +-999,999,999 / 2500 stored values a loan). Measured boundaries: loanamorti term 1331/1332; store 312/313 CD events.

Scope note (02:00): loop 1's kernel substitution is approved as **estate-scoped reachable equivalence**. RoundingKernel is not claimed equivalent for arbitrary inputs: for S/P with a digit count below -9968, legacy continues with the retained WS-SHIFT, which is defined, call-history-dependent behaviour. Loop 2 adds a fail-closed frontier and negative tests. Rule for all kernel replacements: "equivalent on the gated domain, frontier elsewhere".

Loop 3 prepared (03:40): kontor-zins J3S estate, G2 6/6 byte-identical, packaged (loops/L3-kontor-zins.md). Loop 4 in preparation: Joda-Money with J3S emulations on feature/l3-target.
