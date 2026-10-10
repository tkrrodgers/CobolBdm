<#
.SYNOPSIS
  CardDemo batch parity: original COBOL (GnuCOBOL oracle) vs generated Java, same inputs, same pinned clock.

.DESCRIPTION
  1. Converts the CardDemo batch programs COBOL -> IR (C#) -> Java (sidecar) and compiles the Java.
  2. Prepares the shared inputs from app/data/ASCII (IDCAMS REPRO equivalents: fixed and KSDS images).
  3. Runs the job flow twice - GnuCOBOL (-std=ibm -fsign=EBCDIC, COB_CURRENT_DATE pinned) and Java (StepRunner,
     clock FIXED at the same instant) - each in its own data directory:
       S1 CBACT02C  S2 CBACT03C  S3 CBCUS01C  S4 CBTRN01C  S5 POSTTRAN/CBTRN02C  S6 INTCALC/CBACT04C
       S7 COMBTRAN (sort + reload)  S8 TRANREPT (sort + CBTRN03C)
  4. Compares every output dataset byte for byte, SYSOUT line by line, and the step return codes.
  Writes work/parity/parity-report.md and parity-report.json. Exit code 0 = every comparison MATCH.
#>
param(
    [string]$CardDemo = (Join-Path $HOME 'Source\carddemo\app'),
    [string]$Clock = '2022-07-05 10:11:12',
    [string]$Sidecar = 'http://127.0.0.1:8079',
    [switch]$SkipConvert,
    # Where inputs, the oracle executables and both sides' outputs go (a project's oracle/script directory, or work\parity).
    [string]$Work,
    # Recompile every GnuCOBOL executable even when its content hash is unchanged.
    [switch]$Rebuild
)
$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Work) { $Work = Join-Path $Repo 'work\parity' }
New-Item -ItemType Directory -Force $Work | Out-Null
$Work = (Resolve-Path $Work).Path
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + [Environment]::GetEnvironmentVariable('Path', 'Machine')
$Gc = [Environment]::GetEnvironmentVariable('GNUCOBOL_HOME', 'User')
foreach ($v in 'GNUCOBOL_HOME', 'JAVA_HOME') { Set-Item "env:$v" ([Environment]::GetEnvironmentVariable($v, 'User')) }
$userEnv = [Environment]::GetEnvironmentVariables('User')
foreach ($k in $userEnv.Keys) { if ($k -like 'COB_*') { Set-Item "env:$k" $userEnv[$k] } }
$env:Path = "$Gc\bin;$Gc\mingw64\bin;" + $env:Path
$Cobc = Join-Path $Gc 'bin\cobc.exe'
function Invoke-Cobc {
    $ErrorActionPreference = 'Continue'
    $out = & $Cobc @args 2>&1 | ForEach-Object { "$_" }
    [pscustomobject]@{ Code = $LASTEXITCODE; Out = ($out -join "`n") }
}
function Invoke-Java {
    $ErrorActionPreference = 'Continue'
    $out = & (Join-Path $env:JAVA_HOME 'bin\java.exe') @args 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE) { throw "java $($args -join ' ') failed: $($out -join ' ')" }
}

function Step([string]$m) { Write-Host "== $m" -ForegroundColor Cyan }
function Reset-Dir([string]$p) { if (Test-Path $p) { Remove-Item $p -Recurse -Force }; New-Item -ItemType Directory -Force $p | Out-Null; $p }
$L1 = [Text.Encoding]::GetEncoding(28591)
function Quote-Arg([string]$a) { if ($a -match '[\s"]') { '"' + ($a -replace '"', '\"') + '"' } else { $a } }
function First-Diff([byte[]]$x, [byte[]]$y) {
    $n = [Math]::Min($x.Length, $y.Length)
    for ($i = 0; $i -lt $n; $i++) { if ($x[$i] -ne $y[$i]) { return $i } }
    if ($x.Length -ne $y.Length) { return $n }
    return -1
}

