namespace TlbbAgent.Contracts;

public enum School
{
    None = 0,
    Shaolin,
    Wudang,
    Emei,
    Gaibang,
    Xiaoyao,
    Tianshan,
    Mingjiao,
    Xingxiu,
    Tangmen,
    Murong
}

public enum SkillCondition
{
    Always = 0,
    TargetExists,
    HpLow,
    MpLow,
    EmHeal
}

public enum SkillKind
{
    Damage = 0,
    Aoe,
    Buff
}

public enum TargetFilter
{
    All = 0,
    MonsterOnly,
    RedNameOnly,
    SpecificName
}

public enum TargetSwitchMode
{
    Grab = 0,
    KillThenSwitch,
    BossWhitelist,
    Dungeon
}

public enum TargetPolicyCategoryKind
{
    Monster = 0,
    Boss,
    Player
}

public sealed class TargetPolicyEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool AllowAttack { get; set; } = true;
    public bool BuiltIn { get; set; }
    public string GroupPath { get; set; } = "";
    public string Source { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class TargetPolicyCategoryConfig
{
    public TargetPolicyCategoryKind Category { get; set; }
    public bool Enabled { get; set; } = true;
    public bool AllowUnlisted { get; set; } = true;
    public List<TargetPolicyEntry> Entries { get; set; } = new();
}

public sealed class UnifiedTargetPolicyConfig
{
    public bool Enabled { get; set; }
    public List<TargetPolicyCategoryConfig> Categories { get; set; } = TargetPolicyDefaults.CreateCategories();
    public List<string> SuppressedBuiltInEntryIds { get; set; } = new();
}

public static class TargetPolicyDefaults
{
    public static List<TargetPolicyCategoryConfig> CreateCategories() =>
    [
        new()
        {
            Category = TargetPolicyCategoryKind.Monster,
            Enabled = true,
            AllowUnlisted = true
        },
        new()
        {
            Category = TargetPolicyCategoryKind.Boss,
            Enabled = true,
            AllowUnlisted = true,
            Entries =
            [
                new TargetPolicyEntry
                {
                    Id = "boss-bilin-zombie",
                    Name = "碧磷僵尸",
                    Enabled = true,
                    AllowAttack = false,
                    BuiltIn = true,
                    GroupPath = "副本/燕子坞/桑土公",
                    Source = "内置安全规则",
                    Notes = "桑土公召唤物，默认禁止攻击。"
                },
                new TargetPolicyEntry
                {
                    Id = "boss-mirror-lake-chudongjiao",
                    Name = "出洞蛟",
                    Enabled = true,
                    AllowAttack = false,
                    BuiltIn = true,
                    GroupPath = "野外BOSS/镜湖/混江龙",
                    Source = "https://tl.changyou.com/data/monster/monster23.shtml",
                    Notes = "混江龙手下，默认禁止攻击。"
                },
                new TargetPolicyEntry
                {
                    Id = "boss-mirror-lake-fanjiangshen",
                    Name = "翻江蜃",
                    Enabled = true,
                    AllowAttack = false,
                    BuiltIn = true,
                    GroupPath = "野外BOSS/镜湖/混江龙",
                    Source = "https://tl.changyou.com/data/monster/monster23.shtml",
                    Notes = "混江龙手下，默认禁止攻击。"
                }
            ]
        },
        new()
        {
            Category = TargetPolicyCategoryKind.Player,
            Enabled = false,
            AllowUnlisted = false
        }
    ];
}

public enum DeathSwitchStrategy
{
    Conservative = 0,
    Standard,
    Predictive
}

public enum PetSkillActivationMode
{
    Hotkey = 0,
    ClientClick = 1
}

public enum PlayerHealthRecoveryMode
{
    Medicine = 0,
    Symbiosis = 1
}

public enum PlayerManaRecoveryMode
{
    Medicine = 0,
    BloodSacrifice = 1
}

public enum SkillCooldownReadiness
{
    Disabled = 0,
    Ready,
    Cooldown,
    Unknown
}

public sealed class SkillPreset
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "技能";
    public string ExpectedVisualSkillId { get; set; } = "";
    public string Hotkey { get; set; } = "";
    public int IntervalMs { get; set; } = 1000;
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 0;
    public SkillKind Kind { get; set; } = SkillKind.Damage;
    public SkillCondition Condition { get; set; } = SkillCondition.Always;
    public SkillCooldownSlotConfig Cooldown { get; set; } = new();
}

