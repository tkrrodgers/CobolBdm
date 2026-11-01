# Distance to original: kontor-zins

Tool: jobol-audit compare 1.0.0. Original: `C:/Users/Tomrodgers/JavaToCOBOLTest/claude-in-app/workspace/packages/manikouchakian-kontor-zins-b507fe6/source/src/main/java`. Modern: `C:/Users/Tomrodgers/Source/CobolBdm/work/loop4/regress/kontor-zins-m5/src/main/java`. Scope: reached (J3S COBOL `C:/Users/Tomrodgers/Source/CobolBdm/projects/kontor-zins/source`).

## Summary

| Measure | Value |
|---|---|
| API preservation | 64.3% (27/42) |
| API preservation, reached by J3S | 79.4% (27/34) |
| Name fidelity | 91.8% (89/97) |
| Signature fidelity | 65.9% (27/41) |
| Signature fidelity, reached by J3S | 87.1% (27/31) |
| Original types in scope | 5 (0 top-level out of scope) |
| Matched | 5 (5 by name, 0 moved) |
| Original only | 0 |
| Modern only | 37 pipeline infrastructure, 0 Lombok expansion, 0 other |

API preservation counts the public and protected fields, methods, constructors and enum constants of the original's public types that are present in modern with the same name, types, static flag and visibility. Name fidelity is the share of aligned identifier pairs (fields, methods, parameters, locals) whose modern spelling is exact. Signature fidelity is the share of original methods and constructors present with the same name, parameter types, return type and static flag. "Reached" leaves out methods and types no J3S frame was generated for.

## Names

| Element | exact | case | truncated | abbreviated | accessor_style | truncated_numbered | numbered | renamed |
|---|---|---|---|---|---|---|---|---|
| field | 17 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| local | 19 | 0 | 4 | 0 | 0 | 0 | 0 | 0 |
| method | 27 | 0 | 0 | 0 | 4 | 0 | 0 | 0 |
| param | 26 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |

Length of the modern spelling of truncated names: 11:4

Completion of truncated names from identifiers the modern estate spells out elsewhere: none 3, unique-correct 1

## Structure (matched and original-only types against their modern counterparts)

| | Types | LOC | Methods | Avg method lines | Max method lines | Branches | Constructors | Factories | Builders | equals+hashCode | toString |
|---|---|---|---|---|---|---|---|---|---|---|---|
| original | 5 | 251 | 27 | 9.3 | 36 | 38 | 5 | 1 | 0 | 2 | 3 |
| modern | 5 | 285 | 31 | 8.0 | 28 | 39 | 3 | 1 | 0 | 0 | 0 |

## Idioms

"Original" counts the original members that have a modern counterpart (the comparable part); "original, all" also counts the members and types J3S never compiled.

| Idiom | Original, all | Original | Modern | Delta |
|---|---|---|---|---|
| bigdecimal | 66 | 66 | 78 | +12 |
| exceptionBare | 0 | 0 | 25 | +25 |
| exceptionWithArgs | 25 | 25 | 0 | -25 |
| list | 6 | 6 | 6 | 0 |
| loop | 3 | 3 | 3 | 0 |
| nullCheck | 13 | 13 | 13 | 0 |
| record | 2 | 2 | 0 | -2 |

Type uses (declarations, generics, `new`, static calls):

| Type | Original, all | Original | Modern |
|---|---|---|---|
| ArrayList | 1 | 1 | 1 |
| BigDecimal | 55 | 55 | 67 |
| List | 5 | 5 | 5 |
| MathContext | 4 | 4 | 4 |
| RoundingMode | 7 | 7 | 7 |

## Top 5 types by distance

Distance is a sum of penalties (weights in COMPARE-GAPS.md: kind 5, lost supertype 3, missing API method 3, other reached missing method 2, changed signature 2, missing field 2, extra method 1, member name 1, parameter/local name 0.25, ...). Methods J3S never compiled are not penalised.

