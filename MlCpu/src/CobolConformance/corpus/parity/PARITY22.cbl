       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY22.
      * User-defined words with consecutive hyphens (J3S estate:
      * paragraph M1128-J3-S--EVENT--HEAD, M1139-J3-S--STRING--LITERALS):
      * a hyphen may appear anywhere in a word but at its ends, runs of
      * hyphens included. Paragraph names, PERFORM THRU, GO TO, a
      * paragraph whose name holds a verb (STRING, MOVE), data names.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 WS--REC.
          05 WS--A---B         PIC 9(4) VALUE 7.
          05 WS-C--D           PIC X(6) VALUE 'ABC'.
       01 WS--N                PIC 9(4) VALUE 0.
       PROCEDURE DIVISION.
       M0000--MAIN.
           PERFORM M0001-J3-S--STRING--LITERALS THRU F0001--END
           PERFORM M0002--MOVE---IT
           IF WS--N < 3
               GO TO M0000--MAIN
           END-IF
           DISPLAY 'N ' WS--N ' A ' WS--A---B ' C [' WS-C--D ']'
           DISPLAY 'REC [' WS--REC ']'
           GOBACK.
       M0001-J3-S--STRING--LITERALS.
           ADD 1 TO WS--N
           IF WS--N = 2
               GO TO F0001--END
           END-IF
           STRING 'X' WS--N DELIMITED BY SIZE INTO WS-C--D.
       F0001--END.
           EXIT.
       M0002--MOVE---IT.
           ADD WS--N TO WS--A---B.
