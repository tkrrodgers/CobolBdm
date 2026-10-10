# Forward-engineering evaluation protocol

**Status:** proposed, not started. Phase 0 is deferred until the re-engineering evaluation (`PROTOCOL.md`) has finished.
Nothing here runs before that.

**Questions.**

1. Can the models that reproduce legacy behaviour also *build into a new architecture*, alone or working with Gemma 4?
2. Independently of coding: is any model a better *designer* than Gemma 4? A model that cannot code well enough may still
   design well, and a design can be implemented by another model.

**Revision 2 (2026-10-10, owner's direction):** two tracks instead of one gate (section 2); gpt-oss-120b back for the design
track; Llama 4 Maverick out; a design-only round with repeated seeds (section 5); the workbench changes that follow from the
results (section 9).

## 1. Why a forward-engineering test

The re-engineering test asks a model to restate one COBOL program as idiomatic Java on a given platform, judged byte for byte
against the faithful translation. That shows a model can preserve behaviour. It does not show it can:

* design a domain model and module boundaries no one has drawn yet;
* place several legacy programs' rules (from two unrelated estates) into one coherent service;
* satisfy architectural rules, not only output parity;
* build a feature that has no legacy equivalent;
* generalize to tests it has not seen (the re-engineering acceptance test is shown to the model).

Those are the skills the workbench needs once an estate is modernized and new work starts.

## 2. Participants: a design track and a coding track

Design and coding are separate skills, so they have separate entry rules.

* **Design track (phase 1):** every candidate the owner admits, whatever its coding record.
* **Coding track (phases 2 and 3):** only models that met criterion 1 of `PROTOCOL.md` section 5 (arm A: every program passes
  its acceptance test, and the assembled services match the GnuCOBOL oracle on 17/17 job outputs).

| Model | Re-engineering evidence | Design track | Coding track |
|---|---|---|---|
| Gemma 4 31B | 2026-10-10 arm A: 5/5 programs (31 attempts, 73 min); services 116/116 tests + 11/11 outputs, 61/61 + 6/6 | Yes (baseline) | Yes (baseline) |
| Laguna S 2.1 | `PROTOCOL.md` arm A in progress | Yes | If it passes arm A |
| Inkling-Small | `PROTOCOL.md` arm A pending | Yes | If it passes arm A; its runtime still needs unmerged llama.cpp PR #25731 |
| gpt-oss-120b | 2026-10-06: designs valid for all 3 programs; code CBACT04C 1/17, CBTRN02C 0/23 (0/23 with examples), CBTRN03C 3/23 | Yes (second chance, owner's decision) | No, unless it passes `PROTOCOL.md` arm A |
| Llama 4 Maverick | 2026-10-06: designs schema-valid, but CBTRN02C and CBTRN03C never compiled in 10 attempts and CBACT04C reached 1/17 | No (owner's decision) | No |

The 2026-10-06 runs used an older harness, but Gemma ran on the same harness that day and reached 17/23 on CBTRN02C with
examples, so the gap is not explained by the harness.

A design-track model contributes to the coding track through arm X (section 6): if its design is rated best, the best
coding-track model implements it. That is the only way a design-only model's work is tested in code, and it is enough to
decide whether it should design in the workbench.

If no challenger reaches the coding track, phases 2 and 3 run with Gemma alone (arms S and X only): they then answer
whether Gemma can be trusted with forward engineering, and whether a design-track winner improves on Gemma's own design.

## 3. The target: a new Customer Account Service

A Java 21 / Spring Boot 3 service, hexagonal, in Gradle modules: `cas-api` (commands, queries, results, rejection types),
one domain module per bounded context, `cas-application` (use-case orchestration), and adapters (persistence, REST, batch,
legacy). It reuses the existing platform layer (`com.mlcpu.platform`) for record codecs only in adapters.

| Legacy logic | In the new service |
|---|---|
| SAM1: ADD / UPDATE / DELETE customer transactions against a sequential master, sequence check, processing report | `ApplyCustomerChanges` command (batch of changes), `Customer` aggregate, `CustomerChangeSummary` |
| SAM2: field updates (balance, orders year to date, name; REPLACE or ADD) and the 12-month average | `CustomerUpdatePolicy` domain service, `MonthlyAverage` value |
| CBTRN02C: daily posting, validation and rejects (card cross-reference, account, credit limit, expiry), account and category balances | `PostTransaction` (single, real time) and `PostBatch` (nightly) commands; `Account`, `CategoryBalance`; typed `Rejection` with the legacy reason codes |
| CBACT04C: interest per category balance with disclosure-group rates (default group fallback), cycle reset | `AccrueInterest(cycle)`, idempotent per account and cycle |
| CBTRN03C: transaction detail report for a date range, with page, account and grand totals | `TransactionReport` query + a legacy-format report adapter |
| **New, no legacy equivalent** | `CustomerStatement(customer, cycle)`: opening and closing balance, postings by category, interest, rejections, orders year to date and the 12-month average |

The two estates have separate customer files. The service links them through a reference dataset the SME provides
(`customer-link`: SAM customer id to CardDemo customer and account ids), so the statement feature is well defined.

The new feature matters: CardDemo and the IBM sample are public, so a model may recall them. A requirement that exists
nowhere tests design rather than memory.

## 4. Phase 0: what the SME builds (CPU only)

| Deliverable | Shown to models |
|---|---|
| `SPEC.md`: purpose, bounded contexts, glossary, the command and query semantics, the new feature, non-functional rules | Yes |
| `cas-api` source: records, interfaces, ports (repositories, clock, id generator, event publisher), rejection types | Yes |
| `RULES.md`: design rules (below), each with its enforcing test | Yes |
| Architecture tests (ArchUnit) enforcing `RULES.md` | Yes |
| Rule digest per legacy program (2 to 3 thousand tokens, from the requirements packets) and the full packets | Yes |
| Public scenarios: about 20% of the acceptance scenarios, with expected results | Yes |
| Hidden scenarios: the remaining 80% | No |
| Reference implementation of every module | No |

**Design rules** (each enforced by a test, not by trust): hexagonal dependencies (domain depends on nothing but `cas-api`
and the JDK); no I/O, clock or randomness in the domain (ports only); immutable records for values and events; money is
`BigDecimal` with the scale of the legacy field, and truncation or rounding exactly where the legacy rule has it; every
rejection is a typed value carrying the legacy reason code; commands carry idempotency keys and replays are no-ops;
Spring annotations only in adapters and configuration.

**Acceptance suite, derived from the GnuCOBOL oracle.** The oracle's job snapshots become command streams and expected
state:

* POSTTRAN: each daily transaction record becomes a `PostTransaction`; expected are the posted transactions, the
  rejections with their codes, the account and category balances after the run, and the batch counts.
* INTCALC: category balances and disclosure groups in, interest postings and account balances out.
* TRANREPT: the report's values through the query.
* ZDERUN (both scenarios): customer master and changes in, customer state and summary counts out.
* The statement feature: hand-written scenarios, checked against the reference implementation.

Two contract levels: **domain values** must be exact (phase 2); **legacy channels** (datasets and reports byte for byte
through adapters) are added in phase 3.

**Test-suite quality gate** (before any model runs): the reference implementation passes 100%; mutation score on its
domain modules of at least 85% (PIT); every rule in every digest is traced to at least one scenario; public and hidden
scenarios are split by stratified random sampling per rule, with a fixed seed.

## 5. Phase 1: design round (design track: Gemma, Laguna, Inkling-Small, gpt-oss-120b)

The full packets do not fit one prompt (each CardDemo packet is about 40 thousand tokens), so design has two steps:

* **1a Architecture:** spec, `cas-api`, rules and the rule digests in; out: module responsibilities, the domain model per
  bounded context, a trace from every digest rule to a module and method, decisions and risks
  (schema `mlcpu.service-design.v1`).
* **1b Module designs:** per bounded context, the model's own architecture plus that context's full packets in; out:
  classes, methods and the rule-to-method trace.

**Repeated seeds.** A design is one sample from the model, so each model runs 1a with seeds 42, 7 and 1234. All three are
scored; the model's design score is the mean, and its best-scored architecture goes on to 1b (seed 42). Design runs are
short (minutes per model), so three seeds cost little and keep a single lucky or unlucky sample from deciding the round.

**Scoring.**

* **Automated checks** (pass or fail, reported per design): schema valid; every `cas-api` method assigned; every digest
  rule traced; no dependency that `RULES.md` forbids.
* **SME rubric, applied blind** (designs anonymized and shuffled; rubric fixed now, scored 1 to 5 against written anchors):

| Criterion | Weight |
|---|---|
| Domain model: aggregates and invariants match the business, not the COBOL file layout | 25% |
| Rule placement and traceability | 20% |
| Money, rounding and truncation handled where the legacy rule has them | 15% |
| Idempotency and the batch versus real-time split | 15% |
| The new statement feature: correct sources, no duplicated logic | 15% |
| Testability and simplicity | 10% |

* **Peer review (secondary, reported, not in the decision rules):** each coding-track model reviews the other models'
  anonymized architectures against the same rubric. A model never reviews its own work. Models are known to prefer
  designs like their own, so this only flags where the SME's view and the models' views disagree.
* **Consistency check:** the SME re-scores two random designs at the end without seeing the first scores; a difference of
  more than one point on any criterion is reported.

**Design implementability (the link to code).** The two best-rated designs are implemented by the best coding-track model
in arm X (section 6). A design that scores well but cannot be implemented to the hidden scenarios does not win.

## 6. Phase 2: implementation (coding track)

**Arms.**

* **S, solo:** each coding-track model implements its own design.
* **X, cross:** each coding-track model implements the best-rated design and the second-best design, whoever made them,
  design-track models included. This separates coding skill from design skill and tests the design-only models' work in
  code. Where a design is the implementer's own, X equals S and is not rerun.
* **T, team (Gemma with a challenger):** roles taken from the evidence: the best design, implemented by the best coder
  (highest hidden pass rate in S and X), with the other coding-track model as reviewer. After each attempt the reviewer
  gets the diff and the public test results and returns a structured review (rule violations, risks, missing cases),
  which goes back to the implementer with the test feedback. T is compared with X for the same design and implementer, so
  the value of the review is measured, not assumed. Runs only when at least one challenger is on the coding track.

**Feedback policy.**

| Information | Given to the implementer |
|---|---|
| Compiler errors | In full |
| Architecture-test failures | In full |
| Public scenario failures | In full, with the first differing value |
| Hidden scenarios | A pass count only, after each attempt |
| The reviewer's review (arm T) | In full |

Budget: the same as `PROTOCOL.md` arm A (15 implementation attempts per module, 131,072-token context, seed 42, each
model's own sampling, the same llama.cpp build).

## 7. Phase 3: integration (the two best implementers or the best team)

The real modernization work: putting the existing estates behind the new service.

1. **Legacy adapters (strangler):** each command is mapped to the legacy record layouts and run through the faithful
   programs (`ProgramRegistry`, `JobEngine`), so the service can run legacy-backed.
2. **Data migration:** the legacy datasets (VSAM KSDS, sequential, variable-length) are loaded into the service's
   repositories, with a reconciliation report (counts, sums, and per-record keys).
3. **Shadow run:** every command of every oracle scenario goes down both paths (legacy-backed and modern), and results and
   legacy channels (datasets, report bytes) are compared.

Pass: green integration tests, exact reconciliation, zero shadow differences on every scenario.

## 8. Decision rules

| Outcome | Workbench decision |
|---|---|
| A design-track model's mean rubric score beats Gemma's by at least 0.5 (of 5) **and** its design, implemented in arm X, reaches at least the hidden pass rate of Gemma's own design | It becomes the **designer** in forward engineering; the best coder implements |
| A challenger's S or X hidden pass rate is at least Gemma's, with architecture tests green and the 3x wall-clock limit met | Offer it as a forward-engineering **implementer** next to Gemma |
| T beats the best solo implementer by at least 10 points of hidden pass rate, or by 30% fewer attempts at an equal pass rate | Adopt the **team** configuration (designer, implementer, reviewer roles) |
| None of the above | Gemma alone designs and implements |

Any integration also needs the phase 3 pass. Inkling additionally needs its llama.cpp support merged upstream, or the
owner must accept a pinned fork.

## 9. Workbench changes that follow from the results

The workbench is changed only after both evaluations, on the main branch, and only as far as the results justify.

**If one or both challengers pass `PROTOCOL.md` (re-engineering):**

* **Model registry.** The Gemma-specific settings (`GemmaModel`, `GemmaName`, `LlamaDir`) become a list of models, each
  with its weights, its llama.cpp build (Inkling needs its own), sampling and server arguments, as in
  `tools/eval/eval-config.json`. The Settings page edits the list.
* **A Modernization tab per qualified model.** The Gemma 4 Modernization page becomes one page parameterized by model, with a
  navigation entry per qualified model (for example "Laguna S 2.1 Modernization"). `GemmaService` becomes a model service
  taking the model as a parameter.
* **Separate output per model.** Each model writes to `projects/<id>/models/<model>/` instead of the shared `gemma/` folder,
  so one model's run no longer overwrites another's. Existing `gemma/` folders stay readable as Gemma's.
* **A code viewer tab per model.** "View Gemma 4's Code" becomes one page parameterized by model ("View Laguna S 2.1's
  Code", and so on), each with its own explorer tree and tabs.
* **One GPU job at a time**, across all models (the existing `gpu` operation group).

**If neither challenger passes:** Gemma stays the only modernization model; the current Gemma tabs stay as they are.

**In both cases, a Forward Engineering tab** for the active project, once phase 2 has shown that forward work is reliable:
pick a target service specification (the Customer Account Service first), then design, implement, test (public scenarios in
full, hidden as a count) and view the code. The roles (designer, implementer, reviewer) are filled from section 8: Gemma
for everything unless another model earned a role. A design-track winner that is not a coder (gpt-oss-120b, for example)
can hold the designer role.

## 10. Fairness and validity

* Everything `PROTOCOL.md` holds constant: inputs, harness commit, runtime build, context, budgets, seed, no fallback.
* Hidden tests: a model cannot fit the tests instead of the logic.
* The SME writes the spec, the tests and the reference implementation, so the reference never competes; the rubric is fixed
  here, before any design exists; review is blind.
* Designs are sampled three times per model (section 5); close implementation results (a difference of less than 10 points
  of hidden pass rate, or less than 30% in attempts) are rerun with seeds 7 and 1234 before a conclusion.
* Precision: Gemma BF16, Laguna Q8_0, Inkling-Small 5-bit, gpt-oss-120b MXFP4 (its native format). Only the quantized
  challengers are disadvantaged, which favours the baseline.

**Threats to validity:** the spec's wording and the rule digests shape every design (all models get the same ones); the
hidden suite can have blind spots (mitigated by the mutation gate); the statement feature has no oracle (its tests rest on
the SME's reading of the spec, checked by the reference implementation); the SME's rubric is one reviewer's judgement
(mitigated by blinding, the consistency check and the peer review); design-only models are tested in code only through
another model's implementation.

## 11. Tooling (extends `tools/eval`)

* `MlCpu.DesignModel`: verbs `fwd-design` (1a, 1b), `fwd-implement` (per module, own or given design), and reviewer roles
  for peer review (phase 1) and arm T; prompt builders for the spec, API and digests; the feedback policy above.
* A judge: Gradle build of the service with the candidate modules, architecture tests, public scenarios with full detail,
  hidden scenarios with a count only.
* `eval-config.json` gains gpt-oss-120b (MXFP4, `C:\models\gpt-oss-120b-GGUF`, OpenAI's sampling, reasoning effort high)
  for the design track.
* The same runner, queue and report pattern as today, under `<evalRoot>\forward\`.

## 12. Effort and order

1. Finish `PROTOCOL.md` (arms A and B) and decide the coding track.
2. Phase 0 (SME, CPU only): about a day.
3. Phase 1, the design round (GPU): about half a day for four models at three seeds, with Inkling-Small the slowest.
4. Phases 2 and 3 (GPU): about a day for Gemma alone, a day and a half with one challenger, longer with two.
5. Workbench changes (section 9): about a day with a qualified challenger (registry, per-model tabs, per-model output);
   about half a day for the Forward Engineering tab alone.

## 13. Open decisions

1. The `CustomerStatement` feature (a requirement with no legacy equivalent). Recommended: keep it.
2. Whether an external model (Gemini 3.8 Flash, already used through the file-exchange backend) joins the design round as a
   ceiling reference outside the decision rules.
3. Whether gpt-oss-120b should also get a second `PROTOCOL.md` arm A run (about 1.5 hours of GPU) to retry the coding track,
   or stay design-only.
