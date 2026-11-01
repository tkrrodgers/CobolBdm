# Distance to original: fineract-xl

Tool: jobol-audit compare 1.0.0. Original: 28 source roots. Modern: `C:/Users/Tomrodgers/Source/CobolBdm/work/loop4/regress/fineract-xl-m5/src/main/java`. Scope: reached (J3S COBOL `C:/Users/Tomrodgers/Source/CobolBdm/projects/fineract-xl/source`).

- 5396 original files outside the J3S scope were not parsed (not listed)
- Lombok: the original side is an approximate delomboked view (getters, setters, constructors, builder(), equals/hashCode/toString added as members with origin 'lombok'); no delombok run was available

## Summary

| Measure | Value |
|---|---|
| API preservation | 43.4% (568/1309) |
| API preservation, reached by J3S | 83.7% (568/679) |
| Name fidelity | 72.8% (2066/2836) |
| Signature fidelity | 47.6% (694/1457) |
| Signature fidelity, reached by J3S | 90.8% (694/764) |
| Original types in scope | 67 (5396 top-level out of scope) |
| Matched | 67 (65 by name, 2 moved) |
| Original only | 0 |
| Modern only | 50 pipeline infrastructure, 2 Lombok expansion, 0 other |

API preservation counts the public and protected fields, methods, constructors and enum constants of the original's public types that are present in modern with the same name, types, static flag and visibility. Name fidelity is the share of aligned identifier pairs (fields, methods, parameters, locals) whose modern spelling is exact. Signature fidelity is the share of original methods and constructors present with the same name, parameter types, return type and static flag. "Reached" leaves out methods and types no J3S frame was generated for.

## Names

| Element | exact | case | truncated | abbreviated | accessor_style | truncated_numbered | numbered | renamed |
|---|---|---|---|---|---|---|---|---|
| constant | 45 | 0 | 1 | 7 | 0 | 0 | 0 | 0 |
| field | 340 | 5 | 94 | 6 | 0 | 1 | 0 | 0 |
| local | 213 | 7 | 213 | 2 | 0 | 30 | 2 | 6 |
| method | 736 | 0 | 21 | 1 | 6 | 0 | 0 | 0 |
| param | 732 | 0 | 345 | 2 | 0 | 18 | 3 | 0 |

Length of the modern spelling of truncated names: 4:1, 9:2, 10:112, 11:232, 12:14, 13:6, 14:27, 15:65, 16:42, 17:5, 18:29, 19:33, 20:32, 21:10, 22:12, 23:11, 24:19, 25:9, 26:3, 27:4, 28:1, 29:1, 30:2, 31:1, 33:1

Completion of truncated names from identifiers the modern estate spells out elsewhere: ambiguous-correct 97, ambiguous-wrong 71, none 332, unique-correct 104, unique-wrong 119

## Structure (matched and original-only types against their modern counterparts)

| | Types | LOC | Methods | Avg method lines | Max method lines | Branches | Constructors | Factories | Builders | equals+hashCode | toString |
|---|---|---|---|---|---|---|---|---|---|---|---|
| original | 67 | 8566 | 1100 | 7.5 | 146 | 999 | 67 | 61 | 5 | 16 | 18 |
| modern | 67 | 6797 | 757 | 8.2 | 128 | 1159 | 59 | 33 | 3 | 16 | 12 |

## Idioms

"Original" counts the original members that have a modern counterpart (the comparable part); "original, all" also counts the members and types J3S never compiled.

| Idiom | Original, all | Original | Modern | Delta |
|---|---|---|---|---|
| bigdecimal | 735 | 450 | 604 | +154 |
| collection | 15 | 8 | 20 | +12 |
| enum | 15 | 15 | 15 | 0 |
| exceptionBare | 1 | 1 | 25 | +24 |
| exceptionWithArgs | 30 | 20 | 15 | -5 |
| functional | 25 | 17 | 19 | +2 |
| interface | 6 | 6 | 6 | 0 |
| lambda | 171 | 153 | 88 | -65 |
| list | 146 | 103 | 158 | +55 |
| loop | 77 | 40 | 111 | +71 |
| map | 54 | 32 | 35 | +3 |
| methodRef | 68 | 56 | 0 | -56 |
| nullCheck | 250 | 124 | 480 | +356 |
| optional | 62 | 39 | 80 | +41 |
| patternMatch | 5 | 4 | 0 | -4 |
| record | 6 | 6 | 12 | +6 |
| returnNull | 23 | 8 | 11 | +3 |
| set | 30 | 21 | 22 | +1 |
| stream | 94 | 79 | 0 | -79 |
| switchExpr | 18 | 10 | 0 | -10 |
| time | 775 | 373 | 432 | +59 |
| var | 2 | 2 | 0 | -2 |

Type uses (declarations, generics, `new`, static calls):

| Type | Original, all | Original | Modern |
|---|---|---|---|
| ArrayList | 21 | 17 | 31 |
| BiConsumer | 1 | 1 | 1 |
| BiFunction | 1 | 1 | 1 |
| BigDecimal | 596 | 352 | 480 |
| ChronoField | 12 | 4 | 0 |
| ChronoUnit | 40 | 12 | 14 |
| Collection | 8 | 4 | 0 |
| Collections | 1 | 1 | 15 |
| Comparator | 3 | 1 | 1 |
| Consumer | 1 | 1 | 1 |
| DateTimeFormatter | 16 | 4 | 0 |
| Function | 6 | 6 | 8 |
| HashMap | 9 | 7 | 7 |
| HashSet | 4 | 4 | 4 |
| IdentityHashMap | 1 | 1 | 1 |
| IntStream | 1 | 1 | 0 |
| Iterable | 2 | 0 | 0 |
| Iterator | 4 | 3 | 5 |
| List | 125 | 86 | 127 |
| LocalDate | 609 | 346 | 416 |
| LocalDateTime | 42 | 0 | 0 |
| LocalTime | 2 | 0 | 0 |
| Map | 40 | 22 | 25 |
| MathContext | 139 | 98 | 124 |
| NavigableSet | 0 | 0 | 5 |
| OffsetDateTime | 35 | 0 | 0 |
| Optional | 62 | 39 | 80 |
| Predicate | 4 | 1 | 1 |
| Set | 22 | 13 | 11 |
| Supplier | 9 | 6 | 6 |
| Temporal | 3 | 3 | 0 |
| TemporalAdjusters | 1 | 1 | 0 |
| TreeMap | 4 | 2 | 2 |
| TreeSet | 4 | 4 | 2 |
| Year | 2 | 2 | 2 |
| YearMonth | 2 | 1 | 0 |
| ZoneId | 6 | 0 | 0 |
| ZoneOffset | 5 | 0 | 0 |

