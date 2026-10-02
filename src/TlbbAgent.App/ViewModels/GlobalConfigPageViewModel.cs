using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TlbbAgent.Contracts;

namespace TlbbAgent.App.ViewModels;

public sealed record HealthRecoveryModeOption(PlayerHealthRecoveryMode Value, string Display)
{
    public override string ToString() => Display;
}

public sealed record ManaRecoveryModeOption(PlayerManaRecoveryMode Value, string Display)
{
    public override string ToString() => Display;
}

public sealed record SkillKindOption(SkillKind Value, string Display)
{
    public override string ToString() => Display;
}

public sealed class RoleSkillPresetViewModel : ObservableObject
{
    private readonly SkillPreset _model;
    private readonly Action _changed;

    public RoleSkillPresetViewModel(SkillPreset model, Action changed)
    {
        _model = model;
        _changed = changed;
    }

    public string Id => _model.Id;

    public int Priority => _model.Priority;

    public string OrderText => _model.Priority.ToString();

    public bool Enabled
    {
        get => _model.Enabled;
        set
        {
            if (_model.Enabled == value) return;
            _model.Enabled = value;
            OnPropertyChanged();
            _changed();
        }
    }

    public string Name
    {
        get => _model.Name;
        set
        {
            var next = value?.Trim() ?? string.Empty;
            if (_model.Name == next) return;
            _model.Name = next;
            OnPropertyChanged();
            _changed();
        }
    }

    public IReadOnlyList<SkillKindOption> KindOptions { get; } =
    [
        new(SkillKind.Damage, "伤害"),
        new(SkillKind.Aoe, "范围"),
        new(SkillKind.Buff, "增益")
    ];

    public SkillKindOption SelectedKind
    {
        get => KindOptions.First(option => option.Value == _model.Kind);
        set
        {
            if (value is null || _model.Kind == value.Value) return;
            _model.Kind = value.Value;
            _model.Condition = value.Value == SkillKind.Buff ? SkillCondition.Always : SkillCondition.TargetExists;
            OnPropertyChanged();
            _changed();
        }
    }

    public string Hotkey
    {
        get => _model.Hotkey;
        set
        {
            var next = value?.Trim() ?? string.Empty;
            if (_model.Hotkey == next) return;
            _model.Hotkey = next;
            OnPropertyChanged();
            _changed();
        }
    }

    public double IntervalSeconds
    {
        get => Math.Round(Math.Max(0, _model.IntervalMs) / 1000.0, 1);
        set
        {
            var next = (int)Math.Round(Math.Clamp(value, 0.2, 3600) * 1000);
            if (_model.IntervalMs == next) return;
            _model.IntervalMs = next;
            OnPropertyChanged();
            _changed();
        }
    }

    internal SkillPreset Model => _model;

    internal void SetPriority(int priority)
    {
        if (_model.Priority == priority)
            return;

        _model.Priority = priority;
        OnPropertyChanged(nameof(Priority));
        OnPropertyChanged(nameof(OrderText));
    }
}

public sealed class TargetPolicyEntryViewModel : ObservableObject
{
    private readonly TargetPolicyEntry _model;
    private readonly Action _changed;

    public TargetPolicyEntryViewModel(TargetPolicyEntry model, Action changed)
    {
        _model = model;
        _changed = changed;
    }

    public string Id => _model.Id;
    public bool BuiltIn => _model.BuiltIn;
    public string SourceText => string.IsNullOrWhiteSpace(_model.GroupPath) ? _model.Source : _model.GroupPath;

    public bool Enabled
    {
        get => _model.Enabled;
        set => Set(_model.Enabled, value, next => _model.Enabled = next);
    }

    public bool AllowAttack
    {
        get => _model.AllowAttack;
        set => Set(_model.AllowAttack, value, next => _model.AllowAttack = next);
    }

    public string Name
    {
        get => _model.Name;
        set
        {
            var next = value?.Trim() ?? string.Empty;
            if (_model.Name == next) return;
            _model.Name = next;
            OnPropertyChanged();
            _changed();
        }
    }

    internal TargetPolicyEntry Model => _model;

