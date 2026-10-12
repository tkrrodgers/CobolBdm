       IDENTIFICATION DIVISION.
       PROGRAM-ID. LOWER03.
      * ALTER is lowered: the altered GO TO jumps to an ALTERable target.
       PROCEDURE DIVISION.
       MAIN-PARA.
           ALTER SW-PARA TO PROCEED TO END-PARA
           GO TO SW-PARA.
       SW-PARA.
           GO TO END-PARA.
       END-PARA.
           GOBACK.
