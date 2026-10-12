# Library-equivalence probe (JavaToCOBOLToJava.md section 5)

Branch `frontier-burndown` (worktree `CobolBdm-burndown`), not committed: the owner reviews.

**Why.** Gemma 4 passed FNDKRND by rebuilding the kernel's digit algorithm on `BigDecimal`, and is stuck on FNDKDFO, yet
these kernels *are* `java.math` operations: the COBOL was generated from Java that called `BigDecimal.round`,
`multiply/divide/add` with a `MathContext`, and `LocalDate` arithmetic. A model cannot be expected to guess that. The
pipeline can establish it from evidence and hand it over as a verified fact. Rounding and date subroutines are in almost
every banking estate, so the probe is generic: it works from the linkage layouts, the CALL traces and the 88-level names,
never from the procedure division.

## What it does

`com.mlcpu.rt.EquivalenceProbe` (java-sidecar, no new dependencies; `EquivalenceCatalog`, `EquivalenceRules`):

```
EquivalenceProbe --program PID --layouts record-layouts.json [--linkage REC[,REC...]] --traces dir|file...
                 [--conditions conditions.json|ast-outline.txt] [--generate 100000] [--learn 2000] [--seed 42]
                 [--max-seconds N] --out dir          -> equivalences.json, equivalences.md
```

1. **Decode** every traced CALL (mlcpu.call-trace.v1, `trace.json` and the new `probe-trace.json`, each CALL once) with
   the runtime's `CobField` codecs; invalid packed or zoned bytes decode to "invalid", not to a number.
2. **Check the faithful program first**: 200 traced CALLs are replayed through it (`ProgramRegistry.callFaithful`) and must
   come back byte-identical. If none does (wrong classes, program not found), nothing is generated and the report says so.
3. **Classify fields**: outputs (changed in some CALL), selectors (text of at most 4 bytes, or a small integer with
   88-level names, with at most 16 codes), the function selector (by name, `FUNCTION`/`-OP`/`-FN`..., or its 88-level names;
   never a rounding-mode or unit selector). 88-level names come from `--conditions` (mlcpu.condition-names.v1 JSON or an
   AST outline) and serve as priors: a field whose names mostly end in HALF-EVEN, CEILING, UP... is a RoundingMode code; in
   DAYS, WEEKS, MONTHS, YEARS a ChronoUnit code.
4. **Pair decimal operands**: a coefficient (integer, 10+ digits) with an exponent/scale/shift field of the same stem
   (`DF-A-COEF` + `DF-A-EXP` = `new BigDecimal(DF-A-COEF, -DF-A-EXP)`), scaled numerics (`S9(13)V9(4)`), big integers.
   Day numbers: the epoch is solved from CALLs where a YYYYMMDD field and a day-number field hold the same date (here
   COBOL INTEGER-OF-DATE, day 1 = 1601-01-01), else the standard ones (INTEGER-OF-DATE, epoch day, Lilian, Rata Die).
5. **Group** the CALLs by the function selector; 88-level codes no traced CALL used become generated-only groups. A
   group that is not verified is split by another selector of the function's own argument, or one named in its failure
   region (DK-STICKY), when every part has at least 5 traced CALLs.
6. **Search the catalog** per group, simplest first: identity/normalization, `round(MathContext)`, `setScale(n)`,
   `setScale(-n)`, exact `multiply/add/subtract`, `multiply/divide/add/subtract` with a `MathContext`, `divide` to a scale,
   an operation then `setScale` (Money.of), percentage (`x × (y ÷ 100)`), rounding of a value with a sticky digit,
   `precision/scale/signum`; `LocalDate` from a day number or YYYYMMDD, `plus(n, unit)`, `unit.between`,
   `lengthOfMonth/lengthOfYear/getYear/getMonthValue/getDayOfMonth/getDayOfYear/getDayOfWeek/isLeapYear`,
   `withDayOfMonth/withDayOfYear/withMonth`, `LocalDate.of(n, 12, 31)` / `(n, 1, 1)`.
   **Output representations** are part of the hypothesis: a pair stripped of trailing zeros, the pair exactly as the
   BigDecimal is, kept digits plus a shift count (`DK-SHIFT = max(0, precision - p)`), a field holding the value exactly
   (no implicit rounding or truncation), a day number, YYYYMMDD. A result field must change in some CALL and take more than
   one value; a group whose CALLs set more than 12 such fields is a record mapping and out of scope (said so).
