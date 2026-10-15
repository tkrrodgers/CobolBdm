"""The shadow run's bridge to the legacy job (G11, FE 9.3; ofbiz-host.sh shadow).

  shadow_oracle.py prepare <exports dir> <clean-room project dir>
      each day's export (exports/shadow-dNN/<cell>/: LOAN.REQUEST.DATA, RUN.CONTROL) becomes a scenario of the project,
      shadow-dNN (scenarios/shadow-dNN/LOAN.REQUEST.txt and RUN.CONTROL.txt, one record per line, as the corpus keeps them);
      project.json is not changed (the project's oracle scenarios and job checks stay its own)
  shadow_oracle.py collect <exports dir> <clean-room project dir>
      the legacy job's output for each day (oracle/runs/shadow-dNN/data: LNS.LOAN.SCHEDULE.DATA, LNS.LOAN.REJECTS.DATA) beside
      the export, under the names the migration service reads (LOAN.SCHEDULE.DATA, LOAN.REJECTS.DATA)
  shadow_oracle.py legacy <exports dir> <clean-room project dir> <CobolBdm host URL>
      prepare, the oracle on the shadow days alone through the host's API (only=), collect, and the days' scenarios and oracle
      runs removed from the project: the shadow run's middle step, which leaves the project as it found it
  shadow_oracle.py report <shadow dir> <out json>
      per day: the bookings, the loans reconciled (all of them) and the count of each outcome (no DIFF) -> host/shadow.json
"""
import datetime
import glob
import json
import os
import shutil
import sys
import xml.etree.ElementTree as ET


def days(exports):
    for day in sorted(os.listdir(exports)):
        d = os.path.join(exports, day)
        if not os.path.isdir(d) or not day.startswith("shadow-d"):
            continue
        cells = [c for c in sorted(os.listdir(d)) if os.path.isdir(os.path.join(d, c))]
        for cell in cells:
            yield day, cell, os.path.join(d, cell)


def records(path, length):
    data = open(path, "rb").read()
    return [data[i:i + length] for i in range(0, len(data), length)]


def prepare(exports, project):
    names = []
    for day, cell, d in days(exports):
        sc = os.path.join(project, "scenarios", day)
        os.makedirs(sc, exist_ok=True)
        with open(os.path.join(sc, "LOAN.REQUEST.txt"), "wb") as f:
            for r in records(os.path.join(d, "LOAN.REQUEST.DATA"), 200):
                f.write(r + b"\n")
        with open(os.path.join(sc, "RUN.CONTROL.txt"), "wb") as f:
            f.write(records(os.path.join(d, "RUN.CONTROL"), 80)[0] + b"\n")
        names.append(day)
    print(f"{len(names)} shadow day(s) prepared as scenarios of {os.path.basename(os.path.normpath(project))}")
    return names


def collect(exports, project):
    n = 0
    for day, cell, d in days(exports):
        run = os.path.join(project, "oracle", "runs", day, "data")
        for src, dst in (("LNS.LOAN.SCHEDULE.DATA", "LOAN.SCHEDULE.DATA"), ("LNS.LOAN.REJECTS.DATA", "LOAN.REJECTS.DATA")):
            s = os.path.join(run, src)
            if not os.path.exists(s):
                sys.exit(f"{day}: the legacy job wrote no {src} ({run})")
            shutil.copyfile(s, os.path.join(d, dst))
        n += 1
    print(f"the legacy job's output collected for {n} day(s)")


def legacy(exports, project, host):
    """prepare, the oracle on those days through the CobolBdm host (POST /api/projects/<id>/oracle?only=..., then its operation
    until done), collect, and the days removed from the project."""
    import time
    import urllib.parse
    import urllib.request
    names = prepare(exports, project)
    pid = json.load(open(os.path.join(project, "project.json"), encoding="utf-8-sig"))["id"]
    only = urllib.parse.quote(",".join(names))
    req = urllib.request.Request(f"{host}/api/projects/{pid}/oracle?only={only}", method="POST", data=b"")
    op = json.load(urllib.request.urlopen(req, timeout=60))["id"]
    while True:
        o = json.load(urllib.request.urlopen(f"{host}/api/operations/{op}", timeout=60))
        if not o["running"]:
            break
        time.sleep(5)
    print(f"oracle: {o['status']} ({'succeeded' if o['succeeded'] else 'FAILED'}) {o.get('error') or ''}", flush=True)
    if not o["succeeded"]:
        sys.exit(1)
    collect(exports, project)
    for name in names:
        if name.startswith("shadow-d"):
            for d in (os.path.join(project, "scenarios", name), os.path.join(project, "oracle", "runs", name)):
                shutil.rmtree(d, ignore_errors=True)
    print(f"the shadow days' scenarios and oracle runs removed from the project ({len(names)})")


