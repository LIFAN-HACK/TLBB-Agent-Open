using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TlbbAgent.Contracts;

namespace TlbbAgent.App.ViewModels;

public sealed record ActivityPointModeOption(ActivityPointSelectionMode Value, string Display)
{
    public override string ToString() => Display;
}

public sealed class ActivityFeatureItemViewModel : ObservableObject
{
    private readonly ActivityFeaturePreference _preference;
    private readonly Action _onChanged;
    private bool _isBackendReady;
    private string _status = "未就绪";

    public ActivityFeatureItemViewModel(
        string key,
        string name,
        ActivityFeaturePreference preference,
        IReadOnlyList<ActivityPointModeOption> pointModeOptions,
        Action onChanged)
    {
        Key = key;
        Name = name;
        _preference = preference;
        PointModeOptions = pointModeOptions;
        _onChanged = onChanged;
    }

    public string Key { get; }
    public string Name { get; }
    public IReadOnlyList<ActivityPointModeOption> PointModeOptions { get; }

    public bool Enabled
    {
        get => _preference.Enabled;
        set
        {
            if (!_isBackendReady || _preference.Enabled == value)
                return;

            _preference.Enabled = value;
            OnPropertyChanged();
            _onChanged();
        }
    }

    public ActivityPointSelectionMode PointSelectionMode
    {
        get => _preference.PointSelectionMode;
        set
        {
            if (_preference.PointSelectionMode == value || !Enum.IsDefined(value))
                return;

            _preference.PointSelectionMode = value;
            OnPropertyChanged();
            _onChanged();
        }
    }

    public bool IsBackendReady
    {
        get => _isBackendReady;
        private set => SetProperty(ref _isBackendReady, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public void ApplyBackendStatus(bool isReady, string? status)
    {
        IsBackendReady = isReady;
        Status = isReady && !string.IsNullOrWhiteSpace(status)
            ? status.Trim()
            : isReady ? "可用" : "未就绪";
        OnPropertyChanged(nameof(Enabled));
    }
}

public sealed class ActivityCenterPageViewModel : PageViewModel
{
    private static readonly IReadOnlyList<ActivityPointModeOption> PointModeOptions =
    [
        new(ActivityPointSelectionMode.Automatic, "自动找点"),
        new(ActivityPointSelectionMode.ManualArrival, "手动带到刷新点")
    ];

    private readonly IGlobalConfigService? _service;
    private readonly Action _onChanged;

    public ActivityCenterPageViewModel()
        : this(new ActivityCenterConfig(), static () => { })
    {
    }

    public ActivityCenterPageViewModel(IGlobalConfigService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _onChanged = service.NotifyChanged;
        Config = ActivityCenterConfigNormalizer.Normalize(service.Current.ActivityCenter);
        RebuildActivities();
        service.Changed += (_, _) => UiThread.Dispatch(SyncFromConfig);
    }

    public ActivityCenterPageViewModel(ActivityCenterConfig config, Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        _onChanged = onChanged;
        Config = ActivityCenterConfigNormalizer.Normalize(config);
        RebuildActivities();
    }

    public override string Title => "活动中心";
    public ActivityCenterConfig Config { get; private set; }
    public ObservableCollection<ActivityFeatureItemViewModel> Activities { get; } = new();

    public void ApplyBackendStatus(string featureKey, bool isReady, string? status = null)
    {
        Activities.FirstOrDefault(item => item.Key == featureKey)?.ApplyBackendStatus(isReady, status);
    }

    private void SyncFromConfig()
    {
        if (_service is null || ReferenceEquals(Config, _service.Current.ActivityCenter))
            return;

        Config = ActivityCenterConfigNormalizer.Normalize(_service.Current.ActivityCenter);
        RebuildActivities();
    }

    private void RebuildActivities()
    {
        Activities.Clear();
        Activities.Add(CreateItem(ActivityFeatureKeys.Rebel, "自动反贼", Config.Rebel, _onChanged));
        Activities.Add(CreateItem(ActivityFeatureKeys.SmallNpc, "自动宵小", Config.SmallNpc, _onChanged));
        Activities.Add(CreateItem(ActivityFeatureKeys.Monk, "自动和尚", Config.Monk, _onChanged));
    }

    private static ActivityFeatureItemViewModel CreateItem(
        string key,
        string name,
        ActivityFeaturePreference preference,
        Action onChanged) =>
        new(key, name, preference, PointModeOptions, onChanged);
}
