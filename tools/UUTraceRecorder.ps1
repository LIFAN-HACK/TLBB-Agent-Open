param(
    [int]$PollIntervalMs = 1000,
    [int]$HandleIntervalSeconds = 5,
    [int]$MaxSeconds = 0,
    [string]$OutputRoot = "",
    [string]$RunDir = "",
    [string]$MarkerFile = "",
    [switch]$NoConsoleKeys,
    [switch]$NoClearHost
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path (Split-Path -Parent $scriptRoot) "artifacts\uu-observation"
}
$runStamp = Get-Date -Format "yyyyMMdd-HHmmss"
if ([string]::IsNullOrWhiteSpace($RunDir)) {
    $runDir = Join-Path $OutputRoot "uu-trace-$runStamp"
} else {
    $runDir = $RunDir
}
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

$timelinePath = Join-Path $runDir "timeline.csv"
$filePath = Join-Path $runDir "files.csv"
$netPath = Join-Path $runDir "network.csv"
$procPath = Join-Path $runDir "process.csv"
$windowPath = Join-Path $runDir "windows.csv"
$handlePath = Join-Path $runDir "handles.csv"
$readmePath = Join-Path $runDir "README.txt"

"ts,event,detail" | Set-Content -LiteralPath $timelinePath -Encoding UTF8
"ts,root,path,length,last_write,sha256" | Set-Content -LiteralPath $filePath -Encoding UTF8
"ts,pid,state,local,remote" | Set-Content -LiteralPath $netPath -Encoding UTF8
"ts,kind,pid,process,handles,threads,cpu,private_mb,working_mb,path,title" | Set-Content -LiteralPath $procPath -Encoding UTF8
"ts,pid,process,hwnd,left,top,width,height,title" | Set-Content -LiteralPath $windowPath -Encoding UTF8
"ts,owner_pid,owner_name,handle,access,type,target_pid,target_name,target_path,file_path" | Set-Content -LiteralPath $handlePath -Encoding UTF8

@"
UU trace recorder output

Files:
- timeline.csv: manual markers and file-change events.
- process.csv: uu.dvm/vv.dvm/Game process metrics.
- network.csv: TCP records for uu.dvm/vv.dvm/Game.
- windows.csv: top-level windows for uu.dvm/vv.dvm/Game.
- handles.csv: process/file handles owned by uu.dvm/vv.dvm.
- files.csv: initial and changed files under monitored roots.

Marker keys while this console is focused:
1 = auto combat
2 = kill-switch-target
3 = red-only
4 = grab xiaoxiao
5 = grab fanzei
6 = horse bandit
7 = auto monk
8 = dungeon
9 = stop/current feature off
Space = generic marker
M = custom marker text
Q = stop recorder and zip output
"@ | Set-Content -LiteralPath $readmePath -Encoding UTF8

function CsvQ {
    param([AllowNull()][string]$Value)
    '"' + (($Value -as [string]) -replace '"','""') + '"'
}

function Write-Timeline {
    param([string]$Event, [string]$Detail)
    $ts = Get-Date -Format "o"
    ('"{0}",{1},{2}' -f $ts, (CsvQ $Event), (CsvQ $Detail)) |
        Add-Content -LiteralPath $timelinePath -Encoding UTF8
}

