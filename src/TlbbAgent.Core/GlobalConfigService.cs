using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class GlobalConfigService : IGlobalConfigService
{
    private readonly IProfileService _profiles;
    private readonly ILogService _log;
    private const string Module = "GlobalConfig";
    private const int CurrentSchemaVersion = 29;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public GlobalConfigService(IProfileService profiles, ILogService log)
    {
        _profiles = profiles;
        _log = log;
        Current = CreateDefault();
    }

    public GlobalConfig Current { get; private set; }
    public event EventHandler? Changed;

    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public bool SchoolProvidesHeal(School school) => school == School.Emei;

    public void SetSchool(School school)
    {
        Current.School = school;

        if (RemoveCrossSchoolSkillPollution(Current))
            _log.Warn(Module, "已移除与当前门派不一致的 AI 技能绑定；同门派和通用技能已保留。");

        if (SchoolProvidesHeal(school))
        {
            if (!Current.EnableEmHeal)
            {
                Current.EnableEmHeal = true;
                _log.Info(Module, "Emei selected; heal assist enabled.");
            }
        }
        else
        {
            Current.EnableEmHeal = false;
        }

        NotifyChanged();
    }

    public void Save(string profileName)
    {
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        _profiles.Save(profileName, json);
        _log.Info(Module, "Config saved.");
    }

    public bool Load(string profileName)
    {
        var json = _profiles.Load(profileName);
        if (string.IsNullOrWhiteSpace(json)) return false;

        var sourceVersion = ReadSchemaVersion(json);
        var loaded = ImportJson(json, $"profile:{profileName}");
        if (loaded && sourceVersion < CurrentSchemaVersion)
        {
            _profiles.Save(profileName, JsonSerializer.Serialize(Current, JsonOptions));
            _log.Info(Module, $"Config migrated to schema {CurrentSchemaVersion}.");
        }
        if (loaded)
            _log.Info(Module, "Config loaded.");
        return loaded;
    }

    public void ExportToFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(Current, JsonOptions));
        _log.Info(Module, "Config exported.");
    }

    public bool ImportFromFile(string path)
    {
        if (!File.Exists(path))
        {
            _log.Warn(Module, "Config file not found.");
            return false;
        }

        var loaded = ImportJson(File.ReadAllText(path), path);
        if (loaded)
            _log.Info(Module, "Config imported.");
        return loaded;
    }

    private bool ImportJson(string json, string source)
    {
        try
        {
            var sourceVersion = ReadSchemaVersion(json);
            var cfg = JsonSerializer.Deserialize<GlobalConfig>(json);
            if (cfg is null) return false;

            NormalizeConfig(cfg, sourceVersion);
            var removedCrossSchoolSkills = RemoveCrossSchoolSkillPollution(cfg);
            Current = cfg;
            SetSchool(Current.School);
            if (removedCrossSchoolSkills)
                _log.Warn(Module, "加载配置时已移除跨门派 AI 技能绑定；未清空有效技能。");
            NotifyChanged();
            return true;
        }
        catch
        {
            _log.Error(Module, "Config load failed.");
            return false;
        }
    }

    private static GlobalConfig CreateDefault()
    {
        var config = new GlobalConfig
        {
            SchemaVersion = CurrentSchemaVersion,
            InputDispatchMode = InputDispatchMode.Adaptive,
            YieldToManualInput = true,
            ManualInputYieldMs = 800,
            CombatBackendPreference = CombatBackendPreference.VisualModel,
            GameBuiltInHangSwitchMode = GameBuiltInHangSwitchMode.Disabled,
            GameBuiltInHangSwitchHotkey = "",
            GameBuiltInHangSwitchClickX = 0,
            GameBuiltInHangSwitchClickY = 0,
            GameBuiltInHangSwitchCooldownMs = 3000,
            AttackHotkey = "",
            AttackIntervalMs = 1000,
            PublicCooldownMs = 300,
            SkillCooldownRecognitionEnabled = true,
            SkillCooldownProbeIntervalMs = 200,
            SkillCooldownReadyDebounceMs = 350,
            PillHealCooldownMs = 10000,
            PillManaCooldownMs = 10000,
            HealthRecoveryMode = PlayerHealthRecoveryMode.Symbiosis,
            ManaRecoveryMode = PlayerManaRecoveryMode.BloodSacrifice,
            PetDeployActivationMode = PetSkillActivationMode.Hotkey,
            PetDeployClickX = 0,
            PetDeployClickBottomOffset = 0,
            PetDeployCooldownMs = 15000,
            PetFoodCooldownMs = 250,
            BloodManaCooldownMs = 30000,
            SymbiosisHealCooldownMs = 30000,
            BloodManaActivationMode = PetSkillActivationMode.ClientClick,
            SymbiosisHealActivationMode = PetSkillActivationMode.ClientClick,
            Targeting =
            {
                SwitchMode = TargetSwitchMode.KillThenSwitch,
                Filter = TargetFilter.MonsterOnly,
                BlockPlayerTargets = true,
                AcquireHotkey = "",
                ScanIntervalMs = 500,
                AutoAcquireWhenNoTarget = true,
                AcquireIntervalMs = 800,
                TargetLostDelayMs = 600,
                NoDamageReacquireMs = 15000,
                ProtectedTargetClearHotkey = "",
                ProtectedTargetClearAfterScans = 3
            },
            Skills =
            {
                new SkillPreset { Name = "Skill 1", Hotkey = "", IntervalMs = 10000, Priority = 1, Kind = SkillKind.Damage, Enabled = true, Condition = SkillCondition.Always },
                new SkillPreset { Name = "Skill 2", Hotkey = "", IntervalMs = 10000, Priority = 2, Kind = SkillKind.Damage, Enabled = false, Condition = SkillCondition.Always },
                new SkillPreset { Name = "Skill 3", Hotkey = "", IntervalMs = 10000, Priority = 3, Kind = SkillKind.Damage, Enabled = false, Condition = SkillCondition.Always },
                new SkillPreset { Name = "Skill 4", Hotkey = "", IntervalMs = 10000, Priority = 4, Kind = SkillKind.Damage, Enabled = false, Condition = SkillCondition.Always },
                new SkillPreset { Name = "Skill 5", Hotkey = "", IntervalMs = 10000, Priority = 5, Kind = SkillKind.Damage, Enabled = false, Condition = SkillCondition.Always },
                new SkillPreset { Name = "Skill 6", Hotkey = "", IntervalMs = 10000, Priority = 6, Kind = SkillKind.Damage, Enabled = false, Condition = SkillCondition.Always },
                new SkillPreset { Name = "Skill 7", Hotkey = "", IntervalMs = 10000, Priority = 7, Kind = SkillKind.Damage, Enabled = false, Condition = SkillCondition.Always },
                new SkillPreset { Name = "Skill 8", Hotkey = "", IntervalMs = 10000, Priority = 8, Kind = SkillKind.Damage, Enabled = false, Condition = SkillCondition.Always },
                new SkillPreset { Name = "Skill 9", Hotkey = "", IntervalMs = 10000, Priority = 9, Kind = SkillKind.Damage, Enabled = false, Condition = SkillCondition.Always },
            }
        };

        EnsureDefaultSkillSlots(config);
        return config;
    }

    private static int ReadSchemaVersion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(nameof(GlobalConfig.SchemaVersion), out var version)
                   && version.TryGetInt32(out var value)
                ? value
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void NormalizeConfig(GlobalConfig config, int sourceVersion)
    {
        if (sourceVersion < CurrentSchemaVersion)
            MigrateLegacyConfig(config, sourceVersion);

        config.SchemaVersion = CurrentSchemaVersion;
        config.CombatRecognition ??= new CombatRecognitionConfig();
        config.Targeting ??= new TargetingPreset();
        NormalizeTargetPolicy(config.Targeting, sourceVersion < 25);
        if (config.CombatBackendPreference == CombatBackendPreference.Auto)
            config.CombatBackendPreference = CombatBackendPreference.VisualModel;
        if (config.CombatBackendPreference == CombatBackendPreference.InternalPlugin)
            config.GameBuiltInHangSwitchMode = GameBuiltInHangSwitchMode.Hotkey;
        NormalizeDailyTaskPlan(config);
        config.ActivityCenter = ActivityCenterConfigNormalizer.Normalize(config.ActivityCenter);
        config.Skills ??= new List<SkillPreset>();
        config.AiSkills ??= new List<SkillPreset>();

        // 普通挂机不再暴露自动切怪开关；所有加载路径统一启用打死换怪基础能力。
        config.Targeting.AutoAcquireWhenNoTarget = true;
        if (config.ManualInputYieldMs <= 0)
            config.ManualInputYieldMs = 800;
        else if (sourceVersion < 12 && config.ManualInputYieldMs == 1500)
            config.ManualInputYieldMs = 800;
        if (config.GameBuiltInHangSwitchCooldownMs <= 0)
            config.GameBuiltInHangSwitchCooldownMs = 3000;
        config.GameBuiltInHangSwitchHotkey = config.GameBuiltInHangSwitchHotkey?.Trim() ?? string.Empty;
        config.Targeting.AcquireHotkey = config.Targeting.AcquireHotkey?.Trim() ?? string.Empty;
        config.Targeting.NoDamageReacquireMs = Math.Clamp(
            config.Targeting.NoDamageReacquireMs <= 0 ? 15000 : config.Targeting.NoDamageReacquireMs,
            3000,
            120000);
        config.Targeting.ProtectedTargetClearHotkey = NormalizeProtectedTargetClearHotkey(config.Targeting.ProtectedTargetClearHotkey);
        if (config.Targeting.ProtectedTargetClearAfterScans <= 0)
            config.Targeting.ProtectedTargetClearAfterScans = 3;
        config.Targeting.TargetProtectionSampleIndexPath = config.Targeting.TargetProtectionSampleIndexPath?.Trim() ?? string.Empty;
        if (config.Targeting.TargetProtectionSampleThreshold <= 0)
            config.Targeting.TargetProtectionSampleThreshold = 0.93;
        config.Targeting.TargetProtectionSampleThreshold = Math.Clamp(config.Targeting.TargetProtectionSampleThreshold, 0.70, 1.0);
        if (config.AttackIntervalMs <= 0)
            config.AttackIntervalMs = 1000;
        config.SkillCooldownProbeIntervalMs = Math.Clamp(
            config.SkillCooldownProbeIntervalMs <= 0 ? 200 : config.SkillCooldownProbeIntervalMs,
            100,
            1000);
        config.SkillCooldownReadyDebounceMs = Math.Clamp(
            config.SkillCooldownReadyDebounceMs <= 0 ? 350 : config.SkillCooldownReadyDebounceMs,
            150,
            2000);
        if (config.PillHealCooldownMs <= 0)
            config.PillHealCooldownMs = 10000;
        if (config.PillManaCooldownMs <= 0)
            config.PillManaCooldownMs = 10000;
        if (sourceVersion < 23 && !config.RecoveryModesExplicitlyConfigured)
        {
            config.HealthRecoveryMode = PlayerHealthRecoveryMode.Symbiosis;
            config.ManaRecoveryMode = PlayerManaRecoveryMode.BloodSacrifice;
        }
        if (!Enum.IsDefined(config.HealthRecoveryMode))
            config.HealthRecoveryMode = PlayerHealthRecoveryMode.Symbiosis;
        if (!Enum.IsDefined(config.ManaRecoveryMode))
            config.ManaRecoveryMode = PlayerManaRecoveryMode.BloodSacrifice;
        if (config.PetDeployCooldownMs <= 0)
            config.PetDeployCooldownMs = 15000;
        if (config.PetDeployClickX < 0)
            config.PetDeployClickX = 0;
        if (config.PetDeployClickBottomOffset < 0)
            config.PetDeployClickBottomOffset = 0;
        if (!Enum.IsDefined(config.PetDeployActivationMode))
            config.PetDeployActivationMode = PetSkillActivationMode.Hotkey;
        if (sourceVersion < 22 || config.PetFoodCooldownMs <= 0)
            config.PetFoodCooldownMs = 250;
        config.PetFoodCooldownMs = Math.Clamp(config.PetFoodCooldownMs, 150, 2000);
        if (config.BloodManaCooldownMs <= 0)
            config.BloodManaCooldownMs = 30000;
        if (config.SymbiosisHealCooldownMs <= 0)
            config.SymbiosisHealCooldownMs = 30000;
        if (config.BloodManaClickX <= 0)
            config.BloodManaClickX = 170;
        if (config.BloodManaClickBottomOffset <= 0)
            config.BloodManaClickBottomOffset = 90;
        if (config.SymbiosisHealClickX <= 0)
            config.SymbiosisHealClickX = 135;
        if (config.SymbiosisHealClickBottomOffset <= 0)
            config.SymbiosisHealClickBottomOffset = 90;
        config.BloodManaActivationMode = PetSkillActivationMode.ClientClick;
        config.SymbiosisHealActivationMode = PetSkillActivationMode.ClientClick;

        if (string.IsNullOrWhiteSpace(config.BloodManaHotkey) && !string.IsNullOrWhiteSpace(config.PetManaHotkey))
            config.BloodManaHotkey = config.PetManaHotkey;
        if (string.IsNullOrWhiteSpace(config.PetFoodHotkey) && !string.IsNullOrWhiteSpace(config.PetHealHotkey))
            config.PetFoodHotkey = config.PetHealHotkey;
        if (sourceVersion < 7 && SameHotkey(config.SymbiosisHealHotkey, config.PetHealHotkey))
            config.SymbiosisHealHotkey = "";
        if (sourceVersion < 6 && SameHotkey(config.ManaHotkey, config.PetManaHotkey))
            config.ManaHotkey = "";
        if (sourceVersion < 6 && SameHotkey(config.HealHotkey, config.PetHealHotkey))
            config.HealHotkey = "";
        if (config.BloodManaThresholdPct <= 0)
            config.BloodManaThresholdPct = config.MpThresholdPct > 0 ? config.MpThresholdPct : 70;
        if (config.SymbiosisHealThresholdPct <= 0)
            config.SymbiosisHealThresholdPct = config.HpThresholdPct > 0 ? config.HpThresholdPct : 70;
        if (config.PetFoodThresholdPct <= 0)
            config.PetFoodThresholdPct = config.PetHpThresholdPct > 0 ? config.PetHpThresholdPct : 50;

        config.HpThresholdPct = Math.Clamp(config.HpThresholdPct <= 0 ? 70 : config.HpThresholdPct, 1, 100);
        config.MpThresholdPct = Math.Clamp(config.MpThresholdPct <= 0 ? 70 : config.MpThresholdPct, 1, 100);
        config.PetFoodThresholdPct = Math.Clamp(config.PetFoodThresholdPct, 1, 100);

        // v21 exposes one threshold/action per recovery purpose. Keep legacy fields mirrored
        // so older profile readers can still open a profile saved by this version.
        config.BloodManaThresholdPct = config.MpThresholdPct;
        config.SymbiosisHealThresholdPct = config.HpThresholdPct;
        config.PetHpThresholdPct = config.PetFoodThresholdPct;
        config.PetHealHotkey = config.PetFoodHotkey;

        foreach (var skill in config.Skills)
        {
            skill.ExpectedVisualSkillId = skill.ExpectedVisualSkillId?.Trim() ?? string.Empty;
            skill.ExpectedVisualSkillId = string.Empty;
            skill.Hotkey = skill.Hotkey?.Trim() ?? string.Empty;
            skill.IntervalMs = Math.Clamp(skill.IntervalMs <= 0 ? 10000 : skill.IntervalMs, 200, 3600000);
            skill.Cooldown ??= new SkillCooldownSlotConfig();
            skill.Cooldown.Enabled = false;
            NormalizeSkillCooldown(skill.Cooldown);
            if (sourceVersion < 18
                && skill.Kind != SkillKind.Buff
                && skill.Condition == SkillCondition.TargetExists)
            {
                skill.Condition = SkillCondition.Always;
            }

            if (skill.Kind == SkillKind.Buff)
                skill.Condition = SkillCondition.Always;
        }
        EnsureDefaultSkillSlots(config);
        NormalizeCustomSkillOrder(config);

        foreach (var skill in config.AiSkills)
        {
            skill.ExpectedVisualSkillId = skill.ExpectedVisualSkillId?.Trim() ?? string.Empty;
            skill.Hotkey = skill.Hotkey?.Trim() ?? string.Empty;
            skill.Cooldown ??= new SkillCooldownSlotConfig();
            NormalizeSkillCooldown(skill.Cooldown);
            skill.Cooldown.Enabled = true;
            if (skill.Kind == SkillKind.Buff)
                skill.Condition = SkillCondition.Always;
        }

        var mp = config.CombatRecognition.PlayerMp;
        if (mp is { X: 84, Y: 43, Width: 126, Height: 7 })
        {
            mp.X = 82;
            mp.Y = 39;
            mp.Width = 128;
            mp.Height = 5;
        }
        if (mp is { X: 82, Y: 54, Width: 128, Height: 17 })
        {
            mp.Y = 39;
            mp.Height = 5;
        }

        var hp = config.CombatRecognition.PlayerHp;
        if (hp is { X: 84, Y: 33, Width: 126, Height: 7 })
            hp.Height = 5;
    }

    private static bool RemoveCrossSchoolSkillPollution(GlobalConfig config)
    {
        var expectedPrefix = config.School switch
        {
            School.Shaolin => "sl_",
            School.Wudang => "wd_",
            School.Emei => "em_",
            School.Gaibang => "gb_",
            School.Xiaoyao => "xy_",
            School.Tianshan => "ts_",
            School.Mingjiao => "mj_",
            School.Xingxiu => "xx_",
            School.Tangmen => "tm_",
            School.Murong => "mr_",
            _ => string.Empty
        };
        config.AiSkills ??= new List<SkillPreset>();
        if (expectedPrefix.Length == 0 || config.AiSkills.Count == 0)
            return false;

        var removed = config.AiSkills.RemoveAll(skill =>
        {
            var skillId = skill.ExpectedVisualSkillId?.Trim() ?? string.Empty;
            return skillId.Length > 0
                   && !skillId.StartsWith("common_", StringComparison.OrdinalIgnoreCase)
                   && !skillId.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase);
        });
        return removed > 0;
    }

    private static void NormalizeDailyTaskPlan(GlobalConfig config)
    {
        config.DailyTaskPlan ??= new DailyTaskPlan();
        config.DailyTaskPlan.SchoolTaskPolicy ??= new SchoolTaskTypePolicy();
        config.DailyTaskPlan.Items ??= new List<DailyTaskPlanItem>();

        var canonicalKeys = DailyTaskKeys.Ordered
            .ToDictionary(key => key, key => key, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<DailyTaskPlanItem>(DailyTaskKeys.Ordered.Count);

        foreach (var candidate in config.DailyTaskPlan.Items
                     .Select((item, index) => new { Item = item, Index = index })
                     .Where(entry => entry.Item is not null)
                     .OrderBy(entry => entry.Item.Order > 0 ? entry.Item.Order : int.MaxValue)
                     .ThenBy(entry => entry.Index))
        {
            var key = candidate.Item.Key?.Trim() ?? string.Empty;
            if (!canonicalKeys.TryGetValue(key, out var canonicalKey) || !seen.Add(canonicalKey))
                continue;

            normalized.Add(new DailyTaskPlanItem
            {
                Key = canonicalKey,
                Enabled = candidate.Item.Enabled
            });
        }

        foreach (var key in DailyTaskKeys.Ordered)
        {
            if (!seen.Add(key))
                continue;

            normalized.Add(new DailyTaskPlanItem
            {
                Key = key,
                Enabled = false
            });
        }

        for (var index = 0; index < normalized.Count; index++)
            normalized[index].Order = index + 1;

        config.DailyTaskPlan.Items = normalized;
    }

    private static void MigrateLegacyConfig(GlobalConfig config, int sourceVersion)
    {
        config.Skills ??= new List<SkillPreset>();
        config.AiSkills ??= new List<SkillPreset>();

        if (sourceVersion < 29 && config.InputDispatchMode == InputDispatchMode.PostMessage)
        {
            // Preserve background keyboard delivery while allowing mouse-only
            // controls to use a reversible foreground lease.
            config.InputDispatchMode = InputDispatchMode.Adaptive;
        }

        if (sourceVersion < 28)
        {
            var visualRows = config.Skills
                .Where(skill => !string.IsNullOrWhiteSpace(skill.ExpectedVisualSkillId))
                .GroupBy(skill => skill.ExpectedVisualSkillId.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();

            foreach (var visual in visualRows)
            {
                if (!config.AiSkills.Any(skill => string.Equals(
                        skill.ExpectedVisualSkillId?.Trim(),
                        visual.ExpectedVisualSkillId.Trim(),
                        StringComparison.OrdinalIgnoreCase)))
                {
                    config.AiSkills.Add(new SkillPreset
                    {
                        Name = visual.Name,
                        ExpectedVisualSkillId = visual.ExpectedVisualSkillId.Trim(),
                        Hotkey = visual.Hotkey?.Trim() ?? string.Empty,
                        IntervalMs = Math.Max(200, visual.IntervalMs),
                        Enabled = visual.Enabled,
                        Priority = visual.Priority,
                        Kind = visual.Kind,
                        Condition = visual.Kind == SkillKind.Buff ? SkillCondition.Always : SkillCondition.TargetExists,
                        Cooldown = new SkillCooldownSlotConfig { Enabled = true }
                    });
                }
            }

            foreach (var skill in config.Skills)
            {
                skill.ExpectedVisualSkillId = string.Empty;
                skill.Cooldown ??= new SkillCooldownSlotConfig();
                skill.Cooldown.Enabled = false;
            }
        }

        if (sourceVersion < 27
            && (string.Equals(config.AttackHotkey, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(config.AttackHotkey, "F1", StringComparison.OrdinalIgnoreCase)))
        {
            config.AttackHotkey = string.Empty;
        }

        if (sourceVersion < 27
            && config.Targeting is { } targeting
            && string.Equals(targeting.AcquireHotkey, "Ctrl+Tab", StringComparison.OrdinalIgnoreCase))
        {
            targeting.AcquireHotkey = string.Empty;
        }

        foreach (var skill in config.Skills)
        {
            var normalizedHotkey = skill.Hotkey?.Trim().ToUpperInvariant() switch
            {
                "2" => "F2",
                "3" => "F3",
                "4" => "F4",
                "5" => "F5",
                "6" => "F6",
                "7" => "F7",
                "8" => "F8",
                "9" => "F9",
                "0" => "F10",
                _ => skill.Hotkey ?? string.Empty
            };
            var isLegacyPreset = sourceVersion < 27
                                 && string.IsNullOrWhiteSpace(skill.ExpectedVisualSkillId)
                                 && Enumerable.Range(2, 9).Any(slot => string.Equals(
                                     normalizedHotkey,
                                     $"F{slot}",
                                     StringComparison.OrdinalIgnoreCase));
            skill.Hotkey = isLegacyPreset ? string.Empty : normalizedHotkey;
        }

        if (config.AttackIntervalMs is > 0 and <= 500)
            config.AttackIntervalMs = 1000;
    }

    private static void NormalizeTargetPolicy(TargetingPreset targeting, bool migrateLegacyLists)
    {
        targeting.Policy ??= new UnifiedTargetPolicyConfig();
        targeting.Policy.Categories ??= new List<TargetPolicyCategoryConfig>();
        targeting.Policy.SuppressedBuiltInEntryIds = (targeting.Policy.SuppressedBuiltInEntryIds ?? new List<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var suppressedBuiltInIds = new HashSet<string>(
            targeting.Policy.SuppressedBuiltInEntryIds,
            StringComparer.OrdinalIgnoreCase);

        foreach (var categoryKind in Enum.GetValues<TargetPolicyCategoryKind>())
        {
            var category = targeting.Policy.Categories.FirstOrDefault(item => item.Category == categoryKind);
            if (category is null)
            {
                category = TargetPolicyDefaults.CreateCategories().First(item => item.Category == categoryKind);
                targeting.Policy.Categories.Add(category);
            }

            category.Entries ??= new List<TargetPolicyEntry>();
            foreach (var entry in category.Entries)
            {
                entry.Id = string.IsNullOrWhiteSpace(entry.Id)
                    ? Guid.NewGuid().ToString("N")[..8]
                    : entry.Id.Trim();
                entry.Name = entry.Name?.Trim() ?? string.Empty;
                entry.GroupPath = entry.GroupPath?.Trim() ?? string.Empty;
                entry.Source = entry.Source?.Trim() ?? string.Empty;
                entry.Notes = entry.Notes?.Trim() ?? string.Empty;
            }

            category.Entries.RemoveAll(entry => string.IsNullOrWhiteSpace(entry.Name));
            category.Entries.RemoveAll(entry =>
                entry.BuiltIn && suppressedBuiltInIds.Contains(entry.Id));
        }

        EnsureBuiltInTargetPolicyEntries(targeting.Policy);

        if (migrateLegacyLists)
        {
            foreach (var name in SplitLegacyTargetNames(targeting.TargetNameBlacklist))
                AddPolicyEntry(targeting.Policy, TargetPolicyCategoryKind.Monster, name, allowAttack: false, "鏃ч粦鍚嶅崟杩佺Щ");

            foreach (var name in SplitLegacyTargetNames(targeting.TargetNameWhitelist))
                AddPolicyEntry(targeting.Policy, TargetPolicyCategoryKind.Boss, name, allowAttack: true, "鏃х櫧鍚嶅崟杩佺Щ");

            targeting.TargetNameBlacklist = string.Empty;
            targeting.TargetNameWhitelist = string.Empty;
        }
    }

    private static void EnsureBuiltInTargetPolicyEntries(UnifiedTargetPolicyConfig policy)
    {
        var suppressedBuiltInIds = new HashSet<string>(
            policy.SuppressedBuiltInEntryIds ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var defaultCategory in TargetPolicyDefaults.CreateCategories())
        {
            var category = policy.Categories.First(item => item.Category == defaultCategory.Category);
            foreach (var builtIn in defaultCategory.Entries.Where(item => item.BuiltIn))
            {
                if (suppressedBuiltInIds.Contains(builtIn.Id))
                    continue;

                if (category.Entries.Any(item => string.Equals(item.Id, builtIn.Id, StringComparison.OrdinalIgnoreCase)))
                    continue;

                category.Entries.Add(builtIn);
            }
        }
    }

    private static void AddPolicyEntry(
        UnifiedTargetPolicyConfig policy,
        TargetPolicyCategoryKind categoryKind,
        string name,
        bool allowAttack,
        string source)
    {
        var normalizedName = name.Trim();
        if (normalizedName.Length == 0)
            return;

        var category = policy.Categories.First(item => item.Category == categoryKind);
        if (category.Entries.Any(item => string.Equals(item.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
            return;

        category.Entries.Add(new TargetPolicyEntry
        {
            Name = normalizedName,
            Enabled = true,
            AllowAttack = allowAttack,
            Source = source
        });
    }

    private static IEnumerable<string> SplitLegacyTargetNames(string? value) =>
        (value ?? string.Empty)
        .Split(new[] { ';', ',', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(item => item.Length > 0);
    private static bool SameHotkey(string left, string right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeProtectedTargetClearHotkey(string? hotkey)
    {
        var value = hotkey?.Trim() ?? string.Empty;
        return value.Equals("Esc", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Escape", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : value;
    }

    private static void EnsureDefaultSkillSlots(GlobalConfig config)
    {
        for (var slot = config.Skills.Count + 1; slot <= 10; slot++)
        {
            config.Skills.Add(new SkillPreset
            {
                Name = $"Skill {slot}",
                Hotkey = string.Empty,
                IntervalMs = 10000,
                Priority = slot,
                Kind = SkillKind.Damage,
                Enabled = false,
                Condition = SkillCondition.Always
            });
        }
    }

    private static void NormalizeCustomSkillOrder(GlobalConfig config)
    {
        var ordered = config.Skills
            .Select((skill, index) => new { Skill = skill, Index = index })
            .OrderBy(item => item.Skill.Priority <= 0 ? int.MaxValue : item.Skill.Priority)
            .ThenBy(item => item.Index)
            .Select(item => item.Skill)
            .ToArray();

        config.Skills.Clear();
        config.Skills.AddRange(ordered);
        for (var index = 0; index < config.Skills.Count; index++)
            config.Skills[index].Priority = index + 1;
    }

    private static void NormalizeSkillCooldown(SkillCooldownSlotConfig? cooldown)
    {
        if (cooldown is null)
            return;

        cooldown.X = Math.Max(0, cooldown.X);
        cooldown.Y = Math.Max(0, cooldown.Y);
        cooldown.BottomOffset = Math.Max(0, cooldown.BottomOffset);
        cooldown.Width = Math.Clamp(cooldown.Width <= 0 ? 36 : cooldown.Width, 20, 64);
        cooldown.Height = Math.Clamp(cooldown.Height <= 0 ? 36 : cooldown.Height, 20, 64);
        cooldown.ReadyTemplateHash = cooldown.ReadyTemplateHash?.Trim().ToUpperInvariant() ?? "";
        cooldown.ReadyTemplateThreshold = Math.Clamp(
            cooldown.ReadyTemplateThreshold <= 0 ? 0.90 : cooldown.ReadyTemplateThreshold,
            0.70,
            0.99);
    }
}
