# SME design rubric (fixed 2026-10-10, before any design exists)

This rubric scores architecture designs (`mlcpu.service-design.v1`, protocol step 1a) for the Customer Account Service.
It is applied blind:
* the scorer sees anonymized designs under random labels;
* the scorer gets this rubric, `SPEC.md`, `RULES.md`, the digests and `cas-api`;
* the scorer does not get the model names, seeds or any other score.

Each criterion is scored 1 to 5 against the anchors, with a one-paragraph justification that cites the design. A score
between anchors uses the nearer anchor; 2 and 4 are allowed.

| Criterion | Weight |
|---|---|
| C1 Domain model | 25% |
| C2 Rule placement and traceability | 20% |
| C3 Money, rounding and truncation | 15% |
| C4 Idempotency and the batch versus real-time split | 15% |
| C5 The statement feature | 15% |
| C6 Testability and simplicity | 10% |

**Design score:** the weighted mean, 1.00 to 5.00, two decimals.

## C1 Domain model: aggregates and invariants match the business, not the COBOL file layout (25%)

* **5:** Each context has aggregates named for business concepts:
  * card account with its cycle totals; category balance; customer with its profile and contacts.
  * Their invariants are stated as rules the aggregate enforces: credit limit, expiry, cycle close, unsigned profile
    figures.
  * Value objects carry meaning: money with its scale, cycle, reason codes.
  * Persistence snapshots (`cas-api` port records) are mapped at the context boundary and do not leak into the domain
    logic as the model.
  * Posting and interest share one account model.
* **3:** The aggregates are recognizable, but some are file shaped:
  * a "TCATBAL record" or a "transaction file record" treated as the model;
  * or logic working directly on the port records;
  * or the invariants listed but not owned by an aggregate.
* **1:** The design mirrors the COBOL programs and files: one class per program or paragraph, a procedural service per
  file, no aggregates or invariants.

## C2 Rule placement and traceability (20%)

* **5:** Every digest and SPEC rule (POST, INT, RPT, CUST, UPD, AVG, STMT, IDEM) is traced to a specific class and method,
  and each placement is where the rule belongs:
  * validation in the domain;
  * orchestration in the application;
  * lookups through ports.
  The precedence rules (POST-06, UPD-05) and the decisions D-1 to D-11 are visibly handled.
* **3:** Most rules are traced, but some are traced only to a module or a "service", or a few sit in the wrong layer: for
  example business rules in the application, or ordering rules lost.
* **1:** The trace is missing, generic or wrong for many rules.

## C3 Money, rounding and truncation handled where the legacy rule has them (15%)

* **5:** The design names the exact numeric behaviour at each point:
  * interest truncated to cents (INT-07);
  * unsigned and truncated SAM fields (UPD-08, UPD-09) and the two implied decimals (UPD-03);
  * AVG-01 whole-part sums;
  * exact sums elsewhere;
  * scales preserved (2 decimals; scale 0 for the average).
  It does this with a deliberate abstraction (a money or amount type, or named policy methods) and no floating point.
* **3:** BigDecimal throughout and the main truncation (interest) is right, but some legacy numeric rules are missing or
  vague ("round as needed").
* **1:** Floating point, unspecified rounding, or numeric rules ignored.

## C4 Idempotency and the batch versus real-time split (15%)

* **5:** One posting path serves both `postTransaction` and `postBatch`, with the batch as a loop over it that sees earlier
  effects. The design covers:
  * command keys through the journal, recorded only after success (IDEM-01, IDEM-02);
  * the per-account cycle check (IDEM-03);
  * duplicate transaction ids (IDEM-04, POST-01);
  * all-or-nothing interest by validating before writing (INT-10).
  Each is placed deliberately, mostly in the application layer.
* **3:** Shared posting logic and journal-based keys, but one of the per-cycle check, the duplicate check or the
  all-or-nothing interest is missing or misplaced.
* **1:** Batch and real time duplicated, or idempotency absent or naive (for example "check if result exists" without
  defining when it is recorded).

## C5 The new statement feature: correct sources, no duplicated logic (15%)

* **5:** The statement reads the right sources through ports:
  * the customer profile and its link;
  * the accounts, the ledger and the rejection log.
  It computes the STMT rules exactly: closing from current balance minus later entries, opening from closing, interest by
  cycle tag. It reuses the AVG-01 rule without duplicating customer logic: a shared policy is reachable from statements
  only through `cas-api`-level data, or the computation is placed so the rule lives once. It respects A-1.
* **3:** Mostly right sources, but a balance or cycle rule is vague or wrong, or customer logic is copied into
  statements without acknowledging it.
* **1:** Wrong sources (for example opening balance taken from somewhere that does not exist), or the statement calls
  other contexts directly against A-1.

## C6 Testability and simplicity (10%)

* **5:** Small, cohesive classes with pure functions for rules. Ports are injected and the clock comes through
  `CasPorts`. Few layers, each justified, with no speculative abstractions. The design can be implemented by another
  developer from the document alone.
* **3:** Workable but with some heavy patterns (deep hierarchies, generic frameworks, many interfaces with one
  implementation) or some ambiguity about where code goes.
* **1:** Over-engineered or under-specified to the point that an implementer would have to redesign.

## Consistency check

A second scorer re-scores two randomly chosen designs, without seeing the first scores. Any criterion that differs by more
than one point is reported with both justifications.
