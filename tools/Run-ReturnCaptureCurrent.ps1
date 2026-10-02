param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$Profile = "",
    [string]$Kind = "fixed",
    [string]$Label = "current-return-point",
    [switch]$Commit
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$DefaultProfileName = -join ([char]0x65B9, [char]0x6848, [char]0x2D, [char]0x30, [char]0x36, [char]0x32, [char]0x31, [char]0x2D, [char]0x30, [char]0x39, [char]0x31, [char]0x36)
if ([string]::IsNullOrWhiteSpace($Profile)) {
    $Profile = $DefaultProfileName
}

$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
$diagProject = Join-Path $Root "tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = Join-Path $Root "artifacts\return-capture-current-wrapper\$stamp"
$jsonPath = Join-Path $outDir "return-capture-current-wrapper.json"
$mdPath = Join-Path $outDir "return-capture-current-wrapper.md"
$rawPath = Join-Path $outDir "return-capture-current-output.txt"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$diagArgs = @(
    "run", "--no-build", "--project", $diagProject, "--",
    "return-capture-current",
    "--profile", $Profile,
    "--kind", $Kind,
    "--label", $Label
)
if ($Commit) {
    $diagArgs += "--commit"
}

$output = & $dotnet @diagArgs 2>&1
$exitCode = $LASTEXITCODE
$text = ($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
Set-Content -LiteralPath $rawPath -Value $text -Encoding UTF8

function Get-LineValue {
    param([string]$Name)
    if ($text -match ("(?m)^{0}=(.+)$" -f [regex]::Escape($Name))) {
        return $Matches[1].Trim()
    }
    return ""
}

$success = Get-LineValue "Success"
$message = Get-LineValue "Message"
$point = Get-LineValue "Point"
$navigation = Get-LineValue "Navigation"
$diagnosticEvidence = Get-LineValue "Evidence"
$ready = $exitCode -eq 0 -and $success -eq "True"

$mode = "return-capture-current-preview-wrapper"
if ($Commit) {
    $mode = "return-capture-current-commit-wrapper"
}

$report = [pscustomobject]@{
    capturedAt = (Get-Date).ToString("o")
    mode = $mode
    safety = [pscustomobject]@{
        sendsInput = $false
        clicksMouse = $false
        writesProfile = [bool]$Commit
        writesMemory = $false
        injectsDll = $false
        installsHook = $false
        patchesClient = $false
    }
    profile = $Profile
    kind = $Kind
    label = $Label
    commit = [bool]$Commit
    exitCode = $exitCode
    ready = $ready
    success = $success
    message = $message
    point = $point
    navigation = $navigation
    diagnosticEvidence = $diagnosticEvidence
    rawOutput = $rawPath
}

$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# Return Capture Current")
[void]$md.AppendLine()
[void]$md.AppendLine("- CapturedAt: $($report.capturedAt)")
[void]$md.AppendLine("- Profile: $Profile")
[void]$md.AppendLine("- Kind: $Kind")
[void]$md.AppendLine("- Label: $Label")
[void]$md.AppendLine("- Commit: $([bool]$Commit)")
[void]$md.AppendLine("- Ready: $ready")
[void]$md.AppendLine("- Success: $success")
[void]$md.AppendLine("- Safety: no input, no click, no memory write, no injection, no hook; profile write only when Commit is true.")
[void]$md.AppendLine()
[void]$md.AppendLine("## Result")
[void]$md.AppendLine()
[void]$md.AppendLine("- Message: $message")
[void]$md.AppendLine("- Point: $point")
[void]$md.AppendLine("- Navigation: $navigation")
[void]$md.AppendLine()
[void]$md.AppendLine("## Evidence")
[void]$md.AppendLine("- Wrapper JSON: $jsonPath")
[void]$md.AppendLine("- Raw output: $rawPath")
[void]$md.AppendLine("- Diagnostic JSON: $diagnosticEvidence")
Set-Content -LiteralPath $mdPath -Value $md.ToString() -Encoding UTF8

Write-Host "Return capture current"
Write-Host "Ready=$ready"
Write-Host "Commit=$([bool]$Commit)"
Write-Host "Success=$success"
Write-Host "Message=$message"
Write-Host "Point=$point"
Write-Host "Navigation=$navigation"
Write-Host "Evidence=$diagnosticEvidence"
Write-Host "Markdown=$mdPath"
Write-Host "Json=$jsonPath"

exit 0
