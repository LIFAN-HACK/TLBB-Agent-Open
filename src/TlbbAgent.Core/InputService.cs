using System.Diagnostics;
using System.Runtime.InteropServices;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class InputServicePlatformHooks
{
    public Func<nint, string, bool>? ActivateForegroundLease { get; init; }
    public Func<nint, bool>? RestoreForegroundWindow { get; init; }
    public Func<(bool HasActivity, double AgeMs, bool ButtonsDown)>? GetPhysicalMouseActivity { get; init; }
    public Func<(bool Success, int X, int Y)>? GetCursorPosition { get; init; }
    public Func<int, int, bool>? SetCursorPosition { get; init; }
    public Func<InputMouseButton, bool>? SendMouseClick { get; init; }
}

public sealed class InputService : IInputService, IDisposable
{
    private const string Module = "Input";
    private const string UserYieldModule = "人工操作保护";
    private readonly ILogService _log;
    private readonly IBackgroundInputCapabilityService _backgroundInputCapabilities;
    private readonly object _cooldownLock = new();
    private readonly object _backgroundDispatchLock = new();
    private readonly object _foregroundLeaseLock = new();
    private readonly object _mouseActivityLock = new();
    private readonly object _inputPolicyLock = new();
    private readonly LowLevelMouseProc _mouseHookProc;
    private readonly Func<nint> _foregroundWindowProvider;
    private readonly InputServicePlatformHooks? _platformHooks;
    private string _lastActivationFallback = string.Empty;
    private string _lastUserYieldWarning = string.Empty;
    private long _lastUserYieldWarningTicks;
    private readonly Dictionary<nint, long> _lastGlobalCooldownTicksByWindow = new();
    private readonly Dictionary<nint, WindowInputPolicy> _windowInputPolicies = new();
    private nint _mouseHook;
    private MouseClickSnapshot? _lastPhysicalMouseClick;
    private long _lastPhysicalMouseActivityTicks;
    private uint _physicalMouseButtonsDown;
    private string _lastCapabilityWarning = string.Empty;

    public InputService(ILogService log)
        : this(log, new BackgroundInputCapabilityService(), null, null)
    {
    }

    public InputService(ILogService log, Func<nint> foregroundWindowProvider)
        : this(log, new BackgroundInputCapabilityService(), foregroundWindowProvider, null)
    {
    }

    public InputService(ILogService log, IBackgroundInputCapabilityService backgroundInputCapabilities)
        : this(log, backgroundInputCapabilities, null, null)
    {
    }

