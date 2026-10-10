       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY02.
      * Tables, PERFORM VARYING, EVALUATE TRUE, STRING with POINTER, ref-modification.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  WS-TAB.
           05  WS-ROW OCCURS 4 TIMES.
               10  WS-NAME     PIC X(6).
               10  WS-QTY      PIC S9(3) COMP-3.
       01  WS-I                PIC 9(2).
       01  WS-SUM              PIC S9(5) VALUE 0.
       01  WS-LINE             PIC X(40) VALUE SPACES.
       01  WS-PTR              PIC 9(2) VALUE 1.
       01  WS-GRADE            PIC X(6).
       PROCEDURE DIVISION.
       MAIN-PARA.
           MOVE 'APPLE ' TO WS-NAME (1)
           MOVE 'PEAR  ' TO WS-NAME (2)
           MOVE 'FIG   ' TO WS-NAME (3)
           MOVE 'KIWI  ' TO WS-NAME (4)
           PERFORM VARYING WS-I FROM 1 BY 1 UNTIL WS-I > 4
               COMPUTE WS-QTY (WS-I) = WS-I * 7 - 10
               ADD WS-QTY (WS-I) TO WS-SUM
               STRING WS-NAME (WS-I) (1:2) DELIMITED BY SIZE
                   INTO WS-LINE WITH POINTER WS-PTR
               END-STRING
               EVALUATE TRUE
                   WHEN WS-QTY (WS-I) < 0
                       MOVE 'NEG' TO WS-GRADE
                   WHEN WS-QTY (WS-I) < 10
                       MOVE 'LOW' TO WS-GRADE
                   WHEN OTHER
                       MOVE 'HIGH' TO WS-GRADE
               END-EVALUATE
               DISPLAY WS-I ' ' WS-NAME (WS-I) WS-QTY (WS-I)
                   ' ' WS-GRADE
           END-PERFORM
           DISPLAY 'SUM=' WS-SUM ' LINE=' WS-LINE ' PTR=' WS-PTR
           GOBACK.
