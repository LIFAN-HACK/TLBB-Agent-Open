param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$Profile = "",
    [int]$Iterations = 5,
    [int]$IntervalSeconds = 5,
    [int]$PreviewDurationMs = 800
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$DefaultProfileName = -join ([char]0x65B9, [char]0x6848, [char]0x2D, [char]0x30, [char]0x36, [char]0x32, [char]0x31, [char]0x2D, [char]0x30, [char]0x39, [char]0x31, [char]0x36)
if ([string]::IsNullOrWhiteSpace($Profile)) {
    $Profile = $DefaultProfileName
}

if ($Iterations -lt 1) { throw "Iterations must be >= 1." }
if ($IntervalSeconds -lt 0) { throw "IntervalSeconds must be >= 0." }
if ($PreviewDurationMs -lt 200) { throw "PreviewDurationMs must be >= 200." }

$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = "dotnet"
}

$diagProject = Join-Path $Root "tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj"
if (-not (Test-Path -LiteralPath $diagProject)) {
    throw "Diagnostics project not found: $diagProject"
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = Join-Path $Root "artifacts\basic-hang-stability-evidence\$stamp"
$jsonPath = Join-Path $outDir "basic-hang-stability-evidence.json"
$mdPath = Join-Path $outDir "basic-hang-stability-evidence.md"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Get-RegexValue {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Default = ""
    )

    $m = [regex]::Match($Text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)
    if ($m.Success -and $m.Groups.Count -gt 1) {
        return $m.Groups[1].Value.Trim()
    }

    return $Default
}

function Convert-ToNullableInt {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $n = 0
    if ([int]::TryParse($Value.Trim(), [ref]$n)) { return $n }
    return $null
}

function Convert-ToNullableBool {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $v = $Value.Trim()
    if ($v -eq "True" -or $v -eq "true") { return $true }
    if ($v -eq "False" -or $v -eq "false") { return $false }
    return $null
}

