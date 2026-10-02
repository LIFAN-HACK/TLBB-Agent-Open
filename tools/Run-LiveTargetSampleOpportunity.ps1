param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$SampleRoot = "",
    [switch]$NoCapture
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
$diagProject = Join-Path $Root "tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = Join-Path $Root "artifacts\live-target-sample-opportunity\$stamp"
$jsonPath = Join-Path $outDir "live-target-sample-opportunity.json"
$mdPath = Join-Path $outDir "live-target-sample-opportunity.md"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Get-LatestEvidenceDir {
    param([string]$ProjectRoot)
    $rootPath = Join-Path $ProjectRoot "artifacts\live-target-protection"
    if (-not (Test-Path -LiteralPath $rootPath)) {
        return ""
    }

    return Get-ChildItem -LiteralPath $rootPath -Directory |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 |
        ForEach-Object { $_.FullName }
}

function Read-Text {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
        return ""
    }
    return Get-Content -LiteralPath $Path -Encoding UTF8 -Raw
}

function Get-RegexBool {
    param([string]$Text, [string]$Name)
    $pattern = '"' + [regex]::Escape($Name) + '"\s*:\s*(true|false)'
    if ($Text -match $pattern) {
        return [bool]::Parse($Matches[1])
    }
    return $null
}

function Get-RegexInt {
    param([string]$Text, [string]$Name)
    $pattern = '"' + [regex]::Escape($Name) + '"\s*:\s*(-?\d+)'
    if ($Text -match $pattern) {
        return [int]$Matches[1]
    }
    return $null
}

function Get-RegexString {
    param([string]$Text, [string]$Name)
    $pattern = '"' + [regex]::Escape($Name) + '"\s*:\s*"([^"]*)'
    if ($Text -match $pattern) {
        return $Matches[1]
    }
    return ""
}