public sealed class SkillCooldownSlotConfig
{
    public bool Enabled { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int BottomOffset { get; set; }
    public int Width { get; set; } = 36;
    public int Height { get; set; } = 36;
    public string ReadyTemplateHash { get; set; } = "";
    public double ReadyTemplateThreshold { get; set; } = 0.90;
}

public sealed class TargetingPreset
{
    public TargetSwitchMode SwitchMode { get; set; } = TargetSwitchMode.KillThenSwitch;
    public DeathSwitchStrategy DeathSwitchStrategy { get; set; } = DeathSwitchStrategy.Standard;
    public TargetFilter Filter { get; set; } = TargetFilter.MonsterOnly;
    public string SpecificName { get; set; } = "";
    public bool OnlyAttackMarked { get; set; }
    public bool BlockPlayerTargets { get; set; } = true;
    /// <summary>选怪/切怪热键。游戏内置常为 Ctrl+Tab；勿用单独 Tab（会开地图）。支持 Ctrl/Alt/Shift 组合。</summary>
    public string AcquireHotkey { get; set; } = "";
    public int ScanIntervalMs { get; set; } = 500;
    public bool AutoAcquireWhenNoTarget { get; set; } = true;
    public int AcquireIntervalMs { get; set; } = 800;
    public int TargetLostDelayMs { get; set; } = 600;
    public int NoDamageReacquireMs { get; set; } = 15000;
    public string ProtectedTargetClearHotkey { get; set; } = "";
    public int ProtectedTargetClearAfterScans { get; set; } = 3;
    public bool TargetProtectionSampleMatchingEnabled { get; set; } = true;
    public string TargetProtectionSampleIndexPath { get; set; } = "";
    public double TargetProtectionSampleThreshold { get; set; } = 0.93;
    public bool L2TargetNameEnabled { get; set; } = true;
    public string TargetNameWhitelist { get; set; } = "";
    public string TargetNameBlacklist { get; set; } = "";
    public UnifiedTargetPolicyConfig Policy { get; set; } = new();
}

public sealed class OcrRegionConfig
{
    public bool Enabled { get; set; } = true;
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public readonly record struct OcrResolvedRegion(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public static class OcrRegionProjection
{
    public const int RightAnchoredReferenceWidth = 2560;

    public static OcrResolvedRegion Resolve(
        OcrRegionConfig region,
        int clientWidth,
        int clientHeight,
        bool rightAnchored = false,
        int referenceClientWidth = RightAnchoredReferenceWidth)
    {
        if (!region.Enabled || clientWidth <= 0 || clientHeight <= 0 || region.Width <= 0 || region.Height <= 0)
            return new OcrResolvedRegion(0, 0, 0, 0);

        var x = region.X;
        var y = region.Y;
        var width = region.Width;
        var height = region.Height;

        if (rightAnchored && region.X + region.Width > clientWidth)
        {
            var rightMargin = Math.Max(0, referenceClientWidth - region.X - region.Width);
            x = clientWidth - rightMargin - width;
        }

        x = Math.Clamp(x, 0, Math.Max(0, clientWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, clientHeight - 1));
        width = Math.Min(width, clientWidth - x);
        height = Math.Min(height, clientHeight - y);

        return width <= 0 || height <= 0
            ? new OcrResolvedRegion(0, 0, 0, 0)
            : new OcrResolvedRegion(x, y, width, height);
    }
}

public sealed class CombatRecognitionConfig
{
    public OcrRegionConfig TargetName { get; set; } = new() { X = 278, Y = 8, Width = 128, Height = 10 };
    public OcrRegionConfig TargetHp { get; set; } = new() { X = 278, Y = 23, Width = 128, Height = 3 };
    public OcrRegionConfig TargetMp { get; set; } = new() { X = 278, Y = 29, Width = 128, Height = 4 };
    public OcrRegionConfig TargetRage { get; set; } = new() { X = 278, Y = 41, Width = 128, Height = 3 };
    public OcrRegionConfig PlayerHp { get; set; } = new() { X = 82, Y = 33, Width = 128, Height = 5 };
    public OcrRegionConfig PlayerMp { get; set; } = new() { X = 82, Y = 39, Width = 128, Height = 5 };
    public OcrRegionConfig PetHp { get; set; } = new() { X = 136, Y = 76, Width = 96, Height = 5 };
}

public static class DailyTaskKeys
{
    public const string School = "task.school";
    public const string GuildContribution = "task.guild";
    public const string NewButterflyDream = "task.new-butterfly-dream";
    public const string FaceChanging = "task.face-changing";
    public const string ThousandAndOneWishes = "task.thousand-and-one-wishes";

    public static IReadOnlyList<string> Ordered { get; } =
    [
        School,
        GuildContribution,
        NewButterflyDream,
        FaceChanging,
        ThousandAndOneWishes
    ];
}

public sealed class DailyTaskPlanItem
{
    public string Key { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int Order { get; set; }
}

public sealed class SchoolTaskTypePolicy
{
    public bool AllowCombat { get; set; } = true;
    public bool AllowDelivery { get; set; } = true;
    public bool AllowPetCapture { get; set; } = true;
    /// <summary>false 表示后端接到买物品类型的师门任务时应放弃该任务。</summary>
    public bool AllowItemPurchase { get; set; } = true;
}

public sealed class DailyTaskPlan
{
    public List<DailyTaskPlanItem> Items { get; set; } = DailyTaskKeys.Ordered
        .Select((key, index) => new DailyTaskPlanItem
        {
            Key = key,
            Enabled = false,
            Order = index + 1
        })
        .ToList();

    public SchoolTaskTypePolicy SchoolTaskPolicy { get; set; } = new();
}

public sealed class GlobalConfig
{
    public int SchemaVersion { get; set; } = 29;
    public School School { get; set; } = School.None;
    public InputDispatchMode InputDispatchMode { get; set; } = InputDispatchMode.Adaptive;
    public bool YieldToManualInput { get; set; } = true;
    public int ManualInputYieldMs { get; set; } = 800;
    public CombatBackendPreference CombatBackendPreference { get; set; } = CombatBackendPreference.VisualModel;
    public GameBuiltInHangSwitchMode GameBuiltInHangSwitchMode { get; set; } = GameBuiltInHangSwitchMode.Disabled;
    public string GameBuiltInHangSwitchHotkey { get; set; } = "";
    public int GameBuiltInHangSwitchClickX { get; set; }
    public int GameBuiltInHangSwitchClickY { get; set; }
    public int GameBuiltInHangSwitchCooldownMs { get; set; } = 3000;

    public bool AutoReconnect { get; set; }
    public bool AutoLogin { get; set; }
    public bool ShoutEnabled { get; set; }
    public string ShoutText { get; set; } = "";

    public string AttackHotkey { get; set; } = "";
    public int AttackIntervalMs { get; set; } = 1000;
    public string MeditateHotkey { get; set; } = "";
    public string MountHotkey { get; set; } = "";
    public string PetDeployHotkey { get; set; } = "";
    public PetSkillActivationMode PetDeployActivationMode { get; set; } = PetSkillActivationMode.Hotkey;
    public int PetDeployClickX { get; set; }
    public int PetDeployClickBottomOffset { get; set; }
    public string HealHotkey { get; set; } = "";
    public string ManaHotkey { get; set; } = "";
    public string PetManaHotkey { get; set; } = "";
    public string PetHealHotkey { get; set; } = "";
    public string PetFoodHotkey { get; set; } = "";
    public string BloodManaHotkey { get; set; } = "";
    public string SymbiosisHealHotkey { get; set; } = "";
    public PlayerHealthRecoveryMode HealthRecoveryMode { get; set; } = PlayerHealthRecoveryMode.Symbiosis;
    public PlayerManaRecoveryMode ManaRecoveryMode { get; set; } = PlayerManaRecoveryMode.BloodSacrifice;
    public bool RecoveryModesExplicitlyConfigured { get; set; }
    public PetSkillActivationMode BloodManaActivationMode { get; set; } = PetSkillActivationMode.ClientClick;
    public int BloodManaClickX { get; set; } = 170;
    public int BloodManaClickBottomOffset { get; set; } = 90;
    public PetSkillActivationMode SymbiosisHealActivationMode { get; set; } = PetSkillActivationMode.ClientClick;
    public int SymbiosisHealClickX { get; set; } = 135;
    public int SymbiosisHealClickBottomOffset { get; set; } = 90;
    public string TownPortalHotkey { get; set; } = "";
    public string EarthSpiritHotkey { get; set; } = "";

    public int HpThresholdPct { get; set; } = 70;
    public int MpThresholdPct { get; set; } = 70;
    public int PetHpThresholdPct { get; set; } = 50;
    public int PetFoodThresholdPct { get; set; } = 50;
    public int BloodManaThresholdPct { get; set; } = 70;
    public int SymbiosisHealThresholdPct { get; set; } = 70;
    public int PillHealCooldownMs { get; set; } = 10000;
    public int PillManaCooldownMs { get; set; } = 10000;
    public int PetDeployCooldownMs { get; set; } = 15000;
    public int PetFoodCooldownMs { get; set; } = 250;
    public int BloodManaCooldownMs { get; set; } = 30000;
    public int SymbiosisHealCooldownMs { get; set; } = 30000;
    public int PublicCooldownMs { get; set; } = 300;
    public bool SkillCooldownRecognitionEnabled { get; set; } = true;
    public int SkillCooldownProbeIntervalMs { get; set; } = 200;
    public int SkillCooldownReadyDebounceMs { get; set; } = 350;

    public bool EnableEmHeal { get; set; }
    public string EmHealHotkey { get; set; } = "";
    public int EmHealThresholdPct { get; set; } = 50;

    public List<SkillPreset> Skills { get; set; } = new();
    public List<SkillPreset> AiSkills { get; set; } = new();
    public TargetingPreset Targeting { get; set; } = new();
    public CombatRecognitionConfig CombatRecognition { get; set; } = new();
    public CaptchaConfig Captcha { get; set; } = new();
    public DailyTaskPlan DailyTaskPlan { get; set; } = new();
    public ActivityCenterConfig ActivityCenter { get; set; } = new();
}

public interface IGlobalConfigService
{
    GlobalConfig Current { get; }
    event EventHandler? Changed;

    void SetSchool(School school);
    bool SchoolProvidesHeal(School school);
    void NotifyChanged();
    void Save(string profileName);
    bool Load(string profileName);
    void ExportToFile(string path);
    bool ImportFromFile(string path);
}

public interface ITargetPolicyCatalogService
{
    IReadOnlyList<TargetPolicyEntry> Load(TargetPolicyCategoryKind category);
    int MergeInto(UnifiedTargetPolicyConfig policy, TargetPolicyCategoryKind category);
}
