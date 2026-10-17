#!/usr/bin/env bash
# GN1 (SemanticModernization.md §8): the generated project builds (the generated Gradle build: javac --release 21
# -Xlint:all) and a second generation is byte-identical to the installed one (sources, resources, readme, build files).
# usage: tools/modernize/gn1.sh <estate> [project dir]   (MODERN_ROOT: where generated estates live)
set -u
EST=${1:-fineract-xl}
REPO=$(cd "$(dirname "$0")/../.." && pwd)
MODERN_ROOT=${MODERN_ROOT:-C:/Users/Tomrodgers/Source/ModernizedJava}
PROJ=${2:-$REPO/projects/$EST}
ROOT=$MODERN_ROOT/$EST
MOD=$REPO/MlCpu/src/MlCpu.Modernize
SECOND=$(mktemp -d)
dotnet "$MOD/bin/Debug/net10.0/MlCpu.Modernize.dll" lift "$PROJ" "$SECOND" > /dev/null 2>&1
same=yes
for p in src MODERNIZATION.md build.gradle.kts settings.gradle.kts modernize/residue.json; do
  diff -r "$ROOT/$p" "$SECOND/$p" > /dev/null 2>&1 || { same=no; echo "  differs: $p"; }
done
rm -rf "${SECOND:?}"
(cd "$ROOT" && gradle -q --offline build > build/gn1.txt 2>&1)
rc=$?
echo "GN1 $EST: build exit $rc ($(grep -c 'warning:' "$ROOT/build/gn1.txt") warnings, $(grep -c 'error:' "$ROOT/build/gn1.txt") errors), second generation identical: $same"
[ $rc -eq 0 ] && [ $same = yes ]
