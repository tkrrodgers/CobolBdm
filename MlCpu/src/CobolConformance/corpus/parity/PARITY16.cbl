       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY16.
      * LOCAL-STORAGE is allocated and initialized for each invocation
      * (VALUE clauses included) and is private to it, also when the
      * program CALLs itself (RECURSIVE); WORKING-STORAGE is shared.
      * Two programs in one source: the main one CALLs the second.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 WS-ARG             PIC 9(2) VALUE 3.
       PROCEDURE DIVISION.
           CALL 'P16REC' USING WS-ARG
           CALL 'P16REC' USING WS-ARG
           GOBACK.
       END PROGRAM PARITY16.
       IDENTIFICATION DIVISION.
       PROGRAM-ID. P16REC IS RECURSIVE.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 WS-CALLS           PIC 9(3) VALUE 0.
       LOCAL-STORAGE SECTION.
       01 LS-N               PIC 9(2).
       01 LS-MARK            PIC X(3) VALUE 'NEW'.
       LINKAGE SECTION.
       01 LK-N               PIC 9(2).
       PROCEDURE DIVISION USING LK-N.
       MAIN-PARA.
           ADD 1 TO WS-CALLS
           DISPLAY 'CALL ' WS-CALLS
           MOVE LK-N TO LS-N
           DISPLAY 'ENTER ' LS-N ' MARK ' LS-MARK
           MOVE 'OLD' TO LS-MARK
           IF LS-N > 1
              SUBTRACT 1 FROM LK-N
              CALL 'P16REC' USING LK-N
           END-IF
           DISPLAY 'LEAVE ' LS-N ' MARK ' LS-MARK
           GOBACK.
       END PROGRAM P16REC.
