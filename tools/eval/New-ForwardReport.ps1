<#
.SYNOPSIS
  The forward-engineering report (tools\eval\PROTOCOL-forward.md): the design round (automated checks, the blind SME rubric,
  peer reviews, the chosen designs), the implementation arms S and X, and the decisions of section 8. Writes
  <evalRoot>\forward\REPORT.md and <evalRoot>\forward\results.json (read by the workbench's Forward Engineering page).
  Rubric scores are read from <evalRoot>\forward\blind\scores.json and mapped to models through blind-key\key.json.
#>
param([string]$Config)
$ErrorActionPreference = 'Stop'
if (-not $Config) { $Config = Join-Path $PSScriptRoot 'eval-config-forward.json' }
$cfg = Get-Content $Config -Raw | ConvertFrom-Json
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$cas = Join-Path $repo $cfg.cas
$fwd = Join-Path $cfg.evalRoot $cfg.forward
function J([string]$p) { if (Test-Path $p) { Get-Content $p -Raw | ConvertFrom-Json } else { $null } }
$weights = [ordered]@{ C1 = 0.25; C2 = 0.20; C3 = 0.15; C4 = 0.15; C5 = 0.15; C6 = 0.10 }
function Weighted($s) {
    if (-not $s) { return $null }
    $sum = 0.0
    foreach ($c in $weights.Keys) { if ($null -eq $s.$c) { return $null }; $sum += $weights[$c] * [double]($(if ($s.$c.score) { $s.$c.score } else { $s.$c })) }
    [math]::Round($sum, 2)
}

# ---- the service
$gate = J (Join-Path $cas 'gate.json')
$manifest = (J (Join-Path $cas 'scenarios.json')).scenarios
$decisions = ([regex]::Matches((Get-Content (Join-Path $cas 'SPEC.md') -Raw), '(?m)^\| D-\d+ \|')).Count
$service = [ordered]@{
    name = 'Customer Account Service'
    summary = 'Java 21, hexagonal: card accounts (posting, interest), customers, reporting and a new customer statement, from CBTRN02C, CBACT04C, CBTRN03C, SAM1 and SAM2.'
    scenarios = [ordered]@{ total = @($manifest).Count; public = @($manifest | Where-Object visibility -eq 'public').Count; hidden = @($manifest | Where-Object visibility -eq 'hidden').Count }
    gate = [ordered]@{ pass = [bool]$gate.pass; reference = "$($gate.reference.scenarios.total - $gate.reference.scenarios.failed)/$($gate.reference.scenarios.total)"; mutation = $gate.mutation.scorePercent }
    decisions = $decisions
}

# ---- the design round
$key = J (Join-Path $fwd 'blind-key\key.json')
$scores = J (Join-Path $fwd 'blind\scores.json')
$labelOf = @{}
if ($key) { foreach ($p in $key.labels.PSObject.Properties) { $labelOf["$($p.Value.model)|$($p.Value.seed)"] = $p.Name } }
$designModels = @($cfg.models | Where-Object { $_.roles -contains 'design' })
$designs = @()
foreach ($m in $designModels) {
    foreach ($seed in $cfg.design.seeds) {
        $dir = Join-Path $fwd "designs\$($m.name)\seed-$seed"
        $run = J (Join-Path $dir 'architecture.run.json')
        if (-not $run) { continue }
        $label = $labelOf["$($m.name)|$seed"]
        $sc = if ($scores -and $label) { $scores.designs.$label } else { $null }
        $problems = @($run.remainingProblems)
        $checks = @(
            $(if ($problems | Where-Object { $_ -match '^schema|^missing property|not a JSON' }) { 'schema FAIL' } else { 'schema ok' }),
            $(if ($problems | Where-Object { $_ -match '^useCases' }) { 'API methods FAIL' } else { 'API methods ok' }),
            $(if ($problems | Where-Object { $_ -match '^ruleTrace' }) { 'rules FAIL' } else { 'rules ok' }),
            $(if ($problems | Where-Object { $_ -match 'forbids|A-7' }) { 'dependencies FAIL' } else { 'dependencies ok' })) -join ', '
        $peer = @()
        foreach ($r in Get-ChildItem (Join-Path $fwd 'reviews') -Recurse -Filter "review-$label.json" -ErrorAction SilentlyContinue) {
            $rv = J $r.FullName
            if ($rv) { $peer += [pscustomobject]@{ by = $r.Directory.Name; weighted = (Weighted $rv.scores) } }
        }
        $designs += [pscustomobject][ordered]@{
            model = $m.name; seed = $seed; label = $label; folder = "designs\$($m.name)\seed-$seed"
            valid = [bool]$run.valid; attempts = @($run.attempts).Count; minutes = $run.minutes; checks = $checks
            rubric = if ($sc) { [ordered]@{ weighted = (Weighted $sc); C1 = $sc.C1.score; C2 = $sc.C2.score; C3 = $sc.C3.score; C4 = $sc.C4.score; C5 = $sc.C5.score; C6 = $sc.C6.score
                    justifications = [ordered]@{ C1 = $sc.C1.justification; C2 = $sc.C2.justification; C3 = $sc.C3.justification; C4 = $sc.C4.justification; C5 = $sc.C5.justification; C6 = $sc.C6.justification } } } else { $null }
            peerReview = if ($peer.Count) { ($peer | ForEach-Object { "$($_.weighted) by $($_.by)" }) -join '; ' } else { '' }
            peerWeighted = if ($peer.Count) { ($peer | Measure-Object weighted -Average).Average } else { $null }
            chosen = $false
        }
    }
}
$designers = @()
foreach ($m in $designModels) {
    $mine = @($designs | Where-Object model -eq $m.name)
    $scored = @($mine | Where-Object { $_.rubric })
    $best = $scored | Sort-Object { -$_.rubric.weighted }, seed | Select-Object -First 1
    $chosenSeed = if (Test-Path (Join-Path $fwd "designs\$($m.name)\chosen.txt")) { [int](Get-Content (Join-Path $fwd "designs\$($m.name)\chosen.txt") -Raw).Trim() } elseif ($best) { $best.seed } else { $null }
    foreach ($d in $mine) { if ($d.seed -eq $chosenSeed) { $d.chosen = $true } }
    $designers += [pscustomobject][ordered]@{
        model = $m.name
        meanRubric = if ($scored.Count) { [math]::Round(($scored | ForEach-Object { $_.rubric.weighted } | Measure-Object -Average).Average, 2) } else { $null }
        valid = "$(@($mine | Where-Object valid).Count)/$($mine.Count)"
        meanPeerReview = if (@($mine | Where-Object { $null -ne $_.peerWeighted }).Count) { [math]::Round((@($mine | Where-Object { $null -ne $_.peerWeighted }) | Measure-Object peerWeighted -Average).Average, 2) } else { $null }
        chosenSeed = $chosenSeed
    }
}

# ---- the implementation arms
$arms = @()
foreach ($dir in Get-ChildItem (Join-Path $fwd 'implementation') -Directory -ErrorAction SilentlyContinue | Sort-Object Name) {
    $s = J (Join-Path $dir.FullName 'summary.json')
    if (-not $s) { continue }
    $f = $s.final
    $arms += [pscustomobject][ordered]@{
        arm = $s.arm; designer = $s.designer; implementer = $s.model; folder = "implementation\$($dir.Name)"
        slices = @($s.slices | ForEach-Object { [ordered]@{ slice = $_.slice; attempts = $_.attempts; bestAttempt = $_.bestAttempt; endedBy = $_.endedBy; minutes = $_.minutes } })
        final = if ($f) { [ordered]@{ architecture = "$($f.architecture.passed)/$($f.architecture.total)"; public = "$($f.public.passed)/$($f.public.total)"
                hidden = "$($f.hidden.passed)/$($f.hidden.total)"; hiddenRate = if ($f.hidden.total) { [math]::Round(100.0 * $f.hidden.passed / $f.hidden.total, 1) } else { 0 }
                architectureGreen = ($f.compiled -and $f.architecture.total -gt 0 -and $f.architecture.passed -eq $f.architecture.total) } } else { $null }
        attempts = (@($s.slices) | Measure-Object attempts -Sum).Sum; minutes = $s.minutes; passed = [bool]$s.passed; exact = [bool]$s.exact; complete = [bool]$f
    }
}

# ---- section 8
$gemma = 'gemma-4-31B-it'; $challenger = 'gpt-oss-120b'
$S = $arms | Where-Object { $_.arm -eq 'S' } | Select-Object -First 1
$X = $arms | Where-Object { $_.arm -eq 'X' } | Select-Object -First 1
$gScore = ($designers | Where-Object model -eq $gemma).meanRubric
$cScore = ($designers | Where-Object model -eq $challenger).meanRubric
$lines = @()
$forwardPass = [bool](@($arms | Where-Object passed).Count)
$lines += if ($forwardPass) { "Gemma passes forward engineering (arm(s) $((@($arms | Where-Object passed) | ForEach-Object arm) -join ', ')): the Forward Engineering tab is added." } else { 'Gemma does not pass forward engineering in any arm: no Forward Engineering tab.' }
$designer = $gemma
if ($null -ne $gScore -and $null -ne $cScore -and $S -and $X -and $S.final -and $X.final) {
    $margin = [math]::Round($cScore - $gScore, 2)
    $implementable = $X.final.architectureGreen -and $X.final.hiddenRate -ge $S.final.hiddenRate
    $lines += "Rubric: $challenger $cScore against $gemma $gScore (margin $margin; the role needs at least 0.5)."
    $lines += "Implementability: arm X (Gemma codes $challenger's design) hidden $($X.final.hiddenRate)% against arm S (its own) $($S.final.hiddenRate)%; architecture tests $(if ($X.final.architectureGreen) { 'green' } else { 'not green' }) in X."
    if ($margin -ge 0.5 -and $implementable) { $designer = $challenger; $lines += "$challenger becomes the designer in forward engineering; Gemma implements." }
    elseif ($margin -ge 0.5 -or $implementable) { $lines += "No role change: only one of the two conditions holds." }
    else { $lines += 'No role change: Gemma designs and implements.' }
    $close = [math]::Abs($X.final.hiddenRate - $S.final.hiddenRate) -lt 10 -or ($X.final.hiddenRate -eq $S.final.hiddenRate -and [math]::Abs($X.attempts - $S.attempts) -lt 0.3 * [math]::Max($X.attempts, $S.attempts))
    if ($close) { $lines += 'Close result (hidden pass rates within 10 points, or attempts within 30% at equal rates): reruns with seeds 7 and 1234 are required before a conclusion.' }
} else { $lines += 'The design comparison is incomplete (scores or an arm are missing).' }

$results = [ordered]@{
    schema = 'mlcpu.forward-results.v1'; generated = (Get-Date).ToString('s'); protocol = 'tools/eval/PROTOCOL-forward.md (revision 3)'
    service = $service; designs = $designs; designers = $designers; arms = $arms
    decision = [ordered]@{ forwardPass = $forwardPass; designer = $designer; implementer = $gemma; lines = $lines }
}
$results | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $fwd 'results.json') -Encoding UTF8

