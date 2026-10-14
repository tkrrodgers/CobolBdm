"""Document candidates (slice S6, rules RPT; ForwardEngineeringFineract.md section 7.4): the faithful job's printed schedules.
The clean-room project's published job (C = the COBOL by G5) printed each scheduled loan in LNS.LOAN.SCHEDULE.REPORT (FNRP0100);
each loan's report is decoded into the API's ScheduleDocument and checked against the S5 pool's schedule of the same loan
(oracle A, A' for D-2) projected by the RPT rules: they must agree in every value except a D-2 loan's balances, where the legacy
report printed the stale balance and the expected value is A''s. The pool's rejected loans of the same runs give RPT-05's
candidates (the document is the schedule's rejection); a D-3 loan (no disbursement in any period) was printed as an all-zero
schedule and is such a rejection, and a loan the pool set aside (D-5) is left out.

Usage: python -I build_document_candidates.py <runs/publish dir> <S5 pool dir> <out candidates.jsonl>
"""
import json, os, re, sys
from decimal import Decimal

LRECL = 133
TITLE = re.compile(r"LOAN (\d{16})\s+CURRENCY (.{3})\s+DECIMALS (\d)\s+TERM DAYS\s+([\d,]+)\s+PERIODS\s+(\d+)")
TYPES = {"D": "DISBURSEMENT", "P": "DOWN_PAYMENT", "R": "INSTALLMENT"}


def amount(field, places, where, problems):
    """An edited amount (-Z,ZZZ,ZZZ,ZZZ,ZZ9.9999, the sign in the first position) at the currency's decimal places."""
    t = field.replace(",", "").replace(" ", "")
    neg = t.startswith("-")
    v = Decimal(t.lstrip("-"))
    v = -v if neg else v
    q = v.quantize(Decimal(1).scaleb(-places))
    if q != v:
        problems.append(f"{where}: {v} is not exact at {places} decimal places")
    return format(q, "f")


def documents(path, problems):
    """The loans of a report: loan id -> ScheduleDocument (canonical JSON)."""
    data = open(path, "rb").read().decode("latin-1")
    recs = [data[i:i + LRECL] for i in range(0, len(data), LRECL)]
    out, doc, places = {}, None, 0
    for r in recs:
        asa, line = r[0], r[1:]
        if asa == "1":
            m = TITLE.search(line)
            if not m:
                problems.append(f"{path}: title not understood: {line.strip()[:80]}")
                doc = None
                continue
            places = int(m.group(3))
            doc = {"loanId": m.group(1), "currencyCode": m.group(2), "decimalPlaces": places, "termDays": int(m.group(4).replace(",", "")),
                   "periods": int(m.group(5)), "lines": [], "totalPrincipal": None, "totalInterest": None, "totalRepayment": None,
                   "totalDisbursed": None}
            out[doc["loanId"]] = doc
        elif doc is None:
            continue
        elif line.startswith("T  NO."):
            continue
        elif line.startswith("   TOTALS"):
            w = f"loan {doc['loanId']} totals"
            doc["totalPrincipal"] = amount(line[28:51], places, w, problems)
            doc["totalInterest"] = amount(line[53:76], places, w, problems)
            doc["totalRepayment"] = amount(line[78:101], places, w, problems)
            doc["totalDisbursed"] = amount(line[103:126], places, w, problems)
        elif line[0] in TYPES:
            w = f"loan {doc['loanId']} line {len(doc['lines']) + 1}"
            doc["lines"].append({"type": TYPES[line[0]], "number": int(line[2:6]), "from": line[7:17], "due": line[18:28],
                                 "principal": amount(line[29:52], places, w, problems), "interest": amount(line[54:77], places, w, problems),
                                 "totalDue": amount(line[79:102], places, w, problems), "outstanding": amount(line[104:127], places, w, problems)})
        else:
            problems.append(f"{path}: line not understood: {r.strip()[:80]}")
    return out


