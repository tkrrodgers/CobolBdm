# Morning review: programme status (paused for Tomrodgers's review)

Paused by the lead at the stop condition Tomrodgers set: "decent grade across multiple COBOL to modern Java estates by the
deterministic evaluation". All numbers below were re-verified by the lead (JOBOL ruleset 1.1.0, hash 0018a27e11dbcfd2).

## 1. Results

### Provenance-free estates (the real evidence: no J3S comments quoting the original Java)
| Estate | Origin | JOBOL | Strict parity | Lane |
|---|---|---|---|---|
| kontor-zins-legacy | legacy-style COBOL (`legacy` session, B1) | **9.8 A** (from 42.6 C) | 20/20 | loop 5, the blind lead agent overnight |
| fineract-cobol | Tier-1 generator | 12.3 B | 58/58 | loop 1 |
| fineract-cobol-cr | Tier-1 clean room | 12.3 B | 58/58 | loop 1 |
| fineract-legacy | J8 legacy idioms | 14.4 B | 58 + 13 + 640 | loop 2 |

### J3S round-trip estates (valid with strict parity, but they rely on J3S provenance comments: see A0)
| Estate | JOBOL | Strict parity |
|---|---|---|
| fineract-xl | 14.5 B | 46/46 |
| loanamorti | 0.0 A | 7/7 + 21/21 |
| kontor-zins | 0.0 A | 56/56 |
| joda-money | 1.5 A | 41/41 |

## 2. The key findings
- **A0 (provenance ablation):** with only the J3S provenance comments stripped, the J3S lift path fails on all four J3S
  estates. They don't lift, don't build, and recover 15-25% of the functions. Their "good Java" was largely a J3S round
  trip.
- **The bias measured:** the same business logic (kontor-zins) scored 8.0 A at iteration 0 as J3S COBOL, but 42.6 C and
  failed to compile as legacy-style COBOL. Overnight, generic generator work took the legacy version to **9.8 A with strict
  20/20 parity**.
- **What generalized:**
  - the kernel-replacement method: date, rounding, and now a whole decimal library, replaced by the JDK behind
    differential gates (decimal: 686,922 cases, 0 differences, 6/6 mutants caught);
  - the generic subprogram and batch lift;
  - strict parity discipline.
- **What did not:**
  - fidelity to the original Java's names and types on provenance-free COBOL is 0/5 types matched (class names come
    from COBOL program ids);
  - Phase M rules R1 and R5 were rejected by their pre-registered tests; R3 was accepted only behind closedWorld.

## 3. What happened overnight (the lead's actions, all within the delegated mandate)
- ~18:49: both VS Code sessions went idle and could not be woken from the desktop session.
- 19:36: under your instruction ("if they stop, continue the work"), the lead snapshotted the pipeline's UNCOMMITTED
  generator into an isolated repo, C:\Users\Tomrodgers\Source\CobolBdm-l5lead (26604b6). A BLIND agent then did loop 5
  there: it saw only the estate, never how it was produced. Nothing was written to the CobolBdm main checkout.
- Commits in CobolBdm-l5lead, all generic:
  - `d565587`: ClassicLift fixes. S9(18) COMP subscripts; zoned >18-digit stores; `DIVIDE a INTO b` without GIVING was
    silently dropped (an infinite loop); GOBACK in a performed paragraph; COMPUTE `/` lifted as integer division.
  - `d428392`: DecimalKernels. The decimal library becomes BigDecimal behind a differential gate; capacity becomes
    declared guards.
  - `ff87992`: a tolerated RC after a kernel CALL; layout copies named after their records.
  All 7 anchors are byte-identical to the baseline generator after every milestone. Evidence:
  CobolBdm-l5lead\work\LOG.md.

## 4. Decisions for you
1. **Merge the overnight generator work.** The 3 commits in CobolBdm-l5lead sit on top of a snapshot of the pipeline's
   uncommitted MlCpu tree. Hand them to the pipeline session to apply, or apply them yourself, into CobolBdm. Commit the
   pipeline's own long-uncommitted generator work first.
2. **The J3S branch:** JavaToCOBOLTest-l3 feature/l3-target (c054e42, 21735cd and the reference-equality fix c738e10) is
   unmerged and unpushed.
3. **The legacy branch:** JavaToCOBOLTest-legacy feature/legacy-cobol (realism auditor a503687/9d7d2c4, B1 98f0b0b) is
   unmerged.
4. **Answers the lead proposes to the overnight agent's questions (please confirm):**
   - (a) DecimalKernels genericity: the operation semantics are guided by the library's own paragraph names and then
     PROVEN by the gate, failing closed. Acceptable. Cryptically named human libraries would stay lifted, which is safe.
   - (b) GnuCOBOL's 38-digit division scale rule in ClassicLift: acceptable for GnuCOBOL-oracle parity. Record it as a
     dialect property; IBM ARITH semantics differ.
   - (c) Add `.gitattributes` `*.cs eol=lf`, so the generated Java templates are not CRLF under autocrlf.
   - (d) Capacity guards are proven only against the lifted reference, not against native abends. Add native capacity
     probes in a later loop.
5. **Next direction, when you resume:**
   - CardDemo (native human COBOL) on the generic path;
   - the `legacy` session's B2 (traditional style) and the loan calculator;
   - a domain step on kontor-zins-legacy: FSEM0100's value groups as typed BigDecimal domain classes. That is what
     would move fidelity.

## 5. Where everything is
- Ledger: ModernizationLoops.md. Design and decisions: JavaJOBOLAuditor.md §10. Phase M: loops/M-*.md. Loop briefs:
  loops/L*.md.
- The hub: ~/.jobol-hub (signed messages; reports in reports/).
- Both VS Code sessions: idle. Their inboxes hold the lead's latest notes.
