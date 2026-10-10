<#
.SYNOPSIS
  Runs the exported <job>-spring projects (POSTTRAN, INTCALC, TRANREPT) end to end through the generated JobEngine
  (harvested JCL, PROCs expanded, IDCAMS/SORT steps included) and compares every output byte-for-byte with the
  GnuCOBOL oracle outputs produced by Invoke-CardDemoParity.ps1 (run that first; it supplies work\parity).
  Each job's inputs are the oracle's state before that step, staged as files named by DSN.
#>
param(
    [string]$Export = (Join-Path $PSScriptRoot '..\..\work\export'),
    [string]$Parity = (Join-Path $PSScriptRoot '..\..\work\parity'),
    # Must equal the epoch Invoke-CardDemoParity.ps1 pinned for the oracle (COB_CURRENT_DATE).
    [string]$Epoch = '2022-07-05T10:11:12-07:00',
    # An estate project (POST /migrate/estate-java) to run every job from, instead of the per-job <job>-spring projects.
    [string]$Estate,
    # legacy | normalized: overrides the projects' per-program default (mlcpu.implementation); empty = the project default.
    [string]$Implementation,
    # Writes the per-output verdicts as JSON (used by tools/Collect-CapabilityEvidence.ps1).
    [string]$ParityReport
)
$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + [Environment]::GetEnvironmentVariable('Path', 'Machine')
if (-not $env:JAVA_HOME) { $env:JAVA_HOME = [Environment]::GetEnvironmentVariable('JAVA_HOME', 'User') }
$Export = (Resolve-Path $Export).Path; $Parity = (Resolve-Path $Parity).Path
$C = Join-Path $Parity 'common'; $O = Join-Path $Parity 'oracle\sysout'
$Q = 'AWS.M2.CARDDEMO.'
$impl = if ($Implementation) { " --mlcpu.implementation=$Implementation" } else { '' }

$Jobs = @(
    @{ Job = 'POSTTRAN'
       In = [ordered]@{ 'DALYTRAN.PS' = "$C\DALYTRAN.dat"; 'TRANSACT.VSAM.KSDS' = "$C\TRANSACT.ksds"; 'CARDXREF.VSAM.KSDS' = "$C\CARDXREF.ksds"
                        'ACCTDATA.VSAM.KSDS' = "$C\ACCTDATA.ksds"; 'TCATBALF.VSAM.KSDS' = "$C\TCATBAL.ksds" }
       Out = [ordered]@{ 'data:TRANSACT.VSAM.KSDS' = 'S5-POSTTRAN.TRANSACT.ksds'; 'data:DALYREJS' = 'S5-POSTTRAN.DALYREJS.dat'
                         'data:ACCTDATA.VSAM.KSDS' = 'S5-POSTTRAN.ACCTDATA.ksds'; 'data:TCATBALF.VSAM.KSDS' = 'S5-POSTTRAN.TCATBAL.ksds'
                         'sysout:STEP15.SYSOUT.txt' = 'S5-POSTTRAN.txt' } },
    @{ Job = 'INTCALC'
       In = [ordered]@{ 'TCATBALF.VSAM.KSDS' = "$O\S5-POSTTRAN.TCATBAL.ksds"; 'CARDXREF.VSAM.KSDS' = "$C\CARDXREF.ksds"
                        'ACCTDATA.VSAM.KSDS' = "$O\S5-POSTTRAN.ACCTDATA.ksds"; 'DISCGRP.VSAM.KSDS' = "$C\DISCGRP.ksds" }
       Out = [ordered]@{ 'data:SYSTRAN' = 'S6-INTCALC.SYSTRAN.dat'; 'data:ACCTDATA.VSAM.KSDS' = 'S6-INTCALC.ACCTDATA.ksds'
                         'sysout:STEP15.SYSOUT.txt' = 'S6-INTCALC.txt' } },
    @{ Job = 'TRANREPT'
       In = [ordered]@{ 'TRANSACT.VSAM.KSDS' = "$O\S7-COMBTRAN.TRANSACT.ksds"; 'CARDXREF.VSAM.KSDS' = "$C\CARDXREF.ksds"
                        'TRANTYPE.VSAM.KSDS' = "$C\TRANTYPE.ksds"; 'TRANCATG.VSAM.KSDS' = "$C\TRANCATG.ksds"; 'DATEPARM' = "$C\DATEPARM.dat" }
       Out = [ordered]@{ 'data:TRANSACT.DALY' = 'S8-TRANREPT.TRANDALY.dat'; 'data:TRANREPT' = 'S8-TRANREPT.TRANREPT.dat'
                         'sysout:STEP10R.SYSOUT.txt' = 'S8-TRANREPT.txt' } }
)

