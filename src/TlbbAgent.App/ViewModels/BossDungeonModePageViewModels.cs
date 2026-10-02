using CommunityToolkit.Mvvm.Input;
using TlbbAgent.Contracts;

namespace TlbbAgent.App.ViewModels;

public sealed class BossModePageViewModel : PageViewModel
{
    private readonly IGlobalConfigService _global;
    private readonly ITargetPolicyCatalogService _catalog;

    public BossModePageViewModel(
        IGlobalConfigService global,
        ITargetPolicyCatalogService catalog,
        Action openRoleSettings)
    {
        _global = global;
        _catalog = catalog;
        ManagePolicyCommand = new RelayCommand(openRoleSettings);
        _catalog.MergeInto(_global.Current.Targeting.Policy, TargetPolicyCategoryKind.Boss);
        _global.Changed += (_, _) => UiThread.Dispatch(Refresh);
    }

    public override string Title => "BOSS 模式";
    public bool IsBackendReady => true;
    public string Status => "已接入统一目标策略";
    public bool CanManageWhitelist => true;
    public IRelayCommand ManagePolicyCommand { get; }

    public string WhitelistSummary
    {
        get
        {
            var category = BossCategory;
            var allowed = category.Entries.Count(entry => entry.Enabled && entry.AllowAttack);
            var denied = category.Entries.Count(entry => entry.Enabled && !entry.AllowAttack);
            return $"允许 {allowed} · 禁止 {denied}";
        }
    }

    public bool IsModeEnabled
    {
        get => BossCategory.Enabled && _global.Current.Targeting.Policy.Enabled;
        set
        {
            var category = BossCategory;
            if (category.Enabled == value && _global.Current.Targeting.Policy.Enabled == value)
                return;

            category.Enabled = value;
            _global.Current.Targeting.Policy.Enabled = value;
            if (value)
            {
                _global.Current.Targeting.L2TargetNameEnabled = true;
                _global.Current.Targeting.SwitchMode = TargetSwitchMode.BossWhitelist;
            }
            else if (_global.Current.Targeting.SwitchMode == TargetSwitchMode.BossWhitelist)
                _global.Current.Targeting.SwitchMode = TargetSwitchMode.KillThenSwitch;
            Refresh();
            _global.NotifyChanged();
        }
    }

    private TargetPolicyCategoryConfig BossCategory =>
        _global.Current.Targeting.Policy.Categories.First(category => category.Category == TargetPolicyCategoryKind.Boss);

    private void Refresh()
    {
        OnPropertyChanged(nameof(IsModeEnabled));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(WhitelistSummary));
    }
}

public sealed class DungeonModePageViewModel : PageViewModel
{
    public override string Title => "副本模式";
    public string Status => "待接入副本流程";
    public string CurrentDungeon => "-";
    public string CurrentStage => "-";
}
