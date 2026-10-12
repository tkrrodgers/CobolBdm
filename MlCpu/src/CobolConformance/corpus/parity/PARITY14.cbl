       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY14.
      * DECLARATIVES with USE AFTER ERROR / EXCEPTION, as GnuCOBOL
      * routes I/O statuses: a phrase takes its own condition (AT END
      * only status 10, INVALID KEY 21-24); any other exception runs the
      * file's USE procedure, else the one for the file's last OPEN
      * mode (also after a failed OPEN), then control continues after
      * the statement; a read past the end (46) is not an AT END.
       ENVIRONMENT DIVISION.
       INPUT-OUTPUT SECTION.
       FILE-CONTROL.
           SELECT F1 ASSIGN TO 'parity14a.dat' ORGANIZATION SEQUENTIAL
               FILE STATUS IS FS1.
           SELECT F2 ASSIGN TO 'parity14-none.dat'
               ORGANIZATION SEQUENTIAL FILE STATUS IS FS2.
           SELECT F3 ASSIGN TO 'parity14k.dat' ORGANIZATION INDEXED
               ACCESS DYNAMIC RECORD KEY IS K3 FILE STATUS IS FS3.
           SELECT F4 ASSIGN TO 'parity14b.dat' ORGANIZATION SEQUENTIAL.
       DATA DIVISION.
       FILE SECTION.
       FD F1.
       01 R1 PIC X(4).
       FD F2.
       01 R2 PIC X(4).
       FD F3.
       01 R3.
          05 K3 PIC X(2).
          05 D3 PIC X(2).
       FD F4.
       01 R4 PIC X(4).
       WORKING-STORAGE SECTION.
       01 FS1 PIC XX.
       01 FS2 PIC XX.
       01 FS3 PIC XX.
       PROCEDURE DIVISION.
       DECLARATIVES.
       F1-ERR SECTION.
           USE AFTER STANDARD ERROR PROCEDURE ON F1.
       F1-ERR-PARA.
           DISPLAY 'F1 USE ' FS1.
       F3-ERR SECTION.
           USE AFTER ERROR PROCEDURE ON F3.
       F3-ERR-PARA.
           DISPLAY 'F3 USE ' FS3.
       IN-ERR SECTION.
           USE AFTER EXCEPTION PROCEDURE ON INPUT.
       IN-ERR-PARA.
           DISPLAY 'INPUT USE ' FS2.
       IN-ERR-2.
           DISPLAY 'INPUT USE SECOND PARAGRAPH'.
       END DECLARATIVES.
       MAIN SECTION.
       MAIN-PARA.
           OPEN OUTPUT F1
           MOVE 'AAAA' TO R1 WRITE R1
           CLOSE F1
           OPEN INPUT F1
           READ F1
           READ F1
           READ F1 AT END DISPLAY 'AT END PHRASE 46?'
                   NOT AT END DISPLAY 'NOT AT END 46?'
           END-READ
           CLOSE F1
           OPEN INPUT F1
           READ F1 AT END DISPLAY 'NOT REACHED'
                   NOT AT END DISPLAY 'NOT AT END ' FS1
           END-READ
           READ F1 AT END DISPLAY 'AT END PHRASE ' FS1
           END-READ
           CLOSE F1
           OPEN INPUT F2
           READ F2
           DISPLAY 'AFTER F2 ' FS2
           OPEN OUTPUT F3
           MOVE 'K1D1' TO R3 WRITE R3
           WRITE R3 INVALID KEY DISPLAY 'INVALID KEY PHRASE ' FS3
           END-WRITE
           WRITE R3
           CLOSE F3
           OPEN I-O F3
           MOVE 'K9' TO K3
           READ F3 KEY IS K3 INVALID KEY DISPLAY 'NOT FOUND ' FS3
           END-READ
           READ F3 KEY IS K3
           CLOSE F3
           OPEN OUTPUT F4
           MOVE 'BBBB' TO R4 WRITE R4
           CLOSE F4
           DISPLAY 'DONE'
           GOBACK.
