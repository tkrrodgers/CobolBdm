# Verified library equivalences: FNDKRND

These facts were established by the pipeline, not guessed. FNDKRND's CALLs, 21,332 traced from the faithful program over 31 oracle scenario(s), and up to 100,000 generated CALLs per group (inputs drawn from the traced ones, run through the faithful program), were replayed through a catalog of JDK operations. Each statement below reproduced every byte of every checked CALL (all output fields and the return code) where its condition holds. Implement these groups with the named JDK operation (BigDecimal, MathContext, RoundingMode, LocalDate, ChronoUnit) and the stated field mapping; do not re-derive the COBOL digit or calendar algorithm. A decimal pair is a BigDecimal: new BigDecimal(COEF, -EXP) has the value COEF × 10^EXP.

## DK-FUNCTION = 'D' (DK-FN-DECIMALS)

`r = dkValue.setScale(-dkParm, mode)`

- where dkValue = DK-VALUE (a BigDecimal of scale 0); dkParm = DK-PARM; mode by DK-MODE: 'C' CEILING, 'D' DOWN, 'E' HALF_EVEN, 'F' FLOOR, 'H' HALF_UP, 'N' HALF_DOWN, 'U' UP
- result: DK-VALUE = r
- also: DK-RC = 0
- checked: 2,931 traced CALL(s) and 100,000 generated CALL(s) of the faithful FNDKRND: every byte identical.
- generated inputs covered: DK-PARM 0..12; dkValue digits 1..13 (both signs); DK-MODE 'C' 'D' 'E' 'F' 'H' 'N' 'U'

## DK-FUNCTION = 'P' (DK-FN-SHORTEN) and DK-STICKY = 'N'

`r = dkValue.round(new MathContext(dkParm, mode))`

- where dkValue = DK-VALUE (a BigDecimal of scale 0); dkParm = DK-PARM; mode by DK-MODE: 'C' CEILING, 'D' DOWN, 'E' HALF_EVEN, 'F' FLOOR, 'H' HALF_UP, 'N' HALF_DOWN, 'U' UP
- result: DK-SHIFT = max(0, dkValue.precision() - dkParm) (the digits dropped), DK-VALUE = r.movePointLeft(DK-SHIFT) (the digits kept: r = DK-VALUE × 10^DK-SHIFT)
- also: DK-RC = 0
- when the result does not fit DK-VALUE, DK-SHIFT: DK-VALUE = 0; DK-RC = 0; DK-SHIFT = 24; every other field unchanged
- checked: 14,427 traced CALL(s) and 100,000 generated CALL(s) of the faithful FNDKRND: identical on 96,360; the other 3,640 lie where it does not hold, below.
- where it does not hold, when DK-PARM = 0 and magnitude(dkValue) >= 0: FNDKRND does something no single relation per field describes (DK-VALUE, DK-SHIFT vary); the statement does not hold there (3,000 CALLs checked).
- generated inputs covered: DK-PARM 0..12; dkValue digits 1..24 (both signs); DK-MODE 'C' 'D' 'E' 'F' 'H' 'N' 'U'

## DK-FUNCTION = 'P' (DK-FN-SHORTEN) and DK-STICKY = 'Y'

`r = dkValue.add(BigDecimal.valueOf(dkValue.signum() < 0 ? -1 : 1, dkValue.scale() + 1)).round(new MathContext(dkParm, mode))`

