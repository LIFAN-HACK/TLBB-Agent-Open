param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [switch]$SkipBuild,
    [switch]$SkipReadOnlyProbe
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
$solution = Join-Path $Root "TlbbAgent.sln"
$diagProject = Join-Path $Root "tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = Join-Path $Root "artifacts\mainline-p0-audit\$stamp"
$jsonPath = Join-Path $outDir "mainline-p0-audit.json"
$mdPath = Join-Path $outDir "mainline-p0-audit.md"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Invoke-Capture {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string[]]$Args
    )

    $output = & $dotnet @Args 2>&1
    $exitCode = $LASTEXITCODE
    $text = ($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    Set-Content -LiteralPath (Join-Path $outDir "$Name.txt") -Value $text -Encoding UTF8
    [pscustomobject]@{
        name = $Name
        exitCode = $exitCode
        ok = ($exitCode -eq 0)
        output = $text
        log = (Join-Path $outDir "$Name.txt")
    }
}

function Add-Item {
    param(
        [System.Collections.ArrayList]$Items,
        [Parameter(Mandatory = $true)][string]$Key,
        [Parameter(Mandatory = $true)][string]$Title,
        [Parameter(Mandatory = $true)][string]$Status,
        [Parameter(Mandatory = $true)][string]$Evidence,
        [Parameter(Mandatory = $true)][string]$Next
    )

    [void]$Items.Add([pscustomobject]@{
        key = $Key
        title = $Title
        status = $Status
        evidence = $Evidence
        next = $Next
    })
}

