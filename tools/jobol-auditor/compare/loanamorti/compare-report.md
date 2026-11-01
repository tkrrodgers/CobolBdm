# Distance to original: loanamorti

Tool: jobol-audit compare 1.0.0. Original: `C:/Users/Tomrodgers/JavaToCOBOLTest/claude-in-app/workspace/packages/artyompanfutov-loan-amortization-calculator-74c5d4f/source/src/main/java`. Modern: `C:/Users/Tomrodgers/Source/CobolBdm/work/loop4/regress/loanamorti-m5/src/main/java`. Scope: reached (J3S COBOL `C:/Users/Tomrodgers/Source/CobolBdm/projects/loanamorti-74c5d4f-065118/source`).

## Summary

| Measure | Value |
|---|---|
| API preservation | 74.5% (70/94) |
| API preservation, reached by J3S | 90.9% (70/77) |
| Name fidelity | 90.8% (217/239) |
| Signature fidelity | 77.8% (77/99) |
| Signature fidelity, reached by J3S | 96.3% (77/80) |
| Original types in scope | 18 (2 top-level out of scope) |
| Matched | 17 (17 by name, 0 moved) |
| Original only | 1 |
| Modern only | 37 pipeline infrastructure, 0 Lombok expansion, 0 other |

API preservation counts the public and protected fields, methods, constructors and enum constants of the original's public types that are present in modern with the same name, types, static flag and visibility. Name fidelity is the share of aligned identifier pairs (fields, methods, parameters, locals) whose modern spelling is exact. Signature fidelity is the share of original methods and constructors present with the same name, parameter types, return type and static flag. "Reached" leaves out methods and types no J3S frame was generated for.

## Names

| Element | exact | case | truncated | abbreviated | accessor_style | truncated_numbered | numbered | renamed |
|---|---|---|---|---|---|---|---|---|
| constant | 8 | 0 | 4 | 0 | 0 | 0 | 0 | 0 |
| field | 41 | 0 | 1 | 0 | 0 | 0 | 0 | 0 |
| local | 27 | 0 | 4 | 0 | 0 | 2 | 0 | 0 |
| method | 79 | 0 | 0 | 0 | 1 | 0 | 0 | 0 |
| param | 62 | 0 | 9 | 0 | 0 | 0 | 0 | 1 |

Length of the modern spelling of truncated names: 10:4, 11:8, 12:1, 21:2, 23:2, 24:1

Completion of truncated names from identifiers the modern estate spells out elsewhere: ambiguous-wrong 1, none 15, unique-correct 3, unique-wrong 1

## Structure (matched and original-only types against their modern counterparts)

| | Types | LOC | Methods | Avg method lines | Max method lines | Branches | Constructors | Factories | Builders | equals+hashCode | toString |
|---|---|---|---|---|---|---|---|---|---|---|---|
| original | 18 | 745 | 99 | 6.7 | 100 | 63 | 15 | 0 | 4 | 4 | 7 |
| modern | 17 | 518 | 82 | 5.2 | 52 | 67 | 15 | 0 | 3 | 4 | 4 |

## Idioms

"Original" counts the original members that have a modern counterpart (the comparable part); "original, all" also counts the members and types J3S never compiled.

| Idiom | Original, all | Original | Modern | Delta |
|---|---|---|---|---|
| bigdecimal | 113 | 99 | 91 | -8 |
| collection | 3 | 3 | 3 | 0 |
| enum | 5 | 5 | 5 | 0 |
| exceptionBare | 0 | 0 | 1 | +1 |
| exceptionWithArgs | 8 | 6 | 6 | 0 |
| lambda | 3 | 3 | 1 | -2 |
| list | 8 | 7 | 6 | -1 |
| loop | 4 | 4 | 6 | +2 |
| map | 33 | 28 | 32 | +4 |
| methodRef | 2 | 2 | 0 | -2 |
| nullCheck | 21 | 19 | 20 | +1 |
| optional | 0 | 0 | 2 | +2 |
| record | 0 | 0 | 4 | +4 |
| set | 1 | 1 | 2 | +1 |
| stream | 3 | 3 | 0 | -3 |
| time | 18 | 16 | 14 | -2 |

Type uses (declarations, generics, `new`, static calls):

| Type | Original, all | Original | Modern |
|---|---|---|---|
| ArrayList | 1 | 1 | 1 |
| BigDecimal | 106 | 92 | 84 |
| Collections | 3 | 3 | 3 |
| Collectors | 1 | 1 | 0 |
| HashMap | 4 | 2 | 3 |
| List | 7 | 6 | 5 |
| LocalDate | 18 | 16 | 14 |
| Map | 29 | 26 | 29 |
| Optional | 0 | 0 | 2 |
| RoundingMode | 7 | 7 | 7 |
| Set | 1 | 1 | 2 |