    private void Set(bool current, bool value, Action<bool> apply)
    {
        if (current == value) return;
        apply(value);
        OnPropertyChanged();
        _changed();
    }
}

public sealed class TargetPolicyCategoryViewModel : ObservableObject
{
    private readonly TargetPolicyCategoryConfig _model;
    private readonly UnifiedTargetPolicyConfig _policy;
    private readonly Action _changed;

    public TargetPolicyCategoryViewModel(
        TargetPolicyCategoryConfig model,
        UnifiedTargetPolicyConfig policy,
        Action changed)
    {
        _model = model;
        _policy = policy;
        _changed = changed;
        Display = model.Category switch
        {
            TargetPolicyCategoryKind.Boss => "BOSS",
            TargetPolicyCategoryKind.Player => "玩家",
            _ => "普通怪物"
        };
        Hint = model.Category == TargetPolicyCategoryKind.Player
            ? "玩家目标默认保护，仅在明确授权条件下攻击。"
            : "管理允许或禁止攻击的目标。";
        AddEntryCommand = new RelayCommand(AddEntry);
        RemoveEntryCommand = new RelayCommand<TargetPolicyEntryViewModel>(RemoveEntry);
        RebuildEntries();
    }

    public string Display { get; }
    public string Hint { get; }
    public ObservableCollection<TargetPolicyEntryViewModel> Entries { get; } = new();
    public IRelayCommand AddEntryCommand { get; }
    public IRelayCommand<TargetPolicyEntryViewModel> RemoveEntryCommand { get; }
    public int EnabledEntryCount => Entries.Count(entry => entry.Enabled);
    public int UserEntryCount => Entries.Count(entry => !entry.BuiltIn);
    public string Summary => $"{EnabledEntryCount} / {Entries.Count} 启用";

    public bool Enabled
    {
        get => _model.Enabled;
        set => Set(_model.Enabled, value, next => _model.Enabled = next);
    }

    public bool AllowUnlisted
    {
        get => _model.AllowUnlisted;
        set => Set(_model.AllowUnlisted, value, next => _model.AllowUnlisted = next);
    }

    private void AddEntry()
    {
        var model = new TargetPolicyEntry { Name = "新目标", Enabled = true, AllowAttack = true };
        _model.Entries.Add(model);
        Entries.Add(new TargetPolicyEntryViewModel(model, NotifyEntryChanged));
        NotifyEntryChanged();
    }

    private void RemoveEntry(TargetPolicyEntryViewModel? entry)
    {
        if (entry is null || !_model.Entries.Remove(entry.Model))
            return;

        if (entry.BuiltIn
            && !string.IsNullOrWhiteSpace(entry.Id)
            && !_policy.SuppressedBuiltInEntryIds.Contains(entry.Id, StringComparer.OrdinalIgnoreCase))
        {
            _policy.SuppressedBuiltInEntryIds.Add(entry.Id);
        }

        Entries.Remove(entry);
        NotifyEntryChanged();
    }

    private void RebuildEntries()
    {
        Entries.Clear();
        foreach (var entry in _model.Entries)
            Entries.Add(new TargetPolicyEntryViewModel(entry, NotifyEntryChanged));
        OnPropertyChanged(nameof(EnabledEntryCount));
        OnPropertyChanged(nameof(UserEntryCount));
        OnPropertyChanged(nameof(Summary));
    }

    private void NotifyEntryChanged()
    {
        OnPropertyChanged(nameof(EnabledEntryCount));
        OnPropertyChanged(nameof(UserEntryCount));
        OnPropertyChanged(nameof(Summary));
        _changed();
    }

    private void Set(bool current, bool value, Action<bool> apply)
    {
        if (current == value) return;
        apply(value);
        OnPropertyChanged();
        _changed();
    }
}

public sealed partial class GlobalConfigPageViewModel : PageViewModel
{
    private readonly IGlobalConfigService _global;
    private GlobalConfig? _boundConfig;

    public GlobalConfigPageViewModel(IGlobalConfigService global)
    {
        _global = global;
        _selectedSchool = Schools.First(option => option.Value == Current.School);
        RebuildSkills();
        RebuildTargetPolicy();
        _global.Changed += (_, _) => UiThread.Dispatch(RefreshFromConfig);
    }

