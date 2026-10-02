using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TlbbAgent.Contracts;

namespace TlbbAgent.App;

internal static class Brushes
{
    public static SolidColorBrush From(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}

public sealed class IntegrationStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        ModuleIntegrationStatus.Integrated => Brushes.From("#34D399"),
        ModuleIntegrationStatus.Running => Brushes.From("#60A5FA"),
        ModuleIntegrationStatus.Partial => Brushes.From("#FBBF24"),
        ModuleIntegrationStatus.Error => Brushes.From("#F87171"),
        _ => Brushes.From("#69707C"),
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class IntegrationStatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        ModuleIntegrationStatus.Integrated => "已接入",
        ModuleIntegrationStatus.Running => "运行中",
        ModuleIntegrationStatus.Partial => "部分接入",
        ModuleIntegrationStatus.Error => "错误",
        _ => "未接入",
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class RunStateToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        RunState.Idle => "未启动",
        RunState.Ready => "待启动",
        RunState.Running => "运行中",
        RunState.Paused => "暂停",
        RunState.Recovering => "异常恢复",
        RunState.DeathReturn => "死亡返点",
        RunState.Reconnecting => "重连中",
        RunState.Switching => "切换中",
        RunState.Stopped => "已停止",
        RunState.Error => "错误",
        _ => "-",
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class RunStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        RunState.Running => Brushes.From("#34D399"),
        RunState.Paused => Brushes.From("#FBBF24"),
        RunState.Error => Brushes.From("#F87171"),
        RunState.Reconnecting or RunState.Recovering or RunState.DeathReturn or RunState.Switching => Brushes.From("#FBBF24"),
        RunState.Ready => Brushes.From("#60A5FA"),
        _ => Brushes.From("#69707C"),
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        LogLevel.Error => Brushes.From("#F87171"),
        LogLevel.Warn => Brushes.From("#FBBF24"),
        LogLevel.Info => Brushes.From("#60A5FA"),
        _ => Brushes.From("#69707C"),
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class BitnessToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        ClientBitness.X86 => "32 位",
        ClientBitness.X64 => "64 位",
        _ => "未知",
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var has = value is not null;
        if (Invert) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var b = value is true;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is not true;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is not true;
}

