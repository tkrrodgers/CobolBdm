#!/usr/bin/env bash
# The OFBiz host lane of the forward pipeline (ForwardEngineeringFineract.md §9.1, GemmaJavaTemplates.md §5.11): the plugin is
# rendered from the service model by the H-OFBIZ templates, the holes come from a directory (the SME's reference or the model's
# code), the engine from its jars, the scenarios from the SME package; OFBiz's own build compiles it and checkstyle judges it
# (A-13), and its test runner runs the scenarios on a restored H2 snapshot.
#
#   prepare   render, add holes, engine and scenarios, compile + checkstyleMain, load the plugin's data on the base snapshot,
#             save the plugin snapshot
#   judge     copy the holes, compile + checkstyleMain, restore the plugin snapshot, run 'ofbiz --test component=<plugin>',
#             copy the JUnit XML and logs to $OUT
#   mutants   the host gate's discrimination check: judge every mutant of host/mutants.json (the reference holes with one
#             rule broken); each must fail a scenario. Writes host/mutants-result.json
#   gate      the host gate: the reference holes pass every scenario (judge) and every mutant dies (mutants) -> host/gate.json
#   init      once per OFBiz pin: H2 writes every commit at once, loadAll, the base snapshot
#   render    only render the plugin (no OFBiz run): the service model (leak-checked against the package's dictionary), TG1
#             (two renders byte-identical), the render and its lock, the scenarios, the facade digest (host/ofbiz-facade.md)
#   regression  G12: OFBiz's whole integration suite without and with the plugin; same outcome for every OFBiz test
#             -> host/regression[-LABEL].json (run it in a tree of its own: OFBIZ=..., SNAPSHOTS=...)
#   shadow    G11: the shadow run (bookings, export, the legacy job by the GnuCOBOL oracle, reconciliation)
#             -> host/shadow[-LABEL].json: per day the loans reconciled and the count of each outcome
#   paths     PLUGIN=, SRC= (the holes' package directory) and REFERENCE= (the SME's reference holes), for the harness
#   lock-check  TG3: no generated file of the plugin changed
#
# Environment: OFBIZ (OFBiz root), PACKAGE (the SME package, e.g. forward/loans), HOLES (directory of hole .java files), ENGINE
# (directory of engine jars), OUT (judge output), SNAPSHOTS (default $OFBIZ/../ofbiz-snapshots), JAVA_HOME.
set -u
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
OFBIZ_GIVEN="${OFBIZ:-}"
OFBIZ="${OFBIZ:-/c/Users/Tomrodgers/Source/ofbiz-framework}"
PACKAGE="${PACKAGE:-$REPO/forward/loans}"
SNAPSHOTS_GIVEN="${SNAPSHOTS:-}"
SNAPSHOTS="${SNAPSHOTS:-$OFBIZ/../ofbiz-snapshots}"
JAVA_HOME="${JAVA_HOME:-C:\\Users\\Tomrodgers\\AppData\\Local\\Programs\\Java\\zulu21.52.203-ca-jdk21.0.12.1-win_x64}"
export JAVA_HOME
TEMPLATES="dotnet $REPO/MlCpu/src/MlCpu.Templates/bin/Release/net10.0/MlCpu.Templates.dll"
MAPPING="$PACKAGE/host/host-mapping.yaml"
MODEL="$PACKAGE/host/service-model.json"
# the environment profile and the template bundle: forward.json host.profile and host.bundle
HOSTSPEC="$(cygpath -w "$PACKAGE/forward.json" 2>/dev/null || echo "$PACKAGE/forward.json")"
PROFILE="$REPO/$(python -c "import json,sys; print(json.load(open(sys.argv[1],encoding='utf-8-sig'))['host'].get('profile','environments/ofbiz-plugin.yaml'))" "$HOSTSPEC" | tr -d '\r')"
BUNDLE="$(python -c "import json,sys; print(json.load(open(sys.argv[1],encoding='utf-8-sig'))['host'].get('bundle','H-OFBIZ'))" "$HOSTSPEC" | tr -d '\r')"
PLUGIN_ID=$(grep -A3 '^plugin:' "$MAPPING" | sed -n 's/^  id: *\([a-z0-9-]*\).*/\1/p')
PLUGIN="$OFBIZ/plugins/$PLUGIN_ID"
JAVA_PKG=$(grep -A6 '^plugin:' "$MAPPING" | sed -n 's/^  javaPackage: *\([a-z0-9.]*\).*/\1/p')
SRC="$PLUGIN/src/main/java/${JAVA_PKG//.//}"
PY="python -I $(cygpath -w "$REPO/tools/forward/host_mutants.py" 2>/dev/null || echo "$REPO/tools/forward/host_mutants.py")"
win() { cygpath -w "$1" 2>/dev/null || echo "$1"; }
# OFBIZ_HEAP (e.g. 6g): the test run's heap, OFBiz's own JVM arguments (build.gradle) with -Xmx replaced
jvmargs() {
  [ -n "${OFBIZ_HEAP:-}" ] || return 0
  python -c "import re,sys; t=open(sys.argv[1],encoding='utf-8').read(); m=re.search(r'applicationDefaultJvmArgs = .*?: \[(.*?)\]', t, re.S); a=re.findall(r\"'([^']*)'\", m.group(1)); print(' '.join(x if not x.startswith('-Xmx') else '-Xmx' + sys.argv[2] for x in a))" \
    "$(cygpath -w "$OFBIZ/build.gradle" 2>/dev/null || echo "$OFBIZ/build.gradle")" "$OFBIZ_HEAP" | tr -d '\r'
}
# PORT_OFFSET: OFBiz ports shifted (--portoffset), so that a second tree (G12) can run beside the judge's
PORTS="${PORT_OFFSET:+ --portoffset=$PORT_OFFSET}"
log() { echo "$(date '+%H:%M:%S') $*"; }
gradlew() { (cd "$OFBIZ" && ./gradlew.bat --no-daemon -q "$@"); }