    public override string Title => "角色设置";
    public override string Subtitle => string.Empty;

    private GlobalConfig Current => _global.Current;

    public IReadOnlyList<SchoolOption> Schools { get; } = new[]
    {
        new SchoolOption(School.None, "未选择"),
        new SchoolOption(School.Shaolin, "少林"),
        new SchoolOption(School.Wudang, "武当"),
        new SchoolOption(School.Emei, "峨眉"),
        new SchoolOption(School.Gaibang, "丐帮"),
        new SchoolOption(School.Xiaoyao, "逍遥"),
        new SchoolOption(School.Tianshan, "天山"),
        new SchoolOption(School.Mingjiao, "明教"),
        new SchoolOption(School.Xingxiu, "星宿"),
        new SchoolOption(School.Tangmen, "唐门"),
        new SchoolOption(School.Murong, "慕容")
    };

    public IReadOnlyList<HealthRecoveryModeOption> HealthRecoveryModes { get; } = new[]
    {
        new HealthRecoveryModeOption(PlayerHealthRecoveryMode.Medicine, "药品回血"),
        new HealthRecoveryModeOption(PlayerHealthRecoveryMode.Symbiosis, "共生回血")
    };

    public IReadOnlyList<ManaRecoveryModeOption> ManaRecoveryModes { get; } = new[]
    {
        new ManaRecoveryModeOption(PlayerManaRecoveryMode.Medicine, "药品回蓝"),
        new ManaRecoveryModeOption(PlayerManaRecoveryMode.BloodSacrifice, "血迹回蓝")
    };

    public ObservableCollection<RoleSkillPresetViewModel> Skills { get; } = new();
    public ObservableCollection<TargetPolicyCategoryViewModel> TargetPolicyCategories { get; } = new();

    public bool IsTargetPolicyEnabled
    {
        get => Current.Targeting.Policy.Enabled;
        set
        {
            if (Current.Targeting.Policy.Enabled == value)
                return;

            Current.Targeting.Policy.Enabled = value;
            if (value)
                Current.Targeting.L2TargetNameEnabled = true;
            OnPropertyChanged();
            _global.NotifyChanged();
        }
    }

    [ObservableProperty]
    private SchoolOption _selectedSchool;

    [ObservableProperty]
    private bool _isSkillEditorOpen;

    [ObservableProperty]
    private bool _isTargetPolicyManagerOpen;

    partial void OnSelectedSchoolChanged(SchoolOption value)
    {
        if (value is not null && Current.School != value.Value)
            _global.SetSchool(value.Value);
    }

