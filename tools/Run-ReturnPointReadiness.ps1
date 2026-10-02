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
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = Join-Path $Root "artifacts\return-point-readiness\$stamp"
$jsonPath = Join-Path $outDir "return-point-readiness.json"
$mdPath = Join-Path $outDir "return-point-readiness.md"
$rawPath = Join-Path $outDir "return-readiness-output.txt"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$output = & $dotnet run --no-build --project $diagProject -- return-readiness --profile $Profile 2>&1
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

$level = Get-LineValue "Level"
$summary = Get-LineValue "Summary"
$nextAction = Get-LineValue "NextAction"
$scene = Get-LineValue "Scene"
$evidence = Get-LineValue "Evidence"

$ready = $level -eq "Ready"
$blocked = $level -eq "Blocked"

$report = [pscustomobject]@{
    capturedAt = (Get-Date).ToString("o")
    mode = "return-point-readiness-no-business-action"
    safety = [pscustomobject]@{
        sendsInput = $false
        clicksMouse = $false
        writesProfile = $false
        writesMemory = $false
        injectsDll = $false
        installsHook = $false
        patchesClient = $false
    }
    profile = $Profile
    exitCode = $exitCode
    level = $level
    ready = $ready
    blocked = $blocked
    summary = $summary
    nextAction = $nextAction
    scene = $scene
    evidence = $evidence
    rawOutput = $rawPath
}

$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# Return Point Readiness")
[void]$md.AppendLine()
[void]$md.AppendLine("- CapturedAt: $($report.capturedAt)")
[void]$md.AppendLine("- Profile: $Profile")
[void]$md.AppendLine("- Level: $level")
[void]$md.AppendLine("- Ready: $ready")
[void]$md.AppendLine("- Safety: no input, no click, no profile write, no memory write, no injection, no hook.")
[void]$md.AppendLine()
[void]$md.AppendLine("## Summary")
[void]$md.AppendLine()
[void]$md.AppendLine($summary)
[void]$md.AppendLine()
[void]$md.AppendLine("## Scene")
[void]$md.AppendLine()
[void]$md.AppendLine($scene)
[void]$md.AppendLine()
[void]$md.AppendLine("## Next")
[void]$md.AppendLine()
[void]$md.AppendLine($nextAction)
[void]$md.AppendLine()
[void]$md.AppendLine("## Evidence")
[void]$md.AppendLine("- JSON: $jsonPath")
[void]$md.AppendLine("- Raw output: $rawPath")
[void]$md.AppendLine("- Runtime evidence: $evidence")
Set-Content -LiteralPath $mdPath -Value $md.ToString() -Encoding UTF8

Write-Host "Return point readiness"
Write-Host "Level=$level"
Write-Host "Ready=$ready"
Write-Host "Summary=$summary"
Write-Host "NextAction=$nextAction"
Write-Host "Scene=$scene"
Write-Host "Evidence=$evidence"
Write-Host "Markdown=$mdPath"
Write-Host "Json=$jsonPath"

exit 0
