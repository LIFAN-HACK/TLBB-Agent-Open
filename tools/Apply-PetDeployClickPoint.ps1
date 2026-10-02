param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$Profile = "",
    [int]$Rank = 0,
    [int]$X = 0,
    [int]$Bottom = 0,
    [switch]$Confirm,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$DefaultProfileName = -join ([char]0x65B9, [char]0x6848, [char]0x2D, [char]0x30, [char]0x36, [char]0x32, [char]0x31, [char]0x2D, [char]0x30, [char]0x39, [char]0x31, [char]0x36)
if ([string]::IsNullOrWhiteSpace($Profile)) {
    $Profile = $DefaultProfileName
}

$localizedAppName = -join ([char]0x9F99, [char]0x4F34, [char]0x884C)
$profilesDir = Join-Path $env:APPDATA ("{0}\profiles" -f $localizedAppName)
$profilePath = Join-Path $profilesDir ("{0}.json" -f $Profile)

$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
$diagProject = Join-Path $Root "tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = Join-Path $Root "artifacts\pet-deploy-apply\$stamp"
$jsonPath = Join-Path $outDir "pet-deploy-apply.json"
$mdPath = Join-Path $outDir "pet-deploy-apply.md"
$diagOutputPath = Join-Path $outDir "diagnostics-output.txt"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Fail-WithEvidence {
    param([string]$Message)

    $profileExistsForFailure = Test-Path -LiteralPath $profilePath
    $report = [pscustomobject]@{
        capturedAt = (Get-Date).ToString("o")
        ok = $false
        error = $Message
        safety = [pscustomobject]@{
            sendsInput = $false
            clicksMouse = $false
            writesProfile = $false
            writesMemory = $false
            injectsDll = $false
            installsHook = $false
            patchesClient = $false
            modifiesPackets = $false
        }
        profile = [pscustomobject]@{
            name = $Profile
            path = $profilePath
            exists = $profileExistsForFailure
        }
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    Set-Content -LiteralPath $mdPath -Encoding UTF8 -Value @(
        "# Pet Deploy Apply",
        "",
        "- OK: False",
        "- Error: $Message",
        "- Profile: $Profile",
        "- ProfilePath: $profilePath",
        "- Safety: no input, no click, no memory write, no injection, no hook."
    )
    throw $Message
}

$hasRank = $Rank -gt 0
$hasPoint = $X -gt 0 -and $Bottom -gt 0
if ($hasRank -eq $hasPoint) {
    Fail-WithEvidence "Specify exactly one mode: -Rank N, or -X n -Bottom n."
}
if (-not (Test-Path -LiteralPath $diagProject)) {
    Fail-WithEvidence "Diagnostics project not found: $diagProject"
}
if (-not (Test-Path -LiteralPath $profilePath)) {
    Fail-WithEvidence "Profile file not found: $profilePath"
}

$backupPath = ""
if ($Confirm) {
    $backupDir = Join-Path $Root "artifacts\pet-deploy-apply\backups"
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    $safeProfile = $Profile
    foreach ($badChar in [System.IO.Path]::GetInvalidFileNameChars()) {
        $safeProfile = $safeProfile.Replace($badChar, '_')
    }
    $backupPath = Join-Path $backupDir ("{0}-{1}.json" -f $safeProfile, $stamp)
    Copy-Item -LiteralPath $profilePath -Destination $backupPath -Force:$false
}

$diagArgs = @("run", "--no-build", "--project", $diagProject, "--")
if ($hasRank) {
    $diagArgs += @("pet-deploy-candidate-apply", "--latest", "--rank", $Rank.ToString(), "--profile", $Profile)
} else {
    $diagArgs += @("profile-recovery-bootstrap", "--profile", $Profile, "--pet-deploy-click", ("{0},{1}" -f $X, $Bottom))
}
if ($Confirm) {
    $diagArgs += "--commit"
}
if ($Force) {
    $diagArgs += "--force"
}

$diagOutput = & $dotnet @diagArgs 2>&1
$exitCode = $LASTEXITCODE
$diagText = ($diagOutput | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
Set-Content -LiteralPath $diagOutputPath -Value $diagText -Encoding UTF8

$profileAfter = $null
if (Test-Path -LiteralPath $profilePath) {
    $profileAfter = Get-Content -LiteralPath $profilePath -Raw -Encoding UTF8 | ConvertFrom-Json
}

$configuredByClick = $false
if ($profileAfter -ne $null) {
    $configuredByClick = ([int]$profileAfter.PetDeployActivationMode -eq 1) -and ([int]$profileAfter.PetDeployClickX -gt 0) -and ([int]$profileAfter.PetDeployClickBottomOffset -gt 0)
}

$modeName = if ($hasRank) { "rank" } else { "coordinate" }
$profileMode = $null
$profileClickX = $null
$profileClickBottom = $null
if ($profileAfter -ne $null) {
    $profileMode = [int]$profileAfter.PetDeployActivationMode
    $profileClickX = [int]$profileAfter.PetDeployClickX
    $profileClickBottom = [int]$profileAfter.PetDeployClickBottomOffset
}

$report = [pscustomobject]@{
    capturedAt = (Get-Date).ToString("o")
    ok = $exitCode -eq 0
    mode = $modeName
    requested = [pscustomobject]@{
        rank = $Rank
        x = $X
        bottom = $Bottom
        confirm = [bool]$Confirm
        force = [bool]$Force
    }
    safety = [pscustomobject]@{
        sendsInput = $false
        clicksMouse = $false
        writesProfile = [bool]$Confirm
        writesMemory = $false
        injectsDll = $false
        installsHook = $false
        patchesClient = $false
        modifiesPackets = $false
    }
    profile = [pscustomobject]@{
        name = $Profile
        path = $profilePath
        backup = $backupPath
        petDeployActivationMode = $profileMode
        petDeployClickX = $profileClickX
        petDeployClickBottomOffset = $profileClickBottom
        runtimeReadyByClick = $configuredByClick
    }
    diagnostics = [pscustomobject]@{
        exitCode = $exitCode
        output = $diagOutputPath
    }
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# Pet Deploy Apply")
[void]$md.AppendLine()
[void]$md.AppendLine("- OK: $($report.ok)")
[void]$md.AppendLine("- Mode: $($report.mode)")
[void]$md.AppendLine("- Confirm: $([bool]$Confirm)")
[void]$md.AppendLine("- Profile: $Profile")
[void]$md.AppendLine("- RuntimeReadyByClick: $configuredByClick")
[void]$md.AppendLine("- Click: x=$($report.profile.petDeployClickX), bottom=$($report.profile.petDeployClickBottomOffset)")
[void]$md.AppendLine("- Backup: $backupPath")
[void]$md.AppendLine("- DiagnosticsOutput: $diagOutputPath")
[void]$md.AppendLine("- Safety: no input, no click, no memory write, no injection, no hook.")
[void]$md.AppendLine()
[void]$md.AppendLine("## Diagnostics")
[void]$md.AppendLine('```text')
[void]$md.AppendLine($diagText)
[void]$md.AppendLine('```')
Set-Content -LiteralPath $mdPath -Value $md.ToString() -Encoding UTF8

Write-Host "Pet deploy apply"
Write-Host ("OK={0}" -f ($exitCode -eq 0))
Write-Host ("Profile={0}" -f $Profile)
Write-Host ("Confirm={0}" -f ([bool]$Confirm))
Write-Host ("RuntimeReadyByClick={0}" -f $configuredByClick)
Write-Host ("Json={0}" -f $jsonPath)
Write-Host ("Markdown={0}" -f $mdPath)

if ($exitCode -ne 0) {
    exit $exitCode
}






