#!/bin/bash
# Parses every program of the reference estates with the COBOL-85 grammar (MlCpu.JavaBridge --ast) and reports
# clean/total per estate (docs/AST-GRAMMAR.md). The four onboarded projects are read through their project.json
# (program list and copybook roots, as the host's graph stage); the J3S estate is a directory.
# Usage: tools/ast/ast-estates.sh [out-dir] [extra --ast options, e.g. --timeout 3600]
#   MLCPU_PROJECTS  onboarded projects (default: the main tree's projects/, read only)
#   MLCPU_J3S       the J3S generated programs (default: JavaToCOBOLTest fineract-cobol-gen/build/j3s/cbl); its copybooks
#                   are build/j3s/cpy plus the generator's cobol/cpy and kernel/cpy (as j3s-build.sh compiles them)
#   ESTATES         subset, e.g. "carddemo fineract-legacy j3s"
#   BRIDGE          the MlCpu.JavaBridge executable (default: its Debug build)
set -u
W=$(cd "$(dirname "$0")/../.." && pwd)
EXE=${BRIDGE:-$W/MlCpu/src/MlCpu.JavaBridge/bin/Debug/net10.0/MlCpu.JavaBridge.exe}
P=${MLCPU_PROJECTS:-/c/Users/Tomrodgers/Source/CobolBdm/projects}
J3S=${MLCPU_J3S:-/c/Users/Tomrodgers/JavaToCOBOLTest/fineract-cobol-gen/build/j3s/cbl}
OUT=${1:-$W/work/ast/$(date +%Y%m%d-%H%M%S)}
shift $(( $# > 0 ? 1 : 0 ))
ESTATES=${ESTATES:-"carddemo zopeneditor-sample fineract-cobol fineract-legacy j3s"}
mkdir -p "$OUT"
for e in $ESTATES; do
  if [ "$e" = j3s ]; then
    src=$J3S; gen=$(cd "$J3S/../../.." && pwd)
    export MLCPU_COPYBOOKS="$(cygpath -w "$gen/cobol/cpy");$(cygpath -w "$gen/kernel/cpy")"
  else src=$P/$e/project.json; unset MLCPU_COPYBOOKS; fi
  "$EXE" --ast "$src" --out "$OUT/$e" --errors 5 "$@" > "$OUT/$e.log" 2>&1
  printf '%-20s %s\n' "$e" "$(tail -1 "$OUT/$e.log")"
done | tee "$OUT/summary.txt"