# One run at a time per tree: a judge, regression or shadow run that finds another one alive refuses (a stray process once
# replaced a running shadow run's scenarios); a lock whose process is gone is taken over.
take_lock() {  # take_lock <name>
  local lock="$SNAPSHOTS/.$1.lock"
  mkdir -p "$SNAPSHOTS"
  if ! mkdir "$lock" 2>/dev/null; then
    local owner; owner=$(cat "$lock/pid" 2>/dev/null)
    if [ -n "$owner" ] && kill -0 "$owner" 2>/dev/null; then log "another $1 runs in this tree (pid $owner): refused"; exit 3; fi
    log "taking over a stale $1 lock (pid ${owner:-?})"
  fi
  echo $$ > "$lock/pid"
  trap "rm -rf '$lock'" EXIT
}

REFERENCE="$PACKAGE/host/reference/src/main/java/${JAVA_PKG//.//}"
LEAKS=$(python -c "import json,sys; print(json.load(open(sys.argv[1],encoding='utf-8-sig')).get('leakDictionary') or '')" "$(cygpath -w "$PACKAGE/forward.json" 2>/dev/null || echo "$PACKAGE/forward.json")" | tr -d '\r')

render() {
  $TEMPLATES model --api "$PACKAGE/host/api-model.json" --mapping "$MAPPING" --out "$MODEL" ${LEAKS:+--leaks "$PACKAGE/$LEAKS"} || return 1
  $TEMPLATES conform --catalog "$REPO/templates" --model "$MODEL" --profile "$PROFILE" --bundle "$BUNDLE" || return 1
  mkdir -p "$PLUGIN"
  $TEMPLATES render --catalog "$REPO/templates" --model "$MODEL" --profile "$PROFILE" --bundle "$BUNDLE" --out "$PLUGIN" --clean || return 1
  rm -rf "${PLUGIN:?}/testdef/scenarios" && cp -r "$PACKAGE/host/scenarios" "$PLUGIN/testdef/scenarios"
  $TEMPLATES facade --ofbiz "$OFBIZ" --model "$MODEL" --out "$PACKAGE/host/ofbiz-facade.md" || return 1
}