# ------------------------------------------------------------------ definitions
$Programs = 'CBACT02C', 'CBACT03C', 'CBCUS01C', 'CBTRN01C', 'CBTRN02C', 'CBACT04C', 'CBTRN03C'
$Ksds = @(
    @{ Name = 'ACCTDATA'; Src = 'acctdata.txt'; Lrecl = 300; Key = '1,11' },
    @{ Name = 'CARDDATA'; Src = 'carddata.txt'; Lrecl = 150; Key = '1,16' },
    @{ Name = 'CUSTDATA'; Src = 'custdata.txt'; Lrecl = 500; Key = '1,9' },
    @{ Name = 'CARDXREF'; Src = 'cardxref.txt'; Lrecl = 50; Key = '1,16'; Alt = '26,11' },
    @{ Name = 'TCATBAL'; Src = 'tcatbal.txt'; Lrecl = 50; Key = '1,17' },
    @{ Name = 'DISCGRP'; Src = 'discgrp.txt'; Lrecl = 50; Key = '1,16' },
    @{ Name = 'TRANTYPE'; Src = 'trantype.txt'; Lrecl = 60; Key = '1,2' },
    @{ Name = 'TRANCATG'; Src = 'trancatg.txt'; Lrecl = 60; Key = '1,6' },
    @{ Name = 'TRANSACT'; Src = $null; Lrecl = 350; Key = '1,16' }
)
$KsdsByName = @{}; foreach ($k in $Ksds) { $KsdsByName[$k.Name] = $k }
$Steps = @(
    @{ Id = 'S1-CBACT02C'; Pgm = 'CBACT02C'; DD = [ordered]@{ CARDFILE = 'CARDDATA.ksds' }; Out = @() },
    @{ Id = 'S2-CBACT03C'; Pgm = 'CBACT03C'; DD = [ordered]@{ XREFFILE = 'CARDXREF.ksds' }; Out = @() },
    @{ Id = 'S3-CBCUS01C'; Pgm = 'CBCUS01C'; DD = [ordered]@{ CUSTFILE = 'CUSTDATA.ksds' }; Out = @() },
    @{ Id = 'S4-CBTRN01C'; Pgm = 'CBTRN01C'; DD = [ordered]@{ DALYTRAN = 'DALYTRAN.dat'; CUSTFILE = 'CUSTDATA.ksds'; XREFFILE = 'CARDXREF.ksds'; CARDFILE = 'CARDDATA.ksds'; ACCTFILE = 'ACCTDATA.ksds'; TRANFILE = 'TRANSACT.ksds' }; Out = @() },
    @{ Id = 'S5-POSTTRAN'; Pgm = 'CBTRN02C'; DD = [ordered]@{ DALYTRAN = 'DALYTRAN.dat'; TRANFILE = 'TRANSACT.ksds'; XREFFILE = 'CARDXREF.ksds'; DALYREJS = 'DALYREJS.dat'; ACCTFILE = 'ACCTDATA.ksds'; TCATBALF = 'TCATBAL.ksds' }; Out = @('TRANSACT.ksds', 'DALYREJS.dat', 'ACCTDATA.ksds', 'TCATBAL.ksds') },
    @{ Id = 'S6-INTCALC'; Pgm = 'CBACT04C'; Parm = '2022071800'; DD = [ordered]@{ TCATBALF = 'TCATBAL.ksds'; XREFFILE = 'CARDXREF.ksds'; ACCTFILE = 'ACCTDATA.ksds'; DISCGRP = 'DISCGRP.ksds'; TRANSACT = 'SYSTRAN.dat' }; Out = @('SYSTRAN.dat', 'ACCTDATA.ksds') },
    @{ Id = 'S7-COMBTRAN'; Util = 'COMBTRAN'; Out = @('TRANSACT.ksds') },
    @{ Id = 'S8-TRANREPT'; Pgm = 'CBTRN03C'; Pre = 'TRANREPT'; DD = [ordered]@{ TRANFILE = 'TRANDALY.dat'; CARDXREF = 'CARDXREF.ksds'; TRANTYPE = 'TRANTYPE.ksds'; TRANCATG = 'TRANCATG.ksds'; TRANREPT = 'TRANREPT.dat'; DATEPARM = 'DATEPARM.dat' }; Out = @('TRANDALY.dat', 'TRANREPT.dat') }
)
$SortComb = 'SORT FIELDS=(1,16,CH,A)'
$SortRept = @('SORT FIELDS=(263,16,ZD,A)', "INCLUDE COND=(305,10,CH,GE,C'2022-01-01',AND,305,10,CH,LE,C'2022-07-06')")

