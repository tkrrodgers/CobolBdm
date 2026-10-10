<#
.SYNOPSIS
  Collects promotion evidence for the normalized capabilities in an exported single-module project
  (POST /migrate/estate-java?partition=false) and writes capabilities/<estate>/evidence/<PID>.evidence.json.

  Evidence per program, pinned to the requirements and source hashes recorded in the project's normalization/ledger.json:
    - equivalence: every *EquivalenceTest scenario (normalized vs faithful program, byte for byte, all datasets + SYSOUT)
    - oracleParity: the program-level GnuCOBOL oracle test plus the end-to-end job parity run with --mlcpu.implementation=normalized
    - coverage: JaCoCo line/branch over the normalized classes (shared com.mlcpu.carddemo.* plus the capability package,
      excluding the faithful legacy classes and generated workflows)
    - mutation: PIT over the same classes; score = detected / generated; surviving mutants are listed for SME review
#>
param(
    [string]$Project = (Join-Path $PSScriptRoot '..\work\export\carddemo-normalized-spring'),
    [string]$Capabilities = (Join-Path $PSScriptRoot '..\capabilities\carddemo'),
    [switch]$SkipMutation,
    [switch]$SkipJobParity
)
$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + [Environment]::GetEnvironmentVariable('Path', 'Machine')
if (-not $env:JAVA_HOME) { $env:JAVA_HOME = [Environment]::GetEnvironmentVariable('JAVA_HOME', 'User') }
$Project = (Resolve-Path $Project).Path; $Capabilities = (Resolve-Path $Capabilities).Path
$manifest = Get-Content (Join-Path $Capabilities 'capability-manifest.json') -Raw | ConvertFrom-Json
$ledger = Get-Content (Join-Path $Project 'normalization\ledger.json') -Raw | ConvertFrom-Json
$shared = @($manifest.sharedPackages | ForEach-Object { $_.Replace('.', '/') })

function Invoke-Gradle([string]$tasks) {
    Push-Location $Project
    try { $out = cmd /c "gradle $tasks --console=plain --continue 2>&1"; $rc = $LASTEXITCODE } finally { Pop-Location }
    return [pscustomobject]@{ Rc = $rc; Out = $out }
}

Write-Host "== tests + coverage ($Project)"
$t = Invoke-Gradle 'clean test jacocoTestReport'
if ($t.Rc -ne 0) { $t.Out | Select-Object -Last 40 | ForEach-Object { "   $_" } }
$cases = foreach ($f in Get-ChildItem (Join-Path $Project 'build\test-results\test') -Filter 'TEST-*.xml') {
    $x = [xml](Get-Content $f.FullName -Raw)
    foreach ($c in $x.testsuite.testcase) {
        [pscustomobject]@{ Class = $c.classname; Name = $c.name; Failed = [bool]($c.failure -or $c.error); Message = "$($c.failure.message)$($c.error.message)" }
    }
}
$jacoco = [xml](Get-Content (Join-Path $Project 'build\reports\jacoco\test\jacocoTestReport.xml') -Raw)

$mutations = @()
if (-not $SkipMutation) {
    Write-Host "== mutation testing (PIT)"
    $m = Invoke-Gradle 'pitest'
    if ($m.Rc -ne 0) { $m.Out | Select-Object -Last 40 | ForEach-Object { "   $_" } }
    $px = [xml](Get-Content (Join-Path $Project 'build\reports\pitest\mutations.xml') -Raw)
    $mutations = foreach ($u in $px.mutations.mutation) {
        [pscustomobject]@{ Class = $u.mutatedClass; Method = $u.mutatedMethod; Line = [int]$u.lineNumber; Mutator = ($u.mutator -split '\.')[-1]
                           Status = $u.status; Detected = $u.detected -eq 'true'; Description = $u.description }
    }
}

$parity = $null
if (-not $SkipJobParity) {
    Write-Host "== job parity with the normalized implementation"
    $parityFile = Join-Path $env:TEMP 'mlcpu-normalized-parity.json'
    if (Test-Path $parityFile) { Remove-Item $parityFile }
    & (Join-Path $PSScriptRoot 'carddemo\Invoke-SpringJobParity.ps1') -Estate $Project -Implementation normalized -ParityReport $parityFile | Out-Host
    $parity = Get-Content $parityFile -Raw | ConvertFrom-Json
}

function Test-Normalized([string]$cls, [string]$pkg) {
    # cls: JVM name with / or . separators; normalized = shared packages or the capability package, minus legacy and workflow classes.
    $n = $cls.Replace('.', '/')
    if ($n -match '/legacy/' -or $n -match 'Workflow') { return $false }
    foreach ($s in $shared + @($pkg)) { if ($n.StartsWith("$s/")) { return $true } }
    return $false
}

