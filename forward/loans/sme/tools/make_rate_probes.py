"""Probe scenarios for rate rules the corpus does not reach (ForwardEngineeringFineract.md section 7.2, step 3: every rule needs a
scenario that kills its mutant). The expected values still come from the faithful program: these are loan requests to trace
(tools/trace_rates.py), not expectations.

RATE-08 (a period that is not a whole number of installment intervals, with 30-day months) needs monthly due dates off the
schedule start's grid: a start on the 29th or 30th with a disbursement on the 31st, so the month-end day (CAL-07) moves the due
dates to month ends the start's day never reaches. The probes are the monthly 30-day-month loans of a corpus scenario with their
start moved to day 29 or 30 and their disbursement to day 31 of the same month (January, March, May, July, August, October,
December: months with 31 days), loan ids and order kept.

RATE-05 (actual days in the month) rounds the stretch's day count to the precision before dividing by the days in the year; it
shows only when the count has more digits than the precision (a stretch of 1,000 days or more at precision 3), which the corpus
almost never has (3 loans of an edge cell). The probes are the actual-month-day loans of a corpus scenario (days in the year not
actual: those cross years, RATE-04) with their installments every 3 years (1,095 or 1,096 days) and at most 20 of them, loan
ids and order kept. Found by the shadow run (G11) and the S5 sweep: a model's engine that left the days exact.

Usage: python -I make_rate_probes.py <corpus scenario dir> <out scenario dir> [max loans, 120] [rule: RATE-08 (default), RATE-05]
"""
import os
import sys

MONTHS_31 = [1, 3, 5, 7, 8, 10, 12]


def main(src, out, limit=120, rule="RATE-08"):
    limit = int(limit)
    os.makedirs(out, exist_ok=True)
    lines = [l.rstrip("\r") for l in open(os.path.join(src, "LOAN.REQUEST.txt"), encoding="latin-1").read().split("\n") if l.strip()]
    probes = []
    for i, q in enumerate(lines):
        q = q.ljust(200)
        if rule == "RATE-05":
            if q[0:2] != "LR" or q[87:89] == "30" or q[89:92] == "001":
                continue
            probes.append(q[:55] + f"{min(int(q[55:59]), 20):04d}" + "0003" + "YEARS".ljust(10) + q[73:])
            if len(probes) >= limit:
                break
            continue
        if q[0:2] != "LR" or q[63:73].rstrip() != "MONTHS" or q[87:89] != "30":
            continue
        year = q[18:22]
        month = MONTHS_31[len(probes) % len(MONTHS_31)]
        start_day = 29 + (len(probes) % 2)
        start = f"{year}{month:02d}{start_day:02d}"
        disbursed = f"{year}{month:02d}31"
        probes.append(q[:18] + start + q[26:47] + disbursed + q[55:])
        if len(probes) >= limit:
            break
    open(os.path.join(out, "LOAN.REQUEST.txt"), "w", encoding="latin-1", newline="\n").write("".join(p.rstrip() + "\n" for p in probes))
    rc = os.path.join(src, "RUN.CONTROL.txt")
    open(os.path.join(out, "RUN.CONTROL.txt"), "w", encoding="latin-1", newline="\n").write(open(rc, encoding="latin-1").read())
    if os.path.exists(os.path.join(src, "CURRENCY.txt")):
        open(os.path.join(out, "CURRENCY.txt"), "w", encoding="latin-1", newline="\n").write(open(os.path.join(src, "CURRENCY.txt"), encoding="latin-1").read())
    print(f"{len(probes)} probe loan(s) from {src}")


if __name__ == "__main__":
    main(*sys.argv[1:5])
