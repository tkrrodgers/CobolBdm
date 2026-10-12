#!/usr/bin/env python3
"""construct_census.py: which COBOL constructs an estate uses, and how CobolBdm handles each (plan R7, JavaToCOBOLToJava.md).

"Handles the majority of any COBOL code" needs a denominator. This census tokenizes every program and copybook of one or
more estates (fixed format: columns 8-72; comment lines skipped; literals kept whole), counts each construct, and joins
it with the construct's status in CobolBdm:

  lowered    transpiled with COBOL semantics (conformance-tested where a case exists)
  partial    lowered, with known divergences (see the note)
  frontier   reported and stopped at run time (CobRt.frontier) - never silently wrong
  silent     accepted without its semantics (no frontier): the dangerous kind
  n/a        outside the batch scope (CICS, SQL, DLI)

The status table (STATUS below) is maintained by hand from the code: when a construct's support changes, change its row in
the same commit. Usage:
  construct_census.py --estate NAME=DIR [--estate NAME=DIR ...] --out CONSTRUCT-CENSUS.md [--json census.json]
"""
import argparse
import json
import os
import re
from collections import Counter, defaultdict

# construct -> (status, note). Notes cite the survey of 2026-10-11 (MlCpu.Cobol.Analysis, java-sidecar SkeletonEmitter).
STATUS = {
    # procedure division verbs and forms
    "ACCEPT": ("partial", "FROM DATE/DAY/TIME/DAY-OF-WEEK/SYSIN; other sources fail at run time"),
    "ADD": ("lowered", ""), "SUBTRACT": ("lowered", ""), "MULTIPLY": ("lowered", ""), "DIVIDE": ("lowered", ""),
    "COMPUTE": ("lowered", "whole expression in BigDecimal; intermediate truncation as GnuCOBOL's arithmetic-osvs (MLCPU_ARITHMETIC)"),
    "ADD/SUBTRACT CORRESPONDING": ("frontier", ""),
    "MOVE": ("lowered", ""), "MOVE CORRESPONDING": ("partial", "group sender, unsubscripted only"),
    "INITIALIZE": ("lowered", "GnuCOBOL dialect: signed zoned items to plain zeros (PARITY09)"), "INITIALIZE REPLACING": ("lowered", "REPLACING, WITH FILLER, ALL TO VALUE, THEN TO DEFAULT (PARITY09); category TO VALUE and partly written table entries are frontiers (LOWER08)"),
    "IF": ("lowered", ""), "EVALUATE": ("lowered", "ALSO, THRU, ANY, TRUE/FALSE, OTHER"),
    "PERFORM": ("lowered", ""), "PERFORM THRU": ("lowered", ""), "PERFORM VARYING": ("lowered", ""),
    "PERFORM VARYING AFTER": ("lowered", "nested levels as GnuCOBOL generates them, TEST BEFORE/AFTER (PARITY11); EXIT PERFORM inside is a frontier"), "PERFORM TIMES": ("lowered", "not counted by the loop limit"),
    "PERFORM WITH TEST AFTER": ("lowered", ""),
    "GO TO": ("lowered", ""), "GO TO DEPENDING ON": ("lowered", ""), "ALTER": ("lowered", "ALTERable GO TO target per paragraph (PARITY13); a paragraph that is not a lone GO TO is a frontier (LOWER10)"),
    "EXIT PARAGRAPH/SECTION/PERFORM": ("lowered", ""), "CONTINUE": ("lowered", ""), "NEXT SENTENCE": ("lowered", ""),
    "STOP RUN": ("lowered", ""), "GOBACK": ("lowered", ""), "EXIT PROGRAM": ("lowered", ""),
    "CALL": ("lowered", "static and dynamic; BY VALUE treated as BY CONTENT; a whole record BY REFERENCE is the caller's storage (PARITY24)"),
    "ON SIZE ERROR": ("lowered", "receiver unchanged on overflow, as GnuCOBOL"), "NOT ON SIZE ERROR": ("lowered", ""),
    "ROUNDED": ("lowered", "half up"),
    "reference modification": ("lowered", ""),
    "word with consecutive hyphens": ("lowered", "M1-J3-S--EVENT--HEAD (phase 5: the lexer split such words; PARITY22)"),
    "COMPUTE with intermediate results": ("lowered", "arithmetic-osvs as cobc -std=ibm decides it (OsvsPlanner: intermediates truncated, PARITY25); its scale-up defect is reproduced only where harmless, else the run stops (LOWER13); an aligned intermediate of unknown scale is a frontier"), "CALL RETURNING": ("lowered", "callee RETURN-CODE to the item, caller RETURN-CODE kept (PARITY17); PROCEDURE DIVISION RETURNING is a frontier: GnuCOBOL 3.3 lacks it (LOWER11)"),
    "CANCEL": ("frontier", ""), "ENTRY": ("frontier", ""),
    "DISPLAY": ("lowered", "UPON is ignored; zoned items as GnuCOBOL shows them (PARITY18)"), "INSPECT": ("lowered", "TALLYING, REPLACING, CONVERTING"),
    "STRING": ("lowered", ""), "UNSTRING": ("lowered", ""),
    "SEARCH": ("lowered", "as GnuCOBOL: VARYING, AT END, WHENs in order (PARITY08)"), "SEARCH ALL": ("lowered", "GnuCOBOL's binary search over the KEYs; other conditions are frontiers (PARITY08, LOWER07)"),
    "SET TO TRUE": ("lowered", ""), "SET TO FALSE": ("lowered", "88 WHEN SET TO FALSE / FALSE literal (PARITY10)"), "SET index": ("lowered", ""),
    "SET ADDRESS OF": ("frontier", ""),
    "SORT": ("lowered", "USING/GIVING, INPUT/OUTPUT PROCEDURE, stable on the KEYs, table SORT (PARITY19); COLLATING SEQUENCE is a frontier (LOWER12)"),
    "MERGE": ("lowered", "a stable sort of its USING files in order, as GnuCOBOL (PARITY19)"),
    "RELEASE": ("lowered", ""), "RETURN": ("lowered", ""),
    "OPEN": ("lowered", "OPEN I-O on sequential returns status 37"), "CLOSE": ("lowered", ""),
    "READ": ("lowered", "AT END is status 10 only, INVALID KEY 21-24, as GnuCOBOL (PARITY14)"), "READ PREVIOUS": ("frontier", ""), "WRITE": ("lowered", ""),
    "WRITE ADVANCING": ("lowered", "GnuCOBOL control bytes; LINAGE ignored"), "REWRITE": ("lowered", "INDEXED only"),
    "DELETE": ("lowered", "INDEXED only"), "START": ("lowered", ""),
    "DECLARATIVES / USE": ("lowered", "USE AFTER ERROR/EXCEPTION on files and open modes, routed as GnuCOBOL (PARITY14); USE FOR DEBUGGING / BEFORE REPORTING are diagnostics"),
    "EXEC SQL": ("n/a", ""), "EXEC CICS": ("n/a", ""), "EXEC DLI": ("n/a", ""),
    "INITIATE/GENERATE/TERMINATE (Report Writer)": ("frontier", ""),
    "XML/JSON statements": ("frontier", ""), "INVOKE": ("frontier", ""),
    # data division
    "USAGE DISPLAY": ("lowered", ""), "USAGE COMP-3/PACKED-DECIMAL": ("lowered", ""),
    "USAGE COMP/COMP-4/BINARY": ("lowered", "TRUNC by configuration"),
    "USAGE COMP-5": ("lowered", "native little-endian in the GnuCOBOL dialect, big-endian for z/OS (MLCPU_COMP5) (PARITY15)"),
    "USAGE COMP-1/COMP-2": ("frontier", "fails at run time when used"),
    "USAGE INDEX": ("lowered", "DISPLAY as GnuCOBOL's signed 9 digits (PARITY08)"), "USAGE POINTER": ("partial", ""),
    "SIGN SEPARATE": ("lowered", ""), "REDEFINES": ("lowered", ""), "RENAMES (66)": ("silent", "no layout"),
    "88-level": ("lowered", ""), "OCCURS": ("lowered", "no bounds checks"), "OCCURS DEPENDING ON": ("lowered", "current length on every reference (PARITY05); complex ODO, items after the table, (start:) and non-uniform elementary INITIALIZE are frontiers (LOWER06)"),
    "INDEXED BY": ("lowered", ""), "ASCENDING/DESCENDING KEY": ("lowered", "drives SEARCH ALL (PARITY08)"),
    "JUSTIFIED": ("lowered", ""), "BLANK WHEN ZERO": ("lowered", ""), "SYNCHRONIZED": ("silent", "ignored"),
    "EXTERNAL": ("lowered", "one zeroed area per name for the run unit (PARITY17)"), "GLOBAL": ("partial", "no effect within one program; a nested program using its parent's GLOBAL item does not resolve it (frontier)"), "VALUE": ("lowered", ""),
    "group-level USAGE/SIGN": ("lowered", "inherited by members without their own clause (PARITY06)"),
    "LOCAL-STORAGE SECTION": ("lowered", "fresh, initialized storage per invocation, recursion included (PARITY16)"), "LINKAGE SECTION": ("lowered", ""),
    "SD (sort file)": ("lowered", "with the SORT verb"), "RECORD VARYING DEPENDING": ("partial", "READ only"),
    "LINAGE": ("silent", "ignored"), "REPORT SECTION": ("frontier", ""),
    # files and programs
    "ORGANIZATION INDEXED": ("lowered", ""), "ORGANIZATION RELATIVE": ("silent", "behaves as sequential"),
    "ORGANIZATION LINE SEQUENTIAL": ("lowered", ""), "ALTERNATE RECORD KEY": ("lowered", ""),
    "FILE STATUS": ("lowered", ""), "nested program": ("lowered", "every program of a source is analysed and transpiled; the first runs (PARITY16, PARITY17)"),
    "RECURSIVE / IS INITIAL": ("lowered", "INITIAL: initial state and files closed per CALL; RECURSIVE with LOCAL-STORAGE (PARITY16, PARITY17)"), "COPY": ("lowered", "depth 8"), "COPY REPLACING": ("lowered", "text words, :TAG: and (TAG) partial, LEADING/TRAILING (PARITY07)"),
    "REPLACE": ("lowered", "text-word matching as GnuCOBOL; REPLACE OFF (PARITY07)"), "SECTION": ("lowered", ""), "EJECT/SKIP": ("lowered", "listing directives blanked, also TITLE (PARITY07)"),
}
FUNCTIONS_LOWERED = {"CURRENT-DATE", "MOD", "REM", "INTEGER", "INTEGER-PART", "ABS", "MAX", "MIN", "NUMVAL", "NUMVAL-C", "LENGTH",
                     "INTEGER-OF-DATE", "DATE-OF-INTEGER", "UPPER-CASE", "LOWER-CASE", "TRIM", "REVERSE",
                     # frontier burn-down (PARITY12; DISPLAY of a numeric result and MOVE of one to alphanumeric are frontiers, LOWER09)
                     "TEST-NUMVAL", "TEST-NUMVAL-C", "ORD", "CHAR", "ORD-MAX", "ORD-MIN", "SUM", "MEAN", "MEDIAN", "RANGE", "SQRT",
                     "FACTORIAL", "INTEGER-OF-DAY", "DAY-OF-INTEGER", "TEST-DATE-YYYYMMDD", "YEAR-TO-YYYY", "DATE-TO-YYYYMMDD",
                     "WHEN-COMPILED"}

