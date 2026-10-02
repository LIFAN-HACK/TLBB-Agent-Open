[CmdletBinding()]
param(
    [string]$GameRoot = 'C:\Game\TLBB',
    [string]$MaintenanceBaselineSha256 = '25DE33800098293D1448F6D0D4C215DF688D87861457F9549E053164896A9BB6',
    [string]$EvidenceRoot = ''
)

$ErrorActionPreference = 'Stop'

$launcherPath = Join-Path $GameRoot 'Launch.exe'
$serverListPath = Join-Path $GameRoot 'Patch\loginserver.txt'
$launcherLogPath = Join-Path $GameRoot 'Launch.log'

foreach ($requiredPath in @($launcherPath, $serverListPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required TLBB file was not found: $requiredPath"
    }
}

if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $workspaceRoot = Split-Path -Parent $PSScriptRoot
    $EvidenceRoot = Join-Path $workspaceRoot 'artifacts\server-status-monitor'
}

New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
$stagePath = Join-Path $EvidenceRoot '_last-stage.txt'
function Set-MonitorStage {
    param([Parameter(Mandatory = $true)][string]$Stage)
    [System.IO.File]::WriteAllText(
        $stagePath,
        ('{0:O} {1}' -f (Get-Date), $Stage),
        [System.Text.UTF8Encoding]::new($false))
}

Set-MonitorStage -Stage 'initializing'

if (-not ('TlbbServerMonitorNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class TlbbServerMonitorNative
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
}
'@
}

function Get-FileSnapshot {
    param([Parameter(Mandatory = $true)][string]$Path)

    $item = Get-Item -LiteralPath $Path
    [pscustomobject]@{
        Path = $item.FullName
        Length = [long]$item.Length
        LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('O')
        Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    }
}

$capturedAt = Get-Date
$foregroundBefore = [TlbbServerMonitorNative]::GetForegroundWindow()
$before = Get-FileSnapshot -Path $serverListPath
Set-MonitorStage -Stage 'before_snapshot_complete'

$launcher = Start-Process `
    -FilePath $launcherPath `
    -ArgumentList '-downloadServerInfo' `
    -WorkingDirectory $GameRoot `
    -WindowStyle Hidden `
    -PassThru
Set-MonitorStage -Stage ("launcher_started_{0}" -f $launcher.Id)

$deadline = [DateTime]::UtcNow.AddSeconds(15)
do {
    $running = Get-Process -Id $launcher.Id -ErrorAction SilentlyContinue
    if ($null -eq $running) {
        break
    }

    Start-Sleep -Milliseconds 200
} while ([DateTime]::UtcNow -lt $deadline)

$completed = $null -eq (Get-Process -Id $launcher.Id -ErrorAction SilentlyContinue)
if (-not $completed) {
    try { Stop-Process -Id $launcher.Id -Force -ErrorAction Stop } catch { }
}
Set-MonitorStage -Stage ("launcher_wait_complete_{0}" -f $completed)

Start-Sleep -Milliseconds 600

$after = Get-FileSnapshot -Path $serverListPath
$foregroundAfter = [TlbbServerMonitorNative]::GetForegroundWindow()
Set-MonitorStage -Stage 'after_snapshot_complete'

[string[]]$launcherLines = @()
if (Test-Path -LiteralPath $launcherLogPath) {
    $pidPattern = "\[0*$($launcher.Id)\|"
    [string[]]$launcherLines = @(Get-Content -LiteralPath $launcherLogPath -Encoding Default -Tail 160 |
        Where-Object { $_ -match $pidPattern } |
        ForEach-Object { [string]$_ })
}
Set-MonitorStage -Stage ("launcher_log_complete_{0}" -f $launcherLines.Count)

$downloadCompleted = [bool]($launcherLines | Where-Object { $_ -match 'event\s*:\s*det_done' } | Select-Object -First 1)
$downloadStarted = [bool]($launcherLines | Where-Object { $_ -match 'event\s*:\s*det_begin' } | Select-Object -First 1)
$fileRefreshed = $before.LastWriteTimeUtc -ne $after.LastWriteTimeUtc
$hashChanged = $before.Sha256 -ne $after.Sha256
$foregroundChanged = $foregroundBefore -ne $foregroundAfter

$status = if (-not $completed -or -not $downloadCompleted) {
    'refresh_failed'
}
elseif ($after.Sha256 -eq $MaintenanceBaselineSha256.ToUpperInvariant()) {
    'maintenance_baseline'
}
elseif ($hashChanged) {
    'server_list_changed_visual_confirmation_required'
}
else {
    'unchanged_nonbaseline_visual_confirmation_required'
}

$stamp = $capturedAt.ToString('yyyyMMdd-HHmmss')
$evidenceDirectory = Join-Path $EvidenceRoot $stamp
New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
$manifestPath = Join-Path $evidenceDirectory 'manifest.json'
Set-MonitorStage -Stage 'evidence_directory_created'

$manifest = [pscustomobject]@{
    capturedAt = $capturedAt.ToString('O')
    mode = 'official-launcher-server-list-refresh'
    status = $status
    safety = [pscustomobject]@{
        sendsGameInput = $false
        movesPhysicalCursor = $false
        activatesGameWindow = $false
        readsOrStoresCredentials = $false
    }
    launcher = [pscustomobject]@{
        path = $launcherPath
        pid = $launcher.Id
        completed = $completed
        exitCode = $null
        exitCodeReason = 'not-read; completion is verified from the same PID det_done log event'
        downloadStarted = $downloadStarted
        downloadCompleted = $downloadCompleted
    }
    serverList = [pscustomobject]@{
        encrypted = $true
        before = $before
        after = $after
        fileRefreshed = $fileRefreshed
        hashChanged = $hashChanged
        maintenanceBaselineSha256 = $MaintenanceBaselineSha256.ToUpperInvariant()
        requiresVisualConfirmationWhenChanged = $true
    }
    foreground = [pscustomobject]@{
        before = ('0x{0:X}' -f $foregroundBefore.ToInt64())
        after = ('0x{0:X}' -f $foregroundAfter.ToInt64())
        changed = $foregroundChanged
    }
    launcherLog = [pscustomobject]@{
        path = $launcherLogPath
        matchingLineCount = $launcherLines.Count
        lines = $launcherLines
    }
}
Set-MonitorStage -Stage 'manifest_object_created'

$jsonProperties = foreach ($property in $manifest.PSObject.Properties) {
    Set-MonitorStage -Stage ("manifest_serializing_{0}" -f $property.Name)
    $nameJson = ConvertTo-Json -InputObject ([string]$property.Name) -Compress
    $valueJson = ConvertTo-Json -InputObject $property.Value -Depth 7
    '  {0}: {1}' -f $nameJson, $valueJson
}
$manifestJson = "{{`r`n{0}`r`n}}" -f ($jsonProperties -join ",`r`n")
Set-MonitorStage -Stage ("manifest_serialized_{0}" -f $manifestJson.Length)
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, [System.Text.UTF8Encoding]::new($false))
Set-MonitorStage -Stage 'complete'

Write-Output "STATUS=$status"
Write-Output "DOWNLOAD_COMPLETED=$downloadCompleted"
Write-Output "FILE_REFRESHED=$fileRefreshed"
Write-Output "HASH_CHANGED=$hashChanged"
Write-Output "FOREGROUND_CHANGED=$foregroundChanged"
Write-Output "SHA256=$($after.Sha256)"
Write-Output "EVIDENCE=$manifestPath"

if (-not $completed -or -not $downloadCompleted -or $foregroundChanged) {
    exit 1
}

exit 0
