<#
.SYNOPSIS
  Collects every run of the model evaluation (<evalRoot>\runs\<arm>\<model>\...) into <evalRoot>\reports\results.json and
  REPORT.md: per program (design, acceptance, attempts, time, speed), per model and arm (programs passed, attempts, minutes,
  service verification against the GnuCOBOL oracle, footprint), and the decision rule of PROTOCOL.md section 5 as far as
  it can be computed (the code review and licence judgement are added by hand).
#>
param([string]$Config)
$ErrorActionPreference = 'Stop'
if (-not $Config) { $Config = Join-Path $PSScriptRoot 'eval-config.json' }
$cfg = Get-Content $Config -Raw | ConvertFrom-Json
$root = $cfg.evalRoot
$runsDir = if ($cfg.runs) { $cfg.runs } else { 'runs' }
$reportsDir = if ($cfg.reports) { $cfg.reports } else { 'reports' }
$baseline = if ($cfg.baseline) { $cfg.baseline.label } else { $cfg.models[0].name }
function J([string]$p) { if (Test-Path $p) { Get-Content $p -Raw | ConvertFrom-Json } else { $null } }
function Sum($xs) { $s = ($xs | Measure-Object -Sum).Sum; if ($null -eq $s) { 0 } else { $s } }

$cells = @(); $runs = @()
# Pipeline v2 runs are compared with a baseline run kept in another folder (cfg.baseline: label, runs folder, arm, model).
$sources = @(foreach ($arm in @('A', 'B')) { foreach ($m in $cfg.models) { [pscustomobject]@{ arm = $arm; model = $m; label = $m.name; dir = Join-Path $root "$runsDir\$arm\$($m.name)" } } })
if ($cfg.baseline) {
    $bm = [pscustomobject]@{ name = $cfg.baseline.model; lab = $cfg.baseline.lab; precision = $cfg.baseline.precision }
    $sources = @([pscustomobject]@{ arm = $cfg.baseline.arm; model = $bm; label = $cfg.baseline.label; dir = Join-Path $root "$($cfg.baseline.runs)\$($cfg.baseline.arm)\$($cfg.baseline.model)" }) + $sources
}
foreach ($src in $sources) {
    $arm = $src.arm; $m = $src.model
    if ($true) {
        $dir = $src.dir
        if (-not (Test-Path $dir)) { continue }
        $server = J (Join-Path $dir 'server.json')
        $services = @()
        foreach ($e in $cfg.estates) {
            $sv = J (Join-Path $dir "$($e.project)\service-verification.json")
            if ($sv -and $sv.report) {
                $services += [ordered]@{ project = $e.project; tests = $sv.report.tests; failed = $sv.report.failed; outputs = @($sv.report.parity).Count
                                         match = $sv.report.match; modelPrograms = @($sv.programs | Where-Object { $_.inService }).Count }
            }
            foreach ($prog in $e.programs) {
                $pd = Join-Path $dir "$($e.project)\$prog"
                $d = J (Join-Path $pd 'design.run.json')
                $i = J (Join-Path $pd 'implementation.run.json')
                if (-not $d -and -not $i) { continue }
                $atts = if ($i) { @($i.attempts) } else { @() }
                $firstPass = $atts | Where-Object { $_.tests -and $_.tests.total -gt 0 -and $_.tests.passed -eq $_.tests.total } | Select-Object -First 1
                $genTok = Sum ($atts | ForEach-Object { $_.generatedTokens }); $genSec = Sum ($atts | ForEach-Object { $_.seconds })
                $designMin = if ($d) { [math]::Round((Sum (@($d.attempts) | ForEach-Object { $_.seconds })) / 60, 1) } else { 0 }
                $implMin = if ($i) { [math]::Round((Sum ($atts | ForEach-Object { $_.seconds })) / 60, 1) } else { 0 }
                $r = if ($i) { $i.result } else { $null }
                $cells += [pscustomobject][ordered]@{
                    arm = $arm; model = $src.label; project = $e.project; program = $prog
                    designValid = [bool]($d -and $d.valid); designAttempts = if ($d) { @($d.attempts).Count } else { 0 }
                    compiled = [bool]($r -and $r.compiled); passed = if ($r) { [int]$r.testsPassed } else { 0 }; total = if ($r) { [int]$r.testsTotal } else { 0 }
                    passedAll = [bool]($r -and $r.compiled -and $r.testsTotal -gt 0 -and $r.testsPassed -eq $r.testsTotal)
                    outputDifferences = if ($r) { $r.outputDifferences } else { $null }; bestAttempt = if ($r) { $r.bestAttempt } else { $null }
                    attemptsUsed = $atts.Count; firstPassAttempt = if ($firstPass) { $firstPass.attempt } else { $null }
                    designMinutes = $designMin; implementationMinutes = $implMin; minutes = [math]::Round($designMin + $implMin, 1)
                    generatedTokens = $genTok; generationTokensPerSecond = if ($genSec -gt 0) { [math]::Round($genTok / $genSec, 1) } else { $null }
                    promptTokensPerSecond = if ($atts.Count) { [math]::Round((($atts | ForEach-Object { $_.promptTokensPerSecond } | Measure-Object -Average).Average), 0) } else { $null }
                }
            }
        }
        $runs += [pscustomobject][ordered]@{
            arm = $arm; model = $src.label; lab = $m.lab; precision = $m.precision; loadSeconds = $server.loadSeconds
            gpuMiB = if ($server) { ($server.snapshots | Measure-Object gpuMemoryUsedMiB -Maximum).Maximum } else { $null }
            serverRamGB = if ($server) { ($server.snapshots | Measure-Object serverWorkingSetGB -Maximum).Maximum } else { $null }
            wallMinutes = $server.minutes; harnessCommit = $server.harnessCommit; buildInfo = $server.buildInfo; services = $services
            complete = [bool]($server -and $server.completed)
        }
    }
}