$outDir = New-Item -ItemType Directory -Force (Join-Path $Capabilities 'evidence')
foreach ($cap in $manifest.capabilities) {
    $pid_ = $cap.programId
    $facts = $ledger.facts.capabilities | Where-Object programId -eq $pid_
    if (-not $facts) { Write-Host "   $pid_ not in this project; skipped"; continue }
    $pkg = $cap.package.Replace('.', '/')
    $mine = @($cases | Where-Object { $_.Class.StartsWith($cap.package + '.') })
    $eq = @($mine | Where-Object { $_.Class -like '*EquivalenceTest' -and $_.Name -notlike 'normalizedEqualsGnuCobolOracle*' })
    $oracle = @($mine | Where-Object { $_.Name -like 'normalizedEqualsGnuCobolOracle*' })
    $platform = @($cases | Where-Object { $_.Class.StartsWith('com.mlcpu.carddemo.') })

    $lines = @{ c = 0; m = 0 }; $branches = @{ c = 0; m = 0 }; $classes = @()
    foreach ($p in $jacoco.report.package) {
        foreach ($c in $p.class) {
            if (-not (Test-Normalized $c.name $pkg)) { continue }
            $classes += $c.name
            foreach ($k in $c.counter) {
                if ($k.type -eq 'LINE') { $lines.c += [int]$k.covered; $lines.m += [int]$k.missed }
                if ($k.type -eq 'BRANCH') { $branches.c += [int]$k.covered; $branches.m += [int]$k.missed }
            }
        }
    }
    $mm = @($mutations | Where-Object { Test-Normalized $_.Class $pkg })
    $detected = @($mm | Where-Object Detected).Count
    $jobRows = if ($parity) { @($parity.outputs | Where-Object Job -eq $cap.job) } else { @() }
    $jobOk = $parity -and $jobRows.Count -gt 0 -and -not ($jobRows | Where-Object { $_.Verdict -notin 'MATCH', 'EQUIVALENT_EXCEPT_VOLATILE' }) -and
             -not ($jobRows | Where-Object { $_.Normalized -eq $false })
    $oracleOk = $oracle.Count -gt 0 -and -not ($oracle | Where-Object Failed)
    $evidence = [ordered]@{
        schema = 'mlcpu.capability-evidence.v1'
        programId = $pid_
        requirementsHash = $facts.requirementsHash
        sourceHash = $facts.sourceHash
        collectedAt = (Get-Date).ToString('o')
        project = Split-Path $Project -Leaf
        tests = [ordered]@{ capability = $mine.Count; platform = $platform.Count; failed = @($mine + $platform | Where-Object Failed).Count
                            failures = @($mine + $platform | Where-Object Failed | ForEach-Object { "$($_.Class).$($_.Name): $($_.Message)" }) }
        equivalence = [ordered]@{ scenarios = $eq.Count; failed = @($eq | Where-Object Failed).Count; names = @($eq | ForEach-Object Name) }
        oracleParity = [ordered]@{
            verdict = $(if ($oracleOk -and ($SkipJobParity -or $jobOk)) { 'MATCH' } elseif (-not $oracleOk) { 'PROGRAM_ORACLE_FAILED' } else { 'JOB_PARITY_FAILED' })
            programOracleTest = $(if ($oracleOk) { 'PASS' } else { 'FAIL' })
            jobParity = $(if ($SkipJobParity) { 'skipped' } else { @($jobRows | ForEach-Object { [ordered]@{ output = $_.Output; verdict = $_.Verdict; normalized = $_.Normalized } }) })
        }
        coverage = [ordered]@{
            line = $(if ($lines.c + $lines.m) { [math]::Round($lines.c / ($lines.c + $lines.m), 4) } else { $null })
            branch = $(if ($branches.c + $branches.m) { [math]::Round($branches.c / ($branches.c + $branches.m), 4) } else { $null })
            lines = [ordered]@{ covered = $lines.c; missed = $lines.m }; branches = [ordered]@{ covered = $branches.c; missed = $branches.m }
            classes = $classes.Count
        }
        mutation = $(if ($SkipMutation) { $null } else { [ordered]@{
            score = $(if ($mm.Count) { [math]::Round($detected / $mm.Count, 4) } else { $null })
            generated = $mm.Count; detected = $detected
            byStatus = [ordered]@{} + ($mm | Group-Object Status | ForEach-Object -Begin { $h = [ordered]@{} } -Process { $h[$_.Name] = $_.Count } -End { $h })
            survivors = @($mm | Where-Object { -not $_.Detected } | Sort-Object Class, Line | ForEach-Object {
                [ordered]@{ class = $_.Class; method = $_.Method; line = $_.Line; mutator = $_.Mutator; status = $_.Status; description = $_.Description } })
        } })
    }
    $path = Join-Path $outDir "$pid_.evidence.json"
    $evidence | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $path
    Write-Host ("   {0}: equivalence {1}/{2}, oracle {3}, line {4:P1}, branch {5:P1}, mutation {6} -> {7}" -f $pid_,
        ($eq.Count - $evidence.equivalence.failed), $eq.Count, $evidence.oracleParity.verdict, $evidence.coverage.line, $evidence.coverage.branch,
        $(if ($evidence.mutation) { '{0:P1} ({1}/{2})' -f $evidence.mutation.score, $detected, $mm.Count } else { 'skipped' }), $path)
}