$csharp = @"
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class UuTraceHandleProbe
{
    private const int SystemExtendedHandleInformation = 64;
    private const uint PROCESS_DUP_HANDLE = 0x0040;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint DUPLICATE_SAME_ACCESS = 0x00000002;
    private const uint FILE_TYPE_DISK = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
    {
        public IntPtr Object;
        public UIntPtr UniqueProcessId;
        public UIntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength, ref int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle, IntPtr hTargetProcessHandle, out IntPtr lpTargetHandle, uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(IntPtr processHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr hFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(IntPtr hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

    public static void AppendSnapshot(int[] ownerPids, string outputPath, string snapshotTs)
    {
        if (ownerPids == null || ownerPids.Length == 0) return;

        var ownerSet = new HashSet<ulong>();
        foreach (var pid in ownerPids) ownerSet.Add((ulong)pid);

        var size = 0x100000;
        IntPtr buffer = IntPtr.Zero;
        int needed = 0;

        try
        {
            while (true)
            {
                buffer = Marshal.AllocHGlobal(size);
                var status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, size, ref needed);
                if (status == 0) break;
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                size = needed > size ? needed + 0x10000 : size * 2;
                if (size > 256 * 1024 * 1024) return;
            }

            var count = (ulong)Marshal.ReadIntPtr(buffer).ToInt64();
            var entrySize = Marshal.SizeOf(typeof(SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX));
            var entryBase = IntPtr.Add(buffer, IntPtr.Size * 2);
            var sourceProcesses = new Dictionary<ulong, IntPtr>();

            using (var writer = new StreamWriter(outputPath, true, new UTF8Encoding(false)))
            {
                try
                {
                    for (ulong i = 0; i < count; i++)
                    {
                        var entryPtr = IntPtr.Add(entryBase, checked((int)(i * (ulong)entrySize)));
                        var entry = (SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX)Marshal.PtrToStructure(entryPtr, typeof(SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX));
                        var ownerPid = entry.UniqueProcessId.ToUInt64();
                        if (!ownerSet.Contains(ownerPid)) continue;

                        IntPtr sourceProcess;
                        if (!sourceProcesses.TryGetValue(ownerPid, out sourceProcess))
                        {
                            sourceProcess = OpenProcess(PROCESS_DUP_HANDLE | PROCESS_QUERY_LIMITED_INFORMATION, false, (int)ownerPid);
                            sourceProcesses[ownerPid] = sourceProcess;
                        }
                        if (sourceProcess == IntPtr.Zero) continue;

                        IntPtr dup;
                        if (!DuplicateHandle(sourceProcess, (IntPtr)entry.HandleValue.ToUInt64(), GetCurrentProcess(), out dup, 0, false, DUPLICATE_SAME_ACCESS))
                            continue;

                        try
                        {
                            var ownerName = ProcessName((int)ownerPid);
                            var targetPid = GetProcessId(dup);
                            if (targetPid != 0)
                            {
                                writer.WriteLine(Csv(snapshotTs, ownerPid, ownerName, entry.HandleValue.ToUInt64(), entry.GrantedAccess, "Process", targetPid, ProcessName((int)targetPid), ProcessPath((int)targetPid), ""));
                                continue;
                            }

                            if (GetFileType(dup) == FILE_TYPE_DISK)
                            {
                                var sb = new StringBuilder(4096);
                                var len = GetFinalPathNameByHandle(dup, sb, (uint)sb.Capacity, 0);
                                if (len > 0 && len < sb.Capacity)
                                    writer.WriteLine(Csv(snapshotTs, ownerPid, ownerName, entry.HandleValue.ToUInt64(), entry.GrantedAccess, "File", 0, "", "", sb.ToString()));
                            }
                        }
                        finally
                        {
                            CloseHandle(dup);
                        }
                    }
                }
                finally
                {
                    foreach (var h in sourceProcesses.Values)
                        if (h != IntPtr.Zero) CloseHandle(h);
                }
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ProcessName(int pid)
    {
        try { using (var p = Process.GetProcessById(pid)) { return p.ProcessName; } }
        catch { return ""; }
    }

    private static string ProcessPath(int pid)
    {
        try { using (var p = Process.GetProcessById(pid)) { return p.MainModule == null ? "" : p.MainModule.FileName; } }
        catch { return ""; }
    }

    private static string Csv(string ts, ulong ownerPid, string ownerName, ulong handle, uint access, string type, uint targetPid, string targetName, string targetPath, string filePath)
    {
        return Q(ts) + "," + ownerPid + "," + Q(ownerName) + ",0x" + handle.ToString("X") + ",0x" + access.ToString("X") + "," + Q(type) + "," + targetPid + "," + Q(targetName) + "," + Q(targetPath) + "," + Q(filePath);
    }

    private static string Q(string value)
    {
        return "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
    }
}

public static class UuTraceWindowProbe
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    public static string[] Snapshot(int[] processIds)
    {
        var result = new List<string>();
        var set = new HashSet<uint>();
        if (processIds != null)
            foreach (var id in processIds) set.Add((uint)id);

        EnumWindows(delegate(IntPtr hwnd, IntPtr lp)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (!set.Contains(pid) || !IsWindowVisible(hwnd)) return true;

            RECT rect;
            if (!GetWindowRect(hwnd, out rect)) return true;

            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            result.Add(pid + ",0x" + hwnd.ToInt64().ToString("X") + "," + rect.Left + "," + rect.Top + "," + (rect.Right - rect.Left) + "," + (rect.Bottom - rect.Top) + "," + Q(sb.ToString()));
            return true;
        }, IntPtr.Zero);

        return result.ToArray();
    }

    private static string Q(string value)
    {
        return "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
    }
}
"@

Add-Type -TypeDefinition $csharp -Language CSharp

$desktopUu = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Desktop)) "uu"
$roots = @(
    $desktopUu,
    "C:\sys"
) | Where-Object { Test-Path -LiteralPath $_ }

function Get-Sha256Safe {
    param([string]$Path)
    try {
        $item = Get-Item -LiteralPath $Path -ErrorAction Stop
        if ($item.Length -gt 20MB) { return "" }
        return (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash
    } catch {
        return ""
    }
}

function Snapshot-Files {
    param([string]$Root)
    Get-ChildItem -LiteralPath $Root -Force -File -Recurse -ErrorAction SilentlyContinue |
        ForEach-Object {
            [pscustomobject]@{
                Root = $Root
                Path = $_.FullName
                Length = $_.Length
                LastWrite = $_.LastWriteTimeUtc.ToString("o")
                Sha256 = Get-Sha256Safe $_.FullName
            }
        }
}

function Snapshot-Procs {
    Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -in @("uu.dvm", "vv.dvm", "Game") } |
        ForEach-Object {
            $path = ""
            try { $path = $_.Path } catch { }
            [pscustomobject]@{
                Pid = $_.Id
                Process = $_.ProcessName
                Handles = $_.HandleCount
                Threads = $_.Threads.Count
                Cpu = [math]::Round($_.CPU, 3)
                PrivateMb = [math]::Round($_.PrivateMemorySize64 / 1MB, 1)
                WorkingMb = [math]::Round($_.WorkingSet64 / 1MB, 1)
                Path = $path
                Title = $_.MainWindowTitle
            }
        }
}

function Snapshot-Network {
    $ids = @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -in @("uu.dvm", "vv.dvm", "Game") } |
        Select-Object -ExpandProperty Id)
    if ($ids.Count -eq 0) { return @() }

    Get-NetTCPConnection -ErrorAction SilentlyContinue |
        Where-Object { $_.OwningProcess -in $ids } |
        ForEach-Object {
            [pscustomobject]@{
                Pid = $_.OwningProcess
                State = $_.State
                Local = "$($_.LocalAddress):$($_.LocalPort)"
                Remote = "$($_.RemoteAddress):$($_.RemotePort)"
            }
        }
}

