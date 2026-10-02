using System.Collections.Concurrent;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class GameBuiltInHangSwitchService : IGameBuiltInHangSwitchService
{
    private const string Module = "内置挂机开关";
    private readonly IInputService _input;
    private readonly ILogService _log;
    private readonly ConcurrentDictionary<long, long> _lastExecutedTicks = new();

    public GameBuiltInHangSwitchService(IInputService input, ILogService log)
    {
        _input = input;
        _log = log;
    }

    public GameBuiltInHangSwitchResult TryTrigger(BoundGameWindow window, GlobalConfig config, bool execute)
    {
        if (config.GameBuiltInHangSwitchMode == GameBuiltInHangSwitchMode.Disabled)
            return Result(config, execute, false, false, false, true, "none", "未配置内置挂机开关。");

        if (!window.IsAutomationAllowed)
            return Result(config, execute, false, false, true, false, "none", window.AutomationBlockReason);

        _input.ConfigureWindowInput(
            window.Hwnd,
            config.InputDispatchMode,
            config.YieldToManualInput,
            config.ManualInputYieldMs);

        return config.GameBuiltInHangSwitchMode switch
        {
            GameBuiltInHangSwitchMode.Hotkey => TriggerHotkey(window, config, execute),
            GameBuiltInHangSwitchMode.ClientClick => TriggerClientClick(window, config, execute),
            _ => Result(config, execute, false, false, false, false, "none", "未知的内置挂机开关模式。")
        };
    }

    private GameBuiltInHangSwitchResult TriggerHotkey(BoundGameWindow window, GlobalConfig config, bool execute)
    {
        var hotkey = (config.GameBuiltInHangSwitchHotkey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(hotkey))
            return Result(config, execute, false, false, true, false, "hotkey", "未配置内置挂机开关快捷键。", hotkey);

        if (!_input.TryParseHotkey(hotkey, out var error))
            return Result(config, execute, false, false, true, false, "hotkey", $"快捷键无效：{error}", hotkey);

        if (!execute)
            return Result(config, execute, false, true, true, true, "hotkey", $"预演通过：将发送快捷键 {hotkey}。", hotkey);

        if (!TryEnterCooldown(window, config, out var cooldownMessage))
            return Result(config, execute, false, false, true, true, "hotkey", cooldownMessage, hotkey);

        var ok = _input.SendKey(window.Hwnd, hotkey, config.InputDispatchMode);
        var message = ok ? $"已发送内置挂机开关快捷键 {hotkey}。" : $"内置挂机开关快捷键发送失败：{hotkey}。";
        if (ok)
            _log.Info(Module, message, window.Id);
        else
            _log.Warn(Module, message, window.Id);

        return Result(config, execute, ok, true, true, true, "hotkey", message, hotkey);
    }

    private GameBuiltInHangSwitchResult TriggerClientClick(BoundGameWindow window, GlobalConfig config, bool execute)
    {
        var x = config.GameBuiltInHangSwitchClickX;
        var y = config.GameBuiltInHangSwitchClickY;
        if (x <= 0 || y <= 0)
            return Result(config, execute, false, false, true, false, "client-click", "未配置内置挂机开关客户区坐标。", "", x, y);

        if (x >= window.ClientWidth || y >= window.ClientHeight)
        {
            var boundsMessage = $"内置挂机开关坐标超出客户区：({x},{y}) / {window.ClientWidth}x{window.ClientHeight}。";
            return Result(config, execute, false, false, true, false, "client-click", boundsMessage, "", x, y);
        }

        if (!execute)
            return Result(config, execute, false, true, true, true, "client-click", $"预演通过：将点击客户区 ({x},{y})。", "", x, y);

        if (!TryEnterCooldown(window, config, out var cooldownMessage))
            return Result(config, execute, false, false, true, true, "client-click", cooldownMessage, "", x, y);

        var ok = _input.Click(window.Hwnd, x, y, config.InputDispatchMode);
        var message = ok ? $"已点击内置挂机开关客户区 ({x},{y})。" : $"内置挂机开关客户区点击失败：({x},{y})。";
        if (ok)
            _log.Info(Module, message, window.Id);
        else
            _log.Warn(Module, message, window.Id);

        return Result(config, execute, ok, true, true, true, "client-click", message, "", x, y);
    }

    private bool TryEnterCooldown(BoundGameWindow window, GlobalConfig config, out string message)
    {
        var cooldownMs = Math.Clamp(config.GameBuiltInHangSwitchCooldownMs, 500, 60000);
        var now = Environment.TickCount64;
        var key = (long)window.Hwnd;
        var last = _lastExecutedTicks.GetOrAdd(key, 0);
        if (last != 0 && now - last < cooldownMs)
        {
            message = $"内置挂机开关冷却中，剩余约 {(cooldownMs - (now - last)) / 1000.0:0.0} 秒。";
            return false;
        }

        _lastExecutedTicks[key] = now;
        message = string.Empty;
        return true;
    }

    private static GameBuiltInHangSwitchResult Result(
        GlobalConfig config,
        bool requestedExecution,
        bool executed,
        bool wouldExecute,
        bool isConfigured,
        bool isValid,
        string action,
        string message,
        string hotkey = "",
        int? clientX = null,
        int? clientY = null) =>
        new(
            config.GameBuiltInHangSwitchMode,
            requestedExecution,
            executed,
            wouldExecute,
            isConfigured,
            isValid,
            action,
            message,
            hotkey,
            clientX,
            clientY,
            config.InputDispatchMode);
}
