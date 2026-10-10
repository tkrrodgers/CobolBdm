       IDENTIFICATION DIVISION.
       PROGRAM-ID. ACCEPT02.
      * COPY with REPLACING from the sibling cpy/ library.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
           COPY ACPYREC REPLACING ==PFX-ID== BY ==CUST-ID==
                                  ==PFX-NAME== BY ==CUST-NAME==.
       PROCEDURE DIVISION.
       MAIN-PARA.
           MOVE 12345 TO CUST-ID
           MOVE 'ADA LOVELACE' TO CUST-NAME
           DISPLAY CUST-ID ' ' CUST-NAME
           GOBACK.
