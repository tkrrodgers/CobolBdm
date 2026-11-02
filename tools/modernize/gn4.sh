#!/usr/bin/env bash
# GN4: SHADOW mode over the scenarios (SemanticModernization.md §7, §8). The generated strangler runs the legacy job
# (the GnuCOBOL executable of the step) and the modern step on the same input and compares their outputs per request;
# a scenario passes with no difference and equal return codes. One JVM per scenario, in parallel.
#   tools/modernize/gn4.sh <estate name under MODERN_ROOT or absolute dir> <scenarios dir> <legacy bin dir> [currency file] [scenario-glob] [jobs]
# Schedule scenarios (LOAN.REQUEST.txt) run FSLS0100 (a J3S estate) or FNLS0100 then FNRP0100 on the sorted schedule (a
# Tier-1 estate: JCL FNSCHGEN, FNSCHRPT; SYSOUT compared line for line), event scenarios (EMI.EVENTS.txt) FSLE0100, a
# workbench run (jobs/LCAMORT/in, PKG=paqua.loan.amortization.batch) LCLE0100; the legacy bin dir holds <STEP>.exe.
# GN4_EST: a scratch generation; GN4_OUT: the work directory; COBOL_RUNTIME: the GnuCOBOL bin directory (its DLLs).
set -u
source "$(dirname "$0")/sysout-stats.sh"
verify_stats_exclusion || exit 2
if [ "${1:-}" = "--one" ]; then
  # one scenario: --one <estate> <scenario dir> <legacy bin dir> <currency> <out dir>
  EST=$2; d=$3; BIN=$4; CURR=$5; OUT=$6
  PKG=${PKG:-org.apache.fineract.batch}
  # the runtime-compatibility profile of the modern step (Datasets.PROFILE): the legacy runtime's SYSOUT diagnostics
  # (implicit CLOSE of files a run leaves open), pinned to the oracle's cobc 3.3-dev.0 -std=ibm -fsign=EBCDIC
  PROFILE=${GN4_RUNTIME_PROFILE-gnucobol-3.3-dev.0-std-ibm-sysout-diagnostics}
  if [ -d "$EST/build/classes/java/main" ]; then CP="$EST/build/classes/java/main;$EST/build/resources/main"
  else CP="$EST/build/classes;$EST/src/main/resources"; fi
  s=$(basename "$d"); w="$OUT/$s"; mkdir -p "$w"
  # an indexed input (the currency KSDS) is loaded for the legacy job by the estate's LOAD program
  loaders=""
  for l in "$BIN"/LOAD-*.exe; do [ -f "$l" ] && loaders="-Dlegacy.load.CURRFILE=$l"; done
  # the estate's steps (its strangler's Steps): a Tier-1 estate (fineract-cobol, JCL FNSCHGEN) schedules with FNLS0100
  # and prints the report with FNRP0100 (JCL FNSCHRPT); a J3S estate schedules with FSLS0100
  steps=" $(grep -ho 'ALL.put("[A-Z0-9]*"' "$EST/src/main/java/$(echo "$PKG" | tr . /)/strangler/Steps.java" 2>/dev/null | cut -d'"' -f2 | tr '\n' ' ') "
  sched=FSLS0100
  case "$steps" in *" FNLS0100 "*) sched=FNLS0100;; esac
  if [ -f "$d/LOAN.REQUEST.txt" ]; then
    step=$sched; dds="RUNCTL=$d/RUN.CONTROL.txt LOANREQ=$d/LOAN.REQUEST.txt CURRFILE=$CURR SCHEDOUT=$w/SCHEDULE.dat REJECTS=$w/REJECTS.dat"
  elif [ -f "$d/EMI.EVENTS.txt" ]; then
    step=FSLE0100; dds="RUNCTL=$d/RUN.CONTROL.txt EVENTS=$d/EMI.EVENTS.txt CURRFILE=$CURR EMIOUT=$w/EMIOUT.dat"
  elif [ -d "$d/jobs/LCAMORT/in" ]; then
    step=LCLE0100; dds="RUNCTL=$d/jobs/LCAMORT/in/LOANCALC.RUN.CONTROL CASES=$d/jobs/LCAMORT/in/LOANCALC.CASES.DATA LCOUT=$w/LOANCALC.RESULTS.DATA"
  elif [ -d "$d/jobs/KZANNUIT/in" ]; then
    # kontor-zins (Loop 3): JCL KZANNUIT STEP20, RUNCTL FB80, CASES FB100, KZOUT FB200 (an oracle run or an error-path fixture)
    step=KZLE0100; dds="RUNCTL=$d/jobs/KZANNUIT/in/KONTZINS.RUN.CONTROL CASES=$d/jobs/KZANNUIT/in/KONTZINS.CASES.DATA KZOUT=$w/KONTZINS.RESULTS.DATA"
  elif [ -d "$d/jobs/JMMONEY/in" ]; then
    # joda-money (Loop 4): JCL JMMONEY STEP20, RUNCTL FB80, CASES FB100, JMOUT FB120 (an oracle run or a fixture)
    step=JMLE0100; dds="RUNCTL=$d/jobs/JMMONEY/in/JODAMONY.RUN.CONTROL CASES=$d/jobs/JMMONEY/in/JODAMONY.CASES.DATA JMOUT=$w/JODAMONY.RESULTS.DATA"
  else
    exit 0
  fi
  (cd "$w" && java -XX:-OmitStackTraceInFastThrow -cp "$CP" -Dlegacy.$step="$BIN/$step.exe" -Dlegacy.path="${COBOL_RUNTIME:-}" $loaders \
    -Dmlcpu.runtimeProfile="$PROFILE" -Dshadow.report="$w/shadow-report.json" $PKG.strangler.Strangler $step SHADOW $dds > "$w/strangler.txt" 2>&1)
  ok=PASS
  grep -q '"differences": 0,' "$w/shadow-report.json" 2>/dev/null && grep -q 'return codes \([0-9]*\)/\1,' "$w/strangler.txt" || ok=FAIL
  # a Tier-1 step has no approved SYSOUT exclusion: its SYSOUT is the legacy's, line for line
  sysout_equal() { diff -q <(tr -d '\r' < "$w/$1.legacy.sysout" 2>/dev/null) <(tr -d '\r' < "$w/$1.modern.sysout" 2>/dev/null) > /dev/null; }
  if [ "$step" = KZLE0100 ] || [ "$step" = LCLE0100 ] || [ "$step" = FSLE0100 ] || [ "$step" = FSLS0100 ] || [ "$step" = JMLE0100 ]; then
    stats_exclusion_evidence "$w/parity-evidence.txt" "$step"
    if ! stats_only_filter "$w/$step.legacy.sysout" "$step" "$w/sysout-kept.txt" "$w/sysout-excluded.txt" > "$w/sysout-excluded.count"; then
      echo "SYSOUT statistics block validation failed" > "$w/sysout-check.txt"; ok=FAIL
    elif tr -d '\r' < "$w/$step.modern.sysout" | diff -q "$w/sysout-kept.txt" - > /dev/null; then
      echo "SYSOUT exact less $(cat "$w/sysout-excluded.count") END statistics lines" > "$w/sysout-check.txt"
    else
      echo "SYSOUT differs (less $(cat "$w/sysout-excluded.count") END statistics lines)" > "$w/sysout-check.txt"; ok=FAIL
    fi
  fi
  if [ "$step" = FNLS0100 ]; then
    sysout_equal FNLS0100 || ok=FAIL
    # FNSCHRPT: SORT FIELDS=(1,16,CH,A,18,4,CH,A) of the schedule the system of record wrote, then the report step
    case "$steps" in *" FNRP0100 "*)
      python - "$w/SCHEDULE.dat" "$w/SCHEDULE.sorted" <<'EOF'
