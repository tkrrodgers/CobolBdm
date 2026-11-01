# J3S defect: reference equality on Object-typed slots ignores the value

Found by the loop 4 agent (Joda-Money conversion). Analysed read-only by a second agent. The lead confirmed the source
lines (2026-11-01 05:40).

## Defect
`fineract-cobol-gen/gen/src/main/java/fncobolgen/j3s/Lower.java`, `comparison(...)`. The final `else` (lines 2215-2216,
on the l3 worktree) handles two ANY slots (Object or type-variable static types). It compares only the class id `-C`
and the pool index `-I`, never the payload `-V`. Converting a value to ANY sets `-I` to 0. So any two non-null values
of the same value class compare `==`: Integer, Long, Boolean, BigDecimal, LocalDate, MathContext, and JDK enums held in
Object slots. `JObjects.equals` (`a == b || a != null && a.equals(b)`) therefore returns true for
`Objects.equals(Integer 1, Integer 2)`. Generated COBOL: FSJJHM02 paragraph M0276-JO-EQUALS.

Not affected:
- `x.equals(y)` with a value receiver goes through Intrinsics.valueEquals.
- Records use the record equals intrinsic.
- `==` with a value-class static type is rejected at build time.

## Reach (all three J3S estates SAFE today)
- No application source line compares two Object-typed operands with `==`. Every site is in the JRE emulation:
  9 (Fineract), 7 (kontor-zins), 13 (joda-money).
- Fineract: one application-level boxed `Objects.equals`, EqualAmortizationValues.java:50
  (numberOfInstallments). Both operands are `repaymentPeriods.size()` of the same list, so they are always equal, and
  Java gives the same result.
- The HashMap/ConcurrentHashMap `k == key` checks are guarded by equal hashes, and their keys are Integer (or 3-digit
  currency codes), whose hash is one-to-one.
- The list/Optional/identity-map comparisons operate on heap objects, where comparing the class id and pool index is
  correct.
- Inferred, not verified: dispatch-only reachable sites never see value operands, and Lombok equals uses `.equals`.

## Latent risk
Any future code that passes values through these idioms gets a silently wrong answer. Examples: `List<BigDecimal>.equals`,
`Optional<LocalDate>.equals`, `Objects.equals` on BigDecimal or LocalDate. Hash-guarded lookups are correct only while
the key hash is one-to-one. That does not hold for Long (0L vs -1L) or BigDecimal keys; none is used today.

## Recommended fix (not done; the J3S owner decides)
1. Lower.java: for ANY operands, choose the comparison by class id: null, then heap (compare I), then value (compare V;
   enum ordinal). Never compare V for heap references.
2. A bare `==` on boxed values follows Java exactly where Java is deterministic: enums, Boolean, the Integer/Long/
   Short/Byte cache -128..127, Character 0..127. Otherwise trap (abend) instead of guessing.
3. Lower `Objects.equals` (`a == b || a.equals(b)`) as an intrinsic with exact value semantics, and have the emulations
   (JObjects, JOptional, JArrayList, JAbstractList, JHashMap, JHashSet, JConcurrentHashMap) use it.
4. Gates:
   - the Fineract regeneration diff may touch only these conditions; then j3s-gate.sh (build, reproduce, G1b, G1, G0,
     G2 mini and event G2, full event corpus), and preferably the full schedule G2 (115) and the bench;
   - J3S-T: kontor-zins, joda-money and loan-amortization-calculator G2 6/6 each;
   - new G0 cases: Objects.equals(1,2), BigDecimal 1.0 vs 1.00, different LocalDates, List/Optional equals with
     different values, RoundingMode in Object slots, boxed 127==127 and 128==128, Long keys 0L/-1L;
   - a mutant that restores the C/I-only comparison, which must be caught.
5. Docs: J3S.md section 3 (the census works on static types and misses Object-typed slots holding values) and section 17
   (remove "Found and left as they are").
