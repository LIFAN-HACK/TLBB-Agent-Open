using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

[Obsolete("Legacy screenshot OCR provider is disabled in production. Use IPerceptionHub.")]
public sealed class ScreenshotOcrRuntimeStateProvider : IRuntimeStateProvider
{
    private const string SourceName = "screenshot-ocr";
    private readonly ICombatRecognitionService _combat;
    private readonly INavigationRecognitionService _navigation;
    private readonly IDeathRecognitionService _death;
    private readonly IGlobalConfigService _global;
    private readonly IReturnPointService _returnPoints;
    private readonly IUtilityConfigService _utility;
    private readonly IBagCleanupService _bagCleanup;
    private readonly ILogService _log;

    public ScreenshotOcrRuntimeStateProvider(
        ICombatRecognitionService combat,
        INavigationRecognitionService navigation,
        IDeathRecognitionService death,
        IGlobalConfigService global,
        IReturnPointService returnPoints,
        IUtilityConfigService utility,
        IBagCleanupService bagCleanup,
        ILogService log)
    {
        _combat = combat;
        _navigation = navigation;
        _death = death;
        _global = global;
        _returnPoints = returnPoints;
        _utility = utility;
        _bagCleanup = bagCleanup;
        _log = log;
    }

    public RuntimeStateProviderKind Kind => RuntimeStateProviderKind.ScreenshotOcr;

    public RuntimeSnapshot Read(BoundGameWindow window)
    {
        if (window.Hwnd == nint.Zero)
        {
            return new RuntimeSnapshot
            {
                Provider = Kind,
                Source = SourceName,
                Quality = RuntimeSnapshotQuality.Unavailable,
                Message = "窗口未绑定，无法生成截图识别快照。"
            };
        }

        var global = _global.Current;
        var returnConfig = _returnPoints.Current;
        var combat = _combat.Recognize(window, global.CombatRecognition);
        var navigation = _navigation.Recognize(window, returnConfig.NavigationRecognition);
        var death = _death.Recognize(window, returnConfig.DeathRecognition);

        var player = BuildPlayerSnapshot(combat, death);
        var target = BuildTargetSnapshot(combat);
        var pet = BuildPetSnapshot(combat);
        var scene = BuildSceneSnapshot(navigation);
        var inventory = BuildInventorySnapshot(window);
        var quality = CombineQuality(player.Quality, target.Quality, pet.Quality, scene.Quality, inventory.Quality);
        var confidence = AverageNonZero(
            player.ConfidencePercent,
            target.ConfidencePercent,
            pet.ConfidencePercent,
            scene.ConfidencePercent,
            inventory.ConfidencePercent);

        var snapshot = new RuntimeSnapshot
        {
            Provider = Kind,
            Source = SourceName,
            Quality = quality,
            ConfidencePercent = confidence,
            Message = BuildMessage(combat, navigation, death),
            Player = player,
            Target = target,
            Pet = pet,
            Scene = scene,
            Inventory = inventory
        };

        _log.Trace("运行快照", snapshot.Message, window.Id);
        return snapshot;
    }

    private static PlayerRuntimeSnapshot BuildPlayerSnapshot(CombatRecognitionResult combat, DeathRecognitionResult death)
    {
        var hasHp = combat.PlayerHpPercent is not null;
        var hasMp = combat.PlayerMpPercent is not null;
        var quality = hasHp && hasMp
            ? RuntimeSnapshotQuality.Reliable
            : hasHp || hasMp || death.IsDead
                ? RuntimeSnapshotQuality.Partial
                : RuntimeSnapshotQuality.Unavailable;

        return new PlayerRuntimeSnapshot
        {
            Quality = quality,
            Source = SourceName,
            ConfidencePercent = quality switch
            {
                RuntimeSnapshotQuality.Reliable => 85,
                RuntimeSnapshotQuality.Partial => 55,
                _ => 0
            },
            HpPercent = combat.PlayerHpPercent,
            MpPercent = combat.PlayerMpPercent,
            IsDead = death.IsDead
        };
    }

