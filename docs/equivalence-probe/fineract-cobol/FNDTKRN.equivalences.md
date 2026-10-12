# Verified library equivalences: FNDTKRN

These facts were established by the pipeline, not guessed. FNDTKRN's CALLs, 21,149 traced from the faithful program over 31 oracle scenario(s), and up to 100,000 generated CALLs per group (inputs drawn from the traced ones, run through the faithful program), were replayed through a catalog of JDK operations. Each statement below reproduced every byte of every checked CALL (all output fields and the return code) where its condition holds. Implement these groups with the named JDK operation (BigDecimal, MathContext, RoundingMode, LocalDate, ChronoUnit) and the stated field mapping; do not re-derive the COBOL digit or calendar algorithm. A decimal pair is a BigDecimal: new BigDecimal(COEF, -EXP) has the value COEF × 10^EXP.

## DT-FUNCTION = 'BT' (DT-FN-BETWEEN)

`r = unit.between(dtDayDate, dtDay2Date)`

- where dtDayDate = LocalDate.of(1600, 12, 31).plusDays(DT-DAY) (DT-DAY is a COBOL INTEGER-OF-DATE (day 1 = 1601-01-01)); dtDay2Date = LocalDate.of(1600, 12, 31).plusDays(DT-DAY2) (DT-DAY2 is a COBOL INTEGER-OF-DATE (day 1 = 1601-01-01)); unit by DT-UNIT: 'D' DAYS, 'M' MONTHS, 'W' WEEKS, 'Y' YEARS
- result: DT-RESULT = r
- also: DT-RC = 0
- checked: 2,247 traced CALL(s) and 100,000 generated CALL(s) of the faithful FNDTKRN: every byte identical.
- generated inputs covered: DT-DAY 154160..154833; DT-DAY2 154160..155346; DT-UNIT 'D' 'M' 'W' 'Y'

## DT-FUNCTION = 'DM' (DT-FN-DAY-OF-MONTH)

`r = dtDayDate.getDayOfMonth()`

- where dtDayDate = LocalDate.of(1600, 12, 31).plusDays(DT-DAY) (DT-DAY is a COBOL INTEGER-OF-DATE (day 1 = 1601-01-01))
- result: DT-RESULT = r
- also: DT-RC = 0
- checked: 2,802 traced CALL(s) and 100,000 generated CALL(s) of the faithful FNDTKRN: every byte identical.
- generated inputs covered: DT-DAY 153705..155346

## DT-FUNCTION = 'DN' (DT-FN-DAY-OF-DATE)

`r = dtYyyymmddDate`

- where dtYyyymmddDate = LocalDate.parse(DT-YYYYMMDD as 8 digits, DateTimeFormatter.BASIC_ISO_DATE)
- result: DT-RESULT = ChronoUnit.DAYS.between(LocalDate.of(1600, 12, 31), r) (a COBOL INTEGER-OF-DATE (day 1 = 1601-01-01))
- also: DT-RC = 0
- when DT-YYYYMMDD does not hold a valid value: DT-RESULT = 0; DT-RC = 0; every other field unchanged
- checked: 190 traced CALL(s) and 100,000 generated CALL(s) of the faithful FNDTKRN: every byte identical.
- generated inputs covered: DT-YYYYMMDD 20230128..20241115

## DT-FUNCTION = 'LM' (DT-FN-LENGTH-OF-MONTH)

`r = dtDayDate.lengthOfMonth()`

- where dtDayDate = LocalDate.of(1600, 12, 31).plusDays(DT-DAY) (DT-DAY is a COBOL INTEGER-OF-DATE (day 1 = 1601-01-01))
- result: DT-RESULT = r
- also: DT-RC = 0
- checked: 763 traced CALL(s) and 100,000 generated CALL(s) of the faithful FNDTKRN: every byte identical.
- generated inputs covered: DT-DAY 153797..154894

## DT-FUNCTION = 'LY' (DT-FN-LENGTH-OF-YEAR)

`r = dtDayDate.lengthOfYear()`

