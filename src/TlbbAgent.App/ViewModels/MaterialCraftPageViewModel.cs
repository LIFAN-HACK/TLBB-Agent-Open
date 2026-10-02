using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TlbbAgent.Contracts;

namespace TlbbAgent.App.ViewModels;

public sealed partial class MaterialCraftStepViewModel : ObservableObject
{
    private readonly MaterialCraftStep _model;
    private readonly Action _onChanged;

    public MaterialCraftStepViewModel(MaterialCraftStep model, Action onChanged)
    {
        _model = model;
        _onChanged = onChanged;
    }

    public string Id => _model.Id;
    public string Label { get => _model.Label; set { _model.Label = value; OnPropertyChanged(); _onChanged(); } }
    public MaterialCraftStepAction Action { get => _model.Action; set { _model.Action = value; OnPropertyChanged(); _onChanged(); } }
    public string MaterialName { get => _model.MaterialName; set { _model.MaterialName = value; OnPropertyChanged(); _onChanged(); } }
    public string RecipeName { get => _model.RecipeName; set { _model.RecipeName = value; OnPropertyChanged(); _onChanged(); } }
    public string Hotkey { get => _model.Hotkey; set { _model.Hotkey = value; OnPropertyChanged(); _onChanged(); } }
    public string Text { get => _model.Text; set { _model.Text = value; OnPropertyChanged(); _onChanged(); } }
    public int ClickX { get => _model.ClickX; set { _model.ClickX = value; OnPropertyChanged(); _onChanged(); } }
    public int ClickY { get => _model.ClickY; set { _model.ClickY = value; OnPropertyChanged(); _onChanged(); } }
    public int WaitMs { get => _model.WaitMs; set { _model.WaitMs = value; OnPropertyChanged(); _onChanged(); } }
    public int RetryLimit { get => _model.RetryLimit; set { _model.RetryLimit = value; OnPropertyChanged(); _onChanged(); } }
}

public sealed partial class MaterialCraftPageViewModel : PageViewModel
{
    private readonly IMaterialCraftConfigService _service;
    private readonly ILogService _log;
    private bool _isLocalChange;

    public MaterialCraftPageViewModel(
        ModuleDescriptor descriptor,
        IAutomationController? controller,
        IMaterialCraftConfigService service,
        ILogService log)
    {
        Descriptor = descriptor;
        Controller = controller;
        _service = service;
        _log = log;
        _service.Changed += (_, _) =>
        {
            if (!_isLocalChange)
                UiThread.Dispatch(Refresh);
        };
        SubscribeController();
        Refresh();
    }

    public ModuleDescriptor Descriptor { get; }
    public IAutomationController? Controller { get; }
    public override string Title => Descriptor.Title;
    public override string Subtitle => Descriptor.Description;

    public Array Actions { get; } = Enum.GetValues(typeof(MaterialCraftStepAction));
    public Array DispatchModes { get; } = Enum.GetValues(typeof(InputDispatchMode));
    public ObservableCollection<MaterialCraftStepViewModel> Steps { get; } = new();

    private MaterialCraftConfig C => _service.Current;
    public bool HasController => Controller is not null;
    public RunState RunState => Controller?.RunState ?? RunState.Idle;
    public string CurrentStateName => Controller?.CurrentStateName ?? "-";
    public string PlanSummary => $"流程 {Blank(C.PlanName)} / 步骤 {C.Steps.Count} / 批次 {C.BatchCount}";
    public string RunSummary => $"{(LoopPlan ? "循环" : "单轮")} / {(StopAfterBatch ? "批后停" : "批后继续")} / {DispatchModeText(DispatchMode)}";
    public string TimingSummary => $"步骤 {StepIntervalMs}ms / 重试 {MaxStepRetry}";
    public string CleanupSummary => EnableCleanupAfterBatch
        ? $"清包 {Blank(CleanupHotkey)} / 等待 {CleanupWaitMs}ms"
        : "清包关闭";
    public string ReadySummary
    {
        get
        {
            if (C.Steps.Count == 0)
                return "缺少流程";
            if (BatchCount <= 0)
                return "批次无效";
            if (EnableCleanupAfterBatch && string.IsNullOrWhiteSpace(CleanupHotkey))
                return "缺少清包键";
            return "可启动";
        }
    }
    public bool IsReady => ReadySummary == "可启动";

    public string ProfileName { get => C.ProfileName; set { C.ProfileName = value; OnPropertyChanged(); } }
    public string PlanName { get => C.PlanName; set { C.PlanName = value; OnConfigChanged(); } }
    public int BatchCount { get => C.BatchCount; set { C.BatchCount = value; OnConfigChanged(); } }
    public bool LoopPlan { get => C.LoopPlan; set { C.LoopPlan = value; OnConfigChanged(); } }
    public bool StopAfterBatch { get => C.StopAfterBatch; set { C.StopAfterBatch = value; OnConfigChanged(); } }
    public int StepIntervalMs { get => C.StepIntervalMs; set { C.StepIntervalMs = value; OnConfigChanged(); } }
    public int MaxStepRetry { get => C.MaxStepRetry; set { C.MaxStepRetry = value; OnConfigChanged(); } }
    public InputDispatchMode DispatchMode { get => C.DispatchMode; set { C.DispatchMode = value; OnConfigChanged(); } }
    public bool EnableCleanupAfterBatch { get => C.EnableCleanupAfterBatch; set { C.EnableCleanupAfterBatch = value; OnConfigChanged(); } }
    public string CleanupHotkey { get => C.CleanupHotkey; set { C.CleanupHotkey = value; OnConfigChanged(); } }
    public int CleanupClickX { get => C.CleanupClickX; set { C.CleanupClickX = value; OnConfigChanged(); } }
    public int CleanupClickY { get => C.CleanupClickY; set { C.CleanupClickY = value; OnConfigChanged(); } }
    public int CleanupWaitMs { get => C.CleanupWaitMs; set { C.CleanupWaitMs = value; OnConfigChanged(); } }