## Top 20 types by distance

Distance is a sum of penalties (weights in COMPARE-GAPS.md: kind 5, lost supertype 3, missing API method 3, other reached missing method 2, changed signature 2, missing field 2, extra method 1, member name 1, parameter/local name 0.25, ...). Methods J3S never compiled are not penalised.

| Type | Distance | Per member | Largest contributions |
|---|---|---|---|
| `org.apache.fineract.portfolio.loanproduct.calc.ProgressiveEMICalculator` | 175.3 | 1.51 | names 81.3, idioms 38.0, member flags 37.0 |
| `org.apache.fineract.portfolio.loanaccount.loanschedule.domain.LoanApplicationTerms` | 121.8 | 0.48 | names 54.8, field types 36.0, field flags 20.0 |
| `org.apache.fineract.portfolio.loanproduct.calc.data.ProgressiveLoanInterestScheduleModel` | 63.3 | 0.87 | idioms 24.5, names 16.8, member flags 9.0 |
| `org.apache.fineract.portfolio.loanaccount.loanschedule.domain.ProgressiveLoanScheduleGenerator` | 57.3 | 2.2 | signatures 28.0, names 14.3, member flags 6.0 |
| `org.apache.fineract.portfolio.loanproduct.calc.data.RepaymentPeriod` | 36.8 | 0.33 | idioms 12.0, names 8.8, member flags 6.0 |
| `org.apache.fineract.portfolio.loanaccount.loanschedule.data.LoanScheduleParams` | 31.3 | 0.34 | names 16.3, field types 8.0, field flags 3.0 |
| `org.apache.fineract.portfolio.loanproduct.data.LoanConfigurationDetails` | 26.5 | 0.53 | names 22.5, extra methods 4.0 |
| `org.apache.fineract.organisation.monetary.domain.Money` | 24.0 | 0.3 | member flags 13.0, names 5.0, signatures 4.0 |
| `org.apache.fineract.portfolio.loanaccount.loanschedule.domain.DefaultScheduledDateGenerator` | 21.5 | 1.26 | signatures 12.0, names 6.5, member flags 2.0 |
| `org.apache.fineract.portfolio.loanaccount.loanschedule.domain.LoanApplicationTerms.Builder` | 20.8 | 0.28 | names 7.8, field types 7.0, member flags 5.0 |
| `org.apache.fineract.portfolio.loanaccount.loanschedule.data.LoanSchedulePlan` | 20.3 | 0.78 | extra methods 10.0, kind 5.0, names 2.8 |
| `org.apache.fineract.portfolio.util.Memo` | 17.5 | 1.35 | signatures 8.0, generics 3.0, field types 2.0 |
| `org.apache.fineract.portfolio.loanaccount.loanschedule.data.LoanSchedulePlanRepaymentPeriod` | 14.0 | 0.5 | extra methods 7.0, kind 5.0, signatures 2.0 |
| `org.apache.fineract.portfolio.loanaccount.loanschedule.domain.LoanRepaymentScheduleModelData` | 13.5 | 0.32 | names 8.5, extra methods 5.0 |
| `org.apache.fineract.portfolio.loanaccount.loanschedule.data.LoanSchedulePlanDownPaymentPeriod` | 13.3 | 0.6 | kind 5.0, extra methods 4.0, names 2.3 |
| `org.apache.fineract.infrastructure.core.service.DateUtils` | 12.0 | 0.14 | missing fields 4.0, field flags 2.0, field types 2.0 |
| `org.apache.fineract.organisation.monetary.data.CurrencyData` | 10.0 | 0.29 | field flags 3.5, supertypes 3.0, missing fields 2.0 |
| `org.apache.fineract.infrastructure.core.service.MathUtil` | 9.8 | 0.12 | member flags 4.0, signatures 4.0, modifiers 1.0 |
| `org.apache.fineract.portfolio.loanproduct.calc.data.InterestPeriod` | 9.8 | 0.17 | names 5.3, member flags 3.0, idioms 1.0 |
| `org.apache.fineract.portfolio.loanproduct.calc.data.PeriodDueDetails` | 9.0 | 0.82 | kind 5.0, extra methods 3.0, member flags 1.0 |

## Gaps by pattern

