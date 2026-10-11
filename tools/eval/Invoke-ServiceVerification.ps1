<#
.SYNOPSIS
  Verifies a model's whole service per estate, the way the workbench verifies Gemma's (GemmaService.AssembleService + TestAsync):
    1. a copy of the frozen published estate in which each program's capability classes are replaced by the model's code,
       only when that code passed every acceptance scenario (otherwise the published classes stay, as in the workbench);
    2. every test of the service, then each job with --mlcpu.implementation=normalized, every output compared byte for byte
       with the GnuCOBOL oracle. An isolated host runs it: this worktree's MlCpu.Host on the frozen workspace (port 5190),
       through POST /api/projects/{id}/eval/verify. The live workbench and its projects are not touched.
  -Model published verifies the frozen published estate itself; -Service <dir> verifies an already assembled service
  (both are self-tests of the harness).
  Output: <evalRoot>\runs\<Arm>\<Model>\<project>\service-verification.json (+ the assembled service under service\).
#>
param(
    [Parameter(Mandatory)][string]$Model,
    [ValidateSet('A', 'B', 'selftest')][string]$Arm = 'A',
    [string]$Config,
    [string[]]$Projects,
    [string]$Service
)
$ErrorActionPreference = 'Stop'
if (-not $Config) { $Config = Join-Path $PSScriptRoot 'eval-config.json' }
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + [Environment]::GetEnvironmentVariable('Path', 'Machine')
if (-not $env:JAVA_HOME) { $env:JAVA_HOME = [Environment]::GetEnvironmentVariable('JAVA_HOME', 'User') }
$cfg = Get-Content $Config -Raw | ConvertFrom-Json
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$root = $cfg.evalRoot
# Optional layout of the evaluation root (pipeline v2 runs keep their own workspace, runs and harness folders).
$wsDir = if ($cfg.workspace) { $cfg.workspace } else { 'workspace' }
$runsDir = if ($cfg.runs) { $cfg.runs } else { 'runs' }
$harnessDir = if ($cfg.harness) { $cfg.harness } else { 'harness' }
$verifyPort = if ($cfg.verifyPort) { $cfg.verifyPort } else { 5190 }
$hostUrl = "http://localhost:$verifyPort"

function Ensure-Host {
    try { if ((Invoke-WebRequest "$hostUrl/health" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) { return } } catch { }
    $proj = Join-Path $repo 'MlCpu\src\MlCpu.Host'
    $ws = Join-Path $root "$wsDir\projects"
    $settings = Join-Path $root "$wsDir\settings.json"
    if (-not (Test-Path $settings)) { Set-Content $settings '{}' -Encoding UTF8 }
    Write-Host "starting the isolated host on $hostUrl (workspace $ws)"
    Start-Process dotnet -ArgumentList @('run', '--project', $proj, '--no-build', '--urls', $hostUrl, "--MlCpu:WorkspaceRoot=$ws", "--MlCpu:SettingsFile=$settings") `
        -WorkingDirectory $proj -WindowStyle Hidden -RedirectStandardOutput (Join-Path $root 'eval-host.out.log') -RedirectStandardError (Join-Path $root 'eval-host.log') | Out-Null
    $t = Get-Date
    while (((Get-Date) - $t).TotalMinutes -lt 3) {
        Start-Sleep -Seconds 2
        try { if ((Invoke-WebRequest "$hostUrl/health" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) { return } } catch { }
    }
    throw "the isolated host did not start (see $root\eval-host.log)"
}

Ensure-Host
foreach ($e in $cfg.estates) {
    if ($Projects -and $Projects -notcontains $e.project) { continue }
    $publish = Get-ChildItem (Join-Path $root "$wsDir\projects\$($e.project)\publish") -Directory | Select-Object -First 1 -ExpandProperty FullName
    $outDir = Join-Path $root "$runsDir\$Arm\$Model\$($e.project)"
    New-Item -ItemType Directory -Force $outDir | Out-Null
    $programs = @()
    if ($Service) { $svc = (Resolve-Path $Service).Path }
    else {
        $svc = Join-Path $outDir ("service\" + (Split-Path $publish -Leaf))
        if (Test-Path $svc) { Remove-Item -Recurse -Force $svc }
        robocopy $publish $svc /E /XD build .gradle /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
        $manifest = Get-Content (Join-Path $publish 'capability-manifest.json') -Raw | ConvertFrom-Json
        foreach ($cap in $manifest.capabilities) {
            $pkg = $cap.package.Replace('.', '\')
            $run = Join-Path $outDir "$($cap.programId)\implementation.run.json"
            $code = Join-Path $outDir "$($cap.programId)\java\$pkg"
            $r = if (Test-Path $run) { (Get-Content $run -Raw | ConvertFrom-Json).result } else { $null }
            $passed = $r -and $r.compiled -and $r.testsTotal -gt 0 -and $r.testsPassed -eq $r.testsTotal
            $entry = [ordered]@{ programId = $cap.programId; acceptance = if ($r) { "$($r.testsPassed)/$($r.testsTotal)" } else { 'not run' }; inService = $false; files = @(); lines = 0 }
            if ($Model -ne 'published' -and $passed -and (Test-Path $code)) {
                $dst = Join-Path $svc "src\main\java\$pkg"
                foreach ($c in $cap.classes) { Remove-Item (Join-Path $dst "$c.java") -Force -ErrorAction SilentlyContinue }
                $files = Get-ChildItem $code -Filter *.java
                $files | ForEach-Object { Copy-Item $_.FullName $dst -Force }
                $entry.inService = $true
                $entry.files = @($files.Name)
                $entry.lines = ($files | ForEach-Object { (Get-Content $_.FullName).Count } | Measure-Object -Sum).Sum
            }
            $programs += $entry
            Write-Host ("{0}: acceptance {1}; {2}" -f $cap.programId, $entry.acceptance, $(if ($entry.inService) { "model code in the service ($($entry.files.Count) files, $($entry.lines) lines)" } else { 'published classes kept' }))
        }
    }
    $label = "eval-$Arm-$Model"
    Write-Host "== $($e.project): verifying $svc"
    $op = Invoke-RestMethod -Method Post ("$hostUrl/api/projects/$($e.project)/eval/verify?service=" + [uri]::EscapeDataString($svc) + "&label=$label")
    do {
        Start-Sleep -Seconds 10
        $o = Invoke-RestMethod "$hostUrl/api/operations/$($op.id)"
    } while ($o.running)
    if (-not $o.succeeded) { Write-Warning "$($e.project): verification failed: $($o.error)"; $o.log | Select-Object -Last 20 | ForEach-Object { Write-Host "   $_" } }
    $rep = $o.result
    [ordered]@{
        schema = 'mlcpu.model-eval-service.v1'; model = $Model; arm = $Arm; project = $e.project; verifiedAt = (Get-Date).ToString('o')
        service = $svc; programs = $programs; succeeded = $o.succeeded; error = $o.error; report = $rep
    } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $outDir 'service-verification.json') -Encoding UTF8
    if ($rep) { Write-Host "$($e.project): tests $($rep.tests - $rep.failed)/$($rep.tests); job outputs vs GnuCOBOL $($rep.match)/$(@($rep.parity).Count)" }
}