    [ObservableProperty] private string _operationStatus = "载入或添加流程。";
    [ObservableProperty] private MaterialCraftStepAction _newAction = MaterialCraftStepAction.Hotkey;
    [ObservableProperty] private string _newMaterialName = "";
    [ObservableProperty] private string _newRecipeName = "";
    [ObservableProperty] private string _newHotkey = "";
    [ObservableProperty] private string _newText = "";
    [ObservableProperty] private int _newClickX = 520;
    [ObservableProperty] private int _newClickY = 360;
    [ObservableProperty] private int _newWaitMs = 600;

    [RelayCommand]
    private void AddStep()
    {
        _service.AddStep(new MaterialCraftStep
        {
            Label = $"{C.Steps.Count + 1:D2}-{NewAction}",
            Action = NewAction,
            MaterialName = NewMaterialName.Trim(),
            RecipeName = NewRecipeName.Trim(),
            Hotkey = NewHotkey.Trim(),
            Text = NewText.Trim(),
            ClickX = NewClickX,
            ClickY = NewClickY,
            WaitMs = NewWaitMs,
            RetryLimit = C.MaxStepRetry,
            SourceModule = "手动",
            SourcePath = ""
        });

        OperationStatus = "已添加。";
    }

    [RelayCommand]
    private void RemoveStep(MaterialCraftStepViewModel? step)
    {
        if (step is null)
            return;

        if (_service.RemoveStep(step.Id))
            OperationStatus = "已删除。";
    }

    [RelayCommand]
    private void ClearSteps()
    {
        _service.Clear();
        OperationStatus = "已清空流程。";
    }

    [RelayCommand]
    private void ImportPlan()
    {
        var dialog = new OpenFileDialog
        {
            Title = "载入流程",
            Filter = "流程文件 (*.txt;*.csv;*.tsv)|*.txt;*.csv;*.tsv|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var result = _service.ImportPlainPlan(dialog.FileName, "配置");
            OperationStatus = result.Message;
        }
        catch
        {
            OperationStatus = "载入失败。";
            _log.Error("MaterialCraft", OperationStatus);
        }
    }

    [RelayCommand]
    private void Save()
    {
        var name = string.IsNullOrWhiteSpace(ProfileName) ? "默认加工" : ProfileName;
        _service.Save(name);
        OperationStatus = "已保存。";
    }

    [RelayCommand]
    private void Load()
    {
        var name = string.IsNullOrWhiteSpace(ProfileName) ? "默认加工" : ProfileName;
        OperationStatus = _service.Load(name) ? "已加载。" : "未找到。";
    }

    private void Refresh()
    {
        RebuildSteps();
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(PlanName));
        OnPropertyChanged(nameof(BatchCount));
        OnPropertyChanged(nameof(LoopPlan));
        OnPropertyChanged(nameof(StopAfterBatch));
        OnPropertyChanged(nameof(StepIntervalMs));
        OnPropertyChanged(nameof(MaxStepRetry));
        OnPropertyChanged(nameof(DispatchMode));
        OnPropertyChanged(nameof(EnableCleanupAfterBatch));
        OnPropertyChanged(nameof(CleanupHotkey));
        OnPropertyChanged(nameof(CleanupClickX));
        OnPropertyChanged(nameof(CleanupClickY));
        OnPropertyChanged(nameof(CleanupWaitMs));
        RefreshSummary();
    }

    private void RebuildSteps()
    {
        Steps.Clear();
        foreach (var step in _service.Steps.Take(240))
            Steps.Add(new MaterialCraftStepViewModel(step, NotifyConfigChanged));
        RefreshSummary();
    }

    private void OnConfigChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        OnPropertyChanged(propertyName);
        NotifyConfigChanged();
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
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(PlanSummary));
        OnPropertyChanged(nameof(RunSummary));
        OnPropertyChanged(nameof(TimingSummary));
        OnPropertyChanged(nameof(CleanupSummary));
        OnPropertyChanged(nameof(ReadySummary));
        OnPropertyChanged(nameof(IsReady));
    }

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string DispatchModeText(InputDispatchMode mode) => mode switch
    {
        InputDispatchMode.Adaptive => "自动兼容",
        InputDispatchMode.PostMessage => "后台",
        InputDispatchMode.SendInput => "前台",
        _ => mode.ToString()
    };

    private void SubscribeController()
    {
        if (Controller is null)
            return;

        Controller.StateChanged += (_, _) => UiThread.Dispatch(() =>
        {
            OnPropertyChanged(nameof(CurrentStateName));
            OnPropertyChanged(nameof(RunState));
        });
    }
}