    private static TargetRuntimeSnapshot BuildTargetSnapshot(CombatRecognitionResult combat)
    {
        var hasTarget = combat.HasTarget;
        var hasHp = combat.TargetHpPercent is not null;
        var sampleKind = NormalizeTargetSampleKind(combat.TargetSampleKind);
        var sampleConfirmsAttackable = sampleKind is "monster" or "boss" or "elite";
        var quality = hasTarget && hasHp
            ? RuntimeSnapshotQuality.Reliable
            : hasTarget
                ? RuntimeSnapshotQuality.Partial
                : RuntimeSnapshotQuality.Unavailable;

        return new TargetRuntimeSnapshot
        {
            Quality = quality,
            Source = SourceName,
            ConfidencePercent = quality switch
            {
                RuntimeSnapshotQuality.Reliable => combat.TargetLooksLikePlayer ? 80 : combat.TargetProtectionAmbiguous ? 65 : 75,
                RuntimeSnapshotQuality.Partial => 50,
                _ => 0
            },
            Name = string.IsNullOrWhiteSpace(combat.TargetNameText) ? null : combat.TargetNameText,
            HpPercent = combat.TargetHpPercent,
            IsPlayer = hasTarget ? combat.TargetLooksLikePlayer : null,
            IsPet = hasTarget && combat.TargetLooksLikePet ? true : null,
            IsAttackable = hasTarget
                ? combat.TargetIsAttackable ?? (sampleConfirmsAttackable ? true : null)
                : null,
            IsLost = !hasTarget,
            Kind = !hasTarget
                ? null
                : !string.IsNullOrWhiteSpace(sampleKind)
                    ? sampleKind
                    : combat.TargetLooksLikePlayer
                    ? "PlayerCandidate"
                    : combat.TargetProtectionAmbiguous
                        ? "PlayerProtectionAmbiguous"
                    : combat.TargetLooksLikePet
                        ? "PetCandidate"
                    : "UnknownTarget"
        };
    }

    private static string NormalizeTargetSampleKind(string? kind) =>
        string.IsNullOrWhiteSpace(kind) ? string.Empty : kind.Trim().ToLowerInvariant();

    private static PetRuntimeSnapshot BuildPetSnapshot(CombatRecognitionResult combat)
    {
        var quality = combat.PetHpPercent is null
            ? RuntimeSnapshotQuality.Unavailable
            : RuntimeSnapshotQuality.Reliable;

        return new PetRuntimeSnapshot
        {
            Quality = quality,
            Source = SourceName,
            ConfidencePercent = quality == RuntimeSnapshotQuality.Reliable ? 80 : 0,
            HpPercent = combat.PetHpPercent,
            IsSummoned = combat.PetHpPercent is not null
        };
    }

    private static SceneRuntimeSnapshot BuildSceneSnapshot(NavigationRecognitionResult navigation)
    {
        var hasMap = navigation.HasMap;
        var hasCoord = navigation.HasCoordinate;
        var mapSource = hasMap
            ? ResolveSource(navigation.MapNameSource, $"{SourceName}/map-name")
            : string.Empty;
        var coordSource = hasCoord
            ? ResolveSource(navigation.CoordinateSource, $"{SourceName}/coordinate")
            : string.Empty;
        var mapConfidence = hasMap
            ? ResolveConfidence(navigation.MapNameConfidencePercent, 50)
            : 0;
        var coordConfidence = hasCoord
            ? ResolveConfidence(navigation.CoordinateConfidencePercent, 50)
            : 0;
        var quality = hasMap && hasCoord
            ? RuntimeSnapshotQuality.Reliable
            : hasMap || hasCoord
                ? RuntimeSnapshotQuality.Partial
                : RuntimeSnapshotQuality.Unavailable;
        var confidence = AverageNonZero(mapConfidence, coordConfidence);
        var sceneUsable = hasMap && hasCoord;

        return new SceneRuntimeSnapshot
        {
            Quality = quality,
            Source = BuildCombinedSource(mapSource, coordSource),
            ConfidencePercent = confidence,
            SceneUsable = sceneUsable,
            MapFieldReady = hasMap,
            CoordinateFieldReady = hasCoord,
            ReturnPointFieldReady = sceneUsable,
            GateReason = BuildSceneGateReason(hasMap, hasCoord),
            MapName = string.IsNullOrWhiteSpace(navigation.MapNameText) ? null : navigation.MapNameText,
            MapNameSource = string.IsNullOrWhiteSpace(mapSource) ? null : mapSource,
            MapNameConfidencePercent = mapConfidence,
            X = navigation.X,
            Y = navigation.Y,
            CoordinateSource = string.IsNullOrWhiteSpace(coordSource) ? null : coordSource,
            CoordinateConfidencePercent = coordConfidence,
            IsDungeon = ClassifyDungeonScene(navigation.MapNameText)
        };
    }

