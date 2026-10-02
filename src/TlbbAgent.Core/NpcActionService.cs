using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class NpcActionService : INpcActionService
{
    private const string Module = "NpcAction";
    private readonly IInputService _input;
    private readonly ILogService _log;

    public NpcActionService(IInputService input, ILogService log)
    {
        _input = input;
        _log = log;
    }

    public NpcActionResult Scan(BoundGameWindow window, NpcGrabConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ScanHotkey))
            return new NpcActionResult(false, "未配置扫描快捷键。");

        if (!_input.TryParseHotkey(config.ScanHotkey, out var error))
        {
            var message = $"扫描快捷键无效：{config.ScanHotkey} | {error}";
            _log.Warn(Module, message, window.Id);
            return new NpcActionResult(false, message);
        }

        _input.SendKey(window.Hwnd, config.ScanHotkey, config.DispatchMode);
        var ok = $"已发送扫描快捷键：{config.ScanHotkey}";
        _log.Trace(Module, ok, window.Id);
        return new NpcActionResult(true, ok);
    }

    public NpcActionResult Execute(BoundGameWindow window, NpcTargetingResult target, NpcGrabConfig config)
    {
        if (!target.Found)
            return new NpcActionResult(false, "未选中抢 NPC 目标。");

        switch (config.ActionMode)
        {
            case NpcGrabActionMode.HotkeyOnly:
                return ExecuteHotkey(window, target, config);
            case NpcGrabActionMode.ClickClientPoint:
                return ExecuteClick(window, target, config);
            case NpcGrabActionMode.HotkeyThenClickClientPoint:
                var hotkey = ExecuteHotkey(window, target, config);
                var click = ExecuteClick(window, target, config);
                return new NpcActionResult(hotkey.Executed || click.Executed, hotkey.Message + "；" + click.Message);
            default:
                return new NpcActionResult(false, $"未知动作模式：{config.ActionMode}");
        }
    }

    private NpcActionResult ExecuteHotkey(BoundGameWindow window, NpcTargetingResult target, NpcGrabConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ActionHotkey))
            return new NpcActionResult(false, "未配置抢 NPC 动作快捷键。");

        if (!_input.TryParseHotkey(config.ActionHotkey, out var error))
        {
            var message = $"动作快捷键无效：{config.ActionHotkey} | {error}";
            _log.Warn(Module, message, window.Id);
            return new NpcActionResult(false, message);
        }

        _input.SendKey(window.Hwnd, config.ActionHotkey, config.DispatchMode);
        var ok = $"已对 {target.DisplayName} 发送动作快捷键：{config.ActionHotkey}";
        _log.Info(Module, ok, window.Id);
        return new NpcActionResult(true, ok);
    }

    private NpcActionResult ExecuteClick(BoundGameWindow window, NpcTargetingResult target, NpcGrabConfig config)
    {
        if (config.ClickX < 0 || config.ClickY < 0)
        {
            var message = $"点击坐标无效：{config.ClickX},{config.ClickY}";
            _log.Warn(Module, message, window.Id);
            return new NpcActionResult(false, message);
        }

        _input.Click(window.Hwnd, config.ClickX, config.ClickY, config.DispatchMode);
        var ok = $"已对 {target.DisplayName} 点击客户区坐标：{config.ClickX},{config.ClickY}";
        _log.Info(Module, ok, window.Id);
        return new NpcActionResult(true, ok);
    }
}

