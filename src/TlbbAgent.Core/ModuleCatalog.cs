using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class ModuleCatalog : IModuleCatalog
{
    public IReadOnlyList<ModuleDescriptor> Modules { get; } = new List<ModuleDescriptor>
    {
        new()
        {
            Key = FeatureKeys.HangNormal,
            Group = "挂机",
            Title = "挂机打怪",
            Priority = "P0",
            SourceModule = "内置",
            Description = "内置挂机监控或按键打怪。",
            Status = ModuleIntegrationStatus.Partial
        },
        new()
        {
            Key = "hang.return",
            Group = "挂机",
            Title = "返点服务",
            Priority = "P0",
            SourceModule = "配置",
            Description = "土灵珠与自动寻路状态。",
            Status = ModuleIntegrationStatus.Partial
        },
        new()
        {
            Key = FeatureKeys.HangBoss,
            Group = "挂机",
            Title = "BOSS 模式",
            Priority = "P0",
            SourceModule = "配置",
            Description = "白名单目标与极速战斗。",
            Status = ModuleIntegrationStatus.Partial
        },
        new()
        {
            Key = FeatureKeys.HangDungeon,
            Group = "挂机",
            Title = "副本模式",
            Priority = "P0",
            SourceModule = "内置",
            Description = "副本识别与阶段状态。",
            Status = ModuleIntegrationStatus.Partial
        },
        new()
        {
            Key = FeatureKeys.HangCleanup,
            Group = "挂机",
            Title = "自动清包",
            Priority = "P0",
            SourceModule = "内置",
            Description = "月卡或回城清包。",
            Status = ModuleIntegrationStatus.Partial
        },

        new()
        {
            Key = FeatureKeys.ActivityXiaoxiao,
            Group = "活动",
            Title = "自动宵小",
            Priority = "P0",
            SourceModule = "内置",
            Description = "宵小活动状态。",
            Status = ModuleIntegrationStatus.Partial
        },
        new()
        {
            Key = FeatureKeys.ActivityRebel,
            Group = "活动",
            Title = "自动反贼",
            Priority = "P0",
            SourceModule = "内置",
            Description = "反贼活动状态。",
            Status = ModuleIntegrationStatus.Partial
        },
        new()
        {
            Key = FeatureKeys.ActivityMonk,
            Group = "活动",
            Title = "自动和尚",
            Priority = "P0",
            SourceModule = "内置",
            Description = "和尚活动状态。",
            Status = ModuleIntegrationStatus.Partial
        },

        new()
        {
            Key = FeatureKeys.LifeTrade,
            Group = "生活",
            Title = "跑商",
            Priority = "P1",
            SourceModule = "配置",
            Description = "跑商状态。",
            Status = ModuleIntegrationStatus.Partial
        },
        new()
        {
            Key = FeatureKeys.LifeShout,
            Group = "生活",
            Title = "喊话",
            Priority = "P0",
            SourceModule = "配置",
            Description = "角色喊话队列与运行状态。",
            Status = ModuleIntegrationStatus.Partial
        },
        new()
        {
            Key = FeatureKeys.HangMaterial,
            Group = "挂机",
            Title = "材料加工",
            Priority = "P1",
            SourceModule = "配置",
            Description = "材料加工状态。",
            Status = ModuleIntegrationStatus.Partial
        },
        new()
        {
            Key = FeatureKeys.LifeMining,
            Group = "生活",
            Title = "挖矿",
            Priority = "P1",
            SourceModule = "配置",
            Description = "挖矿状态。",
            Status = ModuleIntegrationStatus.Partial
        },
        new()
        {
            Key = FeatureKeys.LifeTreasureMap,
            Group = "生活",
            Title = "挖图",
            Priority = "P1",
            SourceModule = "配置",
            Description = "挖图状态。",
            Status = ModuleIntegrationStatus.Partial
        },
        new()
        {
            Key = FeatureKeys.LifeHerb,
            Group = "生活",
            Title = "采药",
            Priority = "P1",
            SourceModule = "配置",
            Description = "采药状态。",
            Status = ModuleIntegrationStatus.Partial
        },

        new()
        {
            Key = FeatureKeys.TaskDaily,
            Group = "任务自动化",
            Title = "日常任务",
            Priority = "P1",
            SourceModule = "配置",
            Description = "日常任务计划与状态。",
            Status = ModuleIntegrationStatus.Partial
        },
        new()
        {
            Key = FeatureKeys.QuizAssistant,
            Group = "答题",
            Title = "答题助手",
            Priority = "P1",
            SourceModule = "配置",
            Description = "答题服务状态。",
            Status = ModuleIntegrationStatus.Partial
        },

        new()
        {
            Key = "data.dict",
            Group = "数据",
            Title = "字库管理",
            Priority = "P0",
            SourceModule = "配置",
            Description = "字库加载、预览、索引。",
            Status = ModuleIntegrationStatus.Partial
        },
        new()
        {
            Key = "data.ocr",
            Group = "数据",
            Title = "识别测试",
            Priority = "P0",
            SourceModule = "配置",
            Description = "区域预览、识别测试。",
            Status = ModuleIntegrationStatus.Partial
        },
        new()
        {
            Key = "data.import",
            Group = "数据",
            Title = "数据管理",
            Priority = "P0",
            SourceModule = "配置",
            Description = "字库、坐标、配置管理。",
            Status = ModuleIntegrationStatus.Partial
        },
        new()
        {
            Key = "data.logs",
            Group = "数据",
            Title = "日志查看",
            Priority = "P0",
            SourceModule = "TLBB Agent",
            Description = "日志、复制、导出、过滤。",
            Status = ModuleIntegrationStatus.Partial
        },
    };

    public IEnumerable<ModuleDescriptor> ByGroup(string group) =>
        Modules.Where(m => string.Equals(m.Group, group, StringComparison.Ordinal));
}