7. **Codes**: a RoundingMode or ChronoUnit selector is solved per code value: each code keeps the constants that
   reproduce every CALL with that code; 88-level names break ties; up to 20,000 more generated CALLs narrow what is still
   ambiguous; and one constant per code holds for the whole program (the groups' sets are intersected, a group whose choice
   changes is verified again). FNDKDFO's DF-MODE has no 88-level names; its map was solved from data alone and agrees with
   FNDKRND's names.
8. **Learn the rest of the CALL**: every other output field is unchanged, a constant, a copy of another argument field
   before the CALL, or one of these on each side of a condition (`DF-A-EXP < DF-B-EXP`); conditions are tried for at most
   4 fields (a candidate leaving more unexplained is wrong). Exceptions the JDK operation throws (`ArithmeticException:
   Division by zero`, 0/0 counted with it; `DateTimeException`; an invalid input; a result that does not fit) get their own
   learned effects (`DF-RC = 16`, `DF-R unchanged`; or "the program abends"). A CALL with more than one result (FNMY0100
   MU/DV also leave the money amount normalized in MY-DF-R) gets up to two further results from other candidates; the main
   one is the operation with the most inputs.
9. **Accept** a hypothesis only if it reproduces every traced CALL of its group byte for byte (all arguments, every
   output field, the return code), its inputs take at least 5 distinct combinations, and it is the best on 2,000 learning
   CALLs. Then **verify** it on `--generate` CALLs (100,000 by default) generated from the observed distributions (codes,
   ranges, digit counts, signs, with all-nines carries, trailing zeros, exact and near ties, 1% zeros) and run through the
   faithful program. Failures are described by a **failure region**: up to four range terms over input features (field
   values, operand digits, signs, the digits of the exact intermediate, differences such as `DK-PARM - digits(dkValue)`,
   selector codes) that hold every failing CALL; `verified-with-condition` when no matching CALL falls inside it. The
   faithful program's behaviour inside the region is stated too.
10. **Report**: `equivalences.json` (group, Java expression, bindings, code map, result mapping, further results, other
    fields, exceptions, checked counts, input ranges covered, condition, failure examples) and `equivalences.md`, the
    prompt text: verified groups only, then a list of groups not explained by a library operation.

## Pipeline integration

- `CallTrace --probe-out file --probe-max N`: a second, larger sample of the same run (the same spread-over-the-run rule).
  `trace.json` is byte-identical with or without it (checked against the published base trace).
- `CapabilityKitBuilder` (publish): every subprogram trace gets `probe-trace.json` (1,000 CALLs per scenario, every
  acceptance scenario even after the worked examples are full; see the second follow-up) when its
  linkage is at most 4,096 bytes; then `EquivalencesAsync` runs the probe for every subprogram capability of that size: the
  layouts exactly as the record kit publishes them (`RecordKitBuilder.Build(...).LayoutsJson`), the 88-level names of the
  linkage records (`ConditionNames`, mlcpu.condition-names.v1), 100,000 generated CALLs, `--max-seconds 900`, work in
  `work/equivalence/<PID>`. When any group is verified it writes `normalization/<PID>/equivalences.md` and `.json`.
- `NormalizedCapabilities.Import` copies both into the published project's `normalization/<PID>/`.
- `MlCpu.DesignModel`: `DesignStep.Equivalences(packet)` reads `equivalences.md` next to the packet. The design prompt
  gets a `# Verified library equivalences` section after the packet (before the AST outline) and the rule "design that
  group's service operation around the named JDK operation". The implementation prompt gets the same section after the
  acceptance test, before the parity notes and the worked examples, and a contract bullet: implement each listed group
  with the named JDK operation and its field mapping where its condition holds; do not re-derive the COBOL algorithm.
