"""Candidates for the calendar pool (slice S2, ForwardEngineeringFineract.md section 7.4): every distinct calendar input with the
periods the faithful program FNDG0100 gives it, from two sources.

* The FNDG0100 CALL traces of the clean-room project (CobolBdm projects/fineract-cobol-cr/work/calltrace/FNDG0100/<scenario>/
  trace.json): the linkage before and after each CALL. Before: FN-LOAN-TERMS (copybook FNTERMS); after: FN-EXPECTED-PERIODS
  (FNXPTBL) and FN-STATUS (FNSTAT). Each CALL is matched, in order, to the loan request of the traced scenario (LOANREQ.dat,
  decoded as the schedule pool decodes FNLNREQ) whose calendar inputs it carries. These are the seam's own oracle.
* The schedule pool (S5, pool/schedule/*.jsonl): a scheduled loan's installment lines carry FNDG0100's periods unchanged (the
  faithful job writes them to FNSCHED, byte-identical to oracle A by G2 and G5).

A candidate is one calendar input: the schedule start, installments, every, frequency, disbursement date and fixed length (the
ScheduleRequest fields DueDates reads). The first loan with that input gives the request; every source that has it must give
the same periods. Usage:

    python -I build_calendar_candidates.py <pool/schedule dir> <FNDG0100 trace dir> <out file>
"""
import json
import os
import sys
from datetime import date, timedelta

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import build_schedule_pool as sp  # noqa: E402  (the FNLNREQ, RUN.CONTROL and CURRENCY decoders)

DAY_ONE = date(1601, 1, 1)  # INTEGER-OF-DATE day 1 (FNDTKRN)
FREQ = {0: "DAYS", 1: "WEEKS", 2: "MONTHS", 3: "YEARS"}


def day(n):
    return (DAY_ONE + timedelta(days=n - 1)).isoformat()


def comp(b, a, n):
    """PIC S9(..) COMP: big-endian two's complement."""
    return int.from_bytes(b[a:a + n], "big", signed=True)


def terms(b):
    """The calendar fields of FN-LOAN-TERMS (FNTERMS, 81 bytes)."""
    if len(b) != 81:
        raise ValueError(f"FN-LOAN-TERMS is {len(b)} bytes, not 81")
    return {
        "submitted": day(comp(b, 19, 4)), "disbursement": day(comp(b, 23, 4)), "seed": day(comp(b, 27, 4)),
        "installments": comp(b, 31, 4), "every": comp(b, 35, 4), "frequency": FREQ.get(comp(b, 39, 2), f"code {comp(b, 39, 2)}"),
        "fixedLength": comp(b, 73, 4) if b[72:73] == b"Y" else None,
    }


def periods(b):
    """FN-EXPECTED-PERIODS (FNXPTBL): XP-COUNT, then 64-byte entries (number, from day, due day, six COMP-3 amounts)."""
    count = comp(b, 0, 2)
    out = []
    for k in range(count):
        e = 2 + 64 * k
        out.append((comp(b, e, 2), day(comp(b, e + 2, 4)), day(comp(b, e + 6, 4))))
    return out


def status(b):
    return comp(b, 0, 2), b[2:10].decode("latin-1").rstrip()


def key(q):
    return json.dumps([q["scheduleStart"], q["installments"], q["every"], q["frequency"], q["disbursementDate"], q["fixedLength"]])


