<#
.SYNOPSIS
  Assembles a model's implementations of every normalized capability into one service and verifies it end to end:
    1. copies the exported single-module project (POST /migrate/estate-java?partition=false) and replaces each
       capability's authored classes (capability-manifest.json "classes") with the model's code from
       <ModelOutput>\<PID>\java;
    2. runs the whole test suite (every program's equivalence scenarios against the faithful translation, the GnuCOBOL
       oracle tests, platform and characterization tests);
    3. runs the CardDemo jobs (POSTTRAN, INTCALC, TRANREPT) through the generated JobEngine with
       --mlcpu.implementation=normalized, so every program step is the model's code, and compares every output with the
       GnuCOBOL oracle (tools\carddemo\Invoke-SpringJobParity.ps1).
  Writes <ModelOutput>\service-verification.json and keeps the assembled project at -Out.
#>
param(
    [string]$ModelOutput,
    [string]$Project,
    [string]$Capabilities,
    [string]$Out
)
$ErrorActionPreference = 'Stop'
if (-not $ModelOutput) { throw '-ModelOutput <model output folder> is required' }
if (-not $Project) { $Project = Join-Path $PSScriptRoot '..\work\export\carddemo-normalized-spring' }
if (-not $Capabilities) { $Capabilities = Join-Path $PSScriptRoot '..\capabilities\carddemo' }
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + [Environment]::GetEnvironmentVariable('Path', 'Machine')
if (-not $env:JAVA_HOME) { $env:JAVA_HOME = [Environment]::GetEnvironmentVariable('JAVA_HOME', 'User') }
$ModelOutput = (Resolve-Path $ModelOutput).Path; $Project = (Resolve-Path $Project).Path; $Capabilities = (Resolve-Path $Capabilities).Path
if (-not $Out) { $Out = Join-Path $PSScriptRoot ("..\work\model-service\" + (Split-Path $ModelOutput -Leaf)) }
$manifest = Get-Content (Join-Path $Capabilities 'capability-manifest.json') -Raw | ConvertFrom-Json

if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
robocopy $Project $Out /E /XD build .gradle /NFL /NDL /NJH /NJS /NP | Out-Null
$Out = (Resolve-Path $Out).Path
$programs = @()
foreach ($cap in $manifest.capabilities) {
    $pkgRel = $cap.package.Replace('.', '\')
    $src = Join-Path $ModelOutput "$($cap.programId)\java\$pkgRel"
    if (-not (Test-Path $src)) { throw "$($cap.programId): no model code at $src" }
    $dst = Join-Path $Out "src\main\java\$pkgRel"
    foreach ($c in $cap.classes) { Remove-Item (Join-Path $dst "$c.java") -Force -ErrorAction SilentlyContinue }
    $files = Get-ChildItem $src -Filter *.java
    $files | ForEach-Object { Copy-Item $_.FullName $dst -Force }
    $programs += [ordered]@{ programId = $cap.programId; package = $cap.package; files = @($files.Name); lines = ($files | ForEach-Object { (Get-Content $_.FullName).Count } | Measure-Object -Sum).Sum }
    Write-Host "$($cap.programId): $($files.Count) model files -> $($cap.package)"
}

Write-Host "== full test suite"
Push-Location $Out
try { cmd /c "gradle test --offline --no-daemon --continue --console=plain 2>&1" | Out-Null } finally { Pop-Location }
$suites = foreach ($x in Get-ChildItem (Join-Path $Out 'build\test-results\test') -Filter 'TEST-*.xml') {
    $s = ([xml](Get-Content $x.FullName -Raw)).testsuite
    [ordered]@{ suite = $s.name; tests = [int]$s.tests; failed = [int]$s.failures + [int]$s.errors; skipped = [int]$s.skipped }
}
$total = ($suites | ForEach-Object { $_.tests } | Measure-Object -Sum).Sum
$failed = ($suites | ForEach-Object { $_.failed } | Measure-Object -Sum).Sum
$suites | Where-Object { $_.suite -match 'Equivalence|Platform|BatchFailure' } | ForEach-Object { Write-Host ("   {0,-90} {1,3} tests, {2} failed" -f $_.suite, $_.tests, $_.failed) }
Write-Host "   all suites: $total tests, $failed failed"

Write-Host "== CardDemo jobs with the model's code (normalized) vs the GnuCOBOL oracle"
$report = Join-Path $env:TEMP "mlcpu-model-service-parity.json"
if (Test-Path $report) { Remove-Item $report }
& (Join-Path $PSScriptRoot 'carddemo\Invoke-SpringJobParity.ps1') -Estate $Out -Implementation normalized -ParityReport $report | Out-Host
$parity = if (Test-Path $report) { Get-Content $report -Raw | ConvertFrom-Json } else { $null }
$all = @($parity.outputs)
$rows = @($all | Where-Object { $_.Output -notlike '*(wall clock)' })
$clock = @($all | Where-Object { $_.Output -like '*(wall clock)' })
$match = @($rows | Where-Object { $_.Verdict -eq 'MATCH' }).Count
$byModel = @($rows | Where-Object { $_.Normalized }).Count

[ordered]@{
    schema = 'mlcpu.model-service-verification.v1'; model = Split-Path $ModelOutput -Leaf; verifiedAt = (Get-Date).ToString('o'); assembled = $Out
    programs = $programs
    tests = [ordered]@{ total = $total; failed = $failed; suites = @($suites) }
    jobParity = [ordered]@{ implementation = 'normalized'; outputs = $rows.Count; match = $match; producedByModelSteps = $byModel; results = $rows
                            wallClock = @($clock) }
} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $ModelOutput 'service-verification.json') -Encoding UTF8
Write-Host "tests: $($total - $failed)/$total passed; job outputs: $match/$($rows.Count) MATCH ($byModel from the model's steps); wall clock: $(($clock | ForEach-Object { $_.Verdict }) -join ', ')"
