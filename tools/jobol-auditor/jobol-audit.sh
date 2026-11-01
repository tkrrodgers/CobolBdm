#!/usr/bin/env bash
# JOBOL audit of a generated Java estate (JavaJOBOLAuditor.md). All arguments go to the auditor:
#   tools/jobol-auditor/jobol-audit.sh <estate-dir> [--out DIR] [--baseline prev/jobol-report.json] [--no-gate] ...
# Runs the Gradle distribution when it is installed (gradle installDist); otherwise compiles the sources with javac
# against the javaparser/gson jars in the Gradle cache (no network, no Gradle daemon) into build/direct.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
DIST="$HERE/build/install/jobol-audit/bin/jobol-audit"
if [ -x "$DIST" ] && [ -z "${JOBOL_DIRECT:-}" ]; then
  exec "$DIST" "$@"
fi
CACHE=${GRADLE_USER_HOME:-$HOME/.gradle}/caches/modules-2/files-2.1
jar() { find "$CACHE/$1" -name "$2" 2>/dev/null | head -1; }
JP=$(jar com.github.javaparser/javaparser-core/3.28.2 javaparser-core-3.28.2.jar)
GS=$(jar com.google.code.gson/gson/2.11.0 gson-2.11.0.jar)
if [ -z "$JP" ] || [ -z "$GS" ]; then
  echo "jobol-audit: javaparser-core 3.28.2 / gson 2.11.0 not in $CACHE; run 'gradle installDist' in $HERE once" >&2
  exit 2
fi
SEP=":"; conv() { echo "$1"; }
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) SEP=";"; conv() { cygpath -w "$1"; } ;; esac
OUT="$HERE/build/direct/classes"
STAMP="$HERE/build/direct/.stamp"
if [ ! -f "$STAMP" ] || [ -n "$(find "$HERE/src/main" -newer "$STAMP" -type f | head -1)" ]; then
  rm -rf "$OUT" && mkdir -p "$OUT"
  # shellcheck disable=SC2046
  javac -encoding UTF-8 -nowarn -d "$(conv "$OUT")" -cp "$(conv "$JP")$SEP$(conv "$GS")" $(find "$HERE/src/main/java" -name '*.java')
  cp "$HERE/src/main/resources/"* "$OUT/"
  touch "$STAMP"
fi
exec java -Xss16m -Xmx2g -cp "$(conv "$OUT")$SEP$(conv "$JP")$SEP$(conv "$GS")" com.mlcpu.jobol.Main "$@"
