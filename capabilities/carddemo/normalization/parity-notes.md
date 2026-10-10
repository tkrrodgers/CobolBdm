# COBOL parity notes for normalized implementations

Rules of COBOL storage and I/O that decide whether a Java implementation reproduces the program byte for byte. They hold
for every CardDemo batch program; the requirements packet tells you which statements a program runs.

1. **Records are fixed-length byte images.** An output record is the record area as the program left it. Bytes that no
   statement ever wrote hold the storage default (`ctx.storageDefault()`, x'00' in most acceptance runs, a space in
   others; never hard-code either), not spaces. Storage without a VALUE clause, including every record area, starts as the
   storage default. A MOVE to an alphanumeric item or a group sets every byte of the receiver: a shorter sender is padded
   with spaces on the right (a 115-byte record moved to a 133-byte output record ends in 18 spaces), a longer one is
   truncated. INITIALIZE sets only the named elementary items
   (alphanumeric to spaces, numeric to zero) and **skips FILLER**: FILLER bytes keep whatever the area held before.
2. **Zoned decimal.** `PIC S9(n)V99` is n+2 digits, one byte each, with the sign overpunched in the last byte: positive
   `{ A B C D E F G H I` = 0..9, negative `} J K L M N O P Q R` = 0..9. Positive values may also arrive with a plain digit in
   the last byte (unsigned zone, "F-sign"); read them as positive. The platform's `Fields.zoned`/`Fields.putZoned` do this.
3. **Only what the program assigns is rewritten.** REWRITE writes the record area: fields the program assigned (ADD,
   SUBTRACT, MOVE, COMPUTE) are re-encoded in canonical form even when the value did not change (ADD 0 still rewrites the
   field with a signed zone); every other byte, credit limits and FILLER included, stays exactly as it was read.
4. **Truncation, not errors.** A result stored into a numeric picture keeps its low-order digits: high-order digits beyond
   the picture are dropped, without an exception and without rounding (unless ROUNDED). Intermediate results are exact.
5. **One record area per file.** READ ... INTO copies into a working-storage record; WRITE ... FROM copies from it. A record
   built in an area that was used before keeps that area's earlier bytes wherever the program does not move new values,
   so a new record can carry the FILLER of the record last read or written through the same area. Model the area as
   state that lives for the whole run.
6. **Failed reads.** On INVALID KEY or AT END the INTO area keeps its previous content; the program's error branch decides
   what happens next (reject, display, abend).
7. **Indexed files.** A KSDS is kept in key order; WRITE of an existing key fails (status 22) and REWRITE needs a prior READ
   of the same key. Sequential output is in write order.
8. **Console lines.** DISPLAY concatenates its operands with no separators. Literals keep their exact spacing; numeric
   operands display per `ctx.displayNumeric()` (all digits, with the sign as COBOL shows it). Each DISPLAY is one line.
9. **Return code and abends.** The step returns what the program moves to RETURN-CODE. Open, read, write and close
   failures display the program's message and file status, then end the step the way the program does (see the
   platform's `BatchFailure`). An abend ends the program at once: the statements after it, CLOSE included, never run, so
   output records still buffered are never written. Do not close or flush output files on the abend path.
10. **Order of effects.** Reproduce the program's order of reads, writes and console lines exactly; it is observable in the
    output files and SYSOUT.
