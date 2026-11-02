# Deep Thinker: LLM judgement with deterministic application, ledgers and a growing notebook

Decision (Tomrodgers, 2026-11-02): add a "deep thinker" step to the pipeline. Use it for naming first, then for every task
that is judgement over evidence (what AI is good at). The pipeline never trusts the thinker: it proposes, and a
deterministic engine validates, applies, records and proves.

## 1. The pattern (one shape for every task)

```text
COBOL estate + IR --(deterministic)--> GROUNDING PACKET --> DEEP THINKER (LLM) --> PROPOSALS (schema, citations, confidence)
                                              ^                                         |
                                              |                                         v
                          NOTEBOOK (knowledge/, curated, versioned) <-- notes     DETERMINISTIC VALIDATOR
                                                                                        |  (fail closed)
                                                                                        v
                                         LEDGER (approved decisions, input hash) --> GENERATOR replays the ledger
                                                                                        |
                                                                    compile + strict parity + JOBOL + anchors (behaviour proof)
```

Rules that hold for every task:
1. **The thinker never edits code.** It writes proposals only. The generator applies an approved ledger, like
   `--model-edits`, opt-in, and byte-identical to today when no ledger is given.
2. **Every proposal cites evidence:** file and line of comments, paragraph and data names, copybooks, JCL DDs, sample
   records, and notebook entries. A proposal without a citation is rejected.
3. **Reproducible.** A ledger entry records the input hash of its packet slice, the model id and version, the prompt
   version and the date. A rerun replays the ledger and makes no new model call unless the input hash changed. The
   output is deterministic given the ledger.
4. **Reversible and traceable.** Every applied decision maps legacy to modern both ways (AB00400 <-> GeneralLedger) in the
   ledger and in a Javadoc `Legacy:` line.
5. **Behaviour is never at stake.** Naming and structuring are applied by AST and structured model operations, never by
   text replacement. Compile, strict parity, the JOBOL gate and the anchors prove each application.
6. **Blind evaluation.** Score the thinker against estates that have original Java (kontor-zins-legacy, Fineract) with
   the originals NEVER in its packet. Measure first, trust later. CardDemo has no answer key, so it uses confidence plus
   a review queue.
7. **Low confidence goes to review, not to code:** a review queue (markdown plus JSON) for Tomrodgers or an SME. Approved
   items enter the ledger.

## 2. Task 1 (implement now): NAMING

### 2.1 Grounding packet (deterministic export: `MlCpu.Modernize naming packet <project> <out>`)
Per program:
- the header and comment blocks, verbatim with line numbers;
- PROGRAM-ID, paragraph and section names in order;
- data items, with level, PICTURE, 88-levels and REDEFINES;
- copybooks used and their records;
- callers and callees (the CALL graph);
- files: SELECT/ASSIGN, FD records, JCL DD and DSN;
- record samples from the scenarios (a few lines, masked if the owner marks them sensitive);
- the generated Java skeleton (class, methods, fields with their current names and opaque ids);
- the relevant notebook entries (glossary codeTerms that match).
Exclusions: never original Java, and never J3S provenance comments (CLASS/FRAME/PEC). The exporter strips them, and a
poison test proves it.

### 2.2 Proposal schema `mlcpu.naming-proposals.v1`
```json
{ "schema": "mlcpu.naming-proposals.v1", "estate": "...", "packetSha256": "...", "model": "...", "promptVersion": "naming-1",
  "proposals": [
    { "id": "opaque-symbol-id", "kind": "class|method|field|record|component|enum|constant|package|parameter|local",
      "legacy": "AB00400 | 2000-POST-INTEREST | WS-ACCT-BAL", "current": "Ab00400",
      "proposed": "GeneralLedger", "confidence": 0.0,
      "rationale": "one or two sentences",
      "evidence": [ { "file": "AB00400.cbl", "line": 4, "quote": "<= 15 words" } ],
      "notebook": ["glossary:ACCT-BAL"] } ],
  "notes": [ { "kind": "glossary|pattern|caution", "text": "...", "evidence": [...] } ] }
```

### 2.3 Deterministic validator (`naming validate`), failing closed
- Java-legal identifiers, convention by kind (UpperCamel types, lowerCamel members, UPPER_SNAKE constants), not reserved
  words.
- No collisions in any scope. Overrides, interface implementations and JDK contracts keep their required names (the R1
  obligation facts).
- Consistency: the same COBOL item and the same copybook field get the same name everywhere. Copybook-wide decisions
  apply estate-wide.
- Evidence present and resolvable: each cited file and line exists and contains the quote.
- Confidence >= threshold (proposed 0.7) goes to the ledger; anything lower goes to the review queue.
- No leakage: the proposal cannot contain identifiers that appear only in held-out originals. This is checked only in
  the evaluation harness, never at generation time.

### 2.4 Ledger `modernize/naming-ledger.json` plus apply (`lift ... --names <ledger>`)
- An entry is the approved proposal plus the packet slice hash, the model and prompt version, the approval source
  (auto >= threshold, or review), and the date.