function Copy-IfExists {
    param([string]$Source, [string]$Destination)
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

$diagLog = Join-Path $outDir "live-target-protection-output.txt"
$diagExitCode = $null
if (-not $NoCapture) {
    $args = @("run", "--no-build", "--project", $diagProject, "--", "live-target-protection")
    $output = & $dotnet @args 2>&1
    $diagExitCode = $LASTEXITCODE
    $diagText = ($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    Set-Content -LiteralPath $diagLog -Value $diagText -Encoding UTF8
}

$evidenceDir = Get-LatestEvidenceDir $Root
$recognitionPath = if ([string]::IsNullOrWhiteSpace($evidenceDir)) { "" } else { Join-Path $evidenceDir "recognition.json" }
$text = Read-Text $recognitionPath

$hasTarget = Get-RegexBool $text "HasTarget"
$looksPlayer = Get-RegexBool $text "TargetLooksLikePlayer"
$ambiguous = Get-RegexBool $text "TargetProtectionAmbiguous"
$looksPet = Get-RegexBool $text "TargetLooksLikePet"
$guarded = Get-RegexBool $text "ProtectedByPlayerGuard"
$statusBars = Get-RegexBool $text "Matched"
$schoolMarker = Get-RegexBool $text "Matched"
$decision = Get-RegexString $text "Decision"
$targetHp = Get-RegexInt $text "TargetHpPercent"
$targetName = Get-RegexString $text "TargetNameText"
$windowBitness = Get-RegexString $text "bitness"
$clientSize = Get-RegexString $text "clientSize"
$mapName = Get-RegexString $text "MapNameText"
$coordText = Get-RegexString $text "CoordinateText"

# The JSON may contain many "Matched" fields; use targeted text snippets for the two high-risk detectors.
$schoolMarkerSnippet = ""
if ($text -match '(?s)"SchoolMarker"\s*:\s*\{(.{0,700})\}') { $schoolMarkerSnippet = $Matches[1] }
$statusBarsSnippet = ""
if ($text -match '(?s)"StatusBars"\s*:\s*\{(.{0,900})\}') { $statusBarsSnippet = $Matches[1] }
$schoolMarkerMatched = Get-RegexBool $schoolMarkerSnippet "Matched"
$statusBarsMatched = Get-RegexBool $statusBarsSnippet "Matched"

$artifactAssetDir = Join-Path $outDir "assets"
$copied = [ordered]@{}
foreach ($name in @("client.png", "combat-regions-overlay.png", "target-status-x4.png", "target-portrait-x4.png", "school-marker-x4.png", "target-hp-x4.png")) {
    if (-not [string]::IsNullOrWhiteSpace($evidenceDir)) {
        $src = Join-Path $evidenceDir $name
        $dst = Join-Path $artifactAssetDir $name
        $copied[$name] = Copy-IfExists $src $dst
    }
}

$suggestedKind = "review"
$safeToPromote = $false
$recommendation = "manual-review"
$reason = "Current target evidence is not strong enough for automatic sample promotion."

if ([string]::IsNullOrWhiteSpace($recognitionPath) -or -not (Test-Path -LiteralPath $recognitionPath)) {
    $suggestedKind = "none"
    $recommendation = "capture-failed"
    $reason = "No recognition evidence was produced."
} elseif ($hasTarget -eq $false -or ($hasTarget -eq $true -and $targetHp -eq $null -and -not $statusBarsMatched -and -not $schoolMarkerMatched)) {
    $suggestedKind = "none"
    $safeToPromote = $true
    $recommendation = "safe-manual-promote-none"
    $reason = "No active target structure was detected; this can fill the no-target sample after visual confirmation."
} elseif ($looksPet -eq $true -and $guarded -eq $true -and $looksPlayer -ne $true) {
    $suggestedKind = "pet"
    $safeToPromote = $true
    $recommendation = "safe-manual-promote-pet"
    $reason = "Pet-like target evidence is protected and not player-like; this can fill the pet sample after visual confirmation."
} elseif ($guarded -eq $true -or $ambiguous -eq $true -or $schoolMarkerMatched -eq $true) {
    $suggestedKind = "protect-review"
    $safeToPromote = $false
    $recommendation = "do-not-promote-ambiguous"
    $reason = "High-risk player/pet/marker evidence is present or ambiguous; use it only for manual review, not as an attackable sample."
} elseif ($targetHp -ne $null -and $targetHp -gt 0) {
    $suggestedKind = "monster-or-boss-review"
    $safeToPromote = $false
    $recommendation = "review-only"
    $reason = "Target HP exists, but type is not confidently known. Do not promote without manual label."
}

$promoteCommand = ""
if ($safeToPromote -and -not [string]::IsNullOrWhiteSpace($recognitionPath)) {
    $label = "manual_" + $suggestedKind + "_" + (Get-Date -Format "yyyyMMdd_HHmmss")
    $promoteCommand = '& "' + $dotnet + '" run --no-build --project "' + $diagProject + '" -- combat-sample-promote "' + $recognitionPath + '" --kind ' + $suggestedKind + ' --label ' + $label
    if (-not [string]::IsNullOrWhiteSpace($SampleRoot)) {
        $promoteCommand += ' --output "' + $SampleRoot + '"'
    }
}

$result = [ordered]@{
    capturedAt = (Get-Date).ToString("o")
    mode = "read-only-live-target-sample-opportunity"
    safety = [ordered]@{
        sendsInput = $false
        clicksMouse = $false
        writesMemory = $false
        injectsDll = $false
        installsHook = $false
        promotesSamples = $false
    }
    diagnostics = [ordered]@{
        exitCode = $diagExitCode
        log = $diagLog
        evidenceDir = $evidenceDir
        recognitionPath = $recognitionPath
    }
    observed = [ordered]@{
        windowBitness = $windowBitness
        clientSize = $clientSize
        targetNameText = $targetName
        targetHpPercent = $targetHp
        hasTarget = $hasTarget
        targetLooksLikePlayer = $looksPlayer
        targetLooksLikePet = $looksPet
        targetProtectionAmbiguous = $ambiguous
        protectedByPlayerGuard = $guarded
        targetStatusBarsMatched = $statusBarsMatched
        schoolMarkerMatched = $schoolMarkerMatched
        decision = $decision
        mapNameText = $mapName
        coordinateText = $coordText
    }
    opportunity = [ordered]@{
        suggestedKind = $suggestedKind
        safeToPromote = $safeToPromote
        recommendation = $recommendation
        reason = $reason
        promoteCommand = $promoteCommand
    }
    copiedEvidence = $copied
}

$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# Live Target Sample Opportunity")
$lines.Add("")
$lines.Add("- CapturedAt: $($result.capturedAt)")
$lines.Add("- Mode: read-only-live-target-sample-opportunity")
$lines.Add("- Safety: no input, no click, no sample promotion, no memory write, no injection, no hook.")
$lines.Add("- EvidenceDir: $evidenceDir")
$lines.Add("- Recognition: $recognitionPath")
$lines.Add("")
$lines.Add("## Conclusion")
$lines.Add("")
$lines.Add("- SuggestedKind: $suggestedKind")
$lines.Add("- SafeToPromote: $safeToPromote")
$lines.Add("- Recommendation: $recommendation")
$lines.Add("- Reason: $(Escape-Md $reason)")
if (-not [string]::IsNullOrWhiteSpace($promoteCommand)) {
    $lines.Add("- ManualPromoteCommand: ``$promoteCommand``")
}
$lines.Add("")
$lines.Add("## Observed")
$lines.Add("")
$lines.Add("| Field | Value |")
$lines.Add("|---|---|")
foreach ($key in $result.observed.Keys) {
    $lines.Add("| $key | $(Escape-Md ([string]$result.observed[$key])) |")
}
$lines.Add("")
$lines.Add("## Evidence")
$lines.Add("")
foreach ($key in $copied.Keys) {
    if (-not [string]::IsNullOrWhiteSpace($copied[$key])) {
        $lines.Add("- ${key}: $($copied[$key])")
    }
}

Set-Content -LiteralPath $mdPath -Value $lines -Encoding UTF8

Write-Host "Opportunity=$recommendation"
Write-Host "SafeToPromote=$safeToPromote"
Write-Host "SuggestedKind=$suggestedKind"
Write-Host "Reason=$reason"
Write-Host "Evidence=$jsonPath"
Write-Host "Report=$mdPath"
if (-not [string]::IsNullOrWhiteSpace($promoteCommand)) {
    Write-Host "ManualPromoteCommand=$promoteCommand"
}

if ($diagExitCode -ne $null -and $diagExitCode -ne 0) {
    exit 2
}
