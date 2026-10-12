       IDENTIFICATION DIVISION.
       PROGRAM-ID. LOWER10.
      * ALTER of a paragraph that is not a lone GO TO sentence (the
      * standard requires one) is abstained.
       PROCEDURE DIVISION.
       MAIN-PARA.
           ALTER SW-PARA TO PROCEED TO END-PARA
           GO TO SW-PARA.
       SW-PARA.
           DISPLAY 'SW'
           GO TO END-PARA.
       END-PARA.
           GOBACK.
