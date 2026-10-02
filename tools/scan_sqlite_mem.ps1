# Scan target process memory and carve decrypted plaintext SQLite image.
# Usage: powershell -ExecutionPolicy Bypass -File scan_sqlite_mem.ps1 -ProcId 1234 -Out C:\path\dumped.db
param(
    [Parameter(Mandatory=$true)][int]$ProcId,
    [string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) "tools\dumped.db")
)

$sig = @'
using System;
using System.Runtime.InteropServices;
public class Mem {
    [DllImport("kernel32.dll")]
    public static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);
    [DllImport("kernel32.dll")]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, ref int lpNumberOfBytesRead);
    [DllImport("kernel32.dll")]
    public static extern int VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public int AllocationProtect;
        public IntPtr RegionSize;
        public int State;
        public int Protect;
        public int Type;
    }
}
'@
Add-Type -TypeDefinition $sig

$PROCESS_VM_READ = 0x10
$PROCESS_QUERY_INFORMATION = 0x400
$MEM_COMMIT = 0x1000
$PAGE_NOACCESS = 0x01
$PAGE_GUARD = 0x100

$h = [Mem]::OpenProcess($PROCESS_VM_READ -bor $PROCESS_QUERY_INFORMATION, $false, $ProcId)
if ($h -eq [IntPtr]::Zero) { Write-Error "OpenProcess failed - try running as Administrator"; exit 1 }

$magicStr = "SQLite format 3" + [char]0   # 16 chars
$enc = [System.Text.Encoding]::GetEncoding(28591)  # Latin1: 1 byte == 1 char
$found = @()
$addr = [IntPtr]::Zero
$mbi = New-Object Mem+MEMORY_BASIC_INFORMATION
$mbiSize = [System.Runtime.InteropServices.Marshal]::SizeOf($mbi)
$maxAddr = 0x7FFFFFFFFFFF

while ([Mem]::VirtualQueryEx($h, $addr, [ref]$mbi, $mbiSize) -ne 0) {
    $base = $mbi.BaseAddress.ToInt64()
    $size = $mbi.RegionSize.ToInt64()
    if ($mbi.State -eq $MEM_COMMIT -and $mbi.Protect -ne $PAGE_NOACCESS -and (($mbi.Protect -band $PAGE_GUARD) -eq 0)) {
        $buf = New-Object byte[] $size
        $read = 0
        if ([Mem]::ReadProcessMemory($h, $mbi.BaseAddress, $buf, $size, [ref]$read) -and $read -gt 16) {
            $s = $enc.GetString($buf, 0, $read)
            $pos = 0
            while (($i = $s.IndexOf($magicStr, $pos)) -ge 0) {
                $pos = $i + 16
                if ($true) {
                    $abs = $base + $i
                    Write-Host ("HIT SQLite header @ 0x{0:X}" -f $abs)
                    $pageSize = ($buf[$i+16] -shl 8) -bor $buf[$i+17]
                    if ($pageSize -eq 1) { $pageSize = 65536 }
                    $pageCount = ($buf[$i+28] -shl 24) -bor ($buf[$i+29] -shl 16) -bor ($buf[$i+30] -shl 8) -bor $buf[$i+31]
                    $dbSize = $pageSize * $pageCount
                    Write-Host ("  pageSize=$pageSize pageCount=$pageCount => approx $dbSize bytes")
                    $found += [pscustomobject]@{ Addr=$abs; Offset=$i; Buf=$buf; Read=$read; DbSize=$dbSize }
                }
            }
        }
    }
    $addr = [IntPtr]::new($base + $size)
    if ($base + $size -ge $maxAddr) { break }
}

[Mem]::CloseHandle($h) | Out-Null

if ($found.Count -eq 0) { Write-Host "NO plaintext SQLite found in memory (maybe decrypted only after login, or not local)"; exit 0 }

$idx = 0
foreach ($f in $found) {
    $avail = $f.Read - $f.Offset
    $take = [Math]::Min([Math]::Max($f.DbSize, 4096), $avail)
    $slice = New-Object byte[] $take
    [Array]::Copy($f.Buf, $f.Offset, $slice, 0, $take)
    $path = if ($idx -eq 0) { $Out } else { $Out -replace '\.db$', "_$idx.db" }
    [System.IO.File]::WriteAllBytes($path, $slice)
    Write-Host "WROTE: $path ($take bytes)"
    $idx++
}
