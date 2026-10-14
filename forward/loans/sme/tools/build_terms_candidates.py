"""Corpus candidates for the terms pool (slice S4, ForwardEngineeringFineract.md section 7.4): the loan requests the legacy
envelope rejected (FNLS0100 2200-CHECK-ENVELOPE, codes RJENV020 to RJENV090), decoded to ScheduleRequest as the schedule pool
decodes them, with the legacy code. The terms pool (TermsPool) keeps those the service's limits (decision D-6) still reject and
checks that the reference names the field the legacy code stands for. Requests the API cannot represent (D-5: RJENV001,
RJENV010 and unknown codes) are left out. Usage:

    python -I build_terms_candidates.py <corpus scenarios dir> <oracle A dir> <base corpus dir> <out file>
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import build_schedule_pool as sp  # noqa: E402

FIELDS = {"RJENV020": ["installments"], "RJENV030": ["principal"], "RJENV040": ["currency.decimalPlaces"], "RJENV050": ["annualRatePercent"],
          "RJENV060": ["every"], "RJENV070": ["scheduleStart", "disbursementDate"], "RJENV080": ["downPaymentPercent"],
          "RJENV090": ["fixedLength"]}


def main(corpus, oracle, base, out):
    cands, seen = [], set()
    for scen in sorted(os.listdir(corpus)):
        sdir = os.path.join(corpus, scen)
        name = scen.replace("fineract-unit", "unit")
        odir = sp.scenario_dir(oracle, scen, name)
        if not os.path.isdir(sdir) or not os.path.isdir(odir):
            continue
        rc = sp.records(os.path.join(sdir, "RUN.CONTROL.txt"), 80)[0]
        mc = {"precision": sp.num(rc, 2, 4), "roundingMode": sp.text(rc, 4, 14).rstrip(" ")}
        cur = os.path.join(sdir, "CURRENCY.txt") if os.path.exists(os.path.join(sdir, "CURRENCY.txt")) else os.path.join(base, "CURRENCY.txt")
        currencies = {sp.text(c, 0, 3): c for c in sp.records(cur, 40)}
        _, rejects, _ = sp.oracle_runs(odir)
        for q in sp.records(os.path.join(sdir, "LOAN.REQUEST.txt"), 200):
            loan = sp.text(q, 2, 18)
            code = rejects.get(loan)
            if code not in FIELDS:
                continue
            try:
                req, _ = sp.request(q, currencies, mc)
            except (ValueError, ArithmeticError, IndexError):
                req = None
            if req is None:
                continue
            key = json.dumps({k: v for k, v in req.items() if k not in ("loanId", "calculation")}, sort_keys=True) + code
            if key in seen:
                continue
            seen.add(key)
            cands.append({"input": req, "legacyCode": code, "fields": FIELDS[code],
                          "origin": f"corpus {name}, loan {loan}: the legacy envelope reject {code} (oracle A, = the faithful job)"})
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        for c in cands:
            f.write(json.dumps(c, separators=(",", ":")) + "\n")
    by = {}
    for c in cands:
        by[c["legacyCode"]] = by.get(c["legacyCode"], 0) + 1
    print(json.dumps({"candidates": len(cands), "byCode": by}))


if __name__ == "__main__":
    main(*sys.argv[1:5])
