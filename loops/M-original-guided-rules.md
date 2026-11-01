# Phase M: original-guided generic rules (from "not JOBOL" to "good Java")

Decision (Tomrodgers, 2026-11-01): proceed. Lead: the desktop session (design and measurement). Implementer: the VS Code
pipeline session (GPT Sol 6.1 with checkers). The generator, harnesses and pinned policies stay owned by the pipeline.

## The rule that keeps it honest
The original Java is a held-out answer key. It is used only to measure and to discover gap patterns.
- The generator (MlCpu.Modernize) never reads original Java, its names or its structure: no per-estate hints, no
  "class X should be called Y".
- Every rule is generic, evidence-based from the COBOL/IR/estate itself, and fails closed (residue or skip when the
  evidence is missing or ambiguous), like the existing passes.

## Steps
1. **Compare** (lead; running). `jobol-audit compare --original --modern` on joda-money, kontor-zins, loanamorti and
   fineract-xl. It measures:
   - type and package alignment;
   - fields, methods and signatures;
   - API preservation; name fidelity (truncations, numbered temporaries); idioms (Optional, streams, collections,
     records, enums, java.time); method structure.
   Output: a per-estate distance report, plus COMPARE-GAPS.md with recurring gap patterns, each classified (a) closable
   from COBOL evidence, (b) needs JDK-idiom knowledge, or (c) not worth closing.
2. **Specify** (lead). For each (a)/(b) pattern, a rule spec with:
   - the trigger evidence in the COBOL/IR;
   - the transformation;
   - the fail-closed condition;
   - the expected effect on the distance metric;
   - its train estates and test estates.
3. **Implement** (pipeline). One rule per milestone, in the generator only. Each milestone reports:
   - distance deltas on its TRAIN and TEST estates separately;
   - the JOBOL score; strict parity; all seven anchors.
4. **Train/test discipline.**
   - Derive from: fineract-xl and kontor-zins.
   - Validate on: joda-money and loanamorti. These are not consulted while designing a rule.
   - A rule that improves only its train estates is rejected as copying.
   - (The lead may adjust the split per pattern once the catalogue shows where each pattern occurs; the split is fixed
     before implementation.)
5. **Gates (unchanged, mandatory).**
   - Strict parity under the approved policies only: v2.1, the profile, the FSLS/FSLE/JMLE statistics rules.
   - The JOBOL gate. No regression on the seven anchors (legacy e49f5b, cobol a3fb54, cr be10bb, loanamorti 7edea1,
     xl f36f0c, kontor c2e23d, joda 203e70).
   - Distance to the original counts only when behaviour is identical.
6. **Final test.** CardDemo (native COBOL, no original). The learned rules must make it modern by the same gates. That
   is the generalization proof. It starts only when Tomrodgers says so.
