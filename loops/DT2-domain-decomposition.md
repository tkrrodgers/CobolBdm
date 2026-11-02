# DT2: domain decomposition (the deep thinker's second task). PROPOSAL, for Tomrodgers's review

Status: proposal by the lead, 2026-11-02. Not dispatched to the pipeline. DT1 (naming) is accepted
(DT-deep-thinker.md 2.8). Naming made the code readable, but fidelity to the original design needs the domain TYPES,
which renaming cannot create (2.6: no modern type has the shape of LoanTerms, Installment or Schedule).

## 1. The finding that makes DT2 tractable: the domain structure is already in the COBOL
kontor-zins-legacy, copybook KZCASE (the per-loan working state):

| COBOL group | Its items | Domain meaning | The original Java (lead-only answer key) |
|---|---|---|---|
| `TERM-DATA OCCURS 2` | TERM-PRINCIPAL S9(12)V99, TERM-RATE S9(3)V9(12), TERM-MONTHS 9(4) | loan terms (up to 2 offers) | `LoanTerms(principal, annualRate, months)` |
| `PLAN-ROW OCCURS 600` | ROW-PAYMENT, ROW-INTEREST, ROW-PRINCIPAL, ROW-DEBT, each S9(18)V99; the month is the index | one installment | `Installment(month, payment, interest, principalPart, remainingDebt)` |
| `PLAN-DATA OCCURS 2` | PLAN-MONTHS, PLAN-PRINCIPAL, PLAN-INTEREST, PLAN-REPAID, PLAN-PAID, plus the PLAN-ROW table | a repayment schedule and its totals | `Schedule(terms, List<Installment>)` with totalInterest/totalPaid/totalPrincipal |

The generator flattens these into indexed accessors on one 200-byte-style record image (`CaseWorkRecord.termPrincipal(i)`,
`planRowPayment(i, j)`). The shapes are there; they are not yet types. This is the generic pattern on real estates too:
an OCCURS group read and written as a unit is a value type, and its table is a List.

## 2. Division of labour (the same shape as DT1)
**The thinker proposes. It is judgement over evidence, and the original Java never enters its packet.**
- Which groups are domain values and which are scratch or I/O images. Cautions C001/P003 apply.
- The type name and the component names. The component names come from the DT1 ledger where they exist.
- The index meaning: PLAN-ROW's index is the installment's month, so the record gets a `month` component.
- Which paragraphs are operations ON a value: 5100-BUILD-INSTALLMENT computes one PLAN-ROW from the balance and the
  rate; 5000-BUILD-PLAN builds the table; 6200-OUTPUT-SUMMARY reads the totals.
- Collection semantics: OCCURS 600 becomes List<Installment> with size = PLAN-MONTHS. OCCURS 2 becomes a bounded
  pair, so a 2-element List<LoanTerms> or named slots.

**The generator decides deterministically, or fails closed.**
1. Group eligibility, by data flow from the COBOL:
   - every reference to the group's items is through the group's own subscript;
   - no REDEFINES overlays it, unless proven a pure view;
   - no byte-level MOVE of the whole group to a differently laid-out area, unless that is an I/O boundary (kept as a
     record image with a mapping).
2. Emit an immutable Java `record` per accepted group:
   - components typed from the PICTURE, using BigDecimal with the declared scale, or int/long when proven;
   - the group's image becomes an adapter: `LoanTerms CaseWorkRecord.terms(int slot)` and
     `CaseWorkRecord.terms(int slot, LoanTerms)`;
   - OCCURS tables become `List<T>` views sized by their DEPENDING ON or count item.
3. Operations: a paragraph proposed as an operation is lifted to a pure method or static function only if its
   data-flow is CLOSED: it reads only the bound inputs and writes only the bound outputs, plus declared kernels. Then
   the batch path CALLS the domain function (strangler style). Otherwise it stays in the service and fails closed for
   that operation.
4. Proof, the loop-1/5 kernel method: a differential gate between the domain function and the lifted reference
   path, on the scenario inputs plus generated boundary inputs, with mutants. Strict GN4 parity is unchanged.
   JOBOL is measured with code-LOC.

## 3. Acceptance (pre-register before running)
- On kontor-zins-legacy, `compare --align structural` finds structural partners for **LoanTerms, Installment and
  Schedule**, where today it finds 0/5 (best 49.6%, below threshold). Similarity >= 0.5, the threshold unchanged.
- Name fidelity of those pairs is reported separately: DT1 names flow into the new components.
- Behaviour: strict 20/20; differential gates 0 differences with all mutants killed; off-path byte-identity for the 7
  anchors and loop 5.
- JOBOL under code-LOC not worse. Expected better: fewer scratch fields and a smaller business service.
- Generic: the same pass must run unchanged on fineract-legacy (OCCURS tables of repayment schedule rows) and report
  what it accepted and rejected there.

## 4. Order of work (proposed)
1. Pipeline: the group-eligibility analysis as a report only. It lists every OCCURS/group with its verdict and reason
   on kontor-zins-legacy and fineract-legacy. No code changes. This alone tells us how much structure is recoverable.
2. Thinker: the DT2 packet (eligible groups plus their references) leads to proposals (domain types, components, index
   meaning, operations), prompt `domain-1`, with the same blinding and citation rules as naming-1u.
3. Pipeline: emit records and adapters (`lift --domain <ledger>`, opt-in, byte-identical when off). Then operations
   with closure proofs and gates.
4. Lead: structural compare plus the blind evaluation against the originals.

## 5. Open questions for Tomrodgers
- Scope: start with the value types and adapters (low risk), or include lifting operations to pure functions in the
  same milestone?
- OCCURS 2 offers: model as `List<LoanTerms>` (generic) or two named slots (closer to the COBOL)? The lead recommends
  List, because it is generic and the original has a single LoanTerms per schedule.