- where dtDayDate = LocalDate.of(1600, 12, 31).plusDays(DT-DAY) (DT-DAY is a COBOL INTEGER-OF-DATE (day 1 = 1601-01-01))
- result: DT-RESULT = r
- also: DT-RC = 0
- checked: 1,049 traced CALL(s) and 100,000 generated CALL(s) of the faithful FNDTKRN: every byte identical.
- generated inputs covered: DT-DAY 153705..156324

## DT-FUNCTION = 'PL' (DT-FN-PLUS)

`r = dtDayDate.plus(dtN, unit)`

- where dtN = DT-N; dtDayDate = LocalDate.of(1600, 12, 31).plusDays(DT-DAY) (DT-DAY is a COBOL INTEGER-OF-DATE (day 1 = 1601-01-01)); unit by DT-UNIT: 'D' DAYS, 'M' MONTHS, 'W' WEEKS, 'Y' YEARS
- result: DT-RESULT = ChronoUnit.DAYS.between(LocalDate.of(1600, 12, 31), r) (a COBOL INTEGER-OF-DATE (day 1 = 1601-01-01))
- also: DT-RC = 0
- checked: 6,192 traced CALL(s) and 100,000 generated CALL(s) of the faithful FNDTKRN: every byte identical.
- generated inputs covered: DT-DAY 153736..158516; DT-N -365..390; DT-UNIT 'D' 'M' 'W' 'Y'

## DT-FUNCTION = 'WD' (DT-FN-WITH-DAY-OF-MONTH)

`r = dtDayDate.withDayOfMonth(dtN)`

- where dtN = DT-N; dtDayDate = LocalDate.of(1600, 12, 31).plusDays(DT-DAY) (DT-DAY is a COBOL INTEGER-OF-DATE (day 1 = 1601-01-01))
- result: DT-RESULT = ChronoUnit.DAYS.between(LocalDate.of(1600, 12, 31), r) (a COBOL INTEGER-OF-DATE (day 1 = 1601-01-01))
- also: DT-RC = 0
- when the date operation throws DateTimeException: DT-RESULT = 0; DT-RC = 0; every other field unchanged
- checked: 18 traced CALL(s) and 100,000 generated CALL(s) of the faithful FNDTKRN: every byte identical.
- generated inputs covered: DT-DAY 153887..154983; DT-N 0..31

## DT-FUNCTION = 'YE' (DT-FN-YEAR-END)

`r = LocalDate.of(dtN, 12, 31)`

- where dtN = DT-N
- result: DT-RESULT = ChronoUnit.DAYS.between(LocalDate.of(1600, 12, 31), r) (a COBOL INTEGER-OF-DATE (day 1 = 1601-01-01))
- also: DT-RC = 0
- when the result does not fit DT-RESULT: DT-RC = 20; DT-RESULT unchanged; every other field unchanged
- checked: 400 traced CALL(s) and 100,000 generated CALL(s) of the faithful FNDTKRN: every byte identical (its inputs took only 12 distinct combinations, the values the traces show).
- generated inputs covered: DT-N 0..2028

## DT-FUNCTION = 'YM' (DT-FN-DATE-OF-DAY)

`r = dtDayDate`

- where dtDayDate = LocalDate.of(1600, 12, 31).plusDays(DT-DAY) (DT-DAY is a COBOL INTEGER-OF-DATE (day 1 = 1601-01-01))
- result: DT-YYYYMMDD = r as yyyyMMdd (r.format(DateTimeFormatter.BASIC_ISO_DATE))
- also: DT-RC = 0
- checked: 1,091 traced CALL(s) and 100,000 generated CALL(s) of the faithful FNDTKRN: every byte identical.
- generated inputs covered: DT-DAY 153736..155927

## DT-FUNCTION = 'YR' (DT-FN-YEAR)

`r = dtDayDate.getYear()`

- where dtDayDate = LocalDate.of(1600, 12, 31).plusDays(DT-DAY) (DT-DAY is a COBOL INTEGER-OF-DATE (day 1 = 1601-01-01))
- result: DT-RESULT = r
- also: DT-RC = 0
- checked: 6,397 traced CALL(s) and 100,000 generated CALL(s) of the faithful FNDTKRN: every byte identical.
- generated inputs covered: DT-DAY 153705..156292
