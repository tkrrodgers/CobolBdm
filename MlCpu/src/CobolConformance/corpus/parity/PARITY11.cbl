       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY11.
      * PERFORM ... VARYING ... AFTER ... (nested varying), WITH TEST
      * BEFORE and AFTER, inline and out of line, three levels, a FROM
      * that uses the outer variable, an inner UNTIL true at once, an
      * index as the variable, and the variables' values afterwards.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 I                  PIC 99.
       01 J                  PIC 99.
       01 K                  PIC S99.
       01 N                  PIC 9(3) VALUE 0.
       01 T.
          05 R               OCCURS 3 INDEXED BY RX.
             10 C            PIC X OCCURS 2 INDEXED BY CX.
       PROCEDURE DIVISION.
       MAIN-PARA.
           MOVE 'ABCDEF' TO T
           PERFORM VARYING I FROM 1 BY 1 UNTIL I > 2
                   AFTER J FROM I BY 1 UNTIL J > 3
              DISPLAY 'B ' I ' ' J
           END-PERFORM
           DISPLAY 'B END ' I ' ' J
           PERFORM WITH TEST AFTER VARYING I FROM 1 BY 1 UNTIL I > 2
                   AFTER J FROM I BY 1 UNTIL J > 3
              DISPLAY 'A ' I ' ' J
           END-PERFORM
           DISPLAY 'A END ' I ' ' J
           PERFORM COUNT-PARA VARYING I FROM 3 BY -1 UNTIL I = 0
                   AFTER J FROM 1 BY 2 UNTIL J > 4
                   AFTER K FROM -1 BY 1 UNTIL K > 0
           DISPLAY 'C END ' N ' ' I ' ' J ' ' K
           PERFORM VARYING I FROM 1 BY 1 UNTIL I > 3
                   AFTER J FROM 5 BY 1 UNTIL J > 4
              DISPLAY 'NEVER'
           END-PERFORM
           DISPLAY 'D END ' I ' ' J
           PERFORM VARYING RX FROM 1 BY 1 UNTIL RX > 3
                   AFTER CX FROM 1 BY 1 UNTIL CX > 2
              DISPLAY C (RX, CX) WITH NO ADVANCING
           END-PERFORM
           DISPLAY ' '
           GOBACK.
       COUNT-PARA.
           ADD 1 TO N
           DISPLAY 'C ' I ' ' J ' ' K.
