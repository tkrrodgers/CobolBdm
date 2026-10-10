<#
.SYNOPSIS
  Runs local models through the normalization pipeline: for each model and program, the model designs the capability from
  normalization/<PID>/requirements.json, then implements its design in Java on the platform layer only. Each
  implementation is compiled and judged by the program's equivalence test against the faithful COBOL translation.

  Output per model: <OutRoot>\<label>\<PID>\ design.json (+ attempts, run record), java\ (the model's code),
  implementation.run.json, acceptance-tests.xml; <OutRoot>\<label>\summary.json and run.log; <OutRoot>\README.md compares models.

  Backend: llama.cpp (llama-server from -LlamaDir), falling back to LM-Kit when the server cannot start or a request fails.
  Needs the exported single-module project (POST /migrate/estate-java?partition=false) for the harness.
#>
param(
    [string[]]$Models = @(
        'gpt-oss-120b=C:\models\gpt-oss-120b-GGUF\gpt-oss-120b-MXFP4.gguf',
        'Llama-4-Maverick-17B-128E-Instruct=C:\models\Llama-4-Maverick-GGUF\UD-Q4_K_XL\Llama-4-Maverick-17B-128E-Instruct-UD-Q4_K_XL-00001-of-00005.gguf'),
    [string]$Programs = 'CBTRN02C,CBACT04C,CBTRN03C',
    [string]$OutRoot = 'C:\Users\Tomrodgers\Source\modelDesignsAndCodeOutput',
    [string]$Project = (Join-Path $PSScriptRoot '..\work\export\carddemo-normalized-spring'),
    [string]$Capabilities = (Join-Path $PSScriptRoot '..\capabilities\carddemo'),
    [string]$LlamaDir = 'C:\models\llama.cpp',
    [ValidateSet('llama', 'lmkit')][string]$Backend = 'llama',
    [int]$DesignAttempts = 3,
    [int]$ImplementationAttempts = 10,
    [string]$ExtraArgs = ''
)
$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + [Environment]::GetEnvironmentVariable('Path', 'Machine')
if (-not $env:JAVA_HOME) { $env:JAVA_HOME = [Environment]::GetEnvironmentVariable('JAVA_HOME', 'User') }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Project = (Resolve-Path $Project).Path; $Capabilities = (Resolve-Path $Capabilities).Path
New-Item -ItemType Directory -Force $OutRoot | Out-Null

dotnet build (Join-Path $repo 'MlCpu\src\MlCpu.DesignModel') -c Release -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'MlCpu.DesignModel does not build' }
$dll = Join-Path $repo 'MlCpu\src\MlCpu.DesignModel\bin\Release\net10.0\MlCpu.DesignModel.dll'

foreach ($m in $Models) {
    $label, $path = $m -split '=', 2
    if (-not (Test-Path $path)) { Write-Warning "$label`: model not found at $path; skipped"; continue }
    $out = Join-Path $OutRoot $label
    New-Item -ItemType Directory -Force $out | Out-Null
    Write-Host "===== $label ($path)"
    $args_ = @($dll, 'run', '--model', $path, '--name', $label, '--programs', $Programs,
        '--normalization', (Join-Path $Capabilities 'normalization'), '--manifest', (Join-Path $Capabilities 'capability-manifest.json'),
        '--project', $Project, '--out', $out, '--harness-root', (Join-Path $repo "work\model-harness\$label"),
        '--llama-dir', $LlamaDir, '--backend', $Backend, '--attempts', $DesignAttempts, '--impl-attempts', $ImplementationAttempts)
    if ($ExtraArgs) { $args_ += ($ExtraArgs -split ' ') }
    & dotnet @args_ 2>&1 | Tee-Object -FilePath (Join-Path $out 'run.log') -Append
}

# Comparison across every model folder that has a summary.
$rows = foreach ($s in Get-ChildItem $OutRoot -Directory | ForEach-Object { Join-Path $_.FullName 'summary.json' } | Where-Object { Test-Path $_ }) {
    $j = Get-Content $s -Raw | ConvertFrom-Json
    foreach ($p in ($j.programs | Sort-Object programId)) {
        $tests = if ($p.designValid) { if ($p.compiled) { "$($p.testsPassed)/$($p.testsTotal)" } else { 'does not compile' } } else { '-' }
        $diffs = if ($p.compiled) { $p.outputDifferences } else { '-' }
        $bestOf = if ($p.compiled) { "$($p.bestAttempt) of $($p.implementationAttempts)" } elseif ($p.designValid) { "none of $($p.implementationAttempts) compiled" } else { '-' }
        "| $($j.model) | $($p.programId) | $(if ($p.designValid) { 'valid' } else { 'invalid' }) | $tests | $diffs | $bestOf | $(if ($p.firstAttemptPassed) { 'yes' } else { 'no' }) | $($p.minutes) | $($j.backend) |"
    }
}
$services = foreach ($v in Get-ChildItem $OutRoot -Directory | ForEach-Object { Join-Path $_.FullName 'service-verification.json' } | Where-Object { Test-Path $_ }) {
    $s = Get-Content $v -Raw | ConvertFrom-Json
    "| $($s.model) | $(($s.programs | ForEach-Object { $_.programId }) -join ', ') | $($s.tests.total - $s.tests.failed)/$($s.tests.total) | $($s.jobParity.match)/$($s.jobParity.outputs) ($($s.jobParity.producedByModelSteps) from the model's steps) | $(($s.jobParity.wallClock | ForEach-Object { $_.Verdict }) -join ', ') | $($s.verifiedAt.Substring(0, 10)) |"
}
$serviceSection = if ($services) { @"

## Full service

A model's code for every program, assembled into one project (``tools\Test-ModelService.ps1``): the whole test suite, then
the CardDemo jobs POSTTRAN, INTCALC and TRANREPT run through the generated JobEngine with every program step implemented
by the model, each output compared byte for byte with the GnuCOBOL oracle, plus two wall-clock reruns where only
clock-derived fields may differ. Details: ``<model>\service-verification.json``.

| Model | Programs | Tests passed | Job outputs vs GnuCOBOL oracle | Wall-clock reruns | Verified |
|---|---|---|---|---|---|
$($services -join "`n")
"@ } else { '' }
@"
# Model designs and code

Each model designed every program from its requirements packet (``<model>\<PID>\design.json``) and implemented its own design in
Java on the platform layer only (``<model>\<PID>\java\``). Acceptance = the program's equivalence test: the model's step against the
faithful COBOL translation, every scenario, all datasets byte for byte, SYSOUT and return code. Compiler errors and failing scenarios
were returned to the model between attempts (``implementation.run.json`` records each attempt). The delivered code is the best
attempt: most scenarios passed, then fewest output differences (datasets, SYSOUT or return code that differ, summed over the
failing scenarios). Folders ending in ``-with-examples`` or ``-full-service`` had worked examples traced from the faithful program
and the COBOL parity notes in the implementation prompt (``implementation.prompt.md`` holds the exact prompt).

| Model | Program | Design | Scenarios passed | Output differences | Best attempt | Passed on first attempt | Minutes | Backend |
|---|---|---|---|---|---|---|---|---|
$($rows -join "`n")
$serviceSection
"@ | Set-Content (Join-Path $OutRoot 'README.md') -Encoding UTF8
Write-Host "summary: $(Join-Path $OutRoot 'README.md')"
