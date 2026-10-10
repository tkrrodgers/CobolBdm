# Starts the Java sidecar on 127.0.0.1:<Port> (default 8079). Stops a previous instance on the same port first.
param([int]$Port = 8079)
$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + [Environment]::GetEnvironmentVariable('Path', 'Machine')
if (-not $env:JAVA_HOME) { $env:JAVA_HOME = [Environment]::GetEnvironmentVariable('JAVA_HOME', 'User') }
$owner = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty OwningProcess
if ($owner) { Stop-Process -Id $owner -Force; Start-Sleep -Milliseconds 500 }
Set-Location (Join-Path $PSScriptRoot '..\java-sidecar')
$env:MLCPU_CLOCK = 'MONOTONIC'
gradle run --args $Port --console=plain -q