- Requirements packets and their hashes are unchanged (the probe's output is a separate file, like the record kit).
- Callee contracts (follow-up below): after every subprogram has its equivalences, the kit writes
  `normalization/<PID>/callee-contracts.md` for each capability that CALLs one of them directly
  (`CalleeContracts.Build`, MlCpu.CobolKb.Knowledge); `NormalizedCapabilities` imports it; the design and implementation
  prompts get it after the equivalences, as `# Contracts of the programs <PID> CALLs (verified)`, with the rule that a
  model computing a callee's result itself must still produce every effect the contract lists.

## Results on the Fineract estate (2026-10-11)

Traces: `CallTrace` of FNSCHGEN.STEP20 over the 31 short oracle scenarios (base, fineract-unit, rejects, 28 mini), probe
reservoir 1,000; faithful classes: the published `com.mlcpu.core` + capability `legacy` packages compiled against this
worktree's runtime (first publish-v2; the follow-ups reran everything on publish-v3 and publish-v4; the table below is
the first run, before input exploration: the current statements are in the second follow-up).
Reproduce with `docs/equivalence-probe/run-fineract.sh` (writes only under `work/equivalence-probe/$PUBV`, reads the main
tree read-only; about 1.5 minutes). Prompt text and JSON: `docs/equivalence-probe/fineract-cobol/` (`<PID>.equivalences.md`
and `.json`, `<CALLER>.callee-contracts.md`). Every traced CALL of the four kernels falls in a verified group.

| Kernel | Traced CALLs (of total) | Groups verified | Statements |
| --- | --- | --- | --- |
| FNDKRND | 21,332 (of 152,378) | 5 of 5 (2 with condition) | 'D' `dkValue.setScale(-dkParm, mode)`; 'P' + sticky 'N' `dkValue.round(new MathContext(dkParm, mode))` as kept digits + shift count; 'P' + sticky 'Y' the same on dkValue plus a nonzero tail digit, except when `DK-PARM = digits(DK-VALUE)` (then DK-RC = 8, nothing changes); 'S' (never called: generated only) `round(MathContext)`, sticky likewise |
| FNDKDFO | 22,485 (of 189,013) | 4 of 4 | 'M' `dfA.multiply(dfB, new MathContext(dfPrec, mode))`, 'V' `divide`, 'A' `add` (+ the operands swapped in DF-A/DF-B when `DF-A-EXP < DF-B-EXP`), 'N' `dfA`; all stripped of trailing zeros into DF-R; zero divisor: DF-RC = 16 |
| FNDTKRN | 21,149 (of 77,611) | 10 of 10 | DN day number of YYYYMMDD, YM date of a day number, PL `plus(dtN, unit)` (D DAYS, M MONTHS, W WEEKS, Y YEARS), BT `unit.between`, LM/LY/YR/DM properties, WD `withDayOfMonth`, YE `LocalDate.of(dtN, 12, 31)`; day numbers are INTEGER-OF-DATE (solved from the CALLs) |
| FNMY0100 | 20,865 (of 37,975) | 6 of 7 (2 with condition) | OD `myDfA.setScale(ltDecimalPlaces, mode)` (Money.of); MU/DV `myMoneyA.multiply/divide(myDfB, new MathContext(mcPrecision, mode)).setScale(ltDecimalPlaces, mode)` + MY-DF-R = myMoneyA normalized; PC `myDfA.multiply(myDfB.divide(100, mc), mc)` (MathUtil.percentageOf); RS `myDfA.setScale(mcPrecision, mode)`; TD MY-DF-R = myMoneyA normalized. OD and MU fail only when `LT-DECIMAL-PLACES = 0`, `LT-MULTIPLES > 0` and `LT-MULTIPLES-IND = 'Y'` (the tenant-rounding reject: ST-RC = 4, 'RJTNNT03') |