$md = New-Object System.Collections.Generic.List[string]
$md.Add('# Model evaluation: results'); $md.Add('')
$md.Add("Generated $(Get-Date -Format 'yyyy-MM-dd HH:mm') from ``$root\$runsDir``$(if ($cfg.baseline) { " (baseline: $($cfg.baseline.label), ``$root\$($cfg.baseline.runs)``)" }). Protocol: ``tools/eval/PROTOCOL.md``$(if ($cfg.pipeline) { "; pipeline $($cfg.pipeline)" }). Runtime: $($cfg.llamaBuild).")
$md.Add('')
$self = foreach ($p in $cfg.estates.project) { J (Join-Path $root "$runsDir\selftest\published\$p\service-verification.json") }
$selfG = foreach ($p in $cfg.estates.project) { J (Join-Path $root "$runsDir\selftest\gemma-2026-10-09\$p\service-verification.json") }
if ($self -or $selfG) {
    $md.Add('## Harness self-tests'); $md.Add('')
    $md.Add('| Service | Estate | Tests | Job outputs vs GnuCOBOL |'); $md.Add('|---|---|---|---|')
    foreach ($s in @($self) + @($selfG)) { if ($s -and $s.report) { $md.Add("| $($s.model) | $($s.project) | $($s.report.tests - $s.report.failed)/$($s.report.tests) | $($s.report.match)/$(@($s.report.parity).Count) |") } }
    $md.Add('')
}
foreach ($arm in @('A', 'B')) {
    $c = @($cells | Where-Object arm -eq $arm)
    if (-not $c) { continue }
    $md.Add("## Arm ${arm}: $($cfg.arms.$arm.name)"); $md.Add(''); $md.Add($cfg.arms.$arm.about); $md.Add('')
    $md.Add('| Model | Programs passed | Implementation attempts | Minutes (design + implementation) | CardDemo service: tests / outputs | zopeneditor service: tests / outputs | Load s | GPU GiB | Server RAM GB |')
    $md.Add('|---|---|---|---|---|---|---|---|---|')
    foreach ($r in @($runs | Where-Object arm -eq $arm)) {
        $mc = @($c | Where-Object model -eq $r.model)
        $svc = foreach ($p in $cfg.estates.project) { $s = $r.services | Where-Object { $_.project -eq $p }; if ($s) { "$($s.tests - $s.failed)/$($s.tests) / $($s.match)/$($s.outputs)" } else { '-' } }
        $md.Add("| $($r.model) | $(@($mc | Where-Object passedAll).Count)/$($mc.Count) | $(Sum ($mc | ForEach-Object { $_.attemptsUsed })) | $(Sum ($mc | ForEach-Object { $_.minutes })) | $($svc[0]) | $($svc[1]) | $($r.loadSeconds) | $(if ($r.gpuMiB) { [math]::Round($r.gpuMiB / 1024, 1) }) | $($r.serverRamGB) |")
    }
    $md.Add(''); $md.Add('| Program | Model | Design | Acceptance | Output diffs | First full pass | Attempts | Minutes | Gen tok/s | Prompt tok/s |')
    $md.Add('|---|---|---|---|---|---|---|---|---|---|')
    foreach ($row in ($c | Sort-Object project, program, model)) {
        $acc = if (-not $row.designValid) { 'no valid design' } elseif ($null -eq $row.bestAttempt) { 'not run yet' } elseif (-not $row.compiled) { 'does not compile' } else { "$($row.passed)/$($row.total)" }
        $md.Add("| $($row.program) | $($row.model) | $(if ($row.designValid) { "valid ($($row.designAttempts))" } else { 'invalid' }) | $acc | $($row.outputDifferences) | $(if ($row.firstPassAttempt) { "attempt $($row.firstPassAttempt)" } else { '-' }) | $($row.attemptsUsed) | $($row.minutes) | $($row.generationTokensPerSecond) | $($row.promptTokensPerSecond) |")
    }
    $md.Add('')
}

