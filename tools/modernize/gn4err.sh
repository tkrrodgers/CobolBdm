#!/usr/bin/env bash
# GN4 error paths: SHADOW runs of the LCLE0100 step (the loanamorti workbench layout) on inputs the scenarios do not
# have: run-control failures, the seven rounding modes' edge cases, missing datasets, records out of sequence and out of
# place, header dates around leap days (rejects), loans of 1331, 1332, 3001 and 3500 months and a loan of 312 and 313 events around the decimal capacity FSEM0100 abends at (the negative test of
# the exclusion: its abend lines are compared, never dropped), next to the scenarios' own runs. A case passes with equal return codes, no
# dataset difference, and a modern SYSOUT equal line for line to the legacy SYSOUT less, and only:
# - the J3S END statistics block of the called job program (FO-FUNC E = END OF RUN (STATISTICS)): the contiguous lines
#   right before the first "LCLE0100 RECORDS READ" that match STATS_RE and not GUARD_RE (its abend and business lines);
# Implicit-CLOSE warnings are reproduced by the runtime profile and compared, never excluded.
# Each case asserts excluded statistics (89 lines on the END path) and compared warning counts, and writes exclusions to
# sysout-excluded.txt; the exclusion's version and hash are pinned in parity-evidence.txt.
# Approved statistics-only v2.1 supersedes v2; sysout-stats.sh verifies its hash before cleanup or execution.
#   tools/modernize/gn4err.sh <estate dir> <base run dir (with jobs/LCAMORT/in)> <legacy bin dir>
# PKG and COBOL_RUNTIME as for gn4.sh; GN4ERR_OUT selects a separate scratch output for negative controls.
set -u
EST=$1; IN=$2/jobs/LCAMORT/in; BIN=$3
PKG=${PKG:-paqua.loan.amortization.batch}
source "$(dirname "$0")/sysout-stats.sh"
verify_stats_exclusion || exit 2
PROFILE=${GN4_RUNTIME_PROFILE-gnucobol-3.3-dev.0-std-ibm-sysout-diagnostics}
OUT=${GN4ERR_OUT:-$EST/build/gn4err}
rm -rf "${OUT:?}"; mkdir -p "$OUT/in"
if [ -d "$EST/build/classes/java/main" ]; then CP="$EST/build/classes/java/main;$EST/build/resources/main"
else CP="$EST/build/classes;$EST/src/main/resources"; fi
RC=$IN/LOANCALC.RUN.CONTROL; CASES=$IN/LOANCALC.CASES.DATA

# a run control with the rounding mode (10 characters), the record type (2) or the precision (2) replaced
runctl() { # name field value
  local s; s=$(cat "$RC")
  case $2 in
    type) s="$3${s:2}";;
    precision) s="${s:0:2}$3${s:4}";;
    mode) s="${s:0:4}$(printf '%-10s' "$3")${s:14}";;
  esac
  printf '%s' "$s" > "$OUT/in/$1"
}
runctl RC.SIDEWAYS mode SIDEWAYS
# UNNECESSARY is 11 characters: RC-ROUNDING-MODE X(10) holds UNNECESSAR, which no check accepts either
runctl RC.UNNECESSARY mode UNNECESSARY
runctl RC.LOWERCASE mode half_even
runctl RC.LEADING mode " UP"
runctl RC.UP mode UP
runctl RC.TYPE type XX
runctl RC.PRECISION0 precision 00
runctl RC.PRECISIONX precision 1X
: > "$OUT/in/RC.EMPTY"
# the cases a record (100 bytes) at a time
fold -w 100 "$CASES" > "$OUT/in/cases.lines"
join() { tr -d '\n' < "$1" > "$2"; }
# records 2 and 3 swapped: out of sequence
awk 'NR==2{h=$0; next} NR==3{print; print h; next} {print}' "$OUT/in/cases.lines" > "$OUT/in/swap.lines"; join "$OUT/in/swap.lines" "$OUT/in/CASES.SWAPPED"
# a second header inside the first loan: out of place
awk 'NR==2{print substr($0,1,12) "H" substr($0,14); next} {print}' "$OUT/in/cases.lines" > "$OUT/in/place.lines"; join "$OUT/in/place.lines" "$OUT/in/CASES.PLACE"
# the first headers with a first date (LH-HAS-DATE Y, LH-FIRST-DATE): leap days, day 31 of April, month 13, day 0
awk 'BEGIN{n=split("20240229 20230229 19000229 20000229 20230431 20231301 20230100 20231231",d," ")}
     substr($0,13,1)=="H" && k<n {k++; print substr($0,1,38) "Y" d[k] substr($0,48); next} {print}' "$OUT/in/cases.lines" > "$OUT/in/dates.lines"
