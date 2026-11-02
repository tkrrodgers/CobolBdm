#!/usr/bin/env bash
# Statistics-only policy; SHA-256 covers the unchanged STATS_RE and GUARD_RE, each followed by LF.
EXCLUSION_VERSION="sysout-exclusion v2.1 (statistics-only)"
APPROVAL="Tomrodgers decision 2026-11-01, verified hub 20261101T092855-3ef381ee: retire implicit-CLOSE exclusion; compare runtime diagnostics"
APPROVED_SHA256=7d7905e2067af80b3950dd93b7e87b3644ecd72388c1d3f10241bfbe20f4b7c6
STATS_RE='^FSEM0100 (GC RUNS +: [+-]?[0-9]+|POOL [A-Z0-9]+ PEAK +[0-9]+ OF +[0-9]+ LIVE AT GC +[0-9]+| +[A-Z0-9]+ ALLOCATIONS +[0-9]+)$'
GUARD_RE='^FSEM0100 (ABENDING|LOAN |DEAD CODE|UNSUPPORTED|INTERNAL ERROR|SIZE ERROR|RECURSION|DOUBLE ARITHMETIC|JOB ADAPTER)'
FSLS_EXCLUSION_VERSION="sysout-exclusion fsls-statistics v1 (FSGN0100 only)"
FSLS_APPROVAL="Tomrodgers ask_user approval 2026-11-01: contiguous 353 FSGN statistics lines before FSLS0100 REQUESTS READ; no business, abend or warning exclusion"
FSLS_APPROVED_SHA256=972f9d8e5cdd28aca6dc70194ced352bf390eaf289ff571b414d677bf86d388c
FSGN_STATS_RE='^FSGN0100 (GC RUNS +: [+-]?[0-9]+|POOL ([A-Z0-9]+|HLSP-2|HM-2) PEAK +[0-9]+ OF +[0-9]+ LIVE AT GC +[0-9]+| +([A-Z0-9]+|HLSP-2|HM-2) ALLOCATIONS +[0-9]+)$'
FSGN_GUARD_RE='^FSGN0100 (ABENDING|LOAN |DEAD CODE|UNSUPPORTED|INTERNAL ERROR|SIZE ERROR|RECURSION|DOUBLE ARITHMETIC|JOB ADAPTER)'
FSLS_POSITION_RE='^FSLS0100 REQUESTS READ'
FSLS_STATS_LINES=353
FSLE_EXCLUSION_VERSION="sysout-exclusion fsle-statistics v1 (FSEM0100 XL only)"
FSLE_APPROVAL="Tomrodgers ask_user approval 2026-11-01: contiguous 353 FSEM statistics lines before FSLE0100 RECORDS READ; no business, abend or warning exclusion"
FSLE_APPROVED_SHA256=5a1159ded50c69ef69e77a4c6799da578d1e027ed6eb6913697598a86839f81a
FSLE_STATS_RE='^FSEM0100 (GC RUNS +: [+-]?[0-9]+|POOL ([A-Z0-9]+|HLSP-2|HM-2) PEAK +[0-9]+ OF +[0-9]+ LIVE AT GC +[0-9]+| +([A-Z0-9]+|HLSP-2|HM-2) ALLOCATIONS +[0-9]+)$'
FSLE_POSITION_RE='^FSLE0100 RECORDS READ'
FSLE_STATS_LINES=353
JM_EXCLUSION_VERSION="sysout-exclusion joda-statistics v1 (JMLE0100 only)"
JM_APPROVAL="Tomrodgers ask_user approval 2026-11-01: contiguous 83 FSEM statistics lines before JMLE0100 RECORDS READ; no business, abend or warning exclusion"
JM_APPROVED_SHA256=811a128d0363aaa07bc9eaa9b937fd009a9496ecfb2b5a7aa9eafc48c68bf709
JM_STATS_RE='^FSEM0100 (GC RUNS +: [+-]?[0-9]+|POOL ([A-Z0-9]+|HJC-2) PEAK +[0-9]+ OF +[0-9]+ LIVE AT GC +[0-9]+| +([A-Z0-9]+|HJC-2) ALLOCATIONS +[0-9]+)$'
JM_POSITION_RE='^JMLE0100 RECORDS READ'
JM_STATS_LINES=83

