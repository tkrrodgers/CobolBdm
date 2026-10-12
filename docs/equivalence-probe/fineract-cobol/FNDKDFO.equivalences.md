# Verified library equivalences: FNDKDFO

These facts were established by the pipeline, not guessed. FNDKDFO's CALLs, 22,485 traced from the faithful program over 31 oracle scenario(s), and up to 100,000 generated CALLs per group (inputs drawn from the traced ones, run through the faithful program), were replayed through a catalog of JDK operations. Each statement below reproduced every byte of every checked CALL (all output fields and the return code) where its condition holds. Implement these groups with the named JDK operation (BigDecimal, MathContext, RoundingMode, LocalDate, ChronoUnit) and the stated field mapping; do not re-derive the COBOL digit or calendar algorithm. A decimal pair is a BigDecimal: new BigDecimal(COEF, -EXP) has the value COEF × 10^EXP.

## DF-OP = 'A' (DF-OP-ADD)

`r = dfA.add(dfB, new MathContext(dfPrec, mode))`

- where dfA = new BigDecimal(DF-A-COEF, -DF-A-EXP) (the value DF-A-COEF × 10^DF-A-EXP); dfB = new BigDecimal(DF-B-COEF, -DF-B-EXP) (the value DF-B-COEF × 10^DF-B-EXP); dfPrec = DF-PREC; mode by DF-MODE: 'C' CEILING, 'D' DOWN, 'E' HALF_EVEN, 'F' FLOOR, 'H' HALF_UP, 'N' HALF_DOWN, 'U' UP
- result: r = (the result).stripTrailingZeros(); DF-R-COEF = r.unscaledValue(), DF-R-EXP = -r.scale() (a zero result: DF-R-COEF = 0, DF-R-EXP = 0)
- also: when DF-A-EXP < DF-B-EXP: DF-A-COEF = DF-B-COEF (the value the caller passed); otherwise DF-A-COEF unchanged; when DF-A-EXP < DF-B-EXP: DF-A-EXP = DF-B-EXP (the value the caller passed); otherwise DF-A-EXP unchanged; when DF-A-EXP < DF-B-EXP: DF-B-COEF = DF-A-COEF (the value the caller passed); otherwise DF-B-COEF unchanged; when DF-A-EXP < DF-B-EXP: DF-B-EXP = DF-A-EXP (the value the caller passed); otherwise DF-B-EXP unchanged; DF-RC = 0
- checked: 296 traced CALL(s) and 100,000 generated CALL(s) of the faithful FNDKDFO: every byte identical.
- generated inputs covered: DF-A-EXP -14..0; DF-B-EXP -14..0; DF-PREC 0..12; dfA digits 1..12; dfB digits 1..12; DF-MODE 'C' 'D' 'E' 'F' 'H' 'N' 'U'

## DF-OP = 'M' (DF-OP-MULTIPLY)

`r = dfA.multiply(dfB, new MathContext(dfPrec, mode))`

- where dfA = new BigDecimal(DF-A-COEF, -DF-A-EXP) (the value DF-A-COEF × 10^DF-A-EXP); dfB = new BigDecimal(DF-B-COEF, -DF-B-EXP) (the value DF-B-COEF × 10^DF-B-EXP); dfPrec = DF-PREC; mode by DF-MODE: 'C' CEILING, 'D' DOWN, 'E' HALF_EVEN, 'F' FLOOR, 'H' HALF_UP, 'N' HALF_DOWN, 'U' UP
- result: r = (the result).stripTrailingZeros(); DF-R-COEF = r.unscaledValue(), DF-R-EXP = -r.scale() (a zero result: DF-R-COEF = 0, DF-R-EXP = 0)
- also: DF-RC = 0
- checked: 9,166 traced CALL(s) and 100,000 generated CALL(s) of the faithful FNDKDFO: every byte identical.
- generated inputs covered: DF-A-EXP -18..9; DF-B-EXP -14..9; DF-PREC 0..12; dfA digits 1..12; dfB digits 1..13; DF-MODE 'C' 'D' 'E' 'F' 'H' 'N' 'U'

## DF-OP = 'N' (DF-OP-NORMALIZE)

`r = dfA`

- where dfA = new BigDecimal(DF-A-COEF, -DF-A-EXP) (the value DF-A-COEF × 10^DF-A-EXP)
- result: r = (the result).stripTrailingZeros(); DF-R-COEF = r.unscaledValue(), DF-R-EXP = -r.scale() (a zero result: DF-R-COEF = 0, DF-R-EXP = 0)
- also: DF-RC = 0
- checked: 6,451 traced CALL(s) and 100,000 generated CALL(s) of the faithful FNDKDFO: every byte identical.
- generated inputs covered: DF-A-EXP -12..0; dfA digits 1..14 (both signs)

## DF-OP = 'V' (DF-OP-DIVIDE)

`r = dfA.divide(dfB, new MathContext(dfPrec, mode))`

- where dfA = new BigDecimal(DF-A-COEF, -DF-A-EXP) (the value DF-A-COEF × 10^DF-A-EXP); dfB = new BigDecimal(DF-B-COEF, -DF-B-EXP) (the value DF-B-COEF × 10^DF-B-EXP); dfPrec = DF-PREC; mode by DF-MODE: 'C' CEILING, 'D' DOWN, 'E' HALF_EVEN, 'F' FLOOR, 'H' HALF_UP, 'N' HALF_DOWN, 'U' UP
- result: r = (the result).stripTrailingZeros(); DF-R-COEF = r.unscaledValue(), DF-R-EXP = -r.scale() (a zero result: DF-R-COEF = 0, DF-R-EXP = 0)
- also: DF-RC = 0
- when the operation throws ArithmeticException: Division by zero: when DF-PREC = 0: DF-RC = 12; otherwise DF-RC = 16; DF-R-COEF, DF-R-EXP unchanged; every other field unchanged
- when the operation throws ArithmeticException: Non-terminating decimal expansion; no exact representable decimal result.: DF-RC = 12; DF-R-COEF, DF-R-EXP unchanged; every other field unchanged
- checked: 6,572 traced CALL(s) and 100,000 generated CALL(s) of the faithful FNDKDFO: identical on 99,312; the other 688 lie where it does not hold, below.
- where it does not hold, when DF-PREC = 0: DF-RC = 12; every other field unchanged. A contract checked on 1,688 CALL(s) in that region (0 traced, 1,688 generated): every byte identical.
- generated inputs covered: DF-A-EXP -18..7; DF-B-EXP -11..0; DF-PREC 0..12; dfA digits 1..12 (both signs); dfB digits 1..12; DF-MODE 'C' 'D' 'E' 'F' 'H' 'N' 'U'
