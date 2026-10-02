using System.Diagnostics;
using System.Runtime.InteropServices;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class ClientProcessService : IClientProcessService
{
    private const string Module = "ClientProcess";
    private readonly ILogService _log;

    public ClientProcessService(ILogService log)
    {
        _log = log;
    }

    public bool IsWindowAlive(nint hwnd) =>
        hwnd != nint.Zero && IsWindow(hwnd);

    public bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public nint WaitForMainWindow(int pid, int timeoutMs, int pollIntervalMs = 250)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs));
        while (DateTime.UtcNow <= deadline)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Refresh();
                if (process.HasExited)
                    return nint.Zero;

                if (process.MainWindowHandle != nint.Zero && IsWindow(process.MainWindowHandle))
                    return process.MainWindowHandle;
            }
            catch
            {
                return nint.Zero;
            }

            Thread.Sleep(Math.Max(50, pollIntervalMs));
        }

        return nint.Zero;
    }

    public ClientLaunchResult StartClient(string processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            return new ClientLaunchResult(false, null, "启动文件未设置。");

        if (!File.Exists(processPath))
            return new ClientLaunchResult(false, null, "客户端不存在。");

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = processPath,
                WorkingDirectory = Path.GetDirectoryName(processPath) ?? Environment.CurrentDirectory,
                UseShellExecute = true
            };

            var process = Process.Start(info);
            if (process is null)
                return new ClientLaunchResult(false, null, "启动返回为空。");

            _log.Info(Module, "客户端已启动。");
            return new ClientLaunchResult(true, process.Id, "已启动客户端。");
        }
        catch
        {
            return new ClientLaunchResult(false, null, "启动失败。");
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);
}
