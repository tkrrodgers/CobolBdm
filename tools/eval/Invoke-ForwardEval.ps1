<#
.SYNOPSIS
  The forward-engineering evaluation (tools\eval\PROTOCOL-forward.md, revision 3): one model, one phase, one model load.
    architecture  step 1a with every seed of the config      -> <evalRoot>\forward\designs\<model>\seed-<n>\architecture.json
    review        peer review of anonymized architectures    -> <evalRoot>\forward\reviews\<model>\review-<label>.json
                  (-Designs "D1=path;D2=path")
    slices        step 1b for one architecture (-Seed n)      -> <evalRoot>\forward\designs\<model>\seed-<n>\slice-*.json
    implement     one arm (-Arm S|X) of a design (-Design dir, -Designer label)
                                                              -> <evalRoot>\forward\implementation\<arm>-<model>\
  llama-server runs with the model's frozen settings (eval-config-forward.json: the llama.cpp build, context and sampling of
  eval-config-v2.json); LM-Kit fallback is off. The harness (MlCpu.DesignModel) is built from this worktree, so its commit pins
  the run. Log: <evalRoot>\forward\forward.log; each phase writes server.json next to its output.
#>
param(
    [Parameter(Mandatory)][ValidateSet('architecture', 'review', 'slices', 'implement')][string]$Phase,
    [Parameter(Mandatory)][string]$Model,
    [int]$Seed = 42,
    [string]$Designs,
    [string]$Design,
    [string]$Designer,
    [ValidateSet('S', 'X')][string]$Arm = 'S',
    [string]$Config
)
$ErrorActionPreference = 'Stop'
if (-not $Config) { $Config = Join-Path $PSScriptRoot 'eval-config-forward.json' }
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + [Environment]::GetEnvironmentVariable('Path', 'Machine')
if (-not $env:JAVA_HOME) { $env:JAVA_HOME = [Environment]::GetEnvironmentVariable('JAVA_HOME', 'User') }
$cfg = Get-Content $Config -Raw | ConvertFrom-Json
$m = $cfg.models | Where-Object { $_.name -eq $Model }
if (-not $m) { throw "unknown model '$Model' ($Config)" }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$cas = Join-Path $repo $cfg.cas
$fwd = Join-Path $cfg.evalRoot $cfg.forward
New-Item -ItemType Directory -Force $fwd | Out-Null

function Append([string]$path, [string]$text) {
    for ($i = 0; $i -lt 40; $i++) {
        try { [System.IO.File]::AppendAllText($path, $text + "`r`n", (New-Object System.Text.UTF8Encoding $false)); return } catch { Start-Sleep -Milliseconds 250 }
    }
}
function Log([string]$s) { $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $s"; Write-Host $line; Append (Join-Path $fwd 'forward.log') $line }

$out = switch ($Phase) {
    'architecture' { Join-Path $fwd "designs\$Model" }
    'review' { Join-Path $fwd "reviews\$Model" }
    'slices' { Join-Path $fwd "designs\$Model\seed-$Seed" }
    'implement' { Join-Path $fwd "implementation\$Arm-$Model" }
}
New-Item -ItemType Directory -Force $out | Out-Null

# The Phase 0 gate must have passed before any model runs.
$gate = Get-Content (Join-Path $cas 'gate.json') -Raw | ConvertFrom-Json
if (-not $gate.pass) { throw "the Phase 0 gate has not passed ($cas\gate.json)" }

dotnet build (Join-Path $repo 'MlCpu\src\MlCpu.DesignModel') -c Release -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'MlCpu.DesignModel does not build' }
$dll = Join-Path $repo 'MlCpu\src\MlCpu.DesignModel\bin\Release\net10.0\MlCpu.DesignModel.dll'
$commit = (git -C $repo rev-parse --short HEAD).Trim()
$dirty = [bool](git -C $repo status --porcelain -- MlCpu tools forward)

# ---- llama-server, once for the phase
$port = [int]$cfg.port
if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { throw "port $port is already in use" }
$s = $m.sampling
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$serverSeed = if ($Phase -eq 'implement') { $cfg.implementation.seed } else { $Seed }
$serverArgs = @('-m', $m.gguf, '-c', "$($cfg.context)", '-np', '1', '--host', '127.0.0.1', '--port', "$port", '--jinja', '-fa', 'on', '--no-webui',
    '-to', '14400', '--reasoning-format', 'auto', '--seed', "$serverSeed",
    '--temp', ([double]$s.temp).ToString($inv), '--top-p', ([double]$s.top_p).ToString($inv), '--top-k', "$($s.top_k)", '--min-p', ([double]$s.min_p).ToString($inv))
