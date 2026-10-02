using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class DungeonConfigService : IDungeonConfigService
{
    private const string Module = "DungeonConfig";
    private const string ProfilePrefix = "dungeon-";
    private readonly IProfileService _profiles;
    private readonly ILogService _log;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public DungeonConfigService(IProfileService profiles, ILogService log)
    {
        _profiles = profiles;
        _log = log;
    }

    public DungeonConfig Current { get; private set; } = new();
    public event EventHandler? Changed;

    public IReadOnlyList<DungeonScriptStep> CurrentSteps =>
        Current.Steps
            .Where(s => s.Mode == Current.Mode)
            .OrderBy(s => s.Order)
            .ToList();

    public void AddStep(DungeonScriptStep step)
    {
        step.Mode = Current.Mode;
        if (step.Order <= 0)
            step.Order = CurrentSteps.Count == 0 ? 10 : CurrentSteps.Max(s => s.Order) + 10;
        if (string.IsNullOrWhiteSpace(step.StageName))
            step.StageName = $"{Current.Mode} 阶段 {step.Order}";

        Current.Steps.Add(step);
        _log.Info(Module, $"已添加副本脚本步骤：{step.StageName} [{step.Mode}]");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool RemoveStep(string id)
    {
        var step = Current.Steps.FirstOrDefault(s => s.Id == id);
        if (step is null)
            return false;

        Current.Steps.Remove(step);
        _log.Info(Module, $"已删除副本脚本步骤：{step.StageName}");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void ClearSteps(DungeonMode mode)
    {
        Current.Steps.RemoveAll(s => s.Mode == mode);
        _log.Info(Module, $"已清空副本脚本步骤：{mode}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ResetDefaults()
    {
        Current = new DungeonConfig();
        _log.Info(Module, "三环副本配置已恢复默认脚本。");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save(string profileName)
    {
        Current.ProfileName = profileName;
        _profiles.Save(ProfilePrefix + profileName, JsonSerializer.Serialize(Current, JsonOptions));
        _log.Info(Module, $"三环副本配置已保存：{profileName}");
    }

    public bool Load(string profileName)
    {
        var json = _profiles.Load(ProfilePrefix + profileName);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var cfg = JsonSerializer.Deserialize<DungeonConfig>(json);
            if (cfg is null)
                return false;

            EnsureDefaults(cfg);
            Current = cfg;
            _log.Info(Module, $"三环副本配置已加载：{profileName}");
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch
        {
            _log.Error(Module, "配置加载失败。");
            return false;
        }
    }

    private static void EnsureDefaults(DungeonConfig cfg)
    {
        if (cfg.Steps.Count > 0)
            return;

        cfg.Steps = DungeonConfig.CreateDefaultSteps();
    }
}

