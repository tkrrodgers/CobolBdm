#!/usr/bin/env bash
# The service's engine as jars for a host (FE 9.1): the API and the implementation modules built from an implementation
# directory (<dir>/<module>/src/main/java: the SME's reference, or a forward run's deliverable <run>/implementation/java), with
# the package's own Gradle build and module graph. A module the directory has no sources for is an empty jar.
#   engine-jars.sh <package dir> <implementation dir> <out dir>
set -u
PACKAGE="$(cd "${1:?package dir}" && pwd)"
IMPL="$(cd "${2:?implementation dir}" && pwd)"
OUT="${3:?out dir}"
SERVICE="$PACKAGE/service"
# The modules of the service's settings (an include may wrap over lines) but the architecture and acceptance modules
# (forward.json service.architectureModule and service.acceptanceModule).
manifest="$(cygpath -w "$PACKAGE/forward.json" 2>/dev/null || echo "$PACKAGE/forward.json")"
skip=$(python -c "import json,sys; s=json.load(open(sys.argv[1],encoding='utf-8'))['service']; print(s['architectureModule']); print(s['acceptanceModule'])" "$manifest" | tr -d '\r')
modules=$(tr -s '\r\n' '  ' < "$SERVICE/settings.gradle.kts" | grep -o 'include([^)]*)' | grep -o '"[^"]*"' | tr -d '"' | grep -v -x -F -e "$(echo "$skip" | head -1)" -e "$(echo "$skip" | tail -1)")
tasks=$(for m in $modules; do printf ':%s:jar ' "$m"; done)
(cd "$SERVICE" && gradle -q --no-daemon -PimplDir="$IMPL" $tasks) || { echo "engine build failed ($IMPL)"; exit 1; }
mkdir -p "$OUT" && rm -f "${OUT:?}/"*.jar
for m in $modules; do
  jar=$(ls "$SERVICE/$m/build/libs/"*.jar 2>/dev/null | head -1)
  [ -n "$jar" ] || { echo "no jar for $m"; exit 1; }
  cp "$jar" "$OUT/$m.jar"
done
echo "engine: $(ls "$OUT" | wc -l) jars from $IMPL -> $OUT"
