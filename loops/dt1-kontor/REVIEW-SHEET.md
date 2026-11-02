# DT1 review sheet: kontor-zins-legacy names held for a human

The thinker (claude-opus-5-5, prompt naming-1u) proposed these below the 0.7 auto-approval threshold, or they were held by a check. Mark **Approve** with your initials to accept the thinker's name, or write a better one under **Rename**. Approved rows enter the naming ledger as `review:<initials>`; the rename engine applies them by id, and behaviour is proven by the same gates. Evidence quotes are verbatim from the COBOL.

## Types (class, record, enum names) (26)

| Unit | COBOL | Generator | Thinker | Conf | Why | Evidence | Approve | Rename |
|---|---|---|---|---|---|---|---|---|
| U002 | RETURN-CODE | `ReturnCodeRecord` | **`ReturnCode`** | 0.50 | Holder type for the RETURN-CODE special register, not a file layout, so no Record suffix; it is just the job step return code. | `MOVE 16 TO RETURN-CODE` CEE3ABD.cbl:9 | | |
| U004 | DK-FUNCTION | `ParmsFunction` | **`RoundingFunction`** | 0.70 | Enum of the FNDKRND function codes, all named by 88-levels; named after what it classifies (the kind of rounding). | `DK-FN-SIGNIFICANT` FNDKPRM.cpy:4 | | |
| U007 | DK-MODE | `ParmsMode` | **`RoundingMode`** | 0.80 | Enum of the DK-MODE 88-levels, which are exactly the BigDecimal rounding modes. (canonical unit contains a pre-check rejected member (U007)) | `DK-MODE-HALF-EVEN` FNDKPRM.cpy:15 | | |
| U014 | DK-PARMS | `ParmsRecord` | **`RoundingKernelParameters`** | 0.65 | DK-PARMS is the linkage block of the rounding kernel FNDKRND (value, function, digits, mode, sticky, return code); PARMS expanded. | `PROCEDURE DIVISION USING DK-PARMS.` FNDKRND.cbl:30 | | |
| U021 | FO-FUNC | `OutFunc` | **`ServiceFunction`** | 0.60 | Enum of the FO-FUNC values that select the service's action. | `EVALUATE FO-FUNC` FSEM0100.cbl:52 | | |
| U026 | FO-STATUS | `OutStatus` | **`ServiceStatus`** | 0.60 | Enum of the FO-STATUS outcomes of a service call. | `EVALUATE FO-STATUS` KZLE0100.cbl:233 | | |
| U028 | FS-OUT | `OutRecord` | **`ServiceResponse`** | 0.50 | FS-OUT is the exchange area of the annuity calculation service: the function in, then status, reject and result records out. Not a file layout; mostly response. | `KZLE0100 READS CASES AND CALLS THE ANNUITY CALCULATION SERVI` KZANNUIT.jcl:8 | | |
| U039 | JB-OP | `JbOp` | **`DecimalOperation`** | 0.75 | Enum of the decimal kernel's operation codes; each dispatches to an arithmetic paragraph. | `WHEN 'AD' PERFORM 1000-ADD` FSJBDEC.cbl:243 | | |
| U046 | JB | `JbRecord` | **`DecimalKernelParameters`** | 0.60 | JB is the linkage block of the decimal kernel FSJBDEC: operation, operands A and B, result, precision/scale, mode, return code. | `CALL 'FSJBDEC' USING JB` FSEM0100.cbl:553 | | |
| U066 | KZ-CASE-WORK | `CaseWorkRecord` | **`LoanCase`** | 0.55 | KZ-CASE-WORK is reset per case and carries the loan's principal, rate, duration, offer count, stored terms and plans across service calls; the driver calls a ca | `INITIALIZE KZ-CASE-WORK` FSEM0100.cbl:69 | | |
| U119 | KE-TYPE | `EventType` | **`(keep)`** | 0.75 | Enum of the event types the driver accepts and the service dispatches on. | `IF NOT (KE-TYPE = 'MP' OR KE-TYPE = 'NT' OR KE-TYPE = 'MT' O` KZLE0100.cbl:310 | | |
| U138 | ANNUAL-RATE-VALUE | `AnnualRateValueRecord` | **`InterestRateValue`** | 0.50 | Generated type used for both ANNUAL-RATE-VALUE and MONTHLY-RATE-VALUE: an interest rate held as unscaled value, scale and exponent. | `MOVE ANNUAL-RATE-VALUE TO JB-A` FSEM0100.cbl:314 | | |
| U142 | PAYMENT-VALUE | `PaymentValueRecord` | **`DecimalValue`** | 0.55 | Generated type used for PRINCIPAL-, PAYMENT-, NUMERATOR-, POWER- and RESULT-VALUE: a BigDecimal-style number (unscaled value, scale, exponent) exchanged with th | `MOVE PRINCIPAL-VALUE TO JB-A` FSEM0100.cbl:290 | | |
| U146 | POWER-BASE | `PowerBaseRecord` | **`PowerBaseValue`** | 0.45 | POWER-BASE holds 1 plus the monthly rate, the base raised to the number of months in the compound factor; a working value, not a record. | `MOVE JB-R TO POWER-BASE` FSEM0100.cbl:288 | | |
| U148 | LK-CODE | `Cee3abdCodeRecord` | **`AbendCode`** | 0.60 | Single-field linkage parameter holding the abend code; a parameter, not an I/O record, so the type is named for its value. | `CALL 'CEE3ABD' USING DW-ABEND-CODE DW-ABEND-TIMING.` KZLE0100.cbl:433 | | |
| U150 | LK-TIMING | `Cee3abdTimingRecord` | **`AbendTiming`** | 0.60 | Single-field linkage parameter holding the abend timing option; named for its value rather than the program id. | `LK-TIMING` CEE3ABD.cbl:6 | | |
| U152 | WS-POWERS | `FndkrndPowersRecord` | **`RoundingPowerTable`** | 0.60 | WS-POWERS is the rounding kernel's private table of powers of ten; prefixed to stay distinct from the decimal kernel's table. | `1000-INIT-POWERS.` FNDKRND.cbl:61 | | |
| U224 | BG-SAVE | `FsjbdecSaveRecord` | **`SavedOperands`** | 0.55 | BG-SAVE is scratch that preserves the original operands and parameters so a fast-path overflow can be redone by the big-number path. | `MOVE JB-A TO BG-SAVE-A` FSJBDEC.cbl:191 | | |
| U229 | BG-STORE | `FsjbdecStoreRecord` | **`BigNumberStore`** | 0.60 | BG-STORE keeps up to 2500 big results; callers get a handle of generation times 10^6 plus slot. | `05 BG-V OCCURS 2500.` FSJBDEC.cbl:79 | | |
| U257 | BX-REGS | `FsjbdecRegsRecord` | **`BigNumberRegisters`** | 0.55 | BX-REGS are the multi-limb (base 10^9) number registers of the big-number arithmetic: operands, result, quotient, remainder, normalised dividend and divisor. | `BX-REGS.` FSJBDEC.cbl:85 | | |
| U295 | BX-WORK | `FsjbdecWorkRecord` | **`BigNumberScratch`** | 0.45 | BX-WORK is scratch for the big-number algorithms (indexes, carries, estimates); not a business entity. | `BX-WORK.` FSJBDEC.cbl:122 | | |
| U318 | W-MODE | `FsjbdecAreaMode` | **`ResolvedRoundingMode`** | 0.50 | Enum of the letter codes W-MODE resolves JB-M into; it adds UNNECESSARY, so it is kept distinct from the rounding kernel's RoundingMode. | `EVALUATE JB-M` FSJBDEC.cbl:232 | | |
| U345 | W-AREA | `FsjbdecAreaRecord` | **`PackedDecimalScratch`** | 0.45 | W-AREA is the scratch of the 31-digit packed (fast) arithmetic path; an algorithm area, not an entity. | `W-AREA.` FSJBDEC.cbl:8 | | |
| U347 | W-POWERS | `FsjbdecPowersRecord` | **`DecimalPowerTable`** | 0.60 | W-POWERS is the decimal kernel's powers-of-ten table, distinct from the rounding kernel's. | `MOVE 1 TO W-POW (1)` FSJBDEC.cbl:165 | | |
| U375 | DW-DATE | `Kzle0100DateRecord` | **`CalendarDate`** | 0.60 | DW-DATE is a year-month-day value (redefined as an 8-character date); a value type, not a file record. | `DW-DATE-X REDEFINES DW-DATE PIC X(8).` KZLE0100.cbl:62 | | |
| U390 | DW-FIRST, DW-SKIP, DW-SAW-EVENT, DW-PREV | `EventSequence` | **`CaseSequence`** | 0.60 | DW-FIRST, DW-PREV-KEY, DW-LOAN, DW-SAW-EVENT and DW-SKIP together track key order and record placement per loan case. | `MOVE KZ-EVENT-REC (1:12) TO DW-PREV-KEY` KZLE0100.cbl:172 | | |

