       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY18.
      * DISPLAY of zoned numeric items as GnuCOBOL shows them: the bytes
      * as stored, NUL and space shown as '0' (never-set storage reads
      * as zeros), a signed item's sign position decoded and its sign
      * after it; RETURN-CODE and TALLY with their PICTURE's digits.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 X                  PIC X(4).
       01 N REDEFINES X      PIC 9(4).
       01 Y                  PIC X(3).
       01 S REDEFINES Y      PIC S9(3).
       01 T                  PIC 9(5) COMP.
       PROCEDURE DIVISION.
           MOVE X'00203141' TO X
           DISPLAY '[' N ']'
           MOVE 'A:Z' TO X
           DISPLAY '[' N ']'
           MOVE X'002031' TO Y
           DISPLAY '[' S ']'
           MOVE 'AB{' TO Y
           DISPLAY '[' S ']'
           DISPLAY '[' RETURN-CODE '][' TALLY ']'
           GOBACK.
