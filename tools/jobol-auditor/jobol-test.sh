#!/usr/bin/env bash
# The auditor's unit tests without Gradle: javac against the jars in the Gradle cache, then the JUnit Platform launcher
# (test-runner/JUnitMain.java). Equivalent to 'gradle test' where Gradle can run.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
CACHE=${GRADLE_USER_HOME:-$HOME/.gradle}/caches/modules-2/files-2.1
jar() { find "$CACHE/$1" -name "$2" 2>/dev/null | head -1; }
JARS=(
  "$(jar com.github.javaparser/javaparser-core/3.28.2 javaparser-core-3.28.2.jar)"
  "$(jar com.google.code.gson/gson/2.11.0 gson-2.11.0.jar)"
  "$(jar org.junit.jupiter/junit-jupiter-api/5.11.3 junit-jupiter-api-5.11.3.jar)"
  "$(jar org.junit.jupiter/junit-jupiter-engine/5.11.3 junit-jupiter-engine-5.11.3.jar)"
  "$(jar org.junit.platform/junit-platform-commons/1.11.3 junit-platform-commons-1.11.3.jar)"
  "$(jar org.junit.platform/junit-platform-engine/1.11.3 junit-platform-engine-1.11.3.jar)"
  "$(jar org.junit.platform/junit-platform-launcher/1.11.3 junit-platform-launcher-1.11.3.jar)"
  "$(jar org.opentest4j/opentest4j/1.3.0 opentest4j-1.3.0.jar)"
  "$(jar org.apiguardian/apiguardian-api/1.1.2 apiguardian-api-1.1.2.jar)"
)
for j in "${JARS[@]}"; do
  if [ -z "$j" ]; then echo "jobol-test: a dependency is missing from $CACHE; run 'gradle test' once" >&2; exit 2; fi
done
SEP=":"; conv() { echo "$1"; }
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) SEP=";"; conv() { cygpath -w "$1"; } ;; esac
CP=""
for j in "${JARS[@]}"; do CP="$CP$(conv "$j")$SEP"; done
OUT="$HERE/build/junit/classes"
rm -rf "$OUT" && mkdir -p "$OUT"
# shellcheck disable=SC2046
javac -encoding UTF-8 -nowarn -d "$(conv "$OUT")" -cp "$CP" $(find "$HERE/src/main/java" "$HERE/src/test/java" -name '*.java')
cp -r "$HERE/src/main/resources/." "$OUT/"
cp -r "$HERE/src/test/resources/." "$OUT/"
exec java -Xss16m -cp "$(conv "$OUT")$SEP$CP" "$(conv "$HERE/test-runner/JUnitMain.java")" "$@"
