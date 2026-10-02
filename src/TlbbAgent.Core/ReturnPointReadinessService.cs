using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class ReturnPointReadinessService : IReturnPointReadinessService
{
    private readonly IWindowBindingService _binding;
    private readonly IReturnPointService _returnPoints;
    private readonly ISceneStateService _sceneState;

    public ReturnPointReadinessService(
        IWindowBindingService binding,
        IReturnPointService returnPoints,
        ISceneStateService sceneState)
    {
        _binding = binding;
        _returnPoints = returnPoints;
        _sceneState = sceneState;
    }

    public ReturnPointReadinessReport Inspect(bool includeLiveRecognition = true)
    {
        var config = _returnPoints.Current;
        var window = _binding.Current;
        NavigationRecognitionResult? scene = null;
        var sceneReasons = new List<string>();

        if (window is null)
        {
            sceneReasons.Add("未绑定游戏窗口。");
        }
        else
        {
            if (!window.IsAutomationAllowed)
                sceneReasons.Add(window.AutomationBlockReason);

            if (includeLiveRecognition)
            {
                try
                {
                    scene = RecognizeStableScene(window, config.NavigationRecognition);
                    if (!scene.HasMap)
                        sceneReasons.Add("当前地图未识别。");
                    else if (IsManualConfirmedMap(scene))
                        sceneReasons.Add("当前地图仅来自手动确认，未由实时识别确认；为避免跨地图误回点，自动回点保持阻断。");
                    else if (IsRouteLockedMap(scene))
                        sceneReasons.Add("当前地图名已只读识别，但视觉策略尚未授权自动路线；自动回点保持阻断。");
                    else if (!HasReliableRuntimeMap(scene))
                        sceneReasons.Add("当前地图未稳定识别。");
                    if (!scene.HasCoordinate)
                        sceneReasons.Add("当前坐标未稳定识别。");
                }
                catch (Exception ex)
                {
                    sceneReasons.Add($"地图/坐标识别异常：{ex.Message}");
                }
            }
        }

        var slots = new[]
        {
            InspectSlot(ReturnNavigationSlot.FixedFarm, "挂机回点", config.EnableFixedFarmReturn, config.ReturnNavigation, config, scene, requiresSameMapPoint: true),
            InspectSlot(ReturnNavigationSlot.EarthSpirit, "土灵回点", config.EnableEarthSpiritReturn, config.EarthSpiritNavigation, config, scene, requiresSameMapPoint: true),
            InspectSlot(ReturnNavigationSlot.StuckRecovery, "卡点恢复", config.EnableStuckRecovery, config.StuckRecoveryNavigation, config, scene, requiresSameMapPoint: true),
            InspectSlot(ReturnNavigationSlot.DeathReturn, "死亡回点", config.EnableDeathReturn, config.DeathReturnNavigation, config, scene, requiresSameMapPoint: false)
        };

        var allReasons = new List<string>(sceneReasons);
        allReasons.AddRange(slots.Where(s => s.Enabled && s.Level == ReturnPointReadinessLevel.Blocked).SelectMany(s => s.Reasons));
        var enabledSlots = slots.Where(s => s.Enabled).ToArray();
        if (enabledSlots.Length == 0)
            allReasons.Add("所有回点能力均未开启。");

        var level = allReasons.Count > 0
            ? ReturnPointReadinessLevel.Blocked
            : enabledSlots.Any(s => s.Level == ReturnPointReadinessLevel.Warning)
                ? ReturnPointReadinessLevel.Warning
                : ReturnPointReadinessLevel.Ready;

        var summary = level switch
        {
            ReturnPointReadinessLevel.Ready => "回点门禁通过：已具备自动回点执行条件。",
            ReturnPointReadinessLevel.Warning => "回点可用但有兼容性提示，请先小范围验证。",
            _ => "回点暂不可执行：" + string.Join("；", allReasons.Distinct(StringComparer.Ordinal).Take(4))
        };
        var nextAction = BuildNextAction(window, config, scene, slots, level);

        var sceneSummary = scene is null
            ? (includeLiveRecognition ? string.Join("；", sceneReasons) : "未执行实时识别。")
            : $"地图={scene.MapNameText}，坐标={FormatCoordinate(scene.X, scene.Y)}，地图来源={scene.MapNameSource}({scene.MapNameConfidencePercent}%)，坐标来源={scene.CoordinateSource}({scene.CoordinateConfidencePercent}%)";

        return new ReturnPointReadinessReport(
            level,
            summary,
            nextAction,
            window is not null,
            window is null ? "-" : $"{window.Title} / {window.ClientSizeText}",
            scene?.MapNameText ?? "",
            scene?.X,
            scene?.Y,
            sceneSummary,
            slots);
    }

    private static bool HasReliableRuntimeMap(NavigationRecognitionResult navigation) =>
        navigation.HasMap
        && !IsManualConfirmedMap(navigation)
        && !IsRouteLockedMap(navigation);

    private NavigationRecognitionResult RecognizeStableScene(BoundGameWindow window, NavigationRecognitionConfig config)
    {
        NavigationRecognitionResult? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            last = _sceneState.Recognize(window, config);
            if (last.HasMap && !IsManualConfirmedMap(last))
                break;
            if (attempt < 2)
                Thread.Sleep(160);
        }

        return last ?? new NavigationRecognitionResult(string.Empty, string.Empty, null, null);
    }

    private static bool IsManualConfirmedMap(NavigationRecognitionResult navigation) =>
        string.Equals(navigation.MapNameSource, "manual-confirmed-map", StringComparison.OrdinalIgnoreCase);

    private static bool IsRouteLockedMap(NavigationRecognitionResult navigation) =>
        navigation.MapNameSource.Contains("route-locked", StringComparison.OrdinalIgnoreCase);

    private static string BuildNextAction(
        BoundGameWindow? window,
        ReturnPointConfig config,
        NavigationRecognitionResult? scene,
        IReadOnlyList<ReturnPointSlotReadiness> slots,
        ReturnPointReadinessLevel level)
    {
        if (level == ReturnPointReadinessLevel.Ready)
            return "下一步：先执行受控预演；确认距离变化或到点后，再允许真实回点。";

        if (window is null)
            return "下一步：先在左侧绑定游戏窗口。";

        if (!window.IsAutomationAllowed)
            return "下一步：重新绑定可识别的游戏窗口。";

        if (scene is null || !scene.HasMap || !scene.HasCoordinate)
            return "下一步：打开“地图 / 坐标识别区”，校准当前分辨率的地图名和坐标区域。";

        if (IsManualConfirmedMap(scene))
            return "下一步：采集或导入当前地图名样本，让地图名由实时识别确认；手动确认地图只允许配置预览，不允许自动回点。";

        if (IsRouteLockedMap(scene))
            return "下一步：当前地图名已只读识别，但视觉策略尚未授权自动路线；继续补齐多城市实机样本后再放开自动回点。";

        if (!HasReliableRuntimeMap(scene))
            return "下一步：打开“地图 / 坐标识别区”，校准当前分辨率的地图名和坐标区域。";

        if (!config.EnableFixedFarmReturn && !config.EnableEarthSpiritReturn && !config.EnableDeathReturn && !config.EnableStuckRecovery)
            return "下一步：至少开启“挂机回点”或“死亡回点”。";

        if (config.Points.Count == 0)
            return "下一步：在当前挂机点点击“采集并添加”，先建立同地图回点坐标。";

        if (config.Points.All(p => !IsSameMap(p.MapName, scene.MapNameText)))
            return $"下一步：当前地图是 {scene.MapNameText}，请在本地图点击“采集并添加”，避免跨地图误回点。";

        var enabledBlocked = slots.FirstOrDefault(s => s.Enabled && s.Level == ReturnPointReadinessLevel.Blocked);
        if (enabledBlocked is null)
            return "下一步：检查启用的回点槽位配置。";

        if (enabledBlocked.Reasons.Any(r => r.Contains("导航动作关闭", StringComparison.Ordinal)))
            return $"下一步：在“{enabledBlocked.SlotName}”里选择导航动作，优先用录制寻路样本。";

        if (enabledBlocked.Reasons.Any(r => r.Contains("未导入录制点击序列", StringComparison.Ordinal)))
            return "下一步：录制一条真实寻路样本，校验 PASS 后导入当前槽位。";

        if (enabledBlocked.Reasons.Any(r => r.Contains("未通过靠近/到达验证", StringComparison.Ordinal)))
            return "下一步：重新录制寻路样本；录制后必须看到靠近目标或到达目标 PASS。";

        if (enabledBlocked.Reasons.Any(r => r.Contains("缺少导航热键", StringComparison.Ordinal)))
            return $"下一步：为“{enabledBlocked.SlotName}”补导航热键，或改用录制寻路样本。";

        if (enabledBlocked.Reasons.Any(r => r.Contains("缺少有效点击坐标", StringComparison.Ordinal)))
            return $"下一步：为“{enabledBlocked.SlotName}”补点击坐标，或改用录制寻路样本。";

        return $"下一步：处理“{enabledBlocked.SlotName}”的阻断项：{enabledBlocked.Reasons.FirstOrDefault() ?? "配置不完整"}";
    }

    private static ReturnPointSlotReadiness InspectSlot(
        ReturnNavigationSlot slot,
        string slotName,
        bool enabled,
        ReturnNavigationConfig navigation,
        ReturnPointConfig config,
        NavigationRecognitionResult? scene,
        bool requiresSameMapPoint)
    {
        var reasons = new List<string>();
        if (!enabled)
            return new ReturnPointSlotReadiness(slot, slotName, ReturnPointReadinessLevel.Warning, false, $"{slotName} 未开启。", reasons);

        if (navigation.Mode == ReturnNavigationMode.Disabled)
            reasons.Add($"{slotName}导航动作关闭。");

        reasons.AddRange(CheckNavigationConfig(slotName, navigation));

        if (requiresSameMapPoint)
        {
            if (config.Points.Count == 0)
            {
                reasons.Add("未配置任何回点坐标。");
            }
            else if (scene is not null && HasReliableRuntimeMap(scene))
            {
                var sameMapCount = config.Points.Count(p => IsSameMap(p.MapName, scene.MapNameText));
                if (sameMapCount == 0)
                    reasons.Add($"当前地图 {scene.MapNameText} 没有同地图回点坐标。");
            }
        }

        var level = reasons.Count == 0 ? ReturnPointReadinessLevel.Ready : ReturnPointReadinessLevel.Blocked;
        var summary = level == ReturnPointReadinessLevel.Ready
            ? $"{slotName} 可执行。"
            : $"{slotName} 阻断：" + string.Join("；", reasons.Take(3));

        return new ReturnPointSlotReadiness(slot, slotName, level, enabled, summary, reasons);
    }

    private static IEnumerable<string> CheckNavigationConfig(string slotName, ReturnNavigationConfig navigation)
    {
        if (RequiresHotkey(navigation.Mode) && string.IsNullOrWhiteSpace(navigation.Hotkey))
            yield return $"{slotName}缺少导航热键。";

        if (RequiresClick(navigation.Mode) && (navigation.ClickX <= 0 || navigation.ClickY <= 0))
            yield return $"{slotName}缺少有效点击坐标。";

        if (navigation.Mode == ReturnNavigationMode.RecordedClientClickSequence)
        {
            if (navigation.RecordedClicks.Count == 0)
                yield return $"{slotName}未导入录制点击序列。";
            if (!navigation.RecordedMovedCloserToTarget && !navigation.RecordedArrivedAtTarget)
                yield return $"{slotName}录制样本未通过靠近/到达验证。";
            if (string.IsNullOrWhiteSpace(navigation.RecordedSourceClientSize))
                yield return $"{slotName}录制样本缺少来源窗口尺寸。";
        }
    }

    private static bool RequiresHotkey(ReturnNavigationMode mode) =>
        mode is ReturnNavigationMode.HotkeyOnly
            or ReturnNavigationMode.HotkeyThenClickClientPoint
            or ReturnNavigationMode.HotkeyThenDirectionalClientClick;

    private static bool RequiresClick(ReturnNavigationMode mode) =>
        mode is ReturnNavigationMode.ClickClientPoint
            or ReturnNavigationMode.HotkeyThenClickClientPoint
            or ReturnNavigationMode.DirectionalClientClick
            or ReturnNavigationMode.HotkeyThenDirectionalClientClick;

    private static bool IsSameMap(string pointMap, string sceneMap) =>
        !string.IsNullOrWhiteSpace(pointMap)
        && !string.IsNullOrWhiteSpace(sceneMap)
        && (pointMap.Contains(sceneMap, StringComparison.Ordinal) || sceneMap.Contains(pointMap, StringComparison.Ordinal));

    private static string FormatCoordinate(int? x, int? y) => x is null || y is null ? "-,-" : $"{x},{y}";
}
