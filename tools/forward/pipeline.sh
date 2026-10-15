#!/usr/bin/env bash
# The forward pipeline from a fixed API to a host integration, stage by stage (ForwardEngineeringFineract.md, GemmaJavaTemplates.md;
# docs/forward-pipeline.md). Every stage is deterministic except the two model stages, and reads what differs between packages
# from the package: forward.json (the service, its slices, its "host" section and the host lane's script), host/host-mapping.yaml
# (the SME's mapping of the API onto the host) and the package's SME material (reference, scenarios, digests, mutants).
#
#   pipeline.sh <stage> [stage ...]          PACKAGE=forward/loans by default
#     gate         the engine's Phase 0 gate has passed (<package>/gate.json; run by <package>/tools/Test-ForwardGate.ps1)
#     legacy       the legacy records and their decoders (C-LEGACY-LAYOUT): <package>/legacy/legacy-model.json from the record kit's
#                  layouts and legacy/legacy-mapping.yaml (leak-checked), rendered into the API module's src/generated (TG1, lock)
#     api          the API's jar and its model (tools/api-importer): <package>/host/api-model.json
#     engine       the engine as jars from IMPL (an implementation directory: <package>/reference, or a run's implementation/java)
#                  -> <package>/host/build/engine-<ENGINE_LABEL> (default label: reference)
#     prepare      the host lane's prepare: service model (leak-checked), render (TG1 conform, lock), facade digest, the SME's
#                  reference holes and the engine, compile + checkstyle, the host's data, the snapshot
#     host-gate    the reference holes pass every host scenario, and every mutant of host/mutants.json fails one:
#                  <package>/host/gate.json
#     pools        the engine's scenario pools from the SME's generators (forward.json pools: CalendarPool ... MigrationPool), in
#                  order: <package>/scenarios.json and the acceptance scenarios; then the Phase 0 gate (Test-ForwardGate.ps1)
#     host-scenarios  the host scenarios from the SME's generators (forward.json host.generators: OfbizPool, OfbizMigrationPool)
#     regression   G12 in a checkout of its own (REGRESSION_OFBIZ, REGRESSION_SNAPSHOTS; init there once): OFBiz's whole
#                  integration suite with and without the plugin -> <package>/host/regression[-LABEL].json
#     shadow       G11 in a checkout of its own (SHADOW_OFBIZ, SHADOW_SNAPSHOTS, by default the regression's; FCG; MLCPU_HOST: the
#                  CobolBdm host whose GnuCOBOL oracle runs the legacy job): ten days of bookings, each day's export through the
#                  legacy job, every loan reconciled, no DIFF -> <package>/host/shadow-<LABEL, default ENGINE_LABEL>.json
#     run          the model writes the holes (MODEL_RUNNER: the eval's run script, e.g. modelEval/forward-loans/run-host.sh)
#     final        HOLES (a run's java directory) judged on the prepared plugin, and the lock (TG3): <package>/host/final-<label>.json
#
# Example (the loans service into OFBiz with Gemma's engine and Gemma's holes):
#   IMPL=<run>/implementation/java ENGINE_LABEL=gemma pipeline.sh gate legacy api engine prepare host-gate
#   MODEL_RUNNER=<modelEval>/forward-loans/run-host.sh pipeline.sh run
#   HOLES=<modelEval>/forward-loans/gemma-host/host/java LABEL=gemma pipeline.sh final
set -u
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
PACKAGE="$(cd "${PACKAGE:-$REPO/forward/loans}" && pwd)"
export PACKAGE
JAVA_HOME="${JAVA_HOME:-C:\\Users\\Tomrodgers\\AppData\\Local\\Programs\\Java\\zulu21.52.203-ca-jdk21.0.12.1-win_x64}"
export JAVA_HOME
win() { cygpath -w "$1" 2>/dev/null || echo "$1"; }
json() { python -c "import functools,json,sys; d=json.load(open(sys.argv[1],encoding='utf-8-sig')); print(functools.reduce(lambda o, k: o[k], sys.argv[2].split('.'), d))" "$(win "$PACKAGE/forward.json")" "$1" | tr -d '\r'; }
log() { echo "$(date '+%H:%M:%S') [pipeline] $*"; }
HOST_SCRIPT="$(cd "$PACKAGE" && cd "$(dirname "$(json host.script)")" && pwd)/$(basename "$(json host.script)")"
SERVICE="$PACKAGE/$(json service.dir)"
API_MODULE="$(json service.apiModule)"
API_PACKAGE="$(json service.apiPackage)"
ENGINE_DIR="$PACKAGE/host/build/engine-${ENGINE_LABEL:-reference}"

