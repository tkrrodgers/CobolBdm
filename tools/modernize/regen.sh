#!/usr/bin/env bash
# Regenerate an estate's modern Java with MlCpu.Modernize and compile it; prints the error summary.
# usage: tools/modernize/regen.sh <estate> [project dir]   (MODERN_ROOT: where generated estates live)
set -u
EST=${1:-fineract-xl}
REPO=$(cd "$(dirname "$0")/../.." && pwd)
MODERN_ROOT=${MODERN_ROOT:-C:/Users/Tomrodgers/Source/ModernizedJava}
PROJ=${2:-$REPO/projects/$EST}
ROOT=$MODERN_ROOT/$EST
MOD=$REPO/MlCpu/src/MlCpu.Modernize
dotnet build "$MOD" -c Debug 2>&1 | grep -E " error |rror\(s\)" | sort -u | head -5
dotnet "$MOD/bin/Debug/net10.0/MlCpu.Modernize.dll" lift "$PROJ" "$ROOT" 2>&1 | tail -4
cd "$ROOT" || exit 1
rm -rf build/classes && mkdir -p build/classes
find src/main/java -name "*.java" > build/sources.txt
javac -d build/classes -Xmaxerrs 5000 -nowarn @build/sources.txt > build/javac.txt 2>&1
echo "javac: $(tail -1 build/javac.txt)"
grep "error:" build/javac.txt | sed -E 's/^[^:]*:[0-9]+: error: //' | sed -E 's/[A-Za-z0-9_]+\(/X(/g' | sort | uniq -c | sort -rn | head -${3:-25}
