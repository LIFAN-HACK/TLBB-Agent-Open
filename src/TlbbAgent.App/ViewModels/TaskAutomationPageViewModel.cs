using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TlbbAgent.Contracts;

namespace TlbbAgent.App.ViewModels;

public sealed class TaskAutomationItemViewModel : ObservableObject
{
    private readonly DailyTaskPlanItem _model;
    private readonly Action _onChanged;

    public TaskAutomationItemViewModel(DailyTaskPlanItem model, Action onChanged)
    {
        _model = model;
        _onChanged = onChanged;
    }

    public string Key => _model.Key;
    public int Order => _model.Order;
    public string Number => _model.Order.ToString("00");
    public string Name => _model.Key switch
    {
        DailyTaskKeys.School => "师门任务",
        DailyTaskKeys.GuildContribution => "帮贡任务",
        DailyTaskKeys.NewButterflyDream => "新鸳鸯蝴蝶梦",
        DailyTaskKeys.FaceChanging => "百变脸谱",
        DailyTaskKeys.ThousandAndOneWishes => "一千零一个愿望",
        _ => _model.Key
    };

    public bool Enabled
    {
        get => _model.Enabled;
        set
        {
            if (_model.Enabled == value)
                return;

            _model.Enabled = value;
            OnPropertyChanged();
            _onChanged();
        }
    }

    internal DailyTaskPlanItem Model => _model;

    internal void SetOrder(int order)
    {
        if (_model.Order == order)
            return;

        _model.Order = order;
        OnPropertyChanged(nameof(Order));
        OnPropertyChanged(nameof(Number));
    }
}

public sealed class TaskAutomationPageViewModel : PageViewModel
{
    private readonly IGlobalConfigService _config;
    private DailyTaskPlan? _boundPlan;

    public TaskAutomationPageViewModel(IGlobalConfigService config)
    {
        _config = config;
        RebuildTasks();
        _config.Changed += (_, _) => UiThread.Dispatch(SyncFromConfig);
    }

    public override string Title => "日常任务";
    public string Status => "未就绪";
    public bool CanStart => false;
    public ObservableCollection<TaskAutomationItemViewModel> Tasks { get; } = new();

    public bool AllowCombat
    {
        get => Policy.AllowCombat;
        set => SetPolicyValue(Policy.AllowCombat, value, updated => Policy.AllowCombat = updated, nameof(AllowCombat));
    }

    public bool AllowDelivery
    {
        get => Policy.AllowDelivery;
        set => SetPolicyValue(Policy.AllowDelivery, value, updated => Policy.AllowDelivery = updated, nameof(AllowDelivery));
    }

    public bool AllowPetCapture
    {
        get => Policy.AllowPetCapture;
        set => SetPolicyValue(Policy.AllowPetCapture, value, updated => Policy.AllowPetCapture = updated, nameof(AllowPetCapture));
    }

    public bool AllowItemPurchase
    {
        get => Policy.AllowItemPurchase;
        set => SetPolicyValue(Policy.AllowItemPurchase, value, updated => Policy.AllowItemPurchase = updated, nameof(AllowItemPurchase));
    }

    public void MoveTask(TaskAutomationItemViewModel source, TaskAutomationItemViewModel? target)
    {
        var oldIndex = Tasks.IndexOf(source);
        var newIndex = target is null ? Tasks.Count - 1 : Tasks.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex)
            return;

        Tasks.Move(oldIndex, newIndex);
        Plan.Items.RemoveAt(oldIndex);
        Plan.Items.Insert(newIndex, source.Model);
        Reindex();
        _config.NotifyChanged();
    }

    private DailyTaskPlan Plan => _config.Current.DailyTaskPlan;
    private SchoolTaskTypePolicy Policy => Plan.SchoolTaskPolicy;

    private void SetPolicyValue(bool current, bool value, Action<bool> update, string propertyName)
    {
        if (current == value)
            return;

        update(value);
        OnPropertyChanged(propertyName);
        _config.NotifyChanged();
    }

    private void SyncFromConfig()
    {
        var models = Plan.Items;
        var requiresRebuild = !ReferenceEquals(_boundPlan, Plan)
                              || Tasks.Count != models.Count
                              || Tasks.Where((task, index) => !ReferenceEquals(task.Model, models[index])).Any();
        if (requiresRebuild)
            RebuildTasks();

        OnPropertyChanged(nameof(AllowCombat));
        OnPropertyChanged(nameof(AllowDelivery));
        OnPropertyChanged(nameof(AllowPetCapture));
        OnPropertyChanged(nameof(AllowItemPurchase));
    }

    private void RebuildTasks()
    {
        _boundPlan = Plan;
        Tasks.Clear();
        foreach (var model in Plan.Items.OrderBy(item => item.Order))
            Tasks.Add(new TaskAutomationItemViewModel(model, _config.NotifyChanged));
        Reindex();
    }

    private void Reindex()
    {
        for (var index = 0; index < Tasks.Count; index++)
            Tasks[index].SetOrder(index + 1);
    }
}
