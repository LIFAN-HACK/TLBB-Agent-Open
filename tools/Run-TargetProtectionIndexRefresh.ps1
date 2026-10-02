param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$SampleRoot = "",
    [string]$IndexPath = ""
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if ([string]::IsNullOrWhiteSpace($SampleRoot)) {
    $SampleRoot = Join-Path $env:APPDATA "LongBanXing\samples\targets"
}

if ([string]::IsNullOrWhiteSpace($IndexPath)) {
    $IndexPath = Join-Path $env:APPDATA "LongBanXing\samples\target-protection-index.json"
}

$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
$diagProject = Join-Path $Root "tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = Join-Path $Root "artifacts\target-protection-index-refresh\$stamp"
$jsonPath = Join-Path $outDir "target-protection-index-refresh.json"
$mdPath = Join-Path $outDir "target-protection-index-refresh.md"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Invoke-Diag {
    param(
        [string]$Name,
        [string[]]$DiagArgs
    )

    $output = & $dotnet @DiagArgs 2>&1
    $exitCode = $LASTEXITCODE
    $text = ($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    $log = Join-Path $outDir "$Name.txt"
    Set-Content -LiteralPath $log -Value $text -Encoding UTF8

    [pscustomobject]@{
        name = $Name
        exitCode = $exitCode
        ok = $exitCode -eq 0
        text = $text
        log = $log
    }
}

$health = Invoke-Diag "combat-sample-health" @(
    "run", "--no-build", "--project", $diagProject, "--",
    "combat-sample-health", $SampleRoot
)

$next = Invoke-Diag "combat-sample-next" @(
    "run", "--no-build", "--project", $diagProject, "--",
    "combat-sample-next", $SampleRoot
)

$index = Invoke-Diag "target-protection-index" @(
    "run", "--no-build", "--project", $diagProject, "--",
    "target-protection-index", $SampleRoot, "--output", $IndexPath
)

$counts = [ordered]@{
    player = 0
    monster = 0
    boss = 0
    pet = 0
    none = 0
}
$required = [ordered]@{
    player = 3
    monster = 3
    boss = 2
    pet = 1
    none = 1
}
$recommendedReady = $false

if ($next.text -match "(?m)^ReadyCounts=(.+)$") {
    foreach ($part in ($Matches[1] -split ",")) {
        if ($part.Trim() -match "([a-z]+):(\d+)/(\d+)") {
            $counts[$Matches[1]] = [int]$Matches[2]
            $required[$Matches[1]] = [int]$Matches[3]
        }
    }
}

if ($next.text -match "(?m)^RecommendedReady=(True|False)$") {
    $recommendedReady = $Matches[1] -eq "True"
}

$indexSamples = 0
$indexSkipped = 0
if ($index.text -match "(?m)^Samples=(\d+), skipped=(\d+)\s*$") {
    $indexSamples = [int]$Matches[1]
    $indexSkipped = [int]$Matches[2]
}

$missing = New-Object System.Collections.ArrayList
foreach ($key in @("player", "monster", "boss", "pet", "none")) {
    $need = [Math]::Max(0, [int]$required[$key] - [int]$counts[$key])
    if ($need -gt 0) {
        [void]$missing.Add([pscustomobject]@{
            kind = $key
            ready = [int]$counts[$key]
            required = [int]$required[$key]
            missing = $need
        })
    }
}

$report = [pscustomobject]@{
    capturedAt = (Get-Date).ToString("o")
    mode = "target-protection-index-refresh-local-samples-only"
    safety = [pscustomobject]@{
        processAccess = $false
        sendsInput = $false
        clicksMouse = $false
        writesGameMemory = $false
        injectsDll = $false
        installsHook = $false
        modifiesGameFiles = $false
        modifiesSamples = $false
        writesLocalIndex = $true
    }
    sampleRoot = $SampleRoot
    indexPath = $IndexPath
    counts = $counts
    required = $required
    recommendedReady = $recommendedReady
    indexSamples = $indexSamples
    indexSkipped = $indexSkipped
    missing = $missing
    commands = [pscustomobject]@{
        health = $health.log
        next = $next.log
        index = $index.log
    }
}

$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# Target Protection Index Refresh")
[void]$md.AppendLine()
[void]$md.AppendLine("- CapturedAt: $($report.capturedAt)")
[void]$md.AppendLine("- SampleRoot: ``$SampleRoot``")
[void]$md.AppendLine("- IndexPath: ``$IndexPath``")
[void]$md.AppendLine("- RecommendedReady: $recommendedReady")
[void]$md.AppendLine("- IndexSamples: $indexSamples")
[void]$md.AppendLine("- IndexSkipped: $indexSkipped")
[void]$md.AppendLine("- Safety: no process access, no input, no click; writes local index only.")
[void]$md.AppendLine()
[void]$md.AppendLine("## Counts")
[void]$md.AppendLine()
[void]$md.AppendLine("| Kind | Ready | Required | Missing |")
[void]$md.AppendLine("|---|---:|---:|---:|")
foreach ($key in @("player", "monster", "boss", "pet", "none")) {
    $need = [Math]::Max(0, [int]$required[$key] - [int]$counts[$key])
    [void]$md.AppendLine("| $key | $($counts[$key]) | $($required[$key]) | $need |")
}
[void]$md.AppendLine()
[void]$md.AppendLine("## Evidence")
[void]$md.AppendLine("- JSON: ``$jsonPath``")
[void]$md.AppendLine("- Health log: ``$($health.log)``")
[void]$md.AppendLine("- Next log: ``$($next.log)``")
[void]$md.AppendLine("- Index log: ``$($index.log)``")
Set-Content -LiteralPath $mdPath -Value $md.ToString() -Encoding UTF8

Write-Host "Target protection index refresh"
Write-Host "RecommendedReady=$recommendedReady"
Write-Host ("Counts=player:{0}/{1}, monster:{2}/{3}, boss:{4}/{5}, pet:{6}/{7}, none:{8}/{9}" -f $counts.player, $required.player, $counts.monster, $required.monster, $counts.boss, $required.boss, $counts.pet, $required.pet, $counts.none, $required.none)
Write-Host "IndexSamples=$indexSamples"
Write-Host "IndexSkipped=$indexSkipped"
Write-Host "Index=$IndexPath"
Write-Host "Markdown=$mdPath"
Write-Host "Json=$jsonPath"

exit 0
