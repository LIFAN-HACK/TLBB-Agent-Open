param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$Profile = ""
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$DefaultProfileName = -join ([char]0x65B9, [char]0x6848, [char]0x2D, [char]0x30, [char]0x36, [char]0x32, [char]0x31, [char]0x2D, [char]0x30, [char]0x39, [char]0x31, [char]0x36)
if ([string]::IsNullOrWhiteSpace($Profile)) {
    $Profile = $DefaultProfileName
}

$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
$diagProject = Join-Path $Root "tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj"
$localizedAppName = -join ([char]0x9F99, [char]0x4F34, [char]0x884C)
$profileCandidates = @(
    (Join-Path $env:APPDATA ("{0}\profiles\return-points-{1}.json" -f $localizedAppName, $Profile)),
    (Join-Path $env:APPDATA ("LongBanXing\profiles\return-points-{0}.json" -f $Profile))
)
$profilePath = $profileCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($profilePath)) {
    $profilePath = $profileCandidates[0]
}
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = Join-Path $Root "artifacts\death-return-readiness\$stamp"
$jsonPath = Join-Path $outDir "death-return-readiness.json"
$mdPath = Join-Path $outDir "death-return-readiness.md"

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

$profileExists = Test-Path -LiteralPath $profilePath
$config = $null
if ($profileExists) {
    $config = Get-Content -LiteralPath $profilePath -Encoding UTF8 -Raw | ConvertFrom-Json
}

$deathEvidence = Invoke-Diag "live-death-return" @("run", "--no-build", "--project", $diagProject, "--", "live-death-return", "--profile", $Profile)
$executeGate = Invoke-Diag "live-death-return-execute" @("run", "--no-build", "--project", $diagProject, "--", "live-death-return-execute", "--profile", $Profile)

$deathFieldReady = $false
$deathVisual = $false
$deathDead = $false
$deathReason = ""
$deathEvidencePath = ""

if ($deathEvidence.text -match "Death dead=([^,]+), keyword='([^']*)', fieldReady=([^,]+), visual=([^\r\n]+)") {
    $deathDead = $Matches[1].Trim() -eq "True"
    $deathFieldReady = $Matches[3].Trim() -eq "True"
    $deathVisual = $Matches[4].Trim() -eq "True"
}
if ($deathEvidence.text -match "reason=([^\r\n]+)") {
    $deathReason = $Matches[1].Trim()
}
if ($deathEvidence.text -match "(?m)^Evidence=(.+)$") {
    $deathEvidencePath = $Matches[1].Trim()
}

$points = @()
$deathPoints = @()
if ($config -ne $null -and $config.Points -ne $null) {
    $points = @($config.Points)
    $deathPoints = @($points | Where-Object { [int]$_.Kind -eq 3 })
}

$deathNav = $null
if ($config -ne $null) {
    $deathNav = $config.DeathReturnNavigation
}

$deathEnabled = $config -ne $null -and $config.EnableDeathReturn -eq $true
$hasDeathPoint = $deathPoints.Count -gt 0
$hasAnyPoint = $points.Count -gt 0
$deathMode = if ($deathNav -ne $null) { [int]$deathNav.Mode } else { 0 }
$deathClickConfigured = $deathNav -ne $null -and [int]$deathNav.ClickX -gt 0 -and [int]$deathNav.ClickY -gt 0
$deathHotkeyConfigured = $deathNav -ne $null -and -not [string]::IsNullOrWhiteSpace([string]$deathNav.Hotkey)
$deathRecordedConfigured = $deathNav -ne $null -and (
    @($deathNav.RecordedPreHotkeys).Count -gt 0 -or
    @($deathNav.RecordedClicks).Count -gt 0
)
$deathHasAction = switch ($deathMode) {
    1 { $deathClickConfigured }
    2 { $deathHotkeyConfigured }
    3 { $deathHotkeyConfigured -and $deathClickConfigured }
    4 { $deathClickConfigured }
    5 { $deathHotkeyConfigured -and $deathClickConfigured }
    6 { $deathRecordedConfigured }
    default { $false }
}

$readyForDryRun = $profileExists -and $deathFieldReady -and $deathVisual -and $deathEnabled -and $hasAnyPoint -and $deathHasAction
$readyForLiveTrigger = $readyForDryRun -and $deathDead

$next = New-Object System.Collections.ArrayList
if (-not $profileExists) {
    [void]$next.Add("Create or load return point profile first.")
}
if (-not $deathFieldReady -or -not $deathVisual) {
    [void]$next.Add("Calibrate death prompt region; live-death-return must save a nonblank region and OCR text.")
}
if (-not $hasAnyPoint) {
    [void]$next.Add("Add at least one safe return point before enabling death return.")
}
if (-not $hasDeathPoint -and $hasAnyPoint) {
    [void]$next.Add("Optional: add a dedicated DeathReturn point. Without it runtime falls back to the first saved point.")
}
if (-not $deathEnabled) {
    [void]$next.Add("Enable death return only after confirming the target point and action.")
}
if (-not $deathHasAction) {
    [void]$next.Add("Configure DeathReturnNavigation action: recorded path, hotkey, or click action.")
}
if ($deathEnabled -and $deathHasAction -and -not $deathDead) {
    [void]$next.Add("Current character is not dead; execute gate must remain no-action.")
}

