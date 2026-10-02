using System.Text;
using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class SchoolTaskConfigService : ISchoolTaskConfigService
{
    private const string Module = "SchoolTaskConfig";
    private const string ProfilePrefix = "school-task-";
    private readonly IProfileService _profiles;
    private readonly ILogService _log;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    static SchoolTaskConfigService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public SchoolTaskConfigService(IProfileService profiles, ILogService log)
    {
        _profiles = profiles;
        _log = log;
    }

    public SchoolTaskConfig Current { get; private set; } = CreateDefault();
    public event EventHandler? Changed;

    public IReadOnlyList<SchoolTaskStep> Steps => Current.Steps.ToList();
    public IReadOnlyList<string> StoreItems => Current.StoreItems.ToList();
    public IReadOnlyList<string> TradeItems => Current.TradeItems.ToList();

    public SchoolTaskImportResult ImportPlainPlan(string path, string sourceModule)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException("Task plan file does not exist.", full);

        var parsed = new List<SchoolTaskStep>();
        var skipped = 0;
        foreach (var raw in ReadAllLines(full))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            var step = ParseStepLine(line, sourceModule, parsed.Count + 1);
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
        return new SchoolTaskImportResult(full, parsed.Count, Current.StoreItems.Count, Current.TradeItems.Count, skipped, message);
    }

    public int LoadItemList(string path, bool tradeItems)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException("Item list file does not exist.", full);

        var items = ReadAllLines(full)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith(';'))
            .Distinct(StringComparer.Ordinal)
            .Take(500)
            .ToList();

        if (tradeItems)
            Current.TradeItems = items;
        else
            Current.StoreItems = items;

        _log.Info(Module, tradeItems ? $"交易物品已载入：{items.Count}。" : $"存放物品已载入：{items.Count}。");
        Changed?.Invoke(this, EventArgs.Empty);
        return items.Count;
    }

    public void AddStep(SchoolTaskStep step)
    {
        NormalizeStep(step);
        if (string.IsNullOrWhiteSpace(step.Label))
            step.Label = $"{Current.Steps.Count + 1:D2}-{step.Action}";

        Current.Steps.Add(step);
        _log.Info(Module, $"已添加师门步骤：{step.Label}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool RemoveStep(string id)
    {
        var step = Current.Steps.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        if (step is null)
            return false;

        Current.Steps.Remove(step);
        _log.Info(Module, $"已删除师门步骤：{step.Label}");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void ClearSteps()
    {
        Current.Steps.Clear();
        _log.Info(Module, "已清空师门流程。");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save(string profileName)
    {
        Current.ProfileName = profileName;
        Normalize(Current);
        _profiles.Save(ProfilePrefix + profileName, JsonSerializer.Serialize(Current, JsonOptions));
        _log.Info(Module, $"师门配置已保存：{profileName}");
    }

    public bool Load(string profileName)
    {
        var json = _profiles.Load(ProfilePrefix + profileName);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var cfg = JsonSerializer.Deserialize<SchoolTaskConfig>(json);
            if (cfg is null)
                return false;

            Normalize(cfg);
            Current = cfg;
            _log.Info(Module, $"师门配置已加载：{profileName}");
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch
        {
            _log.Error(Module, "配置加载失败。");
            return false;
        }
    }

    private static SchoolTaskConfig CreateDefault() => new()
    {
        Steps =
        {
            new SchoolTaskStep
            {
                Label = "01-接任务",
                MapName = "门派",
                Action = SchoolTaskStepAction.AcceptTask,
                Hotkey = "F8",
                ClickX = 520,
                ClickY = 360,
                WaitMs = 800
            },
            new SchoolTaskStep
            {
                Label = "02-执行",
                MapName = "任务地图",
                Action = SchoolTaskStepAction.CompleteTask,
                Hotkey = "F1",
                WaitMs = 1200
            },
            new SchoolTaskStep
            {
                Label = "03-交任务",
                MapName = "门派",
                Action = SchoolTaskStepAction.SubmitTask,
                Hotkey = "F8",
                ClickX = 520,
                ClickY = 360,
                WaitMs = 800
            }
        }
    };

    private static SchoolTaskStep? ParseStepLine(string line, string sourceModule, int index)
    {
        var parts = SplitFields(line);
        if (parts.Count < 4)
            return null;

        var map = parts[0];
        if (!int.TryParse(parts[1], out var x) || !int.TryParse(parts[2], out var y))
            return null;

        var action = Enum.TryParse<SchoolTaskStepAction>(NormalizeAction(parts[3]), ignoreCase: true, out var parsed)
            ? parsed
            : SchoolTaskStepAction.Navigate;

        var step = new SchoolTaskStep
        {
            Label = $"{index:D2}-{action}",
            MapName = map,
            X = x,
            Y = y,
            Action = action,
            NpcName = parts.Count > 4 ? parts[4] : "",
            Hotkey = parts.Count > 5 ? parts[5] : "",
            ClickX = parts.Count > 6 && int.TryParse(parts[6], out var clickX) ? clickX : 520,
            ClickY = parts.Count > 7 && int.TryParse(parts[7], out var clickY) ? clickY : 360,
            WaitMs = parts.Count > 8 && int.TryParse(parts[8], out var wait) ? wait : 800,
            Text = parts.Count > 9 ? parts[9] : "",
            SourceModule = sourceModule,
            SourcePath = ""
        };

        NormalizeStep(step);
        return step;
    }

    private static string NormalizeAction(string value) =>
        value.Trim() switch
        {
            "导航" or "跑点" => nameof(SchoolTaskStepAction.Navigate),
            "对话" or "NPC" => nameof(SchoolTaskStepAction.TalkNpc),
            "接取" or "接任务" => nameof(SchoolTaskStepAction.AcceptTask),
            "完成" or "做任务" => nameof(SchoolTaskStepAction.CompleteTask),
            "提交" or "交任务" => nameof(SchoolTaskStepAction.SubmitTask),
            "购买" or "买物品" => nameof(SchoolTaskStepAction.BuyItem),
            "存放" or "存物品" => nameof(SchoolTaskStepAction.StoreItem),
            "交易" or "交物品" => nameof(SchoolTaskStepAction.TradeItem),
            "热键" or "按键" => nameof(SchoolTaskStepAction.UseHotkey),
            "点击" => nameof(SchoolTaskStepAction.Click),
            "文本" or "输入" => nameof(SchoolTaskStepAction.Text),
            "等待" => nameof(SchoolTaskStepAction.Wait),
            "清包" => nameof(SchoolTaskStepAction.CleanupBag),
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

    private static void Normalize(SchoolTaskConfig cfg)
    {
        cfg.RoundCount = Math.Clamp(cfg.RoundCount, 1, 500);
        cfg.ArrivalDistance = Math.Clamp(cfg.ArrivalDistance, 1, 100);
        cfg.StepIntervalMs = Math.Clamp(cfg.StepIntervalMs, 50, 60000);
        cfg.ActionIntervalMs = Math.Clamp(cfg.ActionIntervalMs, 50, 60000);
        cfg.MaxStepRetry = Math.Clamp(cfg.MaxStepRetry, 0, 20);
        cfg.Steps ??= new List<SchoolTaskStep>();
        cfg.StoreItems ??= new List<string>();
        cfg.TradeItems ??= new List<string>();
        foreach (var step in cfg.Steps)
            NormalizeStep(step);
    }

    private static void NormalizeStep(SchoolTaskStep step)
    {
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
