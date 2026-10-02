# Scan process memory for URLs and domain-like strings.
param(
    [Parameter(Mandatory=$true)][int]$ProcId,
    [string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) "tools\found_urls.txt")
)

$sig = @'
using System;
using System.Runtime.InteropServices;
public class MemU {
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

$h = [MemU]::OpenProcess(0x10 -bor 0x400, $false, $ProcId)
if ($h -eq [IntPtr]::Zero) { Write-Error "OpenProcess failed (try admin)"; exit 1 }
$enc = [System.Text.Encoding]::GetEncoding(28591)
$mbi = New-Object MemU+MBI
$mbiSize = [System.Runtime.InteropServices.Marshal]::SizeOf($mbi)
$addr = [IntPtr]::Zero
$maxAddr = 0x7FFFFFFFFFFF
$seen = @{}

# http(s) URLs, and bare host:port / domains with common TLDs
$reUrl  = [regex]'(?i)https?://[A-Za-z0-9\.\-_:/%\?&=]{4,200}'
$reHost = [regex]'(?i)\b([A-Za-z0-9\-]{1,63}\.){1,4}(com|cn|net|top|xyz|vip|cc|io|api|org|info|site|club|online|fun|shop|pro)\b(:\d{2,5})?(/[A-Za-z0-9\.\-_/%\?&=]{0,120})?'

while ([MemU]::VirtualQueryEx($h, $addr, [ref]$mbi, $mbiSize) -ne 0) {
    $base = $mbi.BaseAddress.ToInt64(); $size = $mbi.RegionSize.ToInt64()
    if ($mbi.State -eq 0x1000 -and $mbi.Protect -ne 0x01 -and (($mbi.Protect -band 0x100) -eq 0)) {
        $buf = New-Object byte[] $size; $read = 0
        if ([MemU]::ReadProcessMemory($h, $mbi.BaseAddress, $buf, $size, [ref]$read) -and $read -gt 0) {
            # ASCII view
            $sA = $enc.GetString($buf, 0, $read)
            # UTF16 view (every other byte) - many such apps store urls as wide chars
            $sW = [System.Text.Encoding]::Unicode.GetString($buf, 0, ($read - ($read % 2)))
            foreach ($s in @($sA, $sW)) {
                foreach ($m in $reUrl.Matches($s))  { $v=$m.Value.Trim(); if($v.Length -ge 8 -and -not $seen.ContainsKey($v)){ $seen[$v]=$true; Write-Host "URL  $v" } }
                foreach ($m in $reHost.Matches($s)) { $v=$m.Value.Trim(); if($v.Length -ge 6 -and -not $seen.ContainsKey($v)){ $seen[$v]=$true; Write-Host "HOST $v" } }
            }
        }
    }
    $addr = [IntPtr]::new($base + $size)
    if ($base + $size -ge $maxAddr) { break }
}
[MemU]::CloseHandle($h) | Out-Null
$seen.Keys | Sort-Object | Out-File -FilePath $Out -Encoding utf8
Write-Host "DONE - $($seen.Count) unique, saved to $Out"
