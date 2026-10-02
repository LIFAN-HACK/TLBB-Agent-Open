using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class NpcGrabConfigService : INpcGrabConfigService
{
    private const string Module = "NpcGrabConfig";
    private const string ProfilePrefix = "npc-grab-";
    private readonly IProfileService _profiles;
    private readonly ILogService _log;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public NpcGrabConfigService(IProfileService profiles, ILogService log)
    {
        _profiles = profiles;
        _log = log;
    }

    public NpcGrabConfig Current { get; private set; } = new();
    public event EventHandler? Changed;

    public IReadOnlyList<NpcGrabTargetConfig> EnabledTargets =>
        Current.Targets
            .Where(t => t.Enabled)
            .OrderBy(t => t.Priority)
            .ToList();

    public void ResetDefaults()
    {
        Current = new NpcGrabConfig();
        _log.Info(Module, "抢 NPC 配置已恢复默认目标清单。");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save(string profileName)
    {
        Current.ProfileName = profileName;
        _profiles.Save(ProfilePrefix + profileName, JsonSerializer.Serialize(Current, JsonOptions));
        _log.Info(Module, $"抢 NPC 配置已保存：{profileName}");
    }

    public bool Load(string profileName)
    {
        var json = _profiles.Load(ProfilePrefix + profileName);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var cfg = JsonSerializer.Deserialize<NpcGrabConfig>(json);
            if (cfg is null)
                return false;

            EnsureTargets(cfg);
            Current = cfg;
            _log.Info(Module, $"抢 NPC 配置已加载：{profileName}");
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch
        {
            _log.Error(Module, "配置加载失败。");
            return false;
        }
    }

    private static void EnsureTargets(NpcGrabConfig cfg)
    {
        var defaults = NpcGrabConfig.CreateDefaultTargets();
        foreach (var item in defaults)
        {
            var existing = cfg.Targets.FirstOrDefault(t => t.Kind == item.Kind);
            if (existing is not null)
            {
                UpgradeUnsafeLegacyKeywords(existing, item);
                continue;
            }

            cfg.Targets.Add(item);
        }
    }

    private static void UpgradeUnsafeLegacyKeywords(NpcGrabTargetConfig existing, NpcGrabTargetConfig defaults)
    {
        var current = existing.Keywords.Replace('；', ';').Trim();
        var shouldUpgrade = existing.Kind switch
        {
            NpcGrabTargetKind.Small => string.Equals(current, "小;小小;小怪", StringComparison.Ordinal),
            NpcGrabTargetKind.SmallDan => string.Equals(current, "小丹;丹", StringComparison.Ordinal),
            NpcGrabTargetKind.Small2 => string.Equals(current, "小2;小二;小贼", StringComparison.Ordinal),
            NpcGrabTargetKind.Monk => string.Equals(current, "和尚;丹和尚;恶僧", StringComparison.Ordinal),
            NpcGrabTargetKind.GaoJiaWei => string.Equals(current, "高甲卫;甲卫", StringComparison.Ordinal),
            NpcGrabTargetKind.GaoJiaWei23 => string.Equals(current, "高甲223;高甲卫23;高甲23", StringComparison.Ordinal),
            _ => false
        };

        if (shouldUpgrade)
            existing.Keywords = defaults.Keywords;
    }
}