VERBS = {"ACCEPT", "ADD", "ALTER", "CALL", "CANCEL", "CLOSE", "COMPUTE", "CONTINUE", "DELETE", "DISPLAY", "DIVIDE", "ENTRY", "EVALUATE",
         "EXIT", "GO", "GOBACK", "IF", "INITIALIZE", "INSPECT", "MERGE", "MOVE", "MULTIPLY", "OPEN", "PERFORM", "READ", "RELEASE",
         "RETURN", "REWRITE", "SEARCH", "SET", "SORT", "START", "STOP", "STRING", "SUBTRACT", "UNSTRING", "WRITE", "ELSE", "END-IF",
         "WHEN", "END-EVALUATE", "END-PERFORM"}

TOKEN = re.compile(r"'(?:[^']|'')*'|\"(?:[^\"]|\"\")*\"|[A-Za-z0-9][A-Za-z0-9-]*|[.()=<>+*/,:]")


def tokens(path):
    out = []
    with open(path, encoding="latin-1") as f:
        for line in f:
            line = line.rstrip("\r\n")
            if len(line) > 6 and line[6] in "*/":
                continue
            code = line[7:72] if len(line) > 7 else ""
            out.extend(t.upper() if t[0] not in "'\"" else "'" for t in TOKEN.findall(code))
    return out


