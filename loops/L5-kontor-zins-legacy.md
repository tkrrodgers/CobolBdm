# Loop 5 handoff: kontor-zins-legacy, the first PROVENANCE-FREE estate

Why: the A0 ablation showed the J3S lift path depends on J3S provenance comments. This estate has none. It is a
legacy-style COBOL version of kontor-zins, produced by the `legacy` session and accepted by the lead (B1, commit 98f0b0b on
feature/legacy-cobol). The original Java stays the held-out answer key.

Estate (onboarding layout): C:\Users\Tomrodgers\JavaToCOBOLTest-legacy\fineract-cobol-gen\build\kontor-b1\package
- 5 programs (KZLE0100 driver, FSEM0100 event job, FSJBDEC/FNDKRND decimal runtime, CEE3ABD stub), 8 copybooks, 1 JCL,
  2,977 lines;
- 20 scenarios (6 original plus 14 disjoint); reference.json is the original Java outputs.
- A retained >31-digit decimal runtime is a declared frontier. Its content is reduced (26 unreachable paragraphs
  removed), so it differs from the J3S runtime the generator fingerprints.

Pipeline steps:
1. Onboard into CobolBdm as project `kontor-zins-legacy` via the host. GnuCOBOL must equal reference.json on 20/20.
2. Generate "loop5 iter0" with the CURRENT DEFAULT generator (all opt-in rules off) and audit it. That report is the
   baseline. Expect the generic lift path, not the J3S path. Report honestly what lifts (residue, javac).
3. Wire GN4 for its driver (DDs from its JCL).
4. Measure compare fidelity against the original kontor-zins Java (--scope all; there are no J3S headers to read), next to
   the J3S-derived kontor-zins result. That difference is the bias measurement.
5. Exit gate (unchanged): JOBOL PASS against iter0, build clean, strict parity 20/20 under the approved policies, and the
   seven anchors with no regression.

Blinding: the pipeline does not receive the legacy session's ledger, emitter or provenance map. It gets only the estate.
