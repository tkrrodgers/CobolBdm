# Thinker names vs the held-out original (lead only)

Original: `C:/Users/Tomrodgers/JavaToCOBOLTest/claude-in-app/workspace/packages/manikouchakian-kontor-zins-b507fe6/source/src/main/java`. 5 original types, 55 vocabulary tokens.

| Unit kind | Units | Vocab hit, thinker | Vocab hit, generator (baseline) |
|---|---|---|---|
| data | 196 | 33% | 26% |
| increment | 5 | 0% | 0% |
| instance | 4 | 8% | 0% |
| method | 24 | 25% | 17% |
| predicate | 1 | 67% | 50% |
| record-instance | 10 | 15% | 10% |
| switch-validator | 1 | 33% | 100% |
| type | 30 | 14% | 4% |
| **all** | 271 | 29% | 21% |

## Concept recall (original identifiers whose words all appear in one name)

| Names | Recovered | Share |
|---|---|---|
| Thinker | 29/33 | 88% |
| Generator (baseline) | 18/33 | 55% |

Recovered by the thinker: Annuity, Installment, Rounding, Schedule, annualRate, installment, interest, isInterestFree, isInterestOnly, lastInstallment, money, month, monthlyPayment, monthlyRate, months, ofYears, payment, principal, rate, remainingDebt, remainingDebtAfter, requireAmount, sameAmount, schedule, terms, totalInterest, totalPaid, totalPrincipal, validate

Recovered by the generator: Installment, Rounding, annualRate, installment, interest, money, month, monthlyPayment, monthlyRate, months, ofYears, payment, principal, rate, requireAmount, sameAmount, terms, validate

Not recovered: LoanTerms, durationInMonths, installments, principalPart

Original types: Annuity, Installment, LoanTerms, Rounding, Schedule

| Generator type | Thinker type | Confidence | Vocab hit |
|---|---|---|---|
| AnnualRateValueRecord | InterestRateValue | 0.5 | 1.0 |
| CaseWorkRecord | LoanCase | 0.55 | 0.5 |
| Cee3abdCodeRecord | AbendCode | 0.6 | 0.0 |
| Cee3abdTimingRecord | AbendTiming | 0.6 | 0.0 |
| EventOutRecType | ResultRecordType | 0.7 | 0.0 |
| EventOutRecord | ResultRecord | 0.8 | 0.0 |
| EventRecord | CaseRecord | 0.8 | 0.0 |
| EventSequence | CaseSequence | 0.6 | 0.0 |
| FndkrndPowersRecord | RoundingPowerTable | 0.6 | 0.33 |
| Fsem0100 | AnnuityCalculationService | 0.85 | 0.33 |
| FsjbdecAreaMode | ResolvedRoundingMode | 0.5 | 0.33 |
| FsjbdecAreaRecord | PackedDecimalScratch | 0.45 | 0.0 |
| FsjbdecPowersRecord | DecimalPowerTable | 0.6 | 0.0 |
| FsjbdecRegsRecord | BigNumberRegisters | 0.55 | 0.0 |
| FsjbdecSaveRecord | SavedOperands | 0.55 | 0.0 |
| FsjbdecStoreRecord | BigNumberStore | 0.6 | 0.0 |
| FsjbdecWorkRecord | BigNumberScratch | 0.45 | 0.0 |
| JbOp | DecimalOperation | 0.75 | 0.0 |
| JbRecord | DecimalKernelParameters | 0.6 | 0.0 |
| Kzle0100Batch | AnnuityLoanPlanBatch | 0.75 | 0.5 |
| Kzle0100DateRecord | CalendarDate | 0.6 | 0.0 |
| OutFunc | ServiceFunction | 0.6 | 0.0 |
| OutRecord | ServiceResponse | 0.5 | 0.0 |
| OutStatus | ServiceStatus | 0.6 | 0.0 |
| ParmsFunction | RoundingFunction | 0.7 | 0.5 |
| ParmsMode | RoundingMode | 0.8 | 0.5 |
| ParmsRecord | RoundingKernelParameters | 0.65 | 0.33 |
| PaymentValueRecord | DecimalValue | 0.55 | 0.0 |
| PowerBaseRecord | PowerBaseValue | 0.45 | 0.0 |
| RecType | CaseRecordType | 0.7 | 0.0 |