# generators <section>: forward.json <section>.generators (pools, host), the SME's scenario generators in order, each a main class
# of the SME project (<section>.sme) run in its folder with its JVM options ("jvm"); ${repo} and ${FCG} in an argument are the
# repository and the fineract-cobol-gen root (FCG)
generators() {
  local sme; sme="$PACKAGE/$(json "$1.sme")"
  (cd "$sme" && gradle -q --no-daemon installDist) || return 1
  local cp; cp="$(cd "$sme" && ls -d build/install/*/lib)/*"
  python -c "import json,sys; [print('\t'.join([g['main'], ' '.join(g.get('jvm', [])) or '-'] + g['args'])) for g in json.load(open(sys.argv[1],encoding='utf-8-sig'))[sys.argv[2]]['generators']]" \
    "$(win "$PACKAGE/forward.json")" "$1" | tr -d '\r' | while IFS=$'\t' read -r main jvm rest; do
    local args=(); IFS=$'\t' read -r -a args <<< "$rest"
    local resolved=(); for x in "${args[@]}"; do x="${x//\$\{repo\}/$(win "$REPO")}"; x="${x//\$\{FCG\}/$(win "${FCG:-}")}"; resolved+=("$x"); done
    [ "$jvm" = "-" ] && jvm=""
    log "generator $main"
    # shellcheck disable=SC2086 (the JVM options are words)
    (cd "$sme" && "$JAVA_HOME/bin/java" -Xss64m $jvm -cp "$cp" "$main" "${resolved[@]}") || return 1
  done || return 1
}

