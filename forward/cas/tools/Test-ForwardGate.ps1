<#
.SYNOPSIS
  The Phase 0 test-suite quality gate of the forward-engineering evaluation (tools\eval\PROTOCOL-forward.md section 4):
    * the reference implementation passes every acceptance scenario and every architecture test;
    * its context modules reach a mutation score of at least 85% (PIT, the whole suite as tests);
    * every rule of the digests and of SPEC.md is tagged on at least one scenario;
    * every rule with three or more scenarios has a public one.
  Result: forward\cas\gate.json.
#>
param([string]$Work = (Join-Path $env:TEMP 'cas-gate'))
$ErrorActionPreference = 'Stop'
$cas = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (Test-Path $Work) { Remove-Item -Recurse -Force $Work }

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

# 1. the reference implementation against the whole suite
$service = Join-Path $Work 'service'
Copy-Tree (Join-Path $cas 'service') $service
Copy-Item (Join-Path $cas 'reference\*') -Destination $service -Recurse -Force
Invoke-Gradle $service ':cas-architecture:test :cas-acceptance:test' | Out-Null
$architecture = Test-Results (Join-Path $service 'cas-architecture\build\test-results\test')
$acceptance = Test-Results (Join-Path $service 'cas-acceptance\build\test-results\test')
Write-Host "reference: architecture $($architecture.total - $architecture.failed)/$($architecture.total), scenarios $($acceptance.total - $acceptance.failed)/$($acceptance.total)"

# 2. mutation score of the context modules (one flat project: PIT needs the code and the tests together)
$pit = Join-Path $Work 'mutation'
New-Item -ItemType Directory -Force (Join-Path $pit 'src\main\java'), (Join-Path $pit 'src\test\java'), (Join-Path $pit 'src\test\resources') | Out-Null
foreach ($m in 'cas-api', 'cas-accounts', 'cas-customers', 'cas-reporting', 'cas-statements', 'cas-application') {
    Copy-Item (Join-Path $service "$m\src\main\java\*") -Destination (Join-Path $pit 'src\main\java') -Recurse -Force
}
Copy-Item (Join-Path $service 'cas-acceptance\src\test\java\*') -Destination (Join-Path $pit 'src\test\java') -Recurse -Force
Copy-Item (Join-Path $service 'cas-acceptance\src\test\resources\*') -Destination (Join-Path $pit 'src\test\resources') -Recurse -Force
Set-Content (Join-Path $pit 'settings.gradle.kts') 'rootProject.name = "cas-mutation-gate"' -Encoding ASCII
Set-Content (Join-Path $pit 'build.gradle.kts') -Encoding ASCII -Value @'
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
tasks.test { useJUnitPlatform() }
pitest {
    junit5PluginVersion.set("1.2.1")
    targetClasses.set(listOf("com.mlcpu.cas.accounts.*", "com.mlcpu.cas.customers.*", "com.mlcpu.cas.reporting.*", "com.mlcpu.cas.statements.*"))
    targetTests.set(listOf("com.mlcpu.cas.acceptance.*"))
    threads.set(32)
    outputFormats.set(listOf("XML"))
    timestampedReports.set(false)
    timeoutConstInMillis.set(20000)
}
'@
Invoke-Gradle $pit 'pitest' | Out-Null
[xml]$mutations = Get-Content (Join-Path $pit 'build\reports\pitest\mutations.xml') -Raw
$all = @($mutations.mutations.mutation)
$killed = @($all | Where-Object { $_.status -in @('KILLED', 'TIMED_OUT', 'MEMORY_ERROR') }).Count
$score = if ($all.Count) { [math]::Round(100.0 * $killed / $all.Count, 1) } else { 0 }
Write-Host "mutation score: $score% ($killed of $($all.Count) mutants killed)"

# 3. traceability and the public share
$docs = (Get-ChildItem (Join-Path $cas 'digests') -Filter *.md | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
$docs += "`n" + (Get-Content (Join-Path $cas 'SPEC.md') -Raw)
$rules = @([regex]::Matches($docs, '\| ((?:POST|INT|RPT|CUST|UPD|AVG)-\d\d) \|') | ForEach-Object { $_.Groups[1].Value }) +
         @([regex]::Matches($docs, '\*\*((?:STMT|IDEM)-\d\d):\*\*') | ForEach-Object { $_.Groups[1].Value }) | Sort-Object -Unique
$manifest = (Get-Content (Join-Path $cas 'scenarios.json') -Raw | ConvertFrom-Json).scenarios
$untraced = @($rules | Where-Object { $r = $_; -not ($manifest | Where-Object { $_.rules -contains $r }) })
$noPublic = @($rules | Where-Object {
        $r = $_; $with = @($manifest | Where-Object { $_.rules -contains $r })
        $with.Count -ge 3 -and -not ($with | Where-Object { $_.visibility -eq 'public' }) })
$public = @($manifest | Where-Object { $_.visibility -eq 'public' }).Count
Write-Host "rules: $($rules.Count); untraced: $($untraced -join ', '); without a public scenario: $($noPublic -join ', '); public $public of $($manifest.Count)"

$pass = $architecture.failed -eq 0 -and $architecture.total -gt 0 -and $acceptance.failed -eq 0 -and $acceptance.total -eq $manifest.Count -and
        $score -ge 85 -and $untraced.Count -eq 0 -and $noPublic.Count -eq 0
[ordered]@{
    schema = 'mlcpu.cas-gate.v1'; checked = (Get-Date).ToString('o'); pass = $pass
    reference = [ordered]@{ architectureTests = $architecture; scenarios = $acceptance }
    mutation = [ordered]@{ scope = 'cas-accounts, cas-customers, cas-reporting, cas-statements'; mutants = $all.Count; killed = $killed; scorePercent = $score; threshold = 85 }
    traceability = [ordered]@{ rules = $rules.Count; untraced = $untraced; withoutPublicScenario = $noPublic }
    scenarios = [ordered]@{ total = $manifest.Count; public = $public; hidden = $manifest.Count - $public }
} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $cas 'gate.json') -Encoding UTF8
Write-Host ($(if ($pass) { 'GATE PASSED' } else { 'GATE FAILED' }))
if (-not $pass) { exit 1 }