    public InputService(
        ILogService log,
        IBackgroundInputCapabilityService backgroundInputCapabilities,
        Func<nint>? foregroundWindowProvider,
        InputServicePlatformHooks? platformHooks = null)
    {
        _log = log;
        _backgroundInputCapabilities = backgroundInputCapabilities;
        _foregroundWindowProvider = foregroundWindowProvider ?? GetForegroundWindow;
        _platformHooks = platformHooks;
        _mouseHookProc = MouseHookCallback;
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, GetModuleHandle(null), 0);
        if (_mouseHook == nint.Zero)
        {
            _log.Log(new LogEntry
            {
                Level = LogLevel.Warn,
                Module = UserYieldModule,
                Message = $"Mouse hook unavailable. Win32={Marshal.GetLastWin32Error()}",
                Audience = LogAudience.User,
                UserSummary = "人工操作保护暂不可用，自动任务仍可运行。"
            });
        }
    }

    public InputDispatchMode DefaultDispatchMode { get; set; } = InputDispatchMode.PostMessage;
    public bool YieldToUserInput { get; set; }
    public int UserInputYieldMs { get; set; } = 1500;

    public void ConfigureWindowInput(
        nint hwnd,
        InputDispatchMode mode,
        bool yieldToUserInput,
        int userInputYieldMs)
    {
        if (hwnd == nint.Zero)
            return;

        lock (_inputPolicyLock)
        {
            _windowInputPolicies[hwnd] = new WindowInputPolicy(
                mode,
                yieldToUserInput,
                Math.Clamp(userInputYieldMs, 0, 60000));
        }
    }

    public void ClearWindowInputConfiguration(nint hwnd)
    {
        lock (_inputPolicyLock)
            _windowInputPolicies.Remove(hwnd);
        lock (_cooldownLock)
            _lastGlobalCooldownTicksByWindow.Remove(hwnd);
    }

    public InputYieldStatus GetYieldStatus(nint hwnd, InputDispatchMode mode) =>
        BuildYieldStatus(hwnd, mode);

    public bool TryParseHotkey(string hotkey, out string error) =>
        HotkeyChord.TryParse(hotkey, out _, out error);

    public bool SendKey(nint hwnd, string hotkey) =>
        SendKey(hwnd, hotkey, GetWindowInputPolicy(hwnd).DispatchMode);

    public bool SendKey(nint hwnd, string hotkey, InputDispatchMode mode)
    {
        if (!HotkeyChord.TryParse(hotkey, out var chord, out var error))
        {
            WarnDiagnostic($"Invalid hotkey '{hotkey}': {error}");
            return false;
        }

        if (!EnsureWindow(hwnd, "SendKey"))
            return false;

        if (ShouldYieldToRecentUserInput(hwnd, mode, "SendKey", out var yieldReason))
        {
            WarnUserYieldOnce(hwnd, yieldReason);
            return false;
        }

        var success = mode switch
        {
            InputDispatchMode.SendInput => SendKeyBySendInput(hwnd, chord),
            _ => SendKeyByPostMessage(hwnd, chord)
        };

        if (success)
        {
            var verb = mode == InputDispatchMode.SendInput ? "Injected" : "Posted";
            _log.Trace(Module, $"{verb} hotkey {chord.DisplayText} by {mode}; game outcome is not implied.");
        }

        return success;
    }

    public bool SendText(nint hwnd, string text) =>
        SendText(hwnd, text, GetWindowInputPolicy(hwnd).DispatchMode);

    public bool SendText(nint hwnd, string text, InputDispatchMode mode)
    {
        if (string.IsNullOrEmpty(text))
        {
            WarnDiagnostic("SendText skipped: text is empty.");
            return false;
        }

        if (!EnsureWindow(hwnd, "SendText"))
            return false;

        if (ShouldYieldToRecentUserInput(hwnd, mode, "SendText", out var yieldReason))
        {
            WarnUserYieldOnce(hwnd, yieldReason);
            return false;
        }

        var success = mode switch
        {
            InputDispatchMode.SendInput => SendTextBySendInput(hwnd, text),
            _ => SendTextByPostMessage(hwnd, text)
        };

        if (success)
        {
            var verb = mode == InputDispatchMode.SendInput ? "Injected" : "Posted";
            _log.Trace(Module, $"{verb} text length={text.Length} by {mode}; game outcome is not implied.");
        }

        return success;
    }

    public bool Click(nint hwnd, int x, int y) =>
        Click(hwnd, x, y, GetWindowInputPolicy(hwnd).DispatchMode);

    public bool Click(nint hwnd, int x, int y, InputDispatchMode mode)
        => Click(hwnd, x, y, mode, InputMouseButton.Left);

    public bool Click(nint hwnd, int x, int y, InputDispatchMode mode, InputMouseButton button)
    {
        if (x < 0 || y < 0)
        {
            WarnDiagnostic($"Invalid click position ({x},{y}).");
            return false;
        }

        if (!EnsureWindow(hwnd, "Click"))
            return false;

        if (mode == InputDispatchMode.PostMessage
            && !_backgroundInputCapabilities.CanDispatch(hwnd, BackgroundInputKind.Mouse, out var capabilityReason))
        {
            WarnBackgroundCapabilityOnce(hwnd, capabilityReason);
            return false;
        }

        if (ShouldYieldToRecentUserInput(hwnd, mode, "Click", out var yieldReason))
        {
            WarnUserYieldOnce(hwnd, yieldReason);
            return false;
        }

        var success = mode switch
        {
            InputDispatchMode.SendInput or InputDispatchMode.Adaptive =>
                ClickByForegroundLease(hwnd, x, y, button),
            _ => ClickByPostMessage(hwnd, x, y, button)
        };

        if (success)
        {
            var verb = mode switch
            {
                InputDispatchMode.PostMessage => "Posted click messages",
                InputDispatchMode.Adaptive => "Completed foreground mouse lease",
                _ => "Injected click input"
            };
            _log.Trace(Module, $"{verb} at ({x},{y}) {button} by {mode}; awaiting business-state confirmation.");
        }

        return success;
    }

    private void WarnBackgroundCapabilityOnce(nint hwnd, string reason)
    {
        var key = $"{hwnd.ToInt64():X}:{reason}";
        lock (_backgroundDispatchLock)
        {
            if (string.Equals(_lastCapabilityWarning, key, StringComparison.Ordinal))
                return;
            _lastCapabilityWarning = key;
        }

        _log.Warn(
            Module,
            $"Background mouse input is blocked for HWND=0x{hwnd.ToInt64():X}: {reason}");
    }

    public bool TryConsumeGlobalCooldown(int cooldownMs) =>
        TryConsumeGlobalCooldown(nint.Zero, cooldownMs);

    public bool TryConsumeGlobalCooldown(nint hwnd, int cooldownMs)
    {
        if (cooldownMs <= 0)
            return true;

        var now = Stopwatch.GetTimestamp();
        lock (_cooldownLock)
        {
            if (_lastGlobalCooldownTicksByWindow.TryGetValue(hwnd, out var lastTicks)
                && lastTicks != 0)
            {
                var elapsedMs = (now - lastTicks) * 1000.0 / Stopwatch.Frequency;
                if (elapsedMs < cooldownMs)
                    return false;
            }

            _lastGlobalCooldownTicksByWindow[hwnd] = now;
            return true;
        }
    }

    private bool SendKeyByPostMessage(nint hwnd, HotkeyChord chord)
    {
        lock (_backgroundDispatchLock)
        {
            if (!BeginBackgroundMessageSession(hwnd))
                return false;

            var pressedModifiers = new List<ushort>(chord.ModifierVks.Length);
            var mainKeyDown = false;
            try
            {
                foreach (var modifier in chord.ModifierVks)
                {
                    if (!PostKey(hwnd, modifier, isDown: true, altContext: modifier == VK_MENU))
                        return false;
                    pressedModifiers.Add(modifier);
                }

                var hasAlt = chord.ModifierVks.Contains(VK_MENU);
                if (!PostKey(hwnd, chord.KeyVk, isDown: true, altContext: hasAlt))
                    return false;
                mainKeyDown = true;

                Thread.Sleep(BackgroundKeyHoldMs);

                if (!PostKey(hwnd, chord.KeyVk, isDown: false, altContext: hasAlt))
                    return false;
                mainKeyDown = false;

                for (var i = pressedModifiers.Count - 1; i >= 0; i--)
                {
                    var modifier = pressedModifiers[i];
                    if (!PostKey(hwnd, modifier, isDown: false, altContext: modifier == VK_MENU))
                        return false;
                }
                pressedModifiers.Clear();

                return true;
            }
            finally
            {
                if (mainKeyDown)
                    _ = PostKey(hwnd, chord.KeyVk, isDown: false, altContext: chord.ModifierVks.Contains(VK_MENU));
                for (var i = pressedModifiers.Count - 1; i >= 0; i--)
                    _ = PostKey(hwnd, pressedModifiers[i], isDown: false, altContext: pressedModifiers[i] == VK_MENU);
                ScheduleBackgroundMessageSessionEnd(hwnd);
            }
        }
    }

    private bool BeginBackgroundMessageSession(nint hwnd)
    {
        // PostMessage is deliberately non-invasive. Fabricating activation,
        // focus or deactivation transitions makes the client drop its real
        // ground-click/right-drag capture even though ordinary UI buttons still
        // appear clickable. The target window decides whether it accepts the
        // posted key while it is in the background.
        return IsWindow(hwnd);
    }

    private static void ScheduleBackgroundMessageSessionEnd(nint hwnd) => _ = hwnd;

    private bool SendKeyBySendInput(nint hwnd, HotkeyChord chord)
    {
        if (!TryActivateForSendInput(hwnd, "SendKey"))
        {
            WarnActivationFallbackOnce(hwnd, "按键");
            return false;
        }

        var inputs = new List<INPUT>();
        foreach (var modifier in chord.ModifierVks)
            inputs.Add(KeyInput(modifier, keyUp: false));

        inputs.Add(KeyInput(chord.KeyVk, keyUp: false));
        inputs.Add(KeyInput(chord.KeyVk, keyUp: true));

        for (var i = chord.ModifierVks.Length - 1; i >= 0; i--)
            inputs.Add(KeyInput(chord.ModifierVks[i], keyUp: true));

        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        if (sent != inputs.Count)
        {
            WarnDiagnostic($"SendInput sent {sent}/{inputs.Count} keyboard inputs. Win32={Marshal.GetLastWin32Error()}");
            return false;
        }

        return true;
    }

    private bool SendTextByPostMessage(nint hwnd, string text)
    {
        lock (_backgroundDispatchLock)
        {
            if (!BeginBackgroundMessageSession(hwnd))
                return false;

            try
            {
                foreach (var c in text)
                {
                    if (c == '\r')
                        continue;

                    var ch = c == '\n' ? ' ' : c;
                    if (!PostMessage(hwnd, WM_CHAR, (nint)ch, nint.Zero))
                        return WarnLastError($"PostMessage char U+{(int)ch:X4}");
                }

                return true;
            }
            finally
            {
                ScheduleBackgroundMessageSessionEnd(hwnd);
            }
        }
    }

    private bool SendTextBySendInput(nint hwnd, string text)
    {
        if (!TryActivateForSendInput(hwnd, "SendText"))
        {
            WarnActivationFallbackOnce(hwnd, "文本");
            return false;
        }

        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var raw in text)
        {
            if (raw == '\r')
                continue;

            var ch = raw == '\n' ? ' ' : raw;
            inputs.Add(UnicodeInput(ch, keyUp: false));
            inputs.Add(UnicodeInput(ch, keyUp: true));
        }

        if (inputs.Count == 0)
            return false;

        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        if (sent != inputs.Count)
        {
            WarnDiagnostic($"SendInput sent {sent}/{inputs.Count} text inputs. Win32={Marshal.GetLastWin32Error()}");
            return false;
        }

        return true;
    }

    private bool ClickByPostMessage(nint hwnd, int x, int y, InputMouseButton button)
    {
        lock (_backgroundDispatchLock)
        {
            if (!BeginBackgroundMessageSession(hwnd))
                return false;

            var lParam = MakeLParam(x, y);
            var buttonDown = false;
            var downMessage = button == InputMouseButton.Right ? WM_RBUTTONDOWN : WM_LBUTTONDOWN;
            var upMessage = button == InputMouseButton.Right ? WM_RBUTTONUP : WM_LBUTTONUP;
            var buttonWParam = button == InputMouseButton.Right ? MK_RBUTTON : MK_LBUTTON;
            try
            {
                if (!PostMessage(hwnd, WM_MOUSEMOVE, nint.Zero, lParam))
                    return WarnLastError("PostMessage WM_MOUSEMOVE");

                if (!PostMessage(hwnd, downMessage, buttonWParam, lParam))
                    return WarnLastError($"PostMessage {button} button down");
                buttonDown = true;
                Thread.Sleep(BackgroundMouseHoldMs);
                if (!PostMessage(hwnd, upMessage, nint.Zero, lParam))
                    return WarnLastError($"PostMessage {button} button up");
                buttonDown = false;
                return true;
            }
            finally
            {
                if (buttonDown)
                    _ = PostMessage(hwnd, upMessage, nint.Zero, lParam);
                ScheduleBackgroundMessageSessionEnd(hwnd);
            }
        }
    }

    private bool ClickByForegroundLease(nint hwnd, int x, int y, InputMouseButton button)
    {
        lock (_foregroundLeaseLock)
        {
            if (!CanAcquireForegroundLease(out var leaseReason))
            {
                WarnUserYieldOnce(hwnd, leaseReason);
                return false;
            }

            if (!TryGetCursorPosition(out var originalCursor))
                return WarnLastError("GetCursorPos");

            var previousForeground = CurrentForegroundWindow();
            var targetWasMinimized = IsIconic(hwnd);
            var activated = false;
            var cursorMoved = false;
            try
            {
                activated = TryActivateForForegroundLease(hwnd, "Click");
                if (!activated)
                {
                    WarnActivationFallbackOnce(hwnd, "点击");
                    return false;
                }

                var point = new POINT { X = x, Y = y };
                if (!ClientToScreen(hwnd, ref point))
                    return WarnLastError("ClientToScreen");

                if (!SetCursorPosition(point.X, point.Y))
                    return WarnLastError("SetCursorPos");

                cursorMoved = true;
                return SendMouseClick(button);
            }
            finally
            {
                if (cursorMoved && !SetCursorPosition(originalCursor.X, originalCursor.Y))
                    WarnDiagnostic("Foreground mouse lease could not restore the previous cursor position.");

                if (targetWasMinimized)
                    _ = ShowWindowAsync(hwnd, SW_MINIMIZE);

                if (activated
                    && previousForeground != nint.Zero
                    && previousForeground != hwnd
                    && !RestoreForegroundWindow(previousForeground))
                {
                    WarnDiagnostic(
                        $"Foreground mouse lease could not restore HWND=0x{previousForeground.ToInt64():X}.");
                }
            }
        }
    }

    private bool SendMouseClick(InputMouseButton button)
    {
        if (_platformHooks?.SendMouseClick is { } testSender)
            return testSender(button);

        var down = button == InputMouseButton.Right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
        var up = button == InputMouseButton.Right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;
        var buttonDown = false;
        try
        {
            var downSent = SendInput(1, new[] { MouseInput(down) }, Marshal.SizeOf<INPUT>());
            if (downSent != 1)
            {
                WarnDiagnostic($"SendInput sent {downSent}/1 mouse-down inputs. Win32={Marshal.GetLastWin32Error()}");
                return false;
            }

            buttonDown = true;
            Thread.Sleep(ForegroundMouseHoldMs);

            var upSent = SendInput(1, new[] { MouseInput(up) }, Marshal.SizeOf<INPUT>());
            if (upSent != 1)
            {
                WarnDiagnostic($"SendInput sent {upSent}/1 mouse-up inputs. Win32={Marshal.GetLastWin32Error()}");
                return false;
            }

            buttonDown = false;
            return true;
        }
        finally
        {
            if (buttonDown)
                _ = SendInput(1, new[] { MouseInput(up) }, Marshal.SizeOf<INPUT>());
        }
    }

    private bool CanAcquireForegroundLease(out string reason)
    {
        double ageMs;
        bool buttonsDown;
        if (_platformHooks?.GetPhysicalMouseActivity is { } testActivity)
        {
            var activity = testActivity();
            if (!activity.HasActivity)
            {
                reason = string.Empty;
                return true;
            }

            ageMs = activity.AgeMs;
            buttonsDown = activity.ButtonsDown;
        }
        else if (!TryGetLastPhysicalMouseActivity(out ageMs, out buttonsDown))
        {
            reason = string.Empty;
            return true;
        }

        if (!buttonsDown && ageMs > ForegroundLeaseUserIdleMs)
        {
            reason = string.Empty;
            return true;
        }

        reason = buttonsDown
            ? "检测到玩家正在按住鼠标，本次自动点击已暂停。"
            : $"检测到玩家刚刚操作鼠标，本次自动点击延后约 {Math.Max(0, ForegroundLeaseUserIdleMs - ageMs):0}ms。";
        return false;
    }

    private bool TryGetCursorPosition(out POINT point)
    {
        if (_platformHooks?.GetCursorPosition is { } testGetter)
        {
            var result = testGetter();
            point = new POINT { X = result.X, Y = result.Y };
            return result.Success;
        }

        return GetCursorPos(out point);
    }

    private bool SetCursorPosition(int x, int y) =>
        _platformHooks?.SetCursorPosition?.Invoke(x, y) ?? SetCursorPos(x, y);

    private bool PostKey(nint hwnd, ushort vk, bool isDown, bool altContext)
    {
        var message = altContext
            ? isDown ? WM_SYSKEYDOWN : WM_SYSKEYUP
            : isDown ? WM_KEYDOWN : WM_KEYUP;

        var scan = MapVirtualKey(vk, MAPVK_VK_TO_VSC);
        var lParam = 1 | ((int)scan << 16);
        if (altContext)
            lParam |= 1 << 29;
        if (!isDown)
            lParam |= 1 << 30 | unchecked((int)0x80000000);

        if (PostMessage(hwnd, message, vk, lParam))
            return true;

        return WarnLastError($"PostMessage key {vk}");
    }

    private bool TryActivateForSendInput(nint hwnd, string action)
    {
        var initialForeground = CurrentForegroundWindow();
        if (initialForeground == hwnd)
            return true;

        // The app may hand focus to the selected game once when the user starts a
        // task. Never take focus back from an unrelated application afterwards.
        if (initialForeground != nint.Zero)
        {
            _ = GetWindowThreadProcessId(initialForeground, out var foregroundProcessId);
            if (foregroundProcessId != (uint)Environment.ProcessId)
            {
                _log.Trace(Module, $"{action} paused because another application owns the foreground window.");
                return false;
            }
        }

        _ = ShowWindowAsync(hwnd, SW_RESTORE);
        _ = BringWindowToTop(hwnd);
        _ = SetForegroundWindow(hwnd);
        Thread.Sleep(30);
        if (CurrentForegroundWindow() == hwnd)
            return true;

        var foreground = CurrentForegroundWindow();
        var currentThread = GetCurrentThreadId();
        var targetThread = GetWindowThreadProcessId(hwnd, out _);
        var foregroundThread = foreground == nint.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        var attachedTarget = false;
        var attachedForeground = false;

        try
        {
            if (targetThread != 0 && targetThread != currentThread)
                attachedTarget = AttachThreadInput(currentThread, targetThread, true);
            if (foregroundThread != 0 && foregroundThread != currentThread && foregroundThread != targetThread)
                attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);

            _ = ShowWindowAsync(hwnd, SW_RESTORE);
            _ = BringWindowToTop(hwnd);
            _ = SetForegroundWindow(hwnd);
            _ = SetActiveWindow(hwnd);
            _ = SetFocus(hwnd);
            Thread.Sleep(50);
        }
        finally
        {
            if (attachedForeground)
                _ = AttachThreadInput(currentThread, foregroundThread, false);
            if (attachedTarget)
                _ = AttachThreadInput(currentThread, targetThread, false);
        }

        var activated = CurrentForegroundWindow() == hwnd;
        if (!activated)
            _log.Trace(Module, $"{action} foreground activation failed, fallback will be used.");
        return activated;
    }

    private bool TryActivateForForegroundLease(nint hwnd, string action)
    {
        if (_platformHooks?.ActivateForegroundLease is { } testActivator)
            return testActivator(hwnd, action);

        if (CurrentForegroundWindow() == hwnd)
            return true;

        _ = ShowWindowAsync(hwnd, SW_RESTORE);
        _ = BringWindowToTop(hwnd);
        _ = SetForegroundWindow(hwnd);
        Thread.Sleep(ForegroundLeaseSettleMs);
        if (CurrentForegroundWindow() == hwnd)
            return true;

        var currentForeground = CurrentForegroundWindow();
        var currentThread = GetCurrentThreadId();
        var targetThread = GetWindowThreadProcessId(hwnd, out _);
        var foregroundThread = currentForeground == nint.Zero
            ? 0
            : GetWindowThreadProcessId(currentForeground, out _);
        var attachedTarget = false;
        var attachedForeground = false;
        try
        {
            if (targetThread != 0 && targetThread != currentThread)
                attachedTarget = AttachThreadInput(currentThread, targetThread, true);
            if (foregroundThread != 0 && foregroundThread != currentThread && foregroundThread != targetThread)
                attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);

            _ = ShowWindowAsync(hwnd, SW_RESTORE);
            _ = BringWindowToTop(hwnd);
            _ = SetForegroundWindow(hwnd);
            _ = SetActiveWindow(hwnd);
            _ = SetFocus(hwnd);
            Thread.Sleep(ForegroundLeaseSettleMs);
        }
        finally
        {
            if (attachedForeground)
                _ = AttachThreadInput(currentThread, foregroundThread, false);
            if (attachedTarget)
                _ = AttachThreadInput(currentThread, targetThread, false);
        }

        var activated = CurrentForegroundWindow() == hwnd;
        if (!activated)
            _log.Trace(Module, $"{action} foreground lease could not activate HWND=0x{hwnd.ToInt64():X}.");
        return activated;
    }

    private bool RestoreForegroundWindow(nint hwnd)
    {
        if (_platformHooks?.RestoreForegroundWindow is { } testRestorer)
            return testRestorer(hwnd);

        if (hwnd == nint.Zero || !IsWindow(hwnd))
            return false;
        if (CurrentForegroundWindow() == hwnd)
            return true;

        var currentForeground = CurrentForegroundWindow();
        var currentThread = GetCurrentThreadId();
        var restoreThread = GetWindowThreadProcessId(hwnd, out _);
        var foregroundThread = currentForeground == nint.Zero
            ? 0
            : GetWindowThreadProcessId(currentForeground, out _);
        var attachedRestore = false;
        var attachedForeground = false;
        try
        {
            if (restoreThread != 0 && restoreThread != currentThread)
                attachedRestore = AttachThreadInput(currentThread, restoreThread, true);
            if (foregroundThread != 0 && foregroundThread != currentThread && foregroundThread != restoreThread)
                attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);

            _ = BringWindowToTop(hwnd);
            _ = SetForegroundWindow(hwnd);
            _ = SetActiveWindow(hwnd);
            _ = SetFocus(hwnd);
            Thread.Sleep(ForegroundLeaseRestoreSettleMs);
        }
        finally
        {
            if (attachedForeground)
                _ = AttachThreadInput(currentThread, foregroundThread, false);
            if (attachedRestore)
                _ = AttachThreadInput(currentThread, restoreThread, false);
        }

        return CurrentForegroundWindow() == hwnd;
    }

    private void WarnActivationFallbackOnce(nint hwnd, string action)
    {
        var key = $"{hwnd:X}:{action}";
        if (string.Equals(_lastActivationFallback, key, StringComparison.Ordinal))
            return;

        _lastActivationFallback = key;
        WarnDiagnostic($"目标游戏不在前台，{action}已暂停；不会抢占其他应用，也不会伪报后台投递成功。");
    }

    private bool ShouldYieldToRecentUserInput(nint hwnd, InputDispatchMode mode, string action, out string reason)
    {
        if (ShouldYieldForMouseCoexistence(hwnd, out var coexistenceReason))
        {
            reason = $"{InputActionText(action)}已短暂让控：{coexistenceReason}";
            return true;
        }

        var status = BuildYieldStatus(hwnd, mode);
        if (!status.ShouldYield)
        {
            reason = string.Empty;
            return false;
        }

        reason = $"{InputActionText(action)}已让控：{status.Reason}";
        return true;
    }

    private bool ShouldYieldForMouseCoexistence(nint hwnd, out string reason)
    {
        var policy = GetWindowInputPolicy(hwnd);
        if (!policy.YieldToUserInput)
        {
            reason = string.Empty;
            return false;
        }

        if (!TryGetLastPhysicalMouseClick(out var click, out var ageMs, out var buttonsDown)
            || (!buttonsDown && ageMs > MouseCoexistenceYieldMs))
        {
            reason = string.Empty;
            return false;
        }

        var targetForeground = IsWindowWithinTarget(hwnd, click.ForegroundWindow);
        var cursorInside = IsScreenPointInsideClient(hwnd, click.ScreenPoint);
        if (!targetForeground && !cursorInside)
        {
            reason = string.Empty;
            return false;
        }

        lock (_backgroundDispatchLock)
        {
            // No synthetic focus lease exists in PostMessage mode.
        }

        reason = buttonsDown
            ? "检测到玩家正在按住游戏窗口鼠标，自动输入持续让控。"
            : $"检测到玩家点击游戏窗口，保留 {Math.Max(0, MouseCoexistenceYieldMs - ageMs):0}ms 鼠标操作窗口。";
        return true;
    }

    private InputYieldStatus BuildYieldStatus(nint hwnd, InputDispatchMode mode)
    {
        _ = mode;
        var policy = GetWindowInputPolicy(hwnd);
        var yieldMs = policy.UserInputYieldMs;
        if (!policy.YieldToUserInput || yieldMs <= 0)
        {
            return new InputYieldStatus(
                Enabled: false,
                CanReadLastInput: false,
                ShouldYield: false,
                YieldMs: Math.Max(0, yieldMs),
                LastInputAgeMs: -1,
                RemainingYieldMs: 0,
                OwnRecentAutomationInput: false,
                TargetForeground: false,
                CursorInsideClient: false,
                TargetActive: false,
                Scope: "disabled",
                Reason: "人工操作保护未启用。");
        }

        if (_mouseHook == nint.Zero)
        {
            return new InputYieldStatus(
                Enabled: true,
                CanReadLastInput: false,
                ShouldYield: false,
                YieldMs: yieldMs,
                LastInputAgeMs: -1,
                RemainingYieldMs: 0,
                OwnRecentAutomationInput: false,
                TargetForeground: false,
                CursorInsideClient: false,
                TargetActive: false,
                Scope: "mouse-monitor-unavailable",
                Reason: "鼠标操作监控未就绪。");
        }

        if (!TryGetLastPhysicalMouseClick(out var click, out var ageMs, out _))
        {
            return new InputYieldStatus(
                Enabled: true,
                CanReadLastInput: true,
                ShouldYield: false,
                YieldMs: yieldMs,
                LastInputAgeMs: -1,
                RemainingYieldMs: 0,
                OwnRecentAutomationInput: false,
                TargetForeground: false,
                CursorInsideClient: false,
                TargetActive: false,
                Scope: "no-mouse-click",
                Reason: "尚未检测到玩家鼠标点击。");
        }

        var targetForeground = IsWindowWithinTarget(hwnd, click.ForegroundWindow);
        var cursorInside = IsScreenPointInsideClient(hwnd, click.ScreenPoint);
        var targetActive = targetForeground || cursorInside;
        var remainingMs = Math.Max(0, yieldMs - ageMs);

        if (ageMs > yieldMs)
        {
            return new InputYieldStatus(
                Enabled: true,
                CanReadLastInput: true,
                ShouldYield: false,
                YieldMs: yieldMs,
                LastInputAgeMs: ageMs,
                RemainingYieldMs: 0,
                OwnRecentAutomationInput: false,
                TargetForeground: targetForeground,
                CursorInsideClient: cursorInside,
                TargetActive: targetActive,
                Scope: "expired",
                Reason: $"最近鼠标点击已超过暂停时间：{ageMs:0}ms > {yieldMs}ms。");
        }

        if (!targetActive)
        {
            return new InputYieldStatus(
                Enabled: true,
                CanReadLastInput: true,
                ShouldYield: false,
                YieldMs: yieldMs,
                LastInputAgeMs: ageMs,
                RemainingYieldMs: remainingMs,
                OwnRecentAutomationInput: false,
                TargetForeground: targetForeground,
                CursorInsideClient: cursorInside,
                TargetActive: false,
                Scope: "other-window",
                Reason: "最近鼠标点击不属于当前游戏窗口。");
        }

        return new InputYieldStatus(
            Enabled: true,
            CanReadLastInput: true,
            ShouldYield: true,
            YieldMs: yieldMs,
            LastInputAgeMs: ageMs,
            RemainingYieldMs: remainingMs,
            OwnRecentAutomationInput: false,
            TargetForeground: targetForeground,
            CursorInsideClient: cursorInside,
            TargetActive: targetActive,
            Scope: "target-window",
            Reason: $"检测到 {ageMs:0}ms 内玩家点击鼠标，剩余暂停约 {remainingMs:0}ms。");
    }

    private WindowInputPolicy GetWindowInputPolicy(nint hwnd)
    {
        lock (_inputPolicyLock)
        {
            if (hwnd != nint.Zero && _windowInputPolicies.TryGetValue(hwnd, out var policy))
                return policy;
        }

        return new WindowInputPolicy(
            DefaultDispatchMode,
            YieldToUserInput,
            Math.Clamp(UserInputYieldMs, 0, 60000));
    }

    private bool TryGetLastPhysicalMouseClick(out MouseClickSnapshot click, out double ageMs, out bool buttonsDown)
    {
        lock (_mouseActivityLock)
        {
            if (_lastPhysicalMouseClick is not { } snapshot)
            {
                click = default;
                ageMs = -1;
                buttonsDown = false;
                return false;
            }

            click = snapshot;
            buttonsDown = _physicalMouseButtonsDown != 0;
        }

        ageMs = Math.Max(
            0,
            (Stopwatch.GetTimestamp() - click.Timestamp) * 1000d / Stopwatch.Frequency);
        return true;
    }

    private bool TryGetLastPhysicalMouseActivity(out double ageMs, out bool buttonsDown)
    {
        long activityTicks;
        lock (_mouseActivityLock)
        {
            activityTicks = _lastPhysicalMouseActivityTicks;
            buttonsDown = _physicalMouseButtonsDown != 0;
        }

        if (activityTicks == 0)
        {
            ageMs = -1;
            return false;
        }

        ageMs = Math.Max(
            0,
            (Stopwatch.GetTimestamp() - activityTicks) * 1000d / Stopwatch.Frequency);
        return true;
    }

    private static bool IsWindowWithinTarget(nint hwnd, nint candidate)
    {
        return candidate != nint.Zero
               && (candidate == hwnd || IsChild(hwnd, candidate));
    }

    private nint CurrentForegroundWindow() => _foregroundWindowProvider();

    private static bool IsScreenPointInsideClient(nint hwnd, POINT screenPoint)
    {
        var client = screenPoint;
        if (!ScreenToClient(hwnd, ref client))
            return false;

        return GetClientRect(hwnd, out var rect)
               && client.X >= rect.Left
               && client.Y >= rect.Top
               && client.X < rect.Right
               && client.Y < rect.Bottom;
    }

    private nint MouseHookCallback(int nCode, nint wParam, nint lParam)
    {
        try
        {
            var message = unchecked((uint)wParam.ToInt64());
            if (nCode >= 0)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if ((data.Flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) == 0)
                {
                    var now = Stopwatch.GetTimestamp();
                    lock (_mouseActivityLock)
                    {
                        _lastPhysicalMouseActivityTicks = now;
                        if (IsMouseButtonMessage(message))
                        {
                            var bit = MouseButtonBit(message);
                            if (IsMouseButtonDownMessage(message))
                                _physicalMouseButtonsDown |= bit;
                            else
                                _physicalMouseButtonsDown &= ~bit;
                            _lastPhysicalMouseClick = new MouseClickSnapshot(
                                now,
                                data.Point,
                                CurrentForegroundWindow());
                        }
                    }
                }
            }
        }
        catch
        {
            // A low-level hook must never interrupt the system input chain.
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static bool IsMouseButtonDownMessage(uint message) => message is
        WM_LBUTTONDOWN or
        WM_RBUTTONDOWN or
        WM_MBUTTONDOWN or
        WM_XBUTTONDOWN;

    private static bool IsMouseButtonMessage(uint message) =>
        IsMouseButtonDownMessage(message) || message is
            WM_LBUTTONUP or
            WM_RBUTTONUP or
            WM_MBUTTONUP or
            WM_XBUTTONUP;

    private static uint MouseButtonBit(uint message) => message switch
    {
        WM_LBUTTONDOWN or WM_LBUTTONUP => 1u,
        WM_RBUTTONDOWN or WM_RBUTTONUP => 2u,
        WM_MBUTTONDOWN or WM_MBUTTONUP => 4u,
        WM_XBUTTONDOWN or WM_XBUTTONUP => 8u,
        _ => 0u
    };

    public void Dispose()
    {
        nint hook;
        lock (_mouseActivityLock)
        {
            hook = _mouseHook;
            _mouseHook = nint.Zero;
            _lastPhysicalMouseClick = null;
            _lastPhysicalMouseActivityTicks = 0;
            _physicalMouseButtonsDown = 0;
        }

        if (hook != nint.Zero)
            _ = UnhookWindowsHookEx(hook);

        GC.KeepAlive(_mouseHookProc);
        GC.SuppressFinalize(this);
    }

    private void WarnUserYieldOnce(nint hwnd, string reason)
    {
        var now = Stopwatch.GetTimestamp();
        var key = $"{hwnd:X}:{reason}";
        if (string.Equals(_lastUserYieldWarning, key, StringComparison.Ordinal)
            && _lastUserYieldWarningTicks != 0
            && (now - _lastUserYieldWarningTicks) * 1000.0 / Stopwatch.Frequency < 1500)
        {
            return;
        }

        _lastUserYieldWarning = key;
        _lastUserYieldWarningTicks = now;
        _log.Warn(UserYieldModule, reason);
    }

    private static string InputActionText(string action) => action switch
    {
        "SendKey" => "按键",
        "SendText" => "文本",
        "Click" => "点击",
        _ => action
    };

    private bool EnsureWindow(nint hwnd, string action)
    {
        if (hwnd == nint.Zero || !IsWindow(hwnd))
        {
            WarnDiagnostic($"{action} skipped: invalid target window.");
            return false;
        }

        return true;
    }

    private bool WarnLastError(string action)
    {
        WarnDiagnostic($"{action} failed. Win32={Marshal.GetLastWin32Error()}");
        return false;
    }

    private void WarnDiagnostic(string message) =>
        _log.Log(new LogEntry
        {
            Level = LogLevel.Warn,
            Module = Module,
            Message = message,
            Audience = LogAudience.Diagnostic
        });

    private static INPUT KeyInput(ushort vk, bool keyUp) => new()
    {
        Type = INPUT_KEYBOARD,
        Data = new INPUTUNION
        {
            Keyboard = new KEYBDINPUT
            {
                Vk = vk,
                Scan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC),
                Flags = keyUp ? KEYEVENTF_KEYUP : 0,
                Time = 0,
                ExtraInfo = nint.Zero
            }
        }
    };

    private static INPUT UnicodeInput(char ch, bool keyUp) => new()
    {
        Type = INPUT_KEYBOARD,
        Data = new INPUTUNION
        {
            Keyboard = new KEYBDINPUT
            {
                Vk = 0,
                Scan = ch,
                Flags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
                Time = 0,
                ExtraInfo = nint.Zero
            }
        }
    };

    private static INPUT MouseInput(uint flags) => new()
    {
        Type = INPUT_MOUSE,
        Data = new INPUTUNION
        {
            Mouse = new MOUSEINPUT
            {
                Dx = 0,
                Dy = 0,
                MouseData = 0,
                Flags = flags,
                Time = 0,
                ExtraInfo = nint.Zero
            }
        }
    };

    private static nint MakeLParam(int low, int high) =>
        (nint)((high & 0xFFFF) << 16 | (low & 0xFFFF));

    private sealed record HotkeyChord(string DisplayText, ushort[] ModifierVks, ushort KeyVk)
    {
        public static bool TryParse(string? value, out HotkeyChord chord, out string error)
        {
            chord = default!;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(value))
            {
                error = "hotkey is empty";
                return false;
            }

            var tokens = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
            {
                error = "hotkey is empty";
                return false;
            }

            var modifiers = new List<ushort>();
            ushort? main = null;
            var normalized = new List<string>();

            foreach (var raw in tokens)
            {
                if (TryMapModifier(raw, out var modifier, out var modifierName))
                {
                    if (!modifiers.Contains(modifier))
                    {
                        modifiers.Add(modifier);
                        normalized.Add(modifierName);
                    }
                    continue;
                }

                if (main is not null)
                {
                    error = "multiple main keys are not supported";
                    return false;
                }

                if (!TryMapKey(raw, out var keyVk, out var keyName))
                {
                    error = $"unsupported key '{raw}'";
                    return false;
                }

                main = keyVk;
                normalized.Add(keyName);
            }

            if (main is null)
            {
                error = "main key is missing";
                return false;
            }

            chord = new HotkeyChord(string.Join("+", normalized), modifiers.ToArray(), main.Value);
            return true;
        }

        private static bool TryMapModifier(string token, out ushort vk, out string normalized)
        {
            switch (Normalize(token))
            {
                case "CTRL":
                case "CONTROL":
                    vk = VK_CONTROL;
                    normalized = "Ctrl";
                    return true;
                case "ALT":
                    vk = VK_MENU;
                    normalized = "Alt";
                    return true;
                case "SHIFT":
                    vk = VK_SHIFT;
                    normalized = "Shift";
                    return true;
                case "WIN":
                case "WINDOWS":
                    vk = VK_LWIN;
                    normalized = "Win";
                    return true;
                default:
                    vk = 0;
                    normalized = string.Empty;
                    return false;
            }
        }

        private static bool TryMapKey(string token, out ushort vk, out string normalized)
        {
            var key = Normalize(token);

            if (key.Length == 1)
            {
                var c = key[0];
                if (c is >= 'A' and <= 'Z')
                {
                    vk = c;
                    normalized = c.ToString();
                    return true;
                }

                if (c is >= '0' and <= '9')
                {
                    vk = c;
                    normalized = c.ToString();
                    return true;
                }
            }

            if (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24)
            {
                vk = (ushort)(VK_F1 + f - 1);
                normalized = "F" + f;
                return true;
            }

            if (key.StartsWith("NUMPAD", StringComparison.Ordinal) && int.TryParse(key[6..], out var numpad) && numpad is >= 0 and <= 9)
            {
                vk = (ushort)(VK_NUMPAD0 + numpad);
                normalized = "Num" + numpad;
                return true;
            }

            if (key.StartsWith("NUM", StringComparison.Ordinal) && int.TryParse(key[3..], out var num) && num is >= 0 and <= 9)
            {
                vk = (ushort)(VK_NUMPAD0 + num);
                normalized = "Num" + num;
                return true;
            }

            if (NamedKeys.TryGetValue(key, out var mapped))
            {
                vk = mapped.Vk;
                normalized = mapped.Name;
                return true;
            }

            vk = 0;
            normalized = string.Empty;
            return false;
        }

        private static string Normalize(string value) =>
            value.Trim().Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
    }

    private static readonly Dictionary<string, (ushort Vk, string Name)> NamedKeys = new(StringComparer.Ordinal)
    {
        ["SPACE"] = (VK_SPACE, "Space"),
        ["TAB"] = (VK_TAB, "Tab"),
        ["ENTER"] = (VK_RETURN, "Enter"),
        ["RETURN"] = (VK_RETURN, "Enter"),
        ["ESC"] = (VK_ESCAPE, "Esc"),
        ["ESCAPE"] = (VK_ESCAPE, "Esc"),
        ["BACKSPACE"] = (VK_BACK, "Backspace"),
        ["BKSP"] = (VK_BACK, "Backspace"),
        ["DELETE"] = (VK_DELETE, "Delete"),
        ["DEL"] = (VK_DELETE, "Delete"),
        ["INSERT"] = (VK_INSERT, "Insert"),
        ["INS"] = (VK_INSERT, "Insert"),
        ["HOME"] = (VK_HOME, "Home"),
        ["END"] = (VK_END, "End"),
        ["PAGEUP"] = (VK_PRIOR, "PageUp"),
        ["PGUP"] = (VK_PRIOR, "PageUp"),
        ["PAGEDOWN"] = (VK_NEXT, "PageDown"),
        ["PGDN"] = (VK_NEXT, "PageDown"),
        ["UP"] = (VK_UP, "Up"),
        ["DOWN"] = (VK_DOWN, "Down"),
        ["LEFT"] = (VK_LEFT, "Left"),
        ["RIGHT"] = (VK_RIGHT, "Right"),
        ["ADD"] = (VK_ADD, "Add"),
        ["SUBTRACT"] = (VK_SUBTRACT, "Subtract"),
        ["MULTIPLY"] = (VK_MULTIPLY, "Multiply"),
        ["DIVIDE"] = (VK_DIVIDE, "Divide"),
        ["DECIMAL"] = (VK_DECIMAL, "Decimal")
    };

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MAPVK_VK_TO_VSC = 0;
    private const int BackgroundKeyHoldMs = 30;
    private const int BackgroundMouseHoldMs = 30;
    private const int ForegroundMouseHoldMs = 50;
    private const int ForegroundLeaseSettleMs = 35;
    private const int ForegroundLeaseRestoreSettleMs = 20;
    private const int ForegroundLeaseUserIdleMs = 800;
    private const int MouseCoexistenceYieldMs = 800;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const int WH_MOUSE_LL = 14;
    private const uint LLMHF_INJECTED = 0x00000001;
    private const uint LLMHF_LOWER_IL_INJECTED = 0x00000002;

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_CHAR = 0x0102;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_SYSKEYUP = 0x0105;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MBUTTONUP = 0x0208;
    private const uint WM_XBUTTONDOWN = 0x020B;
    private const uint WM_XBUTTONUP = 0x020C;
    private static readonly nint MK_LBUTTON = 0x0001;
    private static readonly nint MK_RBUTTON = 0x0002;

    private const ushort VK_BACK = 0x08;
    private const ushort VK_TAB = 0x09;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_ESCAPE = 0x1B;
    private const ushort VK_SPACE = 0x20;
    private const ushort VK_PRIOR = 0x21;
    private const ushort VK_NEXT = 0x22;
    private const ushort VK_END = 0x23;
    private const ushort VK_HOME = 0x24;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_UP = 0x26;
    private const ushort VK_RIGHT = 0x27;
    private const ushort VK_DOWN = 0x28;
    private const ushort VK_INSERT = 0x2D;
    private const ushort VK_DELETE = 0x2E;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_NUMPAD0 = 0x60;
    private const ushort VK_MULTIPLY = 0x6A;
    private const ushort VK_ADD = 0x6B;
    private const ushort VK_SUBTRACT = 0x6D;
    private const ushort VK_DECIMAL = 0x6E;
    private const ushort VK_DIVIDE = 0x6F;
    private const ushort VK_F1 = 0x70;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private readonly record struct MouseClickSnapshot(
        long Timestamp,
        POINT ScreenPoint,
        nint ForegroundWindow);

    private readonly record struct WindowInputPolicy(
        InputDispatchMode DispatchMode,
        bool YieldToUserInput,
        int UserInputYieldMs);

    private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUTUNION Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsChild(nint hWndParent, nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint SetActiveWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ScreenToClient(nint hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int idHook,
        LowLevelMouseProc callback,
        nint module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);
}