# Decision rule (PROTOCOL.md section 5), computable parts.
$md.Add('## Decision rule (computed parts)'); $md.Add('')
$md.Add('| Challenger | 1. Arm A parity (all programs, 17/17 outputs) | 2a. Arm B programs passed vs Gemma | 2b. Attempts / minutes vs Gemma on programs both pass (arm A) | 3. Wall clock vs Gemma (arm A) |')
$md.Add('|---|---|---|---|---|')
$gA = @($cells | Where-Object { $_.arm -eq 'A' -and $_.model -eq $baseline })
$gB = @($cells | Where-Object { $_.arm -eq 'B' -and $_.model -eq $baseline })
foreach ($m in ($cfg.models | Where-Object { $_.name -ne $baseline })) {
    $a = @($cells | Where-Object { $_.arm -eq 'A' -and $_.model -eq $m.name })
    if (-not $a -and $cfg.baseline) { continue }
    $b = @($cells | Where-Object { $_.arm -eq 'B' -and $_.model -eq $m.name })
    $ra = $runs | Where-Object { $_.arm -eq 'A' -and $_.model -eq $m.name }
    $outs = Sum ($ra.services | ForEach-Object { $_.outputs }); $match = Sum ($ra.services | ForEach-Object { $_.match })
    $parity = if (-not $a) { 'not run' } else { "$(@($a | Where-Object passedAll).Count)/$($a.Count) programs; $match/$outs outputs" }
    $armB = if ($b -and $gB) { "$(@($b | Where-Object passedAll).Count) vs $(@($gB | Where-Object passedAll).Count)" } else { 'not run' }
    $both = @($a | Where-Object passedAll | Where-Object { $p = $_.program; $gA | Where-Object { $_.program -eq $p -and $_.passedAll } })
    $eff = if ($both) {
        # PowerShell names ignore case: these scalars must not be called $ga/$gb ($gA and $gB hold the baseline's cells).
        $baseAttempts = Sum ($gA | Where-Object { $both.program -contains $_.program } | ForEach-Object { $_.attemptsUsed }); $candAttempts = Sum ($both | ForEach-Object { $_.attemptsUsed })
        $baseMinutes = Sum ($gA | Where-Object { $both.program -contains $_.program } | ForEach-Object { $_.minutes }); $candMinutes = Sum ($both | ForEach-Object { $_.minutes })
        "$candAttempts vs $baseAttempts attempts ($([math]::Round(100 * ($candAttempts - $baseAttempts) / [math]::Max($baseAttempts, 1)))%); $candMinutes vs $baseMinutes min ($([math]::Round(100 * ($candMinutes - $baseMinutes) / [math]::Max($baseMinutes, 1)))%) over $($both.Count) program(s)"
    } else { '-' }
    $gw = ($runs | Where-Object { $_.arm -eq 'A' -and $_.model -eq $baseline }).wallMinutes
    $wall = if ($ra.wallMinutes -and $gw) { "$($ra.wallMinutes) vs $gw min ($([math]::Round($ra.wallMinutes / $gw, 1))x)" } else { '-' }
    $md.Add("| $($m.name) | $parity | $armB | $eff | $wall |")
}
$md.Add('')
New-Item -ItemType Directory -Force (Join-Path $root $reportsDir) | Out-Null
[ordered]@{ schema = 'mlcpu.model-eval-results.v1'; generated = (Get-Date).ToString('o'); runs = $runs; programs = $cells } |
    ConvertTo-Json -Depth 8 | Set-Content (Join-Path $root "$reportsDir\results.json") -Encoding UTF8
$md | Set-Content (Join-Path $root "$reportsDir\REPORT.md") -Encoding UTF8
Write-Host "report: $(Join-Path $root "$reportsDir\REPORT.md")"
