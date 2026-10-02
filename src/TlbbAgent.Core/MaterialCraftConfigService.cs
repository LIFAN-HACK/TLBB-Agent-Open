using System.Text;
using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class MaterialCraftConfigService : IMaterialCraftConfigService
{
    private const string Module = "MaterialCraftConfig";
    private const string ProfilePrefix = "material-craft-";
    private readonly IProfileService _profiles;
    private readonly ILogService _log;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    static MaterialCraftConfigService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public MaterialCraftConfigService(IProfileService profiles, ILogService log)
    {
        _profiles = profiles;
        _log = log;
    }

    public MaterialCraftConfig Current { get; private set; } = CreateDefault();
    public event EventHandler? Changed;

    public void NotifyChanged()
    {
        Normalize(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<MaterialCraftStep> Steps =>
        Current.Steps
            .ToList();

    public MaterialCraftImportResult ImportPlainPlan(string path, string sourceModule)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException("Craft plan file does not exist.", full);

        var parsed = new List<MaterialCraftStep>();
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
        return new MaterialCraftImportResult(full, parsed.Count, skipped, message);
    }

    public void AddStep(MaterialCraftStep step)
    {
        if (string.IsNullOrWhiteSpace(step.Label))
            step.Label = $"{Current.Steps.Count + 1:D2}-{step.Action}";

        Current.Steps.Add(step);
        _log.Info(Module, $"已添加加工步骤：{step.Label}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool RemoveStep(string id)
    {
        var step = Current.Steps.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        if (step is null)
            return false;

        Current.Steps.Remove(step);
        _log.Info(Module, $"已删除加工步骤：{step.Label}");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        Current.Steps.Clear();
        _log.Info(Module, "已清空加工流程。");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save(string profileName)
    {
        Current.ProfileName = profileName;
        Normalize(Current);
        _profiles.Save(ProfilePrefix + profileName, JsonSerializer.Serialize(Current, JsonOptions));
        _log.Info(Module, $"材料加工配置已保存：{profileName}");
    }

    public bool Load(string profileName)
    {
        var json = _profiles.Load(ProfilePrefix + profileName);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var cfg = JsonSerializer.Deserialize<MaterialCraftConfig>(json);
            if (cfg is null)
                return false;

            Normalize(cfg);
            Current = cfg;
            _log.Info(Module, $"材料加工配置已加载：{profileName}");
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch
        {
            _log.Error(Module, "配置加载失败。");
            return false;
        }
    }

    private static MaterialCraftConfig CreateDefault() => new()
    {
        RequireVisualConfirmation = true,
        ProcessUntilMaterialsExhausted = true,
        ProcessAllRecipes = true,
        MaxContinuousProcessMinutes = 30,
        MaxRecipePages = 20,
        PlanName = "视觉材料加工",
        SourceSummary = "每次加工前确认面板，持续执行到连续两帧确认材料耗尽。",
        Steps =
        {
            new MaterialCraftStep
            {
                Label = "01-打开材料加工",
                Action = MaterialCraftStepAction.OpenPanel,
                WaitMs = 300
            },
            new MaterialCraftStep
            {
                Label = "02-全部制造",
                Action = MaterialCraftStepAction.Process,
                Text = "manufacture_all",
                WaitMs = 900
            }
        }
    };

    private static MaterialCraftConfig CreateLegacyDefault() => new()
    {
        Steps =
        {
            new MaterialCraftStep
            {
                Label = "01-打开",
                Action = MaterialCraftStepAction.OpenPanel,
                Hotkey = "F8",
                WaitMs = 500
            },
            new MaterialCraftStep
            {
                Label = "02-加工",
                Action = MaterialCraftStepAction.Process,
                Hotkey = "F9",
                WaitMs = 900
            },
            new MaterialCraftStep
            {
                Label = "03-确认",
                Action = MaterialCraftStepAction.Confirm,
                ClickX = 520,
                ClickY = 360,
                WaitMs = 500
            }
        }
    };

    private static MaterialCraftStep? ParseLine(string line, string sourceModule, int index)
    {
        var parts = SplitFields(line);
        if (parts.Count == 0)
            return null;

        if (!Enum.TryParse<MaterialCraftStepAction>(NormalizeAction(parts[0]), ignoreCase: true, out var action))
            return null;

        var material = parts.Count > 1 ? parts[1] : "";
        var recipe = parts.Count > 2 ? parts[2] : "";
        var hotkey = parts.Count > 3 ? parts[3] : "";
        var clickX = parts.Count > 4 && int.TryParse(parts[4], out var x) ? x : 520;
        var clickY = parts.Count > 5 && int.TryParse(parts[5], out var y) ? y : 360;
        var wait = parts.Count > 6 && int.TryParse(parts[6], out var ms) ? ms : 600;
        var text = parts.Count > 7 ? parts[7] : "";

        return new MaterialCraftStep
        {
            Label = $"{index:D2}-{action}",
            Action = action,
            MaterialName = material,
            RecipeName = recipe,
            Hotkey = hotkey,
            ClickX = clickX,
            ClickY = clickY,
            WaitMs = wait,
            Text = text,
            SourceModule = sourceModule,
            SourcePath = ""
        };
    }

    private static string NormalizeAction(string value) =>
        value.Trim() switch
        {
            "热键" or "按键" => nameof(MaterialCraftStepAction.Hotkey),
            "点击" => nameof(MaterialCraftStepAction.Click),
            "按键后点击" or "热键后点击" => nameof(MaterialCraftStepAction.HotkeyThenClick),
            "文本" or "输入" => nameof(MaterialCraftStepAction.Text),
            "等待" => nameof(MaterialCraftStepAction.Wait),
            "打开" or "打开面板" => nameof(MaterialCraftStepAction.OpenPanel),
            "选择" or "选择配方" => nameof(MaterialCraftStepAction.SelectRecipe),
            "加工" or "执行加工" => nameof(MaterialCraftStepAction.Process),
            "确认" => nameof(MaterialCraftStepAction.Confirm),
            "清包" => nameof(MaterialCraftStepAction.CleanupBag),
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

    private static void Normalize(MaterialCraftConfig cfg)
    {
        if (!cfg.RequireVisualConfirmation)
            cfg.ProcessUntilMaterialsExhausted = false;

        cfg.ProcessingCycleMinutes = Math.Clamp(cfg.ProcessingCycleMinutes <= 0 ? 30 : cfg.ProcessingCycleMinutes, 1, 1440);
        cfg.BatchCount = Math.Max(1, cfg.BatchCount);
        cfg.MaxContinuousProcessMinutes = Math.Clamp(cfg.MaxContinuousProcessMinutes <= 0 ? 30 : cfg.MaxContinuousProcessMinutes, 1, 240);
        cfg.MaxRecipePages = Math.Clamp(cfg.MaxRecipePages <= 0 ? 20 : cfg.MaxRecipePages, 1, 100);
        cfg.StepIntervalMs = Math.Clamp(cfg.StepIntervalMs, 50, 60000);
        cfg.MaxStepRetry = Math.Clamp(cfg.MaxStepRetry, 0, 20);
        cfg.CleanupWaitMs = Math.Clamp(cfg.CleanupWaitMs, 50, 60000);
        cfg.Steps ??= new List<MaterialCraftStep>();
        foreach (var step in cfg.Steps)
        {
            step.WaitMs = Math.Clamp(step.WaitMs, 0, 60000);
            step.RetryLimit = Math.Clamp(step.RetryLimit, 0, 20);
            step.SourcePath = "";
            if (string.IsNullOrWhiteSpace(step.SourceModule))
                step.SourceModule = "配置";
        }
    }
}