$at = [datetime]::ParseExact($Clock, 'yyyy-MM-dd HH:mm:ss', $null)
$off = [TimeZoneInfo]::Local.GetUtcOffset($at)
$Epoch = $at.ToString('yyyy-MM-ddTHH:mm:ss') + ('{0}{1:hh\:mm}' -f ($(if ($off -lt [TimeSpan]::Zero) { '-' } else { '+' }), $off))
# YYYYMMDDHHMMSShh pins the hundredths too; the 'YYYY/MM/DD HH:MM:SS' form leaves them on the wall clock.
$CobDate = $at.ToString('yyyyMMddHHmmss') + '00'

# ------------------------------------------------------------------ 1. runtime + conversion
Step 'Build Java runtime'
Push-Location (Join-Path $Repo 'java-sidecar'); gradle classes --console=plain -q; if ($LASTEXITCODE) { throw 'runtime build failed' }; Pop-Location
$Rt = Join-Path $Repo 'java-sidecar\build\classes\java\main'
$JavaSrc = Join-Path $Work 'java\src'
$JavaCls = Join-Path $Work 'java\classes'
if (-not $SkipConvert) {
    Step "Convert COBOL -> Java via $Sidecar"
    try { Invoke-RestMethod "$Sidecar/health" | Out-Null } catch { throw "sidecar not reachable at $Sidecar (run tools\Start-Sidecar.ps1)" }
    Reset-Dir $JavaSrc | Out-Null
    $bridge = Join-Path $Repo 'MlCpu\src\MlCpu.JavaBridge'
    dotnet build $bridge -v q -nologo | Out-Null
    foreach ($p in $Programs) {
        $line = dotnet run --no-build --project $bridge -- --file (Join-Path $CardDemo "cbl\$p.cbl") --spill-dir $JavaSrc --sidecar $Sidecar
        Write-Host "   $line"
        if ($line -notmatch 'verdict=TRANSPILED') { throw "conversion failed for $p" }
    }
}
Reset-Dir $JavaCls | Out-Null
$javacOut = & { $ErrorActionPreference = 'Continue'; & (Join-Path $env:JAVA_HOME 'bin\javac.exe') -nowarn -encoding UTF-8 -d $JavaCls -cp $Rt (Get-ChildItem $JavaSrc -Filter *.java).FullName 2>&1 | ForEach-Object { "$_" } }
if ($LASTEXITCODE) { $javacOut | Write-Host; throw 'javac failed on generated sources' }
$Cp = "$Rt;$JavaCls"

