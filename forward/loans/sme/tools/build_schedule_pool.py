"""The schedule pool (slice S5, ForwardEngineeringFineract.md section 7.4): every loan of the corpus as a scenario of the loans
service. The request comes from the loan request record (FNLNREQ), the run control (FNRUNCT) and the currency file (FNCURR);
the expected outcome from oracle A's schedule (FNSCHED) and reject (FNREJCT) records of the clean-room estate, which the
faithful programs (C) reproduce byte for byte (gates G2 and G5).

Usage: python -I build_schedule_pool.py <corpus scenarios dir> <oracle A dir> <base corpus dir> <out dir> [D-n=<oracle A' dir> ...] [LABEL=<origin>]

Writes <out>/<corpus scenario>.jsonl (one scenario per line: id, slice, origin, steps[{call, input, expected}]) and
<out>/pool.json (counts, and the requests set aside with the reason). A request is set aside when the API cannot represent
it (FE decision D-5: an unknown frequency, day-count or method code; a malformed record; a currency missing from the file)
or when the legacy envelope rejected it (D-6: the service's limits differ; those loans need oracle A without the envelope,
work item W2). A schedule without a disbursement line becomes the rejection DISBURSEMENT_OUTSIDE_TERM (D-3).

Decisions that fix a legacy defect (FE section 6, F6) take their expected values from oracle A' of that decision: Fineract with
the defect patched out (fineract-cobol-gen scripts/aprime-build.sh, cr-oracle.sh with A_PRIME). A loan whose A' records differ
from A's gets the A' outcome, its origin names the decision, and pool.json lists it under "decisions". A' may differ from A only
on scheduled loans of the pool; any other difference is a problem.

Privileged scenarios (decision D-6, work item W2): with LABEL=<origin>, the oracle dir is a run without the envelope (FnHarness
--forward-limits, tools/make_privileged.py's requests) and every scenario's origin is that label. An envelope reject then means
outside the service's limits as well (the terms slice's), and RJCAP001 a schedule the records cannot hold: both set aside."""
import json, os, sys
from decimal import Decimal

# ---------------------------------------------------------------- record layouts (copybooks FNLNREQ, FNSCHED, FNREJCT, FNRUNCT, FNCURR)

def text(r, a, b):
    return r[a:b].decode("latin-1")


def num(r, a, b):
    return int(text(r, a, b))


def signed(r, a, b, scale):
    """S9(n)V9(scale) SIGN LEADING SEPARATE."""
    s = text(r, a, b)
    v = Decimal(s[1:]).scaleb(-scale)
    return -v if s[0] == "-" else v


def date(r, a, b):
    s = text(r, a, b)
    return f"{s[0:4]}-{s[4:6]}-{s[6:8]}"


def records(path, lrecl):
    """A .txt dataset image: one record per line, padded or cut to LRECL (FnHarness.records)."""
    out = []
    with open(path, "rb") as f:
        for line in f.read().split(b"\n"):
            line = line.rstrip(b"\r")
            if not line:
                continue
            out.append(line[:lrecl].ljust(lrecl, b" "))
    return out


def fixed(path, lrecl):
    d = open(path, "rb").read() if os.path.exists(path) else b""
    return [d[i:i + lrecl] for i in range(0, len(d), lrecl)]


FREQUENCY = {"DAYS": "DAYS", "WEEKS": "WEEKS", "MONTHS": "MONTHS", "YEARS": "YEARS"}
MONTH_DAYS = {1: "ACTUAL", 30: "THIRTY"}
YEAR_DAYS = {1: "ACTUAL", 360: "DAYS_360", 364: "DAYS_364", 365: "DAYS_365"}
METHODS = {"DECLINING_BALANCE": "DECLINING_BALANCE", "FLAT": "FLAT"}
REJECTIONS = {"RJTNNT01": "FLAT_INTEREST_NOT_OFFERED", "RJTNNT02": "FULL_TERM_TRANCHE_NOT_OFFERED", "RJTNNT03": "ROUNDING_MULTIPLE_NOT_OFFERED",
              "RJTNNT04": "SINGLE_INSTALLMENT_AFTER_LATE_DISBURSEMENT", "RJTNNT05": "DOWN_PAYMENT_COVERS_PRINCIPAL",
              "RJUNSP01": "PERIOD_NOT_SUPPORTED_WITH_THIRTY_DAY_MONTHS", "RJNULL01": "DOWN_PAYMENT_PERCENT_MISSING",
              "RJNULL02": "FIXED_LENGTH_WITHOUT_ADVANCING_FREQUENCY"}