# ---- REPORT.md
$md = New-Object System.Collections.Generic.List[string]
$md.Add('# Forward engineering: results'); $md.Add('')
$md.Add("Generated $(Get-Date -Format 'yyyy-MM-dd HH:mm'). Protocol: ``tools/eval/PROTOCOL-forward.md`` (revision 3). Package: ``forward/cas`` (gate: reference $($service.gate.reference), mutation $($service.gate.mutation)%; $($service.scenarios.total) scenarios, $($service.scenarios.public) public).")
$md.Add(''); $md.Add('## Decision'); $md.Add('')
foreach ($l in $lines) { $md.Add("* $l") }
$md.Add(''); $md.Add('## Design round'); $md.Add('')
$md.Add('| Model | Seed | Blind label | Valid (attempts) | Automated checks | Rubric | C1 | C2 | C3 | C4 | C5 | C6 | Peer review | Minutes | Chosen |')
$md.Add('|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|')
foreach ($d in $designs) {
    $r = $d.rubric
    $md.Add("| $($d.model) | $($d.seed) | $($d.label) | $(if ($d.valid) { "yes ($($d.attempts))" } else { 'no' }) | $($d.checks) | $(if ($r) { $r.weighted } else { '-' }) | $($r.C1) | $($r.C2) | $($r.C3) | $($r.C4) | $($r.C5) | $($r.C6) | $($d.peerReview) | $($d.minutes) | $(if ($d.chosen) { 'yes' }) |")
}
$md.Add(''); $md.Add('| Designer | Mean rubric | Valid | Mean peer review | Chosen seed |'); $md.Add('|---|---|---|---|---|')
foreach ($s in $designers) { $md.Add("| $($s.model) | $($s.meanRubric) | $($s.valid) | $($s.meanPeerReview) | $($s.chosenSeed) |") }
$md.Add(''); $md.Add('## Implementation (Gemma)'); $md.Add('')
$md.Add('| Arm | Design by | Slices: attempts (best; ended by) | Architecture | Public | Hidden | Attempts | Minutes | Result |'); $md.Add('|---|---|---|---|---|---|---|---|---|')
foreach ($a in $arms) {
    $sl = ($a.slices | ForEach-Object { "$($_.slice) $($_.attempts) ($($_.bestAttempt); $($_.endedBy))" }) -join '; '
    $md.Add("| $($a.arm) | $($a.designer) | $sl | $($a.final.architecture) | $($a.final.public) | $($a.final.hidden) ($($a.final.hiddenRate)%) | $($a.attempts) | $($a.minutes) | $(if ($a.exact) { 'exact' } elseif ($a.passed) { 'passed' } else { 'not passed' }) |")
}
Set-Content (Join-Path $fwd 'REPORT.md') $md -Encoding UTF8
Write-Host "wrote $fwd\REPORT.md and results.json"
