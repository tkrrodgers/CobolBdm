<#
.SYNOPSIS
  Generates worked examples for a normalized capability by running the faithful program on a small illustrative input
  and tracing its inputs, checks and exact output bytes: capabilities/<estate>/normalization/<PID>/examples.md.
  The generator (tools/examples/<PID>WorkedExamples.java) runs as a JUnit class inside a scratch copy of the exported
  single-module project (POST /migrate/estate-java?partition=false).
#>
param(
    [string]$Program = 'CBTRN02C',
    [string]$Project = (Join-Path $PSScriptRoot '..\work\export\carddemo-normalized-spring'),
    [string]$Capabilities = (Join-Path $PSScriptRoot '..\capabilities\carddemo'),
    # Copybooks appended verbatim: layouts whose VALUE clauses (report literals) the requirements packet does not carry.
    [string[]]$Copybooks = @{ 'CBTRN03C' = @('CVTRA07Y') }[$Program],
    [string]$CopybookRoot = (Join-Path $PSScriptRoot '..\..\carddemo\app\cpy')
)
$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + [Environment]::GetEnvironmentVariable('Path', 'Machine')
$Project = (Resolve-Path $Project).Path; $Capabilities = (Resolve-Path $Capabilities).Path
$generator = Join-Path $PSScriptRoot "examples\${Program}WorkedExamples.java"
if (-not (Test-Path $generator)) { throw "no generator for $Program ($generator)" }
$manifest = Get-Content (Join-Path $Capabilities 'capability-manifest.json') -Raw | ConvertFrom-Json
$cap = $manifest.capabilities | Where-Object programId -eq $Program
$scratch = Join-Path $PSScriptRoot "..\work\examples-$Program"
if (Test-Path $scratch) { Remove-Item -Recurse -Force $scratch }
robocopy $Project $scratch /E /XD build .gradle /NFL /NDL /NJH /NJS /NP | Out-Null
$testDir = Join-Path $scratch ('src\test\java\' + $cap.package.Replace('.', '\'))
Copy-Item $generator $testDir
$class = "$($cap.package).${Program}WorkedExamples"
Push-Location $scratch
try { cmd /c "gradle test --offline --no-daemon -q --tests $class 2>&1" | Out-Null } finally { Pop-Location }
$generated = Join-Path $scratch 'build\examples.md'
if (-not (Test-Path $generated)) {
    foreach ($x in Get-ChildItem (Join-Path $scratch 'build\test-results\test') -Filter '*WorkedExamples*.xml' -ErrorAction SilentlyContinue) {
        $tc = ([xml](Get-Content $x.FullName -Raw)).testsuite.testcase | Where-Object { $_.failure -or $_.error }
        foreach ($t in $tc) { Write-Host "$($t.name): $("$($t.failure.InnerText)$($t.error.InnerText)".Split("`n") | Select-Object -First 6 | Out-String)" }
    }
    throw "the $Program example generator failed (scratch project kept at $scratch)"
}
$out = Join-Path $Capabilities "normalization\$Program\examples.md"
Copy-Item $generated $out -Force
foreach ($cb in @($Copybooks | Where-Object { $_ })) {
    $file = Join-Path $CopybookRoot "$cb.cpy"
    if (-not (Test-Path $file)) { throw "copybook $cb not found under $CopybookRoot" }
    $text = (Get-Content $file | ForEach-Object { $_.TrimEnd() }) -join "`n"
    Add-Content -Path $out -Encoding UTF8 -Value @"

## Record layout with VALUE clauses: copybook $cb (verbatim)

The requirements packet lists these records' fields but not their VALUE clauses or the level-01 items without
subordinate fields; this is the copybook as compiled. Report lines are built from these records.

``````cobol
$text
``````
"@
}
Remove-Item -Recurse -Force $scratch
Write-Host "examples: $out"