Each verified group: every traced CALL identical, then 100,000 generated CALLs of the faithful program identical (or
identical outside the stated region). Rounding-mode codes: U UP, D DOWN, C CEILING, F FLOOR, H HALF_UP, N HALF_DOWN,
E HALF_EVEN, unambiguous in the three programs that code a rounding mode (FNDKDFO's solved from data alone).

**Not explained**: FNMY0100 'RD' (MY-RATE-TO-DF) is never called; in the traces MY-RATE-A is always zero, so every
candidate gives the same result and the probe says it cannot tell them apart. FNLS0200 (418-byte linkage, also probed at
publish): out of scope, a CALL sets 22 fields (a record mapping); 16 s. The other 11 subprograms have 30 KB to 293 KB
linkage and are not probed.

**Limits of the statements** (stated per group as "generated inputs covered"): inputs are drawn from what the traces show.
FNDKDFO's operands have at most 12 to 14 digits there, so the kernel's 31-digit capacity (RC 20 when an exact product
needs 32 digits, covered by this repository's G0 gate in JavaToCOBOLTest) is never reached and is not part of the
statement. YE's year takes 7 values and TD's amount 6, which the report says.

## Tests and gates

- `EquivalenceProbeTest` (3 tests; the third is in the follow-up below): a synthetic rounding kernel written with integer digit arithmetic (no BigDecimal),
  400 traced CALLs: 'R' must be `rdValue.setScale(-rdPlaces, mode)` with the code map U/D/H/E = UP/DOWN/HALF_UP/HALF_EVEN,
  'S' `round(new MathContext(...))`, both verified on 5,000 generated CALLs, and the non-library function 'X' unexplained;
  a synthetic date kernel: the INTEGER-OF-DATE epoch solved, `plus(dtN, unit)` with D/M = DAYS/MONTHS, the date-of-day
  conversion. (Writing it found a real distinction: a kernel that keeps zero significant digits is not `MathContext(0)`,
  which means unlimited precision, and the probe correctly refuses it.)
- java-sidecar `gradle test`: 13 tests, 0 failures. Conformance gate (`MLCPU_JAVA_SIDECAR=http://127.0.0.1:8089`): **207
  pass, 0 fail** (`work/gate-equivalence-probe.txt`). `MlCpu.Host` and `MlCpu.DesignModel` build; no new warnings (the one
  Host warning, CS8714 in `GnuCobolOracle.cs`, was there before).
- The kit-builder path was exercised with a scratch harness: `RecordKitBuilder.Build` layouts equal the published
  `record-layouts.json` linkage layouts (FNDKDFO, FNMY0100), and the probe on those layouts plus `ConditionNames` output
  gives the same results as on the published files.

## Findings outside the probe (not changed here)

- (Since fixed by another engineer in this worktree: `DataItem.IsLeaf`, used by `ReplayPlanBuilder.Leaves`.) **Fields with
  88-level names were missing from the requirements packets' dataContracts and from the worked examples.**
  `ReplayPlanBuilder.Leaves` visits an item only when `IsElementary`, which is `Children.Count == 0`; an elementary item
  with 88-level children has children, so DK-FUNCTION, DK-MODE, DF-OP, DT-FUNCTION, DT-UNIT, MY-FUNCTION, MC-MODE,
  LT-MULTIPLES-IND... never appear in `dataContracts` or in `examples.md` (FNDKRND's examples never show the function or
  the rounding mode of a CALL). `RecordKitBuilder.AppendConditions` returns early on the same test, so the record kit has
  `dkMode()` but no `isDkModeHalfEven()`. Fixing `Leaves` changes packet hashes; fixing the examples alone does not. Left
  for the owner because Gemma baselines depend on both.
- `findPairs` relies on names (`-COEF`/`-EXP`, `-SHIFT`, `-SCALE`); an estate that names a decimal pair otherwise is
  probed on its fields one by one (still correct, less likely to find the operation).

## Log

| Step | Result |
| --- | --- |
| First run, existing 40-per-scenario traces | FNDKRND: D and P verified; P mixed sticky 'Y' CALLs that the traces never exposed; split by DK-STICKY found the sticky-digit semantics and its exact failing condition |
| Wide traces (`--probe-out`, 1,000 per scenario, 31 scenarios, 16 s for 4 kernels) | about 21,000 traced CALLs per kernel |
| Published classes on a POSIX-style classpath entry | `CallNotFound` on every generated CALL; the probe now replays traced CALLs first and refuses to generate when the faithful program does not reproduce them |
| FNDKDFO 'V': 29 of 100,000 failed | 'E' had been resolved to HALF_UP (no tie in the learning CALLs, no 88-level names); fixed by one constant per code across groups and a disambiguation pass; 0/0 throws "Division undefined", counted with division by zero |
| FNDTKRN BT, WD unexplained | a spurious solved epoch and constant result fields (DT-RC) filled the candidate cap; epochs must be near the best-supported offset, results must change and vary, at most 60 candidates per result |
| FNMY0100 MU, DV unexplained | the CALL has two results (MY-MONEY-R and the money amount normalized in MY-DF-R); further results added |
| FNMY0100 RD "verified" on degenerate data | MY-RATE-A is always zero; results that never vary are rejected |
| FNLS0200: 10 minutes, then junk "verified" on 1-CALL split parts | conditional rules bounded (predicates evaluated once, at most 4 fields), split parts need 5 traced CALLs, inputs need 5 distinct combinations, record mappings (more than 12 result fields) out of scope: 16 s, unexplained |
| Follow-up: exception-path and callee contracts | see the next section |

## Follow-up: exception-path contracts and callee contracts (2026-10-11)

**Why.** With the probe's facts, Gemma's second round passed FNDKDFO 71/71 on attempt 3 (round 1: 63/71 after 15),
writing `dfA.multiply(dfB, new MathContext(prec, mode))`, and FNMY0100 71/71 on attempt 2 (round 1: 4/71). FNLS0200 ended
69/71 after 15: Gemma inlines the Money operations instead of calling FNMY0100 (a legitimate modernization), but on the
tenant-rounding reject (RJTNNT03) its reject record carries a stale site ('RP:287') where the faithful program has
'LAT:580': FNLS0200 MOVEs 'LAT:580' (LAT:330, LAT:331 at its other calls) to MY-SITE, and FNMY0100 copies MY-SITE to
ST-REJECT-SRC when it rejects. FNMY0100's equivalences stated only the normal path; its condition region had no outputs.

**1. Exception-path contracts (the probe).** For every statement with a condition region, the CALLs where it does not
hold are collected: the failing generated CALLs, any failing traced CALL, and up to 1,000 more region CALLs generated with
the text inputs drawn from the whole program's traced CALLs (so that a field copied from an input, MY-SITE, is told apart
from a constant). Per field one relation from a small catalog, in this order: unchanged; a constant; the statement's
value (the main or a further result, such as MY-DF-R in MU); a copy of an argument field as the caller passed it. The
relations are then checked on the whole argument images of every region CALL. A group with no library operation whose
every CALL ends with a nonzero return code (an error path) gets a contract-only statement (`verified-contract`), checked on
the generated CALLs as well. Exception kinds inside a statement (zero divisor, invalid date) now say "every other field
unchanged" too, which the full-image comparison already verified. Failure regions are simplified: redundant terms
dropped, a term on a simpler feature preferred (field values before signs, digits, magnitudes, differences), sides that
do not matter left open (`LT-MULTIPLES >= 1` where the first run said `LT-DECIMAL-PLACES - LT-MULTIPLES <= -1`).
Normal-path side-effect rules now come from the traced CALLs when a group has 20 or more (generated CALLs in a region
could otherwise contaminate them); each exception kind still learns from all CALLs.

| Group | Region (where the statement does not hold) | Contract | Region CALLs, all identical |
| --- | --- | --- | --- |
| FNMY0100 OD | LT-MULTIPLES >= 1 and LT-DECIMAL-PLACES = 0 and LT-MULTIPLES-IND = 'Y' and signum(myDfA) = 1 | ST-RC = 4; ST-REJECT-CODE = 'RJTNNT03'; ST-REJECT-SRC = MY-SITE (the value the caller passed); every other field unchanged | 1,612 (generated; no traced CALL rejects) |
| FNMY0100 MU | LT-MULTIPLES >= 1 and LT-DECIMAL-PLACES = 0 and LT-MULTIPLES-IND = 'Y' | MY-DF-R-COEF, MY-DF-R-EXP as the statement gives them; ST-RC = 4; ST-REJECT-CODE = 'RJTNNT03'; ST-REJECT-SRC = MY-SITE; every other field unchanged | 1,417 |
| FNDKRND 'P'/'S' with DK-STICKY 'Y' | DK-PARM - digits(dkValue) = 0 | DK-RC = 8; DK-SHIFT = 0; every other field unchanged | 3,000 each |

The verified statements and counts are otherwise unchanged (FNDKRND 5 of 5, FNDKDFO 4 of 4, FNDTKRN 10 of 10, FNMY0100 6 of
7; 21,332 / 22,485 / 21,149 / 20,865 traced CALLs, 100,000 generated per group).

**2. Callee contracts.** `CalleeContracts.Build(caller, equivalences)`: for each program the caller CALLs by a literal name
whose probe verified anything, the CALL statements; every place the caller reaches them, meaning the CALL, or an out-of-line PERFORM
whose range leads to it (following PERFORMs), with the argument fields the paragraph writes since the previous such
place (on any branch; places that write nothing are left out); and the callee's verified sections for the selector codes
the caller MOVEs into the argument fields (the others are named as left out). At publish the kit writes it for every
capability; on the Fineract estate 10 callers get one (FNDG0100, FNDKDFO, FNEM0300, FNEM0400, FNEM0700, FNEM0900,
FNLS0100, FNLS0200, FNLS0300, FNMY0100). FNLS0200's (6 KB, `docs/equivalence-probe/fineract-cobol/FNLS0200.callee-contracts.md`)
lists FNDTKRN ('DN' only) and FNMY0100 ('OD', 'PC', 'TD'; DV, MU, RS left out), with the sites

```
- 0000-MAIN, line 47 (PERFORM 1000-PRINCIPAL): INITIALIZE FN-LOAN-TERMS; LT-CURRENCY-CODE = CU-CODE; LT-DECIMAL-PLACES = CU-DECIMAL-PLACES; LT-MULTIPLES-IND = CU-MULTIPLES-IND; LT-MULTIPLES = 0
- 1000-PRINCIPAL, line 65 (PERFORM 8600-CALL-MONEY): MY-FUNCTION = 'OD'; MY-SITE = 'LAT:580'; MY-DF-A-COEF = WS-MONEY-U; MY-DF-A-EXP = -4
- 4000-DOWN-PAYMENT, line 161 (PERFORM 8600-CALL-MONEY): LT-DOWNPAY-AMOUNT = 0; ST-RC = 4; ST-REJECT-CODE = 'RJNULL01'; ST-REJECT-SRC = 'LAT:331'; MY-FUNCTION = 'TD'; MY-MONEY-A = LT-PRINCIPAL
- 4000-DOWN-PAYMENT, line 168 (PERFORM 8600-CALL-MONEY): MY-DF-A = MY-DF-R; MY-DF-B-COEF = WS-PCT-U; MY-DF-B-EXP = -9; MY-FUNCTION = 'PC'; MY-SITE = 'LAT:331'
- 4000-DOWN-PAYMENT, line 173 (PERFORM 8600-CALL-MONEY): MY-DF-A = MY-DF-R; MY-FUNCTION = 'OD'; MY-SITE = 'LAT:330'
```

and, under OD, "where it does not hold, when LT-MULTIPLES >= 1 and LT-DECIMAL-PLACES = 0 and LT-MULTIPLES-IND = 'Y' and
signum(myDfA) = 1: ST-RC = 4; ST-REJECT-CODE = 'RJTNNT03'; ST-REJECT-SRC = MY-SITE (the value the caller passed); every
other field unchanged". (Line 161's writes include its own reject branch: "on any branch" is said in the text.)

`docs/equivalence-probe/kit-harness/` (renamed from `callee-contracts/` in the second follow-up) holds the small console
project `run-fineract.sh` uses to produce these files outside a publish (it calls the same `CalleeContracts.Build`).

**Tests and gates.** `EquivalenceProbeTest` gained a third test: a synthetic money kernel whose amount-of function rejects
when places = 0 and a flag is 'Y' (RC = 4, a reject code, the reject source copied from the caller's site), never in the
traces; the probe must state the statement with that condition, the contract `MN-RC = 4; MN-REJ-CODE = 'REJ00001';
MN-REJ-SRC = MN-SITE (the value the caller passed); every other field unchanged` on at least 1,000 region CALLs, and an
always-failing function 'Z' as `verified-contract` (MN-RC = 12). java-sidecar `gradle test`: 14 tests, 0 failures.
Conformance gate on this worktree's sidecar (:8089, started and stopped by me): **244 pass, 0 fail**
(`work/gate-equivalence-probe-2.txt`). MlCpu.Host, MlCpu.DesignModel and MlCpu.CobolKb.Knowledge build; no new warnings.

## Second follow-up: the kit and the standalone run agree; inputs explored; deterministic (2026-10-12)

**Why.** The publish of publish-v4 (main tree) gave weaker statements than `run-fineract.sh`: FNMY0100 5 of 7 groups, OD
without the reject region and contract, PC "not explained"; FNDKDFO 3 of 4. FNLS0200's published callee contracts lacked
the reject contract they were built for.

**Cause.** `CapabilityKitBuilder.CallExamplesAsync` traced scenarios inside the loop that writes the worked examples, and
stopped the loop when the examples text passed 70,000 characters: the probe saw 9 scenarios for FNMY0100 and FNDKDFO, 11
for FNDKRND, 15 for FNDTKRN (of 31), with `--probe-max 500`. FNMY0100's PC group had 3 traced CALLs with 2 distinct inputs
(untestable), and no traced CALL had 0 decimals with LT-MULTIPLES-IND 'Y', so the generator never produced the tenant
rounding reject. The standalone run traced all 31 scenarios at 1,000 CALLs.

**Changes.**
- Kit: once the examples are full, the remaining scenarios are still traced (not rendered) for programs the probe runs on
  (linkage up to 4 KB); `examples.md` is unchanged. The probe's constants and arguments are shared
  (`EquivalenceProbeInputs`, MlCpu.CobolKb.Knowledge: `ProbeTraceCalls` 1,000, 100,000 generated CALLs, 900 s, the
  88-level export `ConditionNames`, the argument list), used by the kit and by `docs/equivalence-probe/kit-harness`
  (`inputs`: the kit's `record-layouts.json` via `RecordKitBuilder` and `conditions.json`; `contracts`: callee contracts).
  `run-fineract.sh` now runs exactly the kit's steps: the same inputs, base plus `project.json` acceptance scenarios, the
  same CallTrace and probe arguments.
- Exploration: every other generated CALL explores. An input field (not a return-code or status field, not a field the
  program writes in a traced CALL) with 88-level names takes any of its declared values; a small integer field (at most
  12 values in the traces) takes 0 to max(largest traced value, 4), or 0 to 4 and its traced values when those are large
  (LT-DECIMAL-PLACES 0..4, LT-MULTIPLES 0..4 and 100, MC-PRECISION 0..12). The candidate search still sees only traced and
  plain generated CALLs. Every generated CALL runs through the faithful program, so exploration finds behaviour and
  cannot invent it.
- Where a statement does not hold, the failures are clustered by what the faithful program reported (its abend, or its
  return and status codes). Each cluster gets its own region (learned against the CALLs that match, up to four terms,
  the shortest prefix whose contract explains every CALL in it) and its own contract. Abend regions come first and a CALL
  belongs to the first region it meets ("otherwise, where it does not hold, when ..."). A statement is
  verified-with-condition when every failure lies in a region that its contract explains, or in which the statement
  practically never holds (at most 2% coincidental matches; the text then says the program does something no single
  relation describes there).
- Side-effect rules: among the rules that fit the traced CALLs, generated CALLs choose ("unchanged" versus "set to 0" when
  the traces only ever pass 0). Candidates are scored on CALLs that change their result fields (a candidate agreeing
  where a status field stays 0 says nothing). A further result must explain traced CALLs the main one does not.
  Conditional rules try a field against zero before two fields against each other (`when DF-PREC = 0`, not
  `when DF-PREC = DF-RC`).
- Deterministic: all randomness is seeded per group; the one `Map.of` in the JSON writer (its iteration order varies
  per JVM) is an ordered map. Two runs give byte-identical `equivalences.md`, `equivalences.json` and callee contracts,
  and so do runs on the published classes and on the project's transpiled classes (the kit's classpath).

**Results on publish-v4** (`docs/equivalence-probe/fineract-cobol/`; 31 scenarios; every traced CALL in a verified
group; 100,000 generated CALLs per group, half of them exploring):

| Kernel | Traced CALLs | Verified | Conditions found by exploration |
| --- | --- | --- | --- |
| FNDKRND | 21,332 | 5 of 5 (4 with condition) | 'P'/'S': DK-PARM = 0 (COBOL keeps zero digits, `MathContext(0)` means unlimited: not one relation); sticky 'Y': DK-PARM = digits(DK-VALUE) gives DK-RC = 8 |
| FNDKDFO | 22,485 | 4 of 4 (1 with condition) | 'V': when DF-PREC = 0, DF-RC = 12 and every other field unchanged (1,688 region CALLs); a zero divisor gives DF-RC = 12 when DF-PREC = 0, otherwise 16 |
| FNDTKRN | 21,149 | 10 of 10 | none |
| FNMY0100 | 20,865 | 6 of 7 (4 with condition) | OD, MU, DV: the tenant-rounding reject `LT-DECIMAL-PLACES = 0 and LT-MULTIPLES >= 1 and LT-MULTIPLES-IND = 'Y' and signum(amount) = 1`: ST-RC = 4; ST-REJECT-CODE = 'RJTNNT03'; ST-REJECT-SRC = MY-SITE (the value the caller passed); every other field unchanged (MU, DV: MY-DF-R as the statement gives it), 3,000 / 3,000 / 1,982 region CALLs; DV, PC: MC-PRECISION = 0 ends abnormally (ABEND 2). RD: untestable (MY-RATE-A is always zero) |

FNLS0200's `callee-contracts.md` (6 KB) carries FNMY0100's OD statement with the reject contract and the site
`1000-PRINCIPAL, line 65 (PERFORM 8600-CALL-MONEY): MY-FUNCTION = 'OD'; MY-SITE = 'LAT:580'; ...`, PC with its abend
region, TD, and FNDTKRN's DN.

**Tests and gates.** `EquivalenceProbeTest` (3 tests): the rounding test now expects the condition exploration finds
(RD-PLACES = 0, where the synthetic kernel keeps zero digits and `MathContext(0)` keeps all), the money test the reject
region and contract as one region. java-sidecar `gradle test`: 14 tests, 0 failures. Conformance gate on my :8089
sidecar: **244 pass, 0 fail** (`work/gate-equivalence-probe-3.txt`; two wording-only edits to the probe's markdown came
after it). MlCpu.Host and MlCpu.CobolKb.Knowledge build; no new warnings.

