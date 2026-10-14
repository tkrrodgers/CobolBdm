<#
.SYNOPSIS
  The Phase 0 gate of the loans forward engineering (ForwardEngineeringFineract.md section 7.7; forward\cas\tools\Test-ForwardGate.ps1
  generalized). The runner refuses to start without a passing gate.json:
    * the reference passes every acceptance scenario of every slice (privileged ones included) and the architecture rules;
    * each context module reaches a mutation score of at least 85% (PIT, the whole acceptance suite as tests);
    * every digest rule is tagged on at least one scenario, and every rule with three or more scenarios has a public one;
    * every rule's mutant is killed (the SME pools' reports: no rule without a killing scenario);
    * the leak checker passes on every package file;
    * the cross-check: the reference equals every loan of the S5 pool (whose expected values already carry the decisions D-n).
  A' against A (only the D-2 loans differ) is JavaToCOBOL's evidence (its docs/APRIME.md); the gate records it.
  Result: forward\loans\gate.json.
#>
param([string]$Work = (Join-Path $env:TEMP 'loans-gate'), [switch]$SkipCrossCheck)
$ErrorActionPreference = 'Stop'
$loans = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repo = (Resolve-Path (Join-Path $loans '..\..')).Path
if (Test-Path $Work) { Remove-Item -Recurse -Force $Work }
# the previous result is not package material to check: it is what this run writes
$previous = Join-Path $loans 'gate.json'
if (Test-Path $previous) { Remove-Item -Force $previous }
New-Item -ItemType Directory -Force $Work | Out-Null

function Copy-Tree([string]$from, [string]$to) {
    New-Item -ItemType Directory -Force $to | Out-Null
    Get-ChildItem $from -Force | Where-Object { $_.Name -notin @('build', '.gradle') } | ForEach-Object { Copy-Item $_.FullName -Destination $to -Recurse -Force }
}

function Invoke-Gradle([string]$dir, [string]$tasks) {
    Push-Location $dir
    try {
        $ErrorActionPreference = 'Continue'
        & cmd.exe /c "gradle $tasks --offline --no-daemon --console=plain -q" 2>&1 | ForEach-Object { "$_" } | Select-Object -Last 20
        return $LASTEXITCODE
    } finally {
        $ErrorActionPreference = 'Stop'
        Pop-Location
    }
}

function Test-Results([string]$dir) {
    $total = 0; $failed = 0
    foreach ($f in Get-ChildItem $dir -Filter 'TEST-*.xml' -ErrorAction SilentlyContinue) {
        [xml]$x = Get-Content $f.FullName -Raw
        foreach ($tc in $x.testsuite.testcase) {
            if ($tc.skipped) { continue }
            $total++
            if ($tc.failure -or $tc.error) { $failed++ }
        }
    }
    [ordered]@{ total = $total; failed = $failed }
}

$manifest = (Get-Content (Join-Path $loans 'scenarios.json') -Raw | ConvertFrom-Json).scenarios
$slices = (Get-Content (Join-Path $loans 'forward.json') -Raw | ConvertFrom-Json).slices | ForEach-Object { $_.name }

