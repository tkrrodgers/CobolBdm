<#
.SYNOPSIS
  JOBOL audit of a generated Java estate (JavaJOBOLAuditor.md).
.DESCRIPTION
  All arguments go to the auditor, for example:
    tools\jobol-auditor\jobol-audit.ps1 C:\Users\Tomrodgers\Source\ModernizedJava\loanamorti --out work\jobol\loanamorti
  Runs the Gradle distribution when installed (gradle installDist); otherwise compiles the sources with javac against
  the javaparser/gson jars in the Gradle cache (no network, no Gradle daemon) into build\direct.
  Exit codes: 0 gate passed, 1 gate failed, 2 usage or I/O error.
#>
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$dist = Join-Path $here 'build\install\jobol-audit\bin\jobol-audit.bat'
if ((Test-Path $dist) -and -not $env:JOBOL_DIRECT) {
    & $dist @args
    exit $LASTEXITCODE
}
$gradleHome = if ($env:GRADLE_USER_HOME) { $env:GRADLE_USER_HOME } else { Join-Path $HOME '.gradle' }
$cache = Join-Path $gradleHome 'caches\modules-2\files-2.1'
function Find-Jar($dir, $name) {
    $d = Join-Path $cache $dir
    if (-not (Test-Path $d)) { return $null }
    Get-ChildItem -Path $d -Recurse -Filter $name | Select-Object -First 1 -ExpandProperty FullName
}
$jp = Find-Jar 'com.github.javaparser\javaparser-core\3.28.2' 'javaparser-core-3.28.2.jar'
$gs = Find-Jar 'com.google.code.gson\gson\2.11.0' 'gson-2.11.0.jar'
if (-not $jp -or -not $gs) {
    [Console]::Error.WriteLine("jobol-audit: javaparser-core 3.28.2 / gson 2.11.0 not in $cache; run 'gradle installDist' in $here once")
    exit 2
}
$out = Join-Path $here 'build\direct\classes'
$stamp = Join-Path $here 'build\direct\.stamp'
$sources = Get-ChildItem -Path (Join-Path $here 'src\main') -Recurse -File
$stale = -not (Test-Path $stamp)
if (-not $stale) {
    $built = (Get-Item $stamp).LastWriteTimeUtc
    $stale = [bool]($sources | Where-Object { $_.LastWriteTimeUtc -gt $built } | Select-Object -First 1)
}
if ($stale) {
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    New-Item -ItemType Directory -Force $out | Out-Null
    $java = $sources | Where-Object Extension -eq '.java' | ForEach-Object FullName
    & javac -encoding UTF-8 -nowarn -d $out -cp "$jp;$gs" @java
    if ($LASTEXITCODE -ne 0) { exit 2 }
    Copy-Item (Join-Path $here 'src\main\resources\*') $out
    Set-Content -Path $stamp -Value '' -Encoding utf8
}
& java -Xss16m -Xmx2g -cp "$out;$jp;$gs" com.mlcpu.jobol.Main @args
exit $LASTEXITCODE