def census(paths):
    c = Counter()
    for p in paths:
        raw = tokens(p)
        # Inside EXEC ... END-EXEC only the EXEC kind counts (EXEC CICS RETURN is not the RETURN verb).
        t, inside = [], False
        for i, w in enumerate(raw):
            if w == "EXEC":
                inside = True
                t.extend([w, raw[i + 1] if i + 1 < len(raw) else ""])
            elif w == "END-EXEC":
                inside = False
            elif not inside:
                t.append(w)
        n = len(t)
        programs = t.count("PROGRAM-ID")
        if programs > 1:
            c["nested program"] += programs - 1
        if "DECLARATIVES" in t:
            c["DECLARATIVES / USE"] += 1
        for i, w in enumerate(t):
            nxt = t[i + 1] if i + 1 < n else ""
            nxt2 = t[i + 2] if i + 2 < n else ""
            prev = t[i - 1] if i > 0 else ""
            if w in ("ADD", "SUBTRACT") and nxt in ("CORRESPONDING", "CORR"):
                c["ADD/SUBTRACT CORRESPONDING"] += 1
            elif w in ("ACCEPT", "ADD", "SUBTRACT", "MULTIPLY", "DIVIDE", "COMPUTE", "INITIALIZE", "IF", "EVALUATE", "ALTER",
                       "CONTINUE", "GOBACK", "CANCEL", "ENTRY", "DISPLAY", "INSPECT", "STRING", "UNSTRING", "MERGE", "RELEASE",
                       "OPEN", "CLOSE", "REWRITE", "DELETE", "START", "INVOKE"):
                if w == "DELETE" and prev in ("IDCAMS",):
                    continue
                c[w] += 1
            if w == "MOVE":
                c["MOVE CORRESPONDING" if nxt in ("CORRESPONDING", "CORR") else "MOVE"] += 1
            if w == "INITIALIZE" and "REPLACING" in t[i:i + 12]:
                c["INITIALIZE REPLACING"] += 1
            if w == "PERFORM":
                c["PERFORM"] += 1
                window = t[i:i + 14]
                if "THRU" in window or "THROUGH" in window:
                    c["PERFORM THRU"] += 1
                if "VARYING" in window:
                    c["PERFORM VARYING"] += 1
                    if "AFTER" in t[i:i + 30] and "TEST" not in t[i:i + 30]:
                        c["PERFORM VARYING AFTER"] += 1
                if "TIMES" in window:
                    c["PERFORM TIMES"] += 1
                if "TEST" in window and "AFTER" in window:
                    c["PERFORM WITH TEST AFTER"] += 1
            if w == "GO" and nxt == "TO":
                c["GO TO DEPENDING ON" if "DEPENDING" in t[i:i + 40] and "." not in t[i:t.index("DEPENDING", i) if "DEPENDING" in t[i:i + 40] else i] else "GO TO"] += 1
            if w == "EXIT" and nxt in ("PARAGRAPH", "SECTION", "PERFORM"):
                c["EXIT PARAGRAPH/SECTION/PERFORM"] += 1
            if w == "EXIT" and nxt == "PROGRAM":
                c["EXIT PROGRAM"] += 1
            if w == "NEXT" and nxt == "SENTENCE":
                c["NEXT SENTENCE"] += 1
            if w == "STOP" and nxt == "RUN":
                c["STOP RUN"] += 1
            if w == "SIZE" and nxt == "ERROR":
                c["NOT ON SIZE ERROR" if "NOT" in t[max(0, i - 2):i] else "ON SIZE ERROR"] += 1
            if w == "ROUNDED":
                c["ROUNDED"] += 1
            if w == ":" and prev != "." and i + 1 < n:
                c["reference modification"] += 1
            if "--" in w and w[0].isalnum() and w[-1].isalnum():
                c["word with consecutive hyphens"] += 1
            if w == "COMPUTE":
                j = i + 1
                ops = 0
                while j < n and t[j] not in (".", "ON", "NOT", "END-COMPUTE") and t[j] not in VERBS:
                    if t[j] in ("+", "-", "*", "/", "**"):
                        ops += 1
                    j += 1
                if ops >= 2:
                    c["COMPUTE with intermediate results"] += 1
            if w == "CALL":
                c["CALL"] += 1
                stmt = t[i:i + 40]
                if "RETURNING" in stmt[:stmt.index(".")] if "." in stmt else "RETURNING" in stmt:
                    c["CALL RETURNING"] += 1
            if w == "SEARCH":
                c["SEARCH ALL" if nxt == "ALL" else "SEARCH"] += 1
            if w == "SET":
                window = t[i:i + 12]
                if nxt == "ADDRESS":
                    c["SET ADDRESS OF"] += 1
                elif "TRUE" in window[:6]:
                    c["SET TO TRUE"] += 1
                elif "FALSE" in window[:6]:
                    c["SET TO FALSE"] += 1
                else:
                    c["SET index"] += 1
            if w == "SORT" and prev != "*":
                c["SORT"] += 1
            if w == "RETURN" and prev not in ("ON",):
                c["RETURN"] += 1
            if w == "READ":
                c["READ PREVIOUS" if "PREVIOUS" in t[i:i + 5] else "READ"] += 1
            if w == "WRITE":
                c["WRITE ADVANCING" if "ADVANCING" in t[i:i + 10] else "WRITE"] += 1
            if w == "EXEC" and nxt in ("SQL", "CICS", "DLI"):
                c["EXEC " + nxt] += 1
            if w in ("INITIATE", "GENERATE", "TERMINATE"):
                c["INITIATE/GENERATE/TERMINATE (Report Writer)"] += 1
            if w in ("XML", "JSON") and nxt in ("GENERATE", "PARSE"):
                c["XML/JSON statements"] += 1
            # data division
            if w in ("COMP-3", "COMPUTATIONAL-3", "PACKED-DECIMAL"):
                c["USAGE COMP-3/PACKED-DECIMAL"] += 1
            elif w in ("COMP", "COMPUTATIONAL", "COMP-4", "COMPUTATIONAL-4", "BINARY"):
                c["USAGE COMP/COMP-4/BINARY"] += 1
            elif w in ("COMP-5", "COMPUTATIONAL-5"):
                c["USAGE COMP-5"] += 1
            elif w in ("COMP-1", "COMP-2", "COMPUTATIONAL-1", "COMPUTATIONAL-2"):
                c["USAGE COMP-1/COMP-2"] += 1
            elif w == "POINTER" and prev == "USAGE":
                c["USAGE POINTER"] += 1
            if w == "SEPARATE":
                c["SIGN SEPARATE"] += 1
            if w == "REDEFINES":
                c["REDEFINES"] += 1
            if w == "66" and prev == ".":
                c["RENAMES (66)"] += 1
            if w == "88" and prev == ".":
                c["88-level"] += 1
            if w == "OCCURS":
                c["OCCURS DEPENDING ON" if "DEPENDING" in t[i:i + 10] else "OCCURS"] += 1
            if w == "INDEXED" and nxt == "BY":
                c["INDEXED BY"] += 1
            if w in ("ASCENDING", "DESCENDING") and "KEY" in t[i:i + 3]:
                c["ASCENDING/DESCENDING KEY"] += 1
            if w in ("JUSTIFIED", "JUST"):
                c["JUSTIFIED"] += 1
            if w == "BLANK" and nxt == "WHEN":
                c["BLANK WHEN ZERO"] += 1
            if w in ("SYNC", "SYNCHRONIZED"):
                c["SYNCHRONIZED"] += 1
            if w == "EXTERNAL":
                c["EXTERNAL"] += 1
            if w == "GLOBAL":
                c["GLOBAL"] += 1
            if w == "VALUE" or w == "VALUES":
                c["VALUE"] += 1
            if w == "LOCAL-STORAGE":
                c["LOCAL-STORAGE SECTION"] += 1
            if w == "LINKAGE":
                c["LINKAGE SECTION"] += 1
            if w == "SD" and prev == ".":
                c["SD (sort file)"] += 1
            if w == "VARYING" and prev in ("IS", "RECORD") and "DEPENDING" in t[i:i + 12]:
                c["RECORD VARYING DEPENDING"] += 1
            if w == "LINAGE":
                c["LINAGE"] += 1
            if w == "REPORT" and nxt == "SECTION":
                c["REPORT SECTION"] += 1
            if w == "ORGANIZATION":
                window = t[i:i + 4]
                c["ORGANIZATION INDEXED" if "INDEXED" in window else "ORGANIZATION RELATIVE" if "RELATIVE" in window
                  else "ORGANIZATION LINE SEQUENTIAL" if "LINE" in window else "FILE STATUS" if False else "ORGANIZATION SEQUENTIAL"] += 1
            if w == "ALTERNATE" and "KEY" in t[i:i + 4]:
                c["ALTERNATE RECORD KEY"] += 1
            if w == "STATUS" and prev == "FILE":
                c["FILE STATUS"] += 1
            if w in ("RECURSIVE", "INITIAL") and prev in ("IS", ".") and "PROGRAM-ID" in t[max(0, i - 6):i]:
                c["RECURSIVE / IS INITIAL"] += 1
            if w == "COPY":
                c["COPY REPLACING" if "REPLACING" in t[i:i + 4] else "COPY"] += 1
            if w == "REPLACE" and prev == ".":
                c["REPLACE"] += 1
            if w == "SECTION" and prev not in ("WORKING-STORAGE", "LINKAGE", "FILE", "LOCAL-STORAGE", "CONFIGURATION", "INPUT-OUTPUT",
                                               "REPORT", "SCREEN", "COMMUNICATION") and nxt == ".":
                c["SECTION"] += 1
            if prev == "." and w.isdigit() and w not in ("66", "77", "88"):
                # a group entry (no PICTURE) stating a USAGE or SIGN clause for its members
                j = t.index(".", i) if "." in t[i:i + 40] else i
                entry = t[i:j]
                if entry and "PIC" not in entry and "PICTURE" not in entry and "REDEFINES" not in entry                         and any(u in entry for u in ("COMP", "COMP-3", "COMP-4", "COMP-5", "BINARY", "PACKED-DECIMAL", "COMPUTATIONAL",
                                                     "COMPUTATIONAL-3", "SIGN", "LEADING", "TRAILING"))                         and j + 1 < n and t[j + 1].isdigit() and int(t[j + 1]) > int(w) and t[j + 1] not in ("66", "77", "88"):
                    c["group-level USAGE/SIGN"] += 1
            if w in ("EJECT", "SKIP1", "SKIP2", "SKIP3"):
                c["EJECT/SKIP"] += 1
            if w == "FUNCTION" and nxt and nxt not in ("PIC", "PICTURE", "VALUE", "OCCURS", "REDEFINES", "USAGE", ".") and prev != ":":
                # (a pseudo-text tag such as :PFX:-FUNCTION PIC X is a data name, not an intrinsic function)
                c["FUNCTION " + nxt] += 1
    return c


