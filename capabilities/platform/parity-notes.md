# COBOL parity notes for normalized implementations

Rules of COBOL storage and I/O that decide whether a Java implementation reproduces the program byte for byte. The
reference is GnuCOBOL (`-std=ibm -fsign=EBCDIC`) running the original source; the requirements packet and the AST outline
tell you which statements a program runs.

1. **Records are byte images.** An output record is the record area as the program left it. Bytes no statement ever
   wrote hold the storage default (`ctx.storageDefault()`, x'00' in most acceptance runs, a space in others; never
   hard-code either). Storage without a VALUE clause, including every FD record area, starts as the storage default.
   A MOVE to an alphanumeric item or a group sets every byte of the receiver: a shorter sender is padded with spaces on
   the right, a longer one is truncated. `MOVE SPACES TO group` fills the whole group with spaces (numeric fields inside
   it too). INITIALIZE sets only the named elementary items (alphanumeric to spaces, numeric to zero) and **skips FILLER**.
2. **Zoned decimal (`PIC 9`, `PIC S9`, USAGE DISPLAY).** One digit per byte. An unsigned field stores plain digits. A
   signed field carries the sign overpunched in the last byte: positive `{ A B C D E F G H I` = 0..9, negative
   `} J K L M N O P Q R` = 0..9. `MOVE +0 TO unsigned-field` stores all '0' digits. The platform's `Fields.zoned` /
   `Fields.putZoned` implement this; `Fields.packed` / `putPacked` (COMP-3) and `binary` / `putBinary` (COMP) too.
3. **Only what the program assigns changes.** A rewritten or copied record keeps every byte the program did not assign.
4. **Truncation, not errors.** A result stored into a numeric picture keeps its low-order digits (high-order digits
   beyond the picture are dropped) without an exception and without rounding unless ROUNDED is coded; an unsigned
   receiver stores the absolute value. Intermediate results are exact.
5. **One record area per file.** READ … INTO copies into a working-storage record, WRITE … FROM copies from it. A record
   built in an area that was used before keeps that area's earlier bytes wherever the program does not move new values.
   Model each record area as state that lives for the whole run.
6. **Variable-length records** (`RECORDING MODE IS V`, `RECORD IS VARYING`): each record on disk is a 4-byte descriptor
   (2-byte big-endian length, then x'0000') followed by the record bytes (`VariableDataset`). The length written is the
   length of the 01-level record named in the WRITE (or its FROM item); a READ returns exactly the stored bytes and the
   rest of the record area keeps what it held before.
7. **Print files (`WRITE … ADVANCING`)** are written with GnuCOBOL's control bytes (`PrintFile`): `AFTER n` writes n
   x'0A' before the record, `AFTER PAGE` one x'0C' before it, `BEFORE n` / `BEFORE PAGE` the control after the record, a
   plain WRITE nothing; CLOSE adds one x'0A' when the last advancing write was AFTER. Records keep their full length.
8. **Loops.** `PERFORM … VARYING I FROM a BY b UNTIL cond` tests before each pass; `WITH TEST AFTER` tests after each
   pass and before the increment, so the body also runs with the value that first satisfies the condition
   (`UNTIL I > 12` runs I = 1..13). A subscript beyond the OCCURS bound is not checked: it addresses the bytes that
   follow the table in the record (it overwrites the next fields).
9. **CALL** passes arguments BY REFERENCE: the called program reads and writes the caller's storage directly; every
   change is visible to the caller after the CALL. A CALLed program's WORKING-STORAGE keeps its values from one CALL to
   the next within the run.
10. **Failed reads.** On INVALID KEY or AT END the INTO area keeps its previous content; the program's branch decides
    what happens next. A file status other than '00' after OPEN is how the program detects a missing dataset.
11. **Console lines.** DISPLAY concatenates its operands with no separators; literals keep their exact spacing; numeric
    operands display per `ctx.displayNumeric()` (digits, sign as COBOL shows it). Each DISPLAY is one line.
12. **Implicit CLOSE.** Files still open when the program ends (GOBACK, STOP RUN, or an abend through CEE3ABD) are
    closed implicitly, so every record written before the end is in the output. An abend ends the program at once:
    statements after it never run.
13. **Return code.** The step returns what the program moves to RETURN-CODE (0 when it never does).
14. **Order of effects.** Reproduce the program's order of reads, writes and console lines exactly; it is observable in
    the output files and SYSOUT.
