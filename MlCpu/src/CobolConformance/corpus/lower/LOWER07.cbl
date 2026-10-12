       IDENTIFICATION DIVISION.
       PROGRAM-ID. LOWER07.
      * SEARCH ALL forms that are abstained: a table without KEY, a WHEN
      * relation other than =, and a WHEN on an item that is not a KEY.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 WS-T1.
          05 T1-E            PIC X(2) OCCURS 5 INDEXED BY T1-X.
       01 WS-T2.
          05 T2-E            OCCURS 5 ASCENDING KEY T2-K
                             INDEXED BY T2-X.
             10 T2-K         PIC X(2).
             10 T2-V         PIC X(2).
       PROCEDURE DIVISION.
       MAIN-PARA.
           SEARCH ALL T1-E WHEN T1-E (T1-X) = 'AA' DISPLAY 'A'
           END-SEARCH
           SEARCH ALL T2-E WHEN T2-K (T2-X) > 'AA' DISPLAY 'B'
           END-SEARCH
           SEARCH ALL T2-E WHEN T2-V (T2-X) = 'AA' DISPLAY 'C'
           END-SEARCH
           SEARCH ALL T2-E WHEN T2-K (T2-X) = 'AA' DISPLAY 'D'
           END-SEARCH
           GOBACK.