function Get-TargetProcessIds {
    @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -in @("uu.dvm", "vv.dvm", "Game") } |
        Select-Object -ExpandProperty Id)
}

function Write-InitialFileSnapshot {
    $ts = Get-Date -Format "o"
    foreach ($root in $roots) {
        foreach ($f in Snapshot-Files $root) {
            $script:previousFiles[$f.Path] = $f
            ('"{0}",{1},{2},{3},{4},{5}' -f $ts, (CsvQ $root), (CsvQ $f.Path), $f.Length, (CsvQ $f.LastWrite), (CsvQ $f.Sha256)) |
                Add-Content -LiteralPath $filePath -Encoding UTF8
        }
    }
}

function Write-ProcessSnapshot {
    $ts = Get-Date -Format "o"
    foreach ($p in Snapshot-Procs) {
        ('"{0}","process",{1},{2},{3},{4},{5},{6},{7},{8},{9}' -f $ts, $p.Pid, (CsvQ $p.Process), $p.Handles, $p.Threads, $p.Cpu, $p.PrivateMb, $p.WorkingMb, (CsvQ $p.Path), (CsvQ $p.Title)) |
            Add-Content -LiteralPath $procPath -Encoding UTF8
    }
}

function Write-NetworkSnapshot {
    $ts = Get-Date -Format "o"
    foreach ($n in Snapshot-Network) {
        ('"{0}",{1},{2},{3},{4}' -f $ts, $n.Pid, (CsvQ $n.State), (CsvQ $n.Local), (CsvQ $n.Remote)) |
            Add-Content -LiteralPath $netPath -Encoding UTF8
    }
}