- The generator renames symbols by opaque id in the model before printing, as ModelEdits does. Javadoc gets
  `Legacy: <program/paragraph/item>`. `names.json` gains `source: "deep-thinker"` with the ledger entry id.
- Without `--names`, the output is byte-identical to today. With it, all gates run.

### 2.5 Evaluation (blind): `jobol-audit compare` with STRUCTURAL alignment
The lead adds structural type alignment to the compare tool: match types by members and signatures, not by name, then
report name fidelity separately. That makes name precision measurable on provenance-free estates.
Acceptance for naming v1:
- on kontor-zins-legacy and fineract-legacy, name fidelity rises with structural fidelity unchanged;
- precision of applied names >= 80% on the evaluation estates;
- 0 behaviour changes (strict parity), JOBOL not worse, anchors byte-identical without `--names`.

### 2.6 First measurement (lead, 2026-11-02): `compare --align structural` on kontor-zins-legacy loop5-iter2
- Built: types are paired by shape. A shape is the multiset of field and method type categories (integral, decimal,
  text, date, coll, D, ...), never names. Pairing is greedy and one-to-one, with the threshold 0.5 pre-registered.
  Platform and strangler plumbing are never partners. Type names are then scored separately: exact %, name classes,
  and the mean camel-token Dice. Tests: 24/24. Name mode is unchanged: same Markdown, and the JSON only gains the new
  fields.
- Result: **0 of 5 original types have a structural partner.** The nearest are LoanTerms 49.6% and Installment 47.6%,
  both against DecimalKernel.Operand, which is spurious. Schedule is at 30.1%, Rounding 24.4%, Annuity 16.7%.
- Meaning: the original's domain types (LoanTerms, Installment, Schedule) do not exist yet in the modern estate.
  Renaming cannot raise fidelity on this estate by itself. Naming v1 still gives readability, traceability and the
  ledger. The fidelity gain needs **task 2 (domain decomposition)** next, and the packet is built to serve both tasks.

### 2.7 First thinker run (DT1, kontor-zins-legacy, 2026-11-02)
- Packet eaeb07f7 (pipeline part 1): 2,924 symbols, 827 proposable. The lead verified there are no original names and
  no J3S provenance.
- 390 naming units: 548 of the 604 proposable methods are accessors and setters of 277 data items, so a data item is
  named once and its members follow by suffix.
- Thinker: claude-opus-5-5, prompt naming-1u. Isolated in a folder holding only copies of the COBOL, JCL, masked
  samples and the notebook; about 30 minutes.
- Answers: 339 units named, 51 kept, 99 at confidence 0.5 or lower. 600 citations, all verbatim. 6 notes proposed.
- Lead pre-check (`tools/deep-thinker/dt_expand.py`):
  - 387 of 390 units pass. U363 and U376 are merged derived-role units, sent to review. U007 named a type
    `RoundingMode`, which shadows `java.math.RoundingMode`, so it is rejected.
  - 223 symbol renames are ledger candidates (0.7 or higher); 505 go to review. 0 collisions.
- Blind score against the held-out original (`dt_eval.py`, lead only):
  - concept recall is **88% (29/33) for the thinker against 55% (18/33) for the generator**. The thinker recovered
    Annuity, Schedule, isInterestFree, isInterestOnly, remainingDebtAfter, totalInterest, totalPaid and totalPrincipal,
    among others.
  - Missed: LoanTerms (the type does not exist yet; see 2.6), durationInMonths, installments and principalPart.
  - Vocabulary hit: 29% against 21%. It is diluted, because most of the estate (the decimal library, kernels and I/O
    records) has no counterpart in the 5-class original.
  - Caveats: concept recall was defined after the run, so it is exploratory. Precision against the original cannot be
    measured here without structural pairs (2.6).
- The thinker's own findings, all sent to review:
  - FSEM0100 receives the run control record but hardcodes precision 20 and HALF_UP;
  - 2110-DATE-CHECK is never performed;
  - KH-FREE ("free rate" or "interest-free") cannot be decided from the evidence;
  - the generated value types are shared across different values (PaymentValueRecord holds five).
- Validator rule added from this run: a type name equal to a JDK simple name the estate uses is rejected.

### 2.8 DT1 accepted (2026-11-02 09:00): the end-to-end naming loop works
- Validator (pipeline) and lead pre-check agree on the v2 handoff: 216 approved (204 renames + 12 explicit no-ops in
  complete canonical units), 544 queued (538 entries + 6 notes), 0 rejected. The v1 handoff failed atomicity because it
  dropped no-op members; the validator was right.
- `lift --names` binds by javac Element identity. 204 decisions, 1,110 name nodes, residue 0. The re-attributed
  reference graph is identical, and the non-identifier tokens are invariant. Name-as-data checks: enum
  name/valueOf/toString, record toString, reflection.