holes() {
  [ -d "${HOLES:-}" ] || { log "HOLES is not a directory: ${HOLES:-}"; return 1; }
  # Only hole files go in: a file the lock lists is generated and is never replaced.
  for f in "$HOLES"/*.java; do
    rel="src/main/java/${JAVA_PKG//.//}/$(basename "$f")"
    if grep -q "\"$rel\"" "$PLUGIN/generated.lock.json"; then log "refused: $(basename "$f") is generated"; return 1; fi
    cp "$f" "$SRC/"
  done
}

engine() {
  [ -d "${ENGINE:-}" ] || { log "ENGINE is not a directory: ${ENGINE:-}"; return 1; }
  mkdir -p "$PLUGIN/lib" && rm -f "${PLUGIN:?}/lib/"*.jar && cp "$ENGINE"/*.jar "$PLUGIN/lib/"
}

compile() {
  log "compile + checkstyleMain"
  gradlew classes testClasses checkstyleMain > "$1/build.log" 2>&1
  local rc=$?
  log "compile + checkstyleMain: exit $rc"
  return $rc
}

restore() {
  rm -f "${OFBIZ:?}/runtime/data/h2/"* && cp "$SNAPSHOTS/$1/"* "$OFBIZ/runtime/data/h2/"
}

case "${1:-}" in
  init)
    # OFBiz's command-line runs (load-data, --test) exit without closing H2 (DB_CLOSE_ON_EXIT=FALSE), and H2's MVStore
    # writes commits in the background: the last ones were lost (a plugin's demo rows went missing from the snapshot). In the
    # judge environment H2 closes, and so flushes, at JVM exit; then loadAll builds the base snapshot. Configuration only.
    ee="$OFBIZ/framework/entity/config/entityengine.xml"
    sed -i 's|DB_CLOSE_ON_EXIT=FALSE;WRITE_DELAY=0;|DB_CLOSE_ON_EXIT=FALSE;|; s|jdbc:h2:file:./runtime/data/h2/ofbiz;DB_CLOSE_ON_EXIT=FALSE;|jdbc:h2:file:./runtime/data/h2/ofbiz;DB_CLOSE_ON_EXIT=TRUE;|' "$ee"
    grep -q "h2/ofbiz;DB_CLOSE_ON_EXIT=TRUE;" "$ee" || { log "could not set DB_CLOSE_ON_EXIT=TRUE in $ee"; exit 1; }
    mkdir -p "$SNAPSHOTS/init-log"
    rm -f "${OFBIZ:?}/runtime/data/h2/"*
    log "loadAll (seed, seed-initial, demo)"
    gradlew loadAll > "$SNAPSHOTS/init-log/loadAll.log" 2>&1 || { log "loadAll failed: $SNAPSHOTS/init-log/loadAll.log"; exit 1; }
    rm -rf "${SNAPSHOTS:?}/base" && mkdir -p "$SNAPSHOTS/base" && cp "$OFBIZ/runtime/data/h2/"* "$SNAPSHOTS/base/"
    log "base snapshot: $SNAPSHOTS/base"
    ;;
  render) render ;;
  prepare)
    work="$SNAPSHOTS/prepare-log"; mkdir -p "$work"
    render && holes && engine && compile "$work" || exit 1
    [ -d "$SNAPSHOTS/base" ] || { log "no base snapshot in $SNAPSHOTS/base (copy runtime/data/h2 after loadAll)"; exit 1; }
    restore base
    log "load the plugin's data"
    gradlew "ofbiz -l readers=seed,seed-initial,demo -l component=$PLUGIN_ID$PORTS" > "$work/loaddata.log" 2>&1 || { log "data load failed: $work/loaddata.log"; exit 1; }
    rm -rf "${SNAPSHOTS:?}/${PLUGIN_ID:?}" && mkdir -p "$SNAPSHOTS/$PLUGIN_ID" && cp "$OFBIZ/runtime/data/h2/"* "$SNAPSHOTS/$PLUGIN_ID/"
    log "prepared: snapshot $SNAPSHOTS/$PLUGIN_ID"
    ;;
  judge)
    OUT="${OUT:?OUT is required}"; mkdir -p "$OUT"
    take_lock judge
    [ -n "${HOLES:-}" ] && { holes || exit 1; }
    [ -n "${ENGINE:-}" ] && { engine || exit 1; }
    [ -n "${SCENARIOS:-}" ] && { rm -rf "${PLUGIN:?}/testdef/scenarios" && cp -r "$SCENARIOS" "$PLUGIN/testdef/scenarios"; }
    compile "$OUT" || { echo compile > "$OUT/verdict.txt"; exit 2; }
    [ -n "${NO_RESTORE:-}" ] || restore "$PLUGIN_ID"
    log "ofbiz --test component=$PLUGIN_ID"
    JA="$(jvmargs)"
    gradlew "ofbiz --test component=$PLUGIN_ID$PORTS" ${JA:+"-PjvmArgs=$JA"} > "$OUT/test.log" 2>&1
    rc=$?
    rm -rf "${OUT:?}/test-results" && mkdir -p "$OUT/test-results" && cp "$OFBIZ/runtime/logs/test-results/"*.xml "$OUT/test-results/" 2>/dev/null
    log "tests: exit $rc; results in $OUT/test-results"
    echo "tests $rc" > "$OUT/verdict.txt"
    ;;
  mutants)
    # ONLY="id id": judge only those mutants again (the others keep their earlier results)
    runs="$SNAPSHOTS/mutants"
    [ -z "${ONLY:-}" ] && rm -rf "${runs:?}"
    mkdir -p "$runs/holes"
    $PY make "$(win "$PACKAGE")" "$(win "$runs/holes")" > "$runs/ids.txt" || exit 1
    [ -n "${ONLY:-}" ] && tr ' ' '\n' <<< "$ONLY" > "$runs/ids.txt"
    while read -r id; do
      [ -n "$id" ] || continue
      log "mutant $id"
      HOLES="$runs/holes/$id" OUT="$runs/$id" ENGINE="" SCENARIOS="" bash "$0" judge
    done < <(tr -d '\r' < "$runs/ids.txt")
    HOLES="$REFERENCE" holes
    $PY score "$(win "$PACKAGE")" "$(win "$runs")" "$(win "$PACKAGE/host/mutants-result.json")"
    ;;
  gate)
    ref="$SNAPSHOTS/gate-reference"
    HOLES="$REFERENCE" OUT="$ref" ENGINE="" SCENARIOS="" bash "$0" judge
    $PY junit "$(win "$ref/test-results")" "$(win "$ref/junit.json")"
    bash "$0" mutants || exit 1
    $PY gate "$(win "$PACKAGE")" "$(win "$ref/junit.json")" "$(win "$PACKAGE/host/mutants-result.json")" "$(win "$PACKAGE/host/gate.json")"
    ;;
  regression)
    # G12 (FE 9.1): OFBiz's whole integration suite ('ofbiz --test', what testIntegration runs) on the base snapshot without
    # the plugin, then with the plugin loaded (its entities, services, SECA and data; its own suite left out), and every test
    # of OFBiz's own components must have the same outcome. It takes about an hour: run it in a tree of its own (OFBIZ=,
    # SNAPSHOTS=; init there first). HOLES and ENGINE as for prepare.
    [ -n "$OFBIZ_GIVEN" ] && [ -n "${SNAPSHOTS_GIVEN:-}" ] || { log "regression removes and re-renders the plugin: give OFBIZ= and SNAPSHOTS= of a tree of its own"; exit 1; }
    take_lock judge   # it runs OFBiz's tests in the tree, as a judge does
    out="$SNAPSHOTS/regression"; rm -rf "${out:?}" && mkdir -p "$out"
    [ -d "$SNAPSHOTS/base" ] || { log "no base snapshot in $SNAPSHOTS/base: run init in this tree first"; exit 1; }
    rm -rf "${PLUGIN:?}"
    run_all() {
      restore "$1"
      log "ofbiz --test ($2)"
      gradlew "ofbiz --test$PORTS" > "$out/$2.log" 2>&1
      log "ofbiz --test ($2): exit $?"
      mkdir -p "$out/$2" && cp "$OFBIZ/runtime/logs/test-results/"*.xml "$out/$2/" 2>/dev/null
      rm -f "$OFBIZ/runtime/logs/test-results/"*.xml
    }
    gradlew classes testClasses > "$out/build-without.log" 2>&1 || { log "build failed: $out/build-without.log"; exit 1; }
    run_all base without
    render && holes && engine || exit 1
    sed -i '/<test-suite /d' "$PLUGIN/ofbiz-component.xml"
    compile "$out" || { log "plugin build failed: $out/build.log"; exit 1; }
    restore base
    gradlew "ofbiz -l readers=seed,seed-initial,demo -l component=$PLUGIN_ID$PORTS" > "$out/loaddata.log" 2>&1 || { log "data load failed"; exit 1; }
    rm -rf "${SNAPSHOTS:?}/regression-with" && mkdir -p "$SNAPSHOTS/regression-with" && cp "$OFBIZ/runtime/data/h2/"* "$SNAPSHOTS/regression-with/"
    run_all regression-with with
    $PY regression "$(win "$out/without")" "$(win "$out/with")" "$(win "$PACKAGE/host/regression${LABEL:+-$LABEL}.json")"
    ;;
  shadow)
    # G11 (FE 9.3) in a tree of its own (OFBIZ=, SNAPSHOTS=, PORT_OFFSET=; prepared with the engine and holes under test). One
    # OFBiz test run (its sandbox rolls the database back afterwards): the days' bookings and exports (exportLoansAsLegacyRequests),
    # the legacy job on the exports (a command step: shadow_oracle.py legacy, CobolBdm's GnuCOBOL oracle through MLCPU_HOST), each
    # day reconciled in full with the service's loans (migrateLegacyLoanBook, mode SHADOW: no DIFF) -> host/shadow[-LABEL].json.
    # Environment: FCG (the fineract-cobol-gen root), SHADOW_DAYS (10), SHADOW_PER_DAY (500), MLCPU_HOST, OFBIZ_HEAP (6g).
    [ -n "$OFBIZ_GIVEN" ] && [ -n "$SNAPSHOTS_GIVEN" ] || { log "shadow needs OFBIZ= and SNAPSHOTS= of a tree of its own"; exit 1; }
    take_lock shadow
    sd="$SNAPSHOTS/shadow"; rm -rf "${sd:?}" && mkdir -p "$sd/exports"
    host="${MLCPU_HOST:-http://localhost:5100}"
    cp "${FCG:?FCG is required}/corpus/CURRENCY.txt" "$sd/CURRENCY.txt"
    python -c "import sys; d=open(sys.argv[1],'rb').read().split(b'\n'); open(sys.argv[2],'wb').write(b''.join(l.rstrip(b'\r')[:40].ljust(40) for l in d if l.strip()))" \
      "$(win "$sd/CURRENCY.txt")" "$(win "$sd/exports/CURRENCY.VSAM.KSDS")"
    oracle=$(python -c "import json,sys; print(json.dumps(sys.argv[1:]))" python -I "$(win "$REPO/tools/forward/shadow_oracle.py" | tr '\\' '/')" legacy \
      "$(win "$sd/exports" | tr '\\' '/')" "$(win "$REPO/projects/fineract-cobol-cr" | tr '\\' '/')" "$host")
    sme="$PACKAGE/sme"; (cd "$sme" && gradle -q --no-daemon installDist) || exit 1
    (cd "$sme" && "$JAVA_HOME/bin/java" -Xss64m -cp "build/install/loans-sme/lib/*" com.mlcpu.loans.sme.ShadowRunPool \
      "$(win "$FCG/corpus/envelope-2/LOAN.REQUEST.txt")" "$(win "$sd/CURRENCY.txt")" "$(win "$MODEL")" "$(win "$sd/scenarios")" \
      "$(win "$sd/exports" | tr '\\' '/')" "${SHADOW_DAYS:-10}" "${SHADOW_PER_DAY:-500}" "$oracle") || exit 1
    log "the shadow run: bookings and exports, the legacy job, the reconciliation (one OFBiz run)"
    OUT="$sd/run" SCENARIOS="$sd/scenarios/run" HOLES= ENGINE= OFBIZ_HEAP="${OFBIZ_HEAP:-6g}" bash "$0" judge || exit 1
    python -I "$(win "$REPO/tools/forward/shadow_oracle.py")" report "$(win "$sd")" "$(win "$PACKAGE/host/shadow${LABEL:+-$LABEL}.json")"
    ;;
  paths) echo "PLUGIN=$(win "$PLUGIN")"; echo "SRC=$(win "$SRC")"; echo "REFERENCE=$(win "$REFERENCE")" ;;
  lock-check) $TEMPLATES lock-check --out "$PLUGIN" ;;
  *) echo "usage: ofbiz-host.sh init|render|prepare|judge|mutants|gate|paths|lock-check"; exit 2 ;;
esac
