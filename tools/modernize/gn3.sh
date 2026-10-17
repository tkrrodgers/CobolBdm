#!/usr/bin/env bash
# GN3: the modernized batch steps against oracle A (the Fineract jar), byte for byte, one JVM per scenario.
#   tools/modernize/gn3.sh <estate dir under MODERN_ROOT> <scenarios dir> <oracle dir> <currency file> [scenario-glob] [jobs]
# Schedule scenarios (LOAN.REQUEST.txt) run Fsls0100Batch, event scenarios (EMI.EVENTS.txt) run Fsle0100Batch;
# a workbench run (jobs/LCAMORT/in, loanamorti: PKG=paqua.loan.amortization.batch) runs Lcle0100Batch against its own GnuCOBOL output.
# GN3_EST overrides the estate directory (a scratch generation), GN3_OUT the work directory.
# Scenarios run in parallel (default 24 at a time); each writes PASS or FAIL into its work directory.
set -u
if [ "${1:-}" = "--one" ]; then
  # one scenario: --one <estate> <scenario dir> <oracle dir> <currency> <out dir>
  EST=$2; d=$3; ORACLE=$4; CURR=$5; OUT=$6
  PKG=${PKG:-org.apache.fineract.batch}
  if [ -d "$EST/build/classes/java/main" ]; then CP="$EST/build/classes/java/main;$EST/build/resources/main"
  else CP="$EST/build/classes;$EST/src/main/resources"; fi
  s=$(basename "$d"); w="$OUT/$s"; mkdir -p "$w"
  if [ -f "$d/LOAN.REQUEST.txt" ]; then
    java -XX:-OmitStackTraceInFastThrow -cp "$CP" -Ddd.RUNCTL="$d/RUN.CONTROL.txt" -Ddd.LOANREQ="$d/LOAN.REQUEST.txt" -Ddd.CURRFILE="$CURR" \
      -Ddd.SCHEDOUT="$w/SCHEDULE.dat" -Ddd.REJECTS="$w/REJECTS.dat" $PKG.Fsls0100Batch > "$w/sysout.txt" 2>&1
    files="SCHEDULE.dat REJECTS.dat"
  elif [ -d "$d/jobs/LCAMORT/in" ]; then
    # the workbench sample (loanamorti): oracle B is the GnuCOBOL run's output in the scenario itself
    java -XX:-OmitStackTraceInFastThrow -cp "$CP" -Ddd.RUNCTL="$d/jobs/LCAMORT/in/LOANCALC.RUN.CONTROL" -Ddd.CASES="$d/jobs/LCAMORT/in/LOANCALC.CASES.DATA"       -Ddd.LCOUT="$w/LOANCALC.RESULTS.DATA" ${PKG}.Lcle0100Batch > "$w/sysout.txt" 2>&1
    cmp -s "$w/LOANCALC.RESULTS.DATA" "$d/jobs/LCAMORT/out/LOANCALC.RESULTS.DATA" && echo PASS > "$w/result" || echo FAIL > "$w/result"
    exit 0
  elif [ -f "$d/EMI.EVENTS.txt" ]; then
    java -XX:-OmitStackTraceInFastThrow -cp "$CP" -Ddd.RUNCTL="$d/RUN.CONTROL.txt" -Ddd.EVENTS="$d/EMI.EVENTS.txt" -Ddd.CURRFILE="$CURR" \
      -Ddd.EMIOUT="$w/EMIOUT.dat" $PKG.Fsle0100Batch > "$w/sysout.txt" 2>&1
    files="EMIOUT.dat"
  else
    exit 0
  fi
  ok=PASS
  for f in $files; do
    a="$w/$f"; b="$ORACLE/$s/$f"
    if [ -f "$b" ]; then cmp -s "$a" "$b" || ok=FAIL; elif [ -s "$a" ]; then ok=FAIL; fi
  done
  echo $ok > "$w/result"
  exit 0
fi
MODERN_ROOT=${MODERN_ROOT:-C:/Users/Tomrodgers/Source/ModernizedJava}
EST=${GN3_EST:-$MODERN_ROOT/$1}
SCEN=$2
ORACLE=$3
CURR=$4
GLOB=${5:-*}
JOBS=${6:-24}
OUT=$EST/build/gn3
OUT=${GN3_OUT:-$OUT}; rm -rf "$OUT"; mkdir -p "$OUT"
ls -d "$SCEN"/$GLOB/ | sed 's:/$::' | xargs -P "$JOBS" -I{} bash "$0" --one "$EST" {} "$ORACLE" "$CURR" "$OUT"
pass=$(grep -l PASS "$OUT"/*/result 2>/dev/null | wc -l)
fail=$(grep -l FAIL "$OUT"/*/result 2>/dev/null | wc -l)
echo "GN3: $pass passed, $fail failed"
for r in $(grep -l FAIL "$OUT"/*/result 2>/dev/null); do echo "  FAIL $(basename $(dirname $r))"; done