## Business methods and parameters (job driver and calculation service) (18)

| Unit | COBOL | Generator | Thinker | Conf | Why | Evidence | Approve | Rename |
|---|---|---|---|---|---|---|---|---|
| U194 | 0000-PROCESS-REQUEST/parameter[0], 0000- | `-` | **`(parameters)`** | 0.60 | Entry paragraph is locked; its parameters are the loan case state, the run control, the case record and the service response. | `PROCEDURE DIVISION USING KZ-CASE-WORK FN-RUN-CONTROL` FSEM0100.cbl:45 | | |
| U196 | 1100-CHECK-OFFER, 1100-CHECK-OFFER/param | `checkOffer` | **`countValidOffer`** | 0.55 | CHECK-OFFER validates the offer's terms and, if valid, adds it to OFFER-COUNT; an invalid offer is rejected. | `PERFORM 3000-CHECK-TERMS` FSEM0100.cbl:84 | | |
| U198 | 2100-RAW-CALCULATION, 2100-RAW-CALCULATI | `rawCalculation` | **`loadRawCaseValues`** | 0.60 | RAW-CALCULATION loads the unrounded case principal, rate and months (scales 4 and 13) as the calculation inputs. | `2100-RAW-CALCULATION.` FSEM0100.cbl:175 | | |
| U199 | 2200-TERM-CALCULATION, 2200-TERM-CALCULA | `termCalculation` | **`loadStoredTerms`** | 0.60 | TERM-CALCULATION loads the stored, rounded loan terms of the slot as calculation inputs, failing if none are stored. | `2200-TERM-CALCULATION.` FSEM0100.cbl:181 | | |
| U200 | 2300-CHECK-PLAN, 2300-CHECK-PLAN/paramet | `checkPlan` | **`requirePlan`** | 0.60 | CHECK-PLAN fails with NullPointerException when no plan is stored, like a Java null check. | `IF PLAN-PRESENT (1) NOT = 'Y'` FSEM0100.cbl:193 | | |
| U203 | 3150-ROUND-RATE, 3150-ROUND-RATE/paramet | `roundRate` | **`(keep)`** | 0.65 | ROUND-RATE rounds the rate half-even to 12 decimals into the terms. | `3150-ROUND-RATE.` FSEM0100.cbl:213 | | |
| U207 | 4300-ROUND-POWER | `roundPower` | **`roundCompoundFactor`** | 0.60 | ROUND-POWER rounds the power value to the rounding precision by setting its scale lower. | `4300-ROUND-POWER.` FSEM0100.cbl:358 | | |
| U213 | 6300-OUTPUT-ROW, 6300-OUTPUT-ROW/paramet | `outputRow` | **`outputInstallment`** | 0.65 | OUTPUT-ROW adds one installment row of the plan (record type M) to the response. | `6300-OUTPUT-ROW.` FSEM0100.cbl:509 | | |
| U214 | 6400-MONEY-RESULT | `moneyResult` | **`convertMoneyToResult`** | 0.55 | MONEY-RESULT turns a cents amount into the result value with scale 2. | `6400-MONEY-RESULT.` FSEM0100.cbl:528 | | |
| U216 | 6600-START-OUTPUT, 6600-START-OUTPUT/par | `startOutput` | **`(keep)`** | 0.60 | START-OUTPUT clears the result record and fills its key and number. | `6600-START-OUTPUT.` FSEM0100.cbl:538 | | |
| U217 | 6700-SAVE-OUTPUT, 6700-SAVE-OUTPUT/param | `saveOutput` | **`(keep)`** | 0.60 | SAVE-OUTPUT appends the built result record to the response table. | `6700-SAVE-OUTPUT.` FSEM0100.cbl:545 | | |
| U219 | 9000-REJECT-RECORD, 9000-REJECT-RECORD/p | `rejectRecord` | **`rejectCaseRecord`** | 0.65 | REJECT-RECORD replaces the output with a reject of the input record (code UNMAPPED, exception name) and sets status R. | `9000-REJECT-RECORD.` FSEM0100.cbl:562 | | |
| U220 | 9900-SIZE-ERROR | `sizeError` | **`abendOnCapacityError`** | 0.65 | SIZE-ERROR reports a calculation capacity error and abends through CEE3ABD. | `DISPLAY 'ANNUITY CALCULATION CAPACITY ERROR'` FSEM0100.cbl:578 | | |
| U358 | 1000-READ-RECORD, 1000-READ-RECORD/param | `track` | **`checkRecordOrder`** | 0.60 | The read paragraph's sequence logic checks that keys ascend, each loan starts with a header and offers precede events, and counts loans and events. | `DISPLAY 'KZLE0100 RECORD OUT OF SEQUENCE '` KZLE0100.cbl:167 | | |
| U359 | 2010-CHECK-RECORD, 2010-CHECK-RECORD/par | `checkRecord` | **`processCaseRecord`** | 0.60 | CHECK-RECORD validates the record by type and then either writes a reject or calls the service. | `PERFORM 2900-CHECK-REJECT` KZLE0100.cbl:224 | | |
| U361 | 2020-CALL/parameter[0], 2020-CALL/parame | `-` | **`(parameters)`** | 0.60 | 2020-CALL is locked; its parameters are the case record passed to the service and the run totals it updates. | `CALL 'FSEM0100' USING KZ-CASE-WORK FN-RUN-CONTROL` KZLE0100.cbl:230 | | |
| U364 | 2110-DATE-CHECK | `dateCheck` | **`validateDate`** | 0.55 | DATE-CHECK validates year, month and day including leap years; note it is never performed in this program. | `2110-DATE-CHECK.` KZLE0100.cbl:396 | | |
| U365 | 2200-CHECK-PERIOD, 2200-CHECK-PERIOD/par | `checkPeriod` | **`validateOffer`** | 0.55 | The paragraph is called CHECK-PERIOD, but it validates the P record's KP- fields, which the copybook names the offer layout. | `2200-CHECK-PERIOD.` KZLE0100.cbl:288 | | |