$rows = @()
foreach ($j in $Jobs) {
    $proj = if ($Estate) { (Resolve-Path $Estate).Path } else { Join-Path $Export "$($j.Job.ToLowerInvariant())-spring" }
    if (-not (Test-Path $proj)) { throw "missing $proj - export it with POST /migrate/job-java/$($j.Job)?exportDir=$Export" }
    $task = if (Test-Path (Join-Path $proj 'estate-app')) { ':estate-app:run' } else { 'run' }
    $run = Join-Path $Parity "spring\$($j.Job)"
    if (Test-Path $run) { Remove-Item $run -Recurse -Force }
    $data = New-Item -ItemType Directory (Join-Path $run 'data') -Force; $sys = Join-Path $run 'sysout'
    foreach ($e in $j.In.GetEnumerator()) { Copy-Item $e.Value (Join-Path $data "$Q$($e.Key)") }
    Push-Location $proj
    try {
        $env:MLCPU_DEFAULT_BYTE = '0'
        $log = cmd /c "gradle $task --no-daemon --console=plain -q `"--args=--mlcpu.job=$($j.Job) --mlcpu.data-dir=$data --mlcpu.sysout-dir=$sys --mlcpu.clock=FIXED=$Epoch --mlcpu.display-numeric=gnucobol --mlcpu.binary-truncate=no$impl`" 2>&1"
        $rc = $LASTEXITCODE
    } finally { Pop-Location; Remove-Item env:MLCPU_DEFAULT_BYTE -ErrorAction SilentlyContinue }
    $log | Set-Content (Join-Path $run 'job.log')
    Write-Host "== $($j.Job) rc=$rc"; $log | Where-Object { $_ -match '^(STEP|JOB|MAXCC|.*rc=)' } | ForEach-Object { "   $_" }
    $engine = ($log | Where-Object { $_ -match ' rc=\d+ ' }) -join "`n"
    foreach ($e in $j.Out.GetEnumerator()) {
        $kind, $name = $e.Key -split ':', 2
        $actual = if ($kind -eq 'data') { Join-Path $data "$Q$name" } else { Join-Path $sys $name }
        $expected = Join-Path $O $e.Value
        $verdict = if (-not (Test-Path $actual)) { 'MISSING' } else {
            $a = [IO.File]::ReadAllBytes($actual); $b = [IO.File]::ReadAllBytes($expected)
            if ($kind -eq 'sysout') {
                # GnuCOBOL on Windows terminates DISPLAY lines with CRLF; the JVM writes LF. Compare lines.
                $L1 = [Text.Encoding]::GetEncoding(28591)
                $a = $L1.GetBytes($L1.GetString($a).Replace("`r`n", "`n")); $b = $L1.GetBytes($L1.GetString($b).Replace("`r`n", "`n"))
            }
            if ($a.Length -eq $b.Length -and [Linq.Enumerable]::SequenceEqual($a, $b)) { 'MATCH' } else {
                $i = 0; $n = [Math]::Min($a.Length, $b.Length); while ($i -lt $n -and $a[$i] -eq $b[$i]) { $i++ }
                "DIFF at byte $i (java $($a.Length) / oracle $($b.Length) bytes)"
            }
        }
        $rows += [pscustomobject]@{ Job = $j.Job; Output = $name; Oracle = $e.Value; Verdict = $verdict; Normalized = ($engine -match 'normalized') }
    }
}
$rows | Format-Table -AutoSize | Out-String -Width 200
$bad = @($rows | Where-Object Verdict -ne 'MATCH').Count
Write-Host ("{0}/{1} MATCH" -f ($rows.Count - $bad), $rows.Count)

# Wall-clock reruns compared field by field through replay/ReplayPlan.json: only clock-derived (volatile) fields may differ.
$vol = @(
    @{ Job = 'POSTTRAN'; Step = 'STEP15'; Dd = 'TRANFILE'; Out = 'TRANSACT.VSAM.KSDS'; Oracle = 'S5-POSTTRAN.TRANSACT.ksds' },
    @{ Job = 'INTCALC'; Step = 'STEP15'; Dd = 'TRANSACT'; Out = 'SYSTRAN'; Oracle = 'S6-INTCALC.SYSTRAN.dat' }
)
foreach ($v in $vol) {
    $proj = if ($Estate) { (Resolve-Path $Estate).Path } else { Join-Path $Export "$($v.Job.ToLowerInvariant())-spring" }
    $plan = @("replay\ReplayPlan-$($v.Job).json", 'replay\ReplayPlan.json') | ForEach-Object { Join-Path $proj $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
    $classes = @('estate-common\build\classes\java\main', 'build\classes\java\main') | ForEach-Object { Join-Path $proj $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
    $task = if (Test-Path (Join-Path $proj 'estate-app')) { ':estate-app:run' } else { 'run' }
    $src = Join-Path $Parity "spring\$($v.Job)"
    $run = Join-Path $Parity "spring-wallclock\$($v.Job)"
    if (Test-Path $run) { Remove-Item $run -Recurse -Force }
    $data = New-Item -ItemType Directory (Join-Path $run 'data') -Force
    $j = $Jobs | Where-Object Job -eq $v.Job
    foreach ($e in $j.In.GetEnumerator()) { Copy-Item $e.Value (Join-Path $data "$Q$($e.Key)") }
    Push-Location $proj
    try {
        $env:MLCPU_DEFAULT_BYTE = '0'
        cmd /c "gradle $task --no-daemon --console=plain -q `"--args=--mlcpu.job=$($v.Job) --mlcpu.data-dir=$data --mlcpu.sysout-dir=$run\sysout --mlcpu.display-numeric=gnucobol --mlcpu.binary-truncate=no$impl`" 2>&1" | Out-Null
    } finally { Pop-Location; Remove-Item env:MLCPU_DEFAULT_BYTE -ErrorAction SilentlyContinue }
    $report = Join-Path $run "comparison-$($v.Dd).json"
    & (Join-Path $env:JAVA_HOME 'bin\java.exe') -cp $classes com.mlcpu.rt.RecordCompare --plan $plan --step $v.Step --dd $v.Dd `
        --expected (Join-Path $O $v.Oracle) --actual (Join-Path $data "$Q$($v.Out)") --out $report
    $c = Get-Content $report -Raw | ConvertFrom-Json
    Write-Host ("   {0} {1} wall clock: {2}; volatile fields differing: {3}" -f $v.Job, $v.Dd, $c.verdict, (($c.fields | Where-Object volatile | ForEach-Object field) -join ', '))
    if ($c.verdict -ne 'EQUIVALENT_EXCEPT_VOLATILE') { $bad++ }
    $rows += [pscustomobject]@{ Job = $v.Job; Output = "$($v.Out) (wall clock)"; Oracle = $v.Oracle; Verdict = $c.verdict; Normalized = $null }
}
if ($ParityReport) {
    [ordered]@{ schema = 'mlcpu.job-parity.v1'; implementation = $(if ($Implementation) { $Implementation } else { 'default' }); project = $Estate
                epoch = $Epoch; mismatches = $bad; outputs = $rows } | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $ParityReport
}
exit $bad