# ------------------------------------------------------------------ 2. shared inputs
Step 'Prepare shared inputs'
$Common = Reset-Dir (Join-Path $Work 'common')
$ascii = Join-Path $CardDemo 'data\ASCII'
Invoke-Java -cp $Rt com.mlcpu.rt.DataPrep fixed --in (Join-Path $ascii 'dailytran.txt') --out (Join-Path $Common 'DALYTRAN.dat') --lrecl 350
Set-Content (Join-Path $Common 'dateparm.txt') '2022-01-01 2022-07-06' -NoNewline -Encoding ascii
Invoke-Java -cp $Rt com.mlcpu.rt.DataPrep fixed --in (Join-Path $Common 'dateparm.txt') --out (Join-Path $Common 'DATEPARM.dat') --lrecl 80
foreach ($k in $Ksds) {
    $img = Join-Path $Common "$($k.Name).ksds"
    if ($k.Src) { Invoke-Java -cp $Rt com.mlcpu.rt.DataPrep ksds --in (Join-Path $ascii $k.Src) --out $img --lrecl $k.Lrecl --key $k.Key }
    else { New-Item -ItemType File -Force $img | Out-Null }
}

# ------------------------------------------------------------------ 3. GnuCOBOL oracle build
Step 'Build GnuCOBOL oracle'
# Executables are reused while their content hash (sources, copybooks, stubs, flags, compiler) is unchanged.
$Ob = Join-Path $Work 'oracle\bin'
New-Item -ItemType Directory -Force $Ob | Out-Null
$cobFlags = @('-std=ibm', '-fsign=EBCDIC', '-fstatic-call', '-Wno-others', '-I', (Join-Path $CardDemo 'cpy'))
$ManifestPath = Join-Path $Ob 'build-manifest.json'
$Built = @{}
if ((Test-Path $ManifestPath) -and -not $Rebuild) { (Get-Content $ManifestPath -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $Built[$_.Name] = $_.Value } }
$CobcVersion = (Invoke-Cobc --version).Out.Split("`n")[0]
$Sha = [Security.Cryptography.SHA256]::Create()
function Get-Hash([string[]]$texts) { [BitConverter]::ToString($Sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($texts -join [char]0)))).Replace('-', '').ToLowerInvariant() }
$CopybookHash = Get-Hash @(Get-ChildItem (Join-Path $CardDemo 'cpy') -File | Sort-Object Name | ForEach-Object { $_.Name + [IO.File]::ReadAllText($_.FullName) })
$script:Reused = 0; $script:Compiled = 0
function Build-Exe([string]$id, [string]$exe, [string[]]$srcs) {
    $hash = Get-Hash (@($CobcVersion, ($cobFlags -join ' '), $CopybookHash) + @($srcs | ForEach-Object { [IO.File]::ReadAllText($_) }))
    if ($Built[$id] -eq $hash -and (Test-Path $exe)) { $script:Reused++; return }
    $r = Invoke-Cobc -x @cobFlags -o $exe @srcs
    if ($r.Code) { Write-Host $r.Out; throw "cobc failed for $id" }
    $Built[$id] = $hash; $script:Compiled++
}
@"
       IDENTIFICATION DIVISION.
       PROGRAM-ID. CEE3ABD.
       DATA DIVISION.
       LINKAGE SECTION.
       01 LK-CODE   PIC S9(9) BINARY.
       01 LK-TIMING PIC S9(9) BINARY.
       PROCEDURE DIVISION USING LK-CODE LK-TIMING.
           DISPLAY 'ABEND CEE3ABD'
           MOVE 16 TO RETURN-CODE
           STOP RUN.
"@ | Set-Content (Join-Path $Ob 'CEE3ABD.cbl') -Encoding ascii
@"
       IDENTIFICATION DIVISION.
       PROGRAM-ID. DRVPARM.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01 PARM-AREA.
          05 PARM-LEN   PIC S9(4) COMP.
          05 PARM-TEXT  PIC X(100).
       01 ENV-PARM      PIC X(100).
       PROCEDURE DIVISION.
           ACCEPT ENV-PARM FROM ENVIRONMENT 'MLCPU_PARM'
           MOVE ENV-PARM TO PARM-TEXT
           MOVE FUNCTION LENGTH(FUNCTION TRIM(ENV-PARM)) TO PARM-LEN
           CALL 'CBACT04C' USING PARM-AREA
           STOP RUN.
"@ | Set-Content (Join-Path $Ob 'DRVPARM.cbl') -Encoding ascii