stage() {
  case "$1" in
    gate)
      python -c "import json,sys; g=json.load(open(sys.argv[1],encoding='utf-8-sig')); print('gate', 'PASSED' if g['pass'] else 'NOT PASSED', g['checked']); sys.exit(0 if g['pass'] else 1)" \
        "$(win "$PACKAGE/gate.json")" || { log "the engine's Phase 0 gate has not passed: run $PACKAGE/tools/Test-ForwardGate.ps1"; return 1; } ;;
    legacy)
      [ -f "$PACKAGE/legacy/legacy-mapping.yaml" ] || { log "no legacy mapping: nothing to generate"; return 0; }
      local T="dotnet $REPO/MlCpu/src/MlCpu.Templates/bin/Release/net10.0/MlCpu.Templates.dll"
      local leaks; leaks=$(json leakDictionary)
      $T legacy-model --mapping "$(win "$PACKAGE/legacy/legacy-mapping.yaml")" --out "$(win "$PACKAGE/legacy/legacy-model.json")" \
        --leaks "$(win "$PACKAGE/$leaks")" || return 1
      $T conform --catalog "$(win "$REPO/templates")" --model "$(win "$PACKAGE/legacy/legacy-model.json")" \
        --profile "$(win "$REPO/environments/java-library.yaml")" --bundle C-LEGACY || return 1
      $T render --catalog "$(win "$REPO/templates")" --model "$(win "$PACKAGE/legacy/legacy-model.json")" \
        --profile "$(win "$REPO/environments/java-library.yaml")" --bundle C-LEGACY --out "$(win "$SERVICE/$API_MODULE/src/generated")" --clean || return 1 ;;
    api)
      (cd "$SERVICE" && gradle -q --no-daemon ":$API_MODULE:jar") || return 1
      local jar; jar=$(ls "$SERVICE/$API_MODULE/build/libs/"*.jar | head -1)
      [ -x "$REPO/tools/api-importer/build/install/api-importer/bin/api-importer" ] || (cd "$REPO/tools/api-importer" && gradle -q --no-daemon installDist) || return 1
      "$REPO/tools/api-importer/build/install/api-importer/bin/api-importer" "$API_PACKAGE" "$(win "$PACKAGE/host/api-model.json")" "$(win "$jar")" || return 1
      log "api model: $PACKAGE/host/api-model.json" ;;
    engine)
      bash "$REPO/tools/forward/engine-jars.sh" "$PACKAGE" "${IMPL:?IMPL (an implementation directory) is required}" "$ENGINE_DIR" || return 1 ;;
    prepare)
      local ref; ref=$(bash "$HOST_SCRIPT" paths | sed -n 's/^REFERENCE=//p' | tr -d '\r')
      HOLES="$ref" ENGINE="$ENGINE_DIR" bash "$HOST_SCRIPT" prepare || return 1 ;;
    host-gate)
      bash "$HOST_SCRIPT" gate || return 1 ;;
    pools)
      generators pools || return 1 ;;
    host-scenarios)
      generators host || return 1 ;;
    regression)
      local ref; ref=$(bash "$HOST_SCRIPT" paths | sed -n 's/^REFERENCE=//p' | tr -d '\r')
      OFBIZ="${REGRESSION_OFBIZ:?REGRESSION_OFBIZ (a checkout of its own) is required}" SNAPSHOTS="${REGRESSION_SNAPSHOTS:?}" \
        PORT_OFFSET="${PORT_OFFSET:-10000}" HOLES="${HOLES:-$ref}" ENGINE="$ENGINE_DIR" bash "$HOST_SCRIPT" regression || return 1 ;;
    shadow)
      # G11 in a checkout of its own (SHADOW_OFBIZ, SHADOW_SNAPSHOTS; by default the regression's): the tree prepared with the
      # engine (ENGINE_LABEL) and HOLES (by default the SME's reference holes), then the shadow run (FCG, MLCPU_HOST)
      local ref; ref=$(bash "$HOST_SCRIPT" paths | sed -n 's/^REFERENCE=//p' | tr -d '\r')
      local o="${SHADOW_OFBIZ:-${REGRESSION_OFBIZ:?SHADOW_OFBIZ (a checkout of its own) is required}}"
      local s="${SHADOW_SNAPSHOTS:-${REGRESSION_SNAPSHOTS:?SHADOW_SNAPSHOTS is required}}"
      OFBIZ="$o" SNAPSHOTS="$s" PORT_OFFSET="${PORT_OFFSET:-10000}" HOLES="${HOLES:-$ref}" ENGINE="$ENGINE_DIR" bash "$HOST_SCRIPT" prepare || return 1
      OFBIZ="$o" SNAPSHOTS="$s" PORT_OFFSET="${PORT_OFFSET:-10000}" LABEL="${LABEL:-${ENGINE_LABEL:-reference}}" bash "$HOST_SCRIPT" shadow \
        || return 1 ;;
    run)
      [ -f "${MODEL_RUNNER:-}" ] || { log "MODEL_RUNNER (the eval's run script for the host holes) is required"; return 1; }
      bash "$MODEL_RUNNER" || return 1 ;;
    final)
      local out="$PACKAGE/host/build/final-${LABEL:-model}"
      HOLES="${HOLES:?HOLES (the java directory of a run) is required}" OUT="$out" ENGINE="" SCENARIOS="" bash "$HOST_SCRIPT" judge
      bash "$HOST_SCRIPT" lock-check || return 1
      python -I "$(win "$REPO/tools/forward/host_mutants.py")" junit "$(win "$out/test-results")" "$(win "$PACKAGE/host/final-${LABEL:-model}.json")"
      python -c "import json,sys; j=json.load(open(sys.argv[1])); print(f\"final: {j['tests']} tests, {j['failures']} failures, {j['errors']} errors\"); sys.exit(0 if j['tests'] and not j['failures'] + j['errors'] else 1)" \
        "$(win "$PACKAGE/host/final-${LABEL:-model}.json")" ;;
    *) echo "unknown stage $1 (gate legacy api engine prepare host-gate host-scenarios pools regression shadow run final)"; return 2 ;;
  esac
}

[ $# -gt 0 ] || { sed -n '2,30p' "$0"; exit 2; }
for s in "$@"; do
  log "stage $s"
  stage "$s" || { log "stage $s FAILED"; exit 1; }
done
log "done: $*"