# Rejects whose cause the API cannot express (D-5): the request would not be representable.
UNREPRESENTABLE_REJECTS = {"RJFREQ01", "RJUNSP02", "RJARIT01", "RJUNSP03"}


def plain(d):
    return format(d, "f")


def at_scale(v, places, where, problems):
    """An amount at the currency's decimal places (Money in the original); a value that is not exact there is kept as it is."""
    q = v.quantize(Decimal(1).scaleb(-places))
    if q != v:
        problems.append(f"{where}: {v} is not exact at {places} decimal places")
        return plain(v)
    return plain(q)


def request(q, currencies, mc):
    """The ScheduleRequest of a loan request record; (None, reason) when the API cannot represent it."""
    if text(q, 0, 2) != "LR":
        return None, "record type is not LR"
    loan = text(q, 2, 18)
    cur = currencies.get(text(q, 26, 29))
    if cur is None:
        return None, "currency not in the currency file"
    freq = text(q, 63, 73).rstrip(" ")
    if freq not in FREQUENCY:
        return None, f"frequency {freq!r}"
    try:
        dim, diy = num(q, 87, 89), num(q, 89, 92)
    except ValueError:
        return None, "day counts not numeric"
    if dim not in MONTH_DAYS:
        return None, f"days in month {dim}"
    if diy not in YEAR_DAYS:
        return None, f"days in year {diy}"
    method = text(q, 143, 161).rstrip(" ")
    if method not in METHODS:
        return None, f"interest method {method!r}"
    places, mult_ind, mult = num(cur, 3, 5), text(cur, 5, 6), num(cur, 6, 11)
    return {
        "loanId": loan,
        "scheduleStart": date(q, 18, 26),
        "currency": {"code": text(cur, 0, 3), "decimalPlaces": places, "roundingMultiple": mult if mult_ind == "Y" else None},
        "principal": plain(signed(q, 29, 47, 4)),
        "disbursementDate": date(q, 47, 55),
        "installments": num(q, 55, 59),
        "every": num(q, 59, 63),
        "frequency": FREQUENCY[freq],
        "annualRatePercent": plain(signed(q, 73, 86, 9)),
        "downPaymentPercent": plain(signed(q, 93, 106, 9)) if text(q, 92, 93) == "Y" else None,
        "downPaymentEnabled": text(q, 86, 87) == "Y",
        "monthDays": MONTH_DAYS[dim],
        "yearDays": YEAR_DAYS[diy],
        "fixedLength": num(q, 117, 122) if text(q, 116, 117) == "Y" else None,
        "interestMethod": METHODS[method],
        "fullTermTranche": text(q, 162, 163) == "Y",
        "calculation": mc,
    }, None