function Get-LatestBagCleanupEvidence {
    $dir = Join-Path $Root "artifacts\live-bag-cleanup"
    if (-not (Test-Path -LiteralPath $dir)) { return $null }
    Get-ChildItem -LiteralPath $dir -Filter "bag-cleanup-*.json" -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

$commands = [ordered]@{}

if (-not $SkipBuild) {
    $commands.build = Invoke-Capture -Name "build" -Args @("build", $solution, "-v", "minimal", "/nr:false")
}

$commands.readiness = Invoke-Capture -Name "readiness" -Args @("run", "--no-build", "--project", $diagProject, "--", "readiness")
$commands.profileHealth = Invoke-Capture -Name "profile-health" -Args @("run", "--no-build", "--project", $diagProject, "--", "profile-health")
$commands.profileRecoveryBootstrap = Invoke-Capture -Name "profile-recovery-bootstrap" -Args @("run", "--no-build", "--project", $diagProject, "--", "profile-recovery-bootstrap")

$activeProfile = ""
if ($commands.profileHealth.output -match "(?m)^Profile=([^,\r\n]+)") {
    $activeProfile = $Matches[1].Trim()
}

$commands.combatSampleHealth = Invoke-Capture -Name "combat-sample-health" -Args @("run", "--no-build", "--project", $diagProject, "--", "combat-sample-health")
$commands.combatSampleNext = Invoke-Capture -Name "combat-sample-next" -Args @("run", "--no-build", "--project", $diagProject, "--", "combat-sample-next")
$commands.combatSamples = Invoke-Capture -Name "combat-samples" -Args @("run", "--no-build", "--project", $diagProject, "--", "combat-samples")
$commands.normalHang = Invoke-Capture -Name "normal-hang" -Args @("run", "--no-build", "--project", $diagProject, "--", "normal-hang")
$commands.petDeployCalibration = Invoke-Capture -Name "live-pet-deploy-calibration" -Args @("run", "--no-build", "--project", $diagProject, "--", "live-pet-deploy-calibration")
$returnReadinessArgs = @("run", "--no-build", "--project", $diagProject, "--", "return-readiness")
if (-not [string]::IsNullOrWhiteSpace($activeProfile)) {
    $returnReadinessArgs += @("--profile", $activeProfile)
}
$commands.returnReadiness = Invoke-Capture -Name "return-readiness" -Args $returnReadinessArgs
if (-not $SkipReadOnlyProbe) {
    $commands.readOnlyProbe = Invoke-Capture -Name "readonly-probe" -Args @("run", "--no-build", "--project", $diagProject, "--", "readonly-probe")
}

$items = New-Object System.Collections.ArrayList

$readinessOk = $commands.readiness.ok -and $commands.readiness.output -match "(?m)^PASS$"
Add-Item $items "readiness" "Delivery gate / frontend clean / auth-chain clean" `
    ($(if ($readinessOk) { "PASS" } else { "FAIL" })) `
    "readiness: $($commands.readiness.log)" `
    ($(if ($readinessOk) { "Keep current delivery gate." } else { "Fix readiness failures first." }))

$profile = $commands.profileHealth.output
$profileWarnings = @([regex]::Matches($profile, "(?m)^WARN: .+$") | ForEach-Object { $_.Value })
$profileStatus = if ($profileWarnings.Count -eq 0) { "PASS" } else { "PARTIAL" }
Add-Item $items "profile" "Current profile health" $profileStatus `
    "profile-health: $($commands.profileHealth.log)" `
    ($(if ($profileWarnings.Count -eq 0) { "No profile gate warnings." } else { ($profileWarnings -join " | ") }))

$recoveryBootstrap = $commands.profileRecoveryBootstrap.output
$recoveryWarnings = @([regex]::Matches($recoveryBootstrap, "(?m)^WARN: .+$") | ForEach-Object { $_.Value })
$recoveryErrors = @([regex]::Matches($recoveryBootstrap, "(?m)^ERROR: .+$") | ForEach-Object { $_.Value })
$recoveryChanges = @([regex]::Matches($recoveryBootstrap, "(?m)^CHANGE: .+$") | ForEach-Object { $_.Value })
$recoveryStatus = if ($recoveryErrors.Count -gt 0) {
    "BLOCKED"
} elseif ($recoveryWarnings.Count -gt 0) {
    "PARTIAL"
} else {
    "PASS"
}
Add-Item $items "profile-recovery-bootstrap" "Recovery action bootstrap preview" $recoveryStatus `
    "profile-recovery-bootstrap: $($commands.profileRecoveryBootstrap.log)" `
    ($(if ($recoveryErrors.Count -gt 0) {
        ($recoveryErrors -join " | ")
    } elseif ($recoveryWarnings.Count -gt 0) {
        ($recoveryWarnings -join " | ")
    } elseif ($recoveryChanges.Count -gt 0) {
        ($recoveryChanges -join " | ")
    } else {
        "No recovery bootstrap changes required."
    }))

$sampleNext = $commands.combatSampleNext.output
$sampleReady = $sampleNext -match "RecommendedReady=True"
$sampleCounts = if ($sampleNext -match "ReadyCounts=(.+)") { $Matches[1].Trim() } else { "sample counts not parsed" }
Add-Item $items "target-samples" "Player / boss / pet / no-target sample coverage" `
    ($(if ($sampleReady) { "PASS" } else { "PARTIAL" })) `
    "combat-sample-next: $($commands.combatSampleNext.log)" `
    ($(if ($sampleReady) { "Sample coverage reached recommended gate." } else { $sampleCounts }))

$combatReplayOk = $commands.combatSamples.ok -and $commands.combatSamples.output -match "(?m)^PASS$"
Add-Item $items "combat-samples" "Target sample replay" `
    ($(if ($combatReplayOk) { "PASS" } else { "FAIL" })) `
    "combat-samples: $($commands.combatSamples.log)" `
    ($(if ($combatReplayOk) { "All sample replays passed." } else { "Fix failed samples or recognition logic." }))

$normalHangOk = $commands.normalHang.ok -and $commands.normalHang.output -match "(?m)^PASS$"
Add-Item $items "normal-hang" "Normal hang / target switching / player protection / recovery code path" `
    ($(if ($normalHangOk) { "PASS" } else { "FAIL" })) `
    "normal-hang: $($commands.normalHang.log)" `
    ($(if ($normalHangOk) { "Continue long live-scenario validation." } else { "Fix normal-hang failures first." }))

$petDeployCalibrationOk = $commands.petDeployCalibration.ok -and $commands.petDeployCalibration.output -match "(?m)^PASS$"
$petDeployEvidence = if ($commands.petDeployCalibration.output -match "(?m)^Evidence=(.+)$") { $Matches[1].Trim() } else { $commands.petDeployCalibration.log }
Add-Item $items "pet-deploy-calibration" "Pet deploy click calibration evidence" `
    ($(if ($petDeployCalibrationOk) { "PASS" } else { "PARTIAL" })) `
    "live-pet-deploy-calibration: $($commands.petDeployCalibration.log)" `
    ($(if ($petDeployCalibrationOk) { "Calibration overlay generated; after selecting the numbered point, run pet-deploy-candidate-apply --rank N --latest --commit." } else { "Run live-pet-deploy-calibration with Game.exe visible and verify overlay." }))

$returnText = $commands.returnReadiness.output
$returnLevel = if ($returnText -match "Level=([A-Za-z]+)") { $Matches[1] } else { "Unknown" }
$returnSummary = if ($returnText -match "Summary=(.+)") { $Matches[1].Trim() } else { "return summary not parsed" }
$returnStatus = switch ($returnLevel) {
    "Ready" { "PASS" }
    "Warning" { "PARTIAL" }
    default { "BLOCKED" }
}
Add-Item $items "return" "Return point / death return / stuck recovery gate" $returnStatus `
    "return-readiness: $($commands.returnReadiness.log)" `
    $returnSummary

if ($commands.Contains("readOnlyProbe")) {
    $probeOk = $commands.readOnlyProbe.ok -and $commands.readOnlyProbe.output -match "(?m)^PASS$"
    Add-Item $items "readonly-probe" "Read-only process evidence" `
        ($(if ($probeOk) { "PASS" } else { "FAIL" })) `
        "readonly-probe: $($commands.readOnlyProbe.log)" `
        ($(if ($probeOk) { "Read-only evidence generated; no game-state memory read/write." } else { "Check Game.exe state or probe output." }))
}