function New-KsdsUtil([hashtable]$k, [string]$kind) {
    # LOAD: fixed image -> INDEXED; UNLOAD: INDEXED -> fixed image. Keys exactly as the programs declare them.
    $segs = @(@{ Pos = [int]($k.Key.Split(',')[0]); Len = [int]($k.Key.Split(',')[1]); Name = 'OUT-KEY' })
    if ($k.Alt) { $segs += @{ Pos = [int]($k.Alt.Split(',')[0]); Len = [int]($k.Alt.Split(',')[1]); Name = 'OUT-ALT' } }
    $segs = @($segs | Sort-Object { $_.Pos })
    $fields = @(); $cur = 1
    foreach ($s in $segs) {
        if ($s.Pos -gt $cur) { $fields += "           05 FILLER PIC X($($s.Pos - $cur))." }
        $fields += "           05 $($s.Name) PIC X($($s.Len))."
        $cur = $s.Pos + $s.Len
    }
    if ($cur -le $k.Lrecl) { $fields += "           05 FILLER PIC X($($k.Lrecl - $cur + 1))." }
    $alt = if ($k.Alt) { "`n               ALTERNATE RECORD KEY IS OUT-ALT" } else { '' }
    $id = ($kind.Substring(0, 2) + $k.Name).Substring(0, [Math]::Min(8, 2 + $k.Name.Length))
    $load = $kind -eq 'LOAD'
    @"
       IDENTIFICATION DIVISION.
       PROGRAM-ID. $id.
       ENVIRONMENT DIVISION.
       INPUT-OUTPUT SECTION.
       FILE-CONTROL.
           SELECT SEQ-FILE ASSIGN TO SEQFILE
               ORGANIZATION IS SEQUENTIAL
               FILE STATUS IS WS-SEQ.
           SELECT OUT-FILE ASSIGN TO KSDSFILE
               ORGANIZATION IS INDEXED
               ACCESS MODE IS DYNAMIC
               RECORD KEY IS OUT-KEY$alt
               FILE STATUS IS WS-FS.
       DATA DIVISION.
       FILE SECTION.
       FD SEQ-FILE.
       01 SEQ-REC PIC X($($k.Lrecl)).
       FD OUT-FILE.
       01 OUT-REC.
$($fields -join "`n")
       WORKING-STORAGE SECTION.
       01 WS-SEQ PIC XX.
       01 WS-FS  PIC XX.
       01 WS-N   PIC 9(9) VALUE 0.
       01 WS-EOF PIC X VALUE 'N'.
       PROCEDURE DIVISION.
           OPEN $(if ($load) { 'INPUT SEQ-FILE OUTPUT OUT-FILE' } else { 'OUTPUT SEQ-FILE INPUT OUT-FILE' })
           PERFORM UNTIL WS-EOF = 'Y'
               READ $(if ($load) { 'SEQ-FILE' } else { 'OUT-FILE NEXT' })
                   AT END MOVE 'Y' TO WS-EOF
                   NOT AT END
                       ADD 1 TO WS-N
                       $(if ($load) { 'WRITE OUT-REC FROM SEQ-REC' } else { 'WRITE SEQ-REC FROM OUT-REC' })
                       IF $(if ($load) { 'WS-FS' } else { 'WS-SEQ' }) NOT = '00'
                           DISPLAY 'WRITE FAILED ' WS-FS ' ' WS-SEQ
                           MOVE 8 TO RETURN-CODE
                       END-IF
               END-READ
           END-PERFORM
           CLOSE SEQ-FILE OUT-FILE
           GOBACK.
"@
}

