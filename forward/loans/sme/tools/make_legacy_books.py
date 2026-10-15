"""The legacy loan books of the migration slice (FE 9.2, S9): forward/loans/sme/legacy-books.json, each book's five datasets by
repository and path, with their SHA-256, so that a rebuild can prove it reads the same bytes.

  edge-p12-half_even   the edge corpus (1,207 loans) in the cell p12 HALF_EVEN; legacy output: oracle A of the clean-room
                       estate, equal to the COBOL job's (gate G2); requests and run control as corpus line images
  sample-<cell>        the 28 sample cells (63 loans each); legacy output: the faithful COBOL job (C) of the clean-room
                       project, its datasets as the job left them (fixed-length)
  rejects-p12-half_even the rejects corpus (60 requests, each rejected by the legacy) in C's run

Usage: python -I make_legacy_books.py <CobolBdm root> <fineract-cobol-gen root> <out books.json>
"""
import hashlib
import json
import os
import sys

cobolbdm, fcg, out = sys.argv[1], sys.argv[2], sys.argv[3]
ROOTS = {"cobolbdm": cobolbdm, "fcg": fcg}
ESTATE = "out/tier1-2026-10-12-cr"
RUNS = "projects/fineract-cobol-cr/oracle/runs"


def ds(root, path, fmt):
    full = os.path.join(ROOTS[root], path)
    data = open(full, "rb").read()
    return {"root": root, "path": path, "format": fmt, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def c_run(name, source):
    d = f"{RUNS}/{name}/data"
    return {"name": name, "source": source,
            "request": ds("cobolbdm", f"{d}/LNS.LOAN.REQUEST.DATA", "fixed"),
            "runControl": ds("cobolbdm", f"{d}/LNS.RUN.CONTROL", "fixed"),
            "currency": ds("cobolbdm", f"{d}/LNS.CURRENCY.VSAM.KSDS", "fixed"),
            "schedule": ds("cobolbdm", f"{d}/LNS.LOAN.SCHEDULE.DATA", "fixed"),
            "rejects": ds("cobolbdm", f"{d}/LNS.LOAN.REJECTS.DATA", "fixed")}


books = [{"name": "edge-p12-half_even", "source": "the edge corpus; legacy output oracle A of the clean-room estate (= the COBOL job, G2)",
          "request": ds("fcg", f"{ESTATE}/scenarios/edge-p12-half_even/LOAN.REQUEST.txt", "lines"),
          "runControl": ds("fcg", f"{ESTATE}/scenarios/edge-p12-half_even/RUN.CONTROL.txt", "lines"),
          "currency": ds("fcg", "corpus/CURRENCY.txt", "lines"),
          "schedule": ds("fcg", f"{ESTATE}/oracle-a/edge-p12-half_even/SCHEDULE.dat", "fixed"),
          "rejects": ds("fcg", f"{ESTATE}/oracle-a/edge-p12-half_even/REJECTS.dat", "fixed")}]
for name in sorted(os.listdir(os.path.join(cobolbdm, RUNS))):
    if name.startswith("sample-"):
        books.append(c_run(name, "a sample cell; legacy output the faithful COBOL job (C)"))
books.append(c_run("rejects-p12-half_even", "the rejects corpus; legacy output the faithful COBOL job (C)"))
manifest = {"schema": "mlcpu.legacy-books.v1",
            "about": "The legacy loan books the migration slice reconciles (FE 9.2): five datasets each, by repository root and path. "
                     "Built by sme/tools/make_legacy_books.py; MigrationPool refuses a dataset whose SHA-256 differs.",
            "roots": {"cobolbdm": "the CobolBdm repository", "fcg": "fineract-cobol-gen (the JavaToCOBOLTest repository)"},
            "books": books}
with open(out, "w", encoding="utf-8", newline="\n") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")
print(f"{len(books)} books -> {out}")
