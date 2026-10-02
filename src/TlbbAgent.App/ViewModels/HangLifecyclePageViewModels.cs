using CommunityToolkit.Mvvm.ComponentModel;
using TlbbAgent.Contracts;

namespace TlbbAgent.App.ViewModels;

public sealed record BagCleanupModeOption(BagCleanupIntentMode Value, string Display)
{
    public override string ToString() => Display;
}

public sealed record BagCleanupCityOption(BagCleanupReturnCity Value, string Display)
{
    public override string ToString() => Display;
}

public sealed class AutoCleanupPageViewModel : PageViewModel
{
    private readonly IUtilityConfigService _service;
    private bool _isLocalChange;

    public AutoCleanupPageViewModel(IUtilityConfigService service)
    {
        _service = service;
        _service.Changed += (_, _) =>
        {
            if (!_isLocalChange)
                UiThread.Dispatch(Refresh);
        };
    }

    public override string Title => "自动清包";
    public string Status => "未就绪";
    public bool CanStart => false;
    public bool CanEnableIntent => false;

    public IReadOnlyList<BagCleanupModeOption> Modes { get; } =
    [
        new(BagCleanupIntentMode.MonthlyCard, "月卡清包"),
        new(BagCleanupIntentMode.ReturnToCity, "回城清包")
    ];

    public IReadOnlyList<BagCleanupCityOption> Cities { get; } =
    [
        new(BagCleanupReturnCity.Luoyang, "洛阳"),
        new(BagCleanupReturnCity.Suzhou, "苏州"),
        new(BagCleanupReturnCity.Dali, "大理"),
        new(BagCleanupReturnCity.Loulan, "楼兰")
    ];

    private BagCleanupConfig Cleanup => _service.Current.Cleanup;
    private ItemPolicyConfig Policy => Cleanup.Policy;

    public bool EnabledIntent
    {
        get => Cleanup.EnabledIntent;
        set
        {
            if (Cleanup.EnabledIntent == value)
                return;

            Cleanup.EnabledIntent = value;
            OnPropertyChanged();
            NotifyConfigChanged();
        }
    }

    public BagCleanupIntentMode SelectedMode
    {
        get => Cleanup.IntentMode;
        set
        {
            if (Cleanup.IntentMode == value)
                return;

            Cleanup.IntentMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsReturnToCity));
            NotifyConfigChanged();
        }
    }

    public bool IsReturnToCity => SelectedMode == BagCleanupIntentMode.ReturnToCity;

    public BagCleanupReturnCity SelectedCity
    {
        get => Cleanup.ReturnCity;
        set
        {
            if (Cleanup.ReturnCity == value)
                return;

            Cleanup.ReturnCity = value;
            OnPropertyChanged();
            NotifyConfigChanged();
        }
    }

    public string AllowedSellItemsText
    {
        get => string.Join(Environment.NewLine, Policy.AllowedSellItems);
        set
        {
            var protectedNames = Policy.KeepItems.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var normalized = ParseNames(value)
                .Where(name => !protectedNames.Contains(name))
                .ToList();
            if (SequenceEqual(Policy.AllowedSellItems, normalized))
                return;

            Policy.AllowedSellItems = normalized;
            OnPropertyChanged();
            NotifyConfigChanged();
        }
    }

    public string KeepItemsText
    {
        get => string.Join(Environment.NewLine, Policy.KeepItems);
        set
        {
            var normalized = ParseNames(value);
            if (SequenceEqual(Policy.KeepItems, normalized))
                return;

            Policy.KeepItems = normalized;
            _service.Current.KeepItems = normalized.ToList();
            var protectedNames = normalized.ToHashSet(StringComparer.OrdinalIgnoreCase);
            Policy.AllowedSellItems.RemoveAll(protectedNames.Contains);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AllowedSellItemsText));
            NotifyConfigChanged();
        }
    }

    private void NotifyConfigChanged()
    {
        _isLocalChange = true;
        try
        {
            _service.NotifyChanged();
        }
        finally
        {
            _isLocalChange = false;
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(EnabledIntent));
        OnPropertyChanged(nameof(SelectedMode));
        OnPropertyChanged(nameof(IsReturnToCity));
        OnPropertyChanged(nameof(SelectedCity));
        OnPropertyChanged(nameof(AllowedSellItemsText));
        OnPropertyChanged(nameof(KeepItemsText));
    }

    private static List<string> ParseNames(string? value) =>
        (value ?? string.Empty)
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(1000)
        .ToList();

    private static bool SequenceEqual(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count
        && left.Zip(right).All(pair => pair.First.Equals(pair.Second, StringComparison.Ordinal));
}

public sealed class MaterialProcessingPageViewModel : PageViewModel
{
    private readonly IMaterialCraftConfigService _service;
    private readonly IAutomationController? _controller;
    private bool _isLocalChange;

    public MaterialProcessingPageViewModel(
        IMaterialCraftConfigService service,
        IAutomationController? controller)
    {
        _service = service;
        _controller = controller;
        _service.Changed += (_, _) =>
        {
            if (!_isLocalChange)
                UiThread.Dispatch(Refresh);
        };
        if (_controller is not null)
            _controller.StateChanged += (_, _) => UiThread.Dispatch(Refresh);
    }

    public override string Title => "材料加工";
    public string Status => _controller?.RunState switch
    {
        RunState.Running => "运行中",
        RunState.Paused => "已暂停",
        RunState.Error => "异常",
        RunState.Stopped => "已停止",
        null => "未就绪",
        _ => "可启动"
    };
    public bool CanStart => _controller is not null;
    public bool CanEnableIntent => _controller is not null;

    public bool EnabledIntent
    {
        get => _service.Current.EnabledIntent;
        set
        {
            if (_service.Current.EnabledIntent == value)
                return;

            _service.Current.EnabledIntent = value;
            OnPropertyChanged();
            NotifyConfigChanged();
        }
    }

    public int ProcessingCycleMinutes
    {
        get => _service.Current.ProcessingCycleMinutes;
        set
        {
            var normalized = Math.Clamp(value <= 0 ? 30 : value, 1, 1440);
            if (_service.Current.ProcessingCycleMinutes == normalized)
                return;

            _service.Current.ProcessingCycleMinutes = normalized;
            OnPropertyChanged();
            NotifyConfigChanged();
        }
    }

    private void NotifyConfigChanged()
    {
        _isLocalChange = true;
        try
        {
            _service.NotifyChanged();
        }
        finally
        {
            _isLocalChange = false;
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanEnableIntent));
        OnPropertyChanged(nameof(EnabledIntent));
        OnPropertyChanged(nameof(ProcessingCycleMinutes));
    }
}
