       IDENTIFICATION DIVISION.
       PROGRAM-ID. AST06.
      * COBOL-85 grammar (AST) regression: subscripts, reference
      * modification and function calls in the forms SLL prediction
      * must parse alone (CobolAntlr parses SLL first; a program that
      * needs full LL takes minutes at 130K lines), each in a MOVE, a
      * condition, an arithmetic expression and a DISPLAY.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  WS-I                        PIC 9(2) VALUE 2.
       01  WS-J                        PIC 9(2) VALUE 1.
       01  WS-LEN                      PIC 9(2) VALUE 3.
       01  WS-X                        PIC X(10) VALUE 'ABCDEFGHIJ'.
       01  WS-Y                        PIC X(10).
       01  WS-N                        PIC 9(4) VALUE 0.
       01  WS-T.
           05  WS-ROW                  OCCURS 3.
               10  WS-CELL             PIC X(5) OCCURS 2.
       01  WS-REC.
           05  WS-NAME                 PIC X(8) VALUE 'NAME0001'.
       01  WS-DATE                     PIC X(8).
       PROCEDURE DIVISION.
       0000-MAIN.
           MOVE WS-X (1:4) TO WS-Y
           MOVE WS-X (WS-I:WS-LEN) TO WS-Y (WS-J:)
           MOVE WS-X (WS-I + 1:2) TO WS-CELL (WS-I, WS-J)
           MOVE WS-X TO WS-CELL (WS-I WS-J)
           MOVE WS-CELL (WS-I, 1) (2:3) TO WS-Y
           MOVE WS-CELL (WS-I + 1, WS-J) TO WS-Y
           MOVE WS-NAME OF WS-REC (5:4) TO WS-Y
           MOVE FUNCTION CURRENT-DATE (1:8) TO WS-DATE
           MOVE FUNCTION UPPER-CASE (WS-X (1:3)) TO WS-Y
           COMPUTE WS-N = FUNCTION LENGTH (WS-CELL (1, 1))
                        + FUNCTION MAX (WS-I WS-J WS-LEN)
                        + FUNCTION NUMVAL (WS-NAME (5:4))
           IF WS-X (1:1) = 'A' AND WS-CELL (2, 1) (1:1) NOT = SPACE
              AND WS-NAME OF WS-REC (1:4) = 'NAME'
               DISPLAY WS-X (WS-I:) WS-CELL (2, 1) WS-Y (1:WS-LEN)
                       FUNCTION CURRENT-DATE (9:6)
           END-IF
           DISPLAY WS-N
           GOBACK.
