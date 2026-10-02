# Scan process memory for keyword strings with surrounding context.
param(
    [Parameter(Mandatory=$true)][int]$ProcId,
    [string[]]$Keywords = @("PRAGMA","cipher","sqlite3_key","sqlcipher","aes","kdf_iter","rekey","legacy")
)

$sig = @'
using System;
using System.Runtime.InteropServices;
public class Mem2 {
    [DllImport("kernel32.dll")]
    public static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);
    [DllImport("kernel32.dll")]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, ref int lpNumberOfBytesRead);
    [DllImport("kernel32.dll")]
    public static extern int VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MBI lpBuffer, uint dwLength);
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);
    [StructLayout(LayoutKind.Sequential)]
    public struct MBI {
        public IntPtr BaseAddress; public IntPtr AllocationBase; public int AllocationProtect;
        public IntPtr RegionSize; public int State; public int Protect; public int Type;
    }
}
'@
Add-Type -TypeDefinition $sig

$h = [Mem2]::OpenProcess(0x10 -bor 0x400, $false, $ProcId)
if ($h -eq [IntPtr]::Zero) { Write-Error "OpenProcess failed"; exit 1 }
$enc = [System.Text.Encoding]::GetEncoding(28591)
$mbi = New-Object Mem2+MBI
$mbiSize = [System.Runtime.InteropServices.Marshal]::SizeOf($mbi)
$addr = [IntPtr]::Zero
$maxAddr = 0x7FFFFFFFFFFF
$seen = @{}

while ([Mem2]::VirtualQueryEx($h, $addr, [ref]$mbi, $mbiSize) -ne 0) {
    $base = $mbi.BaseAddress.ToInt64(); $size = $mbi.RegionSize.ToInt64()
    if ($mbi.State -eq 0x1000 -and $mbi.Protect -ne 0x01 -and (($mbi.Protect -band 0x100) -eq 0)) {
        $buf = New-Object byte[] $size; $read = 0
        if ([Mem2]::ReadProcessMemory($h, $mbi.BaseAddress, $buf, $size, [ref]$read) -and $read -gt 0) {
            $s = $enc.GetString($buf, 0, $read)
            foreach ($kw in $Keywords) {
                $pos = 0
                while (($i = $s.IndexOf($kw, $pos, [System.StringComparison]::OrdinalIgnoreCase)) -ge 0) {
                    $pos = $i + 1
                    $st = [Math]::Max(0, $i-8); $len = [Math]::Min(120, $read-$st)
                    $ctx = $s.Substring($st, $len)
                    # keep printable only
                    $clean = ($ctx.ToCharArray() | ForEach-Object { if ([int]$_ -ge 32 -and [int]$_ -lt 127) { $_ } else { '.' } }) -join ''
                    if (-not $seen.ContainsKey($clean)) {
                        $seen[$clean] = $true
                        Write-Host ("[{0}] @0x{1:X}  {2}" -f $kw, ($base+$i), $clean)
                    }
                }
            }
        }
    }
    $addr = [IntPtr]::new($base + $size)
    if ($base + $size -ge $maxAddr) { break }
}
[Mem2]::CloseHandle($h) | Out-Null
Write-Host "DONE"
