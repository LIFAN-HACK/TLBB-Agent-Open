using System.Diagnostics;
using System.Runtime.InteropServices;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class WindowBindingService : IWindowBindingService
{
    private readonly ILogService _log;
    private const string Module = "WindowBinding";

    public WindowBindingService(ILogService log) => _log = log;

    public BoundGameWindow? Current { get; private set; }
    public event EventHandler<BoundGameWindow?>? CurrentChanged;

    public BoundGameWindow? BindByScreenPoint(double screenX, double screenY)
    {
        var pt = new POINT { X = (int)screenX, Y = (int)screenY };
        var hwnd = WindowFromPoint(pt);
        if (hwnd == nint.Zero)
        {
            _log.Warn(Module, "拖拽释放点下未找到窗口。");
            return null;
        }
        hwnd = GetAncestor(hwnd, GA_ROOT);
        if (IsCurrentProcessWindow(hwnd))
        {
            _log.Warn(Module, "释放点位于 TLBB Agent 自身窗口。请把准星拖到游戏客户端窗口后松开。");
            return null;
        }
        return BindByHandle(hwnd);
    }

    public BoundGameWindow? BindByHandle(nint hwnd)
    {
        var win = TryCreateSnapshot(hwnd, out var error);
        if (win is null)
        {
            _log.Warn(Module, string.IsNullOrWhiteSpace(error) ? "绑定失败。" : error);
            return null;
        }

        Current = win;
        CurrentChanged?.Invoke(this, win);
        _log.Info(Module, $"已绑定游戏窗口：{win.ClientSizeText}", win.Id);

        if (win.IsResolutionCompatibilityMode)
            _log.Warn(Module, win.ResolutionWarningReason, win.Id);

        return win;
    }

    public BoundGameWindow? InspectByHandle(nint hwnd)
    {
        var window = TryCreateSnapshot(hwnd, out var error);
        if (window is null && !string.IsNullOrWhiteSpace(error))
            _log.Warn(Module, error);
        return window;
    }

    public IReadOnlyList<BoundGameWindow> DiscoverGameWindows()
    {
        var windows = new List<BoundGameWindow>();
        foreach (var process in Process.GetProcessesByName("Game").OrderBy(p => p.Id))
        {
            try
            {
                if (process.MainWindowHandle == nint.Zero)
                    continue;

                var snapshot = TryCreateSnapshot(process.MainWindowHandle, out _);
                if (snapshot is not null)
                    windows.Add(snapshot);
            }
            catch (InvalidOperationException)
            {
                // 进程可能在扫描瞬间退出；忽略该候选并继续扫描。
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // 进程状态发生竞态时不影响其余候选窗口。
            }
            finally
            {
                process.Dispose();
            }
        }

        return windows;
    }

    public BoundGameWindow? BindFirstGameWindow()
    {
        var candidates = DiscoverGameWindows()
            .OrderBy(window => Current is not null && window.Hwnd == Current.Hwnd ? 1 : 0)
            .ThenByDescending(window => window.Bitness == ClientBitness.X64)
            .ThenBy(window => window.Pid)
            .ToList();

        if (candidates.Count == 0)
        {
            _log.Warn(Module, "未找到游戏窗口。");
            return null;
        }

        return BindByHandle(candidates[0].Hwnd);
    }

    private static BoundGameWindow? TryCreateSnapshot(nint hwnd, out string error)
    {
        error = string.Empty;
        if (hwnd == nint.Zero || !IsWindow(hwnd))
        {
            error = "绑定失败：窗口无效。";
            return null;
        }

        try
        {
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                error = "绑定失败：无法读取窗口。";
                return null;
            }

            var title = GetWindowTitle(hwnd);
            string procName = "-", procPath = "-";
            var bitness = ClientBitness.Unknown;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                procName = process.ProcessName;
                try { procPath = process.MainModule?.FileName ?? "-"; } catch { procPath = "(无权限读取路径)"; }
                bitness = GetBitness(process.Handle);
            }
            catch
            {
                // Snapshot discovery remains best-effort; an unknown bitness is shown as unavailable.
            }

            var clientWidth = 0;
            var clientHeight = 0;
            if (GetClientRect(hwnd, out var clientRect))
            {
                clientWidth = Math.Max(0, clientRect.Right - clientRect.Left);
                clientHeight = Math.Max(0, clientRect.Bottom - clientRect.Top);
            }

            return new BoundGameWindow
            {
                Hwnd = hwnd,
                Pid = (int)pid,
                Title = string.IsNullOrEmpty(title) ? "(无标题)" : title,
                ProcessName = procName,
                ProcessPath = procPath,
                Bitness = bitness,
                ClientType = bitness switch
                {
                    ClientBitness.X86 => ClientType.Tlbb32,
                    ClientBitness.X64 => ClientType.Tlbb64,
                    _ => ClientType.Unknown
                },
                ClientWidth = clientWidth,
                ClientHeight = clientHeight,
                RunState = RunState.Ready
            };
        }
        catch (Exception ex)
        {
            error = $"绑定失败：{ex.Message}";
            return null;
        }
    }

    private static bool IsCurrentProcessWindow(nint hwnd)
    {
        if (hwnd == nint.Zero) return false;
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        return pid != 0 && pid == Environment.ProcessId;
    }

    public void Unbind()
    {
        if (Current is null) return;
        _log.Info(Module, "已解绑。", Current.Id);
        Current = null;
        CurrentChanged?.Invoke(this, null);
    }

    public bool TryScreenToClient(BoundGameWindow window, double screenX, double screenY, out int clientX, out int clientY, out string error)
    {
        clientX = 0;
        clientY = 0;
        error = string.Empty;

        if (window.Hwnd == nint.Zero || !IsWindow(window.Hwnd))
        {
            error = "\u7ed1\u5b9a\u7a97\u53e3\u65e0\u6548\u3002";
            return false;
        }

        var point = new POINT { X = (int)Math.Round(screenX), Y = (int)Math.Round(screenY) };
        if (!ScreenToClient(window.Hwnd, ref point))
        {
            error = "\u5750\u6807\u8f6c\u6362\u5931\u8d25\u3002";
            return false;
        }

        if (!GetClientRect(window.Hwnd, out var rect))
        {
            error = "\u8bfb\u53d6\u5ba2\u6237\u533a\u5931\u8d25\u3002";
            return false;
        }

        if (point.X < rect.Left || point.Y < rect.Top || point.X >= rect.Right || point.Y >= rect.Bottom)
        {
            error = "\u5750\u6807\u4e0d\u5728\u6e38\u620f\u5ba2\u6237\u533a\u5185\u3002";
            return false;
        }

        clientX = point.X;
        clientY = point.Y;
        return true;
    }

    private static string BitnessText(ClientBitness b) => b switch
    {
        ClientBitness.X86 => "32 位",
        ClientBitness.X64 => "64 位",
        _ => "未知位数"
    };

    private static ClientBitness GetBitness(nint processHandle)
    {
        if (IsWow64Process2(processHandle, out var processMachine, out _))
        {
            const ushort IMAGE_FILE_MACHINE_UNKNOWN = 0;
            const ushort IMAGE_FILE_MACHINE_I386 = 0x014c;
            if (processMachine == IMAGE_FILE_MACHINE_I386) return ClientBitness.X86;
            if (processMachine == IMAGE_FILE_MACHINE_UNKNOWN) return ClientBitness.X64;
            return ClientBitness.X64;
        }
        if (IsWow64Process(processHandle, out var wow64))
            return wow64 ? ClientBitness.X86 : ClientBitness.X64;
        return ClientBitness.Unknown;
    }

    private static bool IsLikely64BitCandidate(Process process)
    {
        try
        {
            return GetBitness(process.Handle) == ClientBitness.X64;
        }
        catch
        {
            return false;
        }
    }

    private static string GetWindowTitle(nint hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new System.Text.StringBuilder(len + 1);
        _ = GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    #region WinAPI
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint hWnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hWnd, ref POINT lpPoint);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process(nint hProcess, out bool wow64Process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process2(nint hProcess, out ushort pProcessMachine, out ushort pNativeMachine);
    #endregion
}