    private static bool? ClassifyDungeonScene(string? mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
            return null;

        var normalized = new string(mapName.Where(c => c is >= '\u4E00' and <= '\u9FFF' || char.IsDigit(c)).ToArray());
        if (normalized.Length < 2)
            return null;

        var dungeonKeywords = new[]
        {
            "副本", "三环", "燕子坞", "少室山", "四绝庄", "缥缈峰", "棋局", "藏经阁",
            "杀星", "楼兰寻宝", "玄海", "凤凰古城", "兵圣奇阵", "讨伐", "挑战"
        };
        if (dungeonKeywords.Any(k => normalized.Contains(k, StringComparison.Ordinal)))
            return true;

        var outdoorKeywords = new[]
        {
            "无量山", "剑阁", "敦煌", "嵩山", "太湖", "西湖", "洱海", "雁南", "雁北",
            "草原", "辽西", "长白山", "黄龙府", "南海", "苗疆", "琼州", "梅岭", "玉溪",
            "盐湖", "火焰山", "塔里木", "武夷", "苏州", "洛阳", "大理", "楼兰", "束河",
            "帮会城市", "燕王古墓", "秦皇地宫", "银皑雪原", "昆仑山", "圣兽山", "镜湖",
            "玄武岛", "古墓", "地宫", "飘渺峰野外"
        };
        if (outdoorKeywords.Any(k => normalized.Contains(k, StringComparison.Ordinal)))
            return false;

        return null;
    }

    private static string BuildSceneGateReason(bool hasMap, bool hasCoordinate)
    {
        if (hasMap && hasCoordinate)
            return "地图和坐标均已识别，可作为回点字段候选。";
        if (!hasMap && !hasCoordinate)
            return "地图和坐标均未识别；可能未进入游戏世界、当前界面没有小地图坐标、区域偏移或 OCR 字库不足。";
        if (!hasCoordinate)
            return "已识别地图但未识别坐标；禁止触发自动回点。";
        return "已识别坐标但未识别地图名；为避免跨地图误回点，禁止触发自动回点。";
    }

    private static string ResolveSource(string? source, string fallback) =>
        string.IsNullOrWhiteSpace(source) ? fallback : source.Trim();

    private static int ResolveConfidence(int confidence, int fallback) =>
        confidence > 0 ? Math.Clamp(confidence, 0, 100) : fallback;

    private static string BuildCombinedSource(params string[] sources)
    {
        var values = sources
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return values.Length == 0 ? SourceName : string.Join("+", values);
    }

    private InventoryRuntimeSnapshot BuildInventorySnapshot(BoundGameWindow window)
    {
        var source = $"{SourceName}/bag-cleanup-dry-run";
        try
        {
            var sourceConfig = _utility.Current.Cleanup;
            if (sourceConfig?.Grid is not { Enabled: true })
            {
                return new InventoryRuntimeSnapshot
                {
                    Quality = RuntimeSnapshotQuality.Unavailable,
                    Source = source,
                    ConfidencePercent = 0,
                    IsOpen = false
                };
            }

            var config = CloneCleanupConfig(sourceConfig);
            config.Mode = BagCleanupExecutionMode.DryRun;
            config.OpenBagBeforeScan = false;
            config.AllowActualSell = false;

            var analysis = _bagCleanup.Analyze(window, config);
            if (!analysis.Success)
            {
                return new InventoryRuntimeSnapshot
                {
                    Quality = RuntimeSnapshotQuality.Unavailable,
                    Source = source,
                    ConfidencePercent = 0,
                    IsOpen = false
                };
            }

            var total = analysis.Scan.Slots.Count;
            var candidates = analysis.Candidates
                .Take(30)
                .Select(c => new InventoryCandidateRuntimeSnapshot
                {
                    SlotIndex = c.Slot.Index,
                    Row = c.Slot.Row,
                    Column = c.Slot.Column,
                    ItemName = string.IsNullOrWhiteSpace(c.Slot.ItemName) ? null : c.Slot.ItemName,
                    Decision = c.Decision.ToString(),
                    Reason = c.Reason,
                    Source = c.Slot.Source
                })
                .ToList();

            return new InventoryRuntimeSnapshot
            {
                Quality = RuntimeSnapshotQuality.Reliable,
                Source = source,
                ConfidencePercent = 75,
                IsOpen = true,
                UsedSlots = analysis.Scan.OccupiedCount,
                EmptySlots = analysis.Scan.EmptyCount,
                TotalSlots = total,
                SellCandidateCount = analysis.SellCandidateCount,
                ProtectedCount = analysis.ProtectedCount,
                Executed = analysis.Executed,
                Candidates = candidates
            };
        }
        catch (Exception ex)
        {
            _log.Warn("运行快照", $"背包快照生成失败：{ex.Message}", window.Id);
            return new InventoryRuntimeSnapshot
            {
                Quality = RuntimeSnapshotQuality.Unavailable,
                Source = source,
                ConfidencePercent = 0,
                IsOpen = null
            };
        }
    }

