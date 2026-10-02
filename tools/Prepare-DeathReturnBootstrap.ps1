param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$Profile = "方案-0621-0916",
    [switch]$Commit,
    [switch]$PreferStuckRecovery
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$localizedAppName = -join ([char]0x9F99, [char]0x4F34, [char]0x884C)
$profileCandidates = @(
    (Join-Path $env:APPDATA ("{0}\profiles\return-points-{1}.json" -f $localizedAppName, $Profile)),
    (Join-Path $env:APPDATA ("LongBanXing\profiles\return-points-{0}.json" -f $Profile))
)
$profilePath = $profileCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($profilePath)) {
    throw "Return point profile not found for '$Profile'."
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = Join-Path $Root "artifacts\death-return-bootstrap\$stamp"
$jsonPath = Join-Path $outDir "death-return-bootstrap.json"
$mdPath = Join-Path $outDir "death-return-bootstrap.md"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Copy-JsonObject {
    param([object]$Value)
    return ($Value | ConvertTo-Json -Depth 30 | ConvertFrom-Json)
}

function Test-NavigationConfigured {
    param([object]$Nav)
    if ($null -eq $Nav) { return $false }
    $mode = [int]$Nav.Mode
    switch ($mode) {
        1 { return ([int]$Nav.ClickX -gt 0 -and [int]$Nav.ClickY -gt 0) }
        2 { return -not [string]::IsNullOrWhiteSpace([string]$Nav.Hotkey) }
        3 { return (-not [string]::IsNullOrWhiteSpace([string]$Nav.Hotkey)) -and [int]$Nav.ClickX -gt 0 -and [int]$Nav.ClickY -gt 0 }
        4 { return ([int]$Nav.ClickX -gt 0 -and [int]$Nav.ClickY -gt 0) }
        5 { return (-not [string]::IsNullOrWhiteSpace([string]$Nav.Hotkey)) -and [int]$Nav.ClickX -gt 0 -and [int]$Nav.ClickY -gt 0 }
        6 { return @($Nav.RecordedPreHotkeys).Count -gt 0 -or @($Nav.RecordedClicks).Count -gt 0 }
        default { return $false }
    }
}

function Describe-Navigation {
    param([object]$Nav)
    if ($null -eq $Nav) { return "missing" }
    return "mode={0}, hotkey='{1}', click={2},{3}, interval={4}, dispatch={5}, recordedClicks={6}" -f `
        [int]$Nav.Mode,
        [string]$Nav.Hotkey,
        [int]$Nav.ClickX,
        [int]$Nav.ClickY,
        [int]$Nav.ActionIntervalMs,
        [int]$Nav.DispatchMode,
        @($Nav.RecordedClicks).Count
}

$config = Get-Content -LiteralPath $profilePath -Encoding UTF8 -Raw | ConvertFrom-Json
$fixedNav = $config.ReturnNavigation
$stuckNav = $config.StuckRecoveryNavigation
$sourceName = ""
$sourceNav = $null

if ($PreferStuckRecovery -and (Test-NavigationConfigured $stuckNav)) {
    $sourceName = "StuckRecoveryNavigation"
    $sourceNav = $stuckNav
} elseif (Test-NavigationConfigured $fixedNav) {
    $sourceName = "ReturnNavigation"
    $sourceNav = $fixedNav
} elseif (Test-NavigationConfigured $stuckNav) {
    $sourceName = "StuckRecoveryNavigation"
    $sourceNav = $stuckNav
}

$errors = New-Object System.Collections.ArrayList
$warnings = New-Object System.Collections.ArrayList
$changes = New-Object System.Collections.ArrayList

if ($null -eq $sourceNav) {
    [void]$errors.Add("No configured fixed/stuck navigation action is available to clone into death return.")
}

$beforeEnabled = [bool]$config.EnableDeathReturn
$beforeNav = Describe-Navigation $config.DeathReturnNavigation
$pointCount = @($config.Points).Count
$deathPointCount = @($config.Points | Where-Object { [int]$_.Kind -eq 3 }).Count
if ($pointCount -le 0) {
    [void]$errors.Add("No return points exist. Add at least one safe return point before death return.")
}
if ($deathPointCount -le 0 -and $pointCount -gt 0) {
    [void]$warnings.Add("No dedicated death return point exists; runtime will fall back to the first saved return point.")
}

if ($errors.Count -eq 0) {
    $newNav = Copy-JsonObject $sourceNav
    if ([int]$newNav.ActionIntervalMs -lt 1200) {
        $newNav.ActionIntervalMs = 1200
    }
    $config.DeathReturnNavigation = $newNav
    $config.EnableDeathReturn = $true
    [void]$changes.Add([pscustomobject]@{
        field = "EnableDeathReturn"
        before = $beforeEnabled
        after = $true
        willChange = ($beforeEnabled -ne $true)
    })
    [void]$changes.Add([pscustomobject]@{
        field = "DeathReturnNavigation"
        source = $sourceName
        before = $beforeNav
        after = (Describe-Navigation $newNav)
        willChange = $true
    })
}

 $backupPath = ""
if ($Commit -and $errors.Count -eq 0) {
    $backupDir = Join-Path $outDir "backup"
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    $backupPath = Join-Path $backupDir (Split-Path -Leaf $profilePath)
    Copy-Item -LiteralPath $profilePath -Destination $backupPath -Force
    $config | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $profilePath -Encoding UTF8
} elseif (-not $Commit) {
    [void]$warnings.Add("Preview only: no profile write. Re-run with -Commit after reviewing evidence.")
}

$report = [pscustomobject]@{
    capturedAt = (Get-Date).ToString("o")
    mode = if ($Commit) { "death-return-bootstrap-commit" } else { "death-return-bootstrap-preview" }
    profile = $Profile
    profilePath = $profilePath
    committed = ($Commit -and $errors.Count -eq 0)
    safety = [pscustomobject]@{
        sendsInput = $false
        clicksMouse = $false
        writesMemory = $false
        injectsDll = $false
        installsHook = $false
        patchesClient = $false
        modifiesPackets = $false
        writesProfile = ($Commit -and $errors.Count -eq 0)
    }
    backupPath = $backupPath
    points = [pscustomobject]@{
        total = $pointCount
        death = $deathPointCount
    }
    sourceNavigation = [pscustomobject]@{
        name = $sourceName
        configured = ($null -ne $sourceNav)
        summary = (Describe-Navigation $sourceNav)
    }
    changes = $changes
    warnings = $warnings
    errors = $errors
    next = @(
        "Run tools\Run-DeathReturnReadiness.ps1 to verify dry-run gate.",
        "Only execute live death return when the character is actually dead and live-death-return-execute gate says ready."
    )
}

$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# Death Return Bootstrap")
[void]$md.AppendLine()
[void]$md.AppendLine("- CapturedAt: $($report.capturedAt)")
[void]$md.AppendLine("- Profile: $Profile")
[void]$md.AppendLine("- Committed: $($report.committed)")
[void]$md.AppendLine("- Backup: $backupPath")
[void]$md.AppendLine("- Safety: no input, no click, no memory write, no injection, no hook.")
[void]$md.AppendLine("- SourceNavigation: $($report.sourceNavigation.name) / $($report.sourceNavigation.summary)")
[void]$md.AppendLine("- Points: total=$pointCount, death=$deathPointCount")
[void]$md.AppendLine()
[void]$md.AppendLine("## Changes")
foreach ($change in $changes) {
    [void]$md.AppendLine("- $($change.field): $($change.before) -> $($change.after)")
}
[void]$md.AppendLine()
[void]$md.AppendLine("## Warnings")
foreach ($warning in $warnings) {
    [void]$md.AppendLine("- $warning")
}
[void]$md.AppendLine()
[void]$md.AppendLine("## Errors")
foreach ($errorItem in $errors) {
    [void]$md.AppendLine("- $errorItem")
}
[void]$md.AppendLine()
[void]$md.AppendLine("## Evidence")
[void]$md.AppendLine("- JSON: $jsonPath")
Set-Content -LiteralPath $mdPath -Value $md.ToString() -Encoding UTF8

Write-Host "Death return bootstrap"
Write-Host "Committed=$($report.committed)"
Write-Host "SourceNavigation=$sourceName"
Write-Host "Points=$pointCount DeathPoints=$deathPointCount"
Write-Host "Warnings=$($warnings.Count) Errors=$($errors.Count)"
Write-Host "Markdown=$mdPath"
Write-Host "Json=$jsonPath"

if ($errors.Count -gt 0) {
    exit 1
}
exit 0
