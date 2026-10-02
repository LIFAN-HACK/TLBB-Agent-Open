using System.Text.Json.Nodes;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class MainlineReadinessService : IMainlineReadinessService
{
    private readonly IInputService _input;
    private readonly IReturnPointReadinessService _returnPointReadiness;

    public MainlineReadinessService(
        IInputService input,
        IReturnPointReadinessService returnPointReadiness)
    {
        _input = input;
        _returnPointReadiness = returnPointReadiness;
    }

    public MainlineReadinessReport Inspect(
        GlobalConfig config,
        BoundGameWindow? window,
        bool includeReturnPointReadiness = true,
        bool includeLiveReturnRecognition = false)
    {
        var items = new List<MainlineReadinessItem>();

        if (window is null)
        {
            Add(items, MainlineReadinessSeverity.Info, "窗口绑定", "尚未绑定窗口。", "左侧窗口绑定", "拖拽准星到游戏窗口，或使用自动查找。");
        }
        else if (!window.IsAutomationAllowed)
        {
            Add(items, MainlineReadinessSeverity.Error, "窗口绑定", window.AutomationBlockReason, "左侧窗口绑定", "重新绑定可识别的游戏窗口。");
        }
        else
        {
            Add(items, MainlineReadinessSeverity.Info, "窗口绑定", $"已绑定游戏窗口，尺寸 {window.ClientSizeText}。");
            if (window.IsResolutionCompatibilityMode)
                Add(items, MainlineReadinessSeverity.Warning, "识别区域", $"{window.ResolutionWarningReason} 当前会使用现有识别画像，识别异常时请重新校准。", "数据调试 / 战斗 OCR 校准", "导出当前识别区域截图，校准目标血条、人物血蓝、宠物血条和门派标识区域。");
        }

        RequireHotkey(items, "选怪/切怪", config.Targeting.AcquireHotkey, "没有目标或目标不可攻击时无法切换目标。");
        if (!string.IsNullOrWhiteSpace(config.Targeting.AcquireHotkey)
            && string.Equals(config.Targeting.AcquireHotkey.Trim(), "Tab", StringComparison.OrdinalIgnoreCase))
        {
            Add(items, MainlineReadinessSeverity.Warning, "选怪/切怪",
                "当前选怪键是单独 Tab：游戏里常会打开地图，无法正确切怪。请改成游戏真实选怪键（多为 Ctrl+Tab），或在游戏内改键后在通用配置里重新录入。",
                "通用配置 / 快捷键状态",
                "点击「选怪/切怪」录入框，按下你在游戏里实际用的选怪组合键（例如 Ctrl+Tab），不要用单独 Tab。");
        }

        RequireHotkey(items, "普通攻击", config.AttackHotkey, "确认新目标后无法触发第一次普通攻击。");

        if (config.Targeting.BlockPlayerTargets)
        {
            Add(items, MainlineReadinessSeverity.Info, "玩家保护", "已开启，疑似玩家/宠物/不可攻击目标会跳过。");
            WarnHotkey(items, "保护目标脱困", config.Targeting.ProtectedTargetClearHotkey, "卡在玩家/宠物目标上时无法先清除目标再重新选怪。", "挂机 / 目标策略", "录入清除目标热键，建议用游戏取消目标键。");
            CheckTargetProtectionSamples(items, config);
        }
        else
        {
            Add(items, MainlineReadinessSeverity.Warning, "玩家保护", "当前关闭，可能误打玩家目标。", "挂机 / 目标策略", "开启玩家目标保护。");
        }

        if (config.HealthRecoveryMode == PlayerHealthRecoveryMode.Medicine)
            WarnHotkey(items, "药品回血", config.HealHotkey, "人物血量低于阈值时不会使用回血药。", "通用配置 / 恢复配置", "录入回血药快捷键；药品公共 CD 按 10 秒处理。");
        else
            CheckPetSkill(items, "共生回血", config.SymbiosisHealActivationMode, config.SymbiosisHealHotkey, config.SymbiosisHealClickX, config.SymbiosisHealClickBottomOffset, window, "血量低时不会触发共生。");

        if (config.ManaRecoveryMode == PlayerManaRecoveryMode.Medicine)
            WarnHotkey(items, "药品回蓝", config.ManaHotkey, "人物蓝量低于阈值时不会使用回蓝药。", "通用配置 / 恢复配置", "录入回蓝药快捷键；药品公共 CD 按 10 秒处理。");
        else
            CheckPetSkill(items, "血迹回蓝", config.BloodManaActivationMode, EffectiveBloodManaHotkey(config), config.BloodManaClickX, config.BloodManaClickBottomOffset, window, "蓝量低时不会触发血迹。");

        CheckPetDeployAction(items, config, window);
        WarnHotkey(items, "宠物恢复", EffectivePetFoodHotkey(config), "宠物血量低于阈值时不会使用口粮。", "通用配置 / 恢复配置", "录入口粮快捷键；低于阈值后会按宠物血量监控快速恢复至满血。");

        if (ResolvePillCooldownMs(config) != 10000)
            Add(items, MainlineReadinessSeverity.Info, "药品 CD", "运行时会按 TLBB 药品公共 CD 10 秒处理。");
        if (Math.Max(30000, config.BloodManaCooldownMs) != config.BloodManaCooldownMs)
            Add(items, MainlineReadinessSeverity.Info, "血迹 CD", "运行时会按宠物技能独立 CD 30 秒处理。");
        if (Math.Max(30000, config.SymbiosisHealCooldownMs) != config.SymbiosisHealCooldownMs)
            Add(items, MainlineReadinessSeverity.Info, "共生 CD", "运行时会按宠物技能独立 CD 30 秒处理。");

        CheckEnabledSkills(items, config);
        CheckBuiltInHangSwitch(items, config, window);
        if (includeReturnPointReadiness)
            CheckReturnPointReadiness(items, includeLiveReturnRecognition);

        return new MainlineReadinessReport(
            !items.Any(i => i.Severity == MainlineReadinessSeverity.Error),
            items);
    }

    private void CheckEnabledSkills(List<MainlineReadinessItem> items, GlobalConfig config)
    {
        foreach (var skill in config.Skills.Where(s => s.Enabled))
        {
            if (string.IsNullOrWhiteSpace(skill.Hotkey))
            {
                Add(items, MainlineReadinessSeverity.Warning, "技能预设", $"{skill.Name} 已启用但未录入快捷键。", "通用配置 / 技能预设", "录入该技能热键，或关闭该技能。");
                continue;
            }

            if (!_input.TryParseHotkey(skill.Hotkey, out var error))
                Add(items, MainlineReadinessSeverity.Warning, "技能预设", $"{skill.Name} 快捷键无效：{skill.Hotkey} | {error}", "通用配置 / 技能预设", "重新录入有效按键。");
        }
    }

    private void CheckBuiltInHangSwitch(List<MainlineReadinessItem> items, GlobalConfig config, BoundGameWindow? window)
    {
        if (config.GameBuiltInHangSwitchMode == GameBuiltInHangSwitchMode.Disabled)
            return;

        if (config.GameBuiltInHangSwitchMode == GameBuiltInHangSwitchMode.Hotkey)
        {
            WarnHotkey(items, "游戏内置挂机", config.GameBuiltInHangSwitchHotkey, "当前选择热键触发，但未配置热键。", "通用配置 / 门派战斗", "录入游戏内置挂机开关热键，或改用点击模式校准小地图按钮。");
            return;
        }

        if (config.GameBuiltInHangSwitchClickX <= 0 || config.GameBuiltInHangSwitchClickY <= 0)
        {
            Add(items, MainlineReadinessSeverity.Warning, "游戏内置挂机", "当前选择点击触发，但未校准小地图挂机开关坐标。", "通用配置 / 门派战斗", "校准小地图右侧挂机开关点击点。");
            return;
        }

        if (window is not null && !IsClientPointInside(window, config.GameBuiltInHangSwitchClickX, config.GameBuiltInHangSwitchClickY))
            Add(items, MainlineReadinessSeverity.Warning, "游戏内置挂机", $"点击点 {config.GameBuiltInHangSwitchClickX},{config.GameBuiltInHangSwitchClickY} 超出当前窗口 {window.ClientSizeText}。", "通用配置 / 门派战斗", "按当前分辨率重新校准点击点。");
    }

    private void CheckReturnPointReadiness(List<MainlineReadinessItem> items, bool includeLiveRecognition)
    {
        try
        {
            var report = _returnPointReadiness.Inspect(includeLiveRecognition);
            if (report.Level == ReturnPointReadinessLevel.Ready)
            {
                Add(items, MainlineReadinessSeverity.Info, "回点门禁", "自动回点、死亡回点或卡点恢复门禁通过。");
                return;
            }

            var enabledBlocked = report.Slots.FirstOrDefault(s => s.Enabled && s.Level == ReturnPointReadinessLevel.Blocked);
            var message = enabledBlocked is null
                ? report.Summary
                : enabledBlocked.Summary;
            if (!string.IsNullOrWhiteSpace(report.NextAction))
                message = $"{message} {report.NextAction}";

            Add(items, MainlineReadinessSeverity.Warning, "回点门禁", message, "挂机 / 返点", "采集地图和坐标，确认回点动作热键或录制路径。");
        }
        catch (Exception ex)
        {
            Add(items, MainlineReadinessSeverity.Warning, "回点门禁", $"检查异常：{ex.Message}", "挂机 / 返点", "打开回点诊断重新生成证据。");
        }
    }

    private static void CheckTargetProtectionSamples(List<MainlineReadinessItem> items, GlobalConfig config)
    {
        if (!config.Targeting.TargetProtectionSampleMatchingEnabled)
        {
            Add(items, MainlineReadinessSeverity.Warning, "目标样本", "目标保护样本匹配已关闭；只能依赖基础图色保护。", "数据调试 / 目标采集", "开启样本匹配，并采集玩家、怪物、BOSS、宠物、无目标样本。");
            return;
        }

        try
        {
            var indexPath = ResolveTargetProtectionIndexPath(config.Targeting.TargetProtectionSampleIndexPath);
            if (!File.Exists(indexPath))
            {
                Add(items, MainlineReadinessSeverity.Warning, "目标样本", "未找到目标保护样本索引；请先用目标采集器采集玩家、怪物、BOSS、宠物和无目标样本。", "数据调试 / 目标采集", "启动目标采集器，绑定窗口后逐类采集。");
                return;
            }

            var root = JsonNode.Parse(File.ReadAllText(indexPath));
            var samplesRoot = ReadString(root, "samplesRoot");
            var samples = ReadSampleCounts(root?["samples"]?.AsArray());
            var metadataCounts = ReadMetadataSampleCounts(samplesRoot);
            var player = Math.Max(samples.Player, metadataCounts.Player);
            var monster = Math.Max(samples.Monster, metadataCounts.Monster);
            var boss = Math.Max(samples.Boss, metadataCounts.Boss);
            var pet = Math.Max(samples.Pet, metadataCounts.Pet);
            var none = metadataCounts.None;

            var missing = new List<string>();
            if (player < 3)
                missing.Add($"玩家 {player}/3");
            if (monster < 3)
                missing.Add($"普通怪 {monster}/3");
            if (boss < 2)
                missing.Add($"BOSS {boss}/2");
            if (pet < 1)
                missing.Add("宠物 0/1");
            if (none < 1)
                missing.Add("无目标 0/1");

            if (missing.Count > 0)
            {
                Add(items, MainlineReadinessSeverity.Warning, "目标样本", $"保护样本未达推荐覆盖：{string.Join("，", missing)}。基础打怪可继续，但玩家/宠物/BOSS识别仍需补样本。", "数据调试 / 目标采集", "优先补宠物、无目标、玩家空蓝/空怒气、BOSS/首领样本。");
                return;
            }

            Add(items, MainlineReadinessSeverity.Info, "目标样本", "玩家、怪物、BOSS、宠物和无目标样本达到推荐覆盖。");
        }
        catch (Exception ex)
        {
            Add(items, MainlineReadinessSeverity.Warning, "目标样本", $"覆盖检查失败：{ex.Message}", "数据调试 / 目标采集", "重建目标保护样本索引。");
        }
    }

    private static string ResolveTargetProtectionIndexPath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Environment.ExpandEnvironmentVariables(configuredPath.Trim());

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LongBanXing",
            "samples",
            "target-protection-index.json");
    }

    private static TargetSampleCounts ReadSampleCounts(JsonArray? samples)
    {
        var counts = new TargetSampleCounts();
        if (samples is null)
            return counts;

        foreach (var sample in samples)
            counts.Add(ReadString(sample, "kind"));

        return counts;
    }

    private static TargetSampleCounts ReadMetadataSampleCounts(string samplesRoot)
    {
        var counts = new TargetSampleCounts();
        if (string.IsNullOrWhiteSpace(samplesRoot) || !Directory.Exists(samplesRoot))
            return counts;

        foreach (var sampleDir in Directory.EnumerateDirectories(samplesRoot))
        {
            var metadataPath = Path.Combine(sampleDir, "metadata.json");
            if (!File.Exists(metadataPath))
                continue;

            try
            {
                var root = JsonNode.Parse(File.ReadAllText(metadataPath));
                counts.Add(ReadString(root, "ExpectedKind", "expectedKind", "kind"));
            }
            catch
            {
                // A broken sample should not break startup readiness; diagnostics will report it separately.
            }
        }

        return counts;
    }

    private static string ReadString(JsonNode? node, params string[] names)
    {
        foreach (var name in names)
        {
            var value = node?[name]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private void CheckPetSkill(
        List<MainlineReadinessItem> items,
        string feature,
        PetSkillActivationMode mode,
        string hotkey,
        int clickX,
        int bottomOffset,
        BoundGameWindow? window,
        string missingMessage)
    {
        if (mode == PetSkillActivationMode.Hotkey)
        {
            WarnHotkey(items, feature, hotkey, $"{feature} 使用热键模式，{missingMessage}", "通用配置 / 快捷键状态", $"录入{feature}热键，或改用客户区点击模式。");
            return;
        }

        if (clickX <= 0 || bottomOffset <= 0)
        {
            Add(items, MainlineReadinessSeverity.Warning, feature, "使用宠物技能点击模式，但点击点未校准。", "通用配置 / 快捷键状态", $"校准{feature}宠物技能点击点。");
            return;
        }

        if (window is not null)
        {
            var y = window.ClientHeight - bottomOffset;
            if (!IsClientPointInside(window, clickX, y))
            {
                Add(items, MainlineReadinessSeverity.Warning, feature, $"宠物技能点击点 x={clickX}, bottom={bottomOffset} 超出当前窗口 {window.ClientSizeText}。", "通用配置 / 快捷键状态", "按当前窗口重新校准点击点。");
                return;
            }
        }

        Add(items, MainlineReadinessSeverity.Info, feature, $"使用宠物技能区点击：x={clickX}, bottom={bottomOffset}，不依赖技能栏热键。");
    }

    private void CheckPetDeployAction(List<MainlineReadinessItem> items, GlobalConfig config, BoundGameWindow? window)
    {
        if (config.PetDeployActivationMode == PetSkillActivationMode.Hotkey)
        {
            WarnHotkey(items, "宠物出战", config.PetDeployHotkey, "连续多轮未识别宠物血条时无法自动出战当前宠物。", "通用配置 / 快捷键状态", "录入宠物出战热键，或改用客户区点击模式。");
            return;
        }

        if (config.PetDeployClickX <= 0 || config.PetDeployClickBottomOffset <= 0)
        {
            Add(items, MainlineReadinessSeverity.Warning, "宠物出战", "使用客户区点击模式，但点击点未校准。", "通用配置 / 快捷键状态", "校准宠物出战点击点。");
            return;
        }

        if (window is not null)
        {
            var y = window.ClientHeight - config.PetDeployClickBottomOffset;
            if (!IsClientPointInside(window, config.PetDeployClickX, y))
            {
                Add(items, MainlineReadinessSeverity.Warning, "宠物出战", $"点击点 x={config.PetDeployClickX}, bottom={config.PetDeployClickBottomOffset} 超出当前窗口 {window.ClientSizeText}。", "通用配置 / 快捷键状态", "按当前窗口重新校准宠物出战点击点。");
                return;
            }

            var minActionBandY = (int)Math.Round(window.ClientHeight * 0.55);
            if (y < minActionBandY)
            {
                Add(items, MainlineReadinessSeverity.Warning, "宠物出战", $"点击点不在底部动作区：y={y} < {minActionBandY}。", "通用配置 / 快捷键状态", "重新选择底部动作栏或宠物按钮区域。");
                return;
            }
        }

        Add(items, MainlineReadinessSeverity.Info, "宠物出战", $"使用客户区点击：x={config.PetDeployClickX}, bottom={config.PetDeployClickBottomOffset}。");
    }

    private void RequireHotkey(List<MainlineReadinessItem> items, string feature, string hotkey, string missingMessage)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            Add(items, MainlineReadinessSeverity.Error, feature, $"未配置。{missingMessage}", "通用配置 / 快捷键状态", $"录入{feature}快捷键。");
            return;
        }

        if (!_input.TryParseHotkey(hotkey, out var error))
            Add(items, MainlineReadinessSeverity.Error, feature, $"快捷键无效：{hotkey} | {error}", "通用配置 / 快捷键状态", $"重新录入{feature}快捷键。");
    }

    private void WarnHotkey(List<MainlineReadinessItem> items, string feature, string hotkey, string missingMessage, string actionArea = "", string action = "")
    {
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            Add(items, MainlineReadinessSeverity.Warning, feature, $"未配置。{missingMessage}", actionArea, action);
            return;
        }

        if (!_input.TryParseHotkey(hotkey, out var error))
            Add(items, MainlineReadinessSeverity.Warning, feature, $"快捷键无效：{hotkey} | {error}", actionArea, action);
    }

    private static bool IsClientPointInside(BoundGameWindow window, int x, int y) =>
        x >= 0 && y >= 0 && x < window.ClientWidth && y < window.ClientHeight;

    private static void Add(
        List<MainlineReadinessItem> items,
        MainlineReadinessSeverity severity,
        string feature,
        string message,
        string actionArea = "",
        string action = "") =>
        items.Add(new MainlineReadinessItem(severity, feature, message)
        {
            ActionArea = actionArea,
            Action = action
        });

    private static string EffectiveBloodManaHotkey(GlobalConfig config) =>
        !string.IsNullOrWhiteSpace(config.BloodManaHotkey) ? config.BloodManaHotkey : config.PetManaHotkey;

    private static string EffectivePetFoodHotkey(GlobalConfig config) =>
        !string.IsNullOrWhiteSpace(config.PetFoodHotkey) ? config.PetFoodHotkey : config.PetHealHotkey;

    private static int ResolvePillCooldownMs(GlobalConfig config) =>
        Math.Max(10000, Math.Max(config.PillHealCooldownMs, config.PillManaCooldownMs));

    private sealed class TargetSampleCounts
    {
        public int Player { get; private set; }
        public int Monster { get; private set; }
        public int Boss { get; private set; }
        public int Pet { get; private set; }
        public int None { get; private set; }

        public void Add(string kind)
        {
            switch (kind.Trim().ToLowerInvariant())
            {
                case "player":
                    Player++;
                    break;
                case "monster":
                    Monster++;
                    break;
                case "boss":
                case "elite":
                    Boss++;
                    break;
                case "pet":
                    Pet++;
                    break;
                case "none":
                    None++;
                    break;
            }
        }
    }
}
