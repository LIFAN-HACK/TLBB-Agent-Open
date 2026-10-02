param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$required = @(
    "TLBB Agent.exe",
    "TLBB Agent.dll",
    "TlbbAgent.Core.dll",
    "TlbbAgent.Contracts.dll"
)

$files = [ordered]@{}
foreach ($name in $required) {
    $path = Join-Path $output $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Runtime manifest input is missing: $path"
    }

    $files[$name] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

$fingerprintMaterial = ($files.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join "|"
$sha = [System.Security.Cryptography.SHA256]::Create()
try {
    $fingerprintBytes = [System.Text.Encoding]::UTF8.GetBytes($fingerprintMaterial)
    $fingerprint = ([System.BitConverter]::ToString($sha.ComputeHash($fingerprintBytes))).Replace("-", "").Substring(0, 16)
}
finally {
    $sha.Dispose()
}

$manifest = [ordered]@{
    schemaVersion = 1
    product = "TLBB Agent"
    buildFingerprint = $fingerprint
    configuration = $Configuration
    generatedAtUtc = [DateTime]::UtcNow.ToString("O")
    files = $files
}

$manifestPath = Join-Path $output "runtime-manifest.json"
$tempPath = "$manifestPath.tmp"
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $tempPath -Encoding UTF8
Move-Item -LiteralPath $tempPath -Destination $manifestPath -Force
Write-Output $manifestPath
