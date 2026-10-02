param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$CandidateJson = "",
    [int]$PerKind = 12
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Get-LatestCandidateJson {
    param([string]$ProjectRoot)
    $candidateRoot = Join-Path $ProjectRoot "artifacts\combat-sample-candidates"
    if (-not (Test-Path -LiteralPath $candidateRoot)) {
        return ""
    }

    return Get-ChildItem -LiteralPath $candidateRoot -Filter "combat-sample-candidates.json" -Recurse -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 |
        ForEach-Object { $_.FullName }
}

function Copy-IfExists {
    param(
        [string]$Source,
        [string]$Destination
    )
    if ([string]::IsNullOrWhiteSpace($Source) -or -not (Test-Path -LiteralPath $Source)) {
        return ""
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
    return $Destination
}

function Escape-Md {
    param([string]$Text)
    if ($null -eq $Text) { return "" }
    return $Text.Replace("|", "\|")
}

if ([string]::IsNullOrWhiteSpace($CandidateJson)) {
    $CandidateJson = Get-LatestCandidateJson $Root
}
if ([string]::IsNullOrWhiteSpace($CandidateJson) -or -not (Test-Path -LiteralPath $CandidateJson)) {
    throw "Candidate JSON not found. Run tools\Run-TargetSampleReadiness.ps1 first."
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = Join-Path $Root "artifacts\target-sample-review-pack\$stamp"
$assetDir = Join-Path $outDir "assets"
$mdPath = Join-Path $outDir "target-sample-review-pack.md"
$tsvPath = Join-Path $outDir "target-sample-review-pack.tsv"
$jsonOut = Join-Path $outDir "target-sample-review-pack.json"
New-Item -ItemType Directory -Force -Path $assetDir | Out-Null

$source = Get-Content -LiteralPath $CandidateJson -Encoding UTF8 -Raw | ConvertFrom-Json
$candidates = @($source.candidates)

$selected = New-Object System.Collections.ArrayList
$wantedKinds = @(
    [pscustomobject]@{ kind = "player"; recommendations = @("protect-only-review"); minRisk = @("medium", "low") },
    [pscustomobject]@{ kind = "boss"; recommendations = @("review-for-promotion"); minRisk = @("medium", "low") },
    [pscustomobject]@{ kind = "monster"; recommendations = @("review-for-promotion"); minRisk = @("low") }
)

foreach ($wanted in $wantedKinds) {
    $items = $candidates |
        Where-Object {
            $_.SuggestedKind -eq $wanted.kind -and
            $wanted.recommendations -contains $_.Recommendation -and
            $wanted.minRisk -contains $_.Risk -and
            [int]$_.CompleteRegions -ge [int]$_.RequiredRegions
        } |
        Sort-Object @{ Expression = { $_.Risk -eq "low" }; Descending = $true }, Name -Unique |
        Select-Object -First $PerKind

    foreach ($item in $items) {
        [void]$selected.Add($item)
    }
}

$rows = New-Object System.Collections.ArrayList
$index = 0
foreach ($item in $selected) {
    $index++
    $safeName = ($item.Name -replace '[\\/:*?"<>| ]+', '_')
    $prefix = "{0:000}-{1}-{2}" -f $index, $item.SuggestedKind, $safeName
    $clientCopy = Copy-IfExists ([string]$item.ClientImagePath) (Join-Path $assetDir "$prefix-client.png")

    $cropCopies = @{}
    foreach ($crop in @("target-name.png", "target-hp.png", "target-status.png", "school-marker.png", "target-portrait.png", "target-mp.png", "target-rage.png", "pet-hp.png")) {
        $cropPath = Join-Path ([string]$item.SourcePath) $crop
        $copied = Copy-IfExists $cropPath (Join-Path $assetDir "$prefix-$crop")
        if (-not [string]::IsNullOrWhiteSpace($copied)) {
            $cropCopies[$crop] = $copied
        }
    }

    $label = switch ($item.SuggestedKind) {
        "player" { "manual_player_$index" }
        "boss" { "manual_boss_$index" }
        "monster" { "manual_monster_$index" }
        default { "manual_sample_$index" }
    }
    $allowRisk = if ($item.Risk -eq "low") { "" } else { " --allow-risk" }
    $promoteCommand = "& 'C:\Program Files\dotnet\dotnet.exe' run --no-build --project '$Root\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj' -- combat-sample-promote '$($item.SourcePath)' --kind $($item.SuggestedKind) --label '$label'$allowRisk"

    [void]$rows.Add([pscustomobject]@{
        index = $index
        suggestedKind = [string]$item.SuggestedKind
        recommendation = [string]$item.Recommendation
        risk = [string]$item.Risk
        reason = [string]$item.Reason
        targetHpPercent = $item.TargetHpPercent
        targetLooksLikePlayer = $item.TargetLooksLikePlayer
        targetProtectionAmbiguous = $item.TargetProtectionAmbiguous
        targetLooksLikePet = $item.TargetLooksLikePet
        sourcePath = [string]$item.SourcePath
        clientImage = $clientCopy
        promoteCommand = $promoteCommand
        cropCount = $cropCopies.Count
    })
}

$rows |
    ForEach-Object {
        "{0}`t{1}`t{2}`t{3}`t{4}`t{5}`t{6}`t{7}" -f $_.index,$_.suggestedKind,$_.recommendation,$_.risk,$_.targetHpPercent,$_.sourcePath,$_.clientImage,$_.promoteCommand
    } |
    Set-Content -LiteralPath $tsvPath -Encoding UTF8

$report = [pscustomobject]@{
    capturedAt = (Get-Date).ToString("o")
    mode = "target-sample-review-pack-no-promotion"
    sourceCandidateJson = $CandidateJson
    output = $outDir
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
    selectedCount = $rows.Count
    selected = $rows
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonOut -Encoding UTF8

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# Target Sample Review Pack")
[void]$md.AppendLine()
[void]$md.AppendLine("- CapturedAt: $($report.capturedAt)")
[void]$md.AppendLine("- Source: $CandidateJson")
[void]$md.AppendLine("- Selected: $($rows.Count)")
[void]$md.AppendLine("- Safety: no process access, no input, no click, no promotion, no sample library write.")
[void]$md.AppendLine()
[void]$md.AppendLine("## Missing Gate Context")
[void]$md.AppendLine()
[void]$md.AppendLine("- Still missing recommended coverage: player 2, boss 1, pet 1, none 1.")
[void]$md.AppendLine("- This pack only helps review player/boss/monster candidates already present in artifacts.")
[void]$md.AppendLine("- There are no safe pet or no-target candidates in current history; capture those with the sampler.")
[void]$md.AppendLine()
[void]$md.AppendLine("## Review Items")
[void]$md.AppendLine()
foreach ($row in $rows) {
    [void]$md.AppendLine("### $($row.index). $($row.suggestedKind) / $($row.risk)")
    [void]$md.AppendLine()
    $reasonText = Escape-Md $row.reason
    $sourceText = [string]$row.sourcePath
    $targetHpText = [string]$row.targetHpPercent
    $flagsText = "LooksLikePlayer={0}, Ambiguous={1}, LooksLikePet={2}" -f $row.targetLooksLikePlayer, $row.targetProtectionAmbiguous, $row.targetLooksLikePet
    [void]$md.AppendLine(("- Reason: {0}" -f $reasonText))
    [void]$md.AppendLine(("- Source: {0}" -f $sourceText))
    [void]$md.AppendLine(("- TargetHp: {0}" -f $targetHpText))
    [void]$md.AppendLine(("- Flags: {0}" -f $flagsText))
    if (-not [string]::IsNullOrWhiteSpace($row.clientImage)) {
        [void]$md.AppendLine()
        [void]$md.AppendLine("![client]($($row.clientImage))")
    }
    [void]$md.AppendLine()
    [void]$md.AppendLine("Promotion command after manual confirmation:")
    [void]$md.AppendLine()
    [void]$md.AppendLine('```powershell')
    [void]$md.AppendLine($row.promoteCommand)
    [void]$md.AppendLine('```')
    [void]$md.AppendLine()
}
[void]$md.AppendLine("## Evidence")
[void]$md.AppendLine()
[void]$md.AppendLine("- JSON: $jsonOut")
[void]$md.AppendLine("- TSV: $tsvPath")
Set-Content -LiteralPath $mdPath -Value $md.ToString() -Encoding UTF8

Write-Host "Target sample review pack"
Write-Host "Selected=$($rows.Count)"
Write-Host "Markdown=$mdPath"
Write-Host "Json=$jsonOut"
Write-Host "Tsv=$tsvPath"
