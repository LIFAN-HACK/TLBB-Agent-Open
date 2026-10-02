#Requires -Version 5.1
param(
    [string]$Candidates = (Join-Path (Split-Path -Parent $PSScriptRoot) "data\offsets\uu-static-candidates.json"),
    [string]$OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\uu-observation")
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "UuMemoryTrace\UuMemoryTrace.csproj"

Write-Host "Building UuMemoryTrace (x86)..." -ForegroundColor Cyan
dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$exe = Join-Path $PSScriptRoot "UuMemoryTrace\bin\Release\net8.0-windows\UuMemoryTrace.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    throw "exe not found: $exe"
}

Write-Host ""
Write-Host "Starting UuMemoryTrace (READ-ONLY)." -ForegroundColor Green
Write-Host "1) Leave this console open" -ForegroundColor Yellow
Write-Host "2) Start UU + bind Game.exe" -ForegroundColor Yellow
Write-Host "3) In UU open 检测杀怪数, note the number" -ForegroundColor Yellow
Write-Host "4) Here: r → 1 → v → type that number" -ForegroundColor Yellow
Write-Host ""

& $exe --candidates $Candidates --out $OutputRoot