function Write-WindowSnapshot {
    $ts = Get-Date -Format "o"
    $ids = Get-TargetProcessIds
    if ($ids.Count -eq 0) { return }
    $names = @{}
    Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Id -in $ids } | ForEach-Object { $names[$_.Id] = $_.ProcessName }
    foreach ($row in [UuTraceWindowProbe]::Snapshot([int[]]$ids)) {
        $parts = $row.Split(',', 2)
        $windowPid = [int]$parts[0]
        ('"{0}",{1},{2},{3}' -f $ts, $windowPid, (CsvQ ($names[$windowPid])), $parts[1]) |
            Add-Content -LiteralPath $windowPath -Encoding UTF8
    }
}

function Write-HandleSnapshot {
    $ts = Get-Date -Format "o"
    $ids = @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -in @("uu.dvm", "vv.dvm") } |
        Select-Object -ExpandProperty Id)
    if ($ids.Count -eq 0) { return }
    try {
        [UuTraceHandleProbe]::AppendSnapshot([int[]]$ids, $handlePath, $ts)
    } catch {
        Write-Timeline "handle-error" $_.Exception.Message
    }
}

function Check-FileChanges {
    $ts = Get-Date -Format "o"
    foreach ($root in $roots) {
        foreach ($f in Snapshot-Files $root) {
            $old = $script:previousFiles[$f.Path]
            if ($null -eq $old -or $old.Length -ne $f.Length -or $old.LastWrite -ne $f.LastWrite -or $old.Sha256 -ne $f.Sha256) {
                $script:previousFiles[$f.Path] = $f
                ('"{0}",{1},{2},{3},{4},{5}' -f $ts, (CsvQ $root), (CsvQ $f.Path), $f.Length, (CsvQ $f.LastWrite), (CsvQ $f.Sha256)) |
                    Add-Content -LiteralPath $filePath -Encoding UTF8
                Write-Timeline "file-change" $f.Path
            }
        }
    }
}

function Process-KeyInput {
    if ($NoConsoleKeys) { return }

    try {
        while ([Console]::KeyAvailable) {
            $key = [Console]::ReadKey($true)
            $label = $null
            switch ($key.KeyChar) {
                '1' { $label = "auto-combat" }
                '2' { $label = "kill-switch-target" }
                '3' { $label = "red-only" }
                '4' { $label = "grab-xiaoxiao" }
                '5' { $label = "grab-fanzei" }
                '6' { $label = "horse-bandit" }
                '7' { $label = "auto-monk" }
                '8' { $label = "dungeon" }
                '9' { $label = "feature-stop" }
                ' ' { $label = "manual-marker" }
                'q' { $script:stopRecorder = $true; $label = "stop-recorder" }
                'Q' { $script:stopRecorder = $true; $label = "stop-recorder" }
                'm' {
                    $text = Read-Host "marker"
                    if ($text) { Write-Timeline "custom-marker" $text }
                }
                'M' {
                    $text = Read-Host "marker"
                    if ($text) { Write-Timeline "custom-marker" $text }
                }
            }
            if ($label) {
                Write-Timeline "marker" $label
                Write-Host ("[{0}] marker: {1}" -f (Get-Date -Format "HH:mm:ss"), $label)
            }
        }
    } catch {
        # Console key input is best-effort only.
    }
}

function Process-MarkerFile {
    if ([string]::IsNullOrWhiteSpace($MarkerFile) -or -not (Test-Path -LiteralPath $MarkerFile)) {
        return
    }

    try {
        $lines = [IO.File]::ReadAllLines($MarkerFile, [Text.Encoding]::UTF8)
        if ($lines.Length -le $script:markerLineCount) { return }

        for ($i = $script:markerLineCount; $i -lt $lines.Length; $i++) {
            $line = $lines[$i]
            if ([string]::IsNullOrWhiteSpace($line)) { continue }

            $parts = $line.Split('|', 2)
            $event = if ($parts.Length -ge 1) { $parts[0] } else { "marker" }
            $detail = if ($parts.Length -ge 2) { $parts[1] } else { "" }

            switch ($event) {
                "stop" {
                    Write-Timeline "marker" "stop-recorder"
                    $script:stopRecorder = $true
                }
                "marker" {
                    Write-Timeline "marker" $detail
                }
                "custom-marker" {
                    Write-Timeline "custom-marker" $detail
                }
                default {
                    Write-Timeline $event $detail
                }
            }
        }

        $script:markerLineCount = $lines.Length
    } catch {
        Write-Timeline "marker-file-error" $_.Exception.Message
    }
}

