       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY17.
      * Several programs in one source (the first one runs): EXTERNAL
      * storage shared by name across the run unit (zeroed once), CALL
      * ... RETURNING (the callee's RETURN-CODE goes to the item; the
      * caller's RETURN-CODE is left alone; a plain CALL sets it), and a
      * program IS INITIAL (its WORKING-STORAGE back to the initial
      * state on every CALL). DISPLAY of RETURN-CODE: 9 digits.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 WS-R               PIC S9(9) COMP-5.
       01 WS-RD              PIC 9(4).
       01 SHARED-REC IS EXTERNAL.
          05 SH-NAME         PIC X(5).
          05 SH-NUM          PIC 9(3).
       PROCEDURE DIVISION.
           DISPLAY 'EXT START [' SH-NAME ']' SH-NUM
           MOVE 'MAIN ' TO SH-NAME
           MOVE 7 TO SH-NUM
           CALL 'MP1SUB' RETURNING WS-R
           DISPLAY 'RET ' WS-R ' RC ' RETURN-CODE ' NAME ' SH-NAME
           CALL 'MP1SUB'
           DISPLAY 'PLAIN CALL RC ' RETURN-CODE
           MOVE 5 TO RETURN-CODE
           CALL 'MP1SUB' RETURNING WS-RD
           DISPLAY 'RET2 ' WS-RD ' RC ' RETURN-CODE
           CALL 'MP1INI'
           CALL 'MP1INI'
           MOVE 0 TO RETURN-CODE
           GOBACK.
       END PROGRAM PARITY17.
       IDENTIFICATION DIVISION.
       PROGRAM-ID. MP1SUB.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 SHARED-REC EXTERNAL.
          05 SH-NAME         PIC X(5).
          05 SH-NUM          PIC 9(3).
       PROCEDURE DIVISION.
           DISPLAY 'SUB SEES ' SH-NAME SH-NUM
           MOVE 'SUB  ' TO SH-NAME
           MOVE 42 TO RETURN-CODE
           GOBACK.
       END PROGRAM MP1SUB.
       IDENTIFICATION DIVISION.
       PROGRAM-ID. MP1INI IS INITIAL.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 CNT                PIC 9(2) VALUE 0.
       01 BLANK-ITEM         PIC X(3).
       PROCEDURE DIVISION.
           ADD 1 TO CNT
           DISPLAY 'INITIAL CNT ' CNT ' [' BLANK-ITEM ']'
           MOVE 'XYZ' TO BLANK-ITEM
           GOBACK.
       END PROGRAM MP1INI.
