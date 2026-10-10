<#
.SYNOPSIS
  Runs the whole evaluation unattended, one GPU job at a time, in protocol order (arm A for every model, then arm B):
  for each entry it waits until the model's download has finished (its download log ends with "exit 0") and no other
  evaluation server holds the port, runs Invoke-ModelEval.ps1, verifies the services (Invoke-ServiceVerification.ps1) and
  refreshes the report (New-EvalReport.ps1). An entry whose server.json exists has already run and is skipped, so the queue
  can be stopped and started again. Log: <evalRoot>\queue.log.
#>
param(
    [string[]]$Order = @('A:gemma-4-31B-it', 'A:laguna-s-2.1', 'A:inkling-small', 'B:gemma-4-31B-it', 'B:laguna-s-2.1', 'B:inkling-small'),
    [string]$Config
)
$ErrorActionPreference = 'Stop'
if (-not $Config) { $Config = Join-Path $PSScriptRoot 'eval-config.json' }
$cfg = Get-Content $Config -Raw | ConvertFrom-Json
$root = $cfg.evalRoot
$log = Join-Path $root 'queue.log'
# Appends a line for people to read; a reader holding the file open (Windows sharing) delays it, never stops the run.
function Append([string]$path, [string]$text) {
    for ($i = 0; $i -lt 40; $i++) {
        try { [System.IO.File]::AppendAllText($path, $text + "`r`n", (New-Object System.Text.UTF8Encoding $false)); return } catch { Start-Sleep -Milliseconds 250 }
    }
}
function Log([string]$s) { Append $log "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $s" }
function Script([string]$name) { Join-Path $PSScriptRoot $name }
# A run counts as done only when its server.json says it completed; an aborted run is run again.
function Done([string]$dir) { $f = Join-Path $dir 'server.json'; (Test-Path $f) -and ((Get-Content $f -Raw | ConvertFrom-Json).completed -eq $true) }

Log "queue started (pid $PID): $($Order -join ', ')"
foreach ($entry in $Order) {
    $arm, $model = $entry -split ':', 2
    $m = $cfg.models | Where-Object { $_.name -eq $model }
    $runDir = Join-Path $root "runs\$arm\$model"
    if (Done $runDir) { Log "$entry already run; skipped" }
    else {
        if ($m.downloadLog) {
            $waited = $false
            while (-not ((Test-Path $m.downloadLog) -and ((Get-Content $m.downloadLog -Tail 1) -match '^exit 0'))) {
                if ((Test-Path $m.downloadLog) -and ((Get-Content $m.downloadLog -Tail 1) -match '^exit [1-9]')) { Log "$entry`: download failed ($($m.downloadLog)); skipped"; break }
                if (-not $waited) { Log "$entry`: waiting for the download ($($m.downloadLog))"; $waited = $true }
                Start-Sleep -Seconds 60
            }
            if ((Get-Content $m.downloadLog -Tail 1) -notmatch '^exit 0') { continue }
        }
        # A run started outside the queue holds the port until it finishes and writes its server.json.
        while (Get-NetTCPConnection -LocalPort $cfg.port -State Listen -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 60 }
        Start-Sleep -Seconds 30
        if (Done $runDir) { Log "$entry finished outside the queue; not run again" }
        else {
            Log "$entry`: run"
            & powershell -ExecutionPolicy Bypass -File (Script 'Invoke-ModelEval.ps1') -Model $model -Arm $arm *> (Join-Path $root "$arm-$model.console.log")
            Log "$entry`: run finished (exit $LASTEXITCODE)"
        }
    }
    $verified = @($cfg.estates | Where-Object { Test-Path (Join-Path $runDir "$($_.project)\service-verification.json") }).Count -eq @($cfg.estates).Count
    if ((Done $runDir) -and -not $verified) {
        Log "$entry`: service verification"
        & powershell -ExecutionPolicy Bypass -File (Script 'Invoke-ServiceVerification.ps1') -Model $model -Arm $arm *> (Join-Path $root "$arm-$model.verify.log")
        Log "$entry`: verification finished (exit $LASTEXITCODE)"
    }
    & powershell -ExecutionPolicy Bypass -File (Script 'New-EvalReport.ps1') *> (Join-Path $root 'report.console.log')
}
Log 'queue finished'
