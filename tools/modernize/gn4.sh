#!/usr/bin/env bash
# GN4: SHADOW mode over the scenarios (SemanticModernization.md §7, §8). The generated strangler runs the legacy job
# (the GnuCOBOL executable of the step) and the modern step on the same input and compares their outputs per request;
# a scenario passes with no difference and equal return codes. One JVM per scenario, in parallel.
#   tools/modernize/gn4.sh <estate dir under MODERN_ROOT> <scenarios dir> <legacy bin dir> [currency file] [scenario-glob] [jobs]
# Schedule scenarios (LOAN.REQUEST.txt) run FSLS0100, event scenarios (EMI.EVENTS.txt) FSLE0100, a workbench run
# (jobs/LCAMORT/in, PKG=paqua.loan.amortization.batch) LCLE0100; the legacy bin dir holds <STEP>.exe.
# GN4_EST: a scratch generation; GN4_OUT: the work directory; COBOL_RUNTIME: the GnuCOBOL bin directory (its DLLs).
set -u
if [ "${1:-}" = "--one" ]; then
  # one scenario: --one <estate> <scenario dir> <legacy bin dir> <currency> <out dir>
  EST=$2; d=$3; BIN=$4; CURR=$5; OUT=$6
  PKG=${PKG:-org.apache.fineract.batch}
  if [ -d "$EST/build/classes/java/main" ]; then CP="$EST/build/classes/java/main;$EST/build/resources/main"
  else CP="$EST/build/classes;$EST/src/main/resources"; fi
  s=$(basename "$d"); w="$OUT/$s"; mkdir -p "$w"
  # an indexed input (the currency KSDS) is loaded for the legacy job by the estate's LOAD program
  loaders=""
  for l in "$BIN"/LOAD-*.exe; do [ -f "$l" ] && loaders="-Dlegacy.load.CURRFILE=$l"; done
  if [ -f "$d/LOAN.REQUEST.txt" ]; then
    step=FSLS0100; dds="RUNCTL=$d/RUN.CONTROL.txt LOANREQ=$d/LOAN.REQUEST.txt CURRFILE=$CURR SCHEDOUT=$w/SCHEDULE.dat REJECTS=$w/REJECTS.dat"
  elif [ -f "$d/EMI.EVENTS.txt" ]; then
    step=FSLE0100; dds="RUNCTL=$d/RUN.CONTROL.txt EVENTS=$d/EMI.EVENTS.txt CURRFILE=$CURR EMIOUT=$w/EMIOUT.dat"
  elif [ -d "$d/jobs/LCAMORT/in" ]; then
    step=LCLE0100; dds="RUNCTL=$d/jobs/LCAMORT/in/LOANCALC.RUN.CONTROL CASES=$d/jobs/LCAMORT/in/LOANCALC.CASES.DATA LCOUT=$w/LOANCALC.RESULTS.DATA"
  else
    exit 0
  fi
  (cd "$w" && java -XX:-OmitStackTraceInFastThrow -cp "$CP" -Dlegacy.$step="$BIN/$step.exe" -Dlegacy.path="${COBOL_RUNTIME:-}" $loaders \
    -Dshadow.report="$w/shadow-report.json" $PKG.strangler.Strangler $step SHADOW $dds > "$w/strangler.txt" 2>&1)
  if grep -q '"differences": 0,' "$w/shadow-report.json" 2>/dev/null && grep -q 'return codes \([0-9]*\)/\1,' "$w/strangler.txt"; then
    echo PASS > "$w/result"
  else
    echo FAIL > "$w/result"
  fi
  exit 0
fi
MODERN_ROOT=${MODERN_ROOT:-C:/Users/Tomrodgers/Source/ModernizedJava}
EST=${GN4_EST:-$MODERN_ROOT/$1}
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
