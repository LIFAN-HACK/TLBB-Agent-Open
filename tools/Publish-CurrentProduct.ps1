param(
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$appProject = Join-Path $repo "src\TlbbAgent.App\TlbbAgent.App.csproj"
$productRoot = Join-Path $repo "artifacts\product"
$releasesRoot = Join-Path $productRoot "releases"
$buildId = [DateTime]::Now.ToString("yyyyMMdd-HHmmss")
$releaseDirectory = Join-Path $releasesRoot $buildId

New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
if (-not $NoBuild) {
    & $dotnet publish $appProject -c Release -r win-x64 --self-contained false -o $releaseDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Product publish failed with exit code $LASTEXITCODE."
    }
}

& (Join-Path $PSScriptRoot "Write-RuntimeManifest.ps1") -OutputDirectory $releaseDirectory -Configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "Runtime manifest generation failed."
}

$pointer = Join-Path $productRoot "current-release.txt"
$pointerTemp = "$pointer.tmp"
New-Item -ItemType Directory -Path $productRoot -Force | Out-Null
Set-Content -LiteralPath $pointerTemp -Value $releaseDirectory -Encoding UTF8
Move-Item -LiteralPath $pointerTemp -Destination $pointer -Force

Write-Output "CURRENT_RELEASE=$releaseDirectory"