    partial void OnIsSkillEditorOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(SkillEditorButtonText));
        OnPropertyChanged(nameof(ShowCustomSkillEditor));
    }

    public string SkillEditorButtonText => IsSkillEditorOpen ? "收起" : "管理";
    public string SkillSummary => Current.SkillCooldownRecognitionEnabled
        ? $"AI 已导入 {Current.AiSkills.Count(skill => skill.Enabled && !string.IsNullOrWhiteSpace(skill.ExpectedVisualSkillId))} 个"
        : $"自设已启用 {Current.Skills.Count(skill => skill.Enabled)} / {Current.Skills.Count}";
    public string SkillModeText => Current.SkillCooldownRecognitionEnabled ? "AI 自动判定" : "自设按键";
    public bool IsAiSkillMode => Current.SkillCooldownRecognitionEnabled;
    public bool IsCustomSkillMode => !IsAiSkillMode;
    public bool ShowCustomSkillEditor => IsCustomSkillMode && IsSkillEditorOpen;

    public IReadOnlyList<SkillExecutionModeOption> SkillModes { get; } =
    [
        new(true, "AI 自动判定"),
        new(false, "自设按键")
    ];

    public SkillExecutionModeOption SelectedSkillMode
    {
        get => SkillModes.First(mode => mode.UseAi == Current.SkillCooldownRecognitionEnabled);
        set
        {
            if (value is null || Current.SkillCooldownRecognitionEnabled == value.UseAi)
                return;

            Current.SkillCooldownRecognitionEnabled = value.UseAi;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SkillModeText));
            OnPropertyChanged(nameof(SkillSummary));
            OnPropertyChanged(nameof(IsAiSkillMode));
            OnPropertyChanged(nameof(IsCustomSkillMode));
            OnPropertyChanged(nameof(ShowCustomSkillEditor));
            _global.NotifyChanged();
        }
    }

    public string TargetPolicySummary
    {
        get
        {
            var categories = TargetPolicyCategories;
            var total = categories.Sum(category => category.Entries.Count);
            var enabled = categories.Sum(category => category.EnabledEntryCount);
            return total == 0 ? "暂无名单" : $"{enabled} / {total} 已启用";
        }
    }

    public HealthRecoveryModeOption SelectedHealthRecoveryMode
    {
        get => HealthRecoveryModes.First(option => option.Value == Current.HealthRecoveryMode);
        set
        {
            if (value is null || Current.HealthRecoveryMode == value.Value)
                return;

            Current.HealthRecoveryMode = value.Value;
            Current.RecoveryModesExplicitlyConfigured = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsMedicineHealthRecovery));
            OnPropertyChanged(nameof(HealthRecoveryHint));
            _global.NotifyChanged();
        }
    }

    public ManaRecoveryModeOption SelectedManaRecoveryMode
    {
        get => ManaRecoveryModes.First(option => option.Value == Current.ManaRecoveryMode);
        set
        {
            if (value is null || Current.ManaRecoveryMode == value.Value)
                return;

            Current.ManaRecoveryMode = value.Value;
            Current.RecoveryModesExplicitlyConfigured = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsMedicineManaRecovery));
            OnPropertyChanged(nameof(ManaRecoveryHint));
            _global.NotifyChanged();
        }
    }

    public bool IsMedicineHealthRecovery => Current.HealthRecoveryMode == PlayerHealthRecoveryMode.Medicine;
    public bool IsMedicineManaRecovery => Current.ManaRecoveryMode == PlayerManaRecoveryMode.Medicine;
    public string HealthRecoveryHint => IsMedicineHealthRecovery ? "使用药品快捷键" : "点击宠物共生技能";
    public string ManaRecoveryHint => IsMedicineManaRecovery ? "使用药品快捷键" : "点击宠物血迹技能";

    public string AcquireHotkey
    {
        get => Current.Targeting.AcquireHotkey;
        set => SetText(Current.Targeting.AcquireHotkey, value, next => Current.Targeting.AcquireHotkey = next);
    }

    public string GameBuiltInHangSwitchHotkey
    {
        get => Current.GameBuiltInHangSwitchHotkey;
        set => SetText(Current.GameBuiltInHangSwitchHotkey, value, next => Current.GameBuiltInHangSwitchHotkey = next);
    }

    public bool YieldToManualInput
    {
        get => Current.YieldToManualInput;
        set => SetValue(Current.YieldToManualInput, value, next => Current.YieldToManualInput = next);
    }

    public double ManualInputYieldSeconds
    {
        get => Math.Round(Current.ManualInputYieldMs / 1000.0, 1);
        set
        {
            var milliseconds = (int)Math.Round(Math.Clamp(value, 0.5, 60) * 1000);
            SetValue(Current.ManualInputYieldMs, milliseconds, next => Current.ManualInputYieldMs = next);
        }
    }

    public string AttackHotkey
    {
        get => Current.AttackHotkey;
        set => SetText(Current.AttackHotkey, value, next => Current.AttackHotkey = next);
    }

    public string HealHotkey
    {
        get => Current.HealHotkey;
        set => SetText(Current.HealHotkey, value, next => Current.HealHotkey = next);
    }

    public string ManaHotkey
    {
        get => Current.ManaHotkey;
        set => SetText(Current.ManaHotkey, value, next => Current.ManaHotkey = next);
    }

    public string PetHealHotkey
    {
        get => Current.PetHealHotkey;
        set => SetText(Current.PetHealHotkey, value, next => Current.PetHealHotkey = next);
    }

    public string PetFoodHotkey
    {
        get => Current.PetFoodHotkey;
        set => SetText(Current.PetFoodHotkey, value, next => Current.PetFoodHotkey = next);
    }

    public string BloodManaHotkey
    {
        get => Current.BloodManaHotkey;
        set => SetText(Current.BloodManaHotkey, value, next => Current.BloodManaHotkey = next);
    }

    public string SymbiosisHealHotkey
    {
        get => Current.SymbiosisHealHotkey;
        set => SetText(Current.SymbiosisHealHotkey, value, next => Current.SymbiosisHealHotkey = next);
    }

    public int HpThresholdPct
    {
        get => Current.HpThresholdPct;
        set => SetPercent(Current.HpThresholdPct, value, next => Current.HpThresholdPct = next);
    }

    public int MpThresholdPct
    {
        get => Current.MpThresholdPct;
        set => SetPercent(Current.MpThresholdPct, value, next => Current.MpThresholdPct = next);
    }

    public int PetHpThresholdPct
    {
        get => Current.PetHpThresholdPct;
        set => SetPercent(Current.PetHpThresholdPct, value, next => Current.PetHpThresholdPct = next);
    }

    public int PetFoodThresholdPct
    {
        get => Current.PetFoodThresholdPct;
        set => SetPercent(Current.PetFoodThresholdPct, value, next => Current.PetFoodThresholdPct = next);
    }

    public int BloodManaThresholdPct
    {
        get => Current.BloodManaThresholdPct;
        set => SetPercent(Current.BloodManaThresholdPct, value, next => Current.BloodManaThresholdPct = next);
    }

    public int SymbiosisHealThresholdPct
    {
        get => Current.SymbiosisHealThresholdPct;
        set => SetPercent(Current.SymbiosisHealThresholdPct, value, next => Current.SymbiosisHealThresholdPct = next);
    }

    [RelayCommand]
    private void ToggleSkillEditor() => IsSkillEditorOpen = !IsSkillEditorOpen;

    [RelayCommand]
    private void OpenTargetPolicyManager() => IsTargetPolicyManagerOpen = true;

    [RelayCommand]
    private void CloseTargetPolicyManager() => IsTargetPolicyManagerOpen = false;

    [RelayCommand]
    private void AddSkill()
    {
        var model = new SkillPreset
        {
            Name = $"技能{Current.Skills.Count + 1}",
            IntervalMs = 10000,
            Priority = Current.Skills.Count + 1,
            Kind = SkillKind.Damage,
            Condition = SkillCondition.Always
        };
        Current.Skills.Add(model);
        Skills.Add(new RoleSkillPresetViewModel(model, NotifySkillChanged));
        ReindexSkills();
        NotifySkillChanged();
    }

    [RelayCommand]
    private void RemoveSkill(RoleSkillPresetViewModel? skill)
    {
        if (skill is null || !Current.Skills.Remove(skill.Model))
            return;

        Skills.Remove(skill);
        ReindexSkills();
        NotifySkillChanged();
    }

    public void MoveSkill(RoleSkillPresetViewModel source, RoleSkillPresetViewModel? target)
    {
        var oldIndex = Skills.IndexOf(source);
        var newIndex = target is null ? Skills.Count - 1 : Skills.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex)
            return;

        Skills.Move(oldIndex, newIndex);
        Current.Skills.RemoveAt(oldIndex);
        Current.Skills.Insert(newIndex, source.Model);
        ReindexSkills();
        NotifySkillChanged();
    }

    private void ReindexSkills()
    {
        for (var index = 0; index < Skills.Count; index++)
            Skills[index].SetPriority(index + 1);
    }

    private void SetText(string current, string? value, Action<string> apply)
    {
        var next = value?.Trim() ?? string.Empty;
        if (current == next) return;
        apply(next);
        OnPropertyChanged();
        _global.NotifyChanged();
    }

    private void SetPercent(int current, int value, Action<int> apply) =>
        SetValue(current, Math.Clamp(value, 0, 100), apply);

    private void SetValue<T>(T current, T value, Action<T> apply) where T : IEquatable<T>
    {
        if (current.Equals(value)) return;
        apply(value);
        OnPropertyChanged();
        _global.NotifyChanged();
    }

    private void NotifySkillChanged()
    {
        OnPropertyChanged(nameof(SkillSummary));
        _global.NotifyChanged();
    }

    private void RebuildSkills()
    {
        Skills.Clear();
        var ordered = Current.Skills
            .Select((skill, index) => new { Skill = skill, Index = index })
            .OrderBy(item => item.Skill.Priority <= 0 ? int.MaxValue : item.Skill.Priority)
            .ThenBy(item => item.Index)
            .Select(item => item.Skill)
            .ToArray();

        Current.Skills.Clear();
        Current.Skills.AddRange(ordered);
        foreach (var skill in Current.Skills)
            Skills.Add(new RoleSkillPresetViewModel(skill, NotifySkillChanged));
        ReindexSkills();
        _boundConfig = Current;
        OnPropertyChanged(nameof(SkillSummary));
    }

    private void RebuildTargetPolicy()
    {
        TargetPolicyCategories.Clear();
        foreach (var category in Current.Targeting.Policy.Categories.OrderBy(item => item.Category))
        {
            TargetPolicyCategories.Add(new TargetPolicyCategoryViewModel(
                category,
                Current.Targeting.Policy,
                NotifyTargetPolicyChanged));
        }
        OnPropertyChanged(nameof(TargetPolicySummary));
    }

    private void NotifyTargetPolicyChanged()
    {
        OnPropertyChanged(nameof(IsTargetPolicyEnabled));
        OnPropertyChanged(nameof(TargetPolicySummary));
        _global.NotifyChanged();
    }

    private void RefreshFromConfig()
    {
        var school = Schools.First(option => option.Value == Current.School);
        if (!Equals(SelectedSchool, school))
            SelectedSchool = school;

        if (!ReferenceEquals(_boundConfig, Current)
            || !Skills.Select(skill => skill.Id).SequenceEqual(Current.Skills.Select(skill => skill.Id)))
        {
            RebuildSkills();
            RebuildTargetPolicy();
        }

        OnPropertyChanged(nameof(AcquireHotkey));
        OnPropertyChanged(nameof(GameBuiltInHangSwitchHotkey));
        OnPropertyChanged(nameof(YieldToManualInput));
        OnPropertyChanged(nameof(ManualInputYieldSeconds));
        OnPropertyChanged(nameof(AttackHotkey));
        OnPropertyChanged(nameof(SelectedHealthRecoveryMode));
        OnPropertyChanged(nameof(SelectedManaRecoveryMode));
        OnPropertyChanged(nameof(IsMedicineHealthRecovery));
        OnPropertyChanged(nameof(IsMedicineManaRecovery));
        OnPropertyChanged(nameof(HealthRecoveryHint));
        OnPropertyChanged(nameof(ManaRecoveryHint));
        OnPropertyChanged(nameof(HealHotkey));
        OnPropertyChanged(nameof(ManaHotkey));
        OnPropertyChanged(nameof(PetHealHotkey));
        OnPropertyChanged(nameof(PetFoodHotkey));
        OnPropertyChanged(nameof(BloodManaHotkey));
        OnPropertyChanged(nameof(SymbiosisHealHotkey));
        OnPropertyChanged(nameof(HpThresholdPct));
        OnPropertyChanged(nameof(MpThresholdPct));
        OnPropertyChanged(nameof(PetHpThresholdPct));
        OnPropertyChanged(nameof(PetFoodThresholdPct));
        OnPropertyChanged(nameof(BloodManaThresholdPct));
        OnPropertyChanged(nameof(SymbiosisHealThresholdPct));
        OnPropertyChanged(nameof(SelectedSkillMode));
        OnPropertyChanged(nameof(IsAiSkillMode));
        OnPropertyChanged(nameof(IsCustomSkillMode));
        OnPropertyChanged(nameof(ShowCustomSkillEditor));
        OnPropertyChanged(nameof(SkillModeText));
        OnPropertyChanged(nameof(SkillSummary));
        OnPropertyChanged(nameof(IsTargetPolicyEnabled));
        OnPropertyChanged(nameof(TargetPolicySummary));
    }
}
