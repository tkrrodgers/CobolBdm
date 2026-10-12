       IDENTIFICATION DIVISION.
       PROGRAM-ID. LAYOUT01.
      * Its copybook is in ../members, a directory the analyser's
      * directory-name heuristics never search: only a configured
      * copybook root (project.json layout.copybookRoots) finds it.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       COPY LAYCPY.
       PROCEDURE DIVISION.
           DISPLAY LAY-FIELD
           GOBACK.