$utilExe = @{}
foreach ($k in $Ksds) {
    foreach ($kind in 'LOAD', 'UNLOAD') {
        $src = Join-Path $Ob "$kind-$($k.Name).cbl"
        New-KsdsUtil $k $kind | Set-Content $src -Encoding ascii
        $exe = Join-Path $Ob "$kind-$($k.Name).exe"
        Build-Exe "$kind-$($k.Name)" $exe @($src)
        $utilExe["$kind-$($k.Name)"] = $exe
    }
}
foreach ($p in $Programs) {
    $exe = Join-Path $Ob "$p.exe"
    $srcs = @((Join-Path $CardDemo "cbl\$p.cbl"), (Join-Path $Ob 'CEE3ABD.cbl'))
    if ($p -eq 'CBACT04C') { $srcs = @((Join-Path $Ob 'DRVPARM.cbl')) + $srcs }
    Build-Exe $p $exe $srcs
    Write-Host "   $p.exe"
}
$Built | ConvertTo-Json | Set-Content $ManifestPath -Encoding utf8
Write-Host "   GnuCOBOL: $($script:Compiled) compiled, $($script:Reused) reused from the cache ($CobcVersion)"

# ------------------------------------------------------------------ 4. run both sides
function Invoke-Native([string]$exe, [string[]]$exeArgs, [string]$stdout, [hashtable]$envVars) {
    foreach ($e in $envVars.GetEnumerator()) { Set-Item "env:$($e.Key)" $e.Value }
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo $exe
        $psi.Arguments = ($exeArgs | ForEach-Object { Quote-Arg $_ }) -join ' '
        $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
        $psi.StandardOutputEncoding = $L1
        $proc = [System.Diagnostics.Process]::Start($psi)
        $errTask = $proc.StandardError.ReadToEndAsync()
        $o = $proc.StandardOutput.ReadToEnd(); $proc.WaitForExit(); $err = $errTask.Result
        if ($stdout) { [IO.File]::WriteAllText($stdout, $o, $L1) }
        if ($err.Trim()) { Write-Host "   stderr: $($err.Trim())" -ForegroundColor Yellow }
        return $proc.ExitCode
    } finally { foreach ($e in $envVars.GetEnumerator()) { Remove-Item "env:$($e.Key)" -ErrorAction SilentlyContinue } }
}

