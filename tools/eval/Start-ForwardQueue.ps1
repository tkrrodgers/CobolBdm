<#
.SYNOPSIS
  Runs forward-engineering phases one after another (one GPU job at a time), keeping Windows awake while it runs.
  -Steps is one string (powershell -File passes no arrays): steps separated by "|", each "Key=value;Key=value" with the
  parameters of Invoke-ForwardEval.ps1, for example:
    -Steps 'Phase=architecture;Model=gpt-oss-120b|Phase=architecture;Model=gemma-4-31B-it'
  Log: <evalRoot>\forward\queue.log.
#>
param([Parameter(Mandatory)][string]$Steps, [string]$Config)
$ErrorActionPreference = 'Stop'
if (-not $Config) { $Config = Join-Path $PSScriptRoot 'eval-config-forward.json' }
$cfg = Get-Content $Config -Raw | ConvertFrom-Json
$fwd = Join-Path $cfg.evalRoot $cfg.forward
New-Item -ItemType Directory -Force $fwd | Out-Null
$log = Join-Path $fwd 'queue.log'
function Log([string]$s) {
    for ($i = 0; $i -lt 40; $i++) {
        try { [System.IO.File]::AppendAllText($log, "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $s`r`n"); return } catch { Start-Sleep -Milliseconds 250 }
    }
}
# Keep the system awake for the life of this process (no power setting changes).
Add-Type -Namespace Win32 -Name Power -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint esFlags);'
[void][Win32.Power]::SetThreadExecutionState([uint32]'0x80000000' -bor [uint32]'0x00000001')
$list = @($Steps -split '\|' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
Log "queue started (pid $PID): $($list -join ' | ')"
foreach ($step in $list) {
    $p = @{}
    foreach ($kv in $step -split ';') { $k, $v = $kv -split '=', 2; $p[$k.Trim()] = $v.Trim() }
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'Invoke-ForwardEval.ps1'), '-Config', $Config)
    foreach ($k in $p.Keys) { $argList += @("-$k", $p[$k]) }
    Log "step: $step"
    $console = Join-Path $fwd ("console-" + (($step -replace '[^\w=;.-]', '') -replace '[=;]', '-') + ".log")
    $proc = Start-Process powershell -ArgumentList $argList -NoNewWindow -PassThru -Wait -RedirectStandardOutput $console -RedirectStandardError ($console + '.err')
    Log "step finished (exit $($proc.ExitCode)): $step"
}
Log 'queue finished'