def schedule(loan, recs, problems):
    h = recs[0]
    places = num(h, 24, 26)
    amt = lambda r, a, where: at_scale(signed(r, a, a + 18, 4), places, f"loan {loan} {where}", problems)
    totals = {k: amt(h, 36 + 18 * i, "totals." + k) for i, k in
              enumerate(["disbursed", "principal", "interest", "fee", "penalty", "repayment", "outstanding"])}
    lines = []
    for r in recs[1:]:
        kind, n = text(r, 16, 17), num(r, 21, 25)
        frm, due = date(r, 25, 33), date(r, 33, 41)
        a = lambda i, k: amt(r, 41 + 18 * i, f"line {num(r, 17, 21)} {k}")
        if kind == "D":
            lines.append({"@type": "DisbursementLine", "from": frm, "due": due, "amount": a(0, "amount"), "balanceAfter": a(5, "balance")})
        elif kind == "P":
            lines.append({"@type": "DownPaymentLine", "number": n, "from": frm, "due": due, "principal": a(0, "principal"),
                          "totalDue": a(4, "totalDue"), "balanceAfter": a(5, "balance"), "remainingDue": a(6, "remainingDue")})
        elif kind == "R":
            lines.append({"@type": "InstallmentLine", "number": n, "from": frm, "due": due, "principal": a(0, "principal"),
                          "interest": a(1, "interest"), "fee": a(2, "fee"), "penalty": a(3, "penalty"), "totalDue": a(4, "totalDue"),
                          "balanceAfter": a(5, "balance"), "remainingDue": a(6, "remainingDue")})
        else:
            problems.append(f"loan {loan}: record type {kind!r}")
    if num(h, 32, 36) != len(lines):
        problems.append(f"loan {loan}: header period count {num(h, 32, 36)} but {len(lines)} lines")
    return {"loanId": loan, "currencyCode": text(h, 21, 24), "decimalPlaces": places, "termDays": num(h, 26, 32), "totals": totals,
            "lines": lines}


def oracle_runs(odir):
    """A scenario's oracle output: schedule records and reject code by loan id."""
    by_loan = {}
    for r in fixed(os.path.join(odir, "SCHEDULE.dat"), 200):
        by_loan.setdefault(text(r, 0, 16), []).append(r)
    rejects = {text(r, 0, 16): text(r, 16, 24) for r in fixed(os.path.join(odir, "REJECTS.dat"), 120)}
    raw_rejects = {text(r, 0, 16): r for r in fixed(os.path.join(odir, "REJECTS.dat"), 120)}
    return by_loan, rejects, raw_rejects


def scenario_dir(oracle, scen, name):
    return os.path.join(oracle, scen) if os.path.isdir(os.path.join(oracle, scen)) else os.path.join(oracle, name)


