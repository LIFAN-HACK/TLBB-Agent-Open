using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

/// <summary>
/// Production external combat backend. It consumes only the new perception pipeline:
/// LatestFrame -> L1 layout -> L2 slot/OpenText -> policy.
/// </summary>
public sealed class VisionCombatBackend : ICombatBackend
{
    private readonly IPerceptionHub _perceptionHub;
    private readonly IInputService _input;
    private readonly ILogService _log;
    private readonly Dictionary<string, long> _lastInputDiagnosticTicks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastPerceptionDiagnosticTicks = new(StringComparer.Ordinal);

    public VisionCombatBackend(IPerceptionHub perceptionHub, IInputService input, ILogService log)
    {
        _perceptionHub = perceptionHub;
        _input = input;
        _log = log;
    }

    // Compatibility enum name. The implementation no longer uses the legacy OCR stack.
    public CombatBackendKind Kind => CombatBackendKind.VisualModel;
    public string DisplayName => "视觉模型";

    public CombatBackendStatus GetStatus(BoundGameWindow window, GlobalConfig config)
    {
        if (!window.IsAutomationAllowed)
            return new CombatBackendStatus(Kind, DisplayName, false, window.AutomationBlockReason);

        return new CombatBackendStatus(Kind, DisplayName, true, "视觉模型与外部按键后端可用。");
    }

    public CombatRecognitionResult Read(BoundGameWindow window, GlobalConfig config)
    {
        try
        {
            var snapshot = _perceptionHub.Read(window, config, ShouldReadOpenText(config));
            LogPerceptionDiagnostic(window, snapshot);
            return VisionCombatResultMapper.FromSnapshot(snapshot);
        }
        catch (Exception ex)
        {
            _log.Warn("视觉感知", $"视觉快照读取失败：{ex.Message}", window.Id);
            return VisionCombatResultMapper.Unavailable();
        }
    }

    public bool TryParseHotkey(string hotkey, out string error) =>
        _input.TryParseHotkey(hotkey, out error);

    public bool TryConsumeGlobalCooldown(int cooldownMs) =>
        _input.TryConsumeGlobalCooldown(cooldownMs);

    public bool TryConsumeGlobalCooldown(BoundGameWindow window, int cooldownMs) =>
        _input.TryConsumeGlobalCooldown(window.Hwnd, cooldownMs);

    public bool CastHotkey(BoundGameWindow window, string hotkey, GlobalConfig config)
    {
        ApplyInputSettings(window, config);
        var before = _input.GetYieldStatus(window.Hwnd, config.InputDispatchMode);
        var ok = _input.SendKey(window.Hwnd, hotkey, config.InputDispatchMode);
        LogInputDiagnostic(window, hotkey, config.InputDispatchMode, before, ok);
        return ok;
    }

    public bool ClickClient(BoundGameWindow window, int x, int y, GlobalConfig config)
    {
        ApplyInputSettings(window, config);
        return _input.Click(window.Hwnd, x, y, config.InputDispatchMode);
    }

    private static bool ShouldReadOpenText(GlobalConfig config)
        => TargetPolicyRuntime.RequiresTargetName(config.Targeting);

    private void ApplyInputSettings(BoundGameWindow window, GlobalConfig config)
    {
        _input.ConfigureWindowInput(
            window.Hwnd,
            config.InputDispatchMode,
            config.YieldToManualInput,
            config.ManualInputYieldMs);
    }

    private void LogInputDiagnostic(
        BoundGameWindow window,
        string hotkey,
        InputDispatchMode mode,
        InputYieldStatus yield,
        bool ok)
    {
        var now = Environment.TickCount64;
        var key = $"{window.Hwnd}:{hotkey}:{mode}:{ok}:{yield.ShouldYield}";
        var repeatWindowMs = ok ? 5000 : 1500;
        if (_lastInputDiagnosticTicks.TryGetValue(key, out var last) && now - last < repeatWindowMs)
            return;

        _lastInputDiagnosticTicks[key] = now;
        var delivery = mode != InputDispatchMode.SendInput && ok
            ? "Windows消息已投递（未确认游戏执行）"
            : ok ? "真实输入已发送" : "发送失败";
        var message =
            $"热键={hotkey}，通道={InputModeText(mode)}，结果={delivery}，" +
            $"目标前台={BoolText(yield.TargetForeground)}，鼠标在客户区={BoolText(yield.CursorInsideClient)}，" +
            $"人工让控={(yield.ShouldYield ? "触发" : "未触发")}，原因={yield.Reason}";

        if (ok)
            _log.Info("输入诊断", message, window.Id);
        else
            _log.Log(new LogEntry
            {
                Level = LogLevel.Warn,
                Module = "输入诊断",
                Message = message,
                Window = window.Id,
                Audience = LogAudience.User,
                UserSummary = ResolveInputFailureSummary(mode, yield)
            });
    }