def project(s):
    """The RPT rules on a schedule (the pool's expected JSON)."""
    zero = format(Decimal(0).quantize(Decimal(1).scaleb(-s["decimalPlaces"])), "f")
    lines = []
    for l in s["lines"]:
        if l["@type"] == "DisbursementLine":
            lines.append({"type": "DISBURSEMENT", "number": 0, "from": l["from"], "due": l["due"], "principal": l["amount"], "interest": zero,
                          "totalDue": zero, "outstanding": l["balanceAfter"]})
        elif l["@type"] == "DownPaymentLine":
            lines.append({"type": "DOWN_PAYMENT", "number": l["number"], "from": l["from"], "due": l["due"], "principal": l["principal"],
                          "interest": zero, "totalDue": l["totalDue"], "outstanding": l["balanceAfter"]})
        else:
            lines.append({"type": "INSTALLMENT", "number": l["number"], "from": l["from"], "due": l["due"], "principal": l["principal"],
                          "interest": l["interest"], "totalDue": l["totalDue"], "outstanding": l["balanceAfter"]})
    t = s["totals"]
    return {"loanId": s["loanId"], "currencyCode": s["currencyCode"], "decimalPlaces": s["decimalPlaces"], "termDays": s["termDays"],
            "periods": len(lines), "lines": lines, "totalPrincipal": t["principal"], "totalInterest": t["interest"],
            "totalRepayment": t["repayment"], "totalDisbursed": t["disbursed"]}


def differences(a, b, path=""):
    if isinstance(a, dict) and isinstance(b, dict):
        out = []
        for k in a:
            out += differences(a[k], b.get(k), f"{path}.{k}")
        return out
    if isinstance(a, list) and isinstance(b, list) and len(a) == len(b):
        out = []
        for i, (x, y) in enumerate(zip(a, b)):
            out += differences(x, y, f"{path}[{i}]")
        return out
    return [] if a == b else [path]


def main(runs, pool, out):
    problems, cands = [], []
    counts = {"reported": 0, "equal": 0, "D-2": 0, "rejected": 0, "runs": 0}
    # loans the pool set aside (D-5: the API cannot represent them) were still printed
    set_aside = {(a["scenario"], a["loan"]) for a in json.load(open(os.path.join(pool, "pool.json"), encoding="utf-8"))["setAside"]}
    for scen in sorted(os.listdir(runs)):
        report = os.path.join(runs, scen, "FNSCHRPT", "data", "LNS.LOAN.SCHEDULE.REPORT")
        pool_file = os.path.join(pool, scen + ".jsonl")
        if not os.path.isfile(report) or not os.path.isfile(pool_file):
            continue
        counts["runs"] += 1
        docs = documents(report, problems)
        for line in open(pool_file, encoding="utf-8"):
            sc = json.loads(line)
            step = sc["steps"][0]
            loan, expected = step["input"]["loanId"], step["expected"]
            if expected["@type"] == "Rejected":
                counts["rejected"] += 1
                if docs.pop(loan, None) is not None and expected["reason"] != "DISBURSEMENT_OUTSIDE_TERM":
                    problems.append(f"{scen} loan {loan}: rejected in the pool ({expected['reason']}), printed in the report")
                origin = f"corpus {scen}, loan {loan}: the schedule's rejection (RPT-05), {sc['origin'].split(': ', 1)[-1]}"
                cands.append({"id": f"{scen}-{loan}", "slice": "disclosure", "origin": origin,
                              "steps": [{"call": "document", "input": step["input"], "expected": expected}]})
                continue
            if loan not in docs:
                problems.append(f"{scen} loan {loan}: scheduled in the pool, not in the report")
                continue
            counts["reported"] += 1
            printed, projected = docs.pop(loan), project(expected["schedule"])
            diff = differences(projected, printed)
            if diff and "decision D-2" in sc["origin"] and all(d.endswith(".outstanding") for d in diff):
                counts["D-2"] += 1
                origin = f"corpus {scen}, loan {loan}: the faithful job's report (FNRP0100) with A' balances (decision D-2)"
            elif diff:
                problems.append(f"{scen} loan {loan}: the report differs from the pool's schedule at {', '.join(diff[:4])}")
                continue
            else:
                counts["equal"] += 1
                origin = f"corpus {scen}, loan {loan}: the faithful job's report (FNRP0100), = the pool's schedule"
            cands.append({"id": f"{scen}-{loan}", "slice": "disclosure", "origin": origin,
                          "steps": [{"call": "document", "input": step["input"], "expected": {"@type": "Documented", "document": projected}}]})
        for loan in docs:
            if (scen, loan) not in set_aside:
                problems.append(f"{scen} loan {loan}: in the report, not in the pool")
    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        f.write("".join(json.dumps(c, separators=(",", ":")) + "\n" for c in cands))
    print(json.dumps(counts), f"{len(cands)} candidates, {len(problems)} problem(s)")
    for p in problems[:20]:
        print("  problem:", p)
    if problems:
        sys.exit(1)


if __name__ == "__main__":
    main(*sys.argv[1:4])