| Pattern | Count | Examples |
|---|---|---|
| BUILDER.UNREACHED | 2 | `CurrencyData`: lombok @Builder → none (Builder,Data,NoArgsConstructor,AllArgsConstructor)<br>`ProcessedTransactionData`: lombok @Builder → none (Getter,Builder) |
| FIELD.FINAL_ADDED | 44 | `MonetaryCurrency` code<br>`MonetaryCurrency` digitsAfterDecimal<br>`MonetaryCurrency` inMultiplesOf |
| FIELD.FINAL_ADDED_SETTER_UNREACHED | 20 | `CurrencyData` code<br>`CurrencyData` decimalPlaces<br>`CurrencyData` displayLabel |
| FIELD.FINAL_LOST | 2 | `DateUtils` DEFAULT_DATETIME_FORMAT<br>`DateUtils` DEFAULT_DATE_FORMATTER |
| FIELD.MISSING | 3 | `ProcessedTransactionData` reAgeParameter: final LoanReAgeParameterData reAgeParameter → - (source)<br>`ProcessedTransactionData` transactionDate: final LocalDate transactionDate → - (source)<br>`ProcessedTransactionData` transactionType: final LoanTransactionType transactionType → - (source) |
| FIELD.MISSING_CONSTANT | 3 | `DateUtils` DEFAULT_DATETIME_FORMATTER: static final DateTimeFormatter DEFAULT_DATETIME_FORMATTER → - (source)<br>`DateUtils` DEFAULT_DATE_FORMAT: static final String DEFAULT_DATE_FORMAT → - (source)<br>`ProgressiveLoanInterestScheduleModel` modelVersion: static final String modelVersion → - (source) |
| FIELD.MISSING_SERIAL_VERSION | 1 | `CurrencyData` serialVersionUID: static final long serialVersionUID → - (source) |
| GENERICS.TYPE_PARAMETERS_LOST | 4 | `ProgressiveEMICalculator` generateInterestScheduleModel(List,Function,Function,ILoanConfigurationDetails,Integer,MathContext): &lt;T&gt; → - (method)<br>`ProgressiveEMICalculator` updateModel(ProgressiveLoanInterestScheduleModel,List,Function,Function): &lt;T&gt; → - (method)<br>`Memo`: &lt;T&gt; → - (type) |
| IDIOM.EXCEPTION_MESSAGE_LOST | 5 | `DateUtils`: with message 1 → with message 0, bare 1<br>`Money`: with message 2 → with message 0, bare 9<br>`ProgressiveEMICalculator`: with message 14 → with message 4, bare 12 |
| IDIOM.LAMBDA_LOST | 4 | `ProgressiveLoanScheduleGenerator`: 5 → 2 (-3)<br>`ProgressiveEMICalculator`: 113 → 70 (-43)<br>`ProgressiveLoanInterestScheduleModel`: 21 → 7 (-14) |
| IDIOM.METHODREF_LOST | 4 | `ProgressiveLoanScheduleGenerator`: 1 → 0 (-1)<br>`ProgressiveEMICalculator`: 30 → 0 (-30)<br>`ProgressiveLoanInterestScheduleModel`: 14 → 0 (-14) |
| IDIOM.OPTIONAL_LOST | 1 | `ProcessedTransactionData`: 2 → 1 (-1) |
| IDIOM.PATTERNMATCH_LOST | 2 | `LoanSchedulePlan`: 3 → 0 (-3)<br>`Memo`: 1 → 0 (-1) |
| IDIOM.STREAM_LOST | 4 | `ProgressiveLoanScheduleGenerator`: 2 → 0 (-2)<br>`ProgressiveEMICalculator`: 47 → 0 (-47)<br>`ProgressiveLoanInterestScheduleModel`: 22 → 0 (-22) |
| IDIOM.STREAM_TO_LOOP | 3 | `ProgressiveEMICalculator`: streams+lambdas+method refs -120 → loops +47<br>`ProgressiveLoanInterestScheduleModel`: streams+lambdas+method refs -50 → loops +20<br>`RepaymentPeriod`: streams+lambdas+method refs -24 → loops +8 |
| IDIOM.SWITCHEXPR_LOST | 5 | `DaysInMonthType`: 1 → 0 (-1)<br>`DaysInYearType`: 1 → 0 (-1)<br>`ProgressiveEMICalculator`: 6 → 0 (-6) |
| MEMBER.VISIBILITY_NARROWED_CTOR | 4 | `DaysInYearCustomStrategyType` &lt;init&gt;(String,String): public → private (constructor)<br>`LoanReAgeInterestHandlingType` &lt;init&gt;(String,String): public → private (constructor)<br>`LoanScheduleProcessingType` &lt;init&gt;(String): public → private (constructor) |
| MEMBER.VISIBILITY_NARROWED_FIELD | 4 | `AdjustedDateDetailsDTO` changedActualRepaymentDate: package → private (field)<br>`AdjustedDateDetailsDTO` changedScheduleDate: package → private (field)<br>`AdjustedDateDetailsDTO` nextRepaymentPeriodDueDate: package → private (field) |
| MEMBER.VISIBILITY_NARROWED_METHOD | 48 | `DateUtils` compare(LocalDate,LocalDate,boolean): public → private (method)<br>`DateUtils` getDifference(LocalDate,LocalDate,ChronoUnit): public → private (method)<br>`MathUtil` add(BigDecimal,BigDecimal,MathContext): public → private (method) |
| MEMBER.VISIBILITY_NARROWED_STATIC_FIELD | 2 | `DateUtils` DEFAULT_DATETIME_FORMAT: public → package (field)<br>`DateUtils` DEFAULT_DATE_FORMATTER: public → package (field) |
| MEMBER.VISIBILITY_WIDENED_CTOR | 13 | `AbstractPlatformDomainRuleException` &lt;init&gt;(String,String,Object[]): protected → public (constructor)<br>`AbstractPlatformException` &lt;init&gt;(String,String,Object[]): protected → public (constructor)<br>`Money` &lt;init&gt;(CurrencyData,BigDecimal,MathContext): private → public (constructor) |
| MEMBER.VISIBILITY_WIDENED_METHOD | 48 | `CurrencyData` canEqual(Object): protected → public (method)<br>`LoanSchedulePlan` canEqual(Object): protected → public (method)<br>`ProgressiveLoanScheduleGenerator` applyInterestRateChangesOnPeriod(LoanApplicationTerms,LoanScheduleModelRepaymentPeriod,ProgressiveLoanInterestScheduleModel): private → public (method) |
| MEMBER.VISIBILITY_WIDENED_STATIC_FIELD | 7 | `AbstractPlatformException` NO_ARGS: private → public (field)<br>`ProgressiveEMICalculator` DIVISOR_100: private → public (field)<br>`ProgressiveEMICalculator` ONE_WEEK_IN_DAYS: private → public (field) |
| METHOD.DEAD_STUB | 5 | `ProgressiveLoanScheduleGenerator` calculateInstallmentCharge(PrincipalInterest,Money,LoanCharge,MathContext): Money calculateInstallmentCharge(PrincipalInterest,Money,LoanCharge,MathContext) → Object calculateInstallmentCharge(PrincipalInterest,Money,Object,MathContext) (body is a JX-DEAD throw)<br>`ProgressiveLoanScheduleGenerator` calculateSpecificDueDateChargeWithPercentage(Money,Money,Money,LoanCharge,MathContext): Money calculateSpecificDueDateChargeWithPercentage(Money,Money,Money,LoanCharge,MathContext) → Object calculateSpecificDueDateChargeWithPercentage(Money,Money,Money,Object,MathContext) (body is a JX-DEAD throw)<br>`ProgressiveLoanScheduleGenerator` getCumulativeAmountOfCharge(LocalDate,LocalDate,PrincipalInterest,Money,Money,boolean,boolean,LoanCharge,Money,MathContext): Money getCumulativeAmountOfCharge(LocalDate,LocalDate,PrincipalInterest,Money,Money,boolean,boolean,LoanCharge,Money,MathContext) → Object getCumulativeAmountOfCharge(LocalDate,LocalDate,PrincipalInterest,Money,Money,boolean,boolean,Object,Money,MathContext) (body is a JX-DEAD throw) |
| METHOD.EXTRA_ACCESSOR | 9 | `LoanApplicationTerms` getVariationsDataWrappe(): - → Object getVariationsDataWrappe() (source public)<br>`LoanApplicationTerms` isIsDownPaymentEnable(): - → boolean isIsDownPaymentEnable() (source public)<br>`ProgressiveLoanInterestScheduleModel` getInstallmentAmountI(): - → Integer getInstallmentAmountI() (source public) |
| METHOD.EXTRA_API | 1 | `Money` currency(): - → CurrencyData currency() (source public) |
| METHOD.EXTRA_CTOR | 7 | `DefaultScheduledDateGenerator` &lt;init&gt;(): - → &lt;init&gt;() (source public)<br>`EmbeddableProgressiveLoanScheduleGenerator.NoopInterestScheduleModelRepositoryWrapper` &lt;init&gt;(): - → &lt;init&gt;() (source public)<br>`LoanApplicationTerms.Builder` &lt;init&gt;(): - → &lt;init&gt;() (source public) |
| METHOD.EXTRA_RECORD | 33 | `LoanSchedulePlan` currency(): - → CurrencyData currency() (record public)<br>`LoanSchedulePlan` loanTermInDays(): - → int loanTermInDays() (record public)<br>`LoanSchedulePlan` periods(): - → List&lt;LoanSchedulePlanPeriod&gt; periods() (record public) |
| METHOD.UNREACHED_ACCESSOR | 169 | `AbstractPlatformException` getDefaultUserMessage(): String getDefaultUserMessage() → - (source public)<br>`AbstractPlatformException` getDefaultUserMessageArgs(): Object[] getDefaultUserMessageArgs() → - (source public)<br>`AbstractPlatformException` getGlobalisationMessageCode(): String getGlobalisationMessageCode() → - (source public) |
| METHOD.UNREACHED_API | 344 | `LocalDateInterval` contains(LocalDate): boolean contains(LocalDate) → - (source public)<br>`LocalDateInterval` contains(LocalDateInterval): boolean contains(LocalDateInterval) → - (source public)<br>`LocalDateInterval` containsPortionOf(LocalDateInterval): boolean containsPortionOf(LocalDateInterval) → - (source public) |
| METHOD.UNREACHED_CTOR | 12 | `AbstractPlatformException` &lt;init&gt;(String,String): &lt;init&gt;(String,String) → - (source protected)<br>`AbstractPlatformException` &lt;init&gt;(String,String,Throwable): &lt;init&gt;(String,String,Throwable) → - (source protected)<br>`DateUtils` &lt;init&gt;(): &lt;init&gt;() → - (source private) |
| METHOD.UNREACHED_LOMBOK | 116 | `CurrencyData` &lt;init&gt;(): &lt;init&gt;() → - (lombok public)<br>`CurrencyData` &lt;init&gt;(String,String,int,Integer,String,String,String): &lt;init&gt;(String,String,int,Integer,String,String,String) → - (lombok public)<br>`CurrencyData` builder(): CurrencyDataBuilder builder() → - (lombok public) |
| METHOD.UNREACHED_OBJECT | 2 | `LocalDateInterval` toString(): String toString() → - (source public)<br>`Money` toString(): String toString() → - (source public) |
| METHOD.UNREACHED_PRIVATE | 50 | `LocalDateInterval` daysBetween(): Integer daysBetween() → - (source private)<br>`LocalDateInterval` isBetweenInclusive(LocalDate,LocalDate,LocalDate): boolean isBetweenInclusive(LocalDate,LocalDate,LocalDate) → - (source private)<br>`DateUtils` getDateFormatter(String,Locale): DateTimeFormatter getDateFormatter(String,Locale) → - (source private) |
| NAME.ABBREVIATED | 18 | `MathUtil` percentageOf(BigDecimal,BigDecimal,MathContext): percentage → pct (param)<br>`MathUtil` percentageOf(BigDecimal,BigDecimal,MathContext): percentageOf → pctOf (local)<br>`LoanApplicationTerms` disbursedAmountPercentageForDownPayment: disbursedAmountPercentageForDownPayment → disbursedAmountPctF (field) |
| NAME.ACCESSOR_STYLE | 6 | `LocalDateInterval` endDate(): endDate → getEndDate (method)<br>`LocalDateInterval` startDate(): startDate → getStartDate (method)<br>`InterestPeriod` isPaused(): isPaused → isIsPaused (method) |
| NAME.CASE | 12 | `LoanApplicationTerms` holidayDetailDTO: holidayDetailDTO → holidayDetailDto (field)<br>`ProgressiveEMICalculator` calculateAdjustedEqualAmortizationValues(Money,Money,Money,Integer,Integer,MonetaryCurrency): calculatedEMI → calculatedEmi (local)<br>`ProgressiveEMICalculator` calculateLastUnpaidRepaymentPeriodEMI(ProgressiveLoanInterestScheduleModel,LocalDate): minimumEMI → minimumEmi (local) |
| NAME.NUMBERED | 5 | `ProgressiveLoanScheduleGenerator` processDisbursements(LoanApplicationTerms,List,LoanScheduleParams,ProgressiveLoanInterestScheduleModel,List,BigDecimal,boolean,MathContext): maxOutstandingBalance → money (local)<br>`ProgressiveEMICalculator` payInterest(ProgressiveLoanInterestScheduleModel,LocalDate,LocalDate,LocalDate,Money): repaymentPeriod → repaymentPeriod3 (local)<br>`ProgressiveEMICalculator` rateFactorByRepaymentEveryDay(BigDecimal,BigDecimal,BigDecimal,BigDecimal,BigDecimal,MathContext): daysInYear → daysInYear3 (param) |
| NAME.RENAMED | 6 | `ProgressiveEMICalculator` generateAdditionalRepaymentPeriodDueDates(ProgressiveLoanInterestScheduleModel,int,int,Function): repayEvery → repaymentEvery (local)<br>`ProgressiveEMICalculator` getLatestNotLastOpenRepaymentPeriodBeforeDate(ProgressiveLoanInterestScheduleModel,LocalDate): latestNotLastOpenRepaymentPeriod → rp (local)<br>`ProgressiveEMICalculator` getLatestNotLastOpenRepaymentPeriodBeforeDate(ProgressiveLoanInterestScheduleModel,LocalDate): unpaidRepaymentPeriods → list (local) |
| NAME.TRUNCATED.AMBIGUOUS_CORRECT | 90 | `AbstractPlatformDomainRuleException` &lt;init&gt;(String,String,Object[]): defaultUserMessage → defaultUser (param, completion ambiguous-correct)<br>`AbstractPlatformException` &lt;init&gt;(String,String,Object[]): defaultUserMessage → defaultUser (param, completion ambiguous-correct)<br>`CurrencyData` &lt;init&gt;(String,String,int,Integer,String,String): inMultiplesOf → inMultiples (param, completion ambiguous-correct) |
| NAME.TRUNCATED.AMBIGUOUS_WRONG | 50 | `AbstractPlatformException` filterThrowableCause(Object[]): defaultUserMessageArgs → defaultUser (param, completion ambiguous-wrong)<br>`AbstractPlatformException` findThrowableCause(Object[]): defaultUserMessageArgs → defaultUser (param, completion ambiguous-wrong)<br>`LoanScheduleParams` &lt;init&gt;(int,int,int,LocalDate,LocalDate,Money,Money,Money,Money,Money,Money,Money,Map,Map,Map,Money,Map,Money,Money,Money,List,Collection,LoanRepaymentScheduleTransactionProcessor,LocalDate,boolean,CurrencyData,boolean,MathContext): totalOutstandingInterestPaymentDueToGrace → totalOutstanding (param, completion ambiguous-wrong) |
| NAME.TRUNCATED.NONE | 332 | `AbstractPlatformDomainRuleException` &lt;init&gt;(String,String,Object[]): globalisationMessageCode → globalisationMessage (param, completion none)<br>`AbstractPlatformException` &lt;init&gt;(String,String,Object[]): globalisationMessageCode → globalisationMessage (param, completion none)<br>`AbstractPlatformException` defaultUserMessageArgs: defaultUserMessageArgs → defaultUserMessageA (field, completion none) |
| NAME.TRUNCATED.UNIQUE_CORRECT | 104 | `DisbursementData` &lt;init&gt;(Long,Long,LocalDate,LocalDate,BigDecimal,BigDecimal,String,BigDecimal,BigDecimal): actualDisbursementDate → actualDisbursement (param, completion unique-correct)<br>`LoanRepaymentScheduleProcessingWrapper` isInPeriod(LocalDate,LocalDate,LocalDate,boolean): isFirstPeriod → isFirstPer (param, completion unique-correct)<br>`LoanScheduleParams` &lt;init&gt;(int,int,int,LocalDate,LocalDate,Money,Money,Money,Money,Money,Money,Money,Map,Map,Map,Money,Map,Money,Money,Money,List,Collection,LoanRepaymentScheduleTransactionProcessor,LocalDate,boolean,CurrencyData,boolean,MathContext): actualRepaymentDate → actualRepayment (param, completion unique-correct) |
| NAME.TRUNCATED.UNIQUE_WRONG | 98 | `LoanScheduleModelDownPaymentPeriod` addLoanCharges(BigDecimal,BigDecimal): penaltyCharge → penaltyChar (param, completion unique-wrong)<br>`LoanScheduleParams` createLoanScheduleParams(CurrencyData,Money,LocalDate,Money,MathContext): chargesDueAtTimeOfDisbursement → chargesDue (param, completion unique-wrong)<br>`LoanSchedulePlan` from(LoanScheduleModel): remainingTotalOutstanding → remainingTo (local, completion unique-wrong) |
| NAME.TRUNCATED_NUMBERED.AMBIGUOUS_CORRECT | 7 | `LoanScheduleParams` createLoanScheduleParams(CurrencyData,Money,LocalDate,Money,MathContext): totalCumulativeInterest → totalCumula2 (local, completion ambiguous-correct)<br>`ProgressiveEMICalculator` calculateRateFactorPerPeriod(ProgressiveLoanInterestScheduleModel,RepaymentPeriod,LocalDate,LocalDate): daysInMonth → daysInMont2 (local, completion ambiguous-correct)<br>`ProgressiveEMICalculator` calculateRateFactorPerPeriod(ProgressiveLoanInterestScheduleModel,RepaymentPeriod,LocalDate,LocalDate): daysInYearCustomStrategy → daysInYear2 (local, completion ambiguous-correct) |
| NAME.TRUNCATED_NUMBERED.AMBIGUOUS_WRONG | 21 | `AbstractPlatformDomainRuleException` &lt;init&gt;(String,String,Object[]): defaultUserMessageArgs → defaultUser2 (param, completion ambiguous-wrong)<br>`AbstractPlatformException` &lt;init&gt;(String,String,Object[]): defaultUserMessageArgs → defaultUser2 (param, completion ambiguous-wrong)<br>`AbstractPlatformException` filterThrowableCause(Object[]): defaultUserMessageArg → defaultUser2 (local, completion ambiguous-wrong) |
| NAME.TRUNCATED_NUMBERED.UNIQUE_WRONG | 21 | `LoanSchedulePlan` from(LoanScheduleModel): remainingTotalOutstandingRef → remainingTo2 (local, completion unique-wrong)<br>`ProgressiveEMICalculator` addCapitalizedIncome(ProgressiveLoanInterestScheduleModel,LocalDate,Money): transactionAmount → transaction2 (param, completion unique-wrong)<br>`ProgressiveEMICalculator` addFullTermTrancheDisbursement(ProgressiveLoanInterestScheduleModel,EmiChangeOperation): firstDisbursedPeriodStartDate → firstDisbur2 (local, completion unique-wrong) |
| SUPERTYPE.INTERFACE_LOST | 3 | `DaysInYearCustomStrategyType`: ApiFacingEnum&lt;DaysInYearCustomStrategyType&gt; → -<br>`DisbursementData`: LoanPrincipalRelatedDataHolder → -<br>`ProgressiveLoanScheduleGenerator`: LoanScheduleGenerator → - |
| SUPERTYPE.JDK_INTERFACE_LOST | 1 | `CurrencyData`: Serializable → - |
| TYPE.FINAL_LOST | 15 | `DateUtils`: final → -<br>`MathUtil`: final → -<br>`DisbursementData`: final → - |
| TYPE.KIND_CLASS_TO_RECORD | 6 | `LoanSchedulePlan`: class → record<br>`LoanSchedulePlanDisbursementPeriod`: class → record<br>`LoanSchedulePlanDownPaymentPeriod`: class → record |
| TYPE.MOVED | 2 | `InterestScheduleModelRepositoryWrapper`: org.apache.fineract.portfolio.loanaccount.service.InterestScheduleModelRepositoryWrapper → org.apache.fineract.portfolio.loanaccount.loanschedule.domain.InterestScheduleModelRepositoryWrapper (package org.apache.fineract.portfolio.loanaccount.service -&gt; org.apache.fineract.portfolio.loanaccount.loanschedule.domain)<br>`ILoanConfigurationDetails`: org.apache.fineract.portfolio.loanproduct.domain.ILoanConfigurationDetails → org.apache.fineract.portfolio.loanproduct.data.ILoanConfigurationDetails (package org.apache.fineract.portfolio.loanproduct.domain -&gt; org.apache.fineract.portfolio.loanproduct.data) |
| TYPE.VISIBILITY_WIDENED | 2 | `EmbeddableProgressiveLoanScheduleGenerator.NoopInterestScheduleModelRepositoryWrapper`: private → public<br>`ProgressiveEMICalculator.PaidAmounts`: private → public |
| TYPEREF.BOXING | 2 | `LoanSchedulePlanDownPaymentPeriod` periodNumber(): Integer → int (return)<br>`LoanSchedulePlanRepaymentPeriod` periodNumber(): Integer → int (return) |
| TYPEREF.COLLECTION_CHANGED | 3 | `ProgressiveLoanScheduleGenerator` updatePeriodsWithCharges(CurrencyData,LoanScheduleParams,Collection,Set,MathContext): Collection&lt;LoanScheduleModelPeriod&gt; → List&lt;LoanScheduleModelPeriod&gt; (param 3)<br>`ProgressiveLoanInterestScheduleModel` &lt;init&gt;(List,TreeSet,ILoanConfigurationDetails,Integer,MathContext,boolean): TreeSet&lt;InterestRate&gt; → NavigableSet&lt;InterestRate&gt; (param 2)<br>`ProgressiveLoanInterestScheduleModel` interestRates: TreeSet&lt;InterestRate&gt; → NavigableSet&lt;InterestRate&gt; (field) |
| TYPEREF.GENERIC_CHANGED | 6 | `ProgressiveEMICalculator` generateAdditionalRepaymentPeriodDueDates(ProgressiveLoanInterestScheduleModel,int,int,Function): Function&lt;Long,LocalDate&gt; → Function&lt;Integer,LocalDate&gt; (param 4)<br>`ProgressiveEMICalculator` generateInterestScheduleModel(List,Function,Function,ILoanConfigurationDetails,Integer,MathContext): List&lt;T&gt; → List&lt;?&gt; (param 1)<br>`ProgressiveEMICalculator` updateModel(ProgressiveLoanInterestScheduleModel,List,Function,Function): Function&lt;T,LocalDate&gt; → Function&lt;LocalDateInterval,LocalDate&gt; (param 3) |
| TYPEREF.GENERIC_DROPPED | 5 | `RepaymentPeriod` calculatedDueInterestCalculation: Memo&lt;Money&gt; → Memo (field)<br>`RepaymentPeriod` dueInterestCalculation: Memo&lt;Money&gt; → Memo (field)<br>`RepaymentPeriod` outstandingBalanceCalculation: Memo&lt;Money&gt; → Memo (field) |
| TYPEREF.GENERIC_TO_OBJECT_EXTERNAL | 7 | `LoanScheduleParams` &lt;init&gt;(int,int,int,LocalDate,LocalDate,Money,Money,Money,Money,Money,Money,Money,Map,Map,Map,Money,Map,Money,Money,Money,List,Collection,LoanRepaymentScheduleTransactionProcessor,LocalDate,boolean,CurrencyData,boolean,MathContext): List&lt;LoanRepaymentScheduleInstallment&gt; → List&lt;Object&gt; (param 21)<br>`LoanScheduleParams` installments: List&lt;LoanRepaymentScheduleInstallment&gt; → List&lt;Object&gt; (field)<br>`LoanScheduleModelRepaymentPeriod` loanCompoundingDetails: Set&lt;InterestRecalculationAdditionalDetailData&gt; → Set&lt;Object&gt; (field) |
| TYPEREF.GENERIC_TO_OBJECT_KNOWN | 16 | `LoanScheduleParams` &lt;init&gt;(int,int,int,LocalDate,LocalDate,Money,Money,Money,Money,Money,Money,Money,Map,Map,Map,Money,Map,Money,Money,Money,List,Collection,LoanRepaymentScheduleTransactionProcessor,LocalDate,boolean,CurrencyData,boolean,MathContext): Map&lt;LocalDate,Money&gt; → Map&lt;Object,Object&gt; (param 13)<br>`LoanScheduleParams` &lt;init&gt;(int,int,int,LocalDate,LocalDate,Money,Money,Money,Money,Money,Money,Money,Map,Map,Map,Money,Map,Money,Money,Money,List,Collection,LoanRepaymentScheduleTransactionProcessor,LocalDate,boolean,CurrencyData,boolean,MathContext): Map&lt;LocalDate,Money&gt; → Map&lt;Object,Object&gt; (param 14)<br>`LoanScheduleParams` &lt;init&gt;(int,int,int,LocalDate,LocalDate,Money,Money,Money,Money,Money,Money,Money,Map,Map,Map,Money,Map,Money,Money,Money,List,Collection,LoanRepaymentScheduleTransactionProcessor,LocalDate,boolean,CurrencyData,boolean,MathContext): Map&lt;LocalDate,Money&gt; → Map&lt;Object,Object&gt; (param 15) |
| TYPEREF.INTERFACE_TO_IMPL | 13 | `LoanApplicationTerms` toLoanConfigurationDetails(): ILoanConfigurationDetails → LoanConfigurationDetails (return)<br>`ProgressiveLoanScheduleGenerator` emiCalculator: EMICalculator → ProgressiveEMICalculator (field)<br>`ProgressiveLoanScheduleGenerator` scheduledDateGenerator: ScheduledDateGenerator → DefaultScheduledDateGenerator (field) |
| TYPEREF.OTHER | 3 | `DefaultScheduledDateGenerator` adjustDate(Temporal,Temporal,PeriodFrequencyType): Temporal → LocalDate (param 1)<br>`DefaultScheduledDateGenerator` adjustDate(Temporal,Temporal,PeriodFrequencyType): Temporal → LocalDate (param 2)<br>`DefaultScheduledDateGenerator` adjustDate(Temporal,Temporal,PeriodFrequencyType): Temporal → LocalDate (return) |
| TYPEREF.TO_OBJECT_EXTERNAL | 41 | `LoanScheduleParams` &lt;init&gt;(int,int,int,LocalDate,LocalDate,Money,Money,Money,Money,Money,Money,Money,Map,Map,Map,Money,Map,Money,Money,Money,List,Collection,LoanRepaymentScheduleTransactionProcessor,LocalDate,boolean,CurrencyData,boolean,MathContext): Collection&lt;RecalculationDetail&gt; → Object (param 22)<br>`LoanScheduleParams` &lt;init&gt;(int,int,int,LocalDate,LocalDate,Money,Money,Money,Money,Money,Money,Money,Map,Map,Map,Money,Map,Money,Money,Money,List,Collection,LoanRepaymentScheduleTransactionProcessor,LocalDate,boolean,CurrencyData,boolean,MathContext): LoanRepaymentScheduleTransactionProcessor → Object (param 23)<br>`LoanScheduleParams` loanRepaymentScheduleTransactionProcessor: LoanRepaymentScheduleTransactionProcessor → Object (field) |
| TYPEREF.TO_OBJECT_KNOWN | 36 | `AbstractPlatformException` findThrowableCause(Object[]): Throwable → Object (return)<br>`DateUtils` DEFAULT_DATETIME_FORMAT: String → Object (field)<br>`DateUtils` DEFAULT_DATE_FORMATTER: DateTimeFormatter → Object (field) |
| TYPEREF.VARARGS_TO_ARRAY | 4 | `AbstractPlatformDomainRuleException` &lt;init&gt;(String,String,Object[]): Object... → Object[] (param 3)<br>`MathUtil` add(MathContext,BigDecimal[]): BigDecimal... → BigDecimal[] (param 2)<br>`MathUtil` plus(MathContext,Money[]): Money... → Money[] (param 2) |

