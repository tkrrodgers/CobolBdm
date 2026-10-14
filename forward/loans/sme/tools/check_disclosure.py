"""An independent check of the disclosure's expected values (FE 7.5: the cost of credit has no legacy oracle). Written from
digests/disclosure.md alone, not from the reference: Python's decimal at 50 digits, the rate found as a root to 15 decimals by
bisection on the rate per unit-period, rounded half up to two decimals, and DISC-05's inequality checked at the result. Every
disclose candidate of the disclosure pool must agree in all four figures.

Usage: python -I check_disclosure.py <disclose-candidates.jsonl> <S5 pool dir>
"""
import calendar, collections, json, os, sys
from datetime import date
from decimal import Decimal, getcontext, ROUND_HALF_UP

getcontext().prec = 50
D = Decimal


def d(s):
    return date.fromisoformat(s)


def last_day(y, m):
    return calendar.monthrange(y, m)[1]


def back(p, k):
    """k months back from p (DISC-09)."""
    n = p.year * 12 + (p.month - 1) - k
    y, m = divmod(n, 12)
    m += 1
    if p.day == last_day(p.year, p.month):
        return date(y, m, last_day(y, m))
    return date(y, m, min(p.day, last_day(y, m)))


def full_months(a, p):
    k = max(0, (p.year - a.year) * 12 + p.month - a.month + 1)
    while k > 0 and back(p, k) < a:
        k -= 1
    return k


def days(a, b):
    return (b - a).days