def main(corpus, oracle, base, out, *aprimes):
    os.makedirs(out, exist_ok=True)
    decisions = dict(a.split("=", 1) for a in aprimes)
    label = decisions.pop("LABEL", None)
    pool = {"scenarios": {}, "setAside": [], "problems": [], "decisions": {d: [] for d in decisions}}
    for scen in sorted(os.listdir(corpus)):
        sdir = os.path.join(corpus, scen)
        name = scen.replace("fineract-unit", "unit")
        odir = scenario_dir(oracle, scen, name)
        if not os.path.isdir(sdir) or not os.path.isdir(odir):
            continue
        fixes = {}
        for d, adir in decisions.items():
            fdir = scenario_dir(adir, scen, name)
            if not os.path.isdir(fdir):
                pool["problems"].append(f"{name}: no oracle A' {d} run")
                continue
            fixes[d] = oracle_runs(fdir)
        rc = records(os.path.join(sdir, "RUN.CONTROL.txt"), 80)[0]
        mc = {"precision": num(rc, 2, 4), "roundingMode": text(rc, 4, 14).rstrip(" ")}
        cur_file = os.path.join(sdir, "CURRENCY.txt") if os.path.exists(os.path.join(sdir, "CURRENCY.txt")) else os.path.join(base, "CURRENCY.txt")
        currencies = {text(c, 0, 3): c for c in records(cur_file, 40)}
        by_loan, rejects, raw_rejects = oracle_runs(odir)
        counts = {"loans": 0, "scheduled": 0, "rejected": 0, "setAside": 0}
        scenarios = []
        for q in records(os.path.join(sdir, "LOAN.REQUEST.txt"), 200):
            counts["loans"] += 1
            loan = text(q, 2, 18)
            try:
                req, why = request(q, currencies, mc)
            except (ValueError, ArithmeticError, IndexError) as e:  # decimal.InvalidOperation is an ArithmeticError
                req, why = None, f"malformed record ({type(e).__name__})"
            code = rejects.get(loan)
            # a decision whose oracle A' differs from A on this loan
            fixed_by = [d for d, (fb, _, fr) in fixes.items() if fb.get(loan) != by_loan.get(loan) or fr.get(loan) != raw_rejects.get(loan)]
            if len(fixed_by) > 1:
                pool["problems"].append(f"{name} loan {loan}: oracle A' of {fixed_by} all differ from A")
                continue
            source, records_of = label or "oracle A (clean room), = the faithful job", by_loan
            if fixed_by:
                d = fixed_by[0]
                if req is None or code is not None or fixes[d][1].get(loan) is not None:
                    pool["problems"].append(f"{name} loan {loan}: oracle A' {d} differs from A on a loan that is not scheduled")
                    continue
                records_of = fixes[d][0]
                source = f"oracle A' {d} (clean room): decision {d} fixes a legacy defect; A differs"
                pool["decisions"][d].append(f"{name}-{loan}")
            if req is None or code in UNREPRESENTABLE_REJECTS:
                pool["setAside"].append({"scenario": name, "loan": loan, "reason": why or f"legacy reject {code}: cause not expressible (D-5)"})
                counts["setAside"] += 1
                continue
            if code == "RJCAP001" or (label and code is not None and code.startswith("RJENV")):
                why = "beyond the record capacity (no oracle)" if code == "RJCAP001" else f"outside the service's limits ({code})"
                pool["setAside"].append({"scenario": name, "loan": loan, "reason": why})
                counts["setAside"] += 1
                continue
            if code is not None and code.startswith("RJENV"):
                pool["setAside"].append({"scenario": name, "loan": loan, "reason": f"legacy envelope reject {code} (D-6: needs oracle A without the envelope, W2)"})
                counts["setAside"] += 1
                continue
            if code is not None:
                if code not in REJECTIONS:
                    pool["problems"].append(f"{name} loan {loan}: reject code {code} has no mapping")
                    continue
                expected = {"@type": "Rejected", "loanId": loan, "reason": REJECTIONS[code], "field": None}
                counts["rejected"] += 1
            elif loan in records_of:
                recs = records_of[loan]
                if text(recs[0], 16, 17) != "H":
                    pool["problems"].append(f"{name} loan {loan}: schedule does not start with a header")
                    continue
                s = schedule(loan, recs, pool["problems"])
                if not any(l["@type"] == "DisbursementLine" for l in s["lines"]):
                    expected = {"@type": "Rejected", "loanId": loan, "reason": "DISBURSEMENT_OUTSIDE_TERM", "field": None}
                    counts["rejected"] += 1
                else:
                    expected = {"@type": "Scheduled", "schedule": s}
                    counts["scheduled"] += 1
            else:
                pool["problems"].append(f"{name} loan {loan}: neither a schedule nor a reject")
                continue
            scenario = {"id": f"{'privileged-' if label else ''}{name}-{loan}", "slice": "schedule",
                        "origin": f"{'privileged ' if label else 'corpus '}{name}, loan {loan}: {source}",
                        "steps": [{"call": "schedule", "input": req, "expected": expected}]}
            scenarios.append(json.dumps(scenario, separators=(",", ":")))
        with open(os.path.join(out, name + ".jsonl"), "w", encoding="utf-8", newline="\n") as f:
            f.write("".join(s + "\n" for s in scenarios))
        pool["scenarios"][name] = counts
    totals = {k: sum(c[k] for c in pool["scenarios"].values()) for k in ("loans", "scheduled", "rejected", "setAside")}
    pool["totals"] = totals
    with open(os.path.join(out, "pool.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(pool, f, indent=2)
        f.write("\n")
    print(json.dumps(totals), json.dumps({d: len(v) for d, v in pool["decisions"].items()}), f"{len(pool['problems'])} problem(s)")
    for p in pool["problems"][:20]:
        print("  problem:", p)


if __name__ == "__main__":
    main(*sys.argv[1:])
