       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY08.
      * SEARCH and SEARCH ALL as GnuCOBOL runs them. Serial: from the
      * index's current value, AT END past the size (ODO count or max),
      * WHENs in order, VARYING an own index or a data item (which takes
      * the index's value after each step), NEXT SENTENCE, a table
      * inside a table. SEARCH ALL: binary search on the KEYs in their
      * declared order (DESCENDING negated), keys on either side, a
      * key's condition-name, duplicates, AT END and the index after it.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 WS-T1-DATA         PIC X(18) VALUE 'AAABBBCCCBBBDDDEEE'.
       01 WS-T1 REDEFINES WS-T1-DATA.
          05 T1-E            PIC X(3) OCCURS 6 INDEXED BY T1-X T1-Y.
       01 WS-J               PIC 9(3) VALUE 50.
       01 WS-CNT             PIC 9(2) VALUE 3.
       01 WS-T2.
          05 T2-E            OCCURS 1 TO 5 DEPENDING ON WS-CNT
                             INDEXED BY T2-X.
             10 T2-K         PIC X.
             10 T2-V         PIC 9.
       01 WS-T3-DATA         PIC X(12) VALUE 'ABCDEFGHIJKL'.
       01 WS-T3 REDEFINES WS-T3-DATA.
          05 T3-ROW          OCCURS 3 INDEXED BY T3-R.
             10 T3-COL       PIC X OCCURS 4 INDEXED BY T3-C.
       01 WS-T4-DATA.
          05 FILLER          PIC X(5) VALUE 'AA009'.
          05 FILLER          PIC X(5) VALUE 'AA005'.
          05 FILLER          PIC X(5) VALUE 'BB007'.
          05 FILLER          PIC X(5) VALUE 'CC008'.
          05 FILLER          PIC X(5) VALUE 'CC003'.
          05 FILLER          PIC X(5) VALUE 'CC001'.
          05 FILLER          PIC X(5) VALUE 'DD004'.
       01 WS-T4 REDEFINES WS-T4-DATA.
          05 T4-E            OCCURS 7 ASCENDING KEY T4-A
                             DESCENDING KEY T4-N INDEXED BY T4-X.
             10 T4-A         PIC XX.
                88 T4-IS-BB  VALUE 'BB'.
             10 T4-N         PIC 9(3).
       01 WS-T5.
          05 T5-E            PIC 99 OCCURS 1 TO 4 DEPENDING ON WS-CNT
                             ASCENDING KEY T5-E INDEXED BY T5-X.
       01 WS-KEY             PIC XX.
       01 WS-NUM             PIC 9(3).
       PROCEDURE DIVISION.
       MAIN-PARA.
           MOVE 5 TO WS-CNT
           MOVE 'A1B2C3D4E5' TO WS-T2
           MOVE 4 TO WS-CNT
           MOVE '11223344' TO WS-T5
           MOVE 3 TO WS-CNT
           SET T1-X TO 1
           SEARCH T1-E AT END DISPLAY 'S1 END'
             WHEN T1-E (T1-X) = 'BBB' DISPLAY 'S1 BBB AT ' T1-X
             WHEN T1-E (T1-X) = 'CCC' DISPLAY 'S1 CCC AT ' T1-X
           END-SEARCH
           SET T1-X TO 3
           SEARCH T1-E VARYING WS-J
             AT END DISPLAY 'S2 END ' T1-X ' ' WS-J
             WHEN T1-E (T1-X) = 'BBB' DISPLAY 'S2 BBB AT ' T1-X ' ' WS-J
           END-SEARCH
           SET T1-X TO 1
           SET T1-Y TO 2
           SEARCH T1-E VARYING T1-Y
             WHEN T1-E (T1-Y) = 'DDD' DISPLAY 'S3 DDD AT ' T1-Y ' ' T1-X
           END-SEARCH
           SET T1-X TO 7
           SEARCH T1-E AT END DISPLAY 'S4 END AT ONCE'
             WHEN T1-E (T1-X) = 'AAA' DISPLAY 'S4 WRONG'
           END-SEARCH
           SET T1-X TO 1
           SEARCH T1-E AT END DISPLAY 'S5 END'
             WHEN T1-E (T1-X) = 'ZZZ' DISPLAY 'S5 WRONG'
           END-SEARCH
           DISPLAY 'S5 INDEX ' T1-X
           SET T1-X TO 1
           SEARCH T1-E
             WHEN T1-E (T1-X) = 'CCC' NEXT SENTENCE
           END-SEARCH
           DISPLAY 'S6 NOT REACHED'.
           DISPLAY 'S6 NEXT SENTENCE ' T1-X
           SET T2-X TO 1
           SEARCH T2-E AT END DISPLAY 'S7 END (ODO 3) ' T2-X
             WHEN T2-K (T2-X) = 'D' DISPLAY 'S7 WRONG'
           END-SEARCH
           SET T3-R TO 2
           SET T3-C TO 1
           SEARCH T3-COL
             WHEN T3-COL (T3-R, T3-C) = 'G' DISPLAY 'S8 G AT ' T3-R
                  ' ' T3-C
           END-SEARCH
           MOVE 'CC' TO WS-KEY
           MOVE 3 TO WS-NUM
           SEARCH ALL T4-E AT END DISPLAY 'A1 END'
             WHEN T4-A (T4-X) = WS-KEY AND T4-N (T4-X) = WS-NUM
               DISPLAY 'A1 FOUND ' T4-X
           END-SEARCH
           SEARCH ALL T4-E AT END DISPLAY 'A2 END'
             WHEN 'AA' = T4-A (T4-X) DISPLAY 'A2 AA AT ' T4-X
           END-SEARCH
           SEARCH ALL T4-E AT END DISPLAY 'A3 END'
             WHEN T4-IS-BB (T4-X) DISPLAY 'A3 BB AT ' T4-X
           END-SEARCH
           SEARCH ALL T4-E AT END DISPLAY 'A4 END ' T4-X
             WHEN T4-A (T4-X) = 'CC' AND T4-N (T4-X) = 2
               DISPLAY 'A4 WRONG'
           END-SEARCH
           SEARCH ALL T4-E AT END DISPLAY 'A5 END ' T4-X
             WHEN T4-A (T4-X) = 'ZZ' DISPLAY 'A5 WRONG'
           END-SEARCH
           SEARCH ALL T5-E AT END DISPLAY 'A6 END (ODO 3) ' T5-X
             WHEN T5-E (T5-X) = 44 DISPLAY 'A6 WRONG'
           END-SEARCH
           SEARCH ALL T5-E AT END DISPLAY 'A7 END'
             WHEN T5-E (T5-X) = 22 DISPLAY 'A7 22 AT ' T5-X
           END-SEARCH
           GOBACK.
