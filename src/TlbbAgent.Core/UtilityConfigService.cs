using System.Text;
using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class UtilityConfigService : IUtilityConfigService
{
    private const string Module = "UtilityConfig";
    private const string ProfilePrefix = "utility-";
    private readonly IProfileService _profiles;
    private readonly ILogService _log;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    static UtilityConfigService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public UtilityConfigService(IProfileService profiles, ILogService log)
    {
        _profiles = profiles;
        _log = log;
    }

    public UtilityConfig Current { get; private set; } = CreateDefault();
    public event EventHandler? Changed;

    public void NotifyChanged()
    {
        Normalize(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<UtilityActionStep> Steps => Current.Steps.ToList();
    public IReadOnlyList<string> KeepItems => Current.KeepItems.ToList();

    public int LoadKeepItems(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException("Item list file does not exist.", full);

        var items = ReadAllLines(full)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith(';'))
            .Distinct(StringComparer.Ordinal)
            .Take(1000)
            .ToList();

        Current.KeepItems = items;
        Current.Cleanup.Policy.KeepItems = items.ToList();
        _log.Info(Module, $"保留物品已载入：{items.Count}。");
        Changed?.Invoke(this, EventArgs.Empty);
        return items.Count;
    }

    public void AddStep(UtilityActionStep step)
    {
        NormalizeStep(step);
        if (string.IsNullOrWhiteSpace(step.Label))
            step.Label = $"{Current.Steps.Count + 1:D2}-{step.Group}";

        Current.Steps.Add(step);
        _log.Info(Module, $"已添加便捷动作：{step.Label}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool RemoveStep(string id)
    {
        var step = Current.Steps.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        if (step is null)
            return false;

        Current.Steps.Remove(step);
        _log.Info(Module, $"已删除便捷动作：{step.Label}");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void MoveStep(string id, int offset)
    {
        var index = Current.Steps.FindIndex(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return;

        var target = Math.Clamp(index + offset, 0, Current.Steps.Count - 1);
        if (target == index)
            return;

        var step = Current.Steps[index];
        Current.Steps.RemoveAt(index);
        Current.Steps.Insert(target, step);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save(string profileName)
    {
        Current.ProfileName = profileName;
        Normalize(Current);
        _profiles.Save(ProfilePrefix + profileName, JsonSerializer.Serialize(Current, JsonOptions));
        _log.Info(Module, $"便捷配置已保存：{profileName}");
    }

    public bool Load(string profileName)
    {
        var json = _profiles.Load(ProfilePrefix + profileName);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var cfg = JsonSerializer.Deserialize<UtilityConfig>(json);
            if (cfg is null)
                return false;

            Normalize(cfg);
            Current = cfg;
            _log.Info(Module, $"便捷配置已加载：{profileName}");
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch
        {
            _log.Error(Module, "配置加载失败。");
            return false;
        }
    }

    private static UtilityConfig CreateDefault() => new()
    {
        Steps =
        {
            new UtilityActionStep { Enabled = false, Group = UtilityActionGroup.Cleanup, Label = "清包", Kind = UtilityActionKind.Hotkey },
            new UtilityActionStep { Enabled = false, Group = UtilityActionGroup.PortableBank, Label = "随身银行", Kind = UtilityActionKind.Hotkey },
            new UtilityActionStep { Enabled = false, Group = UtilityActionGroup.PortableShop, Label = "随身商店", Kind = UtilityActionKind.Hotkey },
            new UtilityActionStep { Enabled = false, Group = UtilityActionGroup.Yulong, Label = "御龙帖", Kind = UtilityActionKind.Hotkey }
        }
    };

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

    private static void Normalize(UtilityConfig cfg)
    {
        cfg.RepeatIntervalMs = Math.Clamp(cfg.RepeatIntervalMs, 500, 3600000);
        cfg.Cleanup ??= new BagCleanupConfig();
        cfg.Cleanup.Grid ??= new InventoryGridConfig();
        cfg.Cleanup.Policy ??= new ItemPolicyConfig();
        cfg.Cleanup.TemplateMatching ??= new BagItemTemplateMatchConfig();
        if (!Enum.IsDefined(cfg.Cleanup.IntentMode))
            cfg.Cleanup.IntentMode = BagCleanupIntentMode.MonthlyCard;
        if (!Enum.IsDefined(cfg.Cleanup.ReturnCity))
            cfg.Cleanup.ReturnCity = BagCleanupReturnCity.Luoyang;
        cfg.Cleanup.Mode = cfg.Cleanup.EnabledIntent
            ? BagCleanupExecutionMode.DryRun
            : BagCleanupExecutionMode.Disabled;
        cfg.Cleanup.AllowActualSell = false;
        if (!cfg.Cleanup.OpenBagHotkeyExplicitlyConfigured)
        {
            if (string.Equals(cfg.Cleanup.OpenBagHotkey?.Trim(), "B", StringComparison.OrdinalIgnoreCase))
            {
                cfg.Cleanup.OpenBagHotkey = "";
            }
            else if (!string.IsNullOrWhiteSpace(cfg.Cleanup.OpenBagHotkey))
            {
                // 旧配置中非默认值只能来自用户录入，迁移时保留其显式意图。
                cfg.Cleanup.OpenBagHotkey = cfg.Cleanup.OpenBagHotkey.Trim();
                cfg.Cleanup.OpenBagHotkeyExplicitlyConfigured = true;
            }
        }
        var bundledIndex = Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "runtime",
            "item_assets_v1",
            "runtime",
            "runtime_template_index.json");
        if (string.IsNullOrWhiteSpace(cfg.Cleanup.TemplateMatching.IndexPath)
            && File.Exists(bundledIndex))
        {
            cfg.Cleanup.TemplateMatching.Enabled = true;
            cfg.Cleanup.TemplateMatching.IndexPath = bundledIndex;
        }
        cfg.Cleanup.Policy.RequireExplicitSellKeyword = true;
        cfg.Cleanup.Policy.ProtectUnknownItems = true;
        cfg.KeepItems ??= new List<string>();
        if (cfg.Cleanup.Policy.KeepItems.Count == 0 && cfg.KeepItems.Count > 0)
            cfg.Cleanup.Policy.KeepItems = cfg.KeepItems.ToList();
        cfg.Cleanup.Policy.AllowedSellItems = NormalizeItemNames(cfg.Cleanup.Policy.AllowedSellItems);
        cfg.Cleanup.Policy.KeepItems = NormalizeItemNames(cfg.Cleanup.Policy.KeepItems);
        var protectedNames = cfg.Cleanup.Policy.KeepItems.ToHashSet(StringComparer.OrdinalIgnoreCase);
        cfg.Cleanup.Policy.AllowedSellItems.RemoveAll(protectedNames.Contains);
        cfg.Steps ??= new List<UtilityActionStep>();
        cfg.KeepItems = cfg.Cleanup.Policy.KeepItems.ToList();
        foreach (var step in cfg.Steps)
            NormalizeStep(step);
    }

    private static List<string> NormalizeItemNames(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>())
        .Select(value => value?.Trim() ?? string.Empty)
        .Where(value => value.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(1000)
        .ToList();

    private static void NormalizeStep(UtilityActionStep step)
    {
        step.Label = step.Label.Trim();
        step.Hotkey = step.Hotkey.Trim();
        step.Text = step.Text.Trim();
        step.ClickX = Math.Max(0, step.ClickX);
        step.ClickY = Math.Max(0, step.ClickY);
        step.DelayAfterMs = Math.Clamp(step.DelayAfterMs, 0, 60000);
    }
}
