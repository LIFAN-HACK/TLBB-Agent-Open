param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$auditScript = Join-Path $Root "tools\Run-MainlineP0Audit.ps1"
$petDeployScript = Join-Path $Root "tools\Run-PetDeployReadiness.ps1"
$deathReturnScript = Join-Path $Root "tools\Run-DeathReturnReadiness.ps1"
$targetSampleScript = Join-Path $Root "tools\Run-TargetSampleReadiness.ps1"
$appSmokeScript = Join-Path $Root "tools\Run-AppSmokeTabs.ps1"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = Join-Path $Root "artifacts\stable-test-readiness\$stamp"
$jsonPath = Join-Path $outDir "stable-test-readiness.json"
$mdPath = Join-Path $outDir "stable-test-readiness.md"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$auditArgs = @("-ExecutionPolicy", "Bypass", "-File", $auditScript)
if ($SkipBuild) {
    $auditArgs += "-SkipBuild"
}

$auditOutput = & powershell @auditArgs 2>&1
$auditExit = $LASTEXITCODE
$auditText = ($auditOutput | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
Set-Content -LiteralPath (Join-Path $outDir "mainline-p0-audit-output.txt") -Value $auditText -Encoding UTF8

$auditJsonPath = $null
if ($auditText -match "(?m)^Json=(.+)$") {
    $auditJsonPath = $Matches[1].Trim()
}

if ([string]::IsNullOrWhiteSpace($auditJsonPath) -or -not (Test-Path -LiteralPath $auditJsonPath)) {
    throw "Cannot locate P0 audit JSON. AuditExit=$auditExit"
}

$audit = Get-Content -LiteralPath $auditJsonPath -Encoding UTF8 -Raw | ConvertFrom-Json

function Invoke-ScriptGate {
    param(
        [string]$Name,
        [string]$Script,
        [string[]]$ExtraArgs
    )

    if (-not (Test-Path -LiteralPath $Script)) {
        return [pscustomobject]@{
            name = $Name
            ok = $false
            output = "missing script: $Script"
            log = ""
            json = ""
            data = $null
        }
    }

    $args = @("-ExecutionPolicy", "Bypass", "-File", $Script, "-Root", $Root) + $ExtraArgs
    $output = & powershell @args 2>&1
    $exitCode = $LASTEXITCODE
    $text = ($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    $log = Join-Path $outDir "$Name-output.txt"
    Set-Content -LiteralPath $log -Value $text -Encoding UTF8
    $json = ""
    if ($text -match "(?m)^Json=(.+)$") {
        $json = $Matches[1].Trim()
    }
    $data = $null
    if (-not [string]::IsNullOrWhiteSpace($json) -and (Test-Path -LiteralPath $json)) {
        $data = Get-Content -LiteralPath $json -Encoding UTF8 -Raw | ConvertFrom-Json
    }

    [pscustomobject]@{
        name = $Name
        ok = $exitCode -eq 0
        output = $text
        log = $log
        json = $json
        data = $data
    }
}

function Get-AuditItem {
    param([string]$Key)
    $audit.items | Where-Object { $_.key -eq $Key } | Select-Object -First 1
}

function Test-Pass {
    param([string]$Key)
    $item = Get-AuditItem $Key
    return $item -ne $null -and $item.status -eq "PASS"
}

function Test-NoHardBlock {
    $hard = @($audit.items | Where-Object { $_.status -in @("FAIL", "BLOCKED", "MISSING") })
    return $hard.Count -eq 0
}

$profile = Get-AuditItem "profile"
$recovery = Get-AuditItem "profile-recovery-bootstrap"
$samples = Get-AuditItem "target-samples"
$returnGate = Get-AuditItem "return"
$bag = Get-AuditItem "bag-cleanup"

$profileName = -join ([char]0x65B9, [char]0x6848, [char]0x2D, [char]0x30, [char]0x36, [char]0x32, [char]0x31, [char]0x2D, [char]0x30, [char]0x39, [char]0x31, [char]0x36)
if ($profile -ne $null -and $profile.evidence -match "profile-health") {
    $profileLogPath = (($profile.evidence -split "profile-health:\s*", 2)[-1]).Trim()
    if (Test-Path -LiteralPath $profileLogPath) {
        $profileLogText = Get-Content -LiteralPath $profileLogPath -Encoding UTF8 -Raw
        if ($profileLogText -match "(?m)^Profile=([^,\r\n]+)") {
            $profileName = $Matches[1].Trim()
        }
    }
}

$petGate = Invoke-ScriptGate "pet-deploy-readiness" $petDeployScript @("-Profile", $profileName)
$deathGate = Invoke-ScriptGate "death-return-readiness" $deathReturnScript @("-Profile", $profileName)
$targetSampleGate = Invoke-ScriptGate "target-sample-readiness" $targetSampleScript @()
$appSmokeGate = Invoke-ScriptGate "app-smoke-tabs" $appSmokeScript @("-AutoBind", "-CloseLaunched")

$petRuntimeReady = $petGate.data -ne $null -and $petGate.data.gates.runtimeReady -eq $true
$deathDryRunReady = $deathGate.data -ne $null -and $deathGate.data.gates.readyForDryRun -eq $true
$targetSamplesReady = $targetSampleGate.data -ne $null -and $targetSampleGate.data.gates.targetSampleReady -eq $true
$appSmokeReady = $appSmokeGate.data -ne $null -and $appSmokeGate.data.ok -eq $true

$basicHangReady =
    $appSmokeReady -and
    (Test-NoHardBlock) -and
    (Test-Pass "readiness") -and
    (Test-Pass "combat-samples") -and
    (Test-Pass "normal-hang")

$returnDryRunReady =
    $basicHangReady -and
    (Test-Pass "return")

$fullP0Ready =
    $audit.overall -eq "PASS" -and
    $petRuntimeReady -and
    $deathDryRunReady -and
    $targetSamplesReady

$warnings = New-Object System.Collections.ArrayList
foreach ($item in $audit.items) {
    if ($item.status -ne "PASS") {
        [void]$warnings.Add([pscustomobject]@{
            key = $item.key
            title = $item.title
            status = $item.status
            next = $item.next
            evidence = $item.evidence
        })
    }
}

$phase1 = [pscustomobject]@{
    name = "basic-hang-stability"
    label = "Basic hang stability test"
    ready = $basicHangReady
    duration = "15-30 minutes"
    scope = "Bind x86 game window, acquire target, one normal attack per new target, skill timers, target switching, hp/mp recovery, player/pet guard code path."
    condition = "No FAIL/BLOCKED/MISSING gates; combat sample replay and normal-hang diagnostics must PASS. PARTIAL profile/sample warnings are allowed for this stage."
}
$phase2 = [pscustomobject]@{
    name = "return-point-dry-run"
    label = "Return point dry run"
    ready = $returnDryRunReady
    duration = "5-10 minutes"
    scope = "Current map/coordinate recognition, fixed return point gate, stuck recovery preview, log review before any long run."
    condition = "Basic hang is ready and return gate is PASS."
}
$phase3 = [pscustomobject]@{
    name = "full-p0-stability"
    label = "Full P0 stability test"
    ready = $fullP0Ready
    duration = "60+ minutes"
    scope = "Hang, player guard, pet handling, return/death return, cleanup candidates, autosave, recognition evidence."
    condition = "P0 audit overall must be PASS."
}
$phases = @($phase1, $phase2, $phase3)

$auditMarkdownPath = ""
if ($auditText -match "(?m)^Markdown=(.+)$") {
    $auditMarkdownPath = $Matches[1].Trim()
}

if ($fullP0Ready) {
    $currentGate = "FULL_P0_READY"
    $currentGateText = "Full P0 stability test can start."
} elseif ($returnDryRunReady) {
    $currentGate = "BASIC_AND_RETURN_DRY_RUN_READY"
    $currentGateText = "Basic hang stability test and return dry run can start; full P0 still needs remaining PARTIAL gates."
} elseif ($basicHangReady) {
    $currentGate = "BASIC_HANG_READY"
    $currentGateText = "Basic hang stability test can start; return/full P0 are not recommended yet."
} else {
    $currentGate = "NOT_READY"
    $currentGateText = "Stable testing is not recommended; fix hard gates first."
}

$keyRemaining = New-Object System.Collections.ArrayList
if ($profile -ne $null -and $profile.status -ne "PASS") { [void]$keyRemaining.Add("profile: $($profile.next)") }
if ($recovery -ne $null -and $recovery.status -ne "PASS") { [void]$keyRemaining.Add("recovery: $($recovery.next)") }
if ($samples -ne $null -and $samples.status -ne "PASS") { [void]$keyRemaining.Add("target-samples: $($samples.next)") }
if ($returnGate -ne $null -and $returnGate.status -ne "PASS") { [void]$keyRemaining.Add("return: $($returnGate.next)") }
if ($bag -ne $null -and $bag.status -ne "PASS") { [void]$keyRemaining.Add("bag-cleanup: $($bag.next)") }
if (-not $petRuntimeReady) { [void]$keyRemaining.Add("pet-deploy: runtime action is not configured or not verified.") }
if (-not $deathDryRunReady) { [void]$keyRemaining.Add("death-return: dry-run gate is not ready.") }
if (-not $targetSamplesReady) { [void]$keyRemaining.Add("target-samples: recommended protection coverage is not ready.") }
if (-not $appSmokeReady) { [void]$keyRemaining.Add("app-smoke-tabs: app launch/tab switching/start-button gate is not ready.") }

$report = [pscustomobject]@{
    capturedAt = (Get-Date).ToString("o")
    mode = "stable-test-readiness-no-business-action"
    audit = [pscustomobject]@{
        exitCode = $auditExit
        overall = $audit.overall
        json = $auditJsonPath
        markdown = $auditMarkdownPath
    }
    subGates = [pscustomobject]@{
        petDeploy = [pscustomobject]@{
            ok = $petGate.ok
            runtimeReady = $petRuntimeReady
            json = $petGate.json
            log = $petGate.log
        }
        deathReturn = [pscustomobject]@{
            ok = $deathGate.ok
            readyForDryRun = $deathDryRunReady
            json = $deathGate.json
            log = $deathGate.log
        }
        targetSamples = [pscustomobject]@{
            ok = $targetSampleGate.ok
            ready = $targetSamplesReady
            json = $targetSampleGate.json
            log = $targetSampleGate.log
        }
        appSmokeTabs = [pscustomobject]@{
            ok = $appSmokeGate.ok
            ready = $appSmokeReady
            json = $appSmokeGate.json
            log = $appSmokeGate.log
        }
    }
    safety = $audit.safety
    currentGate = $currentGate
    currentGateText = $currentGateText
    phases = $phases
    blockingWarnings = $warnings
    keyRemaining = $keyRemaining
}

$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# LongBanXing Stable Test Readiness")
[void]$md.AppendLine()
[void]$md.AppendLine("- CapturedAt: $($report.capturedAt)")
[void]$md.AppendLine("- P0 audit: $($audit.overall)")
[void]$md.AppendLine("- Pet deploy runtime ready: $petRuntimeReady")
[void]$md.AppendLine("- Death return dry-run ready: $deathDryRunReady")
[void]$md.AppendLine("- Target samples ready: $targetSamplesReady")
[void]$md.AppendLine("- App smoke tabs ready: $appSmokeReady")
[void]$md.AppendLine("- Current gate: $currentGate")
[void]$md.AppendLine("- Current conclusion: $currentGateText")
[void]$md.AppendLine("- Safety: no input, no click, no sell, no memory write, no injection, no hook.")
[void]$md.AppendLine()
[void]$md.AppendLine("## Test phases")
[void]$md.AppendLine()
[void]$md.AppendLine("| Phase | Ready | Duration | Scope | Condition |")
[void]$md.AppendLine("|---|---:|---|---|---|")
foreach ($phase in $phases) {
    [void]$md.AppendLine("| $($phase.label) | $($phase.ready) | $($phase.duration) | $($phase.scope) | $($phase.condition) |")
}
[void]$md.AppendLine()
[void]$md.AppendLine("## Remaining gates")
if ($warnings.Count -eq 0) {
    [void]$md.AppendLine()
    [void]$md.AppendLine("None.")
} else {
    foreach ($warning in $warnings) {
        $next = (($warning.next -replace "(\r\n|\r|\n)", " ") -replace "\|", "/")
        [void]$md.AppendLine("- $($warning.title): $($warning.status). $next")
    }
}
if (-not $petRuntimeReady) {
    [void]$md.AppendLine("- Pet deploy: runtime action is not configured or verified.")
}
if (-not $deathDryRunReady) {
    [void]$md.AppendLine("- Death return: dry-run gate is not ready.")
}
if (-not $targetSamplesReady) {
    [void]$md.AppendLine("- Target samples: recommended protection coverage is not ready.")
}
if (-not $appSmokeReady) {
    [void]$md.AppendLine("- App smoke tabs: launch/tab switching/start-button gate is not ready.")
}
[void]$md.AppendLine()
[void]$md.AppendLine("## Evidence")
[void]$md.AppendLine()
[void]$md.AppendLine("- Stable readiness JSON: $jsonPath")
[void]$md.AppendLine("- P0 audit JSON: $auditJsonPath")
[void]$md.AppendLine("- P0 audit markdown: $auditMarkdownPath")
[void]$md.AppendLine("- P0 audit output: $(Join-Path $outDir "mainline-p0-audit-output.txt")")
[void]$md.AppendLine("- Pet deploy readiness JSON: $($petGate.json)")
[void]$md.AppendLine("- Pet deploy readiness output: $($petGate.log)")
[void]$md.AppendLine("- Death return readiness JSON: $($deathGate.json)")
[void]$md.AppendLine("- Death return readiness output: $($deathGate.log)")
[void]$md.AppendLine("- Target sample readiness JSON: $($targetSampleGate.json)")
[void]$md.AppendLine("- Target sample readiness output: $($targetSampleGate.log)")
[void]$md.AppendLine("- App smoke tabs JSON: $($appSmokeGate.json)")
[void]$md.AppendLine("- App smoke tabs output: $($appSmokeGate.log)")
Set-Content -LiteralPath $mdPath -Value $md.ToString() -Encoding UTF8

Write-Host "Stable test readiness"
Write-Host "Overall=$($audit.overall)"
Write-Host "PetDeployRuntimeReady=$petRuntimeReady"
Write-Host "DeathReturnDryRunReady=$deathDryRunReady"
Write-Host "TargetSamplesReady=$targetSamplesReady"
Write-Host "AppSmokeTabsReady=$appSmokeReady"
Write-Host "CurrentGate=$currentGate"
Write-Host "CurrentConclusion=$currentGateText"
foreach ($phase in $phases) {
    Write-Host ("{0}={1} ({2})" -f $phase.name, $phase.ready, $phase.duration)
}
Write-Host "Markdown=$mdPath"
Write-Host "Json=$jsonPath"

exit 0

