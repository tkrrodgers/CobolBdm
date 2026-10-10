       IDENTIFICATION DIVISION.
       PROGRAM-ID. LOWER03.
      * ALTER is a frontier; the plain GO TO around it is lowered.
       PROCEDURE DIVISION.
       MAIN-PARA.
           ALTER SW-PARA TO PROCEED TO END-PARA
           GO TO SW-PARA.
       SW-PARA.
           GO TO END-PARA.
       END-PARA.
           GOBACK.
