       IDENTIFICATION DIVISION.
       PROGRAM-ID. BINDPGM.
      * DD-binding and replay fixture: five files, one stamped with the clock.
       ENVIRONMENT DIVISION.
       INPUT-OUTPUT SECTION.
       FILE-CONTROL.
           SELECT IN-FILE ASSIGN TO INFILE.
           SELECT OUT-FILE ASSIGN TO OUTFILE.
           SELECT AUDIT-FILE ASSIGN TO AUDITF.
           SELECT REPORT-FILE ASSIGN TO REPORTF.
           SELECT SYM-FILE ASSIGN TO UT-S-SYMF.
       DATA DIVISION.
       FILE SECTION.
       FD  IN-FILE.
       01  IN-REC              PIC X(80).
       FD  OUT-FILE.
       01  OUT-REC             PIC X(80).
       FD  AUDIT-FILE.
       01  AUDIT-REC           PIC X(40).
       FD  REPORT-FILE.
       01  REPORT-REC          PIC X(133).
       FD  SYM-FILE.
       01  SYM-REC             PIC X(20).
       WORKING-STORAGE SECTION.
       01  WS-OUT.
           05  WS-OUT-DATA     PIC X(54).
           05  WS-OUT-TS       PIC X(26).
       01  WS-NOW              PIC X(21).
       01  WS-EOF              PIC X VALUE 'N'.
       PROCEDURE DIVISION.
       MAIN-PARA.
           OPEN INPUT IN-FILE SYM-FILE
                OUTPUT OUT-FILE AUDIT-FILE REPORT-FILE
           MOVE FUNCTION CURRENT-DATE TO WS-NOW
           PERFORM UNTIL WS-EOF = 'Y'
               READ IN-FILE
                   AT END MOVE 'Y' TO WS-EOF
                   NOT AT END
                       MOVE IN-REC TO WS-OUT-DATA
                       MOVE WS-NOW TO WS-OUT-TS
                       WRITE OUT-REC FROM WS-OUT
               END-READ
           END-PERFORM
           CLOSE IN-FILE SYM-FILE OUT-FILE AUDIT-FILE REPORT-FILE
           GOBACK.