$script:previousFiles = @{}
$script:stopRecorder = $false
$script:markerLineCount = 0

$watchers = @()
foreach ($root in $roots) {
    $watcher = New-Object System.IO.FileSystemWatcher
    $watcher.Path = $root
    $watcher.IncludeSubdirectories = $true
    $watcher.EnableRaisingEvents = $true
    $watcher.NotifyFilter = [IO.NotifyFilters]'FileName, DirectoryName, LastWrite, Size, CreationTime'
    foreach ($eventName in "Created","Changed","Deleted","Renamed") {
        Register-ObjectEvent -InputObject $watcher -EventName $eventName -SourceIdentifier "UUTrace_${eventName}_$($watchers.Count)" -Action {
            $ts = Get-Date -Format "o"
            $kind = $Event.SourceEventArgs.ChangeType
            $full = $Event.SourceEventArgs.FullPath
            Add-Content -LiteralPath $Event.MessageData -Encoding UTF8 -Value ('"{0}",{1},{2}' -f $ts, ('"fs:' + $kind + '"'), ('"' + $full.Replace('"','""') + '"'))
        } -MessageData $timelinePath | Out-Null
    }
    $watchers += $watcher
}

Write-InitialFileSnapshot
Write-Timeline "start" $runDir

if (-not $NoClearHost) {
    Clear-Host
}
Write-Host "UU Trace Recorder is running." -ForegroundColor Cyan
Write-Host ""
Write-Host "Output:"
Write-Host "  $runDir" -ForegroundColor Yellow
Write-Host ""
if ($NoConsoleKeys) {
    Write-Host "Console keys disabled."
    if ($MarkerFile) {
        Write-Host "Marker file:"
        Write-Host "  $MarkerFile" -ForegroundColor Yellow
    }
} else {
    Write-Host "Focus this console and press marker keys before/after switching UU features:"
    Write-Host "  1 auto combat    2 kill switch target    3 red-only"
    Write-Host "  4 grab xiaoxiao  5 grab fanzei           6 horse bandit"
    Write-Host "  7 auto monk      8 dungeon               9 feature stop"
    Write-Host "  Space generic marker, M custom marker, Q stop and zip"
}
Write-Host ""

$lastHandle = Get-Date "2000-01-01"
$lastStatus = Get-Date "2000-01-01"
$startedAt = Get-Date

while (-not $script:stopRecorder) {
    if ($MaxSeconds -gt 0 -and ((Get-Date) - $startedAt).TotalSeconds -ge $MaxSeconds) {
        Write-Timeline "max-seconds" $MaxSeconds
        break
    }

    Process-MarkerFile
    Process-KeyInput

    Write-ProcessSnapshot
    Write-NetworkSnapshot
    Write-WindowSnapshot
    Check-FileChanges

    if (((Get-Date) - $lastHandle).TotalSeconds -ge $HandleIntervalSeconds) {
        Write-HandleSnapshot
        $lastHandle = Get-Date
    }

    if (((Get-Date) - $lastStatus).TotalSeconds -ge 5) {
        $procs = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -in @("uu.dvm", "vv.dvm", "Game") })
        Write-Host ("[{0}] samples written. processes={1}" -f (Get-Date -Format "HH:mm:ss"), $procs.Count)
        $lastStatus = Get-Date
    }

    Start-Sleep -Milliseconds $PollIntervalMs
}

Write-Timeline "stop" "user"

Get-EventSubscriber | Where-Object { $_.SourceIdentifier -like "UUTrace_*" } | Unregister-Event
foreach ($w in $watchers) { $w.Dispose() }

$zipPath = "$runDir.zip"
try {
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -LiteralPath $runDir -DestinationPath $zipPath -Force
    Write-Timeline "zip" $zipPath
} catch {
    Write-Timeline "zip-error" $_.Exception.Message
}

Write-Host ""
Write-Host "Recorder stopped." -ForegroundColor Green
Write-Host "Output:"
Write-Host "  $runDir" -ForegroundColor Yellow
Write-Host "Zip:"
Write-Host "  $zipPath" -ForegroundColor Yellow
Write-Host ""
