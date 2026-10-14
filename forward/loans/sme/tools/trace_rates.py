"""More FNEM0300 CALL traces for the rates pool (slice S3): the faithful job step (FNLS0100, the clean-room project's compiled
translation, C) run on corpus scenarios with com.mlcpu.rt.CallTrace tracing FNEM0300, as the capability kit does for its
examples. A trace holds every argument before and after each kept CALL (about 3 MB per CALL: the model record is 262 KB), so
each scenario runs in chunks of loans (the loan ids stay ascending), at most --max CALLs kept per chunk, spread over the run;
each chunk's trace is decoded (build_rates_candidates.decode) and deleted at once.

Usage: python -I trace_rates.py <corpus scenarios dir> <base corpus dir> <CobolBdm repo> <out candidates.jsonl>
           <scenario> [<scenario> ...] [--chunk 25] [--max 30] [--jobs 16]
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
from concurrent.futures import ThreadPoolExecutor

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import build_rates_candidates as rc  # noqa: E402

CLOCK = "FIXED=2022-07-05T10:11:12-07:00"   # the project's oracle clock (project.json)
ENV = {"MLCPU_DISPLAY_NUMERIC": "gnucobol", "MLCPU_BINARY_TRUNCATE": "no", "MLCPU_DEFAULT_BYTE": "0"}


def records(path, lrecl):
    out = []
    with open(path, "rb") as f:
        for line in f.read().split(b"\n"):
            line = line.rstrip(b"\r")
            if line:
                out.append(line[:lrecl].ljust(lrecl, b" "))
    return out


def run_chunk(job):
    scen, k, loans, runctl, currency, cp, work_root, max_calls = job
    work = tempfile.mkdtemp(prefix=f"{scen}-{k:03d}-", dir=work_root)
    try:
        open(os.path.join(work, "LOANREQ.dat"), "wb").write(b"".join(loans))
        open(os.path.join(work, "RUNCTL.dat"), "wb").write(runctl)
        open(os.path.join(work, "CURRFILE.dat"), "wb").write(currency)
        args = ["java", "-cp", cp, "com.mlcpu.rt.CallTrace", "--program", "FNLS0100", "--trace", "FNEM0300", "--clock", CLOCK,
                "--out", "trace.json", "--max", str(max_calls)]
        for dd in ("LOANREQ", "RUNCTL", "CURRFILE", "SCHEDOUT", "REJECTS"):
            args += ["--dd", f"{dd}={dd}.dat"]
        r = subprocess.run(args, cwd=work, env=dict(os.environ, **ENV), capture_output=True, text=True, timeout=3600)
        if r.returncode != 0 or not os.path.exists(os.path.join(work, "trace.json")):
            return [], 0, [f"{scen} chunk {k}: CallTrace failed: {(r.stdout + r.stderr)[-300:]}"]
        problems = []
        cands, calls = rc.decode(os.path.join(work, "trace.json"), f"{scen} (loans {loans[0][2:18].decode()} to {loans[-1][2:18].decode()})",
                                 problems, set())
        return cands, calls, problems
    finally:
        shutil.rmtree(work, ignore_errors=True)


def main(argv):
    opts = {"--chunk": 25, "--max": 30, "--jobs": 16}
    pos = []
    i = 0
    while i < len(argv):
        if argv[i] in opts:
            opts[argv[i]] = int(argv[i + 1])
            i += 2
        else:
            pos.append(argv[i])
            i += 1
    corpus, base, repo, out = pos[:4]
    scenarios = pos[4:]
    cp = os.pathsep.join([os.path.join(repo, "java-sidecar", "build", "classes", "java", "main"),
                          os.path.join(repo, "projects", "fineract-cobol-cr", "transpile", "classes")])
    work_root = os.path.join(os.path.dirname(os.path.abspath(out)), "trace-work")
    os.makedirs(work_root, exist_ok=True)
    jobs = []
    for scen in scenarios:
        sdir = os.path.join(corpus, scen)
        runctl = records(os.path.join(sdir, "RUN.CONTROL.txt"), 80)[0]
        cur = os.path.join(sdir, "CURRENCY.txt") if os.path.exists(os.path.join(sdir, "CURRENCY.txt")) else os.path.join(base, "CURRENCY.txt")
        currency = b"".join(records(cur, 40))
        loans = records(os.path.join(sdir, "LOAN.REQUEST.txt"), 200)
        for k in range(0, len(loans), opts["--chunk"]):
            jobs.append((scen, k // opts["--chunk"], loans[k:k + opts["--chunk"]], runctl, currency, cp, work_root, opts["--max"]))
    seen, kept, problems, calls = set(), [], [], 0
    with ThreadPoolExecutor(max_workers=opts["--jobs"]) as pool:
        for n, (cands, c, p) in enumerate(pool.map(run_chunk, jobs), 1):
            calls += c
            problems += p
            for x in cands:
                key = json.dumps([x["query"], x["calculation"], x.get("rejection", False)], sort_keys=True)
                if key not in seen:
                    seen.add(key)
                    kept.append(x)
            if n % 20 == 0 or n == len(jobs):
                print(f"  {n}/{len(jobs)} chunks, {calls} CALLs, {len(kept)} candidates", flush=True)
    with open(out, "w", encoding="utf-8", newline="\n") as fh:
        for x in kept:
            fh.write(json.dumps(x, separators=(",", ":")) + "\n")
    print(json.dumps({"chunks": len(jobs), "tracedCalls": calls, "candidates": len(kept),
                      "rejections": sum(1 for x in kept if x.get("rejection"))}), f"{len(problems)} problem(s)")
    for p in problems[:20]:
        print("  problem:", p)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
