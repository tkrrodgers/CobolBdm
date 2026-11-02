#!/usr/bin/env bash
set -eu
source "$(dirname "$0")/sysout-stats.sh"
verify_stats_exclusion
test "$SHA" = 7d7905e2067af80b3950dd93b7e87b3644ecd72388c1d3f10241bfbe20f4b7c6

temp=$(mktemp -d -t mlcpu-sysout-stats-test.XXXXXXXX)
trap 'rm -rf -- "$temp"' EXIT
cat > "$temp/input" <<'EOF'
FSEM0100 GC RUNS : 1
BUSINESS BEFORE THE END BLOCK
libcob: warning: implicit CLOSE of EARLY-FILE ('EARLY')
FSEM0100 ABENDING CODE 37 AT JBDO:67
FSEM0100 GC RUNS : 3
FSEM0100 POOL DEC PEAK 5 OF 100 LIVE AT GC 0
FSEM0100  DEC ALLOCATIONS 7
LCLE0100 RECORDS READ 2
ABEND CEE3ABD
libcob: warning: implicit CLOSE of RESULT-FILE ('LCOUT')
libcob: warning: implicit CLOSE of EVENT-FILE ('CASES')
EOF
cat > "$temp/expected" <<'EOF'
FSEM0100 GC RUNS : 1
BUSINESS BEFORE THE END BLOCK
libcob: warning: implicit CLOSE of EARLY-FILE ('EARLY')
FSEM0100 ABENDING CODE 37 AT JBDO:67
LCLE0100 RECORDS READ 2
ABEND CEE3ABD
libcob: warning: implicit CLOSE of RESULT-FILE ('LCOUT')
libcob: warning: implicit CLOSE of EVENT-FILE ('CASES')
EOF
count=$(stats_only_filter "$temp/input" LCLE0100 "$temp/kept" "$temp/excluded")
test "$count" = 3
diff -u "$temp/expected" "$temp/kept"
test "$(grep -c '^libcob: warning: implicit CLOSE' "$temp/kept")" = 3
if grep -Eq 'libcob|ABENDING|ABEND CEE3ABD|BUSINESS' "$temp/excluded"; then
  echo "FAIL: non-statistics output was excluded" >&2
  exit 1
fi
echo "PASS: only the contiguous END statistics block is excluded; warnings and abends are compared"

count=$(stats_only_filter "$temp/input" KZLE0100 "$temp/kept" "$temp/excluded")
test "$count" = 0
diff -u "$temp/input" "$temp/kept"
echo "PASS: statistics outside the step's END position remain compared"

{
  echo "BUSINESS BEFORE THE FSGN END BLOCK"
  echo "FSGN0100 ABENDING CODE 37 AT JBDO:67"
  echo "FSGN0100 GC RUNS : 0"
  for ((i = 1; i <= 176; i++)); do
    pool=P$i
    case "$i" in 1) pool=HLSP-2;; 2) pool=HM-2;; esac
    echo "FSGN0100 POOL $pool PEAK 1 OF 100 LIVE AT GC 0"
    echo "FSGN0100  $pool ALLOCATIONS 1"
  done
  echo "FSLS0100 REQUESTS READ 1"
  echo "ABEND CEE3ABD"
  echo "libcob: warning: implicit CLOSE of RESULT-FILE ('SCHEDOUT')"
} > "$temp/fsgn-input"
count=$(stats_only_filter "$temp/fsgn-input" FSLS0100 "$temp/kept" "$temp/excluded")
test "$count" = 353
test "$(wc -l < "$temp/kept")" = 5
test "$(grep -c '^libcob:' "$temp/kept")" = 1
if grep -Eq 'libcob|ABENDING|ABEND CEE3ABD|BUSINESS' "$temp/excluded"; then
  echo "FAIL: FSGN policy excluded protected output" >&2
  exit 1
fi
echo "PASS: separately approved FSGN block has exactly 353 lines; business, abends and warnings remain compared"

count=$(stats_only_filter "$temp/fsgn-input" KZLE0100 "$temp/kept" "$temp/excluded")
test "$count" = 0
diff -u "$temp/fsgn-input" "$temp/kept"
echo "PASS: FSGN policy never applies to another step"

grep -v 'POOL P176 ' "$temp/fsgn-input" > "$temp/fsgn-short"
if stats_only_filter "$temp/fsgn-short" FSLS0100 "$temp/kept" "$temp/excluded"; then
  echo "FAIL: incomplete FSGN statistics block passed" >&2
  exit 1
else
  test "$?" = 2
fi
echo "PASS: unexpected FSGN block size fails explicitly"

sed -e 's/FSGN0100/FSEM0100/g' -e 's/FSLS0100 REQUESTS READ/FSLE0100 RECORDS READ/g' "$temp/fsgn-input" > "$temp/fsle-input"
count=$(stats_only_filter "$temp/fsle-input" FSLE0100 "$temp/kept" "$temp/excluded")
test "$count" = 353
test "$(wc -l < "$temp/kept")" = 5
test "$(grep -c '^libcob:' "$temp/kept")" = 1
if grep -Eq 'libcob|ABENDING|ABEND CEE3ABD|BUSINESS' "$temp/excluded"; then
  echo "FAIL: FSLE policy excluded protected output" >&2
  exit 1
fi
echo "PASS: separately approved FSLE block has exactly 353 lines; protected output remains compared"

count=$(stats_only_filter "$temp/fsle-input" LCLE0100 "$temp/kept" "$temp/excluded")
test "$count" = 0
diff -u "$temp/fsle-input" "$temp/kept"
echo "PASS: base v2.1 does not inherit the FSLE-specific pool scope"

{
  echo "BUSINESS BEFORE THE JODA END BLOCK"
  echo "FSEM0100 GC RUNS : 0"
  for ((i = 1; i <= 41; i++)); do
    pool=P$i
    if [ "$i" = 1 ]; then pool=HJC-2; fi
    echo "FSEM0100 POOL $pool PEAK 1 OF 100 LIVE AT GC 0"
    echo "FSEM0100  $pool ALLOCATIONS 1"
  done
  echo "JMLE0100 RECORDS READ 1"
  echo "ABEND CEE3ABD"
  echo "libcob: warning: implicit CLOSE of RESULT-FILE ('JMOUT')"
} > "$temp/joda-input"
count=$(stats_only_filter "$temp/joda-input" JMLE0100 "$temp/kept" "$temp/excluded")
test "$count" = 83
test "$(wc -l < "$temp/kept")" = 4
test "$(grep -c '^libcob:' "$temp/kept")" = 1
if grep -Eq 'libcob|ABENDING|ABEND CEE3ABD|BUSINESS' "$temp/excluded"; then
  echo "FAIL: Joda policy excluded protected output" >&2
  exit 1
fi
echo "PASS: Joda-specific block has exactly 83 lines; protected output remains compared"

count=$(stats_only_filter "$temp/joda-input" LCLE0100 "$temp/kept" "$temp/excluded")
test "$count" = 0
diff -u "$temp/joda-input" "$temp/kept"
echo "PASS: Joda HJC-2 scope never widens the base v2.1 policy"

STATS_RE='^FSEM0100 .*'
if verify_stats_exclusion; then
  echo "FAIL: a changed allowlist passed hash verification" >&2
  exit 1
else
  test "$?" = 2
fi
echo "PASS: changed policy fails with exit 2"
