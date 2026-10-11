<#
.SYNOPSIS
  Blinding for the design round (tools\eval\PROTOCOL-forward.md section 5): every architecture of the design-track models
  (<evalRoot>\forward\designs\<model>\seed-<n>\architecture.json) is copied under a random label (D1, D2, ...) into
  <evalRoot>\forward\blind\designs, with model and lab names redacted. The key (label -> model, seed) goes to
  <evalRoot>\forward\blind-key\key.json, which is opened only after every score is recorded.
#>
param([string]$Config)
$ErrorActionPreference = 'Stop'
if (-not $Config) { $Config = Join-Path $PSScriptRoot 'eval-config-forward.json' }
$cfg = Get-Content $Config -Raw | ConvertFrom-Json
$fwd = Join-Path $cfg.evalRoot $cfg.forward
$blind = Join-Path $fwd 'blind\designs'
$keyDir = Join-Path $fwd 'blind-key'
if (Test-Path (Join-Path $keyDir 'key.json')) { throw "a blind set already exists ($keyDir\key.json); remove it first to draw a new one" }
New-Item -ItemType Directory -Force $blind, $keyDir | Out-Null

$designs = @()
foreach ($m in $cfg.models | Where-Object { $_.roles -contains 'design' }) {
    foreach ($seed in $cfg.design.seeds) {
        $f = Join-Path $fwd "designs\$($m.name)\seed-$seed\architecture.json"
        $designs += [pscustomobject]@{ model = $m.name; seed = $seed; path = $f; present = (Test-Path $f) }
    }
}
$randomSeed = Get-Random -Minimum 1 -Maximum 2000000000
$rng = New-Object System.Random $randomSeed
$order = @($designs | Sort-Object { $rng.Next() })
$key = [ordered]@{ schema = 'mlcpu.blind-key.v1'; created = (Get-Date).ToString('o'); randomSeed = $randomSeed; labels = [ordered]@{} }
$n = 0
foreach ($d in $order) {
    $n++
    $label = "D$n"
    $key.labels[$label] = [ordered]@{ model = $d.model; seed = $d.seed; source = $d.path; present = $d.present }
    if (-not $d.present) { continue }
    $text = Get-Content $d.path -Raw
    $text = [regex]::Replace($text, '(?i)gemma[\w.-]*|gpt-oss[\w.-]*|\bgpt\b|openai|google|deepmind|llama[\w.-]*|\bclaude\b|anthropic', '[model]')
    Set-Content (Join-Path $blind "$label.json") $text -Encoding UTF8
}
$key | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $keyDir 'key.json') -Encoding UTF8
Write-Host "$n design(s) labelled in $blind (missing ones keep their label in the key); key in $keyDir"
