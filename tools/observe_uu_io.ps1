param(
    [int]$DurationSeconds = 60,
    [int]$PollIntervalMs = 1000,
    [string]$OutputRoot = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\uu-observation"
}

$runStamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runDir = Join-Path $OutputRoot "uu-io-$runStamp"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

$timelinePath = Join-Path $runDir "timeline.csv"
$filePath = Join-Path $runDir "files.csv"
$netPath = Join-Path $runDir "network.csv"
$procPath = Join-Path $runDir "process.csv"

"ts,kind,pid,process,handles,threads,cpu,private_mb,working_mb,path,title" | Set-Content -LiteralPath $procPath -Encoding UTF8
"ts,root,path,length,last_write,sha256" | Set-Content -LiteralPath $filePath -Encoding UTF8
"ts,pid,state,local,remote" | Set-Content -LiteralPath $netPath -Encoding UTF8
"ts,event,detail" | Set-Content -LiteralPath $timelinePath -Encoding UTF8

$desktopUu = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Desktop)) "uu"
$roots = @(
    $desktopUu,
    "C:\sys"
) | Where-Object { Test-Path -LiteralPath $_ }

function Get-Sha256Safe {
    param([string]$Path)
    try {
        if ((Get-Item -LiteralPath $Path -ErrorAction Stop).Length -gt 20MB) { return "" }
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
    $items = Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -in @("uu.dvm", "vv.dvm", "Game") }
    foreach ($p in $items) {
        $path = ""
        try { $path = $p.Path } catch { }
        [pscustomobject]@{
            Pid = $p.Id
            Process = $p.ProcessName
            Handles = $p.HandleCount
            Threads = $p.Threads.Count
            Cpu = [math]::Round($p.CPU, 3)
            PrivateMb = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
            WorkingMb = [math]::Round($p.WorkingSet64 / 1MB, 1)
            Path = $path
            Title = $p.MainWindowTitle
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

$previous = @{}
foreach ($root in $roots) {
    foreach ($f in Snapshot-Files $root) {
        $previous[$f.Path] = $f
        '"{0}","{1}","{2}",{3},"{4}","{5}"' -f (Get-Date -Format "o"), $root, $f.Path.Replace('"','""'), $f.Length, $f.LastWrite, $f.Sha256 |
            Add-Content -LiteralPath $filePath -Encoding UTF8
    }
}

$watchers = @()
foreach ($root in $roots) {
    $watcher = New-Object System.IO.FileSystemWatcher
    $watcher.Path = $root
    $watcher.IncludeSubdirectories = $true
    $watcher.EnableRaisingEvents = $true
    $watcher.NotifyFilter = [IO.NotifyFilters]'FileName, DirectoryName, LastWrite, Size, CreationTime'
    foreach ($eventName in "Created","Changed","Deleted","Renamed") {
        Register-ObjectEvent -InputObject $watcher -EventName $eventName -SourceIdentifier "UU_${eventName}_$($watchers.Count)" -Action {
            $ts = Get-Date -Format "o"
            $kind = $Event.SourceEventArgs.ChangeType
            $full = $Event.SourceEventArgs.FullPath
            Add-Content -LiteralPath $Event.MessageData -Encoding UTF8 -Value ('"{0}","fs:{1}","{2}"' -f $ts, $kind, $full.Replace('"','""'))
        } -MessageData $timelinePath | Out-Null
    }
    $watchers += $watcher
}

$stopAt = (Get-Date).AddSeconds($DurationSeconds)
while ((Get-Date) -lt $stopAt) {
    $ts = Get-Date -Format "o"
    foreach ($p in Snapshot-Procs) {
        '"{0}","process",{1},"{2}",{3},{4},{5},{6},{7},"{8}","{9}"' -f `
            $ts, $p.Pid, $p.Process, $p.Handles, $p.Threads, $p.Cpu, $p.PrivateMb, $p.WorkingMb, $p.Path.Replace('"','""'), $p.Title.Replace('"','""') |
            Add-Content -LiteralPath $procPath -Encoding UTF8
    }
    foreach ($n in Snapshot-Network) {
        '"{0}",{1},"{2}","{3}","{4}"' -f $ts, $n.Pid, $n.State, $n.Local, $n.Remote |
            Add-Content -LiteralPath $netPath -Encoding UTF8
    }

    foreach ($root in $roots) {
        foreach ($f in Snapshot-Files $root) {
            $old = $previous[$f.Path]
            if ($null -eq $old -or $old.Length -ne $f.Length -or $old.LastWrite -ne $f.LastWrite -or $old.Sha256 -ne $f.Sha256) {
                $previous[$f.Path] = $f
                '"{0}","{1}","{2}",{3},"{4}","{5}"' -f $ts, $root, $f.Path.Replace('"','""'), $f.Length, $f.LastWrite, $f.Sha256 |
                    Add-Content -LiteralPath $filePath -Encoding UTF8
                '"{0}","file-change","{1}"' -f $ts, $f.Path.Replace('"','""') |
                    Add-Content -LiteralPath $timelinePath -Encoding UTF8
            }
        }
    }

    Start-Sleep -Milliseconds $PollIntervalMs
}

Get-EventSubscriber | Where-Object { $_.SourceIdentifier -like "UU_*" } | Unregister-Event
foreach ($w in $watchers) { $w.Dispose() }

Write-Host $runDir
