param(
    [switch]$KeepExisting
)

$ErrorActionPreference = "Stop"
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$pointer = Join-Path $repo "artifacts\product\current-release.txt"
if (-not (Test-Path -LiteralPath $pointer -PathType Leaf)) {
    throw "No current release. Run tools\Publish-CurrentProduct.ps1 first."
}

$releaseDirectory = (Get-Content -LiteralPath $pointer -Encoding UTF8 | Select-Object -First 1).Trim()
$exe = Join-Path $releaseDirectory "TLBB Agent.exe"
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Current release executable does not exist: $exe"
}

$workspacePrefix = $repo.TrimEnd('\') + '\'
$existing = @(Get-Process -Name "TLBB Agent" -ErrorAction SilentlyContinue)
foreach ($process in $existing) {
    $path = $null
    try { $path = $process.Path } catch { }
    if ($KeepExisting -or [string]::IsNullOrWhiteSpace($path)) {
        continue
    }

    if ($path.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(2000)) {
            Stop-Process -Id $process.Id -Force
        }
    }
}

Start-Process -FilePath $exe -WorkingDirectory $releaseDirectory
Write-Output "STARTED=$exe"