## Record instances and state (11)

| Unit | COBOL | Generator | Thinker | Conf | Why | Evidence | Approve | Rename |
|---|---|---|---|---|---|---|---|---|
| U153 | CEE3ABD | `cee3abd` | **`abendService`** | 0.60 | Field holding the CEE3ABD abend service, called on a capacity error. | `CALL 'CEE3ABD' USING SIZE-ERROR-CODE ABEND-TIMING.` FSEM0100.cbl:579 | | |
| U156 | JB | `jbRecord` | **`decimalParameters`** | 0.60 | The JB block passed on every decimal kernel call. | `CALL 'FSJBDEC' USING JB` FSEM0100.cbl:553 | | |
| U161 | POWER-BASE | `powerBaseRecord` | **`powerBaseValue`** | 0.55 | Field for POWER-BASE (1 plus the monthly rate); not an I/O record, so no Record suffix. | `MOVE JB-R TO POWER-BASE` FSEM0100.cbl:288 | | |
| U162 | POWER-VALUE | `powerValue` | **`compoundFactorValue`** | 0.65 | POWER-VALUE is (1 + monthly rate) raised to the months, produced by COMPOUND-FACTOR. | `4200-COMPOUND-FACTOR.` FSEM0100.cbl:322 | | |
| U348 | CEE3ABD | `cee3abd` | **`abendService`** | 0.60 | Field holding the CEE3ABD abend service the driver calls on severe errors. | `CALL 'CEE3ABD' USING DW-ABEND-CODE DW-ABEND-TIMING.` KZLE0100.cbl:433 | | |
| U350 | DW-DATE | `kzle0100DateRecord` | **`checkedDate`** | 0.45 | DW-DATE is the year/month/day validated by DATE-CHECK; that paragraph is never performed, so its role is unconfirmed. | `DW-MM > 0 AND DW-MM < 13 AND DW-DD > 0` KZLE0100.cbl:398 | | |
| U352 | FS-OUT | `outRecord` | **`serviceResponse`** | 0.50 | Field holding the service's response area (status, reject, result records). | `EVALUATE FO-STATUS` KZLE0100.cbl:233 | | |
| U353 | KZ-CASE-WORK | `caseWorkRecord` | **`loanCase`** | 0.55 | Field holding the per-loan case state passed to the service on every call. | `CALL 'FSEM0100' USING KZ-CASE-WORK FN-RUN-CONTROL` KZLE0100.cbl:230 | | |
| U389 | KZLE0100 | `sequence` | **`caseSequence`** | 0.60 | Field holding the state that tracks record order within each loan case. | `IF DW-FIRST = 'Y' OR KZ-EVENT-REC (1:8) NOT = DW-LOAN` KZLE0100.cbl:173 | | |
| U363 | 2105-NUMERIC-TESTS/parameter[0], DW-NUM- | `isRateNumeric` | **`isAnnualRateNumeric`** | 0.60 | DW-NUM-3 tests that the header rate has a sign and 15 digits; 'annualRate' matches the rate's name elsewhere. Sibling predicates isKpPrincipalNumeric etc. would (merged derived-role unit; per-member names needed) | `IF (KH-RATE (1:1) NOT = '+' AND KH-RATE (1:1) NOT = '-')` KZLE0100.cbl:331 | | |
| U376 | DW-SKIP | `reject` | **`(keep)`** | 0.60 | DW-SKIP marks the current loan as rejected so its remaining records are skipped; rejected()/reject() state that. (merged derived-role unit; per-member names needed) | `IF DW-SKIP NOT = 'Y'` KZLE0100.cbl:207 | | |