def junit(results):
    out = {}
    for f in glob.glob(os.path.join(results, "*.xml")):
        for tc in ET.parse(f).getroot().iter("testcase"):
            bad = tc.find("failure") if tc.find("failure") is not None else tc.find("error")
            name = tc.attrib.get("name", "")
            out[name[name.find("[") + 1:name.rfind("]")] if "[" in name else name] = None if bad is None else (bad.attrib.get("message") or "")[:2000]
    return out


REPORT = "[scenario-report] "  # H-OFBIZ-TESTS: a step with "report": true logs its result after this prefix


def reported(log, scenario):
    """The results a scenario's steps reported to the test log, one JSON line each."""
    out = []
    if os.path.exists(log):
        for line in open(log, encoding="utf-8", errors="replace"):
            i = line.find(REPORT)
            if i >= 0:
                r = json.loads(line[i + len(REPORT):])
                if r.get("scenario") == scenario:
                    out.append(r)
    return out


def report(shadow, out):
    """The run's one scenario (shadow-run): each day's reconciliation as the migration service reported it (its loans and the
    count of each outcome), and the failure message's DIFF counts and failed steps. A day passes when every booking of it was
    reconciled and no difference is left unexplained."""
    import re
    plan = json.load(open(os.path.join(shadow, "scenarios", "days.json"), encoding="utf-8"))
    results = junit(os.path.join(shadow, "run", "test-results"))
    ran = "shadow-run" in results
    message = results.get("shadow-run") or ""
    drift = {int(m.group(1)): int(m.group(2)) for m in re.finditer(r"migrationId=SHADOW-D(\d+), outcome=DIFF\}: expected 0 row\(s\), got (\d+)", message)}
    failed_steps = re.findall(r"step \d+ [^\n:]+: [^\n]*", message)
    runs = {int(r["result"]["migrationId"][len("SHADOW-D"):]): r["result"]
            for r in reported(os.path.join(shadow, "run", "test.log"), "shadow-run") if r.get("service") == "migrateLegacyLoanBook" and r.get("result")}
    rows, totals = [], {}
    for d in plan["days"]:
        run = runs.get(d["day"]) or {}
        outcomes = run.get("outcomes") or {}
        for k, v in outcomes.items():
            totals[k] = totals.get(k, 0) + v
        # the reported outcomes when the day reported them, else the failure message's count (the same rows: never both)
        unexplained = sum(v for k, v in outcomes.items() if k.split(" ")[0] == "DIFF") if run else drift.get(d["day"], 0)
        rows.append({"day": d["day"], "date": d["date"], "cell": d["cell"], "bookings": d["bookings"],
                     "reconciledLoans": run.get("loans"), "outcomes": outcomes, "drift": unexplained if ran else None,
                     "reconciled": ran and not failed_steps and run.get("loans") == d["bookings"] and unexplained == 0})
    ok = ran and not failed_steps and bool(rows) and all(r["reconciled"] for r in rows)
    result = {"schema": "mlcpu.shadow-run.v1", "checked": datetime.datetime.now().astimezone().isoformat(timespec="seconds"), "pass": ok,
              "days": len(rows), "bookings": sum(r["bookings"] for r in rows), "outcomes": dict(sorted(totals.items())),
              "failedSteps": failed_steps[:20], "rows": rows}
    json.dump(result, open(out, "w", encoding="utf-8", newline="\n"), indent=2)
    print(f"G11 {'PASSED' if ok else 'NOT PASSED'}: {len(rows)} days, {result['bookings']} bookings; "
          f"{sum(r['reconciled'] for r in rows)} day(s) reconciled in full without drift; outcomes {result['outcomes']}")
    return 0 if ok else 1


if __name__ == "__main__":
    verb = sys.argv[1]
    if verb == "prepare":
        prepare(sys.argv[2], sys.argv[3])
    elif verb == "collect":
        collect(sys.argv[2], sys.argv[3])
    elif verb == "legacy":
        legacy(sys.argv[2], sys.argv[3], sys.argv[4])
    elif verb == "report":
        sys.exit(report(sys.argv[2], sys.argv[3]))
    else:
        sys.exit(__doc__)