- Gates:
  - compile;
  - kernel gates: decimal 686,922 cases, 0 differences, 6/6 mutants; rounding 2,082,656 cases, 0 differences, 3/3;
  - GN4 strict 20/20, with result SHA-256s equal to native;
  - abend source-map remap fixture (RC 16 on both sides);
  - two applies byte-identical (57 files); off-path 641 files byte-identical; toggling naming off restores 54/54;
  - JOBOL: a delta of 0 under code-LOC 1.2.0. The raw 1.1.0 +0.3 is a Javadoc weight artifact (decision log section 10).
- Traceability: names.json carries 216 deep-thinker entries (ledger id, origin, model, prompt), and every renamed
  declaration carries a Legacy Javadoc line. FSEM0100 <-> AnnuityCalculationService resolves from the files alone.
- Samples: Kzle0100Batch -> AnnuityLoanPlanBatch; Fsem0100 -> AnnuityCalculationService; EventRecord -> CaseRecord;
  EventOutRecord -> ResultRecord; monthlyPayment() -> computeMonthlyPayment(); checkTerms() -> validateTerms().
- Open:
  - DT1.1: record parameters take derived names (today the same record is `loanCase` in one method and `caseWork` in
    another, because their units fell on different sides of the threshold); the Javadoc star; one Legacy line per type;
  - the human review sheet (loops/dt1-kontor/REVIEW-SHEET.md: 253 held units, 26 type names first);
  - generalization on fineract-legacy;
  - task 2 (domain decomposition) for fidelity.

## 3. The notebook (memory that improves over time)
`knowledge/notebook/` (versioned in the repo, reviewed like code), built on the existing `knowledge/glossary` and
`taxonomy.json`:
- **glossary:** COBOL fragment -> business term (ACCT -> account, BAL -> balance, INT -> interest, DSN conventions), with
  evidence and the estates where it was confirmed.
- **patterns:** recurring idioms ("a 9000-ABEND paragraph with CEE3ABD is the abend routine"; "a paragraph pair
  OPEN/READ-NEXT is a cursor"), with the estates that confirmed them.
- **cautions:** where the thinker was wrong before, and why. These are read on later runs.
- Every note carries evidence, a confidence, `reviewed: true|false`, and its origin (estate, run, ledger id).
  Unreviewed notes are used as hints with lower weight. Reviewed notes are trusted.
- After each run, the thinker proposes notebook additions. The validator checks the citations, and the review queue
  promotes them.
- The notebook goes into every packet (matched by codeTerms). This is the "learn over time" loop: each estate makes the
  next one faster and better.

## 4. Other steps the same pattern accelerates (ranked by value; each has a deterministic verifier)
| # | Task | What the thinker proposes | What proves it |
|---|---|---|---|
| 1 | Naming (this spec) | names with rationale | AST rename + parity, plus the blind fidelity metric |
| 2 | Domain decomposition | which records and paragraphs form a domain class (LoanTerms, Schedule = List<Installment>) | the generator applies the grouping; compile, parity, JOBOL; structural fidelity against originals |
| 3 | Kernel/library recognition | "this program is a date, decimal or rounding library equivalent to JDK X; its op codes mean ..." | the differential gate against the lifted reference, with mutants (the loop-1/5 method) |
| 4 | Scenario and test generation for coverage gaps | inputs that reach unexercised paragraphs (the loop 2 residuals) | the native coverage trace, plus GN4 parity on the new inputs |
| 5 | Range and precision invariants | bounds ("principal <= 10^11", "rate scale 12") enabling native or BigDecimal choices | the PICTURE and data-flow proof, or fail closed; parity |
| 6 | Business documentation | Javadoc and a business-rule catalog cited to paragraphs | citation check; docs never change code |
| 7 | Frontier and residue triage | the cause and a generic fix for each residue or javac error class | the fix is still implemented by the pipeline and gated as usual |
| 8 | Policy evidence for the owner | e.g. "these SYSOUT lines are runtime statistics" with evidence | the owner decides; never auto-applied |

Not delegated, ever: behaviour, parity verdicts, exclusions, and gates.

## 5. Implementation plan
- **Pipeline (generator owner):** `naming packet`, `naming validate`, `lift --names` (AST rename by opaque id, Javadoc
  Legacy line, names.json source), poison test (no originals, no provenance in the packet), and byte-identity when off.
- **Deep thinker runner:** model-agnostic. It reads the packet plus the notebook and writes proposals. First runner: a
  Claude Code agent (Opus) started by the pipeline or the lead with the packet only (no repo access beyond it). The model
  id goes in every ledger entry. GPT Sol can be a second runner, which allows comparison.
- **Lead:** structural alignment in the compare tool (2.5), the evaluation harness, and the notebook bootstrap from
  knowledge/glossary.
- **First estates:** kontor-zins-legacy (evaluated blind against the kontor original), then fineract-legacy (against
  Fineract). CardDemo after acceptance (review queue).