    private static string ResolveInputFailureSummary(InputDispatchMode mode, InputYieldStatus yield)
    {
        if (yield.ShouldYield)
            return "检测到玩家正在操作，自动按键已短暂停止，稍后会自行继续。";
        if (mode == InputDispatchMode.SendInput && !yield.TargetForeground)
            return "游戏窗口不在前台，本次前台按键未发送。";
        return "游戏窗口未接受本次按键，程序将在下一轮继续尝试。";
    }

    private void LogPerceptionDiagnostic(BoundGameWindow window, RuntimePerceptionSnapshot snapshot)
    {
        var now = Environment.TickCount64;
        if (_lastPerceptionDiagnosticTicks.TryGetValue(window.Id, out var last) && now - last < 1000)
            return;

        _lastPerceptionDiagnosticTicks[window.Id] = now;
        var panel = snapshot.Layout.Best(LayoutClass.TargetPanel);
        var hp = snapshot.TargetHpSlot;
        _log.Trace(
            "视觉感知",
            $"frame={snapshot.FrameSequence} age={snapshot.FrameAgeMilliseconds:0.0}ms " +
            $"capture={snapshot.CaptureMilliseconds:0.0}ms L1={snapshot.Layout.InferenceMilliseconds:0.0}ms " +
            $"panel={panel?.Confidence:0.000} HP={hp?.InferenceMilliseconds:0.0}ms " +
            $"group={hp?.HpGroupConfidence:0.000} fill={hp?.FillConfidence:0.000} " +
            $"life={snapshot.Target.Life} identity={snapshot.Target.Identity}",
            window.Id);
    }

    private static string InputModeText(InputDispatchMode mode) => mode switch
    {
        InputDispatchMode.SendInput => "前台输入",
        InputDispatchMode.Adaptive => "自动兼容",
        _ => "后台输入"
    };

    private static string BoolText(bool value) => value ? "是" : "否";
}

public static class VisionCombatResultMapper
{
    public static CombatRecognitionResult FromSnapshot(RuntimePerceptionSnapshot snapshot)
    {
        var target = snapshot.Target;
        var hpPercent = target.Life switch
        {
            TargetLifeState.DeadByHpBottom => 0,
            TargetLifeState.Live or TargetLifeState.Weak => target.HpPercent,
            _ => null
        };
        var knownKind = target.Identity switch
        {
            TargetIdentity.Monster => "monster",
            TargetIdentity.Boss => "boss",
            TargetIdentity.Elite => "elite",
            TargetIdentity.Player => "player",
            TargetIdentity.Pet => "pet",
            TargetIdentity.Npc => "npc",
            _ => string.Empty
        };
        bool? attackable = target.Presence != TargetPresence.Present
            ? null
            : target.IsProtectedTarget
                ? false
                : target.IsActionableTarget
                    ? true
                    : null;

        return new CombatRecognitionResult(
            target.NameText,
            PercentText(hpPercent),
            PercentText(snapshot.SelfBars.PlayerHpPercent),
            PercentText(snapshot.SelfBars.PlayerMpPercent),
            PercentText(snapshot.SelfBars.PetHpPercent),
            hpPercent,
            snapshot.SelfBars.PlayerHpPercent,
            snapshot.SelfBars.PlayerMpPercent,
            snapshot.SelfBars.PetHpPercent)
        {
            TargetLooksLikePlayer = target.Identity == TargetIdentity.Player,
            TargetLooksLikePet = target.Identity == TargetIdentity.Pet,
            TargetIsAttackable = attackable,
            TargetHostilityEvidenceAvailable = target.HostilityEvidenceAvailable,
            TargetIsHostileRedName = target.IsHostileRedName,
            TargetAggressionEvidenceAvailable = target.AggressionEvidenceAvailable,
            TargetRecentlyDamagedPlayer = target.RecentlyDamagedPlayer,
            TargetSampleKind = knownKind,
            TargetSampleScore = target.Confidence,
            PetDeploymentState = snapshot.SelfBars.PetState,
            NearbyMonsterState = snapshot.Minimap.NearbyMonsterState
        };
    }

    public static CombatRecognitionResult Unavailable() =>
        new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, null, null, null, null);

    private static string PercentText(int? value) =>
        value is null ? string.Empty : value.Value.ToString();
}

public sealed class InternalPluginCombatBackend : ICombatBackend
{
    public CombatBackendKind Kind => CombatBackendKind.InternalPlugin;
    public string DisplayName => "游戏内置挂机";

    public CombatBackendStatus GetStatus(BoundGameWindow window, GlobalConfig config) =>
        new(Kind, DisplayName, true, "游戏内置挂机监控可用；程序只负责状态监控和恢复流程。");

    public CombatRecognitionResult Read(BoundGameWindow window, GlobalConfig config) =>
        VisionCombatResultMapper.Unavailable();