function Invoke-Side([string]$side) {
    $run = Reset-Dir (Join-Path $Work "$side\run")
    $sys = Reset-Dir (Join-Path $Work "$side\sysout")
    Copy-Item (Join-Path $Common '*') $run
    $rcs = [ordered]@{}
    if ($side -eq 'oracle') {
        foreach ($k in $Ksds) {
            $img = Join-Path $run "$($k.Name).ksds"; $bdb = Join-Path $run "$($k.Name).bdb"
            $rc = Invoke-Native $utilExe["LOAD-$($k.Name)"] @() $null @{ DD_SEQFILE = $img; DD_KSDSFILE = $bdb }
            if ($rc) { throw "oracle load failed for $($k.Name)" }
        }
    }
    function DsPath([string]$ds) {
        if ($side -eq 'oracle' -and $ds.EndsWith('.ksds')) { return (Join-Path $run ($ds -replace '\.ksds$', '.bdb')) }
        return (Join-Path $run $ds)
    }
    function Unload([string]$name) {
        # Oracle KSDS -> fixed image (Java's KSDS image already is one).
        if ($side -ne 'oracle') { return (Join-Path $run "$name.ksds") }
        $img = Join-Path $run "$name.unl"
        $rc = Invoke-Native $utilExe["UNLOAD-$name"] @() $null @{ DD_SEQFILE = $img; DD_KSDSFILE = (DsPath "$name.ksds") }
        if ($rc) { throw "oracle unload failed for $name" }
        return $img
    }
    foreach ($s in $Steps) {
        if ($s.Util -eq 'COMBTRAN') {
            $bk = Unload 'TRANSACT'
            Copy-Item $bk (Join-Path $run 'TRANSACT.BKUP.dat') -Force
            Invoke-Java -cp $Rt com.mlcpu.rt.SortReplay --card $SortComb --sortin "$(Join-Path $run 'TRANSACT.BKUP.dat'),$(Join-Path $run 'SYSTRAN.dat')" --lrecl 350 --out (Join-Path $run 'TRANSACT.COMBINED.dat')
            if ($side -eq 'oracle') {
                Remove-Item (DsPath 'TRANSACT.ksds') -Force -ErrorAction SilentlyContinue
                $rc = Invoke-Native $utilExe['LOAD-TRANSACT'] @() $null @{ DD_SEQFILE = (Join-Path $run 'TRANSACT.COMBINED.dat'); DD_KSDSFILE = (DsPath 'TRANSACT.ksds') }
            } else {
                Invoke-Java -cp $Rt com.mlcpu.rt.DataPrep ksds --fixed-in --in (Join-Path $run 'TRANSACT.COMBINED.dat') --out (Join-Path $run 'TRANSACT.ksds') --lrecl 350 --key '1,16'
                $rc = 0
            }
            $rcs[$s.Id] = $rc
            continue
        }
        if ($s.Pre -eq 'TRANREPT') {
            $bk = Unload 'TRANSACT'
            $cards = @(); foreach ($c in $SortRept) { $cards += '--card'; $cards += $c }
            Invoke-Java -cp $Rt com.mlcpu.rt.SortReplay @cards --sortin $bk --lrecl 350 --out (Join-Path $run 'TRANDALY.dat')
        }
        $sysout = Join-Path $sys "$($s.Id).txt"
        if ($side -eq 'oracle') {
            $envs = @{ COB_CURRENT_DATE = $CobDate }
            foreach ($d in $s.DD.GetEnumerator()) { $envs["DD_$($d.Key)"] = DsPath $d.Value }
            if ($s.Parm) { $envs['MLCPU_PARM'] = $s.Parm }
            $rc = Invoke-Native (Join-Path $Ob "$($s.Pgm).exe") @() $sysout $envs
        } else {
            $a = @('-cp', $Cp, 'com.mlcpu.rt.StepRunner', '--program', $s.Pgm, '--sysout', $sysout, '--clock', "FIXED=$Epoch")
            foreach ($d in $s.DD.GetEnumerator()) { $a += '--dd'; $a += "$($d.Key)=$(DsPath $d.Value)" }
            if ($s.Parm) { $a += '--parm'; $a += $s.Parm }
            $rc = Invoke-Native (Join-Path $env:JAVA_HOME 'bin\java.exe') $a $null @{ MLCPU_DISPLAY_NUMERIC = 'gnucobol'; MLCPU_BINARY_TRUNCATE = 'no'; MLCPU_DEFAULT_BYTE = '0' }
        }
        $rcs[$s.Id] = $rc
        Write-Host ("   {0,-7} {1,-12} rc={2}" -f $side, $s.Id, $rc)
        foreach ($o in $s.Out) {
            $name = $o -replace '\.(ksds|dat)$', ''
            $src = if ($o.EndsWith('.ksds')) { Unload $name } else { Join-Path $run $o }
            Copy-Item $src (Join-Path $sys "$($s.Id).$o") -Force
        }
    }
    foreach ($s in $Steps | Where-Object { $_.Util }) {
        foreach ($o in $s.Out) { Copy-Item (Unload ($o -replace '\.ksds$', '')) (Join-Path $sys "$($s.Id).$o") -Force }
    }
    return $rcs
}

Step 'Run GnuCOBOL oracle'
$rcOracle = Invoke-Side 'oracle'
Step 'Run generated Java'
$rcJava = Invoke-Side 'java'

