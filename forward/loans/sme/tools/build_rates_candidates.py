"""Candidates for the rates pool (slice S3, ForwardEngineeringFineract.md section 7.4): the rate factors the faithful program
FNEM0300 computes for each stretch of a period, decoded from its CALL traces (the capability kit's, CobolBdm
projects/fineract-cobol-cr/work/calltrace/FNEM0300/<scenario>/trace.json, and more from tools/trace_rates.py: the linkage
before and after each CALL).

FNEM0300's arguments: LK-RATE-FACTOR-REQUEST (FNLKEM03: function, from and to period, date), LM-MODEL (FNMODEL: the periods
and their stretches), FN-LOAN-TERMS (FNTERMS), FN-MATH-CONTEXT (FNMC) and FN-STATUS (FNSTAT).
* Function P rates the stretches of periods RF-FROM-RP to RF-TO-RP: for each, the factor of the stretch itself
  (IP-RATE-FACTOR, the SPI's `installment`) and the factor from its start to the period's due date (IP-RF-TILL-DUE, `interest`).
* Function T first cuts the stretch containing RF-DATE at that date, rates every stretch, then zeroes the stretches that end
  after the date. The zeroing is the schedule's rule, not the SPI's: those stretches are left out; the others are queries with
  their dates as cut.
A CALL that ends with a rejection (FN-STATUS 4) gives one candidate: its first stretch's query, expecting the rejection (the
pool tool checks that the reference agrees on which stretch rejects).

Each candidate is one RateQuery and its Calculation (the API's JSON) with the expected outcome; the factors as the trace holds
them (12 decimals). Usage:
    python -I build_rates_candidates.py <FNEM0300 trace dir> <out file>
"""
import json
import os
import sys
from datetime import date, timedelta
from decimal import Decimal

DAY_ONE = date(1601, 1, 1)
FREQ = {0: "DAYS", 1: "WEEKS", 2: "MONTHS", 3: "YEARS"}
MONTH_DAYS = {1: "ACTUAL", 30: "THIRTY"}
YEAR_DAYS = {1: "ACTUAL", 360: "DAYS_360", 364: "DAYS_364", 365: "DAYS_365"}
MODES = {"U": "UP", "D": "DOWN", "C": "CEILING", "F": "FLOOR", "H": "HALF_UP", "N": "HALF_DOWN", "E": "HALF_EVEN"}
REJECTS = {"RJUNSP01": "PERIOD_NOT_SUPPORTED_WITH_THIRTY_DAY_MONTHS"}
HEADER, RP, IP = 15, 546, 83
RP_IP_COUNT, RP_IPS = 212, 214


def day(n):
    return (DAY_ONE + timedelta(days=n - 1)).isoformat()


def day_num(iso):
    return (date.fromisoformat(iso) - DAY_ONE).days + 1


def comp(b, a, n):
    return int.from_bytes(b[a:a + n], "big", signed=True)


def packed(b, a, n, scale):
    """COMP-3: digits in nibbles, the sign in the last nibble (D negative)."""
    raw = b[a:a + n].hex()
    digits, sign = raw[:-1], raw[-1]
    v = Decimal(int(digits)).scaleb(-scale)
    return -v if sign in "dD" else v


def model(b):
    """[(period from, period due, [(stretch from, stretch due, installment factor, interest factor)])]"""
    out = []
    for k in range(comp(b, 0, 2)):
        r = HEADER + RP * k
        ips = []
        for j in range(comp(b, r + RP_IP_COUNT, 2)):
            i = r + RP_IPS + IP * j
            ips.append((day(comp(b, i, 4)), day(comp(b, i + 4, 4)), packed(b, i + 8, 8, 12), packed(b, i + 16, 8, 12)))
        out.append((day(comp(b, r, 4)), day(comp(b, r + 4, 4)), ips))
    return out


