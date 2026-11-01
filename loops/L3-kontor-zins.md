# Loop 3 handoff: kontor-zins (new J3S-T target, prepared by the lead overnight)

Source: manikouchakian/kontor-zins @b507fe6efd7d9ccf6d7015ecbe92e1466d5a299c (MIT). An annuity loan calculator in exact
BigDecimal: 5 main classes, 581 lines. Workbench readiness: "ready".

J3S conversion: run 20261101-031551-kontor-zins
(C:/Users/Tomrodgers/JavaToCOBOLTest/claude-in-app/workspace/runs/20261101-031551-kontor-zins).
- target/: target.yaml and replay.yaml with 9 event types (MP, NT, MT, SC, QI, QR, CO, SY, YS); offers come as 'P' records.
- Corpus: accept-01 (30 cases) and replay-01..05 (200 cases each), with edges, 1% damaged records and invalid inputs
  that trigger the library's IllegalArgumentException.
- Build reproducible (39 members byte-identical); cobc clean; 30 programs, 15,563 generated lines.
- G2: 6/6 PASS byte-identical against oracle A (249,964 records, 696 'X' rejects). Lead verified target/g2/g2.json at 03:37.
- Estate (packaged for CobolBdm): .../runs/20261101-031551-kontor-zins/estate.
  - 31 programs, 10 copybooks, JCL KZANNUIT, 6 scenarios, 15,844 lines.
  - reference.json maps KONTZINS.RESULTS.DATA to oracle-a/<scenario>/RESULTS.dat.
- J3S generator additions: JavaToCOBOLTest-l3 worktree, branch feature/l3-target, commit c054e42 (not merged, not
  pushed). They add BigDecimal.pow(int, MathContext), Math.multiplyExact and List.copyOf emulations.
  - G0: 93,668 records with 0 differences; each emulation's mutant caught.
  - Fineract J3S build: 156 members byte-identical before and after.

Known limits:
- No LocalDate in the API.
- The CO event outputs only the offer count, because the spec grammar has no raw-list element type.
- A negative-exponent pow is bounded by FSJBDEC capacity; kontor-zins never uses one.

Pipeline steps:
1. Onboard into CobolBdm. Use the host (POST /api/projects/onboard?name=kontor-zins&source=<estate>/source), copy
   scenarios/*, list them under oracle.scenarios, and run the oracle (GnuCOBOL must equal reference.json on 6/6).
   Follow the loancalc-74c5d4f precedent (onboarded from runs/r1-lac/estate).
2. Generate "loop3 iter0" with the current generator and audit it: that is the baseline.
3. Wire GN4 for the KZANNUIT driver step: the event-job pattern, like LCLE0100/FSLE0100.
4. Exit gate:
   - JOBOL PASS vs iter0, build clean, GN4 6/6 (plus error paths: damaged records, IllegalArgumentException rejects);
   - all regression baselines green (loanamorti, fineract-xl, fineract-cobol, fineract-cobol-cr, fineract-legacy final).