public sealed class SkillConditionToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        SkillCondition.Always => "始终",
        SkillCondition.TargetExists => "有目标",
        SkillCondition.HpLow => "血量低",
        SkillCondition.MpLow => "蓝量低",
        SkillCondition.EmHeal => "峨眉加血",
        _ => value?.ToString() ?? ""
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class SkillKindToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        SkillKind.Damage => "伤害",
        SkillKind.Aoe => "AOE",
        SkillKind.Buff => "增益Buff",
        _ => value?.ToString() ?? ""
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class TargetFilterToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        TargetFilter.All => "所有目标",
        TargetFilter.MonsterOnly => "只打怪",
        TargetFilter.RedNameOnly => "只打红名/红怪",
        TargetFilter.SpecificName => "指定名称",
        _ => value?.ToString() ?? ""
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class TargetSwitchModeToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        TargetSwitchMode.Grab => "抢怪",
        TargetSwitchMode.KillThenSwitch => "打死换怪",
        _ => value?.ToString() ?? ""
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class DeathSwitchStrategyToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        DeathSwitchStrategy.Conservative => "保守",
        DeathSwitchStrategy.Standard => "标准",
        DeathSwitchStrategy.Predictive => "预测",
        _ => value?.ToString() ?? ""
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class InputDispatchModeToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        InputDispatchMode.Adaptive => "自动兼容",
        InputDispatchMode.PostMessage => "仅后台",
        InputDispatchMode.SendInput => "仅前台",
        _ => value?.ToString() ?? ""
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class CombatBackendPreferenceToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        CombatBackendPreference.Auto => "自动判断",
        CombatBackendPreference.InternalPlugin => "游戏内置",
        CombatBackendPreference.ExternalOcr => "按键打怪",
        _ => value?.ToString() ?? ""
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class GameBuiltInHangSwitchModeToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        GameBuiltInHangSwitchMode.Disabled => "不触发",
        GameBuiltInHangSwitchMode.Hotkey => "快捷键",
        GameBuiltInHangSwitchMode.ClientClick => "点击坐标",
        _ => value?.ToString() ?? ""
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class SmallNpcGrabStrategyToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        SmallNpcGrabStrategy.AutoRecognizeOnly => "主动识别",
        SmallNpcGrabStrategy.ContextFastOnly => "上下文快抢",
        SmallNpcGrabStrategy.DualChannelPriority => "双通道优先",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class AutoLoginStepKindToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        AutoLoginStepKind.Wait => "等待",
        AutoLoginStepKind.Hotkey => "热键",
        AutoLoginStepKind.Click => "点击",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class LifeCollectModeToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        LifeCollectMode.Mining => "挖矿",
        LifeCollectMode.Herb => "采药",
        LifeCollectMode.Gathering => "采集",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class LifeCollectActionModeToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        LifeCollectActionMode.HotkeyOnly => "只按采集键",
        LifeCollectActionMode.ClickClientPoint => "只点击客户区",
        LifeCollectActionMode.HotkeyThenClickClientPoint => "按键后点击",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class ReturnNavigationModeToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        ReturnNavigationMode.Disabled => "不导航",
        ReturnNavigationMode.ClickClientPoint => "点击客户区",
        ReturnNavigationMode.HotkeyOnly => "只按热键",
        ReturnNavigationMode.HotkeyThenClickClientPoint => "按键后点击",
        ReturnNavigationMode.DirectionalClientClick => "方向点击",
        ReturnNavigationMode.HotkeyThenDirectionalClientClick => "按键后方向点击",
        ReturnNavigationMode.RecordedClientClickSequence => "录制序列",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class ReturnNavigationSlotToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        ReturnNavigationSlot.FixedFarm => "挂机回点",
        ReturnNavigationSlot.EarthSpirit => "土灵回点",
        ReturnNavigationSlot.StuckRecovery => "卡点恢复",
        ReturnNavigationSlot.DeathReturn => "死亡回点",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class TradeRouteActionToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        TradeRouteAction.Navigate => "导航",
        TradeRouteAction.Buy => "买入",
        TradeRouteAction.Sell => "卖出",
        TradeRouteAction.TalkNpc => "对话",
        TradeRouteAction.Hotkey => "热键",
        TradeRouteAction.Click => "点击",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class TradeExecutionModeToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        TradeExecutionMode.HotkeyOnly => "只按热键",
        TradeExecutionMode.ClickOnly => "只点击",
        TradeExecutionMode.HotkeyThenClick => "热键后点击",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class MaterialCraftStepActionToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        MaterialCraftStepAction.Hotkey => "热键",
        MaterialCraftStepAction.Click => "点击",
        MaterialCraftStepAction.HotkeyThenClick => "热键后点击",
        MaterialCraftStepAction.Text => "文本",
        MaterialCraftStepAction.Wait => "等待",
        MaterialCraftStepAction.OpenPanel => "打开",
        MaterialCraftStepAction.SelectRecipe => "选配方",
        MaterialCraftStepAction.Process => "加工",
        MaterialCraftStepAction.Confirm => "确认",
        MaterialCraftStepAction.CleanupBag => "清包",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class SchoolTaskStepActionToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        SchoolTaskStepAction.Navigate => "导航",
        SchoolTaskStepAction.TalkNpc => "对话",
        SchoolTaskStepAction.AcceptTask => "接取",
        SchoolTaskStepAction.CompleteTask => "执行",
        SchoolTaskStepAction.SubmitTask => "提交",
        SchoolTaskStepAction.BuyItem => "购买",
        SchoolTaskStepAction.StoreItem => "存放",
        SchoolTaskStepAction.TradeItem => "交易",
        SchoolTaskStepAction.UseHotkey => "热键",
        SchoolTaskStepAction.Click => "点击",
        SchoolTaskStepAction.Text => "文本",
        SchoolTaskStepAction.Wait => "等待",
        SchoolTaskStepAction.CleanupBag => "清包",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class SchoolTaskExecutionModeToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        SchoolTaskExecutionMode.HotkeyOnly => "只按热键",
        SchoolTaskExecutionMode.ClickOnly => "只点击",
        SchoolTaskExecutionMode.HotkeyThenClick => "热键后点击",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class TreasureMapStepActionToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        TreasureMapStepAction.OpenMap => "开图",
        TreasureMapStepAction.Navigate => "导航",
        TreasureMapStepAction.Dig => "挖图",
        TreasureMapStepAction.OpenBox => "开箱",
        TreasureMapStepAction.Pickup => "拾取",
        TreasureMapStepAction.Click => "点击",
        TreasureMapStepAction.UseLocator => "定位符",
        TreasureMapStepAction.WarehouseFetch => "仓库取图",
        TreasureMapStepAction.ShopFetch => "商会取图",
        TreasureMapStepAction.Heal => "回血",
        TreasureMapStepAction.CleanupBag => "清包",
        TreasureMapStepAction.Wait => "等待",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class TreasureMapActionModeToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        TreasureMapActionMode.HotkeyOnly => "只按热键",
        TreasureMapActionMode.ClickOnly => "只点击",
        TreasureMapActionMode.HotkeyThenClick => "热键后点击",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class GuildContributionStepActionToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        GuildContributionStepAction.Navigate => "导航",
        GuildContributionStepAction.TalkNpc => "对话",
        GuildContributionStepAction.AcceptTask => "接取",
        GuildContributionStepAction.ExecuteTask => "执行",
        GuildContributionStepAction.SubmitTask => "提交",
        GuildContributionStepAction.BuyItem => "购买",
        GuildContributionStepAction.DeliverItem => "交付",
        GuildContributionStepAction.UseHotkey => "热键",
        GuildContributionStepAction.Click => "点击",
        GuildContributionStepAction.Text => "文本",
        GuildContributionStepAction.Wait => "等待",
        GuildContributionStepAction.CleanupBag => "清包",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class GuildContributionExecutionModeToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        GuildContributionExecutionMode.HotkeyOnly => "只按热键",
        GuildContributionExecutionMode.ClickOnly => "只点击",
        GuildContributionExecutionMode.HotkeyThenClick => "热键后点击",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class UtilityActionGroupToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        UtilityActionGroup.Cleanup => "清包",
        UtilityActionGroup.PortableBank => "随身银行",
        UtilityActionGroup.PortableShop => "随身商店",
        UtilityActionGroup.Yulong => "御龙帖",
        UtilityActionGroup.Custom => "自定义",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class UtilityActionKindToTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        UtilityActionKind.Wait => "等待",
        UtilityActionKind.Hotkey => "热键",
        UtilityActionKind.Click => "点击",
        UtilityActionKind.HotkeyThenClick => "热键后点击",
        UtilityActionKind.Text => "文本",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class IntegrationStatusToBgConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        ModuleIntegrationStatus.Integrated => Brushes.From("#1F34D399"),
        ModuleIntegrationStatus.Running => Brushes.From("#1F60A5FA"),
        ModuleIntegrationStatus.Partial => Brushes.From("#1FFBBF24"),
        ModuleIntegrationStatus.Error => Brushes.From("#1FF87171"),
        _ => Brushes.From("#22222A35"),
    };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class BoolToStableBgConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true ? Brushes.From("#1F34D399") : Brushes.From("#1FFBBF24");

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class InputYieldActiveToBgConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true ? Brushes.From("#1FFBBF24") : Brushes.From("#22222A35");

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class InputYieldActiveToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true ? Brushes.From("#FBBF24") : Brushes.From("#60A5FA");

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
