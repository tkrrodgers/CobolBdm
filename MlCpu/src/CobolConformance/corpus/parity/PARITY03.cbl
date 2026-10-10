       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY03.
      * PERFORM VARYING WITH TEST AFTER (the body also runs with the
      * value that ends the loop; the subscript is not checked), and
      * WRITE ... ADVANCING control bytes on a record sequential file,
      * read back one byte at a time.
       ENVIRONMENT DIVISION.
       INPUT-OUTPUT SECTION.
       FILE-CONTROL.
           SELECT PRT-FILE ASSIGN TO PRTFILE
               FILE STATUS IS WS-FS.
           SELECT BYTE-FILE ASSIGN TO PRTFILE
               FILE STATUS IS WS-FS.
       DATA DIVISION.
       FILE SECTION.
       FD PRT-FILE.
       01 PRT-REC            PIC X(8).
       FD BYTE-FILE.
       01 BYTE-REC           PIC X.
       WORKING-STORAGE SECTION.
       01 WS-FS              PIC XX.
       01 WS-EOF             PIC X VALUE 'N'.
       01 WS-I               PIC 99 VALUE 0.
       01 WS-N               PIC 99 VALUE 0.
       01 WS-TOTAL           PIC 9(4) VALUE 0.
       01 WS-NL              PIC 9(4) VALUE 0.
       01 WS-FF              PIC 9(4) VALUE 0.
       01 WS-TABLE.
          05 WS-CELL         PIC X OCCURS 3.
          05 WS-NEXT         PIC X(4) VALUE 'ZZZZ'.
       PROCEDURE DIVISION.
       MAIN-PARA.
           MOVE '...' TO WS-TABLE(1:3)
           PERFORM TEST AFTER VARYING WS-I FROM 1 BY 1 UNTIL WS-I > 3
               ADD 1 TO WS-N
               MOVE '*' TO WS-CELL(WS-I)
           END-PERFORM
           DISPLAY 'TEST AFTER RUNS=' WS-N ' I=' WS-I ' TABLE=' WS-TABLE
           OPEN OUTPUT PRT-FILE
           MOVE 'PAGE' TO PRT-REC
           WRITE PRT-REC AFTER ADVANCING PAGE
           MOVE 'PLAIN' TO PRT-REC
           WRITE PRT-REC
           MOVE 'AFTER2' TO PRT-REC
           WRITE PRT-REC AFTER 2 LINES
           MOVE 'BEFORE1' TO PRT-REC
           WRITE PRT-REC BEFORE ADVANCING 1 LINE
           MOVE 'LAST' TO PRT-REC
           WRITE PRT-REC AFTER 1
           CLOSE PRT-FILE
           OPEN INPUT BYTE-FILE
           PERFORM UNTIL WS-EOF = 'Y'
               READ BYTE-FILE
                   AT END MOVE 'Y' TO WS-EOF
                   NOT AT END
                       ADD 1 TO WS-TOTAL
                       IF BYTE-REC = X'0A'
                           ADD 1 TO WS-NL
                       END-IF
                       IF BYTE-REC = X'0C'
                           ADD 1 TO WS-FF
                       END-IF
               END-READ
           END-PERFORM
           CLOSE BYTE-FILE
           DISPLAY 'PRINT BYTES=' WS-TOTAL ' LF=' WS-NL ' FF=' WS-FF
           GOBACK.