if ($m.serverArgs) { $serverArgs += ($m.serverArgs -split ' ') }
Log "===== $Model, phase $Phase$(if ($Phase -eq 'implement') { " arm $Arm" }); harness $commit$(if ($dirty) { ' (uncommitted changes)' })"
Log "llama-server $($serverArgs -join ' ')"
$quoted = $serverArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
$server = Start-Process -FilePath (Join-Path $cfg.llamaDir 'llama-server.exe') -ArgumentList $quoted -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $out 'llama-server.out.log') -RedirectStandardError (Join-Path $out 'llama-server.log')
$loadStart = Get-Date
$healthy = $false
while (((Get-Date) - $loadStart).TotalMinutes -lt 45) {
    if ($server.HasExited) { throw "llama-server exited with code $($server.ExitCode) while loading (see $out\llama-server.log)" }
    try { if ((Invoke-WebRequest "http://127.0.0.1:$port/health" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) { $healthy = $true; break } } catch { }
    Start-Sleep -Seconds 3
}
if (-not $healthy) { Stop-Process -Id $server.Id -Force; throw 'llama-server not healthy after 45 min' }
$loadSeconds = [math]::Round(((Get-Date) - $loadStart).TotalSeconds)
$props = try { Invoke-RestMethod "http://127.0.0.1:$port/props" -TimeoutSec 10 } catch { $null }
Log "loaded in $loadSeconds s"

function Run([string[]]$arguments, [string]$label) {
    $runLog = Join-Path $out "$label.log"
    $t = Get-Date
    $ErrorActionPreference = 'Continue'
    & dotnet @($dll) @arguments 2>&1 | ForEach-Object { Append $runLog "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    Log "$label exit $code after $([math]::Round(((Get-Date) - $t).TotalMinutes, 1)) min"
    if ($server.HasExited) { throw "llama-server exited (code $($server.ExitCode)) during $label" }
    return $code
}

$common = @('--model', $m.gguf, '--name', $Model, '--server', "http://127.0.0.1:$port", '--no-fallback', '--ctx', "$($cfg.context)", '--cas', $cas,
    '--reasoning', $m.reasoning, '--impl-reasoning', $m.reasoning)
$started = Get-Date
$completed = $false
try {
    switch ($Phase) {
        'architecture' {
            foreach ($sd in $cfg.design.seeds) {
                Run (@('fwd-architecture') + $common + @('--seed', "$sd", '--attempts', "$($cfg.design.attempts)", '--max-tokens', "$($cfg.design.maxTokens)",
                        '--out', (Join-Path $out "seed-$sd"))) "architecture-seed-$sd" | Out-Null
            }
        }
        'review' {
            Run (@('fwd-review') + $common + @('--seed', "$Seed", '--designs', $Designs, '--attempts', "$($cfg.review.attempts)",
                    '--max-tokens', "$($cfg.review.maxTokens)", '--out', $out)) 'review' | Out-Null
        }
        'slices' {
            Run (@('fwd-slices') + $common + @('--seed', "$Seed", '--architecture', (Join-Path $out 'architecture.json'),
                    '--normalization', ($cfg.normalization -join ';'), '--attempts', "$($cfg.design.attempts)", '--max-tokens', "$($cfg.design.slicesMaxTokens)",
                    '--out', $out)) 'slices' | Out-Null
        }
        'implement' {
            if (-not $Design) { throw '-Design <dir with architecture.json and slice-*.json> is required' }
            Run (@('fwd-implement') + $common + @('--seed', "$($cfg.implementation.seed)", '--design', $Design, '--arm', $Arm,
                    '--designer', $(if ($Designer) { $Designer } else { $Model }), '--impl-attempts', "$($cfg.implementation.attempts)",
                    '--max-tokens', "$($cfg.implementation.maxTokens)", '--plateau', "$($cfg.implementation.plateau)",
                    '--test-timeout-min', "$($cfg.implementation.testTimeoutMinutes)", '--harness', (Join-Path $fwd "harness\$Arm-$Model"), '--out', $out)) "implement-$Arm" | Out-Null
        }
    }
    $completed = $true
}
finally {
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    [ordered]@{
        schema = 'mlcpu.forward-eval-server.v1'; model = $Model; phase = $Phase; arm = $(if ($Phase -eq 'implement') { $Arm } else { $null })
        seed = $(if ($Phase -eq 'architecture') { $cfg.design.seeds } else { $Seed }); design = $Design; designer = $Designer
        weights = $m.gguf; sampling = $m.sampling; reasoning = $m.reasoning; llamaBuild = $cfg.llamaBuild; buildInfo = $props.build_info
        serverArguments = $serverArgs -join ' '; harnessCommit = $commit; harnessDirty = $dirty; completed = $completed; loadSeconds = $loadSeconds
        started = $started.ToString('o'); finished = (Get-Date).ToString('o'); minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
    } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $out "server-$Phase$(if ($Phase -eq 'implement') { "-$Arm" }).json") -Encoding UTF8
    Log "===== $Model, phase ${Phase}: done in $([math]::Round(((Get-Date) - $started).TotalMinutes, 1)) min"
}