## Matched types

| Original | Modern | Kind | API | Signatures | Fields m/x | Methods m/x | Distance |
|---|---|---|---|---|---|---|---|
| `LocalDateInterval` | same | class | 20.0% | 16.7% | 0/0 | 8/0 | 2.0 |
| `AbstractPlatformDomainRuleException` | same | abstract | 0.0% | 0.0% | 0/0 | 0/0 | 3.8 |
| `AbstractPlatformException` | same | abstract | 0.0% | 25.0% | 0/0 | 5/0 | 7.5 |
| `DateUtils` | same | class | 13.6% | 16.3% | 2/0 | 67/0 | 12.0 |
| `MathUtil` | same | class | 18.1% | 22.6% | 0/0 | 63/0 | 9.8 |
| `CurrencyData` | same | class | 28.0% | 34.6% | 1/0 | 17/0 | 10.0 |
| `MonetaryCurrency` | same | class | 30.0% | 27.3% | 0/0 | 8/0 | 1.5 |
| `Money` | same | class | 49.3% | 66.2% | 0/0 | 24/1 | 24.0 |
| `AdjustedDateDetailsDTO` | same | class | 12.5% | 12.5% | 0/0 | 6/0 | 7.8 |
| `DaysInMonthType` | same | enum | 66.7% | 57.1% | 0/0 | 3/0 | 0.8 |
| `DaysInYearCustomStrategyType` | same | enum | 40.0% | 33.3% | 0/0 | 2/0 | 4.0 |
| `DaysInYearType` | same | enum | 72.7% | 57.1% | 0/0 | 3/0 | 1.0 |
| `PeriodFrequencyType` | same | enum | 57.1% | 33.3% | 0/0 | 6/0 | 0.0 |
| `DisbursementData` | same | class | 18.2% | 16.0% | 0/0 | 21/0 | 8.5 |
| `LoanRepaymentScheduleProcessingWrapper` | same | class | 22.2% | 12.5% | 0/0 | 14/0 | 0.3 |
| `LoanReAgeInterestHandlingType` | same | enum | 44.4% | 20.0% | 0/0 | 4/0 | 1.0 |
| `LoanTransactionProcessingException` | same | class | 0.0% | 0.0% | 0/0 | 1/0 | 2.5 |
| `LoanScheduleModelDownPaymentPeriod` | same | class | 45.8% | 48.0% | 0/0 | 13/0 | 2.3 |
| `LoanScheduleParams` | same | class | 31.1% | 30.6% | 0/0 | 40/0 | 31.3 |
| `LoanSchedulePlan` | same | class → record | 87.5% | 93.8% | 0/0 | 0/10 | 20.3 |
| `LoanSchedulePlanDisbursementPeriod` | same | class → record | 83.3% | 83.3% | 0/0 | 2/2 | 7.0 |
| `LoanSchedulePlanDownPaymentPeriod` | same | class → record | 80.0% | 80.0% | 0/0 | 1/4 | 13.3 |
| `LoanSchedulePlanPeriod` | same | interface | 0.0% | 0.0% | 0/0 | 3/0 | 0.0 |
| `LoanSchedulePlanRepaymentPeriod` | same | class → record | 88.9% | 88.9% | 0/0 | 1/7 | 14.0 |
| `DefaultScheduledDateGenerator` | same | class | 9.1% | 17.6% | 0/0 | 9/1 | 21.5 |
| `EmbeddableProgressiveLoanScheduleGenerator` | same | class | 100.0% | 100.0% | 0/0 | 0/0 | 0.0 |
| `EmbeddableProgressiveLoanScheduleGenerator.NoopInterestScheduleModelRepositoryWrapper` | same | class | - | 0.0% | 0/0 | 8/1 | 4.0 |
| `LoanApplicationTerms` | same | class | 10.8% | 9.8% | 0/0 | 131/2 | 121.8 |
| `LoanApplicationTerms.Builder` | same | class | 62.2% | 75.7% | 0/0 | 9/1 | 20.8 |
| `LoanRepaymentScheduleModelData` | same | record | 95.7% | 95.7% | 0/0 | 0/5 | 13.5 |
| `LoanScheduleModel` | same | class | 60.0% | 62.5% | 0/0 | 4/0 | 8.5 |
| `LoanScheduleModelDisbursementPeriod` | same | class | 41.7% | 44.0% | 0/0 | 14/0 | 5.3 |
| `LoanScheduleModelPeriod` | same | interface | 36.8% | 36.8% | 0/0 | 12/0 | 0.3 |
| `LoanScheduleModelRepaymentPeriod` | same | class | 58.8% | 58.8% | 0/0 | 14/0 | 7.5 |
| `LoanScheduleProcessingType` | same | enum | 25.0% | 16.7% | 0/0 | 5/0 | 1.0 |
| `LoanScheduleType` | same | enum | 28.6% | 20.0% | 0/0 | 4/0 | 1.0 |
| `PrincipalInterest` | same | class | 16.7% | 16.7% | 0/0 | 5/0 | 2.3 |
| `ProgressiveLoanScheduleGenerator` | same | class | 25.0% | 27.3% | 0/0 | 6/0 | 57.3 |
| `ScheduledDateGenerator` | same | interface | 0.0% | 0.0% | 0/0 | 10/0 | 0.0 |
| `InterestScheduleModelRepositoryWrapper` | `org.apache.fineract.portfolio.loanaccount.loanschedule.domain.InterestScheduleModelRepositoryWrapper` (moved) | interface | 0.0% | 0.0% | 0/0 | 8/0 | 0.0 |
| `EMICalculator` | same | interface | 0.0% | 0.0% | 0/0 | 31/0 | 0.0 |
| `ProgressiveEMICalculator` | same | class | 80.6% | 90.3% | 0/0 | 4/0 | 175.3 |
| `ProgressiveEMICalculator.PaidAmounts` | same | record | - | 100.0% | 0/0 | 0/0 | 5.0 |
| `EmiAdjustment` | same | record | 84.6% | 92.9% | 0/0 | 1/0 | 1.5 |
| `EmiChangeOperation` | same | class | 83.3% | 84.6% | 0/0 | 2/0 | 3.0 |
| `EmiChangeOperation.Action` | same | enum | 0.0% | 100.0% | 0/0 | 0/1 | 4.5 |
| `EqualAmortizationValues` | same | record | 83.3% | 91.7% | 0/0 | 1/0 | 1.5 |
| `InterestPeriod` | same | class | 78.6% | 86.0% | 0/0 | 5/0 | 9.8 |
| `InterestRate` | same | record | 100.0% | 100.0% | 0/0 | 0/0 | 0.0 |
| `LoanInterestScheduleModelModifiers` | same | enum | 40.0% | 100.0% | 0/0 | 0/1 | 3.5 |
| `LoanReAgeParameterData` | same | class | 85.7% | 100.0% | 0/0 | 0/1 | 3.3 |
| `OutstandingDetails` | same | class → record | 85.7% | 100.0% | 0/0 | 0/2 | 8.0 |
| `OverdueBalanceCorrection` | same | record | 100.0% | 100.0% | 0/0 | 0/0 | 0.0 |
| `PeriodDueDetails` | same | class → record | 87.5% | 100.0% | 0/0 | 0/3 | 9.0 |
| `ProcessedTransactionData` | same | class | 25.0% | 25.0% | 3/0 | 6/0 | 7.5 |
| `ProgressiveLoanInterestScheduleModel` | same | class | 66.7% | 73.0% | 1/0 | 11/3 | 63.3 |
| `RepaymentPeriod` | same | class | 78.0% | 84.5% | 0/0 | 11/0 | 36.8 |
| `RepaymentScheduleInstallmentData` | same | class | 87.5% | 100.0% | 0/0 | 0/1 | 3.0 |
| `LoanConfigurationDetails` | same | class | 26.9% | 29.6% | 0/0 | 10/4 | 26.5 |
| `AmortizationMethod` | same | enum | 37.5% | 16.7% | 0/0 | 5/0 | 0.0 |
| `ILoanConfigurationDetails` | `org.apache.fineract.portfolio.loanproduct.data.ILoanConfigurationDetails` (moved) | interface | 0.0% | 0.0% | 0/0 | 25/0 | 0.0 |
| `InterestCalculationPeriodMethod` | same | enum | 50.0% | 33.3% | 0/0 | 4/0 | 0.0 |
| `InterestMethod` | same | enum | 37.5% | 16.7% | 0/0 | 5/0 | 0.0 |
| `LoanPreCloseInterestCalculationStrategy` | same | enum | 40.0% | 25.0% | 0/0 | 6/0 | 4.5 |
| `RecalculationFrequencyType` | same | enum | 38.5% | 11.1% | 0/0 | 8/0 | 1.5 |
| `RepaymentStartDateType` | same | enum | 25.0% | 16.7% | 0/0 | 5/0 | 1.0 |
| `Memo` | same | class | 0.0% | 28.6% | 0/0 | 2/0 | 17.5 |

## Modern-only types

50 pipeline infrastructure types (the generated job, platform, record and strangler layers; not gaps). By rule: glob:**/batch/** 10, lane:compat 40.

| Other modern-only type | Kind | Category | Why |
|---|---|---|---|
| `org.apache.fineract.portfolio.loanproduct.calc.data.LoanReAgeParameterData.LoanReAgeParameterDataBuilder` | class | lombok-expansion | the delomboked builder class of @Builder on org.apache.fineract.portfolio.loanproduct.calc.data.LoanReAgeParameterData |
| `org.apache.fineract.portfolio.loanproduct.calc.data.RepaymentScheduleInstallmentData.RepaymentScheduleInstallmentDataBuilder` | class | lombok-expansion | the delomboked builder class of @Builder on org.apache.fineract.portfolio.loanproduct.calc.data.RepaymentScheduleInstallmentData |