## Business data items (fields; their getters and setters follow) (79)

| Unit | COBOL | Generator | Thinker | Conf | Why | Evidence | Approve | Rename |
|---|---|---|---|---|---|---|---|---|
| U020 | FO-FUNC | `func` | **`serviceFunction`** | 0.60 | FO-FUNC tells the annuity calculation service which kind of call this is (header, offer, event, end of run). | `EVALUATE FO-FUNC` FSEM0100.cbl:52 | | |
| U023 | FO-REC | `rec` | **`resultRecord`** | 0.60 | FO-REC is the table of result-record images the service returns; the driver writes each one to the results file. | `WRITE RESULT-REC FROM FO-REC (DW-K)` KZLE0100.cbl:237 | | |
| U024 | FO-REJECT | `reject` | **`rejectRecord`** | 0.65 | FO-REJECT holds the reject result record built when the calculation fails; the driver writes it on status R. | `MOVE KZ-EVENT-OUT TO FO-REJECT` FSEM0100.cbl:575 | | |
| U025 | FO-STATUS | `status` | **`serviceStatus`** | 0.60 | FO-STATUS is the outcome of the service call (S success, R rejected); the driver abends on any other value. | `DISPLAY 'KZLE0100 ENTRY STATUS INVALID ' FO-STATUS` KZLE0100.cbl:259 | | |
| U031 | JB-A-Z | `aZ` | **`operandAExponent`** | 0.55 | JB-A-Z sits where the value groups keep their EXPONENT: a positive count of extra trailing zeros, negative when the value is a big-number handle. | `COMPUTE JB-N = W-NX + W-ZA` FSJBDEC.cbl:699 | | |
| U034 | JB-B-Z | `bZ` | **`operandBExponent`** | 0.55 | JB-B-Z is operand B's exponent (trailing-zero count, or big-number handle flag when negative), twin of JB-A-Z. | `IF JB-B-Z > 0` FSJBDEC.cbl:814 | | |
| U035 | JB-D | `d` | **`doubleValue`** | 0.25 | JB-D is never referenced; its fixed 13.5-digit picture suggests a binary-floating (double) value for an unimplemented op such as VD. Needs review. | `PIC S9(13)V9(5) COMP-3.` FSJBDP.cpy:18 | | |
| U037 | JB-N | `n` | **`integerResult`** | 0.60 | JB-N returns integer answers: the precision (digit count), the comparison result -1/0/1, or an exported limb. | `COMPUTE JB-N = W-NX + W-ZA` FSJBDEC.cbl:699 | | |
| U041 | JB-P | `p` | **`precisionOrScale`** | 0.55 | JB-P is a precision for divide-with-context (20 digits) but the target scale for divide-to-scale and set-scale (2), so both words are needed. | `MOVE 20 TO JB-P` FSEM0100.cbl:317 | | |
| U044 | JB-R-Z | `rZ` | **`resultExponent`** | 0.55 | JB-R-Z is the result's exponent (trailing-zero count, or -1 for a big-number handle). | `MOVE W-RZ TO JB-R-Z` FSJBDEC.cbl:401 | | |
| U047 | CASE-MONTHS | `caseMonths` | **`months`** | 0.60 | CASE-MONTHS is the loan duration in months taken from the header; it drives the payment and plan length. | `MOVE KH-MONTHS TO CASE-MONTHS` FSEM0100.cbl:76 | | |
| U049 | CASE-RATE | `caseRate` | **`annualRate`** | 0.60 | CASE-RATE becomes the annual rate value that is divided by 12 into the monthly rate. | `MOVE CASE-RATE TO CALC-RATE` FSEM0100.cbl:177 | | |
| U050 | CASE-YEARS | `caseYears` | **`years`** | 0.60 | CASE-YEARS is the loan duration in years; the SY event turns it into months. | `COMPUTE CALC-MONTHS = CASE-YEARS * 12` FSEM0100.cbl:158 | | |
| U057 | PLAN-REPAID | `planRepaid` | **`planPrincipalRepaid`** | 0.65 | PLAN-REPAID sums the principal portion of every installment, i.e. the principal repaid over the plan. | `ADD CURRENT-PRINCIPAL TO PLAN-REPAID (PLAN-SLOT)` FSEM0100.cbl:457 | | |
| U060 | ROW-PAYMENT | `payment` | **`installmentAmount`** | 0.65 | ROW-PAYMENT is the installment's total amount, interest plus principal portion. | `COMPUTE CURRENT-PAYMENT = CURRENT-INTEREST +` FSEM0100.cbl:435 | | |
| U061 | ROW-PRINCIPAL | `principal` | **`installmentPrincipal`** | 0.65 | ROW-PRINCIPAL is the principal portion repaid by the installment; zero marks an interest-only row. | `MOVE CURRENT-PRINCIPAL TO ROW-PRINCIPAL` FSEM0100.cbl:451 | | |
| U062 | TERM-MONTHS | `termMonths` | **`termsMonths`** | 0.60 | TERM-MONTHS is the duration stored with the validated loan terms (TERM-DATA, written by STORE-TERMS). | `MOVE CALC-MONTHS TO TERM-MONTHS (TERM-SLOT)` FSEM0100.cbl:210 | | |
| U063 | TERM-PRESENT | `termPresent` | **`termsPresent`** | 0.60 | TERM-PRESENT flags that loan terms are stored in the slot; later calculations fail without them. | `MOVE 'Y' TO TERM-PRESENT (TERM-SLOT)` FSEM0100.cbl:211 | | |
| U064 | TERM-PRINCIPAL | `termPrincipal` | **`termsPrincipal`** | 0.60 | TERM-PRINCIPAL is the loan terms' principal rounded to cents. | `COMPUTE TERM-PRINCIPAL (TERM-SLOT)` FSEM0100.cbl:205 | | |
| U065 | TERM-RATE | `termRate` | **`termsAnnualRate`** | 0.60 | TERM-RATE is the terms' annual rate rounded half-even to 12 decimals. | `COMPUTE TERM-RATE (TERM-SLOT) =` FSEM0100.cbl:231 | | |
| U069 | KA-EVENT-TYPE | `eventType` | **`amountEventType`** | 0.60 | KA-EVENT-TYPE echoes the event type in the amount variant; prefixed by variant because every variant repeats EVENT-TYPE in the same record. | `MOVE KE-TYPE TO KA-EVENT-TYPE` FSEM0100.cbl:466 | | |
| U073 | KC-EVENT-TYPE | `kcEventType` | **`offerCountEventType`** | 0.55 | KC-EVENT-TYPE echoes the event type in the offer-count variant. | `MOVE KE-TYPE TO KC-EVENT-TYPE` FSEM0100.cbl:535 | | |
| U074 | KO-NUM | `num` | **`resultNumber`** | 0.60 | KO-NUM numbers the result records produced for one input record (1, 2, ...). | `COMPUTE KO-NUM = FO-COUNT + 1` FSEM0100.cbl:542 | | |
| U079 | KX-EVENT-TYPE | `kxEventType` | **`rejectEventType`** | 0.65 | KX-EVENT-TYPE is the event type of the rejected record (blank for headers and offers). | `MOVE KE-TYPE TO KX-EVENT-TYPE` FSEM0100.cbl:568 | | |
| U080 | KX-REC-TYPE | `kxRecType` | **`rejectedRecordType`** | 0.65 | KX-REC-TYPE copies the type (H, P, E) of the input record that was rejected. | `MOVE KV-REC-TYPE TO KX-REC-TYPE` FSEM0100.cbl:566 | | |
| U081 | KX-SOURCE | `source` | **`rejectSource`** | 0.65 | KX-SOURCE names where the reject came from ('ENVELOPE' for the driver's format check, '?' from the service). | `MOVE 'ENVELOPE' TO KX-SOURCE` KZLE0100.cbl:379 | | |
| U089 | KR-PAYMENT-S | `paymentS` | **`installmentAmountScale`** | 0.65 | KR-PAYMENT-S is the scale of the installment amount. | `MOVE 2 TO KR-PAYMENT-S KR-INTEREST-S KR-PRINCIPAL-S` FSEM0100.cbl:520 | | |
| U090 | KR-PAYMENT-U | `paymentU` | **`installmentAmountUnscaled`** | 0.65 | KR-PAYMENT-U is the unscaled installment amount (interest plus principal portion). | `ROW-PAYMENT (PLAN-SLOT, OUTPUT-ROW) * 100` FSEM0100.cbl:514 | | |
| U091 | KR-PRINCIPAL-S | `krPrincipalS` | **`installmentPrincipalScale`** | 0.65 | KR-PRINCIPAL-S is the scale of the installment's principal portion. | `MOVE 2 TO KR-PAYMENT-S KR-INTEREST-S KR-PRINCIPAL-S` FSEM0100.cbl:520 | | |
| U092 | KR-PRINCIPAL-U | `krPrincipalU` | **`installmentPrincipalUnscaled`** | 0.65 | KR-PRINCIPAL-U is the unscaled principal portion of the installment. | `ROW-PRINCIPAL (PLAN-SLOT, OUTPUT-ROW) * 100` FSEM0100.cbl:518 | | |
| U094 | KS-DEBT-0-S | `debt0S` | **`openingDebtScale`** | 0.60 | KS-DEBT-0-S is the scale of the debt at month 0, the plan's opening debt. | `KS-TOT-PAID-S KS-DEBT-0-S` FSEM0100.cbl:507 | | |
| U095 | KS-DEBT-0-U | `debt0U` | **`openingDebtUnscaled`** | 0.60 | KS-DEBT-0-U is the unscaled debt at month 0, i.e. the plan principal. | `COMPUTE KS-DEBT-0-U = PLAN-PRINCIPAL (PLAN-SLOT) * 100` FSEM0100.cbl:505 | | |
| U096 | KS-DURATION | `duration` | **`planMonths`** | 0.60 | KS-DURATION reports PLAN-MONTHS, so it takes the same word as the plan field. | `MOVE PLAN-MONTHS (PLAN-SLOT) TO KS-DURATION KS-LAST-MONTH` FSEM0100.cbl:500 | | |
| U097 | KS-EVENT-TYPE | `ksEventType` | **`summaryEventType`** | 0.60 | KS-EVENT-TYPE echoes the event type in the summary variant. | `MOVE KE-TYPE TO KS-EVENT-TYPE` FSEM0100.cbl:499 | | |
| U098 | KS-LAST-MONTH | `lastMonth` | **`lastInstallmentMonth`** | 0.60 | KS-LAST-MONTH is the month of the plan's last installment. | `MOVE PLAN-MONTHS (PLAN-SLOT) TO KS-DURATION KS-LAST-MONTH` FSEM0100.cbl:500 | | |
| U103 | KS-TOT-PRINCIPAL-S | `totPrincipalS` | **`totalPrincipalRepaidScale`** | 0.65 | KS-TOT-PRINCIPAL-S is the scale of the total principal repaid (PLAN-REPAID). | `MOVE 2 TO KS-TOT-INTEREST-S KS-TOT-PRINCIPAL-S` FSEM0100.cbl:506 | | |
| U104 | KS-TOT-PRINCIPAL-U | `totPrincipalU` | **`totalPrincipalRepaidUnscaled`** | 0.65 | KS-TOT-PRINCIPAL-U reports PLAN-REPAID, the principal repaid over the plan. | `COMPUTE KS-TOT-PRINCIPAL-U = PLAN-REPAID (PLAN-SLOT) * 100` FSEM0100.cbl:503 | | |
| U105 | KT-EVENT-TYPE | `ktEventType` | **`termsEventType`** | 0.60 | KT-EVENT-TYPE echoes the event type in the terms variant. | `MOVE KE-TYPE TO KT-EVENT-TYPE` FSEM0100.cbl:480 | | |
| U109 | KT-MONTHS | `months` | **`termsMonths`** | 0.60 | KT-MONTHS is the duration of the loan terms being reported; same word as the stored terms field. | `MOVE CALC-MONTHS TO KT-MONTHS` FSEM0100.cbl:487 | | |
| U110 | KT-PRINCIPAL-S | `principalS` | **`termsPrincipalScale`** | 0.65 | KT-PRINCIPAL-S is the scale (2) of the terms' principal. | `MOVE 2 TO KT-PRINCIPAL-S` FSEM0100.cbl:483 | | |
| U111 | KT-PRINCIPAL-U | `principalU` | **`termsPrincipalUnscaled`** | 0.65 | KT-PRINCIPAL-U is the unscaled principal of the stored loan terms. | `TERM-PRINCIPAL (TERM-SLOT) * 100` FSEM0100.cbl:482 | | |
| U112 | KT-RATE-S | `rateS` | **`termsAnnualRateScale`** | 0.60 | KT-RATE-S is the scale (12) of the terms' annual rate. | `MOVE 12 TO KT-RATE-S` FSEM0100.cbl:486 | | |
| U113 | KT-RATE-U | `rateU` | **`termsAnnualRateUnscaled`** | 0.60 | KT-RATE-U is the unscaled annual rate of the stored terms. | `COMPUTE KT-RATE-U = TERM-RATE (TERM-SLOT) *` FSEM0100.cbl:484 | | |
| U117 | KE-MONTH | `month` | **`queryMonth`** | 0.60 | KE-MONTH is the month the QI and QR query events ask about (installment row or remaining debt). | `MOVE KE-MONTH TO OUTPUT-ROW` FSEM0100.cbl:131 | | |
| U122 | KH-FREE | `free` | **`useFreeRate`** | 0.45 | KH-FREE (Y/N) selects the free rate instead of the normal rate for the case; named for that effect because 'free' is not explained. | `IF KH-FREE = 'Y'` FSEM0100.cbl:71 | | |
| U125 | KH-RATE | `rate` | **`annualRate`** | 0.60 | KH-RATE is the loan's annual rate; the service divides it by 12 for the monthly rate. | `MOVE KH-RATE TO CASE-RATE` FSEM0100.cbl:74 | | |
| U127 | KP-MONTHS | `kpMonths` | **`offerMonths`** | 0.65 | KP-MONTHS is the duration of an offer record (KV-OFFER variant). | `MOVE KP-MONTHS TO CALC-MONTHS` FSEM0100.cbl:83 | | |
| U128 | KP-PRINCIPAL | `kpPrincipal` | **`offerPrincipal`** | 0.65 | KP-PRINCIPAL is the principal of an offer, checked by CHECK-OFFER. | `MOVE KP-PRINCIPAL TO CALC-PRINCIPAL` FSEM0100.cbl:81 | | |
| U129 | KP-RATE | `kpRate` | **`offerAnnualRate`** | 0.60 | KP-RATE is the offer's annual rate. | `MOVE KP-RATE TO CALC-RATE` FSEM0100.cbl:82 | | |
| U135 | ANNUAL-RATE-EXPONENT | `annualRateExponent` | **`exponent`** | 0.50 | The type is shared by the annual and monthly rate values, so its fields take the generic decimal-part names; EXPONENT is the copybook's own word. | `05 ANNUAL-RATE-EXPONENT PIC S9(4) COMP.` KZNUMWRK.cpy:8 | | |
| U136 | ANNUAL-RATE-SCALE | `annualRateScale` | **`scale`** | 0.50 | Scale of a rate value; generic because the type holds both the annual and the monthly rate. | `MOVE CALC-RATE-SCALE TO ANNUAL-RATE-SCALE` FSEM0100.cbl:271 | | |
| U137 | ANNUAL-RATE-UNSCALED | `annualRateUnscaled` | **`unscaledValue`** | 0.50 | Unscaled value of a rate; generic because the type holds both the annual and the monthly rate. | `COMPUTE ANNUAL-RATE-UNSCALED =` FSEM0100.cbl:259 | | |
| U139 | PAYMENT-EXPONENT | `paymentExponent` | **`exponent`** | 0.50 | The type is shared by the principal, payment, numerator, power and result values, so the field takes the generic name. | `05 PAYMENT-EXPONENT PIC S9(4) COMP.` KZNUMWRK.cpy:28 | | |
| U140 | PAYMENT-SCALE | `paymentScale` | **`scale`** | 0.50 | Scale of a shared decimal value (payment, principal, result, ...). | `MOVE 2 TO RESULT-SCALE.` FSEM0100.cbl:531 | | |
| U141 | PAYMENT-UNSCALED | `paymentUnscaled` | **`unscaledValue`** | 0.50 | Unscaled value of a shared decimal value (payment, principal, result, ...). | `COMPUTE RESULT-UNSCALED = TEMP-MONEY * 100` FSEM0100.cbl:530 | | |
| U143 | POWER-BASE-EXPONENT | `powerBaseExponent` | **`exponent`** | 0.50 | Exponent part of the power base, same decimal-part naming as the other value types. | `05 POWER-BASE-EXPONENT PIC S9(4) COMP.` KZNUMWRK.cpy:16 | | |
| U144 | POWER-BASE-SCALE | `powerBaseScale` | **`scale`** | 0.50 | Scale part of the power base value. | `05 POWER-BASE-SCALE PIC S9(9) COMP.` KZNUMWRK.cpy:15 | | |
| U145 | POWER-BASE-UNSCALED | `powerBaseUnscaled` | **`unscaledValue`** | 0.50 | Unscaled part of the power base value. | `05 POWER-BASE-UNSCALED PIC S9(31) COMP-3.` KZNUMWRK.cpy:14 | | |
| U165 | CALC-MONTHS | `calcMonths` | **`calculationMonths`** | 0.65 | CALC-MONTHS is the duration the current calculation runs on (from the case, an offer or stored terms). CALC expanded. | `MOVE CASE-MONTHS TO CALC-MONTHS` FSEM0100.cbl:178 | | |
| U167 | CALC-PRINCIPAL | `calcPrincipal` | **`calculationPrincipal`** | 0.65 | CALC-PRINCIPAL is the principal the current calculation uses. | `MOVE CASE-PRINCIPAL TO CALC-PRINCIPAL` FSEM0100.cbl:176 | | |
| U166 | CALC-PRINCIPAL-SCALE | `calcPrincipalScale` | **`calculationPrincipalScale`** | 0.65 | CALC-PRINCIPAL-SCALE is the scale of the calculation principal (4 raw, 2 from terms). | `MOVE 4 TO CALC-PRINCIPAL-SCALE` FSEM0100.cbl:179 | | |
| U169 | CALC-RATE | `calcRate` | **`calculationAnnualRate`** | 0.60 | CALC-RATE is the annual rate the current calculation uses. | `MOVE CASE-RATE TO CALC-RATE` FSEM0100.cbl:177 | | |
| U168 | CALC-RATE-SCALE | `calcRateScale` | **`calculationAnnualRateScale`** | 0.60 | CALC-RATE-SCALE is the scale of the calculation's annual rate (13 raw, 12 from terms). | `MOVE CALC-RATE-SCALE TO ANNUAL-RATE-SCALE` FSEM0100.cbl:271 | | |
| U170 | CURRENT-BALANCE | `currentBalance` | **`remainingDebt`** | 0.65 | CURRENT-BALANCE starts at the principal, is reduced by each principal portion and is stored as ROW-DEBT; same concept as the row's debt. | `SUBTRACT CURRENT-PRINCIPAL FROM CURRENT-BALANCE` FSEM0100.cbl:439 | | |
| U171 | CURRENT-INTEREST | `currentInterest` | **`installmentInterest`** | 0.65 | CURRENT-INTEREST is the interest of the installment being built. | `MOVE CURRENT-INTEREST TO ROW-INTEREST` FSEM0100.cbl:449 | | |
| U172 | CURRENT-PAYMENT | `currentPayment` | **`installmentAmount`** | 0.65 | CURRENT-PAYMENT is interest plus principal portion of the installment being built. | `COMPUTE CURRENT-PAYMENT = CURRENT-INTEREST +` FSEM0100.cbl:435 | | |
| U173 | CURRENT-PRINCIPAL | `currentPrincipal` | **`installmentPrincipal`** | 0.65 | CURRENT-PRINCIPAL is the principal portion of the installment being built. | `MOVE CURRENT-PRINCIPAL TO ROW-PRINCIPAL` FSEM0100.cbl:451 | | |
| U177 | OUTPUT-ROW | `outputRow` | **`outputMonth`** | 0.55 | OUTPUT-ROW is the month whose plan row is being reported. | `MOVE OUTPUT-ROW TO KR-MONTH` FSEM0100.cbl:512 | | |
| U179 | POWER-BIT | `powerBit` | **`exponentBit`** | 0.50 | POWER-BIT walks the binary digits of the month exponent in square-and-multiply exponentiation. | `PERFORM UNTIL POWER-BIT * 2 > CALC-MONTHS` FSEM0100.cbl:330 | | |
| U180 | POWER-DIGITS | `powerDigits` | **`exponentDigits`** | 0.50 | POWER-DIGITS counts the decimal digits of the month exponent to size the working precision. | `COMPUTE WORK-PRECISION = 21 + POWER-DIGITS` FSEM0100.cbl:329 | | |
| U181 | POWER-QUOTIENT | `powerQuotient` | **`exponentQuotient`** | 0.40 | POWER-QUOTIENT is a copy of the exponent divided by 10 repeatedly to count its digits. | `DIVIDE 10 INTO POWER-QUOTIENT` FSEM0100.cbl:326 | | |
| U182 | POWER-REMAINDER | `powerRemainder` | **`remainingExponent`** | 0.50 | POWER-REMAINDER is the part of the month exponent not yet multiplied in. | `SUBTRACT POWER-BIT FROM POWER-REMAINDER` FSEM0100.cbl:337 | | |
| U186 | RATE-TAIL | `rateTail` | **`droppedRateDigit`** | 0.50 | RATE-TAIL is the 13th decimal digit removed when the rate is rounded to 12 decimals. | `REMAINDER RATE-TAIL` FSEM0100.cbl:218 | | |
| U188 | ROUND-PRECISION | `roundPrecision` | **`roundingPrecision`** | 0.55 | ROUND-PRECISION is the number of significant digits the power value is rounded to (working precision, finally 20). | `MOVE 20 TO ROUND-PRECISION` FSEM0100.cbl:356 | | |
| U189 | ROW-NUMBER | `rowNumber` | **`installmentNumber`** | 0.60 | ROW-NUMBER counts the installments 1 to the number of months while the plan is built. | `PERFORM VARYING ROW-NUMBER FROM 1 BY 1` FSEM0100.cbl:384 | | |
| U190 | SIZE-ERROR-CODE | `sizeErrorCode` | **`capacityAbendCode`** | 0.60 | SIZE-ERROR-CODE (9) is the abend code passed to CEE3ABD on a calculation capacity error. | `05 SIZE-ERROR-CODE PIC S9(9) COMP VALUE 9.` FSEM0100.cbl:35 | | |
| U191 | TEMP-MONEY | `tempMoney` | **`moneyAmount`** | 0.45 | TEMP-MONEY stages a cents amount (here the queried remaining debt) before it is converted to the result value. | `MOVE PLAN-PRINCIPAL (1) TO TEMP-MONEY` FSEM0100.cbl:140 | | |
| U192 | TERM-SLOT | `termSlot` | **`termsSlot`** | 0.60 | TERM-SLOT selects which stored loan terms are used (1 months-based, 2 years-based). | `IF TERM-PRESENT (TERM-SLOT) NOT = 'Y'` FSEM0100.cbl:182 | | |
| U193 | WORK-PRECISION | `workPrecision` | **`workingPrecision`** | 0.60 | WORK-PRECISION is the extra-digit precision used for intermediate products of the exponentiation. | `COMPUTE WORK-PRECISION = 21 + POWER-DIGITS` FSEM0100.cbl:329 | | |

## Everything else (119 units, counts only)

Mostly scratch storage of the decimal library (FSJBDEC), named plainly at low confidence on purpose (caution C001).

- FNDKRND / code-enum: 2
- FNDKRND / data: 2
- FSEM0100 / code-enum: 4
- FSJBDEC / code-enum: 1
- FSJBDEC / data: 110