def from_trace(tdir, scen, problems):
    """The traced CALLs of one scenario, each with its loan's request: [(request, periods, origin)]."""
    rc = open(os.path.join(tdir, "RUNCTL.dat"), "rb").read()[:80]
    mc = {"precision": sp.num(rc, 2, 4), "roundingMode": sp.text(rc, 4, 14).rstrip(" ")}
    cur = open(os.path.join(tdir, "CURRFILE.dat"), "rb").read()
    currencies = {sp.text(cur[i:i + 40], 0, 3): cur[i:i + 40] for i in range(0, len(cur), 40)}
    req = open(os.path.join(tdir, "LOANREQ.dat"), "rb").read()
    requests = []
    for i in range(0, len(req), 200):
        try:
            q, _ = sp.request(req[i:i + 200], currencies, mc)
        except (ValueError, ArithmeticError, IndexError):
            q = None
        requests.append(q)
    trace = json.load(open(os.path.join(tdir, "trace.json"), encoding="utf-8"))
    out, at = [], 0
    for c in trace["calls"]:
        before = [s.encode("latin-1") for s in c["before"]]
        after = [s.encode("latin-1") for s in c["after"]]
        t = terms(before[1])
        rcode, reject = status(after[2])
        if rcode != 0:
            problems.append(f"{scen} CALL {c['seq']}: FNDG0100 returned {rcode} {reject} (the API cannot reach it)")
            continue
        want = json.dumps([t["submitted"], t["installments"], t["every"], t["frequency"], t["disbursement"], t["fixedLength"]])
        if t["seed"] != t["disbursement"]:
            problems.append(f"{scen} CALL {c['seq']}: seed day {t['seed']} is not the disbursement day {t['disbursement']}")
        while at < len(requests) and (requests[at] is None or key(requests[at]) != want):
            at += 1
        if at == len(requests):
            problems.append(f"{scen} CALL {c['seq']}: no loan request with the traced terms {want}")
            at = 0
            continue
        q = requests[at]
        at += 1
        ps = periods(after[0])
        if [p[0] for p in ps] != list(range(1, len(ps) + 1)) or len(ps) != t["installments"]:
            problems.append(f"{scen} CALL {c['seq']}: periods numbered {[p[0] for p in ps][:5]}... for {t['installments']} installments")
            continue
        out.append((q, [{"from": f, "due": d} for _, f, d in ps], f"FNDG0100 CALL trace, scenario {scen}, CALL {c['seq']} (loan {q['loanId']})"))
    return out


def main(pool_dir, trace_root, out_file):
    problems = []
    cands = {}

    def add(q, ps, origin, source):
        k = key(q)
        c = cands.get(k)
        if c is None:
            cands[k] = {"input": q, "expected": ps, "origins": [origin], "sources": {source}}
        elif c["expected"] != ps:
            problems.append(f"{origin}: periods differ from {c['origins'][0]}")
        else:
            c["sources"].add(source)
            if len(c["origins"]) < 3:
                c["origins"].append(origin)

    traced = 0
    for scen in sorted(os.listdir(trace_root)):
        tdir = os.path.join(trace_root, scen)
        if not os.path.isfile(os.path.join(tdir, "trace.json")):
            continue
        for q, ps, origin in from_trace(tdir, scen, problems):
            add(q, ps, origin, "trace")
            traced += 1
    scheduled = 0
    for f in sorted(os.listdir(pool_dir)):
        if not f.endswith(".jsonl"):
            continue
        with open(os.path.join(pool_dir, f), encoding="utf-8") as fh:
            for line in fh:
                s = json.loads(line)
                step = s["steps"][0]
                e = step["expected"]
                if e["@type"] != "Scheduled":
                    continue
                ps = [{"from": l["from"], "due": l["due"]} for l in e["schedule"]["lines"] if l["@type"] == "InstallmentLine"]
                add(step["input"], ps, f"the installment lines of the scheduled loan {s['id']} (oracle A, = the faithful job)", "schedule")
                scheduled += 1
    both = sum(1 for c in cands.values() if c["sources"] == {"trace", "schedule"})
    with open(out_file, "w", encoding="utf-8", newline="\n") as fh:
        for k in sorted(cands):
            c = cands[k]
            fh.write(json.dumps({"input": c["input"], "expected": c["expected"], "origins": c["origins"],
                                 "sources": sorted(c["sources"])}, separators=(",", ":")) + "\n")
    print(json.dumps({"tracedCalls": traced, "scheduledLoans": scheduled, "candidates": len(cands),
                      "fromTraces": sum(1 for c in cands.values() if "trace" in c["sources"]), "inBothSources": both}),
          f"{len(problems)} problem(s)")
    for p in problems[:20]:
        print("  problem:", p)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(*sys.argv[1:4]))