    public bool TryParseHotkey(string hotkey, out string error)
    {
        error = "游戏内置挂机后端不发送技能热键。";
        return false;
    }

    public bool TryConsumeGlobalCooldown(int cooldownMs) => false;
    public bool CastHotkey(BoundGameWindow window, string hotkey, GlobalConfig config) => false;
    public bool ClickClient(BoundGameWindow window, int x, int y, GlobalConfig config) => false;
}

public sealed class CombatBackendSelector : ICombatBackendSelector
{
    private readonly VisionCombatBackend _vision;
    private readonly InternalPluginCombatBackend _gameBuiltIn;
    private readonly IPerceptionHub _perceptionHub;
    private readonly ILogService _log;
    private string _lastDecisionKey = string.Empty;

    public CombatBackendSelector(
        VisionCombatBackend vision,
        InternalPluginCombatBackend gameBuiltIn,
        IPerceptionHub perceptionHub,
        ILogService log)
    {
        _vision = vision;
        _gameBuiltIn = gameBuiltIn;
        _perceptionHub = perceptionHub;
        _log = log;
    }

    public ICombatBackend Resolve(BoundGameWindow window, GlobalConfig config, out CombatBackendStatus status)
    {
        if (config.CombatBackendPreference == CombatBackendPreference.InternalPlugin)
        {
            status = _gameBuiltIn.GetStatus(window, config);
            return _gameBuiltIn;
        }

        if (config.CombatBackendPreference == CombatBackendPreference.Auto)
            return ResolveAuto(window, config, out status);

        status = _vision.GetStatus(window, config);
        return _vision;
    }

    private ICombatBackend ResolveAuto(BoundGameWindow window, GlobalConfig config, out CombatBackendStatus status)
    {
        try
        {
            var scene = _perceptionHub.Read(window, config).Minimap;
            if (scene.IsDungeon == true)
            {
                status = _vision.GetStatus(window, config);
                LogDecisionOnce(window, "自动判断：副本场景使用视觉按键打怪。");
                return _vision;
            }

            if (scene.IsDungeon == false)
            {
                status = _gameBuiltIn.GetStatus(window, config);
                LogDecisionOnce(window, "自动判断：野外场景使用游戏内置挂机监控。");
                return _gameBuiltIn;
            }
        }
        catch (Exception ex)
        {
            LogDecisionOnce(window, $"场景模型读取失败：{ex.Message}", warning: true);
        }

        status = _vision.GetStatus(window, config);
        LogDecisionOnce(window, "场景模型尚未接入，自动模式暂按视觉按键打怪执行。", warning: true);
        return _vision;
    }

    private void LogDecisionOnce(BoundGameWindow window, string message, bool warning = false)
    {
        var key = $"{window.Id}:{message}";
        if (string.Equals(_lastDecisionKey, key, StringComparison.Ordinal))
            return;

        _lastDecisionKey = key;
        if (warning)
            _log.Warn("战斗后端", message, window.Id);
        else
            _log.Info("战斗后端", message, window.Id);
    }
}

/// <summary>
/// Legacy-only adapter kept so old diagnostics still compile. Production DI never registers it.
/// </summary>
[Obsolete("Legacy OCR backend is disabled. Use VisionCombatBackend.")]
public sealed class OcrCombatBackend : ICombatBackend
{
    private readonly ICombatRecognitionService _recognition;
    private readonly IInputService _input;

    public OcrCombatBackend(ICombatRecognitionService recognition, IInputService input)
    {
        _recognition = recognition;
        _input = input;
    }

    public OcrCombatBackend(
        ICombatRecognitionService recognition,
        IInputService input,
        IRuntimeSnapshotService? runtimeSnapshots)
        : this(recognition, input)
    {
    }

    public CombatBackendKind Kind => CombatBackendKind.ExternalOcr;
    public string DisplayName => "已停用的旧识别适配器";

    public CombatBackendStatus GetStatus(BoundGameWindow window, GlobalConfig config) =>
        new(Kind, DisplayName, false, "旧 OCR 后端已停用，仅保留诊断编译兼容。" );

    public CombatRecognitionResult Read(BoundGameWindow window, GlobalConfig config) =>
        _recognition.Recognize(window, config.CombatRecognition);

    public bool TryParseHotkey(string hotkey, out string error) => _input.TryParseHotkey(hotkey, out error);
    public bool TryConsumeGlobalCooldown(int cooldownMs) => _input.TryConsumeGlobalCooldown(cooldownMs);
    public bool TryConsumeGlobalCooldown(BoundGameWindow window, int cooldownMs) =>
        _input.TryConsumeGlobalCooldown(window.Hwnd, cooldownMs);
    public bool CastHotkey(BoundGameWindow window, string hotkey, GlobalConfig config) => false;
    public bool ClickClient(BoundGameWindow window, int x, int y, GlobalConfig config) => false;
}