import sys
data = open(sys.argv[1], 'rb').read() if __import__('os').path.exists(sys.argv[1]) else b''
recs = [data[i:i + 200] for i in range(0, len(data), 200)]
recs.sort(key=lambda r: (r[0:16], r[17:21]))
open(sys.argv[2], 'wb').write(b''.join(recs))
EOF
      (cd "$w" && java -XX:-OmitStackTraceInFastThrow -cp "$CP" -Dlegacy.FNRP0100="$BIN/FNRP0100.exe" -Dlegacy.path="${COBOL_RUNTIME:-}" \
        -Dmlcpu.runtimeProfile="$PROFILE" -Dshadow.report="$w/shadow-report-FNRP0100.json" $PKG.strangler.Strangler FNRP0100 SHADOW SCHEDIN="$w/SCHEDULE.sorted" RPTOUT="$w/REPORT.dat" \
        > "$w/strangler-FNRP0100.txt" 2>&1)
      grep -q '"differences": 0,' "$w/shadow-report-FNRP0100.json" 2>/dev/null && grep -q 'return codes \([0-9]*\)/\1,' "$w/strangler-FNRP0100.txt" || ok=FAIL
      sysout_equal FNRP0100 || ok=FAIL
      ;;
    esac
  fi
  echo $ok > "$w/result"
  exit 0
fi
MODERN_ROOT=${MODERN_ROOT:-C:/Users/Tomrodgers/Source/ModernizedJava}
MODERN_ROOT=${MODERN_ROOT//\\//}
if [ -n "${GN4_EST:-}" ]; then
  EST=$GN4_EST
else
  EST=${1//\\//}
  case "$EST" in
    /*|[[:alpha:]]:/*) ;;
    *) EST="${MODERN_ROOT%/}/$EST";;
  esac
fi
EST=${EST//\\//}
if [ ! -d "$EST" ]; then
  echo "GN4: estate directory does not exist: $EST" >&2
  exit 2
fi
SCEN=$2
BIN=$3
CURR=${4:-none}
GLOB=${5:-*}
JOBS=${6:-16}
OUT=${GN4_OUT:-$EST/build/gn4}
rm -rf "${OUT:?}"; mkdir -p "$OUT"
ls -d "$SCEN"/$GLOB/ | sed 's:/$::' | xargs -P "$JOBS" -I{} bash "$0" --one "$EST" {} "$BIN" "$CURR" "$OUT"
pass=$(grep -l PASS "$OUT"/*/result 2>/dev/null | wc -l)
fail=$(grep -l FAIL "$OUT"/*/result 2>/dev/null | wc -l)
echo "GN4: $pass passed, $fail failed"
for r in $(grep -l FAIL "$OUT"/*/result 2>/dev/null); do echo "  FAIL $(basename "$(dirname "$r")")"; done
