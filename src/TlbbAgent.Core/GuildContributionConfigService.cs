using System.Text;
using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class GuildContributionConfigService : IGuildContributionConfigService
{
    private const string Module = "GuildContributionConfig";
    private const string ProfilePrefix = "guild-contribution-";
    private readonly IProfileService _profiles;
    private readonly ILogService _log;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    static GuildContributionConfigService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public GuildContributionConfigService(IProfileService profiles, ILogService log)
    {
        _profiles = profiles;
        _log = log;
    }

    public GuildContributionConfig Current { get; private set; } = CreateDefault();
    public event EventHandler? Changed;

    public IReadOnlyList<GuildContributionTaskType> TaskTypes =>
        Current.TaskTypes.OrderBy(t => t.Priority).ToList();

    public IReadOnlyList<GuildContributionStep> Steps => Current.Steps.ToList();
    public IReadOnlyList<string> ItemRules => Current.ItemRules.ToList();

    public GuildContributionImportResult ImportPlainPlan(string path, string sourceModule)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException("Task plan file does not exist.", full);

        var parsed = new List<GuildContributionStep>();
        var skipped = 0;
        foreach (var raw in ReadAllLines(full))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            var step = ParseLine(line, sourceModule, parsed.Count + 1);
            if (step is null)
            {
                skipped++;
                continue;
            }

            parsed.Add(step);
        }

        if (parsed.Count > 0)
            Current.Steps = parsed;

        Current.SourceSummary = $"流程已载入：{parsed.Count} 步。";
        var message = $"流程已载入：步骤 {parsed.Count}，跳过 {skipped}。";
        _log.Info(Module, message);
        Changed?.Invoke(this, EventArgs.Empty);
        return new GuildContributionImportResult(full, parsed.Count, Current.ItemRules.Count, skipped, message);
    }

    public int LoadItemRules(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException("Item rule file does not exist.", full);

        var items = ReadAllLines(full)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith(';'))
            .Distinct(StringComparer.Ordinal)
            .Take(500)
            .ToList();

        Current.ItemRules = items;
        _log.Info(Module, $"物品规则已载入：{items.Count}。");
        Changed?.Invoke(this, EventArgs.Empty);
        return items.Count;
    }

    public void AddStep(GuildContributionStep step)
    {
        NormalizeStep(step, Current);
        if (string.IsNullOrWhiteSpace(step.Label))
            step.Label = $"{Current.Steps.Count + 1:D2}-{step.Action}";

        Current.Steps.Add(step);
        _log.Info(Module, $"已添加帮贡步骤：{step.Label}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool RemoveStep(string id)
    {
        var step = Current.Steps.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        if (step is null)
            return false;

        Current.Steps.Remove(step);
        _log.Info(Module, $"已删除帮贡步骤：{step.Label}");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void ClearSteps()
    {
        Current.Steps.Clear();
        _log.Info(Module, "已清空帮贡流程。");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save(string profileName)
    {
        Current.ProfileName = profileName;
        Normalize(Current);
        _profiles.Save(ProfilePrefix + profileName, JsonSerializer.Serialize(Current, JsonOptions));
        _log.Info(Module, $"帮贡配置已保存：{profileName}");
    }

    public bool Load(string profileName)
    {
        var json = _profiles.Load(ProfilePrefix + profileName);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var cfg = JsonSerializer.Deserialize<GuildContributionConfig>(json);
            if (cfg is null)
                return false;

            Normalize(cfg);
            Current = cfg;
            _log.Info(Module, $"帮贡配置已加载：{profileName}");
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch
        {
            _log.Error(Module, "配置加载失败。");
            return false;
        }
    }

    private static GuildContributionConfig CreateDefault()
    {
        var cfg = new GuildContributionConfig
        {
            TaskTypes =
            {
                new GuildContributionTaskType { Name = "任务一", Enabled = true, Priority = 1 },
                new GuildContributionTaskType { Name = "任务二", Enabled = true, Priority = 2 },
                new GuildContributionTaskType { Name = "任务三", Enabled = true, Priority = 3 },
                new GuildContributionTaskType { Name = "任务四", Enabled = true, Priority = 4 },
                new GuildContributionTaskType { Name = "任务五", Enabled = true, Priority = 5 }
            }
        };

        cfg.Steps.Add(new GuildContributionStep
        {
            TaskType = "任务一",
            Label = "01-接取",
            MapName = "帮派",
            Action = GuildContributionStepAction.AcceptTask,
            Hotkey = "F8",
            ClickX = 520,
            ClickY = 360,
            WaitMs = 800
        });
        cfg.Steps.Add(new GuildContributionStep
        {
            TaskType = "任务一",
            Label = "02-执行",
            MapName = "任务地图",
            Action = GuildContributionStepAction.ExecuteTask,
            Hotkey = "F1",
            WaitMs = 1200
        });
        cfg.Steps.Add(new GuildContributionStep
        {
            TaskType = "任务一",
            Label = "03-提交",
            MapName = "帮派",
            Action = GuildContributionStepAction.SubmitTask,
            Hotkey = "F8",
            ClickX = 520,
            ClickY = 360,
            WaitMs = 800
        });
        return cfg;
    }

    private static GuildContributionStep? ParseLine(string line, string sourceModule, int index)
    {
        var parts = SplitFields(line);
        if (parts.Count < 5)
            return null;

        var taskType = parts[0];
        var map = parts[1];
        if (!int.TryParse(parts[2], out var x) || !int.TryParse(parts[3], out var y))
            return null;

        var action = Enum.TryParse<GuildContributionStepAction>(NormalizeAction(parts[4]), ignoreCase: true, out var parsed)
            ? parsed
            : GuildContributionStepAction.Navigate;

        var step = new GuildContributionStep
        {
            TaskType = taskType,
            Label = $"{index:D2}-{action}",
            MapName = map,
            X = x,
            Y = y,
            Action = action,
            NpcName = parts.Count > 5 ? parts[5] : "",
            Hotkey = parts.Count > 6 ? parts[6] : "",
            ClickX = parts.Count > 7 && int.TryParse(parts[7], out var clickX) ? clickX : 520,
            ClickY = parts.Count > 8 && int.TryParse(parts[8], out var clickY) ? clickY : 360,
            WaitMs = parts.Count > 9 && int.TryParse(parts[9], out var wait) ? wait : 800,
            Text = parts.Count > 10 ? parts[10] : "",
            SourceModule = sourceModule,
            SourcePath = ""
        };

        NormalizeStep(step, new GuildContributionConfig());
        return step;
    }

    private static string NormalizeAction(string value) =>
        value.Trim() switch
        {
            "导航" or "跑点" => nameof(GuildContributionStepAction.Navigate),
            "对话" or "NPC" => nameof(GuildContributionStepAction.TalkNpc),
            "接取" or "接任务" => nameof(GuildContributionStepAction.AcceptTask),
            "执行" or "做任务" => nameof(GuildContributionStepAction.ExecuteTask),
            "提交" or "交任务" => nameof(GuildContributionStepAction.SubmitTask),
            "购买" => nameof(GuildContributionStepAction.BuyItem),
            "交付" or "交物品" => nameof(GuildContributionStepAction.DeliverItem),
            "热键" or "按键" => nameof(GuildContributionStepAction.UseHotkey),
            "点击" => nameof(GuildContributionStepAction.Click),
            "文本" or "输入" => nameof(GuildContributionStepAction.Text),
            "等待" => nameof(GuildContributionStepAction.Wait),
            "清包" => nameof(GuildContributionStepAction.CleanupBag),
            _ => value.Trim()
        };

    private static List<string> SplitFields(string line)
    {
        var delimiter = line.Contains('\t') ? '\t' : line.Contains('|') ? '|' : ',';
        return line.Split(delimiter, StringSplitOptions.TrimEntries).ToList();
    }

    private static string[] ReadAllLines(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return SplitLines(new UTF8Encoding(false, true).GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            return SplitLines(Encoding.GetEncoding(936).GetString(bytes));
        }
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static void Normalize(GuildContributionConfig cfg)
    {
        cfg.RoundCount = Math.Clamp(cfg.RoundCount, 1, 500);
        cfg.ArrivalDistance = Math.Clamp(cfg.ArrivalDistance, 1, 100);
        cfg.StepIntervalMs = Math.Clamp(cfg.StepIntervalMs, 50, 60000);
        cfg.ActionIntervalMs = Math.Clamp(cfg.ActionIntervalMs, 50, 60000);
        cfg.MaxStepRetry = Math.Clamp(cfg.MaxStepRetry, 0, 20);
        cfg.TaskTypes ??= new List<GuildContributionTaskType>();
        cfg.Steps ??= new List<GuildContributionStep>();
        cfg.ItemRules ??= new List<string>();
        if (cfg.TaskTypes.Count == 0)
            cfg.TaskTypes = CreateDefault().TaskTypes;
        foreach (var step in cfg.Steps)
            NormalizeStep(step, cfg);
    }

    private static void NormalizeStep(GuildContributionStep step, GuildContributionConfig cfg)
    {
        step.TaskType = string.IsNullOrWhiteSpace(step.TaskType)
            ? cfg.TaskTypes.OrderBy(t => t.Priority).FirstOrDefault()?.Name ?? "任务一"
            : step.TaskType.Trim();
        step.MapName = step.MapName.Trim();
        step.NpcName = step.NpcName.Trim();
        step.Hotkey = step.Hotkey.Trim();
        step.Text = step.Text.Trim();
        step.X = Math.Max(0, step.X);
        step.Y = Math.Max(0, step.Y);
        step.ClickX = Math.Max(0, step.ClickX);
        step.ClickY = Math.Max(0, step.ClickY);
        step.WaitMs = Math.Clamp(step.WaitMs, 0, 60000);
        step.RetryLimit = Math.Clamp(step.RetryLimit, 0, 20);
        step.SourcePath = "";
        if (string.IsNullOrWhiteSpace(step.SourceModule))
            step.SourceModule = "配置";
    }
}
