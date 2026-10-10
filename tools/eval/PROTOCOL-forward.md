# Forward-engineering evaluation protocol

**Status:** proposed, not started. Phase 0 is deferred until the re-engineering evaluation (`PROTOCOL.md`) has decided which
models qualify. Nothing here runs before that.

**Question.** Can the models that reproduce legacy behaviour also *design and build into a new architecture*, alone or
working with Gemma 4, well enough to be used for forward engineering in the workbench?

## 1. Why a forward-engineering test

The re-engineering test asks a model to restate one COBOL program as idiomatic Java on a given platform, judged byte for byte
against the faithful translation. That shows a model can preserve behaviour. It does not show it can:

* design a domain model and module boundaries no one has drawn yet;
* place several legacy programs' rules (from two unrelated estates) into one coherent service;
* satisfy architectural rules, not only output parity;
* build a feature that has no legacy equivalent;
* generalize to tests it has not seen (the re-engineering acceptance test is shown to the model).

Those are the skills the workbench needs once an estate is modernized and new work starts.

## 2. Entry gate and participants

A model takes part only if it met criterion 1 of `PROTOCOL.md` section 5 (arm A: every program passes its acceptance test,
and the assembled services match the GnuCOBOL oracle on 17/17 job outputs).

| Model | Re-engineering evidence | Forward test |
|---|---|---|
| Gemma 4 31B | 2026-10-10 arm A: 5/5 programs (31 attempts, 73 min); services 116/116 tests + 11/11 outputs, 61/61 + 6/6 | Baseline |
| Laguna S 2.1 | Arm A pending | Admitted if it passes |
| Inkling-Small | Arm A pending | Admitted if it passes; its runtime still needs unmerged llama.cpp PR #25731 |
| gpt-oss-120b | 2026-10-06: CBACT04C 1/17, CBTRN02C 0/23 (0/23 with examples), CBTRN03C 3/23 | Excluded |
| Llama 4 Maverick | 2026-10-06: CBACT04C 1/17; CBTRN02C and CBTRN03C did not compile in 10 attempts | Excluded |

The 2026-10-06 runs used an older harness, but Gemma ran on the same harness that day and reached 17/23 on CBTRN02C with
examples, so the gap is not explained by the harness. Either excluded model is re-admitted if it later passes
`PROTOCOL.md` arm A.

If no challenger qualifies, the forward test still runs with Gemma alone: it then answers whether Gemma can be trusted with
forward engineering, without the comparative and team arms.

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

## 5. Phase 1: design (every participant)

The full packets do not fit one prompt (each CardDemo packet is about 40 thousand tokens), so design has two steps:

* **1a Architecture:** spec, `cas-api`, rules and the rule digests in; out: module responsibilities, the domain model per
  bounded context, a trace from every digest rule to a module and method, decisions and risks
  (schema `mlcpu.service-design.v1`).
* **1b Module designs:** per bounded context, its own architecture plus that context's full packets in; out: classes,
  methods and the rule-to-method trace.

**Scoring.** Automated: schema valid; every `cas-api` method assigned; every digest rule traced; no dependency the rules
forbid. SME rubric, applied blind (designs anonymized and shuffled; rubric fixed now, scored 1 to 5 against written anchors):

| Criterion | Weight |
|---|---|
| Domain model: aggregates and invariants match the business, not the COBOL file layout | 25% |
| Rule placement and traceability | 20% |
| Money, rounding and truncation handled where the legacy rule has them | 15% |
| Idempotency and the batch versus real-time split | 15% |
| The new statement feature: correct sources, no duplicated logic | 15% |
| Testability and simplicity | 10% |

Consistency check: the SME re-scores two random designs at the end without seeing the first scores; a difference of more than
one point on any criterion is reported.

## 6. Phase 2: implementation (participants and teams)

**Arms.**

* **S, solo:** each participant implements its own design.
* **X, cross:** each participant implements the best-rated design (highest rubric score). This separates coding skill from
  design skill. Where the best design is the participant's own, X equals S and is not rerun.
* **T, team (Gemma with a challenger):** roles taken from the evidence: the best designer's design, implemented by the
  best coder (highest hidden pass rate in S and X), with the other model as reviewer. After each attempt the reviewer gets
  the diff and the public test results and returns a structured review (rule violations, risks, missing cases), which goes
  back to the implementer with the test feedback. T is compared with X for the same design and implementer, so the value
  of the review is measured, not assumed. With one challenger there are two team configurations (each model as implementer).

With one qualifying challenger this is at most 2 S + 1 X + 2 T = 5 implementation runs; with two, at most 9.

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
| A challenger's S or X hidden pass rate is at least Gemma's, with architecture tests green and the 3x wall-clock limit met | Offer it as the forward-engineering implementer next to Gemma |
| T beats the best solo implementer by at least 10 points of hidden pass rate, or by 30% fewer attempts at an equal pass rate | Adopt the team configuration (design, implement, review roles) for forward work |
| A challenger's design is the best-rated and X with it beats S | Use it as designer, Gemma (or the best coder) as implementer |
| Otherwise | Gemma solo remains the forward-engineering model |

Any integration also needs the phase 3 pass; Inkling additionally needs its llama.cpp support merged upstream, or the team
must accept a pinned fork.

## 9. Fairness and validity

* Everything `PROTOCOL.md` holds constant: inputs, harness commit, runtime build, context, budgets, seed, no fallback.
* Hidden tests: a model cannot fit the tests instead of the logic.
* The SME writes the spec, the tests and the reference implementation, so the reference never competes; the rubric is fixed
  here, before any design exists; review is blind.
* Close results (a difference of less than 10 points of hidden pass rate, or less than 30% in attempts) are rerun with
  seeds 7 and 1234 before a conclusion.
* Precision asymmetry (Gemma BF16, challengers quantized) carries over from `PROTOCOL.md` and favours Gemma.

**Threats to validity:** the spec's wording and the rule digests shape every design (all models get the same ones); the
hidden suite can have blind spots (mitigated by the mutation gate); the statement feature has no oracle (its tests rest on
the SME's reading of the spec, checked by the reference implementation); a single seed per cell otherwise.

## 10. Tooling (extends `tools/eval`)

* `MlCpu.DesignModel`: verbs `fwd-design` (1a, 1b), `fwd-implement` (per module, own or given design), and a reviewer
  role for arm T; prompt builders for the spec, API and digests; the feedback policy above.
* A judge: Gradle build of the service with the candidate modules, architecture tests, public scenarios with full detail,
  hidden scenarios with a count only.
* The same runner, queue and report pattern as today, under `<evalRoot>\forward\`.

## 11. Effort and order

* Phase 0 (SME, CPU only): about a day.
* Phases 1 to 3 (GPU): about a day and a half for Gemma and one challenger, longer with two.
* Order: finish `PROTOCOL.md` (arms A and B); decide the participants by the entry gate; then Phase 0; then phases 1 to 3.

## 12. Open decisions

1. The `CustomerStatement` feature (a requirement with no legacy equivalent). Recommended: keep it.
2. Whether an external model (Gemini 3.8 Flash, already used through the file-exchange backend) joins as a ceiling
   reference outside the decision rules.
