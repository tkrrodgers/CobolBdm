       IDENTIFICATION DIVISION.
       PROGRAM-ID. PARITY25.
      * GnuCOBOL -std=ibm sets arithmetic-osvs: an intermediate result
      * of COMPUTE (and of ADD/SUBTRACT with several operands) is cut
      * to the scale cobc chooses at compile time (cob_decimal_align):
      * the largest scale of the receivers and operands, or the scale
      * the nested operands give. Nested divisions and products,
      * several receivers, folded literals, unary minus, parentheses.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 T                 PIC S9(9) VALUE 27956.
       01 K                 PIC 9(4) VALUE 66.
       01 A2                PIC S9(5)V99 VALUE 1.25.
       01 A5                PIC S9(5)V9(5) VALUE 3.14159.
       01 A5Z               PIC S9(5)V9(5) VALUE 3.14.
       01 CB                PIC S9(4) COMP VALUE 123.
       01 DB                PIC S9(4) COMP VALUE 477.
       01 B3                PIC S9(7)V999 COMP-3 VALUE -12.345.
       01 R0                PIC S9(9) COMP-3.
       01 R2                PIC S9(9)V99.
       01 R3                PIC S9(9)V999 COMP-3.
       01 R6                PIC S9(9)V9(6) COMP-3.
       PROCEDURE DIVISION.
           COMPUTE R0 = 4793 - T / 7
           DISPLAY '01 ' R0
           COMPUTE R3 = 4793 - T / 7
           DISPLAY '02 ' R3
           COMPUTE R0 = A2 + T / 7
           DISPLAY '03 ' R0
           COMPUTE R2 = (T / 7) * (A2 / 3) + 1
           DISPLAY '04 ' R2
           COMPUTE R6 R0 = 4793 - T / 7
           DISPLAY '05 ' R6 ' ' R0
           COMPUTE R2 = A5 * A2 / 3
           DISPLAY '06 ' R2
           COMPUTE R3 = B3 * B3 * B3
           DISPLAY '07 ' R3
           COMPUTE R2 = - T / 7 + A2
           DISPLAY '08 ' R2
           COMPUTE R2 = (T + K) / (K - 6) * A2
           DISPLAY '09 ' R2
           COMPUTE R3 = T / 7 / 3
           DISPLAY '10 ' R3
           COMPUTE R0 = 1.5 * 2 + T / 7
           DISPLAY '11 ' R0
           COMPUTE R2 = T * 1 + K / 7
           DISPLAY '12 ' R2
           ADD A2 A5 TO R2
           DISPLAY '13 ' R2
           SUBTRACT A5 B3 FROM R3 GIVING R6
           DISPLAY '14 ' R6
           ADD A5 T GIVING R2
           DISPLAY '15 ' R2
           COMPUTE R3 = (A5 + A2) * (A5 - A2)
           DISPLAY '16 ' R3
           COMPUTE R2 = T / 7 * 7
           DISPLAY '17 ' R2
           COMPUTE R2 = 7 * (T / 7)
           DISPLAY '18 ' R2
           COMPUTE R6 = A5 / 3 + A5 / 7
           DISPLAY '19 ' R6
      * 20-22: cobc aligns an intermediate to more decimals than it has
      * (a GnuCOBOL defect: it divides instead); harmless here, where
      * the digits it drops are zeros (LOWER13: where they are not)
           COMPUTE R2 = B3 / 0.7 - A5Z * 2
           DISPLAY '20 ' R2
           ADD CB DB TO R2
           DISPLAY '21 ' R2
           COMPUTE R3 = 1000 + DB * 1000 - 1
           DISPLAY '22 ' R3
           GOBACK.
