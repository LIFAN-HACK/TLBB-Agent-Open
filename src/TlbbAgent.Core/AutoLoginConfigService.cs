using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class AutoLoginConfigService : IAutoLoginConfigService
{
    private const string Module = "自动上号";
    private const string Prefix = "auto-login-";
    private readonly IProfileService _profiles;
    private readonly ILogService _log;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AutoLoginConfigService(IProfileService profiles, ILogService log)
    {
        _profiles = profiles;
        _log = log;
        Current = CreateDefault();
    }

    public AutoLoginConfig Current { get; private set; }
    public event EventHandler? Changed;

    public void NotifyChanged()
    {
        Normalize(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save(string profileName)
    {
        Normalize(Current);
        _profiles.Save(Prefix + profileName, JsonSerializer.Serialize(Current, JsonOptions));
        _log.Info(Module, $"已保存自动上号配置：{profileName}");
    }

    public bool Load(string profileName)
    {
        var json = _profiles.Load(Prefix + profileName);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            Current = JsonSerializer.Deserialize<AutoLoginConfig>(json) ?? CreateDefault();
            Normalize(Current);
            Changed?.Invoke(this, EventArgs.Empty);
            _log.Info(Module, $"已加载自动上号配置：{profileName}");
            return true;
        }
        catch
        {
            _log.Warn(Module, "配置加载失败。");
            return false;
        }
    }

    public void AddStep(AutoLoginStep step)
    {
        Current.Steps.Add(step);
        Normalize(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool RemoveStep(string id)
    {
        var step = Current.Steps.FirstOrDefault(s => s.Id == id);
        if (step is null)
            return false;

        Current.Steps.Remove(step);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void MoveStep(string id, int offset)
    {
        var index = Current.Steps.FindIndex(s => s.Id == id);
        if (index < 0)
            return;

        var next = Math.Clamp(index + offset, 0, Current.Steps.Count - 1);
        if (next == index)
            return;

        var step = Current.Steps[index];
        Current.Steps.RemoveAt(index);
        Current.Steps.Insert(next, step);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static AutoLoginConfig CreateDefault() => new()
    {
        Enabled = false,
        StartDelayMs = 1500,
        StopAfterSequence = true,
        Steps =
        {
            new AutoLoginStep { Kind = AutoLoginStepKind.Wait, Label = "等待界面稳定", DelayAfterMs = 1500 },
            new AutoLoginStep { Kind = AutoLoginStepKind.Hotkey, Label = "进入角色", Hotkey = "Enter", DelayAfterMs = 2000 }
        }
    };

    private static void Normalize(AutoLoginConfig config)
    {
        config.StartDelayMs = Math.Clamp(config.StartDelayMs, 0, 120000);
        config.Steps ??= new List<AutoLoginStep>();
        foreach (var step in config.Steps)
        {
            step.Label = (step.Label ?? "").Trim();
            step.Hotkey = (step.Hotkey ?? "").Trim();
            step.DelayAfterMs = Math.Clamp(step.DelayAfterMs, 0, 120000);
            step.ClickX = Math.Clamp(step.ClickX, 0, 20000);
            step.ClickY = Math.Clamp(step.ClickY, 0, 20000);
            if (string.IsNullOrWhiteSpace(step.Label))
                step.Label = step.Kind.ToString();
        }
    }
}