$bagEvidence = Get-LatestBagCleanupEvidence
if ($bagEvidence -eq $null) {
    Add-Item $items "bag-cleanup" "Bag cleanup read-only recognition / candidate protection" "MISSING" `
        "No artifacts\live-bag-cleanup\bag-cleanup-*.json found" `
        "Open bag and run live-bag-cleanup read-only diagnostics; default does not sell."
} else {
    $bag = Get-Content -LiteralPath $bagEvidence.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    $safeNoSell = ($bag.safety.sellsItems -eq $false) -and ($bag.safety.modifiesInventory -eq $false)
    $detected = [string]$bag.status -ne "NOT_DETECTED"
    $status = if ($safeNoSell -and $detected) { "PASS" } elseif ($safeNoSell) { "PARTIAL" } else { "FAIL" }
    $next = if ($detected) {
        "Bag candidates generated; default selling still disabled."
    } else {
        "Latest evidence did not detect a reliable bag grid; open bag or calibrate first slot."
    }
    Add-Item $items "bag-cleanup" "Bag cleanup read-only recognition / candidate protection" $status `
        "latest bag cleanup: $($bagEvidence.FullName)" `
        $next
}

$blocking = @($items | Where-Object { $_.status -in @("FAIL", "BLOCKED", "MISSING") })
$partial = @($items | Where-Object { $_.status -eq "PARTIAL" })
$overall = if ($blocking.Count -gt 0) {
    "BLOCKED"
} elseif ($partial.Count -gt 0) {
    "PARTIAL"
} else {
    "PASS"
}

$report = [pscustomobject]@{
    capturedAt = (Get-Date).ToString("o")
    mode = "mainline-p0-audit-no-business-action"
    root = $Root
    overall = $overall
    safety = [pscustomobject]@{
        sendsInput = $false
        clicksMouse = $false
        sellsItems = $false
        modifiesInventory = $false
        writesMemory = $false
        injectsDll = $false
        installsHook = $false
        patchesClient = $false
        modifiesPackets = $false
    }
    items = $items
    commandLogs = $commands.GetEnumerator() | ForEach-Object {
        [pscustomobject]@{
            name = $_.Key
            exitCode = $_.Value.exitCode
            ok = $_.Value.ok
            log = $_.Value.log
        }
    }
}

$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# LongBanXing P0 Mainline Audit")
[void]$md.AppendLine()
[void]$md.AppendLine("- CapturedAt: $($report.capturedAt)")
[void]$md.AppendLine("- Overall: $overall")
[void]$md.AppendLine("- Safety: no input, no click, no sell, no memory write, no injection, no hook.")
[void]$md.AppendLine()
[void]$md.AppendLine("| Item | Status | Evidence | Next |")
[void]$md.AppendLine("|---|---|---|---|")
foreach ($item in $items) {
    $next = (($item.next -replace "(\r\n|\r|\n)", " / ") -replace "\|", "/")
    $evidence = (($item.evidence -replace "(\r\n|\r|\n)", " / ") -replace "\|", "/")
    [void]$md.AppendLine("| $($item.title) | $($item.status) | $evidence | $next |")
}
[void]$md.AppendLine()
[void]$md.AppendLine("## Command Logs")
foreach ($entry in $report.commandLogs) {
    [void]$md.AppendLine("- $($entry.name): exit=$($entry.exitCode), log=$($entry.log)")
}
Set-Content -LiteralPath $mdPath -Value $md.ToString() -Encoding UTF8

Write-Host "Mainline P0 audit"
Write-Host "Overall=$overall"
foreach ($item in $items) {
    Write-Host ("{0}: {1} - {2}" -f $item.status, $item.title, $item.next)
}
Write-Host "Markdown=$mdPath"
Write-Host "Json=$jsonPath"

exit 0