join "$OUT/in/dates.lines" "$OUT/in/CASES.DATES"
# a header with an amount without its sign: a format reject, the loan's other records skipped
awk 'substr($0,13,1)=="H" && !done {done=1; print substr($0,1,13) " " substr($0,15); next} {print}' "$OUT/in/cases.lines" > "$OUT/in/sign.lines"; join "$OUT/in/sign.lines" "$OUT/in/CASES.NOSIGN"

# the first header's term 3500 months: the legacy job program abends 37 AT APLC:225 (J3S decimal capacity, FSJBDEC: a
# stored value of at most 2223 limbs of 9 digits; (1 + monthly rate) ** term is beyond it); the modern code keeps the limit
# at the lifted sites (DecimalLimit). The boundary at the first loan's 29.715% a year: 1331 months is the largest term the
# legacy job computes (its whole schedule), 1332 the smallest it abends on; 3001 abends too.
longterm() { # term, cases file
  awk -v T="$1" 'substr($0,13,1)=="H" && !done {done=1; print substr($0,1,34) T substr($0,39); next} {print}' "$OUT/in/cases.lines" > "$OUT/in/term$1.lines"; join "$OUT/in/term$1.lines" "$2"
}
longterm 3500 "$OUT/in/CASES.LONG"
longterm 1331 "$OUT/in/CASES.T1331"
longterm 1332 "$OUT/in/CASES.T1332"
longterm 3001 "$OUT/in/CASES.T3001"
# FSJBDEC's store keeps at most 2500 values beyond 31 digits for one loan (BG-USED, emptied at each loan header). The base's
# loan 2 (term 16, a DECREASE_MONTHLY_PAYMENT early payment that recomputes the payment) stores 8 for each CD event: 312
# events are the most the legacy job computes, the 313th abends 37 AT APLC:225 after the 312 events' results.
events() { # events, cases file
  awk -v K="$1" 'substr($0,1,8)=="00000002" && substr($0,13,1)!="E" {print} END {for (k=0;k<K;k++) printf "00000002%04dECD%85s\n", 5+k, ""}' "$OUT/in/cases.lines" > "$OUT/in/events$1.lines"; join "$OUT/in/events$1.lines" "$2"
}
events 312 "$OUT/in/CASES.E312"
events 313 "$OUT/in/CASES.E313"

# The legacy SYSOUT less statistics only; native warnings remain in the compared file.
filter() { # legacy sysout, case dir
  stats_only_filter "$1" LCLE0100 "$2/sysout-kept.txt" "$2/sysout-excluded.txt"
}

stats_exclusion_evidence "$OUT/parity-evidence.txt"