$dotnetQuoted = "& '" + $dotnet + "'"
$projectQuoted = "'" + $diagProject + "'"
$profileQuoted = "'" + $Profile + "'"
$suggestedCommands = @(
    "$dotnetQuoted run --no-build --project $projectQuoted -- live-death-return --profile $profileQuoted",
    "$dotnetQuoted run --no-build --project $projectQuoted -- live-death-return-execute --profile $profileQuoted",
    "$dotnetQuoted run --no-build --project $projectQuoted -- return-capture-current --profile $profileQuoted --kind death --label 'death-return-safe-point'",
    "$dotnetQuoted run --no-build --project $projectQuoted -- return-prepare-saved-point --profile $profileQuoted --kind death"
)

$report = [pscustomobject]@{
    capturedAt = (Get-Date).ToString("o")
    mode = "death-return-readiness-no-business-action"
    profile = $Profile
    profilePath = $profilePath
    profileExists = $profileExists
    safety = [pscustomobject]@{
        sendsInput = $false
        clicksMouse = $false
        writesMemory = $false
        injectsDll = $false
        installsHook = $false
        patchesClient = $false
        modifiesPackets = $false
        modifiesProfile = $false
    }
    deathRecognition = [pscustomobject]@{
        dead = $deathDead
        fieldReady = $deathFieldReady
        visual = $deathVisual
        reason = $deathReason
        evidence = $deathEvidencePath
    }
    config = [pscustomobject]@{
        enabled = $deathEnabled
        totalPoints = $points.Count
        deathPoints = $deathPoints.Count
        navigationMode = $deathMode
        hasAction = $deathHasAction
    }
    gates = [pscustomobject]@{
        readyForDryRun = $readyForDryRun
        readyForLiveTrigger = $readyForLiveTrigger
    }
    next = $next
    suggestedCommands = $suggestedCommands
    logs = [pscustomobject]@{
        liveDeathReturn = $deathEvidence.log
        liveDeathReturnExecute = $executeGate.log
    }
}

$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# Death Return Readiness")
[void]$md.AppendLine()
[void]$md.AppendLine("- CapturedAt: $($report.capturedAt)")
[void]$md.AppendLine("- Profile: $Profile")
[void]$md.AppendLine("- ReadyForDryRun: $readyForDryRun")
[void]$md.AppendLine("- ReadyForLiveTrigger: $readyForLiveTrigger")
[void]$md.AppendLine("- Safety: no input, no click, no profile write, no memory write, no injection, no hook.")
[void]$md.AppendLine()
[void]$md.AppendLine("## Recognition")
[void]$md.AppendLine()
[void]$md.AppendLine("- Dead: $deathDead")
[void]$md.AppendLine("- FieldReady: $deathFieldReady")
[void]$md.AppendLine("- Visual: $deathVisual")
[void]$md.AppendLine("- Reason: $deathReason")
[void]$md.AppendLine("- Evidence: $deathEvidencePath")
[void]$md.AppendLine()
[void]$md.AppendLine("## Config")
[void]$md.AppendLine()
[void]$md.AppendLine("- EnableDeathReturn: $deathEnabled")
[void]$md.AppendLine("- TotalPoints: $($points.Count)")
[void]$md.AppendLine("- DeathPoints: $($deathPoints.Count)")
[void]$md.AppendLine("- DeathNavigationMode: $deathMode")
[void]$md.AppendLine("- DeathHasAction: $deathHasAction")
[void]$md.AppendLine()
[void]$md.AppendLine("## Next")
foreach ($item in $next) {
    [void]$md.AppendLine("- $item")
}
[void]$md.AppendLine()
[void]$md.AppendLine("## Suggested Commands")
foreach ($cmd in $suggestedCommands) {
    [void]$md.AppendLine("- ``$cmd``")
}
[void]$md.AppendLine()
[void]$md.AppendLine("## Evidence")
[void]$md.AppendLine()
[void]$md.AppendLine("- JSON: $jsonPath")
[void]$md.AppendLine("- live-death-return log: $($deathEvidence.log)")
[void]$md.AppendLine("- live-death-return-execute log: $($executeGate.log)")
Set-Content -LiteralPath $mdPath -Value $md.ToString() -Encoding UTF8

Write-Host "Death return readiness"
Write-Host "ReadyForDryRun=$readyForDryRun"
Write-Host "ReadyForLiveTrigger=$readyForLiveTrigger"
Write-Host "Recognition fieldReady=$deathFieldReady visual=$deathVisual dead=$deathDead"
Write-Host "Config enabled=$deathEnabled points=$($points.Count) deathPoints=$($deathPoints.Count) hasAction=$deathHasAction"
Write-Host "Markdown=$mdPath"
Write-Host "Json=$jsonPath"

exit 0