function Parse-PreviewOutput {
    param(
        [string]$Text,
        [int]$ExitCode,
        [int]$Index,
        [string]$OutputPath
    )

    $recognition = [regex]::Match(
        $Text,
        "^Recognition target='(.*)', targetHp=([^,]*), hp=([^,]*), mp=([^,]*), pet=([^,]*), player=([^,]*), protectionRisk=([^,]*), petTarget=([^,]*), attackable=(.*)$",
        [System.Text.RegularExpressions.RegexOptions]::Multiline)

    $would = [regex]::Match(
        $Text,
        "^Would acquire=([^,]*), normalAttack=([^,]*), combatSkill=([^,]*), buff=([^,]*), anySkill=([^,]*), attack=([^,]*), recoveryKey=([^,]*), recoveryClick=([^,\r\n]*)$",
        [System.Text.RegularExpressions.RegexOptions]::Multiline)

    $runtime = [regex]::Match(
        $Text,
        "^RuntimeSnapshot provider=([^,]*), source=([^,]*), targetKind=([^,]*), targetName='(.*)', targetHp=([^,]*), attackable=(.*)$",
        [System.Text.RegularExpressions.RegexOptions]::Multiline)

    $protectionConflict = Convert-ToNullableBool (Get-RegexValue $Text "^ProtectionConflict=(.*)$")
    $hasPassLine = [regex]::IsMatch($Text, "^PASS\s*$", [System.Text.RegularExpressions.RegexOptions]::Multiline)
    $hasFailLine = [regex]::IsMatch($Text, "^FAIL\b|^FAIL:", [System.Text.RegularExpressions.RegexOptions]::Multiline)
    $evidencePath = Get-RegexValue $Text "^Evidence=(.*)$"

    $pass = $ExitCode -eq 0 -and $hasPassLine -and -not $hasFailLine -and $protectionConflict -ne $true

    $targetText = ""
    $targetHp = $null
    $playerHp = $null
    $playerMp = $null
    $petHp = $null
    $targetLooksLikePlayer = $null
    $targetProtectionRisk = $null
    $targetLooksLikePet = $null
    $targetAttackable = $null

    if ($recognition.Success) {
        $targetText = $recognition.Groups[1].Value
        $targetHp = Convert-ToNullableInt $recognition.Groups[2].Value
        $playerHp = Convert-ToNullableInt $recognition.Groups[3].Value
        $playerMp = Convert-ToNullableInt $recognition.Groups[4].Value
        $petHp = Convert-ToNullableInt $recognition.Groups[5].Value
        $targetLooksLikePlayer = Convert-ToNullableBool $recognition.Groups[6].Value
        $targetProtectionRisk = Convert-ToNullableBool $recognition.Groups[7].Value
        $targetLooksLikePet = Convert-ToNullableBool $recognition.Groups[8].Value
        $targetAttackable = Convert-ToNullableBool $recognition.Groups[9].Value
    }

    $wouldAcquire = $null
    $wouldNormalAttack = $null
    $wouldCombatSkill = $null
    $wouldBuff = $null
    $wouldAnySkill = $null
    $wouldAttack = $null
    $wouldRecoveryKey = $null
    $wouldRecoveryClick = $null

    if ($would.Success) {
        $wouldAcquire = Convert-ToNullableBool $would.Groups[1].Value
        $wouldNormalAttack = Convert-ToNullableBool $would.Groups[2].Value
        $wouldCombatSkill = Convert-ToNullableBool $would.Groups[3].Value
        $wouldBuff = Convert-ToNullableBool $would.Groups[4].Value
        $wouldAnySkill = Convert-ToNullableBool $would.Groups[5].Value
        $wouldAttack = Convert-ToNullableBool $would.Groups[6].Value
        $wouldRecoveryKey = Convert-ToNullableBool $would.Groups[7].Value
        $wouldRecoveryClick = Convert-ToNullableBool $would.Groups[8].Value
    }

    $runtimeProvider = ""
    $runtimeKind = ""
    $runtimeTargetName = ""
    if ($runtime.Success) {
        $runtimeProvider = $runtime.Groups[1].Value.Trim()
        $runtimeKind = $runtime.Groups[3].Value.Trim()
        $runtimeTargetName = $runtime.Groups[4].Value
    }

    [pscustomobject]@{
        index = $Index
        exitCode = $ExitCode
        pass = $pass
        outputPath = $OutputPath
        evidencePath = $evidencePath
        pid = Convert-ToNullableInt (Get-RegexValue $Text "^Pid=(.*)$")
        bitness = Get-RegexValue $Text "^Bitness=([^,]*),.*$"
        clientSize = Get-RegexValue $Text "^Bitness=[^,]*, size=([^,]*),.*$"
        allowed = Convert-ToNullableBool (Get-RegexValue $Text "^Bitness=[^,]*, size=[^,]*, allowed=(.*)$")
        backend = Get-RegexValue $Text "^Duration=[^,]*, backend=([^,]*),.*$"
        inputMode = Get-RegexValue $Text "^Duration=[^,]*, backend=[^,]*, inputMode=(.*)$"
        recognition = [pscustomobject]@{
            targetText = $targetText
            targetHp = $targetHp
            playerHp = $playerHp
            playerMp = $playerMp
            petHp = $petHp
            targetLooksLikePlayer = $targetLooksLikePlayer
            targetProtectionRisk = $targetProtectionRisk
            targetLooksLikePet = $targetLooksLikePet
            targetAttackable = $targetAttackable
        }
        runtimeSnapshot = [pscustomobject]@{
            provider = $runtimeProvider
            targetKind = $runtimeKind
            targetName = $runtimeTargetName
        }
        plannedAction = [pscustomobject]@{
            acquire = $wouldAcquire
            normalAttack = $wouldNormalAttack
            combatSkill = $wouldCombatSkill
            buff = $wouldBuff
            anySkill = $wouldAnySkill
            attack = $wouldAttack
            recoveryKey = $wouldRecoveryKey
            recoveryClick = $wouldRecoveryClick
            hotkeys = Get-RegexValue $Text "^Hotkeys=(.*)$"
            clicks = Convert-ToNullableInt (Get-RegexValue $Text "^Clicks=(.*)$")
        }
        protectionConflict = $protectionConflict
        passLine = $hasPassLine
        failLine = $hasFailLine
    }
}

$iterationsData = New-Object System.Collections.ArrayList