pass=0; fail=0
run() { # name runctl cases expected-statistics expected-compared-warnings
  local w=$OUT/$1; mkdir -p "$w"
  (cd "$w" && java -XX:-OmitStackTraceInFastThrow -cp "$CP" -Dlegacy.LCLE0100="$BIN/LCLE0100.exe" -Dlegacy.path="${COBOL_RUNTIME:-}" \
    -Dmlcpu.runtimeProfile="$PROFILE" -Dshadow.report="$w/shadow-report.json" "$PKG.strangler.Strangler" LCLE0100 SHADOW RUNCTL="$2" CASES="$3" LCOUT="$w/LOANCALC.RESULTS.DATA" > "$w/strangler.txt" 2>&1)
  local rcs; rcs="rc $(grep -o '"legacyReturnCode": [0-9]*' "$w/shadow-report.json" 2>/dev/null | grep -o '[0-9]*$')/$(grep -o '"modernReturnCode": [0-9]*' "$w/shadow-report.json" 2>/dev/null | grep -o '[0-9]*$'), $(grep -o '"differences": [0-9]*' "$w/shadow-report.json" 2>/dev/null | head -1)"
  local same=no counts; counts=$(filter "$w/LCLE0100.legacy.sysout" "$w")
  if tr -d '\r' < "$w/LCLE0100.modern.sysout" | diff -q "$w/sysout-kept.txt" - > /dev/null; then same=yes; fi
  local legacy_warnings modern_warnings
  legacy_warnings=$(grep -Ec '^libcob: warning: implicit CLOSE' "$w/LCLE0100.legacy.sysout")
  modern_warnings=$(grep -Ec '^libcob: warning: implicit CLOSE' "$w/LCLE0100.modern.sysout")
  local excluded="excluded $counts statistics lines, expected $4; implicit-CLOSE compared $legacy_warnings/$modern_warnings, expected $5"
  if grep -q '"differences": 0,' "$w/shadow-report.json" 2>/dev/null && grep -q 'return codes \([0-9]*\)/\1,' "$w/strangler.txt" && [ $same = yes ] && [ "$counts" = "$4" ] && [ "$legacy_warnings" = "$5" ] && [ "$modern_warnings" = "$5" ]; then
    echo "PASS $1 ($rcs, SYSOUT exact less the END statistics block, $excluded)"; pass=$((pass + 1))
  else
    echo "FAIL $1 ($rcs, SYSOUT exact: $same, $excluded)"; fail=$((fail + 1))
  fi
}
run missing-runctl "$OUT/in/NO.SUCH.RUNCTL" "$CASES" 0 0
run empty-runctl "$OUT/in/RC.EMPTY" "$CASES" 0 1
run runctl-type "$OUT/in/RC.TYPE" "$CASES" 0 0
run precision-0 "$OUT/in/RC.PRECISION0" "$CASES" 0 0
run precision-x "$OUT/in/RC.PRECISIONX" "$CASES" 0 0
run rounding-sideways "$OUT/in/RC.SIDEWAYS" "$CASES" 0 0
run rounding-unnecessary "$OUT/in/RC.UNNECESSARY" "$CASES" 0 0
run rounding-lowercase "$OUT/in/RC.LOWERCASE" "$CASES" 0 0
run rounding-leading-space "$OUT/in/RC.LEADING" "$CASES" 0 0
run rounding-up "$OUT/in/RC.UP" "$CASES" 89 0
run missing-cases "$RC" "$OUT/in/NO.SUCH.CASES" 0 0
run out-of-sequence "$RC" "$OUT/in/CASES.SWAPPED" 0 2
run out-of-place "$RC" "$OUT/in/CASES.PLACE" 0 2
run header-dates "$RC" "$OUT/in/CASES.DATES" 89 0
run header-no-sign "$RC" "$OUT/in/CASES.NOSIGN" 89 0
run fsem0100-abend-3500-months "$RC" "$OUT/in/CASES.LONG" 0 2
run decimal-largest-term-1331 "$RC" "$OUT/in/CASES.T1331" 89 0
run decimal-abend-term-1332 "$RC" "$OUT/in/CASES.T1332" 0 2
run decimal-abend-term-3001 "$RC" "$OUT/in/CASES.T3001" 0 2
run decimal-store-312-events "$RC" "$OUT/in/CASES.E312" 89 0
run decimal-store-abend-313-events "$RC" "$OUT/in/CASES.E313" 0 2
echo "GN4 error paths: $pass passed, $fail failed"
# the scenarios' runs: SYSOUT exact less the excluded lines
if [ -d "$EST/build/gn4" ]; then
  exact=0; total=0
  for d in "$EST"/build/gn4/*/; do
    [ -f "$d/LCLE0100.modern.sysout" ] || continue
    total=$((total + 1))
    counts=$(filter "$d/LCLE0100.legacy.sysout" "$d")
    if [ "$counts" = "89" ] && tr -d '\r' < "$d/LCLE0100.modern.sysout" | diff -q "$d/sysout-kept.txt" - > /dev/null; then exact=$((exact + 1));
    else echo "  SYSOUT differs or unexpected exclusion ($counts): $(basename "$d")"; fail=$((fail + 1)); fi
  done
  echo "GN4 scenarios: SYSOUT equal less the approved J3S END statistics block (89 lines each) in $exact of $total"
fi
echo "exclusion: $(head -1 "$OUT/parity-evidence.txt"), $(tail -1 "$OUT/parity-evidence.txt")"
[ $fail -eq 0 ]
