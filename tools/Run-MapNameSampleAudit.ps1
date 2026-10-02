param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
$diagProject = Join-Path $Root "tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = Join-Path $Root "artifacts\map-name-sample-audit\$stamp"
$jsonPath = Join-Path $outDir "map-name-sample-audit-wrapper.json"
$mdPath = Join-Path $outDir "map-name-sample-audit-wrapper.md"
$rawPath = Join-Path $outDir "map-name-sample-audit-output.txt"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$output = & $dotnet run --no-build --project $diagProject -- map-name-samples audit 2>&1
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

$issues = Get-LineValue "Issues"
$accepted = Get-LineValue "Accepted"
$total = Get-LineValue "Total"
$directoryMismatches = Get-LineValue "DirectoryMismatches"
$missingMask = Get-LineValue "MissingMask"
$diagnosticEvidence = Get-LineValue "Evidence"
$diagnosticMarkdown = Get-LineValue "Markdown"
$ready = $exitCode -eq 0 -and $issues -eq "0"

$report = [pscustomobject]@{
    capturedAt = (Get-Date).ToString("o")
    mode = "map-name-sample-audit-wrapper-readonly"
    safety = [pscustomobject]@{
        visitsGameWindow = $false
        sendsInput = $false
        clicksMouse = $false
        writesProfile = $false
        writesSamples = $false
        writesMemory = $false
        injectsDll = $false
        installsHook = $false
        patchesClient = $false
    }
    exitCode = $exitCode
    ready = $ready
    total = $total
    accepted = $accepted
    issues = $issues
    directoryMismatches = $directoryMismatches
    missingMask = $missingMask
    diagnosticEvidence = $diagnosticEvidence
    diagnosticMarkdown = $diagnosticMarkdown
    rawOutput = $rawPath
}

$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# Map Name Sample Audit")
[void]$md.AppendLine()
[void]$md.AppendLine("- CapturedAt: $($report.capturedAt)")
[void]$md.AppendLine("- Ready: $ready")
[void]$md.AppendLine("- Total: $total")
[void]$md.AppendLine("- Accepted: $accepted")
[void]$md.AppendLine("- Issues: $issues")
[void]$md.AppendLine("- DirectoryMismatches: $directoryMismatches")
[void]$md.AppendLine("- MissingMask: $missingMask")
[void]$md.AppendLine("- Safety: read-only local sample audit; no game window, no input, no profile write.")
[void]$md.AppendLine()
[void]$md.AppendLine("## Evidence")
[void]$md.AppendLine("- Wrapper JSON: $jsonPath")
[void]$md.AppendLine("- Raw output: $rawPath")
[void]$md.AppendLine("- Diagnostic JSON: $diagnosticEvidence")
[void]$md.AppendLine("- Diagnostic Markdown: $diagnosticMarkdown")
Set-Content -LiteralPath $mdPath -Value $md.ToString() -Encoding UTF8

Write-Host "Map name sample audit"
Write-Host "Ready=$ready"
Write-Host "Total=$total"
Write-Host "Accepted=$accepted"
Write-Host "Issues=$issues"
Write-Host "DirectoryMismatches=$directoryMismatches"
Write-Host "MissingMask=$missingMask"
Write-Host "Evidence=$diagnosticEvidence"
Write-Host "Markdown=$mdPath"
Write-Host "Json=$jsonPath"

exit 0