def unit_period(a, dates):
    """(kind, size, per year, counter) per DISC-08."""
    if len(dates) == 1:
        p = dates[0]
        if back(p, 12) >= a:
            return ("YEAR", 12)
        k = full_months(a, p)
        if k > 0 and back(p, k) == a:
            return ("TERM_MONTHS", k)
        return ("TERM_DAYS", days(a, p))
    measures = []
    prev = a
    for p in dates:
        k = full_months(prev, p)
        measures.append(("M", k) if k > 0 and back(p, k) == prev else ("D", days(prev, p)))
        prev = p
    count = collections.Counter(measures)
    common = [m for m, c in count.items() if c > 1]
    if common:
        length = lambda m: 30 * m[1] if m[0] == "M" else m[1]
        best = max(count[m] for m in common)
        tied = [m for m in common if count[m] == best]
        tied.sort(key=lambda m: (length(m), 0 if m[0] == "D" else 1))
        kind, n = tied[0]
        if kind == "M":
            return ("YEAR", 12) if n >= 12 else ("MONTHS", n)
        if n > 365:
            return ("YEAR", 12)
        if n % 7 == 0:
            return ("WEEKS", n // 7)
        return ("DAYS", n)
    avg = D(days(a, dates[-1])) / D(len(dates))
    cands = [("DAYS", 1, 1)] + [("WEEKS", w, 7 * w) for w in range(1, 53)] + [("SEMIMONTH", 1, 15)] + \
            [("MONTHS", m, 30 * m) for m in range(1, 12)] + [("YEAR", 12, 365)]
    best = None
    for c in cands:
        dist = abs(avg - c[2])
        if best is None or dist < best[0] or (dist == best[0] and c[2] < best[1][2]):
            best = (dist, c)
    return best[1][:2]


def per_year(u, a, dates):
    kind, n = u
    return {"DAYS": D(365) / n, "WEEKS": D(52) / n, "SEMIMONTH": D(24), "MONTHS": D(12) / n, "YEAR": D(1),
            "TERM_MONTHS": D(12) / n, "TERM_DAYS": D(365) / (n if n else 1)}[kind]


def count(u, a, p):
    kind, n = u
    if kind in ("TERM_MONTHS", "TERM_DAYS"):
        return 1, D(0)
    if kind in ("DAYS", "WEEKS"):
        unit = n if kind == "DAYS" else 7 * n
        dd = days(a, p)
        return dd // unit, D(dd % unit) / unit
    if kind in ("MONTHS", "SEMIMONTH"):
        k = full_months(a, p)
        r = days(a, back(p, k))
        if kind == "MONTHS" and n == 1:
            return k, D(r) / 30
        unit = 15 if kind == "SEMIMONTH" else 30 * n
        total = 30 * k + r
        return total // unit, D(total % unit) / unit
    years = full_months(a, p) // 12
    rest = back(p, 12 * years)
    k = full_months(a, rest)
    f = D(k) / 12 if back(rest, k) == a else D(days(a, rest)) / 365
    return years, f


def pv_at_i(i, counts, pays):
    return sum((pay / ((1 + f * i) * (1 + i) ** t) for (t, f), pay in zip(counts, pays)), D(0))


def disclosure(s):
    lines = s["lines"]
    a = d(next(l["due"] for l in lines if l["@type"] == "DisbursementLine"))
    principal = sum((D(l["amount"]) for l in lines if l["@type"] == "DisbursementLine"), D(0))
    down = sum((D(l["principal"]) for l in lines if l["@type"] == "DownPaymentLine"), D(0))
    q = D(1).scaleb(-s["decimalPlaces"])
    financed = (principal - down).quantize(q)
    total = sum((D(l["totalDue"]) for l in lines if l["@type"] == "InstallmentLine"), D(0)).quantize(q)
    charge = total - financed
    if charge <= 0:
        return financed, charge, total, D("0.00")
    pays_lines = [l for l in lines if l["@type"] == "InstallmentLine" and d(l["due"]) > a]
    dates = [d(l["due"]) for l in pays_lines]
    pays = [D(l["totalDue"]) for l in pays_lines]
    u = unit_period(a, dates)
    py = per_year(u, a, dates)
    counts = [count(u, a, p) for p in dates]
    # the root, as a rate per unit-period, by bisection to 1e-15 (DISC-05 is then checked exactly at the result)
    lo, hi = D(0), D(1)
    while pv_at_i(hi, counts, pays) > financed:
        lo, hi = hi, hi * 2
    while hi - lo > D("1e-15"):
        mid = (lo + hi) / 2
        if pv_at_i(mid, counts, pays) > financed:
            lo = mid
        else:
            hi = mid
    near = (lo * py * 100).quantize(D("0.01"), rounding=ROUND_HALF_UP)
    # DISC-05 decides: the grid point next to the root whose half-cent neighbours bracket the amount financed (a root on a
    # half cent exactly, which the bisection approaches from below, rounds up)
    pv = lambda pct: pv_at_i(pct / 100 / py, counts, pays)
    for apr in (near, near + D("0.01"), near - D("0.01")):
        if pv(apr - D("0.005")) >= financed > pv(apr + D("0.005")):
            return financed, charge, total, apr
    return financed, charge, total, D("-1")


def main(cands, pool):
    schedules = {}
    for f in os.listdir(pool):
        if f.endswith(".jsonl"):
            for line in open(os.path.join(pool, f), encoding="utf-8"):
                sc = json.loads(line)
                schedules[sc["id"]] = sc["steps"][0]["expected"]
    n, bad = 0, []
    for line in open(cands, encoding="utf-8"):
        c = json.loads(line)
        n += 1
        exp = c["steps"][0]["expected"]
        sched = schedules.get(c["id"]) or c.get("schedule")  # an SME probe carries its schedule
        if sched["@type"] == "Rejected":
            if exp != sched:
                bad.append(f"{c['id']}: the disclosure of a rejected schedule is {exp}")
            continue
        financed, charge, total, apr = disclosure(sched["schedule"])
        got = {"amountFinanced": format(financed, "f"), "financeCharge": format(charge, "f"), "totalOfPayments": format(total, "f"),
               "annualPercentageRate": format(apr, "f")}
        if exp.get("@type") != "Disclosed" or exp["disclosure"] != got:
            bad.append(f"{c['id']}: expected {exp.get('disclosure', exp)}, independent {got}")
    print(f"independent disclosure check: {n} candidates, {len(bad)} disagree")
    for b in bad[:20]:
        print("  ", b)
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main(*sys.argv[1:3])