- where dkValue = DK-VALUE (a BigDecimal of scale 0); dkParm = DK-PARM; dkValue.add(BigDecimal.valueOf(dkValue.signum() < 0 ? -1 : 1, dkValue.scale() + 1)) is dkValue with one more nonzero digit after its last: the value dkValue stands for has further nonzero digits (a sticky digit; any digit 1-9 gives the same result); mode by DK-MODE: 'C' CEILING, 'D' DOWN, 'E' HALF_EVEN, 'F' FLOOR, 'H' HALF_UP, 'N' HALF_DOWN, 'U' UP
- result: DK-SHIFT = max(0, dkValue.precision() - dkParm) (the digits dropped), DK-VALUE = r.movePointLeft(DK-SHIFT) (the digits kept: r = DK-VALUE × 10^DK-SHIFT)
- also: DK-STICKY = 'N'; DK-RC = 0
- when the result does not fit DK-VALUE, DK-SHIFT: when DK-PARM = 0: DK-STICKY = 'N'; otherwise DK-STICKY unchanged; when DK-PARM = 0: DK-RC = 0; otherwise DK-RC = 8; DK-SHIFT = 0; DK-VALUE unchanged; every other field unchanged
- checked: 3,974 traced CALL(s) and 100,000 generated CALL(s) of the faithful FNDKRND: identical on 90,378; the other 9,622 lie where it does not hold, below.
- where it does not hold, when DK-PARM - digits(dkValue) = 0: DK-RC = 8; DK-SHIFT = 0; every other field unchanged. A contract checked on 2,224 CALL(s) in that region (0 traced, 2,224 generated): every byte identical.
- otherwise, where it does not hold, when DK-PARM = 0: FNDKRND does something no single relation per field describes (DK-VALUE, DK-SHIFT vary); the statement does not hold there (1,776 CALLs checked).
- generated inputs covered: DK-PARM 0..12; dkValue digits 1..14 (both signs); DK-MODE 'C' 'D' 'E' 'F' 'H' 'N' 'U'

## DK-FUNCTION = 'S' (DK-FN-SIGNIFICANT) and DK-STICKY = 'N'

`r = dkValue.round(new MathContext(dkParm, mode))`

- where dkValue = DK-VALUE (a BigDecimal of scale 0); dkParm = DK-PARM; mode by DK-MODE: 'C' CEILING, 'D' DOWN, 'E' HALF_EVEN, 'F' FLOOR, 'H' HALF_UP, 'N' HALF_DOWN, 'U' UP
- result: DK-VALUE = r
- also: DK-RC = 0; DK-SHIFT = 0
- checked: 0 traced CALL(s) and 100,000 generated CALL(s) of the faithful FNDKRND: identical on 96,350; the other 3,650 lie where it does not hold, below.
- where it does not hold, when DK-PARM = 0 and magnitude(dkValue) >= 0: FNDKRND does something no single relation per field describes (DK-VALUE varies); the statement does not hold there (3,000 CALLs checked).
- generated inputs covered: DK-PARM 0..12; dkValue digits 1..24 (both signs); DK-MODE 'C' 'D' 'E' 'F' 'H' 'N' 'U'

## DK-FUNCTION = 'S' (DK-FN-SIGNIFICANT) and DK-STICKY = 'Y'

`r = dkValue.add(BigDecimal.valueOf(dkValue.signum() < 0 ? -1 : 1, dkValue.scale() + 1)).round(new MathContext(dkParm, mode))`

- where dkValue = DK-VALUE (a BigDecimal of scale 0); dkParm = DK-PARM; dkValue.add(BigDecimal.valueOf(dkValue.signum() < 0 ? -1 : 1, dkValue.scale() + 1)) is dkValue with one more nonzero digit after its last: the value dkValue stands for has further nonzero digits (a sticky digit; any digit 1-9 gives the same result); mode by DK-MODE: 'C' CEILING, 'D' DOWN, 'E' HALF_EVEN, 'F' FLOOR, 'H' HALF_UP, 'N' HALF_DOWN, 'U' UP
- result: DK-VALUE = r
- also: DK-STICKY = 'N'; DK-SHIFT = 0
- when the result does not fit DK-VALUE: when DK-PARM = 0: DK-STICKY = 'N'; otherwise DK-STICKY unchanged; when DK-PARM = 0: DK-RC = 0; otherwise DK-RC = 8; DK-SHIFT = 0; DK-VALUE unchanged; every other field unchanged
- checked: 0 traced CALL(s) and 100,000 generated CALL(s) of the faithful FNDKRND: identical on 90,278; the other 9,722 lie where it does not hold, below.
- where it does not hold, when DK-PARM - magnitude(dkValue) = 1: DK-RC = 8; DK-SHIFT = 0; every other field unchanged. A contract checked on 2,208 CALL(s) in that region (0 traced, 2,208 generated): every byte identical.
- otherwise, where it does not hold, when DK-PARM = 0: FNDKRND does something no single relation per field describes (DK-VALUE varies); the statement does not hold there (1,792 CALLs checked).
- generated inputs covered: DK-PARM 0..12; dkValue digits 1..14 (both signs); DK-MODE 'C' 'D' 'E' 'F' 'H' 'N' 'U'
