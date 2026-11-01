#!/usr/bin/env bash
# Distance to original for the four loop-4 estates, then the cross-estate gap catalogue (COMPARE-GAPS.md).
#   tools/jobol-auditor/compare-estates.sh [out-dir]   (default: tools/jobol-auditor/compare)
# Inputs are read-only: the original Java (JavaToCOBOLTest packages and vendor/fineract), the J3S COBOL (CobolBdm
# projects/<estate>/source) and the generated modern estates. Override the roots with the variables below.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
OUT=${1:-$HERE/compare}
PKG=${PKG:-C:/Users/Tomrodgers/JavaToCOBOLTest/claude-in-app/workspace/packages}
VENDOR=${VENDOR:-C:/Users/Tomrodgers/JavaToCOBOLTest/vendor}
BDM=${BDM:-C:/Users/Tomrodgers/Source/CobolBdm}
MODERN=${MODERN:-C:/Users/Tomrodgers/Source/ModernizedJava}
REG=$BDM/work/loop4/regress
A="$HERE/jobol-audit.sh"

"$A" compare --name joda-money --quiet --out "$OUT/joda-money" \
  --original "$PKG/jodaorg-joda-money-39d5bbf/source/src/main/java" \
  --modern "$MODERN/joda-money-loop4-iter6/src/main/java" --cobol "$BDM/projects/joda-money/source"
"$A" compare --name kontor-zins --quiet --out "$OUT/kontor-zins" \
  --original "$PKG/manikouchakian-kontor-zins-b507fe6/source/src/main/java" \
  --modern "$REG/kontor-zins-m5/src/main/java" --cobol "$BDM/projects/kontor-zins/source"
"$A" compare --name loanamorti --quiet --out "$OUT/loanamorti" \
  --original "$PKG/artyompanfutov-loan-amortization-calculator-74c5d4f/source/src/main/java" \
  --modern "$REG/loanamorti-m5/src/main/java" --cobol "$BDM/projects/loanamorti-74c5d4f-065118/source"
"$A" compare --name fineract-xl --quiet --out "$OUT/fineract-xl" \
  --original "$VENDOR/fineract/*/src/main/java" \
  --modern "$REG/fineract-xl-m5/src/main/java" --cobol "$BDM/projects/fineract-xl/source"

"$A" compare-gaps --catalogue "$HERE/compare-catalogue.json" --out "$OUT" --md "$HERE/COMPARE-GAPS.md" \
  --report "$OUT/joda-money/compare-report.json" --report "$OUT/kontor-zins/compare-report.json" \
  --report "$OUT/loanamorti/compare-report.json" --report "$OUT/fineract-xl/compare-report.json"
