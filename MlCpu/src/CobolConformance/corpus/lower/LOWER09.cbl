       IDENTIFICATION DIVISION.
       PROGRAM-ID. LOWER09.
      * Intrinsic-function uses that are abstained: DISPLAY of a numeric
      * function result and MOVE of one to an alphanumeric item (both
      * show GnuCOBOL's own temporary field), TEST-NUMVAL-C with a
      * currency argument (GnuCOBOL mis-scans it); EXIT PERFORM inside
      * PERFORM VARYING ... AFTER. Their neighbours are lowered.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 X                  PIC X(10).
       01 N                  PIC 9(4).
       01 I                  PIC 9.
       01 J                  PIC 9.
       PROCEDURE DIVISION.
       MAIN-PARA.
           DISPLAY FUNCTION SQRT (2)
           MOVE FUNCTION ORD ('A') TO X
           MOVE FUNCTION ORD ('A') TO N
           COMPUTE N = FUNCTION TEST-NUMVAL-C ('EUR1.5' 'EUR')
           PERFORM VARYING I FROM 1 BY 1 UNTIL I > 2
                   AFTER J FROM 1 BY 1 UNTIL J > 2
              IF J = 2 EXIT PERFORM END-IF
           END-PERFORM
           GOBACK.
