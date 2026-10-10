<#
.SYNOPSIS
  One model, one arm of the model evaluation (tools\eval\PROTOCOL.md). Starts llama-server once with the model's frozen
  settings (eval-config.json: same llama.cpp build, context, seed and harness for every model; each model's own published
  sampling), then for each estate runs the workbench's two steps on the frozen published estate:
    design-all     every program designed from its requirements packet (+ AST outline);
    implement-all  every design implemented on the platform layer, judged by the program's equivalence test, with compiler
                   errors and every differing byte fed back (+ worked examples and parity notes in arm A).
  LM-Kit fallback is off, so every reply comes from the model under test.

  Output: <evalRoot>\runs\<Arm>\<Model>\<project>\<PID>\ (design, prompts, every attempt, java\), summary.json, run.log;
          <evalRoot>\runs\<Arm>\<Model>\server.json (load time, memory, build, exact server arguments).
#>
param(
    [Parameter(Mandatory)][string]$Model,
    [Parameter(Mandatory)][ValidateSet('A', 'B')][string]$Arm,
    [string]$Config,
    [string[]]$Projects
)
$ErrorActionPreference = 'Stop'
if (-not $Config) { $Config = Join-Path $PSScriptRoot 'eval-config.json' }
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + [Environment]::GetEnvironmentVariable('Path', 'Machine')
if (-not $env:JAVA_HOME) { $env:JAVA_HOME = [Environment]::GetEnvironmentVariable('JAVA_HOME', 'User') }

$cfg = Get-Content $Config -Raw | ConvertFrom-Json
$m = $cfg.models | Where-Object { $_.name -eq $Model }
if (-not $m) { throw "unknown model '$Model' (eval-config.json)" }
$armCfg = $cfg.arms.$Arm
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$root = $cfg.evalRoot
$runDir = Join-Path $root "runs\$Arm\$Model"
New-Item -ItemType Directory -Force $runDir | Out-Null

# A split model is loaded from its first shard.
$gguf = $m.gguf
if (Test-Path $gguf -PathType Container) {
    $gguf = Get-ChildItem $gguf -Filter *.gguf | Where-Object { $_.Name -match '00001-of-\d+\.gguf$' } | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $gguf -or -not (Test-Path $gguf)) { throw "${Model}: model file not found ($($m.gguf))" }
$weights = if (Test-Path $m.gguf -PathType Container) { Get-ChildItem $m.gguf -Filter *.gguf } else { Get-Item $gguf }
$weightsGB = [math]::Round((($weights | Measure-Object Length -Sum).Sum) / 1GB, 1)

# Appends a line for people to read; a reader holding the file open (Windows sharing) delays it, never stops the run.
function Append([string]$path, [string]$text) {
    for ($i = 0; $i -lt 40; $i++) {
        try { [System.IO.File]::AppendAllText($path, $text + "`r`n", (New-Object System.Text.UTF8Encoding $false)); return } catch { Start-Sleep -Milliseconds 250 }
    }
}
function Log([string]$s) { $line = "$(Get-Date -Format 'HH:mm:ss') $s"; Write-Host $line; Append (Join-Path $runDir 'eval.log') $line }

# The harness (MlCpu.DesignModel) is built from this worktree, so its commit pins the protocol.
dotnet build (Join-Path $repo 'MlCpu\src\MlCpu.DesignModel') -c Release -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'MlCpu.DesignModel does not build' }
$dll = Join-Path $repo 'MlCpu\src\MlCpu.DesignModel\bin\Release\net10.0\MlCpu.DesignModel.dll'
$commit = (git -C $repo rev-parse --short HEAD).Trim()
$dirty = [bool](git -C $repo status --porcelain -- MlCpu tools)

# ---- llama-server, once for every program of this arm
$port = [int]$cfg.port
if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { throw "port $port is already in use" }
$s = $m.sampling
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$serverArgs = @('-m', $gguf, '-c', "$($cfg.context)", '-np', '1', '--host', '127.0.0.1', '--port', "$port", '--jinja', '-fa', 'on', '--no-webui',
    '-to', '14400', '--reasoning-format', 'auto', '--seed', "$($cfg.seed)",
    '--temp', ([double]$s.temp).ToString($inv), '--top-p', ([double]$s.top_p).ToString($inv), '--top-k', "$($s.top_k)", '--min-p', ([double]$s.min_p).ToString($inv))
