# Forward-engineering evaluation protocol

**Status:** revision 3, adopted 2026-10-10. Phase 0 (the SME package) is built before any model runs; phases 1 and 2 run
on pipeline v2 (`PIPELINE-V2.md`). Phase 3 is deferred.

**Questions.**

1. Can Gemma 4, the workbench's model, *build into a new architecture*: design a service no one has drawn yet and
   implement it to tests it has not seen?
2. Is gpt-oss-120b a better *designer* than Gemma 4? It is the larger model (117B MoE against 31B dense) and may have
   seen more architecture and documentation in training. It could not code CardDemo well enough on 2026-10-06, but a
   design can be implemented by another model.

**Revision 3 (2026-10-10, owner's direction):**

* **Two participants.** Gemma 4 designs and implements. gpt-oss-120b designs only, and Gemma implements gpt-oss's
  design. Laguna S 2.1 and Inkling-Small are out of this round: the owner re-tests only Gemma on pipeline v2.
* **Arms S and X only.** Arm T (team) and the reviewer role are dropped, because there is no second coder.
* **Pipeline v2 for implementation.** The forward implementer gets v2's repairs and feedback (section 6).
* **Phase 3 (integration) deferred.** It does not bear on either question.
* **The Forward Engineering tab is added only if Gemma passes phase 2** (section 8).
* **SME work is concrete.** Phase 0 is built in `forward/cas/`, with the scorer and its blinding defined.

Revision 2 (2026-10-10) had four candidates and three arms; revision 1 had a single gate.

## 1. Why a forward-engineering test

The re-engineering test asks a model to restate one COBOL program as idiomatic Java on a given platform, judged byte for byte
against the faithful translation. That shows a model can preserve behaviour. It does not show it can:

* design a domain model and module boundaries no one has drawn yet;
* place several legacy programs' rules (from two unrelated estates) into one coherent service;
* satisfy architectural rules, not only output parity;
* build a feature that has no legacy equivalent;
* generalize to tests it has not seen (the re-engineering acceptance test is shown to the model).

Those are the skills the workbench needs once an estate is modernized and new work starts.

## 2. Participants

| Model | Evidence so far | Design (phase 1) | Implementation (phase 2) |
|---|---|---|---|
| Gemma 4 31B (BF16, MTP draft) | `PROTOCOL.md` arm A, pipeline v1: 5/5 programs, services 17/17 job outputs. Pipeline v2 arm A: `PIPELINE-V2.md` | Yes (baseline) | Yes: implements its own design (S) and gpt-oss's design (X) |
| gpt-oss-120b (MXFP4) | Pipeline v2 arm A: 4/5 programs (SAM1 9/13). 2026-10-06, older harness: designs valid, code failed | Yes | No (owner's decision: design only) |

Gemma is on the coding track by its v1 arm-A result (`PROTOCOL.md` criterion 1). Its v2 arm-A result is reported next to it
and does not gate this round.

## 3. The target: a new Customer Account Service

A Java 21 service, hexagonal, in Gradle modules. The SME fixes the bounded contexts, the module set and the public API
(`cas-api`); the models design everything inside the modules.

| Module | Package | Content | Legacy logic |
|---|---|---|---|
| `cas-api` (given) | `com.mlcpu.cas.api..` | Use-case interface, commands, queries, results, typed rejections, persistence snapshots, outbound ports | - |
| `cas-accounts` | `com.mlcpu.cas.accounts..` | Card accounts: posting (real time and nightly batch), interest accrual | CBTRN02C, CBACT04C |
| `cas-customers` | `com.mlcpu.cas.customers..` | Customer master maintenance, the update policy, the 12-month average | SAM1, SAM2 |
| `cas-reporting` | `com.mlcpu.cas.reporting..` | The transaction report query | CBTRN03C |
| `cas-statements` | `com.mlcpu.cas.statements..` | **New, no legacy equivalent:** the customer statement | - |
| `cas-application` | `com.mlcpu.cas.application..` | Use-case orchestration, idempotency, wiring (`CasApplication.create(CasPorts)`) | - |

**The new feature.** `CustomerStatement(customer, cycle)` gives:
* opening and closing balance per linked card account;
* postings by category;
* interest charged for the cycle;
* rejections;
* orders year to date and the 12-month average.

The two estates have separate customer files, so the SME provides a `customer-link` reference dataset that maps a SAM
customer id to CardDemo accounts. CardDemo and the IBM sample are public, so a model may recall them. A requirement that
exists nowhere tests design rather than memory.

**Legacy defects are not carried forward.** A new service fixes behaviour that no business would want. Each fix is
listed in `SPEC.md` (decisions D-1 to D-n) with its legacy cause. Everything else keeps the legacy rule exactly,
including truncation, reason-code precedence and the implied decimals of SAM's update amounts.

## 4. Phase 0: what the SME builds (CPU only, before any model runs)

Everything is in `forward/cas/`:

| Deliverable | Shown to models |
|---|---|
| `SPEC.md`: purpose, bounded contexts, glossary, command and query semantics, the new feature, decisions D-n, non-goals | Yes |
| `cas-api` source (`service/cas-api`) | Yes |
| `RULES.md`: design rules, each with its enforcing test | Yes |
| Architecture tests (ArchUnit, `service/cas-architecture`) enforcing `RULES.md` | Yes (the rules; failures in full) |
| A rule digest per legacy program (`digests/`, 2,000 to 3,000 tokens, forward rule ids traced to the packet's rule ids) and the full requirements packets | Yes (packets in design step 1b only) |
| Public scenarios: about 20% of the acceptance scenarios, with expected results | Yes (examples in the prompt, failures in full) |
| Hidden scenarios: the remaining 80% | No (a pass count only) |
| Reference implementation of every module (`reference/`) | No |
| The design rubric with written anchors (`rubric.md`) | No (fixed before any design exists) |

**Design rules** (`RULES.md`; each enforced by a test, not by trust):
* hexagonal dependencies: a context module depends only on `cas-api` and the JDK, and the application only on `cas-api` and
  the context modules;
* no I/O, clock or randomness in the context modules (the clock comes through `CasPorts`);
* immutable domain state (final fields; records for values);
* money is `BigDecimal` (no `double` or `float`, no `BigDecimal(double)`), with the scale of the legacy field and truncation
  exactly where the legacy rule has it;
* every rejection is a typed value carrying the legacy reason code;
* commands carry idempotency keys, and replays are no-ops that return the first result;
* no framework annotations in phase 2.

**Acceptance suite, derived from the GnuCOBOL oracle.** The scenarios of the published estates' equivalence tests are run
through the faithful programs (byte-identical to GnuCOBOL), and their inputs and outputs are decoded into commands,
initial state and expected results:
* POSTTRAN: each daily transaction becomes a `PostTransaction`, or the day becomes one `PostBatch`. Expected: outcomes
  with reason codes, accounts, category balances, the ledger and the rejection log.
* INTCALC: category balances and disclosure groups in; interest transactions and account balances out.
* TRANREPT: the report's lines and totals, through the query.
* ZDERUN (both scenarios) and single changes cut from them: the customer master, the change totals and the errors.
* The statement feature: SME-authored scenarios, with expected values from the reference implementation.

Expected values come from the reference implementation. It must agree with the faithful program on every oracle-derived
scenario, except where a listed decision D-n changes the rule. Any other disagreement is a defect in the reference, the
spec or the decoder, and is fixed before any model runs. **Contract level:** domain values must be exact. Legacy channels
(datasets and report bytes through adapters) belong to phase 3.

**Test-suite quality gate** (before any model runs):
* the reference implementation passes 100% of the scenarios and the architecture tests;
* its context modules reach a mutation score of at least 85% (PIT, the full suite as tests);
* every digest rule and every `SPEC.md` statement rule is tagged on at least one scenario;
* public and hidden scenarios are split by stratified random sampling per primary rule, with a fixed seed (20260010), at
  least one public scenario per rule that has three or more.

## 5. Phase 1: design round (Gemma and gpt-oss-120b)

Design has two steps:

* **1a Architecture:** in: `SPEC.md`, `cas-api`, `RULES.md` and the digests; out: module responsibilities, the domain model
  per context (aggregates, invariants, value objects, policies), use-case flows with idempotency, a trace from every digest
  and statement rule to a module, class and method, decisions and risks (schema `mlcpu.service-design.v1`).
* **1b Slice designs:** per implementation slice (accounts, customers, reporting, statements), the model's own architecture
  plus that slice's full requirements packets in (statements: none); out: classes with fields and method signatures,
  the application classes the slice needs, and the rule-to-method trace (schema `mlcpu.slice-design.v1`).

**Repeated seeds.** Each model runs 1a with seeds 42, 7 and 1234. All three architectures are scored. The model's design
score is their mean, and its best-scored architecture goes on to 1b (seed 42). Each step has 3 attempts to pass the
automated checks.

**Scoring.**

* **Automated checks** (pass or fail, reported per design): schema valid; every `CustomerAccountService` method assigned to
  a flow; every rule traced; module dependencies within `RULES.md`.
* **SME rubric, applied blind** (`rubric.md`: criteria and 1-to-5 anchors written before any design exists):

| Criterion | Weight |
|---|---|
| Domain model: aggregates and invariants match the business, not the COBOL file layout | 25% |
| Rule placement and traceability | 20% |
| Money, rounding and truncation handled where the legacy rule has them | 15% |
| Idempotency and the batch versus real-time split | 15% |
| The new statement feature: correct sources, no duplicated logic | 15% |
| Testability and simplicity | 10% |

  **Scorer and blinding.** The SME role is held by Claude, appointed by the owner. Before scoring, a script strips every
  model and seed marker, shuffles the six architectures under random labels and keeps the key in a separate file. A fresh
  scorer that has seen no run receives the rubric, `SPEC.md`, the digests and the anonymized designs. The key is opened only
  after all scores are recorded.
* **Consistency check:** a second fresh scorer re-scores two random designs without seeing the first scores. A difference of
  more than one point on any criterion is reported.
* **Peer review (secondary, reported, not in the decision rules):** Gemma reviews gpt-oss's three anonymized architectures
  and gpt-oss reviews Gemma's, against the same rubric. Models are known to prefer designs like their own, so this only shows
  where the SME and the models disagree.

## 6. Phase 2: implementation (Gemma)

**Arms.**

* **S, solo:** Gemma implements its own design.
* **X, cross:** Gemma implements gpt-oss-120b's design. With S, this separates design quality from coding skill: same
  implementer, same budget, different designer.

**Slices.** Each arm is built in four vertical slices, in this order:
1. accounts (`cas-accounts` and the application's posting and interest flows);
2. customers;
3. reporting;
4. statements.

Each slice is one conversation. Its prompt holds:
* `SPEC.md`, `RULES.md` and `cas-api`;
* the slice's digests;
* the design (1a, and 1b for the slice);
* the code already accepted (all application files, and the context modules the slice may read through `cas-api` types);
* worked examples from the slice's public scenarios.

The model returns complete files or SEARCH/REPLACE edits (pipeline v2). A slice is judged on every scenario of every slice
built so far, so a later slice cannot silently break an earlier one.

**Feedback policy.**

| Information | Given to the implementer |
|---|---|
| Compiler errors | In full, with the source lines and the misused `cas-api` class's API (v2) |
| Architecture-test failures | In full |
| Public scenario failures | In full: the scenario, the command, and the first differing value by path (`accounts[27].currentBalance: expected 1234.56, got 1200.00`) |
| Hidden scenarios | A pass count per slice only, after each attempt |

**Budget:**
* 15 attempts per slice;
* 131,072-token context, seed 42, Gemma's own sampling, the same llama.cpp build as `PROTOCOL.md`;
* missing imports of `cas-api` and `java.*` classes are added by the harness (v2).

A slice ends when it compiles, its architecture tests are green and every scenario passes, public and hidden.

**Plateau rule:** a slice also ends after three consecutive attempts in which all public scenarios pass and the hidden
count does not improve.

The deliverable of a slice is its best attempt: architecture green first, then the most scenarios passed. The next slice
starts from that deliverable.

**Measures per arm:**
* hidden pass rate (all slices, final code);
* public pass rate;
* architecture tests;
* attempts per slice;
* minutes;
* compile failures;
* tokens.

## 7. Phase 3: integration (deferred)

The strangler adapters, data migration with reconciliation, and the shadow run against the faithful programs (revision
2, section 7) are deferred. They become the next round once a forward implementer is chosen.

## 8. Decision rules

| Outcome | Decision |
|---|---|
| **Gemma passes forward engineering:** in arm S or X, every slice compiles, architecture tests green, every public scenario passes, hidden pass rate at least 90% | Add the **Forward Engineering tab** to the workbench (section 9) |
| gpt-oss-120b's mean rubric score beats Gemma's by at least 0.5 (of 5) **and** arm X's hidden pass rate is at least arm S's, with architecture tests green | gpt-oss-120b becomes the **designer** in forward engineering and Gemma implements |
| gpt-oss-120b scores better but X does not reach S, or X reaches S without the score margin | No role change. Report it, with the attempts and minutes of S and X |
| None of the above | Gemma designs and implements |

A close result triggers reruns of the implementation with seeds 7 and 1234 before any conclusion. Close means:
* hidden pass rates of S and X less than 10 points apart; or
* attempts less than 30% apart at equal pass rates.

A 100% hidden pass rate is reported as **exact**.

## 9. Workbench changes that follow from the results

The workbench is changed on the main branch, only as far as the results justify.

**If Gemma passes forward engineering:** add a **Forward Engineering** tab (`/forward`). It shows:
* the target specification (the Customer Account Service);
* the design round: each architecture's automated checks, rubric scores (after unblinding) and peer reviews, and the chosen
  designs;
* arms S and X slice by slice: attempts, public and hidden pass counts, architecture tests, minutes;
* the generated code in the same explorer and Monaco viewer as "View Gemma 4's Code";
* the decision of section 8.

It reads the run folders under `<evalRoot>\forward\` (or `projects/<id>/forward/` once forward runs move into the
workbench). The roles shown are filled from section 8: Gemma for everything unless gpt-oss-120b earned the designer role.

**In either case:** the per-model registry and per-model Modernization tabs of revision 2 wait for a challenger that passes
`PROTOCOL.md`; this round adds none.

## 10. Fairness and validity

* Held constant for both arms:
  * the inputs (spec, API, rules, digests, public scenarios);
  * the harness commit, the runtime build, context, budgets and seed;
  * no fallback.

  Only the design differs between S and X.
* Hidden tests: a model cannot fit the tests instead of the logic.
* The SME's role:
  * the SME writes the spec, the tests and the reference implementation, so the reference never competes;
  * the rubric is fixed in `rubric.md` before any design exists;
  * scoring is blind (section 5).
* Close results are rerun with seeds 7 and 1234 (section 8). Designs are sampled three times per model.
* Precision: Gemma BF16, gpt-oss-120b MXFP4 (its native format).
* gpt-oss-120b designs at reasoning effort high; Gemma's thinking is on, as in `PROTOCOL.md`.

**Threats to validity:**
* The spec's wording and the digests shape every design. Both models get the same ones.
* The hidden suite can have blind spots. The mutation gate mitigates this.
* The statement feature has no oracle. Its tests rest on the SME's reading of the spec, checked by the reference
  implementation.
* The SME's rubric is one scorer's judgement. Blinding, the consistency check and the peer review mitigate this.
* The SME and the scorers are the same assistant family. This is mitigated by:
  * fresh scorers with no run context;
  * anonymization;
  * the implementability measure, which needs no judgement.
* gpt-oss's designs are tested in code only through Gemma. A design Gemma cannot follow counts against the design, which
  is the workbench's situation if gpt-oss designs.

## 11. Tooling (extends `tools/eval` and `MlCpu.DesignModel`)

* `MlCpu.DesignModel` verbs:
  * `fwd-design` (1a with `--seed`, 1b per slice), validated against the schemas and the automated checks;
  * `fwd-review` (peer review);
  * `fwd-implement` (the slices of one design, one arm).
* The judge:
  * a Gradle build of `forward/cas/service` with the candidate's modules;
  * the architecture tests;
  * the acceptance suite: public scenarios with the first differing value, hidden scenarios as a count.
* `tools/eval/eval-config-forward.json` and `Invoke-ForwardEval.ps1`: the same runner, queue, logs and report pattern as
  today, under `<evalRoot>\forward\`.
* `forward/cas/tools/Test-ForwardGate.ps1`: the Phase 0 quality gate (reference build, scenarios, mutation score,
  traceability).

## 12. Order

1. Pipeline v2: Gemma arm A (running 2026-10-10), compared with Gemma v1.
2. Phase 0 and its quality gate (CPU).
3. Phase 1:
   * gpt-oss 1a with three seeds;
   * Gemma 1a with three seeds;
   * peer reviews;
   * blind scoring;
   * 1b for each model's best architecture.
4. Phase 2: Gemma in arms S and X.
5. Report; the Forward Engineering tab if Gemma passes.

## 13. Open decisions

1. Whether an external model (Gemini 3.8 Flash, already used through the file-exchange backend) joins the design round as a
   ceiling reference outside the decision rules. Not in this round.
2. Phase 3 after a forward implementer is chosen.
