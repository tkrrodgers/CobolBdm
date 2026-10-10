<#
.SYNOPSIS
  Stops the Blazor host listening on -Port (default 5100) and rebuilds it. Start it again with
  dotnet run --project MlCpu\src\MlCpu.Host --no-build --urls http://localhost:5100
#>
param([int]$Port = 5100)
$ErrorActionPreference = 'Stop'
$owner = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty OwningProcess
if ($owner) { Stop-Process -Id $owner -Force; Start-Sleep -Milliseconds 800; Write-Host "stopped host (pid $owner)" }
Push-Location (Join-Path $PSScriptRoot '..')
try {
    $out = dotnet build MlCpu\src\MlCpu.Host -v q -nologo 2>&1
    $errors = $out | Select-String -Pattern ' error '
    if ($LASTEXITCODE -ne 0) { $errors | Select-Object -First 20 | ForEach-Object { Write-Host $_ }; throw 'host build failed' }
    Write-Host 'host built'
} finally { Pop-Location }