    private static BagCleanupConfig CloneCleanupConfig(BagCleanupConfig source)
    {
        return new BagCleanupConfig
        {
            EnabledIntent = source.EnabledIntent,
            IntentMode = source.IntentMode,
            ReturnCity = source.ReturnCity,
            Mode = source.Mode,
            OpenBagBeforeScan = source.OpenBagBeforeScan,
            OpenBagHotkey = source.OpenBagHotkey,
            OpenBagHotkeyExplicitlyConfigured = source.OpenBagHotkeyExplicitlyConfigured,
            AllowActualSell = source.AllowActualSell,
            MinFreeSlotsWarning = source.MinFreeSlotsWarning,
            Grid = CloneGrid(source.Grid),
            Policy = ClonePolicy(source.Policy)
        };
    }

    private static InventoryGridConfig CloneGrid(InventoryGridConfig? source)
    {
        if (source is null)
            return new InventoryGridConfig();

        return new InventoryGridConfig
        {
            ScanMode = source.ScanMode,
            Enabled = source.Enabled,
            X = source.X,
            Y = source.Y,
            Columns = source.Columns,
            Rows = source.Rows,
            CellWidth = source.CellWidth,
            CellHeight = source.CellHeight,
            GapX = source.GapX,
            GapY = source.GapY,
            SlotNameOverrides = new Dictionary<int, string>(source.SlotNameOverrides),
            SlotNameSources = new Dictionary<int, string>(source.SlotNameSources)
        };
    }

    private static ItemPolicyConfig ClonePolicy(ItemPolicyConfig? source)
    {
        if (source is null)
            return new ItemPolicyConfig();

        return new ItemPolicyConfig
        {
            AllowedSellItems = source.AllowedSellItems.ToList(),
            KeepItems = source.KeepItems.ToList(),
            StoreItems = source.StoreItems.ToList(),
            TradeItems = source.TradeItems.ToList(),
            SellKeywords = source.SellKeywords.ToList(),
            BlockKeywords = source.BlockKeywords.ToList(),
            RequireExplicitSellKeyword = source.RequireExplicitSellKeyword,
            ProtectUnknownItems = source.ProtectUnknownItems
        };
    }

    private static RuntimeSnapshotQuality CombineQuality(params RuntimeSnapshotQuality[] qualities)
    {
        if (qualities.Any(q => q == RuntimeSnapshotQuality.Reliable))
            return RuntimeSnapshotQuality.Reliable;
        if (qualities.Any(q => q == RuntimeSnapshotQuality.Partial))
            return RuntimeSnapshotQuality.Partial;
        return RuntimeSnapshotQuality.Unavailable;
    }

    private static int AverageNonZero(params int[] values)
    {
        var valid = values.Where(v => v > 0).ToArray();
        return valid.Length == 0 ? 0 : (int)Math.Round(valid.Average());
    }

    private static string BuildMessage(
        CombatRecognitionResult combat,
        NavigationRecognitionResult navigation,
        DeathRecognitionResult death)
    {
        var target = combat.HasTarget
            ? $"{Blank(combat.TargetNameText)} HP={Percent(combat.TargetHpPercent)}"
            : "无目标";
        var player = $"人物 HP={Percent(combat.PlayerHpPercent)} MP={Percent(combat.PlayerMpPercent)}";
        var pet = $"宠物 HP={Percent(combat.PetHpPercent)}";
        var scene = navigation.HasCoordinate
            ? $"{Blank(navigation.MapNameText)} ({navigation.X},{navigation.Y})"
            : Blank(navigation.MapNameText);
        var dead = death.IsDead ? "，死亡=true" : "";
        return $"截图快照：{target}，{player}，{pet}，场景={scene}{dead}";
    }

    private static string Percent(int? value) => value is null ? "-" : $"{value}%";
    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
}
