# Loop 2 handoff: fineract-legacy (J8 legacy-idiom estate)

Estate: CobolBdm projects/fineract-legacy, onboarded from JavaToCOBOLTest-j8/fineract-cobol-gen/out/tier1-2026-10-11-legacy.
- 21 programs, 7,915 lines.
- Batch FNLS0100 and FNRP0100; 58 scenarios.
- Legacy binaries in projects/fineract-legacy/oracle/bin (FNLS0100.exe, FNRP0100.exe, LOAD/UNLOAD-FINERACT_C-a270e232, CEE3ABD).

Lead's dry run (same generator snapshot): lifted 49 functions, residue 128 (5 RESIDUE calls), javac 37 errors,
JOBOL 93.9 F, modern lane 20.6%.

Why it is loop 2: the COBOL is written in deliberate legacy idioms, so it is the strongest test that loop 1's generic
lift is not tuned to generator-clean COBOL. Expect GO TO / PERFORM THRU structures, REDEFINES and 88-levels at scale.
- Structure them with the proven, fail-closed IR passes.
- Every new pass needs evidence-based naming and the per-step JOBOL delta.

Exit gate: as loop 1. No regression on loanamorti, fineract-xl final and fineract-cobol final.