def files(root):
    for d, _, fs in os.walk(root):
        for f in fs:
            if os.path.splitext(f)[1].lower() in (".cbl", ".cob", ".cobol", ".cpy", ".cpb", ".copy", ".ccp"):
                yield os.path.join(d, f)


def status_of(construct):
    if construct.startswith("FUNCTION "):
        name = construct.split(" ", 1)[1]
        return ("lowered", "") if name in FUNCTIONS_LOWERED else ("frontier", "intrinsic function not in the supported list")
    return STATUS.get(construct, ("lowered", "") if construct == "ORGANIZATION SEQUENTIAL" else ("unknown", ""))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--estate", action="append", required=True, help="NAME=DIR")
    ap.add_argument("--out", required=True)
    ap.add_argument("--json")
    a = ap.parse_args()
    per = {}
    for e in a.estate:
        name, root = e.split("=", 1)
        fl = list(files(root))
        per[name] = (census(fl), len(fl), sum(1 for p in fl for _ in open(p, encoding="latin-1")))
    names = list(per)
    allc = Counter()
    for c, _, _ in per.values():
        allc.update(c)
    rows = sorted(allc, key=lambda k: (["silent", "frontier", "partial", "unknown", "lowered", "n/a"].index(status_of(k)[0]), k))
    md = ["# COBOL construct census", "",
          "Generated by `tools/census/construct_census.py`; do not edit. Counts are occurrences in programs and copybooks.", "",
          "| Estate | Members | Lines |", "| --- | --- | --- |"]
    md += ["| %s | %d | %d |" % (n, per[n][1], per[n][2]) for n in names]
    by_status = defaultdict(int)
    for k in rows:
        by_status[status_of(k)[0]] += allc[k]
    total = sum(allc.values())
    md += ["", "**Occurrences by status** (all estates): " + ", ".join("%s %d (%.1f%%)" % (s, by_status[s], 100.0 * by_status[s] / max(1, total))
                                                                       for s in ["lowered", "partial", "frontier", "silent", "unknown", "n/a"] if by_status[s]), "",
           "| Construct | Status | " + " | ".join(names) + " | Note |", "| --- | --- | " + " | ".join("---" for _ in names) + " | --- |"]
    for k in rows:
        st, note = status_of(k)
        md.append("| %s | %s | %s | %s |" % (k, st, " | ".join(str(per[n][0].get(k, 0)) or "0" for n in names), note))
    open(a.out, "w", encoding="utf-8").write("\n".join(md) + "\n")
    if a.json:
        json.dump({n: {"members": per[n][1], "lines": per[n][2], "constructs": dict(per[n][0])} for n in names}, open(a.json, "w"), indent=1)
    print("census: %d constructs over %s -> %s" % (len(rows), ", ".join(names), a.out))


if __name__ == "__main__":
    main()
