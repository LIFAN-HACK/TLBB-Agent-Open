param(
    [string]$OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\uu-observation")
)

$ErrorActionPreference = "Stop"

$runStamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runDir = Join-Path $OutputRoot "uu-handles-$runStamp"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
$outPath = Join-Path $runDir "handles.csv"

$code = @"
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class UuHandleProbe
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

    public static void Run(int[] ownerPids, string outputPath)
    {
        var ownerSet = new HashSet<ulong>();
        foreach (var pid in ownerPids)
            ownerSet.Add((ulong)pid);

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
                if (size > 256 * 1024 * 1024)
                    throw new InvalidOperationException("Handle table too large.");
            }

            var count = (ulong)Marshal.ReadIntPtr(buffer).ToInt64();
            var entrySize = Marshal.SizeOf<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();
            var entryBase = IntPtr.Add(buffer, IntPtr.Size * 2);

            StreamWriter writer = new StreamWriter(outputPath, false, new UTF8Encoding(true));
            writer.WriteLine("owner_pid,owner_name,handle,access,type,target_pid,target_name,target_path,file_path");

            var sourceProcesses = new Dictionary<ulong, IntPtr>();
            try
            {
                for (ulong i = 0; i < count; i++)
                {
                    var entryPtr = IntPtr.Add(entryBase, checked((int)(i * (ulong)entrySize)));
                    var entry = Marshal.PtrToStructure<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>(entryPtr);
                    var ownerPid = entry.UniqueProcessId.ToUInt64();
                    if (!ownerSet.Contains(ownerPid))
                        continue;

                    IntPtr sourceProcess;
                    if (!sourceProcesses.TryGetValue(ownerPid, out sourceProcess))
                    {
                        sourceProcess = OpenProcess(PROCESS_DUP_HANDLE | PROCESS_QUERY_LIMITED_INFORMATION, false, (int)ownerPid);
                        sourceProcesses[ownerPid] = sourceProcess;
                    }

                    if (sourceProcess == IntPtr.Zero)
                        continue;

                    IntPtr dup;
                    if (!DuplicateHandle(sourceProcess, (IntPtr)entry.HandleValue.ToUInt64(), GetCurrentProcess(), out dup, 0, false, DUPLICATE_SAME_ACCESS))
                        continue;

                    try
                    {
                        var ownerName = ProcessName((int)ownerPid);
                        var targetPid = GetProcessId(dup);
                        if (targetPid != 0)
                        {
                            var targetName = ProcessName((int)targetPid);
                            var targetPath = ProcessPath((int)targetPid);
                            writer.WriteLine(Csv(ownerPid, ownerName, entry.HandleValue.ToUInt64(), entry.GrantedAccess, "Process", targetPid, targetName, targetPath, ""));
                            continue;
                        }

                        var fileType = GetFileType(dup);
                        if (fileType == FILE_TYPE_DISK)
                        {
                            var sb = new StringBuilder(4096);
                            var len = GetFinalPathNameByHandle(dup, sb, (uint)sb.Capacity, 0);
                            if (len > 0 && len < sb.Capacity)
                                writer.WriteLine(Csv(ownerPid, ownerName, entry.HandleValue.ToUInt64(), entry.GrantedAccess, "File", 0, "", "", sb.ToString()));
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
                writer.Dispose();
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

    private static string Csv(ulong ownerPid, string ownerName, ulong handle, uint access, string type, uint targetPid, string targetName, string targetPath, string filePath)
    {
        return ownerPid + "," + Q(ownerName) + ",0x" + handle.ToString("X") + ",0x" + access.ToString("X") + "," + Q(type) + "," + targetPid + "," + Q(targetName) + "," + Q(targetPath) + "," + Q(filePath);
    }

    private static string Q(string value)
    {
        return "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
    }
}
"@

Add-Type -TypeDefinition $code -Language CSharp

$targets = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -in @("uu.dvm", "vv.dvm") }
if (-not $targets) {
    throw "uu.dvm/vv.dvm process not found."
}

[UuHandleProbe]::Run([int[]]($targets | Select-Object -ExpandProperty Id), $outPath)
Write-Host $runDir