## Top 17 types by distance

Distance is a sum of penalties (weights in COMPARE-GAPS.md: kind 5, lost supertype 3, missing API method 3, other reached missing method 2, changed signature 2, missing field 2, extra method 1, member name 1, parameter/local name 0.25, ...). Methods J3S never compiled are not penalised.

| Type | Distance | Per member | Largest contributions |
|---|---|---|---|
| `paqua.loan.amortization.dto.MonthlyPayment` | 17.0 | 0.85 | extra methods 7.0, kind 5.0, supertypes 3.0 |
| `paqua.loan.amortization.dto.Loan` | 15.0 | 0.94 | extra methods 5.0, kind 5.0, supertypes 3.0 |
| `paqua.loan.amortization.api.impl.LoanAmortizationCalculatorImpl` | 14.8 | 2.46 | idioms 3.5, supertypes 3.0, member flags 2.0 |
| `paqua.loan.amortization.dto.EarlyPayment` | 14.3 | 1.02 | kind 5.0, extra methods 4.0, supertypes 3.0 |
| `paqua.loan.amortization.dto.LoanAmortization` | 14.0 | 1.0 | kind 5.0, extra methods 3.0, supertypes 3.0 |
| `paqua.loan.amortization.api.impl.annual.AnnualPaymentLoanCalculator` | 9.3 | 1.03 | supertypes 3.0, names 2.8, visibility 2.0 |
| `paqua.loan.amortization.api.impl.repeating.EarlyPaymentRepeatingStrategy` | 7.0 | 1.17 | supertypes 3.0, member flags 2.0, extra methods 1.5 |
| `paqua.loan.amortization.dto.EarlyPaymentAdditionalParameters` | 3.5 | 3.5 | supertypes 3.0, extra methods 0.5 |
| `paqua.loan.amortization.api.impl.LoanAmortizationCalculatorFactory` | 3.0 | 1.5 | signatures 2.0, modifiers 1.0 |
| `paqua.loan.amortization.api.impl.annual.AnnualPaymentLoanCalculatorFactory` | 3.0 | 1.5 | signatures 2.0, modifiers 1.0 |
| `paqua.loan.amortization.api.impl.message.Messages` | 3.0 | 0.38 | names 3.0 |
| `paqua.loan.amortization.exception.LoanAmortizationCalculatorException` | 2.0 | 0.5 | missing fields 2.0 |
| `paqua.loan.amortization.dto.MonthlyPayment.MonthlyPaymentBuilder` | 1.5 | 0.09 | modifiers 1.0, names 0.5 |
| `paqua.loan.amortization.exception.ExceptionType` | 1.5 | 1.5 | names 1.0, extra methods 0.5 |
| `paqua.loan.amortization.dto.LoanAmortization.LoanAmortizationBuilder` | 1.3 | 0.1 | modifiers 1.0, names 0.3 |
| `paqua.loan.amortization.dto.Loan.LoanBuilder` | 1.0 | 0.06 | modifiers 1.0 |
| `paqua.loan.amortization.dto.EarlyPaymentStrategy` | 0.5 | 0.25 | extra methods 0.5 |

## Gaps by pattern