if ($m.serverArgs) { $serverArgs += ($m.serverArgs -split ' ') }
$exe = Join-Path $cfg.llamaDir 'llama-server.exe'
Log "===== $Model, arm $Arm ($($armCfg.name)); $weightsGB GB of weights; harness $commit$(if ($dirty) { ' (uncommitted changes)' })"
Log "llama-server $($serverArgs -join ' ')"
$quoted = $serverArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
$server = Start-Process -FilePath $exe -ArgumentList $quoted -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $runDir 'llama-server.out.log') -RedirectStandardError (Join-Path $runDir 'llama-server.log')
$loadStart = Get-Date
$healthy = $false
while (((Get-Date) - $loadStart).TotalMinutes -lt 45) {
    if ($server.HasExited) { throw "llama-server exited with code $($server.ExitCode) while loading (see $runDir\llama-server.log)" }
    try { if ((Invoke-WebRequest "http://127.0.0.1:$port/health" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) { $healthy = $true; break } } catch { }
    Start-Sleep -Seconds 3
}
if (-not $healthy) { Stop-Process -Id $server.Id -Force; throw 'llama-server not healthy after 45 min' }
$loadSeconds = [math]::Round(((Get-Date) - $loadStart).TotalSeconds)
function Snapshot {
    $p = Get-Process -Id $server.Id -ErrorAction SilentlyContinue
    [ordered]@{
        at = (Get-Date).ToString('o')
        gpuMemoryUsedMiB = [int]((nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits) | Select-Object -First 1)
        serverPrivateGB = if ($p) { [math]::Round($p.PrivateMemorySize64 / 1GB, 1) } else { $null }
        serverWorkingSetGB = if ($p) { [math]::Round($p.WorkingSet64 / 1GB, 1) } else { $null }
    }
}
$props = try { Invoke-RestMethod "http://127.0.0.1:$port/props" -TimeoutSec 10 } catch { $null }
$snapshots = @((Snapshot))
Log "loaded in $loadSeconds s; GPU $($snapshots[0].gpuMemoryUsedMiB) MiB; server working set $($snapshots[0].serverWorkingSetGB) GB"

$started = Get-Date
$completed = $false
try {
    foreach ($e in $cfg.estates) {
        if ($Projects -and $Projects -notcontains $e.project) { continue }
        $publish = Get-ChildItem (Join-Path $root "workspace\projects\$($e.project)\publish") -Directory | Select-Object -First 1 -ExpandProperty FullName
        $out = Join-Path $runDir $e.project
        if (Test-Path $out) { Remove-Item -Recurse -Force $out }
        New-Item -ItemType Directory -Force $out | Out-Null
        $programs = $e.programs -join ','
        $common = @('--model', $gguf, '--name', $Model, '--server', "http://127.0.0.1:$port", '--no-fallback', '--ctx', "$($cfg.context)",
            '--seed', "$($cfg.seed)", '--programs', $programs, '--normalization', (Join-Path $publish 'normalization'), '--out', $out)
        $design = @($dll, 'design-all') + $common + @('--attempts', "$($armCfg.designAttempts)")
        if ($armCfg.outline) { $design += '--outline' }
        $implement = @($dll, 'implement-all') + $common + @('--manifest', (Join-Path $publish 'capability-manifest.json'), '--project', $publish,
            '--harness-root', (Join-Path $root "harness\$Arm\$Model\$($e.project)"), '--impl-attempts', "$($armCfg.implementationAttempts)")
        if ($armCfg.outline) { $implement += '--outline' }
        if ($armCfg.examples) { $implement += '--with-examples' }
        foreach ($step in @(@{ name = 'design'; args = $design }, @{ name = 'implement'; args = $implement })) {
            Log "$($e.project): $($step.name) $programs"
            $t = Get-Date
            $ErrorActionPreference = 'Continue'
            $runLog = Join-Path $out 'run.log'
            & dotnet @($step.args) 2>&1 | ForEach-Object { Append $runLog "$_" }
            $code = $LASTEXITCODE
            $ErrorActionPreference = 'Stop'
            Log "$($e.project): $($step.name) exit $code after $([math]::Round(((Get-Date) - $t).TotalMinutes, 1)) min"
            if ($server.HasExited) { throw "llama-server exited (code $($server.ExitCode)) during $($e.project) $($step.name)" }
        }
        $snapshots += (Snapshot)
    }
    $completed = $true
}
finally {
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    [ordered]@{
        schema = 'mlcpu.model-eval-server.v1'; model = $Model; arm = $Arm; armName = $armCfg.name; lab = $m.lab; license = $m.license
        architecture = $m.architecture; precision = $m.precision; weights = $gguf; weightsGB = $weightsGB; source = $m.source
        sampling = $m.sampling; llamaBuild = $cfg.llamaBuild; buildInfo = $props.build_info; serverArguments = $serverArgs -join ' '
        harnessCommit = $commit; harnessDirty = $dirty; completed = $completed; loadSeconds = $loadSeconds; snapshots = $snapshots
        started = $started.ToString('o'); finished = (Get-Date).ToString('o'); minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $runDir 'server.json') -Encoding UTF8
    Log "===== $Model, arm ${Arm}: done in $([math]::Round(((Get-Date) - $started).TotalMinutes, 1)) min"
}
