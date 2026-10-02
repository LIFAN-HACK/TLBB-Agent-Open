param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$SampleRoot = "",
    [string]$CandidateRoot = ""
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
$diagProject = Join-Path $Root "tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = Join-Path $Root "artifacts\target-sample-readiness\$stamp"
$jsonPath = Join-Path $outDir "target-sample-readiness.json"
$mdPath = Join-Path $outDir "target-sample-readiness.md"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

if ([string]::IsNullOrWhiteSpace($CandidateRoot)) {
    $CandidateRoot = Join-Path $Root "artifacts"
}

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

$nextArgs = @("run", "--no-build", "--project", $diagProject, "--", "combat-sample-next")
if (-not [string]::IsNullOrWhiteSpace($SampleRoot)) {
    $nextArgs += $SampleRoot
}
$next = Invoke-Diag "combat-sample-next" $nextArgs

$candidateArgs = @("run", "--no-build", "--project", $diagProject, "--", "combat-sample-candidates", $CandidateRoot)
$candidates = Invoke-Diag "combat-sample-candidates" $candidateArgs

$nextJsonPath = ""
if ($next.text -match "(?m)^Evidence=(.+)$") {
    $nextJsonPath = $Matches[1].Trim()
}

$candidateJsonPath = ""
if ($candidates.text -match "(?m)^Evidence=(.+)$") {
    $candidateJsonPath = $Matches[1].Trim()
}

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

if (-not [string]::IsNullOrWhiteSpace($nextJsonPath) -and (Test-Path -LiteralPath $nextJsonPath)) {
    $nextData = Get-Content -LiteralPath $nextJsonPath -Encoding UTF8 -Raw | ConvertFrom-Json
    foreach ($key in @("player", "monster", "boss", "pet", "none")) {
        if ($nextData.counts.PSObject.Properties.Name -contains $key) {
            $counts[$key] = [int]$nextData.counts.$key
        }
    }
    $recommendedReady = $nextData.recommendedReady -eq $true
} elseif ($next.text -match "ReadyCounts=(.+)") {
    $line = $Matches[1]
    foreach ($part in $line -split ",") {
        if ($part.Trim() -match "([a-z]+):(\d+)/(\d+)") {
            $counts[$Matches[1]] = [int]$Matches[2]
            $required[$Matches[1]] = [int]$Matches[3]
        }
    }
}

$candidateGroups = @()
$reviewCandidates = @()
if (-not [string]::IsNullOrWhiteSpace($candidateJsonPath) -and (Test-Path -LiteralPath $candidateJsonPath)) {
    $candidateData = Get-Content -LiteralPath $candidateJsonPath -Encoding UTF8 -Raw | ConvertFrom-Json
    $candidateGroups = @(
        $candidateData.candidates |
            Group-Object SuggestedKind, Recommendation |
            Sort-Object Count -Descending |
            ForEach-Object {
                [pscustomobject]@{
                    count = $_.Count
                    group = $_.Name
                }
            }
    )
    $reviewCandidates = @(
        $candidateData.candidates |
            Where-Object { $_.Recommendation -in @("review-for-promotion", "protect-only-review") } |
            Select-Object -First 20 Name, SuggestedKind, Recommendation, Risk, Reason, SourcePath, ClientImagePath
    )
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

$hasPetCandidate = @($reviewCandidates | Where-Object { $_.SuggestedKind -eq "pet" }).Count -gt 0
$hasNoneCandidate = @($reviewCandidates | Where-Object { $_.SuggestedKind -eq "none" }).Count -gt 0
$safeAutoPromote = $false
$manualReviewUseful = $reviewCandidates.Count -gt 0

$nextSteps = New-Object System.Collections.ArrayList
if (-not $recommendedReady) {
    foreach ($item in $missing) {
        switch ($item.kind) {
            "player" { [void]$nextSteps.Add("Capture $($item.missing) more real player target samples, including empty mana/rage or different school marker.") }
            "boss" { [void]$nextSteps.Add("Capture $($item.missing) more boss/elite sample; do not reuse player-like duplicate evidence.") }
            "pet" { [void]$nextSteps.Add("Capture a pet target sample. Current history has no safe pet candidate.") }
            "none" { [void]$nextSteps.Add("Capture a no-target sample after clearing current target. Current history has no safe none candidate.") }
            default { [void]$nextSteps.Add("Capture $($item.missing) more $($item.kind) samples.") }
        }
    }
}
if ($manualReviewUseful) {
    [void]$nextSteps.Add("Review candidate HTML, but do not auto-promote duplicates or ambiguous player samples.")
}

$report = [pscustomobject]@{
    capturedAt = (Get-Date).ToString("o")
    mode = "target-sample-readiness-no-process-no-input-no-promotion"
    safety = [pscustomobject]@{
        processAccess = $false
        sendsInput = $false
        clicksMouse = $false
        promotesSamples = $false
        modifiesSampleLibrary = $false
        writesMemory = $false
        injectsDll = $false
        installsHook = $false
    }
    counts = $counts
    required = $required
    recommendedReady = $recommendedReady
    missing = $missing
    candidates = [pscustomobject]@{
        root = $CandidateRoot
        json = $candidateJsonPath
        groups = $candidateGroups
        reviewCandidateCount = $reviewCandidates.Count
        hasPetCandidate = $hasPetCandidate
        hasNoneCandidate = $hasNoneCandidate
        safeAutoPromote = $safeAutoPromote
        reviewCandidates = $reviewCandidates
    }
    gates = [pscustomobject]@{
        targetSampleReady = $recommendedReady
        stableTestBlocker = -not $recommendedReady
        manualReviewUseful = $manualReviewUseful
    }
    next = $nextSteps
    evidence = [pscustomobject]@{
        nextLog = $next.log
        nextJson = $nextJsonPath
        candidatesLog = $candidates.log
        candidatesJson = $candidateJsonPath
    }
}

$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# Target Sample Readiness")
[void]$md.AppendLine()
[void]$md.AppendLine("- CapturedAt: $($report.capturedAt)")
[void]$md.AppendLine("- RecommendedReady: $recommendedReady")
[void]$md.AppendLine("- SafeAutoPromote: $safeAutoPromote")
[void]$md.AppendLine("- Safety: no process access, no input, no click, no sample promotion.")
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
[void]$md.AppendLine("## Candidate Groups")
foreach ($group in $candidateGroups) {
    [void]$md.AppendLine("- $($group.group): $($group.count)")
}
[void]$md.AppendLine()
[void]$md.AppendLine("## Next")
foreach ($item in $nextSteps) {
    [void]$md.AppendLine("- $item")
}
[void]$md.AppendLine()
[void]$md.AppendLine("## Evidence")
[void]$md.AppendLine("- JSON: $jsonPath")
[void]$md.AppendLine("- combat-sample-next JSON: $nextJsonPath")
[void]$md.AppendLine("- combat-sample-candidates JSON: $candidateJsonPath")
Set-Content -LiteralPath $mdPath -Value $md.ToString() -Encoding UTF8

Write-Host "Target sample readiness"
Write-Host "RecommendedReady=$recommendedReady"
Write-Host ("Counts=player:{0}/{1}, monster:{2}/{3}, boss:{4}/{5}, pet:{6}/{7}, none:{8}/{9}" -f $counts.player, $required.player, $counts.monster, $required.monster, $counts.boss, $required.boss, $counts.pet, $required.pet, $counts.none, $required.none)
Write-Host "SafeAutoPromote=$safeAutoPromote"
Write-Host "CandidateJson=$candidateJsonPath"
Write-Host "Markdown=$mdPath"
Write-Host "Json=$jsonPath"

exit 0
