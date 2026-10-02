param(
    [int]$DurationSeconds = 900,
    [int]$PollIntervalMs = 250,
    [int]$SnapshotEveryMs = 1000,
    [string]$OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\runtime-observation")
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32WindowProbe {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
"@

function Get-WindowRectInfo {
    param([IntPtr]$Handle)
    $rect = New-Object Win32WindowProbe+RECT
    if ($Handle -eq [IntPtr]::Zero) { return $null }
    if (-not [Win32WindowProbe]::GetWindowRect($Handle, [ref]$rect)) { return $null }

    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) { return $null }

    [pscustomobject]@{
        X = $rect.Left
        Y = $rect.Top
        Width = $width
        Height = $height
    }
}

function Save-ScreenShot {
    param(
        [string]$Path,
        [int]$X,
        [int]$Y,
        [int]$Width,
        [int]$Height
    )

    if ($Width -le 0 -or $Height -le 0) { return }

    $bitmap = New-Object System.Drawing.Bitmap $Width, $Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($X, $Y, 0, 0, (New-Object System.Drawing.Size $Width, $Height))
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Get-VirtualScreen {
    $screens = [System.Windows.Forms.Screen]::AllScreens
    $left = ($screens | ForEach-Object { $_.Bounds.Left } | Measure-Object -Minimum).Minimum
    $top = ($screens | ForEach-Object { $_.Bounds.Top } | Measure-Object -Minimum).Minimum
    $right = ($screens | ForEach-Object { $_.Bounds.Right } | Measure-Object -Maximum).Maximum
    $bottom = ($screens | ForEach-Object { $_.Bounds.Bottom } | Measure-Object -Maximum).Maximum
    [pscustomobject]@{
        X = [int]$left
        Y = [int]$top
        Width = [int]($right - $left)
        Height = [int]($bottom - $top)
    }
}

$runStamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runDir = Join-Path $OutputRoot "xiaoxiao-$runStamp"
$screenDir = Join-Path $runDir "screens"
New-Item -ItemType Directory -Force -Path $screenDir | Out-Null

$timelinePath = Join-Path $runDir "timeline.csv"
$manifestPath = Join-Path $runDir "manifest.txt"

@(
    "LongBanXing refresh observation capture"
    "Start: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')"
    "DurationSeconds: $DurationSeconds"
    "PollIntervalMs: $PollIntervalMs"
    "SnapshotEveryMs: $SnapshotEveryMs"
    "Read-only capture: Game/uu/vv/app process timeline and visible screenshots."
) | Set-Content -LiteralPath $manifestPath -Encoding UTF8

"timestamp_ms,iso_time,kind,pid,process,title,path,hwnd,x,y,width,height,foreground,handle_count,thread_count,cpu_seconds,private_mb,screen_file" |
    Set-Content -LiteralPath $timelinePath -Encoding UTF8

$start = [Diagnostics.Stopwatch]::StartNew()
$lastSnapshot = -1
$virtual = Get-VirtualScreen

while ($start.Elapsed.TotalSeconds -lt $DurationSeconds) {
    $elapsedMs = [int64]$start.Elapsed.TotalMilliseconds
    $now = Get-Date
    $foreground = [Win32WindowProbe]::GetForegroundWindow()
    $doSnapshot = ($elapsedMs - $lastSnapshot) -ge $SnapshotEveryMs
    $desktopFile = ""

    if ($doSnapshot) {
        $lastSnapshot = $elapsedMs
        $desktopFile = Join-Path $screenDir ("desktop_{0:D9}.png" -f $elapsedMs)
        try {
            Save-ScreenShot -Path $desktopFile -X $virtual.X -Y $virtual.Y -Width $virtual.Width -Height $virtual.Height
        }
        catch {
            $desktopFile = "ERR:" + $_.Exception.Message.Replace(",", " ")
        }
    }

    $procs = Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -in @("Game", "uu.dvm", "vv.dvm") }

    foreach ($p in $procs) {
        $rect = Get-WindowRectInfo -Handle $p.MainWindowHandle
        $x = ""; $y = ""; $w = ""; $h = ""
        if ($rect -ne $null) {
            $x = $rect.X; $y = $rect.Y; $w = $rect.Width; $h = $rect.Height
        }

        $windowFile = ""
        if ($doSnapshot -and $p.ProcessName -eq "Game" -and $rect -ne $null) {
            $windowFile = Join-Path $screenDir ("game_{0}_{1:D9}.png" -f $p.Id, $elapsedMs)
            try {
                Save-ScreenShot -Path $windowFile -X $rect.X -Y $rect.Y -Width $rect.Width -Height $rect.Height
            }
            catch {
                $windowFile = "ERR:" + $_.Exception.Message.Replace(",", " ")
            }
        }

        $title = ""
        if ($null -ne $p.MainWindowTitle) { $title = $p.MainWindowTitle.Replace('"', '""') }
        $path = ""
        try {
            if ($null -ne $p.Path) { $path = $p.Path.Replace('"', '""') }
        } catch { }
        $isFg = if ($p.MainWindowHandle -ne [IntPtr]::Zero -and $p.MainWindowHandle -eq $foreground) { "1" } else { "0" }
        $privateMb = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
        $line = '{0},"{1}","process",{2},"{3}","{4}","{5}","0x{6:X}",{7},{8},{9},{10},{11},{12},{13},{14},{15},"{16}"' -f `
            $elapsedMs,
            $now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            $p.Id,
            $p.ProcessName,
            $title,
            $path,
            $p.MainWindowHandle.ToInt64(),
            $x,$y,$w,$h,
            $isFg,
            $p.HandleCount,
            $p.Threads.Count,
            [math]::Round($p.CPU, 3),
            $privateMb,
            $windowFile.Replace('"','""')
        Add-Content -LiteralPath $timelinePath -Value $line -Encoding UTF8
    }

    if ($doSnapshot) {
        $line = '{0},"{1}","desktop",,"","","","",{2},{3},{4},{5},,,,,,"{6}"' -f `
            $elapsedMs,
            $now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            $virtual.X,$virtual.Y,$virtual.Width,$virtual.Height,
            $desktopFile.Replace('"','""')
        Add-Content -LiteralPath $timelinePath -Value $line -Encoding UTF8
    }

    Start-Sleep -Milliseconds $PollIntervalMs
}

"End: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')" | Add-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host $runDir