# 1. the reference against the whole suite
$service = Join-Path $Work 'service'
Copy-Tree (Join-Path $loans 'service') $service
$reference = (Join-Path $loans 'reference').Replace('\', '/')
Invoke-Gradle $service ":loans-architecture:test :loans-acceptance:test -PimplDir=$reference -PloansSlices=$($slices -join ',')" | Out-Null
$architecture = Test-Results (Join-Path $service 'loans-architecture\build\test-results\test')
$acceptance = Test-Results (Join-Path $service 'loans-acceptance\build\test-results\test')
Write-Host "reference: architecture $($architecture.total - $architecture.failed)/$($architecture.total), scenarios $($acceptance.total - $acceptance.failed)/$($acceptance.total) of $($manifest.Count)"

# 2. mutation score per context module (one flat project: PIT needs the code and the tests together)
$contexts = [ordered]@{ calendar = 'loans-calendar'; rates = 'loans-rates'; terms = 'loans-terms'; schedule = 'loans-schedule'; disclosure = 'loans-disclosure'; application = 'loans-application' }
$pit = Join-Path $Work 'mutation'
New-Item -ItemType Directory -Force (Join-Path $pit 'src\main\java'), (Join-Path $pit 'src\test\java'), (Join-Path $pit 'src\test\resources') | Out-Null
Copy-Item (Join-Path $loans 'service\loans-api\src\main\java\*') -Destination (Join-Path $pit 'src\main\java') -Recurse -Force
foreach ($m in @('loans-kernel') + @($contexts.Values)) {
    Copy-Item (Join-Path $loans "reference\$m\src\main\java\*") -Destination (Join-Path $pit 'src\main\java') -Recurse -Force
}
Copy-Item (Join-Path $loans 'service\loans-acceptance\src\test\java\*') -Destination (Join-Path $pit 'src\test\java') -Recurse -Force
Copy-Item (Join-Path $loans 'service\loans-acceptance\src\memory\java\*') -Destination (Join-Path $pit 'src\test\java') -Recurse -Force
Copy-Item (Join-Path $loans 'service\loans-acceptance\src\test\resources\*') -Destination (Join-Path $pit 'src\test\resources') -Recurse -Force
$targets = ($contexts.Keys | ForEach-Object { "`"com.mlcpu.loans.$_.*`"" }) -join ', '
Set-Content (Join-Path $pit 'settings.gradle.kts') 'rootProject.name = "loans-mutation-gate"' -Encoding ASCII
Set-Content (Join-Path $pit 'build.gradle.kts') -Encoding ASCII -Value @"
plugins {
    java
    id("info.solidsoft.pitest") version "1.19.0"
}
repositories { mavenCentral() }
java { sourceCompatibility = JavaVersion.VERSION_21; targetCompatibility = JavaVersion.VERSION_21 }
dependencies {
    testImplementation(platform("org.junit:junit-bom:5.11.3"))
    testImplementation("org.junit.jupiter:junit-jupiter")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
    testImplementation("com.fasterxml.jackson.core:jackson-databind:2.22.1")
}
tasks.withType<JavaCompile>().configureEach { options.encoding = "UTF-8" }
tasks.test { useJUnitPlatform(); jvmArgs("-Xss16m") }
pitest {
    junit5PluginVersion.set("1.2.1")
    targetClasses.set(listOf($targets))
    targetTests.set(listOf("com.mlcpu.loans.acceptance.*"))
    threads.set(16)
    jvmArgs.set(listOf("-Xss16m"))
    outputFormats.set(listOf("XML"))
    timestampedReports.set(false)
    timeoutConstInMillis.set(60000)
}
"@
Invoke-Gradle $pit 'pitest' | Out-Null
[xml]$mutations = Get-Content (Join-Path $pit 'build\reports\pitest\mutations.xml') -Raw
$all = @($mutations.mutations.mutation)
$byModule = [ordered]@{}
$mutationPass = $true
foreach ($c in $contexts.Keys) {
    $mine = @($all | Where-Object { $_.mutatedClass -like "com.mlcpu.loans.$c.*" })
    $killed = @($mine | Where-Object { $_.status -in @('KILLED', 'TIMED_OUT', 'MEMORY_ERROR') }).Count
    $score = 0
    if ($mine.Count) { $score = [math]::Round(100.0 * $killed / $mine.Count, 1) }
    if ($score -lt 85) { $mutationPass = $false }
    $byModule[$contexts[$c]] = [ordered]@{ mutants = $mine.Count; killed = $killed; scorePercent = $score }
    Write-Host "mutation score $($contexts[$c]): $score% ($killed of $($mine.Count))"
}

# 3. traceability and the public share
$docs = (Get-ChildItem (Join-Path $loans 'digests') -Filter *.md | ForEach-Object { Get-Content $_.FullName -Raw -Encoding UTF8 }) -join "`n"
$rules = @([regex]::Matches($docs, '\| ((?:MNY|DATE|CAL|RATE|TRM|AMZ|PLAN|RPT|DISC|SRV|IDEM)-\d\d) \|') | ForEach-Object { $_.Groups[1].Value }) | Sort-Object -Unique
$untraced = @($rules | Where-Object { $r = $_; -not ($manifest | Where-Object { $_.rules -contains $r }) })
$noPublic = @($rules | Where-Object {
        $r = $_; $with = @($manifest | Where-Object { $_.rules -contains $r })
        $with.Count -ge 3 -and -not ($with | Where-Object { $_.visibility -eq 'public' }) })
Write-Host "rules: $($rules.Count); untraced: $($untraced -join ', '); without a public scenario: $($noPublic -join ', ')"

# 4. every rule's mutant killed (the pools' reports list each rule with the scenarios that kill its mutant)
$unkilled = @()
foreach ($r in Get-ChildItem (Join-Path $loans 'sme\reports') -Filter '*-pool.md') {
    foreach ($line in Get-Content $r.FullName -Encoding UTF8) {
        if ($line -match '^\| ([A-Z]+-\d\d) \| [^|]+ \| 0 \|') { $unkilled += "$($Matches[1]) ($($r.Name))" }
    }
}
Write-Host "rules whose mutant no scenario kills: $($unkilled -join ', ')"

# 5. the leak checker
$dll = Join-Path $repo 'MlCpu\src\MlCpu.DesignModel\bin\Release\net10.0\MlCpu.DesignModel.dll'
$leakOut = & dotnet $dll fwd-leaks --cas $loans --out (Join-Path $Work 'leaks') 2>&1 | ForEach-Object { "$_" }
$leakLine = @($leakOut | Where-Object { $_ -match 'leak check of package' }) | Select-Object -Last 1
$leakHits = -1
if ($leakLine -match ': (\d+) hit') { $leakHits = [int]$Matches[1] }
Write-Host "leak checker: $leakLine"

# 6. the cross-check: the reference on every loan of the S5 pool
$crossCheck = [ordered]@{ run = $false }
if (-not $SkipCrossCheck) {
    $sme = Join-Path $loans 'sme'
    Invoke-Gradle $sme 'installDist' | Out-Null
    $lib = Join-Path $sme 'build\install\loans-sme\lib\*'
    $diffs = Join-Path $Work 'cross-check.txt'
    $out = & java -Xss64m -cp $lib com.mlcpu.loans.sme.PoolRunner (Join-Path $sme 'pool\schedule') 0 'jsonl' $diffs 2>&1 | ForEach-Object { "$_" }
    $summary = @($out | Where-Object { $_ -match '^TOTAL' }) | Select-Object -Last 1
    $differ = -1
    if ($summary -match '(\d+) differ') { $differ = [int]$Matches[1] }
    $crossCheck = [ordered]@{ run = $true; summary = $summary; differ = $differ }
    Write-Host "cross-check: $summary"
}

$pass = $architecture.failed -eq 0 -and $architecture.total -gt 0 -and $acceptance.failed -eq 0 -and $acceptance.total -eq $manifest.Count -and
        $mutationPass -and $untraced.Count -eq 0 -and $noPublic.Count -eq 0 -and $unkilled.Count -eq 0 -and $leakHits -eq 0 -and
        ($SkipCrossCheck -or $crossCheck.differ -eq 0)
$public = @($manifest | Where-Object { $_.visibility -eq 'public' }).Count
$privileged = @($manifest | Where-Object { $_.visibility -eq 'privileged' }).Count
[ordered]@{
    schema = 'mlcpu.loans-gate.v1'; checked = (Get-Date).ToString('o'); pass = $pass
    reference = [ordered]@{ architectureTests = $architecture; scenarios = $acceptance }
    mutation = [ordered]@{ threshold = 85; perModule = $byModule }
    traceability = [ordered]@{ rules = $rules.Count; untraced = $untraced; withoutPublicScenario = $noPublic; mutantsUnkilled = $unkilled }
    leaks = [ordered]@{ hits = $leakHits; line = $leakLine }
    crossCheck = $crossCheck
    aPrime = 'JavaToCOBOL docs/APRIME.md: A'' differs from A on exactly the 896 D-2 loans of 147,904'
    scenarios = [ordered]@{ total = $manifest.Count; public = $public; privileged = $privileged; hidden = $manifest.Count - $public - $privileged }
} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $loans 'gate.json') -Encoding UTF8
Write-Host ($(if ($pass) { 'GATE PASSED' } else { 'GATE FAILED' }))
if (-not $pass) { exit 1 }