| Pattern | Count | Examples |
|---|---|---|
| BUILDER.UNREACHED | 1 | `EarlyPayment`: explicit → none |
| FIELD.MISSING_SERIAL_VERSION | 5 | `EarlyPayment` serialVersionUID: static final long serialVersionUID → - (source)<br>`Loan` serialVersionUID: static final long serialVersionUID → - (source)<br>`LoanAmortization` serialVersionUID: static final long serialVersionUID → - (source) |
| IDIOM.LAMBDA_LOST | 1 | `LoanAmortizationCalculatorImpl`: 3 → 1 (-2) |
| IDIOM.METHODREF_LOST | 1 | `LoanAmortizationCalculatorImpl`: 2 → 0 (-2) |
| IDIOM.STREAM_LOST | 1 | `LoanAmortizationCalculatorImpl`: 3 → 0 (-3) |
| IDIOM.STREAM_TO_LOOP | 1 | `LoanAmortizationCalculatorImpl`: streams+lambdas+method refs -7 → loops +2 |
| MEMBER.VISIBILITY_WIDENED_METHOD | 4 | `LoanAmortizationCalculatorImpl` extractOnlySingleEarlyPayments(Loan): private → public (method)<br>`LoanAmortizationCalculatorImpl` validate(Loan): private → public (method)<br>`EarlyPaymentRepeatingStrategy` log(EarlyPayment,EarlyPaymentRepeatingStrategy): package → public (method) |
| MEMBER.VISIBILITY_WIDENED_STATIC_FIELD | 4 | `LoanAmortizationCalculatorImpl` ANNUAL_PAYMENT_LOAN_AMORTIZATION_CALCULATOR: private → public (field)<br>`LoanAmortizationCalculatorImpl` LOGGER: private → public (field)<br>`AnnualPaymentLoanCalculator` LOGGER: private → public (field) |
| METHOD.EXTRA_API | 1 | `EarlyPaymentRepeatingStrategy` getRepeated(Loan,int,EarlyPayment): - → Map&lt;Integer,EarlyPayment&gt; getRepeated(Loan,int,EarlyPayment) (source public) |
| METHOD.EXTRA_CTOR | 6 | `LoanAmortizationCalculatorImpl` &lt;init&gt;(): - → &lt;init&gt;() (source public)<br>`AnnualPaymentLoanCalculator` &lt;init&gt;(): - → &lt;init&gt;() (source public)<br>`EarlyPaymentRepeatingStrategy` &lt;init&gt;(): - → &lt;init&gt;() (source private) |
| METHOD.EXTRA_RECORD | 19 | `EarlyPayment` additionalParameters(): - → Map&lt;EarlyPaymentAdditionalParameters,String&gt; additionalParameters() (record public)<br>`EarlyPayment` amount(): - → BigDecimal amount() (record public)<br>`EarlyPayment` repeatingStrategy(): - → EarlyPaymentRepeatingStrategy repeatingStrategy() (record public) |
| METHOD.UNREACHED_API | 4 | `EarlyPayment` builder(): EarlyPaymentBuilder builder() → - (source public)<br>`Loan.LoanBuilder` amount(double): LoanBuilder amount(double) → - (source public)<br>`Loan.LoanBuilder` earlyPayment(int,EarlyPayment): LoanBuilder earlyPayment(int,EarlyPayment) → - (source public) |
| METHOD.UNREACHED_CTOR | 5 | `LoanAmortizationCalculatorFactory` &lt;init&gt;(): &lt;init&gt;() → - (source private)<br>`AnnualPaymentLoanCalculatorFactory` &lt;init&gt;(): &lt;init&gt;() → - (source private)<br>`Loan.LoanBuilder` &lt;init&gt;(BigDecimal,BigDecimal,Integer,Map,LocalDate): &lt;init&gt;(BigDecimal,BigDecimal,Integer,Map&lt;Integer,EarlyPayment&gt;,LocalDate) → - (source public) |
| METHOD.UNREACHED_OBJECT | 2 | `LoanAmortization.LoanAmortizationBuilder` toString(): String toString() → - (source public)<br>`LoanAmortizationCalculatorException` toString(): String toString() → - (source public) |
| NAME.ACCESSOR_STYLE | 1 | `LoanAmortization` getEarlyPayments(): getEarlyPayments → earlyPayments (method) |
| NAME.RENAMED | 1 | `EarlyPayment` &lt;init&gt;(BigDecimal,EarlyPaymentStrategy,EarlyPaymentRepeatingStrategy,Map): type → strategy (param) |
| NAME.TRUNCATED.NONE | 15 | `LoanAmortizationCalculatorImpl` ANNUAL_PAYMENT_LOAN_AMORTIZATION_CALCULATOR: ANNUAL_PAYMENT_LOAN_AMORTIZATION_CALCULATOR → ANNUAL_PAYMENT_LOAN_A (field, completion none)<br>`LoanAmortizationCalculatorImpl` getLoanWithImplementedEarlyPaymentStrategy(Loan): allEarlyPayments → allEarlyPa (local, completion none)<br>`AnnualPaymentLoanCalculator` calculate(Loan): amortizationBuilder → amortization (local, completion none) |
| NAME.TRUNCATED.UNIQUE_CORRECT | 3 | `LoanAmortization.LoanAmortizationBuilder` overPaymentAmount(BigDecimal): overPaymentAmount → overPayment (param, completion unique-correct)<br>`MonthlyPayment.MonthlyPaymentBuilder` debtPaymentAmount(BigDecimal): debtPaymentAmount → debtPayment (param, completion unique-correct)<br>`MonthlyPayment.MonthlyPaymentBuilder` loanBalanceAmount(BigDecimal): loanBalanceAmount → loanBalance (param, completion unique-correct) |
| NAME.TRUNCATED_NUMBERED.AMBIGUOUS_WRONG | 1 | `AnnualPaymentLoanCalculator` calculate(Loan): additionalPaymentsWithRemainingLoanBalance → additionalP2 (local, completion ambiguous-wrong) |
| NAME.TRUNCATED_NUMBERED.UNIQUE_WRONG | 1 | `AnnualPaymentLoanCalculator` calculate(Loan): lastPaymentNumber → lastPayment2 (local, completion unique-wrong) |
| SUPERTYPE.INTERFACE_LOST | 3 | `LoanAmortizationCalculatorImpl`: LoanAmortizationCalculator → -<br>`AnnualPaymentLoanCalculator`: LoanAmortizationCalculator → -<br>`EarlyPaymentRepeatingStrategy`: RepeatableEarlyPayment → - |
| SUPERTYPE.JDK_INTERFACE_LOST | 5 | `EarlyPayment`: Serializable → -<br>`EarlyPaymentAdditionalParameters`: Serializable → -<br>`Loan`: Serializable → - |
| TYPE.FINAL_LOST | 5 | `LoanAmortizationCalculatorFactory`: final → -<br>`AnnualPaymentLoanCalculatorFactory`: final → -<br>`Loan.LoanBuilder`: final static → static |
| TYPE.KIND_CLASS_TO_RECORD | 4 | `EarlyPayment`: class → record<br>`Loan`: class → record<br>`LoanAmortization`: class → record |
| TYPE.UNREACHED_CLASS | 1 | `EarlyPayment.EarlyPaymentBuilder`: paqua.loan.amortization.dto.EarlyPayment.EarlyPaymentBuilder → - (nested in EarlyPayment, 12 members) |
| TYPE.VISIBILITY_WIDENED | 2 | `LoanAmortizationCalculatorImpl`: package → public<br>`AnnualPaymentLoanCalculator`: package → public |
| TYPEREF.INTERFACE_TO_IMPL | 3 | `LoanAmortizationCalculatorFactory` create(): LoanAmortizationCalculator → LoanAmortizationCalculatorImpl (return)<br>`LoanAmortizationCalculatorImpl` ANNUAL_PAYMENT_LOAN_AMORTIZATION_CALCULATOR: LoanAmortizationCalculator → AnnualPaymentLoanCalculator (field)<br>`AnnualPaymentLoanCalculatorFactory` create(): LoanAmortizationCalculator → AnnualPaymentLoanCalculator (return) |