verify_stats_exclusion() {
  SHA=$(printf '%s\n%s\n' "$STATS_RE" "$GUARD_RE" | sha256sum | cut -d' ' -f1)
  if [ "$SHA" != "$APPROVED_SHA256" ]; then
    echo "SYSOUT statistics exclusion changed (sha256 $SHA, approved $APPROVED_SHA256): approval needs renewal" >&2
    return 2
  fi
  JM_SHA=$(printf '%s\n%s\n%s\n%s\n' "$JM_STATS_RE" "$GUARD_RE" "$JM_POSITION_RE" "$JM_STATS_LINES" | sha256sum | cut -d' ' -f1)
  if [ "$JM_SHA" != "$JM_APPROVED_SHA256" ]; then
    echo "Joda statistics exclusion changed (sha256 $JM_SHA, approved $JM_APPROVED_SHA256): approval needs renewal" >&2
    return 2
  fi
  FSLE_SHA=$(printf '%s\n%s\n%s\n%s\n' "$FSLE_STATS_RE" "$GUARD_RE" "$FSLE_POSITION_RE" "$FSLE_STATS_LINES" | sha256sum | cut -d' ' -f1)
  if [ "$FSLE_SHA" != "$FSLE_APPROVED_SHA256" ]; then
    echo "FSLE statistics exclusion changed (sha256 $FSLE_SHA, approved $FSLE_APPROVED_SHA256): approval needs renewal" >&2
    return 2
  fi
  FSLS_SHA=$(printf '%s\n%s\n%s\n%s\n' "$FSGN_STATS_RE" "$FSGN_GUARD_RE" "$FSLS_POSITION_RE" "$FSLS_STATS_LINES" | sha256sum | cut -d' ' -f1)
  if [ "$FSLS_SHA" != "$FSLS_APPROVED_SHA256" ]; then
    echo "FSGN statistics exclusion changed (sha256 $FSLS_SHA, approved $FSLS_APPROVED_SHA256): approval needs renewal" >&2
    return 2
  fi
}

stats_exclusion_evidence() {
  if [ "${2:-}" = FSLS0100 ]; then
    { echo "$FSLS_EXCLUSION_VERSION"; echo "$FSLS_APPROVAL"
      echo "STATS_RE=$FSGN_STATS_RE"; echo "GUARD_RE=$FSGN_GUARD_RE"
      echo "POSITION_RE=$FSLS_POSITION_RE"; echo "expected statistics lines $FSLS_STATS_LINES"
      echo "approved sha256 $FSLS_APPROVED_SHA256"; echo "sha256 $FSLS_SHA"
    } > "$1"
    return
  fi
  if [ "${2:-}" = FSLE0100 ]; then
    { echo "$FSLE_EXCLUSION_VERSION"; echo "$FSLE_APPROVAL"
      echo "STATS_RE=$FSLE_STATS_RE"; echo "GUARD_RE=$GUARD_RE"
      echo "POSITION_RE=$FSLE_POSITION_RE"; echo "expected statistics lines $FSLE_STATS_LINES"
      echo "approved sha256 $FSLE_APPROVED_SHA256"; echo "sha256 $FSLE_SHA"
    } > "$1"
    return
  fi
  if [ "${2:-}" = JMLE0100 ]; then
    { echo "$JM_EXCLUSION_VERSION"; echo "$JM_APPROVAL"
      echo "STATS_RE=$JM_STATS_RE"; echo "GUARD_RE=$GUARD_RE"
      echo "POSITION_RE=$JM_POSITION_RE"; echo "expected statistics lines $JM_STATS_LINES"
      echo "approved sha256 $JM_APPROVED_SHA256"; echo "sha256 $JM_SHA"
    } > "$1"
    return
  fi
  { echo "$EXCLUSION_VERSION"; echo "$APPROVAL"
    echo "STATS_RE=$STATS_RE"; echo "GUARD_RE=$GUARD_RE"
    echo "approved sha256 $APPROVED_SHA256"; echo "sha256 $SHA"
  } > "$1"
}

stats_only_filter() { # legacy SYSOUT, step, kept file, excluded file; prints excluded count
  local stats=$STATS_RE guard=$GUARD_RE position="^$2 RECORDS READ" expected=-1
  if [ "$2" = FSLS0100 ]; then
    stats=$FSGN_STATS_RE; guard=$FSGN_GUARD_RE; position=$FSLS_POSITION_RE; expected=$FSLS_STATS_LINES
  elif [ "$2" = FSLE0100 ]; then
    stats=$FSLE_STATS_RE; position=$FSLE_POSITION_RE; expected=$FSLE_STATS_LINES
  elif [ "$2" = JMLE0100 ]; then
    stats=$JM_STATS_RE; position=$JM_POSITION_RE; expected=$JM_STATS_LINES
  fi
  awk -v S="$stats" -v G="$guard" -v P="$position" -v E="$expected" -v K="$3" -v X="$4" '
    { sub(/\r$/, ""); line[NR] = $0 }
    END {
      printf "" > K; printf "" > X
      t = 0; for (i = 1; i <= NR; i++) if (line[i] ~ P) { t = i; break }
      if (t > 0) for (j = t - 1; j >= 1 && line[j] ~ S && line[j] !~ G; j--) { ex[j] = 1; n++ }
      for (i = 1; i <= NR; i++) { if (i in ex) print line[i] >> X; else print line[i] >> K }
      if (t > 0 && E >= 0 && n + 0 != E) {
        printf "Unexpected statistics block count: %d, expected %d\n", n + 0, E > "/dev/stderr"
        exit 2
      }
      printf "%d\n", n + 0
    }' "$1"
}
