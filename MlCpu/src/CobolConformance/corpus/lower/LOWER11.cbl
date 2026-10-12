       IDENTIFICATION DIVISION.
       PROGRAM-ID. LOWER11.
      * PROCEDURE DIVISION RETURNING: GnuCOBOL 3.3 does not implement a
      * program's RETURNING item, so the program stops when entered.
       DATA DIVISION.
       LINKAGE SECTION.
       01 LK-OUT             PIC 9(4).
       PROCEDURE DIVISION RETURNING LK-OUT.
       MAIN-PARA.
           MOVE 1234 TO LK-OUT
           GOBACK.
