# Contracts of the programs FNDKDFO CALLs (verified)

FNDKDFO CALLs the programs below. Each contract was verified by the pipeline on the callee's own CALLs (traced from the faithful program, and generated ones run through it), field by field. You may CALL the program or compute its result yourself. If you compute it yourself, you must still produce every effect its contract lists for that CALL: the result fields, the return and status fields, and every field the callee copies from the caller's arguments (for example a reject site taken from the value the caller placed in an argument field before that CALL), on the error paths as well as on the normal one. The callee's field names are the argument records' fields as the caller passes them.

## FNDKRND

- `CALL 'FNDKRND' USING DK-PARMS` in 9050-ROUND-TO-PREC, line 220

Where FNDKDFO reaches it (the CALL, or a PERFORM that leads to it), with the argument fields its paragraph writes since the previous such place, on any branch (fields not written there keep the values they had):

- 9050-ROUND-TO-PREC, line 220 (the CALL): DK-VALUE = WS-X; DK-FUNCTION = 'P'; DK-PARM = DF-PREC; DK-MODE = DF-MODE; DK-STICKY = WS-STICKY

The verified contract of FNDKRND for the codes FNDKDFO sets (left out: DK-FUNCTION = 'D', DK-FUNCTION = 'S', which FNDKDFO never sets):

### DK-FUNCTION = 'P' (DK-FN-SHORTEN) and DK-STICKY = 'N'

`r = dkValue.round(new MathContext(dkParm, mode))`

- where dkValue = DK-VALUE (a BigDecimal of scale 0); dkParm = DK-PARM; mode by DK-MODE: 'C' CEILING, 'D' DOWN, 'E' HALF_EVEN, 'F' FLOOR, 'H' HALF_UP, 'N' HALF_DOWN, 'U' UP
- result: DK-SHIFT = max(0, dkValue.precision() - dkParm) (the digits dropped), DK-VALUE = r.movePointLeft(DK-SHIFT) (the digits kept: r = DK-VALUE × 10^DK-SHIFT)
- also: DK-RC = 0
- when the result does not fit DK-VALUE, DK-SHIFT: DK-VALUE = 0; DK-RC = 0; DK-SHIFT = 24; every other field unchanged
- checked: 14,427 traced CALL(s) and 100,000 generated CALL(s) of the faithful FNDKRND: identical on 96,360; the other 3,640 lie where it does not hold, below.
- where it does not hold, when DK-PARM = 0 and magnitude(dkValue) >= 0: FNDKRND does something no single relation per field describes (DK-VALUE, DK-SHIFT vary); the statement does not hold there (3,000 CALLs checked).
- generated inputs covered: DK-PARM 0..12; dkValue digits 1..24 (both signs); DK-MODE 'C' 'D' 'E' 'F' 'H' 'N' 'U'

### DK-FUNCTION = 'P' (DK-FN-SHORTEN) and DK-STICKY = 'Y'

`r = dkValue.add(BigDecimal.valueOf(dkValue.signum() < 0 ? -1 : 1, dkValue.scale() + 1)).round(new MathContext(dkParm, mode))`

- where dkValue = DK-VALUE (a BigDecimal of scale 0); dkParm = DK-PARM; dkValue.add(BigDecimal.valueOf(dkValue.signum() < 0 ? -1 : 1, dkValue.scale() + 1)) is dkValue with one more nonzero digit after its last: the value dkValue stands for has further nonzero digits (a sticky digit; any digit 1-9 gives the same result); mode by DK-MODE: 'C' CEILING, 'D' DOWN, 'E' HALF_EVEN, 'F' FLOOR, 'H' HALF_UP, 'N' HALF_DOWN, 'U' UP
- result: DK-SHIFT = max(0, dkValue.precision() - dkParm) (the digits dropped), DK-VALUE = r.movePointLeft(DK-SHIFT) (the digits kept: r = DK-VALUE × 10^DK-SHIFT)
- also: DK-STICKY = 'N'; DK-RC = 0
- when the result does not fit DK-VALUE, DK-SHIFT: when DK-PARM = 0: DK-STICKY = 'N'; otherwise DK-STICKY unchanged; when DK-PARM = 0: DK-RC = 0; otherwise DK-RC = 8; DK-SHIFT = 0; DK-VALUE unchanged; every other field unchanged
- checked: 3,974 traced CALL(s) and 100,000 generated CALL(s) of the faithful FNDKRND: identical on 90,378; the other 9,622 lie where it does not hold, below.
- where it does not hold, when DK-PARM - digits(dkValue) = 0: DK-RC = 8; DK-SHIFT = 0; every other field unchanged. A contract checked on 2,224 CALL(s) in that region (0 traced, 2,224 generated): every byte identical.
- otherwise, where it does not hold, when DK-PARM = 0: FNDKRND does something no single relation per field describes (DK-VALUE, DK-SHIFT vary); the statement does not hold there (1,776 CALLs checked).
- generated inputs covered: DK-PARM 0..12; dkValue digits 1..14 (both signs); DK-MODE 'C' 'D' 'E' 'F' 'H' 'N' 'U'
