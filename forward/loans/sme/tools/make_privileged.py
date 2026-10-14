"""Loan requests beyond the legacy envelope and inside the service's limits (ForwardEngineeringFineract.md decision D-6, F5,
work item W2): the privileged scenarios' requests. Oracle A computes them without the envelope (fineract-cobol-gen
scripts/cr-oracle.sh with FORWARD_LIMITS=config/forward-limits.yaml and A_PRIME for D-2); build_schedule_pool.py then turns
the outcomes into scenarios labelled privileged. Nothing here sets an expected value.

Each output directory is a corpus scenario (LOAN.REQUEST.txt, RUN.CONTROL.txt, CURRENCY.txt), one axis of D-6 each:

  envelope-<scenario>      the corpus loans the legacy envelope rejected (RJENV020 to RJENV090), as they were
  long-p12-half_even       481 to 1,200 installments (the envelope: 480)
  every-p12-half_even      `every` above the envelope's 365 days, 52 weeks, 12 months, 5 years
  principal-p12-half_even  a principal from 10^9 (the envelope's limit) to below 10^13 (the records' 13 integer digits)
  precision-pNN-<mode>     a precision from 13 to 34 (the run control's 12), the loans otherwise inside the envelope
  combined-p34-half_up     long, large and wide apart at once

The base loans are the `edge` set's (edge-p12-half_even). Loan ids are new and ascending per directory (the job's order).
Seeded: the same arguments give the same files.

Usage: python -I make_privileged.py <corpus dir (fineract-cobol-gen/corpus)> <S5 pool dir (pool/schedule)> <out dir> [seed 42]
"""
import json, os, random, shutil, sys
from decimal import Decimal

LRECL = 200
ENVELOPE_EVERY = {"DAYS": 365, "WEEKS": 52, "MONTHS": 12, "YEARS": 5}
# `every` beyond the envelope, up to a term the records still hold (dates of 4-digit years)
WIDE_EVERY = {"DAYS": 3650, "WEEKS": 520, "MONTHS": 240, "YEARS": 50}
PRECISION_CELLS = [(13, "HALF_UP"), (16, "HALF_EVEN"), (20, "DOWN"), (25, "CEILING"), (34, "HALF_EVEN"), (34, "FLOOR")]


def records(path):
    out = []
    with open(path, "rb") as f:
        for line in f.read().split(b"\n"):
            line = line.rstrip(b"\r")
            if line:
                out.append(bytearray(line[:LRECL].ljust(LRECL, b" ")))
    return out


def put(r, a, b, text):
    assert len(text) == b - a, (a, b, text)
    r[a:b] = text.encode("latin-1")


def text(r, a, b):
    return bytes(r[a:b]).decode("latin-1")


def unsigned(n, width):
    s = str(n)
    assert len(s) <= width, (n, width)
    return s.zfill(width)


def amount(v, digits=17, scale=4):
    """S9(13)V9(4) SIGN LEADING SEPARATE."""
    u = int((v * (10 ** scale)).to_integral_exact())
    s = str(abs(u))
    assert len(s) <= digits, v
    return ("-" if u < 0 else "+") + s.zfill(digits)


def freq(r):
    return text(r, 63, 73).rstrip(" ")


def write_dir(out, name, recs, run_control, currency):
    d = os.path.join(out, name)
    os.makedirs(d, exist_ok=True)
    with open(os.path.join(d, "LOAN.REQUEST.txt"), "wb") as f:
        f.write(b"".join(bytes(r).rstrip(b" ") + b"\n" for r in recs))
    with open(os.path.join(d, "RUN.CONTROL.txt"), "w", encoding="latin-1", newline="\n") as f:
        f.write(run_control + "\n")
    shutil.copyfile(currency, os.path.join(d, "CURRENCY.txt"))
    return len(recs)


def renumber(recs, axis):
    for i, r in enumerate(recs, 1):
        put(r, 2, 18, f"{axis:02d}{i:014d}")
    return recs


def fit_fixed_length(r):
    """A base loan's fixed length, moved into the window (n - 1) * every < length <= (n + 1) * every; dropped when the field cannot hold it."""
    if text(r, 116, 117) != "Y":
        return
    n, every = int(text(r, 55, 59)), int(text(r, 59, 63))
    fl = n * every
    if fl > 99999:
        put(r, 116, 117, "N")
        put(r, 117, 122, "00000")
    else:
        put(r, 117, 122, unsigned(fl, 5))


