# Loop 1 handoff: fineract-cobol (Tier-1, J1-J5 "gen lower"), with fineract-cobol-cr as the independence check

Estate: CobolBdm projects/fineract-cobol, onboarded from JavaToCOBOLTest/fineract-cobol-gen/out/tier1-2026-10-11.
- 18 programs, 5,680 lines.
- Batch steps: FNLS0100 (schedule, 714 lines) and FNRP0100 (report, 234 lines); jobs FNSCHGEN, then FNSCHRPT.
- 16 subprograms: FNDG0100, FNEM0100..FNEM0900, FNLS0200, FNLS0300, and the kernels FNDKDFO, FNDKRND (rounding),
  FNDTKRN (dates as day numbers) and FNMY0100 (money).
- 58 scenarios (fineract-unit-*, mini-p03-*, ...).
- Legacy binaries in projects/fineract-cobol/oracle/bin: FNLS0100.exe, FNRP0100.exe, LOAD-/UNLOAD-FINERACT_C-a270e232.exe
  (the currency KSDS).

Lead's dry run (generator snapshot of the main working tree, 2026-10-31 21:25):
- lift: 18 programs, but "0 functions, 0 classes". Only the batch layer was lifted (Fnls0100Batch, Fnrp0100Batch:
  34 functions); the 16 subprograms produced no domain classes.
- Residue 87, including RESIDUE("DT-YYYYMMDD") for schedule dates.
- javac: 31 errors from type solving: BigDecimal <-> long x8, String <-> int x5, valueOf(String) x5, relational
  operators on non-numerics x11, symbols x2.
- JOBOL 93.2 F (Fnls0100Batch 100, Fnrp0100Batch 100); modern lane 18.8%.
- fineract-cobol-cr gives identical numbers (34/87/31/93.2), so the same generator fixes should carry over.

What this loop must prove: MlCpu.Modernize generalizes beyond J3S frames and pools (modernizeJavaCode.md section 2).
1. A generic subprogram lift for Tier-1 CALL subprograms: LINKAGE records become typed parameters and results, and
   the FNEM* chain and kernels become domain classes. Do not wrap them as faithful facades; JOB001 blocks that.
2. Type solving from copybook PICTUREs: 9(n) -> int/long, S9(n)V9(m) -> BigDecimal with scale (the IBM mapping note),
   X(n) -> String. Dates in DT-YYYYMMDD become LocalDate at the record boundary.
3. Zero residue and zero javac errors, then the batch passes already proven on J3S (iterable datasets, RunTotals,
   enums, record views, EventSequence-style ownership).
4. GN4 wiring: gn4.sh maps LOAN.REQUEST -> FSLS0100 only. Add FNLS0100 (DDs from the estate's FNSCHGEN JCL) and the
   FNRP0100 report step (FNSCHRPT). Use the legacy binaries above.

Exit gate (ModernizationLoops.md section 1):
- fineract-cobol: JOBOL PASS vs its loop-1 iteration-0 report; build clean; GN4 58/58.
- fineract-cobol-cr: builds clean and GN4 58/58 with the same generator, as the independence check.
- No regression: loanamorti (3ae043) and fineract-xl final stay PASS with change <= 0, and GN4 stays green.
