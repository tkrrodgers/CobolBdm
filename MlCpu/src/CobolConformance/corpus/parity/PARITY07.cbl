       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY07.
      * Compiler-directing text: COPY ... REPLACING and REPLACE match
      * whole text words (case-insensitively, literals included), with
      * :TAG: and (TAG) replacing part of a word; a later REPLACE
      * replaces the earlier set and REPLACE OFF ends it; the listing
      * directives EJECT, SKIP1/2/3 and TITLE never swallow the next
      * entry or statement.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       COPY PARITY07 REPLACING ==WS-OLD== BY ==WS-NEW==
                               ==:TAG:== BY ==ZZ==
                               ==(TAG)== BY ==YY==
                               ==P== BY ==R==
                               ==Q== BY ==QQ==
                               LEADING ==PRE== BY ==POST==.
       01 WS-ONE            PIC X(3) VALUE 'one'.
           EJECT
       01 WS-TWO            PIC X(3) VALUE 'two'.
       SKIP1
       01 WS-THREE          PIC X(5) VALUE 'three'.
           SKIP2.
       01 WS-FOUR           PIC X(4) VALUE 'four'.
       SKIP3
           TITLE 'PARITY07 - WORKING STORAGE'
       01 WS-FIVE           PIC X(4) VALUE 'five'.
       REPLACE ==WS-ALIAS== BY ==WS-ONE==
               =='lit'== BY =='XYZ'==.
       01 WS-LIT            PIC X(3) VALUE 'LIT'.
       PROCEDURE DIVISION.
           DISPLAY WS-NEW ' ' WS-OLD-X ' ' ZZ-A ' ' B-ZZ ' ' C-YY-D
           DISPLAY P-FLD ' ' QQ ' ' POST-NAME
           DISPLAY WS-ONE ' ' WS-TWO ' ' WS-THREE ' ' WS-FOUR
           EJECT
           DISPLAY WS-FIVE
           SKIP1
           DISPLAY WS-ALIAS ' ' WS-LIT ' ' 'Lit'
           REPLACE ==DISPLAY 'A'
                     'B'== BY ==DISPLAY 'C'==.
           DISPLAY 'A' 'B'
           DISPLAY    'A'
               'B'
           DISPLAY 'lit'
           REPLACE OFF.
           DISPLAY 'A' 'B'
           DISPLAY 'lit'
           GOBACK.
