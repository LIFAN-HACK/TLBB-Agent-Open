# Dump committed memory regions that contain any of the given marker strings.
# Lets us recover the in-memory JS/HTML/config that drives the captcha flow.
param(
    [Parameter(Mandatory=$true)][int]$ProcId,
    [string[]]$Markers = @("guaguale","dianxuan","xuanzhuan","captcha","yzm","base64"),
    [string]$OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) "tools\mem_dump")
)

$sig = @'
using System;
using System.Runtime.InteropServices;
public class MemC {
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(int a, bool b, int c);
    [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] buf, int sz, ref int read);
    [DllImport("kernel32.dll")] public static extern int VirtualQueryEx(IntPtr h, IntPtr a, out MBI m, uint len);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct MBI {
        public IntPtr BaseAddress; public IntPtr AllocationBase; public int AllocationProtect;
        public IntPtr RegionSize; public int State; public int Protect; public int Type; }
}
'@
Add-Type -TypeDefinition $sig

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
$h = [MemC]::OpenProcess(0x10 -bor 0x400, $false, $ProcId)
if ($h -eq [IntPtr]::Zero) { Write-Error "OpenProcess failed (try admin)"; exit 1 }
$enc = [System.Text.Encoding]::GetEncoding(28591)
$mbi = New-Object MemC+MBI
$mbiSize = [System.Runtime.InteropServices.Marshal]::SizeOf($mbi)
$addr = [IntPtr]::Zero
$maxAddr = 0x7FFFFFFFFFFF
$count = 0

while ([MemC]::VirtualQueryEx($h, $addr, [ref]$mbi, $mbiSize) -ne 0) {
    $base = $mbi.BaseAddress.ToInt64(); $size = $mbi.RegionSize.ToInt64()
    if ($mbi.State -eq 0x1000 -and $mbi.Protect -ne 0x01 -and (($mbi.Protect -band 0x100) -eq 0) -and $size -le 64MB) {
        $buf = New-Object byte[] $size; $read = 0
        if ([MemC]::ReadProcessMemory($h, $mbi.BaseAddress, $buf, $size, [ref]$read) -and $read -gt 0) {
            $s = $enc.GetString($buf, 0, $read)
            $hit = $null
            foreach ($mk in $Markers) {
                if ($s.IndexOf($mk, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { $hit = $mk; break }
            }
            if ($hit) {
                $path = Join-Path $OutDir ("region_0x{0:X}_{1}_{2}.bin" -f $base, $read, $hit)
                $out = New-Object byte[] $read
                [Array]::Copy($buf, $out, $read)
                [System.IO.File]::WriteAllBytes($path, $out)
                Write-Host ("DUMP [{0}] @0x{1:X} {2} bytes -> {3}" -f $hit, $base, $read, $path)
                $count++
            }
        }
    }
    $addr = [IntPtr]::new($base + $size)
    if ($base + $size -ge $maxAddr) { break }
}
[MemC]::CloseHandle($h) | Out-Null
Write-Host "DONE - $count regions dumped to $OutDir"