for ($i = 1; $i -le $Iterations; $i++) {
    $outputPath = Join-Path $outDir ("preview-{0:D2}.txt" -f $i)
    $args = @(
        "run",
        "--no-build",
        "--project",
        $diagProject,
        "--",
        "live-normal-hang-preview",
        "--duration-ms",
        "$PreviewDurationMs",
        "--profile",
        $Profile
    )

    $raw = & $dotnet @args 2>&1
    $exitCode = $LASTEXITCODE
    $text = ($raw | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    Set-Content -LiteralPath $outputPath -Value $text -Encoding UTF8

    [void]$iterationsData.Add((Parse-PreviewOutput -Text $text -ExitCode $exitCode -Index $i -OutputPath $outputPath))

    if ($i -lt $Iterations -and $IntervalSeconds -gt 0) {
        Start-Sleep -Seconds $IntervalSeconds
    }
}

$passes = @($iterationsData | Where-Object { $_.pass -eq $true })
$failures = @($iterationsData | Where-Object { $_.pass -ne $true })
$conflicts = @($iterationsData | Where-Object { $_.protectionConflict -eq $true })
$exitFailures = @($iterationsData | Where-Object { $_.exitCode -ne 0 })
$missingGame = @($iterationsData | Where-Object { $_.pid -eq $null -or $_.allowed -ne $true })
$plannedAttacks = @($iterationsData | Where-Object { $_.plannedAction.attack -eq $true -or $_.plannedAction.normalAttack -eq $true -or $_.plannedAction.combatSkill -eq $true })
$plannedRecovery = @($iterationsData | Where-Object { $_.plannedAction.recoveryKey -eq $true -or $_.plannedAction.recoveryClick -eq $true })

$allPass = $failures.Count -eq 0
$noProtectionConflict = $conflicts.Count -eq 0
$gameWindowOk = $missingGame.Count -eq 0
$readyForControlledBasicTest = $allPass -and $noProtectionConflict -and $gameWindowOk

$status = "PASS"
if (-not $readyForControlledBasicTest) {
    if ($exitFailures.Count -gt 0 -or $conflicts.Count -gt 0 -or $missingGame.Count -gt 0) {
        $status = "FAIL"
    } else {
        $status = "PARTIAL"
    }
}

$summary = [pscustomobject]@{
    capturedAt = (Get-Date).ToString("o")
    mode = "basic-hang-stability-evidence-no-input"
    safety = [pscustomobject]@{
        sendsInput = $false
        clicksMouse = $false
        writesProfile = $false
        readsOnlyGameWindow = $true
        memoryWrite = $false
        injection = $false
        hook = $false
    }
    parameters = [pscustomobject]@{
        root = $Root
        profile = $Profile
        iterations = $Iterations
        intervalSeconds = $IntervalSeconds
        previewDurationMs = $PreviewDurationMs
    }
    status = $status
    gates = [pscustomobject]@{
        allIterationsPass = $allPass
        noProtectionConflict = $noProtectionConflict
        gameWindowOk = $gameWindowOk
        readyForControlledBasicTest = $readyForControlledBasicTest
    }
    counts = [pscustomobject]@{
        total = $iterationsData.Count
        pass = $passes.Count
        fail = $failures.Count
        protectionConflicts = $conflicts.Count
        exitFailures = $exitFailures.Count
        plannedAttackIterations = $plannedAttacks.Count
        plannedRecoveryIterations = $plannedRecovery.Count
    }
    iterations = @($iterationsData)
}

$summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# Basic Hang Stability Evidence")
$lines.Add("")
$lines.Add("- CapturedAt: $($summary.capturedAt)")
$lines.Add("- Status: $status")
$lines.Add("- Profile: $Profile")
$lines.Add("- Iterations: $Iterations")
$lines.Add("- Preview duration: ${PreviewDurationMs}ms")
$lines.Add("- Safety: no input, no click, no profile write, no memory write, no injection, no hook.")
$lines.Add("")
$lines.Add("## Gates")
$lines.Add("")
$lines.Add("| Gate | Result |")
$lines.Add("|---|---:|")
$lines.Add("| All iterations PASS | $allPass |")
$lines.Add("| No protection conflict | $noProtectionConflict |")
$lines.Add("| Game window OK | $gameWindowOk |")
$lines.Add("| Ready for controlled basic test | $readyForControlledBasicTest |")
$lines.Add("")
$lines.Add("## Iterations")
$lines.Add("")
$lines.Add("| # | Pass | PID | Size | Backend | Target | TargetHp | HP | MP | Pet | AttackPlan | RecoveryPlan | Conflict | Evidence |")
$lines.Add("|---:|---:|---:|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|")
foreach ($item in $iterationsData) {
    $attackPlan = ($item.plannedAction.attack -eq $true -or $item.plannedAction.normalAttack -eq $true -or $item.plannedAction.combatSkill -eq $true)
    $recoveryPlan = ($item.plannedAction.recoveryKey -eq $true -or $item.plannedAction.recoveryClick -eq $true)
    $target = $item.recognition.targetText
    if ([string]::IsNullOrWhiteSpace($target)) { $target = "-" }
    $evidence = $item.evidencePath
    if ([string]::IsNullOrWhiteSpace($evidence)) { $evidence = $item.outputPath }
    $lines.Add("| $($item.index) | $($item.pass) | $($item.pid) | $($item.clientSize) | $($item.backend) | $target | $($item.recognition.targetHp) | $($item.recognition.playerHp) | $($item.recognition.playerMp) | $($item.recognition.petHp) | $attackPlan | $recoveryPlan | $($item.protectionConflict) | ``$evidence`` |")
}
$lines.Add("")
$lines.Add("## Meaning")
$lines.Add("")
$lines.Add("This is a no-input preflight. It proves the diagnostics can repeatedly bind the current 32-bit window, read recognition state, and predict actions without a protected-target conflict.")
$lines.Add("It does not prove real 15-30 minute combat stability by itself; that still requires a controlled live run with logs.")
$lines.Add("")
$lines.Add("## Evidence")
$lines.Add("")
$lines.Add("- JSON: $jsonPath")
$lines.Add("- Markdown: $mdPath")
Set-Content -LiteralPath $mdPath -Value $lines -Encoding UTF8

Write-Host "Status=$status"
Write-Host "ReadyForControlledBasicTest=$readyForControlledBasicTest"
Write-Host "Json=$jsonPath"
Write-Host "Markdown=$mdPath"

if ($status -eq "FAIL") {
    exit 1
}