## Matched types

| Original | Modern | Kind | API | Signatures | Fields m/x | Methods m/x | Distance |
|---|---|---|---|---|---|---|---|
| `LoanAmortizationCalculatorFactory` | same | class | 0.0% | 0.0% | 0/0 | 1/0 | 3.0 |
| `LoanAmortizationCalculatorImpl` | same | class | - | 100.0% | 0/0 | 0/1 | 14.8 |
| `AnnualPaymentLoanCalculator` | same | class | - | 100.0% | 0/0 | 0/1 | 9.3 |
| `AnnualPaymentLoanCalculatorFactory` | same | class | 0.0% | 0.0% | 0/0 | 1/0 | 3.0 |
| `Messages` | same | enum | 50.0% | 100.0% | 0/0 | 0/0 | 3.0 |
| `EarlyPaymentRepeatingStrategy` | same | enum | 100.0% | 100.0% | 0/0 | 0/2 | 7.0 |
| `EarlyPayment` | same | class → record | 88.9% | 88.9% | 1/0 | 1/4 | 14.3 |
| `EarlyPaymentAdditionalParameters` | same | enum | 100.0% | 100.0% | 0/0 | 0/1 | 3.5 |
| `EarlyPaymentStrategy` | same | enum | 100.0% | 100.0% | 0/0 | 0/1 | 0.5 |
| `Loan` | same | class → record | 100.0% | 100.0% | 1/0 | 0/5 | 15.0 |
| `Loan.LoanBuilder` | same | class | 63.6% | 63.6% | 0/0 | 4/0 | 1.0 |
| `LoanAmortization` | same | class → record | 88.9% | 88.9% | 1/0 | 0/3 | 14.0 |
| `LoanAmortization.LoanAmortizationBuilder` | same | class | 75.0% | 75.0% | 0/0 | 2/0 | 1.3 |
| `MonthlyPayment` | same | class → record | 100.0% | 100.0% | 1/0 | 0/7 | 17.0 |
| `MonthlyPayment.MonthlyPaymentBuilder` | same | class | 90.0% | 90.0% | 0/0 | 1/0 | 1.5 |
| `ExceptionType` | same | enum | 0.0% | 100.0% | 0/0 | 0/1 | 1.5 |
| `LoanAmortizationCalculatorException` | same | class | 50.0% | 50.0% | 1/0 | 1/0 | 2.0 |

## Original-only types

| Type | Kind | Reached by J3S | API members | Members |
|---|---|---|---|---|
| `paqua.loan.amortization.dto.EarlyPayment.EarlyPaymentBuilder` | class | yes | 8 | 12 |

## Modern-only types

37 pipeline infrastructure types (the generated job, platform, record and strangler layers; not gaps). By rule: glob:**/batch/** 6, lane:compat 31.

## Out of scope (not compiled by J3S, no modern counterpart)

- `paqua.loan.amortization.api.LoanAmortizationCalculator`
- `paqua.loan.amortization.api.impl.repeating.RepeatableEarlyPayment`

