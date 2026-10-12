       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY21.
      * An abend through CEE3ABD (the oracle's stub ends the run with
      * STOP RUN) closes the files left open: the records written before
      * it are in the output (GnuCOBOL: "implicit CLOSE"). The Java
      * runtime used to drop them. Found by the Fineract sweep, where a
      * cut run left its REJECTS file empty.
       ENVIRONMENT DIVISION.
       INPUT-OUTPUT SECTION.
       FILE-CONTROL.
           SELECT OUT-FILE ASSIGN TO OUTFILE.
       DATA DIVISION.
       FILE SECTION.
       FD OUT-FILE.
       01 OUT-REC            PIC X(10).
       WORKING-STORAGE SECTION.
       01 WS-I               PIC 9(4).
       01 WS-CODE            PIC S9(9) BINARY VALUE 999.
       01 WS-TIMING          PIC S9(9) BINARY VALUE 0.
       PROCEDURE DIVISION.
           OPEN OUTPUT OUT-FILE
           PERFORM VARYING WS-I FROM 1 BY 1 UNTIL WS-I > 3
               MOVE 'RECORD' TO OUT-REC
               MOVE WS-I TO OUT-REC (8:3)
               WRITE OUT-REC
           END-PERFORM
           CALL 'CEE3ABD' USING WS-CODE WS-TIMING
           DISPLAY 'NOT REACHED'
           GOBACK.
       IDENTIFICATION DIVISION.
       PROGRAM-ID. CEE3ABD.
       DATA DIVISION.
       LINKAGE SECTION.
       01 LK-CODE            PIC S9(9) BINARY.
       01 LK-TIMING          PIC S9(9) BINARY.
       PROCEDURE DIVISION USING LK-CODE LK-TIMING.
           DISPLAY 'ABEND CEE3ABD'
           MOVE 16 TO RETURN-CODE
           STOP RUN.
       END PROGRAM CEE3ABD.
       END PROGRAM PARITY21.