| Type | Distance | Per member | Largest contributions |
|---|---|---|---|
| `de.manikouchakian.kontorzins.model.LoanTerms` | 11.5 | 0.77 | kind 5.0, idioms 4.0, missing fields 2.0 |
| `de.manikouchakian.kontorzins.schedule.Schedule` | 8.8 | 0.51 | names 4.8, idioms 3.0, modifiers 1.0 |
| `de.manikouchakian.kontorzins.model.Installment` | 7.5 | 0.47 | kind 5.0, idioms 2.5 |
| `de.manikouchakian.kontorzins.model.Rounding` | 6.0 | 0.75 | missing fields 4.0, idioms 1.0, modifiers 1.0 |
| `de.manikouchakian.kontorzins.Annuity` | 4.8 | 0.95 | idioms 3.0, modifiers 1.0, field flags 0.5 |

## Gaps by pattern

| Pattern | Count | Examples |
|---|---|---|
| FIELD.MISSING_CONSTANT | 3 | `LoanTerms` MAX_MONTHS: static final int MAX_MONTHS → - (source)<br>`Rounding` MONEY_SCALE: static final int MONEY_SCALE → - (source)<br>`Rounding` RATE_SCALE: static final int RATE_SCALE → - (source) |
| IDIOM.EXCEPTION_MESSAGE_LOST | 5 | `Annuity`: with message 6 → with message 0, bare 6<br>`Installment`: with message 4 → with message 0, bare 4<br>`LoanTerms`: with message 7 → with message 0, bare 7 |
| IDIOM.RECORD_LOST | 2 | `Installment`: 1 → 0 (-1)<br>`LoanTerms`: 1 → 0 (-1) |
| MEMBER.VISIBILITY_WIDENED_STATIC_FIELD | 2 | `Annuity` MC: private → public (field)<br>`LoanTerms` MONTHS_PER_YEAR: private → public (field) |
| METHOD.UNREACHED_API | 1 | `Schedule` terms(): LoanTerms terms() → - (source public) |
| METHOD.UNREACHED_CTOR | 2 | `Annuity` &lt;init&gt;(): &lt;init&gt;() → - (source private)<br>`Rounding` &lt;init&gt;(): &lt;init&gt;() → - (source private) |
| METHOD.UNREACHED_OBJECT | 1 | `Schedule` toString(): String toString() → - (source public) |
| METHOD.UNREACHED_RECORD | 6 | `Installment` equals(Object): boolean equals(Object) → - (record public)<br>`Installment` hashCode(): int hashCode() → - (record public)<br>`Installment` toString(): String toString() → - (record public) |
| NAME.ACCESSOR_STYLE | 4 | `Schedule` installments(): installments → getInstallments (method)<br>`Schedule` totalInterest(): totalInterest → getTotalInterest (method)<br>`Schedule` totalPaid(): totalPaid → getTotalPaid (method) |
| NAME.TRUNCATED.NONE | 3 | `Annuity` schedule(LoanTerms): actualPayment → actualPayme (local, completion none)<br>`Schedule` validate(): expectedDebt → expectedDeb (local, completion none)<br>`Schedule` validate(): expectedMonth → expectedMon (local, completion none) |
| NAME.TRUNCATED.UNIQUE_CORRECT | 1 | `Schedule` validate(): totalPrincipal → totalPrinci (local, completion unique-correct) |
| TYPE.FINAL_LOST | 3 | `Annuity`: final → -<br>`Rounding`: final → -<br>`Schedule`: final → - |
| TYPE.KIND_RECORD_TO_CLASS | 2 | `Installment`: record → class<br>`LoanTerms`: record → class |

## Matched types

| Original | Modern | Kind | API | Signatures | Fields m/x | Methods m/x | Distance |
|---|---|---|---|---|---|---|---|
| `Annuity` | same | class | 100.0% | 75.0% | 0/0 | 1/0 | 4.8 |
| `Installment` | same | record → class | 70.0% | 72.7% | 0/0 | 3/0 | 7.5 |
| `LoanTerms` | same | record → class | 63.6% | 70.0% | 1/0 | 3/0 | 11.5 |
| `Rounding` | same | class | 71.4% | 75.0% | 2/0 | 1/0 | 6.0 |
| `Schedule` | same | class | 45.5% | 50.0% | 0/0 | 2/0 | 8.8 |

## Modern-only types

37 pipeline infrastructure types (the generated job, platform, record and strangler layers; not gaps). By rule: glob:**/batch/** 6, lane:compat 31.

