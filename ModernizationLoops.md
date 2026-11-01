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
| loanamorti, iteration 7 | `20261031-202101-3ae043` (1.6 A, PASS); 1.1 reference `20261031-193948-bbd188` | Every loop |
| fineract-xl final (end of loop 0) | Set when loop 0 is approved | Every loop that changes a shared generator pass |
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
| 0 | fineract-xl | `20261031-200550-7bab1a` (1.1 re-audit of iteration 4) | | | |
