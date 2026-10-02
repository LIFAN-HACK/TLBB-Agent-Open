param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$Profile = "",
    [switch]$RefreshCalibration
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$DefaultProfileName = -join ([char]0x65B9, [char]0x6848, [char]0x2D, [char]0x30, [char]0x36, [char]0x32, [char]0x31, [char]0x2D, [char]0x30, [char]0x39, [char]0x31, [char]0x36)
if ([string]::IsNullOrWhiteSpace($Profile)) {
    $Profile = $DefaultProfileName
}

$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
$diagProject = Join-Path $Root "tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj"
$samplerProject = Join-Path $Root "tools\TlbbTargetSampler\TlbbTargetSampler.csproj"
$localizedAppName = -join ([char]0x9F99, [char]0x4F34, [char]0x884C)
$profileCandidates = @(
    (Join-Path $env:APPDATA ("{0}\profiles\{1}.json" -f $localizedAppName, $Profile)),
    (Join-Path $env:APPDATA ("LongBanXing\profiles\{0}.json" -f $Profile))
)
$profilePath = $profileCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($profilePath)) {
    $profilePath = $profileCandidates[0]
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = Join-Path $Root "artifacts\pet-deploy-readiness\$stamp"
$jsonPath = Join-Path $outDir "pet-deploy-readiness.json"
$mdPath = Join-Path $outDir "pet-deploy-readiness.md"
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

function Get-LatestCalibration {
    $rootPath = Join-Path $Root "artifacts\live-pet-deploy-calibration"
    if (-not (Test-Path -LiteralPath $rootPath)) {
        return $null
    }

    Get-ChildItem -LiteralPath $rootPath -Filter "pet-deploy-calibration.json" -Recurse -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

$calibrationLog = $null
if ($RefreshCalibration) {
    $calibrationLog = Invoke-Diag "live-pet-deploy-calibration" @("run", "--no-build", "--project", $diagProject, "--", "live-pet-deploy-calibration")
}

$profileExists = Test-Path -LiteralPath $profilePath
$profileConfig = $null
if ($profileExists) {
    $profileConfig = Get-Content -LiteralPath $profilePath -Encoding UTF8 -Raw | ConvertFrom-Json
}

$mode = if ($profileConfig -ne $null) { [int]$profileConfig.PetDeployActivationMode } else { 0 }
$hotkey = if ($profileConfig -ne $null) { [string]$profileConfig.PetDeployHotkey } else { "" }
$clickX = if ($profileConfig -ne $null) { [int]$profileConfig.PetDeployClickX } else { 0 }
$clickBottom = if ($profileConfig -ne $null) { [int]$profileConfig.PetDeployClickBottomOffset } else { 0 }
$cooldownMs = if ($profileConfig -ne $null) { [int]$profileConfig.PetDeployCooldownMs } else { 0 }

$configuredByHotkey = $mode -eq 0 -and -not [string]::IsNullOrWhiteSpace($hotkey)
$configuredByClick = $mode -eq 1 -and $clickX -gt 0 -and $clickBottom -gt 0
$runtimeReady = $configuredByHotkey -or $configuredByClick

$calibrationFile = Get-LatestCalibration
$calibration = $null
if ($calibrationFile -ne $null) {
    $calibration = Get-Content -LiteralPath $calibrationFile.FullName -Encoding UTF8 -Raw | ConvertFrom-Json
}

$candidateCount = 0
$topSuggestions = @()
$overlayPath = ""
$bottomOverlayPath = ""
$calibrationVisual = $false
if ($calibration -ne $null) {
    $candidateCount = @($calibration.detectedSlots).Count
    $topSuggestions = @($calibration.suggestions | Select-Object -First 8)
    $overlayPath = [string]$calibration.capture.overlay.path
    $bottomOverlayPath = [string]$calibration.capture.bottomOverlay.path
    $calibrationVisual = ($calibration.capture.client.visual -eq $true) -and (Test-Path -LiteralPath $overlayPath)
}

$manualCommitReady = $calibrationVisual -and $topSuggestions.Count -gt 0
$safeToAutoCommit = $false

$next = New-Object System.Collections.ArrayList
if (-not $profileExists) {
    [void]$next.Add("Profile file is missing.")
}
if (-not $runtimeReady) {
    [void]$next.Add("Pet deploy runtime action is not configured. Use the target sampler to choose a human-confirmed client click point, or set an explicit hotkey.")
}
if ($mode -eq 1 -and -not $configuredByClick) {
    [void]$next.Add("Client-click mode is selected but click x/bottom is empty.")
}
if ($mode -eq 0 -and [string]::IsNullOrWhiteSpace($hotkey)) {
    [void]$next.Add("Hotkey mode is selected but hotkey is empty.")
}
if (-not $manualCommitReady) {
    [void]$next.Add("Run live-pet-deploy-calibration or open the target sampler, then manually confirm the current-pet deploy button.")
} else {
    [void]$next.Add("Open the overlay image or target sampler and manually choose the point exactly on the current-pet deploy button. Do not auto-commit skill-bar guesses.")
}

$dotnetQuoted = "& '" + $dotnet + "'"
$projectQuoted = "'" + $diagProject + "'"
$samplerProjectQuoted = "'" + $samplerProject + "'"
$profileQuoted = "'" + $Profile + "'"
$applyScriptQuoted = "'" + (Join-Path $Root "tools\Apply-PetDeployClickPoint.ps1") + "'"
$suggestedCommands = @(
    "$dotnetQuoted run --no-build --project $projectQuoted -- live-pet-deploy-calibration",
    "$dotnetQuoted run --no-build --project $samplerProjectQuoted",
    "& $applyScriptQuoted -Rank N",
    "& $applyScriptQuoted -Rank N -Confirm",
    "& $applyScriptQuoted -X 1337 -Bottom 122 -Confirm",
    "$dotnetQuoted run --no-build --project $projectQuoted -- pet-deploy-candidate-apply --latest --rank N --profile $profileQuoted --commit",
    "$dotnetQuoted run --no-build --project $projectQuoted -- profile-recovery-bootstrap --profile $profileQuoted --commit --pet-deploy-hotkey F7"
)

$report = [pscustomobject]@{
    capturedAt = (Get-Date).ToString("o")
    mode = "pet-deploy-readiness-no-input-no-click"
    profile = $Profile
    profilePath = $profilePath
    profileExists = $profileExists
    safety = [pscustomobject]@{
        sendsInput = $false
        clicksMouse = $false
        writesProfile = $false
        writesMemory = $false
        injectsDll = $false
        installsHook = $false
        patchesClient = $false
    }
    config = [pscustomobject]@{
        activationMode = $mode
        hotkey = $hotkey
        clickX = $clickX
        clickBottom = $clickBottom
        cooldownMs = $cooldownMs
        configuredByHotkey = $configuredByHotkey
        configuredByClick = $configuredByClick
        runtimeReady = $runtimeReady
    }
    calibration = [pscustomobject]@{
        refreshed = [bool]$RefreshCalibration
        latest = if ($calibrationFile -ne $null) { $calibrationFile.FullName } else { "" }
        visual = $calibrationVisual
        candidateCount = $candidateCount
        overlay = $overlayPath
        bottomOverlay = $bottomOverlayPath
        manualCommitReady = $manualCommitReady
        safeToAutoCommit = $safeToAutoCommit
        requiresHumanPointSelection = -not $runtimeReady
        warning = "Candidate grid is only a visual aid. Pet deploy must be confirmed by a human point selection before writing the profile."
        topSuggestions = $topSuggestions
    }
    gates = [pscustomobject]@{
        runtimeReady = $runtimeReady
        manualCommitReady = $manualCommitReady
        stableTestBlocker = -not $runtimeReady
    }
    next = $next
    suggestedCommands = $suggestedCommands
    logs = [pscustomobject]@{
        livePetDeployCalibration = if ($calibrationLog -ne $null) { $calibrationLog.log } else { "" }
    }
}

$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# Pet Deploy Readiness")
[void]$md.AppendLine()
[void]$md.AppendLine("- CapturedAt: $($report.capturedAt)")
[void]$md.AppendLine("- Profile: $Profile")
[void]$md.AppendLine("- RuntimeReady: $runtimeReady")
[void]$md.AppendLine("- ManualCommitReady: $manualCommitReady")
[void]$md.AppendLine("- SafeToAutoCommit: $safeToAutoCommit")
[void]$md.AppendLine("- Safety: no input, no click, no profile write, no memory write, no injection, no hook.")
[void]$md.AppendLine()
[void]$md.AppendLine("## Config")
[void]$md.AppendLine("- ActivationMode: $mode")
[void]$md.AppendLine("- Hotkey: $hotkey")
[void]$md.AppendLine("- Click: x=$clickX, bottom=$clickBottom")
[void]$md.AppendLine("- CooldownMs: $cooldownMs")
[void]$md.AppendLine()
[void]$md.AppendLine("## Calibration")
[void]$md.AppendLine("- Latest: $($report.calibration.latest)")
[void]$md.AppendLine("- Visual: $calibrationVisual")
[void]$md.AppendLine("- CandidateCount: $candidateCount")
[void]$md.AppendLine("- Overlay: $overlayPath")
[void]$md.AppendLine("- BottomOverlay: $bottomOverlayPath")
[void]$md.AppendLine("- Warning: $($report.calibration.warning)")
[void]$md.AppendLine()
[void]$md.AppendLine("## Next")
foreach ($item in $next) {
    [void]$md.AppendLine("- $item")
}
[void]$md.AppendLine()
[void]$md.AppendLine("## Top Suggestions")
foreach ($s in $topSuggestions) {
    [void]$md.AppendLine("- rank=$($s.Rank), name=$($s.Name), x=$($s.X), bottom=$($s.Bottom)")
}
[void]$md.AppendLine()
[void]$md.AppendLine("## Suggested Commands")
foreach ($cmd in $suggestedCommands) {
    [void]$md.AppendLine("- ``$cmd``")
}
[void]$md.AppendLine()
[void]$md.AppendLine("## Evidence")
[void]$md.AppendLine("- JSON: $jsonPath")
if ($calibrationLog -ne $null) {
    [void]$md.AppendLine("- Calibration log: $($calibrationLog.log)")
}
Set-Content -LiteralPath $mdPath -Value $md.ToString() -Encoding UTF8

Write-Host "Pet deploy readiness"
Write-Host "RuntimeReady=$runtimeReady"
Write-Host "ManualCommitReady=$manualCommitReady"
Write-Host "SafeToAutoCommit=$safeToAutoCommit"
Write-Host "Mode=$mode Hotkey='$hotkey' Click=$clickX,bottom-$clickBottom"
Write-Host "CalibrationVisual=$calibrationVisual CandidateCount=$candidateCount"
Write-Host "Overlay=$overlayPath"
Write-Host "BottomOverlay=$bottomOverlayPath"
Write-Host "Markdown=$mdPath"
Write-Host "Json=$jsonPath"

exit 0

