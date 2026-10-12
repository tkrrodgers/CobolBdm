#!/bin/bash
# run-fineract.sh [PID...] : the capability kit's equivalence-probe steps on the Fineract estate, outside a publish
# (docs/EQUIVALENCE-PROBE.md): the same inputs, traces and arguments as CapabilityKitBuilder, so the statements are the
# ones a publish writes.
#
# Reads the main tree's project fineract-cobol (read-only): oracle step inputs, the published estate (PUBV, default
# publish-v4), the COBOL sources. Writes only under work/equivalence-probe/$PUBV[-transpile]:
#   estate/                    the published faithful classes (com.mlcpu.core + capability legacy), compiled against this
#                              worktree's runtime (CLASSES=transpile: the project's transpile/classes, which the kit uses)
#   inputs/<PID>/              record-layouts.json and conditions.json (kit-harness inputs: RecordKitBuilder,
#                              EquivalenceProbeInputs.ConditionNames)
#   traces/<PID>/<scenario>/   CallTrace of FNSCHGEN.STEP20 over base and the acceptance scenarios, --max 40 and
#                              --probe-max 1000 (EquivalenceProbeInputs.ProbeTraceCalls)
#   out/<PID>/                 equivalences.json, equivalences.md, summary.txt
#   out/<CALLER>/              callee-contracts.md (kit-harness contracts: CalleeContracts.Build)
set -e
W=/c/Users/Tomrodgers/Source/CobolBdm-burndown
PUBV=${PUBV:-publish-v4}
P=C:/Users/Tomrodgers/Source/CobolBdm/projects/fineract-cobol
PUB=$P/$PUBV/fineract-cobol-normalized-spring
RT=$(cygpath -m $W/java-sidecar/build/classes/java/main)
export MLCPU_DISPLAY_NUMERIC=gnucobol MLCPU_BINARY_TRUNCATE=no MLCPU_DEFAULT_BYTE=0
PIDS=${@:-FNDKRND FNDKDFO FNDTKRN FNMY0100}
if [ "$CLASSES" = "transpile" ]; then D=$(cygpath -m $W/work/equivalence-probe/$PUBV-transpile); else D=$(cygpath -m $W/work/equivalence-probe/$PUBV); fi
mkdir -p $D

# 0. the kit harness (the kit's own C# code for the probe's inputs and the callee contracts)
H=$D/kit-harness
mkdir -p $H
cp $W/docs/equivalence-probe/kit-harness/Program.cs $W/docs/equivalence-probe/kit-harness/kit-harness.csproj $H/
dotnet build $H/kit-harness.csproj -v q -nologo -p:KnowledgeProject=$(cygpath -m $W/MlCpu/src/MlCpu.CobolKb.Knowledge/MlCpu.CobolKb.Knowledge.csproj) \
  | grep -E "error|Build succeeded" || true
HARNESS="dotnet $H/bin/Debug/net10.0/kit-harness.dll"

# 1. the faithful classes
if [ "$CLASSES" = "transpile" ]; then
  CLS=$P/transpile/classes
else
  CLS=$D/estate
  if [ ! -d $D/estate/com/mlcpu/core ]; then
    mkdir -p $D/estate
    (cd $PUB/src/main/java && find com/mlcpu/core com/mlcpu/capabilities -path '*legacy*' -name '*.java' -o -path 'com/mlcpu/core/*' -name '*.java' | sort -u > $D/estate-src.txt)
    (cd $PUB/src/main/java && javac -nowarn -encoding UTF-8 -d $D/estate -cp "$RT" @$D/estate-src.txt)
  fi
fi

# 2. the probe's inputs, as the kit writes them
$HARNESS inputs $D/inputs $P/source/app/cbl/FNLS0100.cbl $(for p in $PIDS; do echo $P/source/app/cbl/$p.cbl; done)

# 3. CALL traces over base and the acceptance scenarios (project.json publish.acceptanceScenarios), as the kit runs them
SCEN=$(python -c "import json;p=json.load(open('$P/project.json'));print(' '.join(['base']+p['publish']['acceptanceScenarios']))")
trace1() {
  local pid=$1 sc=$2 t=$D/traces/$1/$2
  mkdir -p $t
  local in=$P/oracle/runs/$sc/steps/FNSCHGEN.STEP20/in
  cp $in/FINERACT.RUN.CONTROL $t/RUNCTL.dat; cp $in/FINERACT.LOAN.REQUEST.DATA $t/LOANREQ.dat; cp $in/FINERACT.CURRENCY.VSAM.KSDS $t/CURRFILE.dat
  (cd $t && timeout 300 java -cp "$RT;$CLS" com.mlcpu.rt.CallTrace --program FNLS0100 --trace $pid --clock "FIXED=2022-07-05T10:11:12-07:00" \
     --out trace.json --max 40 --probe-out probe-trace.json --probe-max 1000 \
     --dd RUNCTL=RUNCTL.dat --dd LOANREQ=LOANREQ.dat --dd CURRFILE=CURRFILE.dat --dd SCHEDOUT=SCHEDOUT.dat --dd REJECTS=REJECTS.dat > log.txt 2>&1)
  rm -f $t/RUNCTL.dat $t/LOANREQ.dat $t/CURRFILE.dat $t/SCHEDOUT.dat $t/REJECTS.dat
}
export -f trace1; export D P RT CLS
for pid in $PIDS; do
  if [ ! -d $D/traces/$pid ]; then
    echo $SCEN | tr ' ' '\n' | xargs -P 8 -I{} bash -c "trace1 $pid {}"
  fi
done

# 4. the probe, with the kit's arguments (EquivalenceProbeInputs.Arguments)
for pid in $PIDS; do
  mkdir -p $D/out/$pid
  java -Xmx2g -cp "$RT;$CLS" com.mlcpu.rt.EquivalenceProbe --program $pid --layouts $D/inputs/$pid/record-layouts.json \
    --conditions $D/inputs/$pid/conditions.json --traces $D/traces/$pid --generate 100000 --max-seconds 900 --out $D/out/$pid \
    2> $D/out/$pid/progress.txt | tee $D/out/$pid/summary.txt
done

# 5. callee contracts: every program of the estate that CALLs one of them directly
$HARNESS contracts $D/out $D/out $(ls $P/source/app/cbl/*.cbl | xargs -n1 cygpath -m)