def main(corpus, pool, out, seed="42"):
    rnd = random.Random(int(seed))
    scenarios = os.path.join(corpus, "scenarios")
    currency = os.path.join(corpus, "CURRENCY.txt")
    if os.path.isdir(out):
        shutil.rmtree(out)
    os.makedirs(out)
    made = {}

    # 1. the corpus loans the envelope rejected
    set_aside = json.load(open(os.path.join(pool, "pool.json"), encoding="utf-8"))["setAside"]
    by_scenario = {}
    for s in set_aside:
        if "legacy envelope reject" in s["reason"]:
            by_scenario.setdefault(s["scenario"], set()).add(s["loan"])
    for name, loans in sorted(by_scenario.items()):
        src = os.path.join(scenarios, name if os.path.isdir(os.path.join(scenarios, name)) else name.replace("unit", "fineract-unit", 1))
        recs = [r for r in records(os.path.join(src, "LOAN.REQUEST.txt")) if text(r, 2, 18) in loans]
        cur = os.path.join(src, "CURRENCY.txt") if os.path.exists(os.path.join(src, "CURRENCY.txt")) else currency
        rc = open(os.path.join(src, "RUN.CONTROL.txt"), encoding="latin-1").read().split("\n")[0]
        made["envelope-" + name] = write_dir(out, "envelope-" + name, recs, rc, cur)

    base = records(os.path.join(scenarios, "edge-p12-half_even", "LOAN.REQUEST.txt"))
    by_freq = {f: [r for r in base if freq(r) == f] for f in ENVELOPE_EVERY}

    def pick(n, freqs=None):
        pool_ = [r for r in base if freqs is None or freq(r) in freqs]
        return [bytearray(r) for r in rnd.sample(pool_, n)]

    # 2. long loans: 481 to 1,200 installments, every 1 (the term stays inside 4-digit years)
    recs = []
    for f in ENVELOPE_EVERY:
        for r in [bytearray(x) for x in rnd.sample(by_freq[f], 8)]:
            put(r, 55, 59, unsigned(rnd.randint(481, 1200), 4))
            put(r, 59, 63, unsigned(1, 4))
            fit_fixed_length(r)
            recs.append(r)
    made["long-p12-half_even"] = write_dir(out, "long-p12-half_even", renumber(recs, 1), "MC12HALF_EVEN", currency)

    # 3. every beyond the envelope's maximum for the frequency
    recs = []
    for f in ENVELOPE_EVERY:
        for r in [bytearray(x) for x in rnd.sample(by_freq[f], 30)]:
            put(r, 59, 63, unsigned(rnd.randint(ENVELOPE_EVERY[f] + 1, WIDE_EVERY[f]), 4))
            put(r, 55, 59, unsigned(rnd.randint(1, 24), 4))
            fit_fixed_length(r)
            recs.append(r)
    made["every-p12-half_even"] = write_dir(out, "every-p12-half_even", renumber(recs, 2), "MC12HALF_EVEN", currency)

    # 4. principal from 10^9 to below 10^13, at the currency's decimal places (at most 4)
    places = {l[0:3]: int(l[3:5]) for l in open(currency, encoding="latin-1").read().split("\n") if l.strip()}
    recs = []
    for r in pick(120):
        p = places[text(r, 26, 29)]
        if p > 4:
            continue
        digits = rnd.randint(10, 13)
        whole = rnd.randint(max(10 ** (digits - 1), 10 ** 9), 10 ** digits - 1)
        frac = Decimal(rnd.randint(0, 10 ** p - 1)).scaleb(-p) if p else Decimal(0)
        put(r, 29, 47, amount(Decimal(whole) + frac))
        recs.append(r)
    made["principal-p12-half_even"] = write_dir(out, "principal-p12-half_even", renumber(recs, 3), "MC12HALF_EVEN", currency)

    # 5. precision 13 to 34: loans otherwise inside the envelope
    for i, (prec, mode) in enumerate(PRECISION_CELLS):
        name = f"precision-p{prec}-{mode.lower()}"
        made[name] = write_dir(out, name, renumber(pick(60), 10 + i), f"MC{prec:02d}{mode}", currency)

    # 6. combined: 481 to 720 monthly installments every 13 to 24 months, a principal of 10^10 to 10^12, precision 34
    recs = []
    for r in [bytearray(x) for x in rnd.sample(by_freq["MONTHS"], 20)]:
        put(r, 55, 59, unsigned(rnd.randint(481, 720), 4))
        put(r, 59, 63, unsigned(rnd.randint(13, 24), 4))
        p = places[text(r, 26, 29)]
        put(r, 29, 47, amount(Decimal(rnd.randint(10 ** 10, 10 ** 12)) + (Decimal(rnd.randint(0, 10 ** p - 1)).scaleb(-p) if p else 0)))
        fit_fixed_length(r)
        recs.append(r)
    made["combined-p34-half_up"] = write_dir(out, "combined-p34-half_up", renumber(recs, 4), "MC34HALF_UP", currency)

    with open(os.path.join(out, "privileged.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump({"seed": int(seed), "base": "edge-p12-half_even", "directories": made}, f, indent=2)
        f.write("\n")
    print(f"{sum(made.values())} privileged requests in {len(made)} directories: {out}")
    for k, v in made.items():
        print(f"  {k}: {v}")


if __name__ == "__main__":
    main(*sys.argv[1:])