# ------------------------------------------------------------------ 5. compare
Step 'Compare'
$results = @()
$oSys = Join-Path $Work 'oracle\sysout'; $jSys = Join-Path $Work 'java\sysout'
foreach ($s in $Steps) {
    $results += [pscustomobject]@{ Step = $s.Id; Artifact = 'RETURN-CODE'; Verdict = $(if ($rcOracle[$s.Id] -eq $rcJava[$s.Id]) { 'MATCH' } else { 'DIFFER' }); Detail = "oracle=$($rcOracle[$s.Id]) java=$($rcJava[$s.Id])" }
    if (-not $s.Util) {
        $a = [IO.File]::ReadAllText((Join-Path $oSys "$($s.Id).txt"), $L1) -split "`r?`n"
        $b = [IO.File]::ReadAllText((Join-Path $jSys "$($s.Id).txt"), $L1) -split "`r?`n"
        $diff = $null
        for ($i = 0; $i -lt [Math]::Max($a.Count, $b.Count); $i++) {
            if ($a[$i] -cne $b[$i]) { $diff = "line $($i+1): oracle=[$($a[$i])] java=[$($b[$i])]"; break }
        }
        $results += [pscustomobject]@{ Step = $s.Id; Artifact = 'SYSOUT'; Verdict = $(if ($diff) { 'DIFFER' } else { 'MATCH' }); Detail = $(if ($diff) { $diff } else { "$($a.Count) lines" }) }
    }
    foreach ($o in $s.Out) {
        $lrecl = if ($o -like 'DALYREJS*') { 430 } elseif ($o -like 'TRANREPT*') { 133 } elseif ($o.EndsWith('.ksds')) { $KsdsByName[($o -replace '\.ksds$', '')].Lrecl } else { 350 }
        $x = [IO.File]::ReadAllBytes((Join-Path $oSys "$($s.Id).$o")); $y = [IO.File]::ReadAllBytes((Join-Path $jSys "$($s.Id).$o"))
        $detail = "$($x.Length / $lrecl) records"
        $verdict = 'MATCH'
        $at = First-Diff $x $y
        if ($at -ge 0) {
            $verdict = 'DIFFER'
            $rec = [Math]::Floor($at / $lrecl); $col = $at % $lrecl
            $ox = if ($rec * $lrecl -lt $x.Length) { $L1.GetString($x, $rec * $lrecl, [Math]::Min($lrecl, $x.Length - $rec * $lrecl)) } else { '<eof>' }
            $oy = if ($rec * $lrecl -lt $y.Length) { $L1.GetString($y, $rec * $lrecl, [Math]::Min($lrecl, $y.Length - $rec * $lrecl)) } else { '<eof>' }
            $detail = "sizes oracle=$($x.Length) java=$($y.Length); first difference record $($rec+1) column $($col+1)`n      oracle: [$ox]`n      java  : [$oy]"
        }
        $results += [pscustomobject]@{ Step = $s.Id; Artifact = $o; Verdict = $verdict; Detail = $detail }
    }
}
$results | Format-Table Step, Artifact, Verdict, @{ n = 'Detail'; e = { ($_.Detail -split "`n")[0] } } -AutoSize | Out-String -Width 220 | Write-Host
$md = @("# CardDemo batch parity - GnuCOBOL oracle vs generated Java", "", "Clock pinned at $Epoch. Oracle: GnuCOBOL $((Invoke-Cobc --version).Out.Split("`n")[0]) (-std=ibm -fsign=EBCDIC).", "",
    "| Step | Artifact | Verdict | Detail |", "|---|---|---|---|")
foreach ($r in $results) { $md += "| $($r.Step) | $($r.Artifact) | $($r.Verdict) | $(($r.Detail -replace "`n", '<br>') -replace '\|', '\|') |" }
$md | Set-Content (Join-Path $Work 'parity-report.md') -Encoding utf8
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Work 'parity-report.json') -Encoding utf8
$bad = @($results | Where-Object Verdict -ne 'MATCH').Count
Write-Host ("{0} comparisons, {1} differ. Report: {2}" -f $results.Count, $bad, (Join-Path $Work 'parity-report.md'))
exit ([int]($bad -gt 0))
