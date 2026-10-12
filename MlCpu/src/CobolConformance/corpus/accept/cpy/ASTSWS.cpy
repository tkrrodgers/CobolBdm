      * Switches with FALSE values (AST01, COPY ... REPLACING).
           05  :P:-FOUND-SW            PIC X.
               88  :P:-FOUND           VALUE 'Y' WHEN SET TO FALSE 'N'.
           05  :P:-DONE-SW             PIC X.
               88  :P:-DONE            VALUE 'Y' WHEN SET TO FALSE 'N'.
