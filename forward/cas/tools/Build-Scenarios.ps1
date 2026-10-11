<#
.SYNOPSIS
  Phase 0 of the forward-engineering evaluation (tools\eval\PROTOCOL-forward.md): rebuilds the Customer Account
  Service's acceptance scenarios from the GnuCOBOL-equivalent faithful programs.
    1. Copies the published CardDemo and zopeneditor estates (pipeline v2 workspace), adds the export tests
       (forward\cas\sme\export) and runs them: every equivalence scenario of CBTRN02C, CBACT04C, CBTRN03C and SAM1/SAM2
       is run by the faithful program and decoded into the service's terms (drafts).
    2. Runs ScenarioBuild (forward\cas\sme) with the reference implementation:
       * expected values;
       * the cross-check against the faithful programs (cross-check.md);
       * the SME-authored scenarios;
       * rule tags and the stratified public/hidden split (scenarios.json, service\cas-acceptance\src\test\resources\scenarios).
  Then run Test-ForwardGate.ps1.
#>
param(
    [string]$Workspace = 'C:\Users\Tomrodgers\Source\modelEval\workspace-v2',
    [string]$Work = (Join-Path $env:TEMP 'cas-scenario-build')
)
$ErrorActionPreference = 'Stop'
$cas = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (Test-Path $Work) { Remove-Item -Recurse -Force $Work }
New-Item -ItemType Directory -Force $Work | Out-Null
$drafts = Join-Path $Work 'drafts'

function Copy-Tree([string]$from, [string]$to) {
    New-Item -ItemType Directory -Force $to | Out-Null
    Get-ChildItem $from -Force | Where-Object { $_.Name -notin @('build', '.gradle') } | ForEach-Object { Copy-Item $_.FullName -Destination $to -Recurse -Force }
}

function Invoke-Gradle([string]$dir, [string]$tasks) {
    Push-Location $dir
    try {
        $ErrorActionPreference = 'Continue'
        & cmd.exe /c "gradle $tasks --offline --no-daemon --console=plain -q" 2>&1 | ForEach-Object { "$_" } | Select-Object -Last 30
        if ($LASTEXITCODE -ne 0) { throw "gradle $tasks failed in $dir" }
    } finally {
        $ErrorActionPreference = 'Stop'
        Pop-Location
    }
}

# 1. legacy drafts
$env:FORWARD_EXPORT_DIR = $drafts
foreach ($e in @(@{ project = 'carddemo'; export = 'carddemo' }, @{ project = 'zopeneditor-sample'; export = 'zopeneditor-sample' })) {
    $published = Get-ChildItem (Join-Path $Workspace "projects\$($e.project)\publish") -Directory | Select-Object -First 1 -ExpandProperty FullName
    $copy = Join-Path $Work "estate-$($e.project)"
    Copy-Tree $published $copy
    Copy-Item (Join-Path $cas "sme\export\$($e.export)\*") -Destination (Join-Path $copy 'src\test\java') -Recurse -Force
    Write-Host "$($e.project): running the faithful programs on every equivalence scenario"
    Invoke-Gradle $copy "test --tests *ForwardExportTest"
}
Remove-Item Env:FORWARD_EXPORT_DIR

# 2. the scenario build with the reference implementation
$gate = Join-Path $Work 'service'
Copy-Tree (Join-Path $cas 'service') $gate
Copy-Item (Join-Path $cas 'reference\*') -Destination $gate -Recurse -Force
$smeDir = Join-Path $gate 'cas-acceptance\src\test\java\com\mlcpu\cas\acceptance\sme'
New-Item -ItemType Directory -Force $smeDir | Out-Null
Copy-Item (Join-Path $cas 'sme\src\test\java\com\mlcpu\cas\acceptance\sme\*.java') -Destination $smeDir
$env:CAS_DRAFTS = $drafts
$env:CAS_OUT = $cas
Write-Host 'scenario build: reference implementation, cross-check, split'
Push-Location $gate
try {
    $ErrorActionPreference = 'Continue'
    & cmd.exe /c "gradle :cas-acceptance:test --tests *ScenarioBuild --offline --no-daemon --console=plain -i" 2>&1 | ForEach-Object { "$_" } |
        Where-Object { $_ -match 'scenarios:|rules:|FAILED|Exception' } | ForEach-Object { $_.Trim() }
    if ($LASTEXITCODE -ne 0) { throw 'the scenario build failed' }
} finally {
    $ErrorActionPreference = 'Stop'
    Pop-Location
    Remove-Item Env:CAS_DRAFTS, Env:CAS_OUT
}
Write-Host "scenarios, scenarios.json and cross-check.md written under $cas"
