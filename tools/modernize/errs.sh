#!/usr/bin/env bash
# show javac errors matching a pattern with the offending line: tools/modernize/errs.sh <estate> <pattern> [n]
MODERN_ROOT=${MODERN_ROOT:-C:/Users/Tomrodgers/Source/ModernizedJava}
cd "$MODERN_ROOT/$1" || exit 1
grep -n "error:" build/javac.txt | grep -E "$2" | head -${3:-6} | while IFS=: read -r n rest; do
  sed -n "${n},$((n+5))p" build/javac.txt | grep -v "^\s*\^\s*$" | head -4
  echo "--"
done