def terms(b):
    return {"scheduleStart": day(comp(b, 19, 4)), "annualRatePercent": format(packed(b, 41, 7, 9), "f"),
            "frequency": FREQ.get(comp(b, 39, 2)), "every": comp(b, 35, 4), "monthDays": MONTH_DAYS.get(comp(b, 48, 2)),
            "yearDays": YEAR_DAYS.get(comp(b, 50, 2))}


def decode(path, scen, problems, seen):
    """The candidates of one trace file; seen: the keys already taken (duplicates are dropped). Returns (candidates, CALLs)."""
    cands, calls = [], 0
    for c in json.load(open(path, encoding="utf-8"))["calls"]:
        calls += 1
        before = [s.encode("latin-1") for s in c["before"]]
        after = [s.encode("latin-1") for s in c["after"]]
        fn = before[0][0:1].decode("latin-1")
        frm, to, at = comp(before[0], 1, 2), comp(before[0], 3, 2), comp(before[0], 5, 4)
        t = terms(before[2])
        mc = {"precision": comp(before[3], 0, 2), "roundingMode": MODES.get(before[3][2:3].decode("latin-1"))}
        if None in t.values() or mc["roundingMode"] is None:
            problems.append(f"{scen} CALL {c['seq']}: terms or calculation not representable {t} {mc}")
            continue
        rc, code = comp(after[4], 0, 2), after[4][2:10].decode("latin-1").rstrip()
        m_before, m_after = model(before[1]), model(after[1])
        if fn == "P":
            ks = range(frm - 1, to)
        elif fn == "T":
            ks = range(len(m_after))
        else:
            problems.append(f"{scen} CALL {c['seq']}: function {fn!r}")
            continue
        origin = f"FNEM0300 CALL trace, {scen}, CALL {c['seq']} (function {fn})"
        if rc != 0:
            if code not in REJECTS:
                problems.append(f"{scen} CALL {c['seq']}: status {rc} {code}")
                continue
            pf, pd, ips = (m_after if fn == "T" else m_before)[ks[0]]
            q = dict(t, period={"from": pf, "due": pd}, **{"from": ips[0][0], "to": ips[0][1]})
            key = json.dumps([q, mc, "rejected"], sort_keys=True)
            if key not in seen:
                seen.add(key)
                cands.append({"query": q, "calculation": mc, "expected": {"@type": "RateRejected", "reason": REJECTS[code]},
                              "origin": origin + ", rejected", "rejection": True})
            continue
        for k in ks:
            pf, pd, ips_after = m_after[k]
            ips_dates = (m_after if fn == "T" else m_before)[k][2]
            for j, (sf, sd, inst, intr) in enumerate(ips_after):
                if fn == "T" and day_num(sd) > at:
                    continue  # zeroed by the till-date rule (the schedule's rule, not the SPI's)
                q = dict(t, period={"from": pf, "due": pd}, **{"from": ips_dates[j][0], "to": ips_dates[j][1]})
                key = json.dumps([q, mc], sort_keys=True)
                if key in seen:
                    continue
                seen.add(key)
                cands.append({"query": q, "calculation": mc, "origin": origin,
                              "expected": {"@type": "Factors", "installment": format(inst, "f"), "interest": format(intr, "f")}})
    return cands, calls


def main(trace_root, out_file):
    problems, cands, seen, calls = [], [], set(), 0
    for scen in sorted(os.listdir(trace_root)):
        f = os.path.join(trace_root, scen, "trace.json")
        if os.path.isfile(f):
            c, n = decode(f, f"scenario {scen}", problems, seen)
            cands += c
            calls += n
    with open(out_file, "w", encoding="utf-8", newline="\n") as fh:
        for c in cands:
            fh.write(json.dumps(c, separators=(",", ":")) + "\n")
    print(json.dumps({"tracedCalls": calls, "candidates": len(cands), "rejections": sum(1 for c in cands if c.get("rejection"))}),
          f"{len(problems)} problem(s)")
    for p in problems[:20]:
        print("  problem:", p)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(*sys.argv[1:3]))
