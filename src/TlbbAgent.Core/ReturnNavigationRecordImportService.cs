using System.Text;
using System.Text.Json.Nodes;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class ReturnNavigationRecordImportService : IReturnNavigationRecordImportService
{
    private const string Module = "ReturnNavigationImport";
    private readonly IReturnPointService _returnPoints;
    private readonly ILogService _log;

    public ReturnNavigationRecordImportService(IReturnPointService returnPoints, ILogService log)
    {
        _returnPoints = returnPoints;
        _log = log;
    }

    public ReturnNavigationRecordImportResult Import(ReturnNavigationRecordImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProfileName))
            return Fail(request, false, "", "必须提供配置方案名。");

        if (string.IsNullOrWhiteSpace(request.EvidencePath))
            return Fail(request, false, "", "必须提供录制证据文件路径。");

        var evidencePath = Path.GetFullPath(request.EvidencePath);
        if (!File.Exists(evidencePath))
            return Fail(request, false, evidencePath, $"录制证据不存在：{evidencePath}");

        var maxClicks = Math.Clamp(request.MaxClicks, 1, 50);
        var fallbackDelayMs = Math.Clamp(request.FallbackDelayMs, 20, 5000);
        ParsedNavigationRecord record;
        try
        {
            record = ParseRecord(evidencePath, maxClicks, fallbackDelayMs);
        }
        catch (Exception ex)
        {
            return Fail(request, false, evidencePath, $"录制证据解析失败：{ex.Message}");
        }

        record = ApplyConfirmedMapName(record, request.ConfirmedMapName);
        if (!request.AllowSyntheticForDiagnostics && IsSyntheticRecordEvidencePath(evidencePath))
            return Fail(request, false, evidencePath, $"录制证据来自合成/Smoke 样本，只能用于诊断回归，禁止导入真实方案：{evidencePath}", record);

        if (record.Clicks.Count == 0)
            return Fail(request, false, evidencePath, "录制证据中没有可导入的客户区点击。", record);

        var validation = ValidateRecordForImport(record);
        if (!validation.Valid)
            return Fail(request, false, evidencePath, validation.Message, record);

        var existed = _returnPoints.Load(request.ProfileName);
        var (slotName, nav) = SelectSlot(_returnPoints.Current, request.Slot);
        if (nav.RecordedClicks.Count > 0 && !request.Force)
            return Fail(request, existed, evidencePath, $"方案 {request.ProfileName} 的 {slotName} 已有录制点击 {nav.RecordedClicks.Count} 步。确认覆盖请启用强制覆盖。", record);

        if (!existed)
            DisableAllSlots(_returnPoints.Current);

        EnableSlot(_returnPoints.Current, request.Slot);
        nav.Mode = ReturnNavigationMode.RecordedClientClickSequence;
        nav.DispatchMode = request.DispatchMode;
        nav.RecordedSourceEvidencePath = evidencePath;
        nav.RecordedSourceClientSize = record.SourceClientSize;
        nav.RecordedBeforeMap = NormalizeMapName(record.BeforeMap);
        nav.RecordedBeforeX = record.BeforeX;
        nav.RecordedBeforeY = record.BeforeY;
        nav.RecordedAfterMap = NormalizeMapName(record.AfterMap);
        nav.RecordedAfterX = record.AfterX;
        nav.RecordedAfterY = record.AfterY;
        nav.RecordedTargetX = record.TargetX;
        nav.RecordedTargetY = record.TargetY;
        nav.RecordedMovedCloserToTarget = record.MovedCloserToTarget;
        nav.RecordedArrivedAtTarget = record.ArrivedAtTarget;
        nav.RecordedPreHotkeys.Clear();
        foreach (var hotkey in request.PreHotkeys)
        {
            var value = (hotkey.Hotkey ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            nav.RecordedPreHotkeys.Add(new RecordedNavigationHotkey
            {
                Hotkey = value,
                DelayAfterMs = Math.Clamp(hotkey.DelayAfterMs, 20, 5000),
                SourceEvidencePath = string.IsNullOrWhiteSpace(hotkey.SourceEvidencePath) ? evidencePath : hotkey.SourceEvidencePath
            });
        }
        nav.RecordedClicks.Clear();
        foreach (var click in record.Clicks)
            nav.RecordedClicks.Add(click);

        var confirmedMapFromRecordEnd = false;
        var addedReturnPointFromRecordEnd = false;
        ReturnPoint? importedPoint = null;
        var endMap = NormalizeMapName(record.AfterMap);

        if (request.ConfirmMapFromRecordEnd)
        {
            if (!IsUsableMapName(endMap))
                return Fail(request, existed, evidencePath, "录制记录缺少可用的结束地图，不能确认地图。", record);

            _returnPoints.Current.NavigationRecognition.ConfirmedMapName = endMap;
            _returnPoints.Current.NavigationRecognition.ConfirmedMapNameAutoFilled = false;
            confirmedMapFromRecordEnd = true;
        }

        if (request.AddReturnPointFromRecordEnd)
        {
            if (!IsUsableMapName(endMap) || record.AfterX is null || record.AfterY is null)
                return Fail(request, existed, evidencePath, "录制记录缺少可用的结束地图或坐标，不能生成返点坐标。", record);

            var kind = request.ReturnPointKind == ReturnPointKind.Normal
                ? ReturnPointKindForSlot(request.Slot)
                : request.ReturnPointKind;
            importedPoint = _returnPoints.Current.Points.FirstOrDefault(p =>
                string.Equals(NormalizeMapName(p.MapName), endMap, StringComparison.Ordinal)
                && p.X == record.AfterX.Value
                && p.Y == record.AfterY.Value
                && p.Kind == kind);

            if (importedPoint is null)
            {
                importedPoint = new ReturnPoint
                {
                    MapName = endMap,
                    X = record.AfterX.Value,
                    Y = record.AfterY.Value,
                    Label = string.IsNullOrWhiteSpace(request.ReturnPointLabel) ? SlotName(request.Slot) : request.ReturnPointLabel.Trim(),
                    Kind = kind,
                    SourceModule = "录制寻路",
                    SourcePath = evidencePath
                };
                _returnPoints.AddPoint(importedPoint);
                addedReturnPointFromRecordEnd = true;
            }
        }

        _returnPoints.NotifyChanged();
        _returnPoints.Save(request.ProfileName);
        _log.Info(Module, $"录制寻路样本已导入：方案 {request.ProfileName}，{slotName}，点击 {nav.RecordedClicks.Count} 步。");

        return new ReturnNavigationRecordImportResult(
            true,
            "录制寻路样本已导入。",
            request.ProfileName,
            existed,
            request.Slot,
            slotName,
            evidencePath,
            record.SourceClientSize,
            record.BeforeMap,
            record.BeforeCoordinate,
            record.BeforeX,
            record.BeforeY,
            record.AfterMap,
            record.AfterCoordinate,
            record.AfterX,
            record.AfterY,
            record.TargetX,
            record.TargetY,
            record.MovedCloserToTarget,
            record.ArrivedAtTarget,
            confirmedMapFromRecordEnd,
            addedReturnPointFromRecordEnd,
            importedPoint,
            nav.RecordedPreHotkeys.ToArray(),
            nav.RecordedClicks.ToArray());
    }

    private static ReturnNavigationRecordImportResult Fail(
        ReturnNavigationRecordImportRequest request,
        bool profileExisted,
        string evidencePath,
        string message,
        ParsedNavigationRecord? record = null)
    {
        var slotName = SlotName(request.Slot);
        return new ReturnNavigationRecordImportResult(
            false,
            message,
            request.ProfileName,
            profileExisted,
            request.Slot,
            slotName,
            evidencePath,
            record?.SourceClientSize ?? "",
            record?.BeforeMap ?? "",
            record?.BeforeCoordinate ?? "",
            record?.BeforeX,
            record?.BeforeY,
            record?.AfterMap ?? "",
            record?.AfterCoordinate ?? "",
            record?.AfterX,
            record?.AfterY,
            record?.TargetX,
            record?.TargetY,
            record?.MovedCloserToTarget ?? false,
            record?.ArrivedAtTarget ?? false,
            false,
            false,
            null,
            Array.Empty<RecordedNavigationHotkey>(),
            record?.Clicks ?? Array.Empty<RecordedNavigationClick>());
    }

    private static bool IsSyntheticRecordEvidencePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var value = path.Replace('\\', '/').ToLowerInvariant();
        return value.Contains("synthetic", StringComparison.Ordinal)
               || value.Contains("smoke", StringComparison.Ordinal)
               || value.Contains("navigation-record-synthetic", StringComparison.Ordinal);
    }

    private static ParsedNavigationRecord ParseRecord(string evidencePath, int maxClicks, int fallbackDelayMs)
    {
        var root = JsonNode.Parse(File.ReadAllText(evidencePath, Encoding.UTF8))?.AsObject()
            ?? throw new InvalidOperationException("invalid navigation record json");

        var window = root["window"]?.AsObject();
        var before = root["beforeNavigation"]?.AsObject();
        var after = root["afterNavigation"]?.AsObject();
        var target = root["target"]?.AsObject();
        var verification = root["verification"]?.AsObject();
        var events = root["events"]?.AsArray() ?? new JsonArray();
        var rawClicks = new List<RawRecordedClick>();
        var targetX = ReadJsonInt(target, "X");
        var targetY = ReadJsonInt(target, "Y");
        var inClientDownClickCountFromEvents = 0;
        var outOfClientDownClickCountFromEvents = 0;

        foreach (var node in events)
        {
            if (node is not JsonObject e)
                continue;

            if (!string.Equals(ReadJsonString(e, "Action"), "Down", StringComparison.OrdinalIgnoreCase))
                continue;

            var inClient = ReadJsonBool(e, "InClient");
            var clientX = ReadJsonInt(e, "ClientX");
            var clientY = ReadJsonInt(e, "ClientY");
            if (!inClient || clientX is null || clientY is null || clientX < 0 || clientY < 0)
            {
                outOfClientDownClickCountFromEvents++;
                continue;
            }

            inClientDownClickCountFromEvents++;
            if (rawClicks.Count >= maxClicks)
                continue;

            var buttonText = ReadJsonString(e, "Button");
            var button = string.Equals(buttonText, "Right", StringComparison.OrdinalIgnoreCase)
                ? InputMouseButton.Right
                : InputMouseButton.Left;

            rawClicks.Add(new RawRecordedClick(
                ReadJsonLong(e, "ElapsedMs") ?? 0,
                button,
                clientX.Value,
                clientY.Value));
        }

        var clicks = new List<RecordedNavigationClick>();
        for (var i = 0; i < rawClicks.Count; i++)
        {
            var current = rawClicks[i];
            var delay = fallbackDelayMs;
            if (i + 1 < rawClicks.Count)
                delay = (int)Math.Clamp(rawClicks[i + 1].ElapsedMs - current.ElapsedMs, 20, 5000);

            clicks.Add(new RecordedNavigationClick
            {
                ClientX = current.ClientX,
                ClientY = current.ClientY,
                Button = current.Button,
                DelayAfterMs = delay,
                SourceEvidencePath = evidencePath
            });
        }

        var inClientDownClickCount = ReadJsonInt(verification, "inClientDownClickCount") ?? inClientDownClickCountFromEvents;
        var outOfClientDownClickCount = ReadJsonInt(verification, "outOfClientDownClickCount") ?? outOfClientDownClickCountFromEvents;
        var beforeClientSize = ReadJsonString(verification, "beforeClientSize");
        var afterClientSize = ReadJsonString(verification, "afterClientSize");
        var hasClientSizeStable = verification is not null
                                  && verification.TryGetPropertyValue("clientSizeStable", out var clientSizeStableNode)
                                  && clientSizeStableNode is not null;
        var clientSizeStable = !hasClientSizeStable || ReadJsonBool(verification, "clientSizeStable");
        if (!string.IsNullOrWhiteSpace(beforeClientSize)
            && !string.IsNullOrWhiteSpace(afterClientSize)
            && !string.Equals(beforeClientSize, afterClientSize, StringComparison.OrdinalIgnoreCase))
            clientSizeStable = false;

        var operatorInterferenceRisk = ReadJsonBool(verification, "operatorInterferenceRisk")
                                       || inClientDownClickCount > 1
                                       || outOfClientDownClickCount > 0
                                       || !clientSizeStable;

        return new ParsedNavigationRecord(
            ReadJsonString(window, "clientSize"),
            ReadJsonString(before, "MapNameText"),
            ReadJsonString(before, "CoordinateText"),
            ReadJsonInt(before, "X"),
            ReadJsonInt(before, "Y"),
            ReadJsonString(after, "MapNameText"),
            ReadJsonString(after, "CoordinateText"),
            ReadJsonInt(after, "X"),
            ReadJsonInt(after, "Y"),
            targetX,
            targetY,
            verification is not null,
            ReadJsonBool(verification, "usableForImport"),
            inClientDownClickCount,
            outOfClientDownClickCount,
            operatorInterferenceRisk,
            ReadJsonBool(verification, "movedCloserToTarget"),
            ReadJsonBool(verification, "arrivedAtTarget"),
            clicks);
    }

    private static (bool Valid, string Message) ValidateRecordForImport(ParsedNavigationRecord record)
    {
        if (!record.HasVerification)
            return (false, "录制记录缺少验收字段，请用带目标验收的采集命令重新录制。");

        if (record.TargetX is null || record.TargetY is null)
            return (false, "录制记录缺少目标坐标，不能证明该点击会靠近返点。");

        if (record.InClientDownClickCount is null or <= 0)
            return (false, "录制记录没有客户区点击，不能作为回点样本。");

        if (record.InClientDownClickCount > 1)
            return (false, $"录制记录包含 {record.InClientDownClickCount} 次客户区点击；真实回点样本默认只允许一次明确寻路点击，请重新录制。");

        if (record.OutOfClientDownClickCount > 0)
            return (false, $"录制记录包含 {record.OutOfClientDownClickCount} 次客户区外点击，疑似录制期间用户操作干扰，请重新录制。");

        if (record.OperatorInterferenceRisk)
            return (false, "录制记录已标记为疑似人工操作干扰，拒绝导入真实回点方案。");

        if (!record.UsableForImport)
            return (false, "录制记录未标记为可导入，请重新录制并确认命令输出 PASS。");

        if (!record.MovedCloserToTarget && !record.ArrivedAtTarget)
            return (false, "录制记录没有证明角色靠近或到达目标点，拒绝导入。");

        return (true, "");
    }

    private static ParsedNavigationRecord ApplyConfirmedMapName(ParsedNavigationRecord record, string confirmedMapName)
    {
        var mapName = NormalizeMapName(confirmedMapName);
        if (!IsUsableMapName(mapName))
            return record;

        var beforeMap = IsUsableMapName(NormalizeMapName(record.BeforeMap)) ? record.BeforeMap : mapName;
        var afterMap = IsUsableMapName(NormalizeMapName(record.AfterMap)) ? record.AfterMap : mapName;
        return record with
        {
            BeforeMap = beforeMap,
            AfterMap = afterMap
        };
    }

    private static (string SlotName, ReturnNavigationConfig Config) SelectSlot(ReturnPointConfig config, ReturnNavigationSlot slot) =>
        slot switch
        {
            ReturnNavigationSlot.FixedFarm => (SlotName(slot), config.ReturnNavigation),
            ReturnNavigationSlot.EarthSpirit => (SlotName(slot), config.EarthSpiritNavigation),
            ReturnNavigationSlot.StuckRecovery => (SlotName(slot), config.StuckRecoveryNavigation),
            ReturnNavigationSlot.DeathReturn => (SlotName(slot), config.DeathReturnNavigation),
            _ => (SlotName(ReturnNavigationSlot.FixedFarm), config.ReturnNavigation)
        };

    private static void EnableSlot(ReturnPointConfig config, ReturnNavigationSlot slot)
    {
        switch (slot)
        {
            case ReturnNavigationSlot.FixedFarm:
                config.EnableFixedFarmReturn = true;
                break;
            case ReturnNavigationSlot.EarthSpirit:
                config.EnableEarthSpiritReturn = true;
                break;
            case ReturnNavigationSlot.StuckRecovery:
                config.EnableStuckRecovery = true;
                break;
            case ReturnNavigationSlot.DeathReturn:
                config.EnableDeathReturn = true;
                break;
        }
    }

    private static void DisableAllSlots(ReturnPointConfig config)
    {
        config.EnableFixedFarmReturn = false;
        config.EnableEarthSpiritReturn = false;
        config.EnableStuckRecovery = false;
        config.EnableDeathReturn = false;
    }

    public static ReturnNavigationSlot ParseSlot(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "fixed" or "return" or "farm" or "normal" or "挂机" or "回点" => ReturnNavigationSlot.FixedFarm,
            "earth" or "earth-spirit" or "tuling" or "土灵" or "土灵珠" => ReturnNavigationSlot.EarthSpirit,
            "stuck" or "recovery" or "卡点" or "恢复" => ReturnNavigationSlot.StuckRecovery,
            "death" or "death-return" or "死亡" or "死亡回点" => ReturnNavigationSlot.DeathReturn,
            _ when Enum.TryParse<ReturnNavigationSlot>(value, ignoreCase: true, out var parsed) => parsed,
            _ => ReturnNavigationSlot.FixedFarm
        };

    private static string SlotName(ReturnNavigationSlot slot) => slot switch
    {
        ReturnNavigationSlot.FixedFarm => "挂机回点",
        ReturnNavigationSlot.EarthSpirit => "土灵回点",
        ReturnNavigationSlot.StuckRecovery => "卡点恢复",
        ReturnNavigationSlot.DeathReturn => "死亡回点",
        _ => "挂机回点"
    };

    private static ReturnPointKind ReturnPointKindForSlot(ReturnNavigationSlot slot) => slot switch
    {
        ReturnNavigationSlot.FixedFarm => ReturnPointKind.FixedFarm,
        ReturnNavigationSlot.EarthSpirit => ReturnPointKind.EarthSpirit,
        ReturnNavigationSlot.StuckRecovery => ReturnPointKind.Recovery,
        ReturnNavigationSlot.DeathReturn => ReturnPointKind.DeathReturn,
        _ => ReturnPointKind.FixedFarm
    };

    private static string NormalizeMapName(string mapName)
    {
        var chars = mapName.Trim()
            .Where(c => c is >= '\u4E00' and <= '\u9FFF' || char.IsLetterOrDigit(c))
            .ToArray();
        return chars.Length == 0 ? string.Empty : new string(chars);
    }

    private static bool IsUsableMapName(string mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
            return false;

        var meaningful = mapName.Count(c => c is >= '\u4E00' and <= '\u9FFF' || char.IsLetterOrDigit(c));
        return meaningful >= 2;
    }

    private static string ReadJsonString(JsonObject? obj, string name) =>
        obj is not null && obj.TryGetPropertyValue(name, out var node) ? node?.GetValue<string>() ?? "" : "";

    private static int? ReadJsonInt(JsonObject? obj, string name) =>
        obj is not null && obj.TryGetPropertyValue(name, out var node) && node is not null && int.TryParse(node.ToString(), out var value)
            ? value
            : null;

    private static long? ReadJsonLong(JsonObject? obj, string name) =>
        obj is not null && obj.TryGetPropertyValue(name, out var node) && node is not null && long.TryParse(node.ToString(), out var value)
            ? value
            : null;

    private static bool ReadJsonBool(JsonObject? obj, string name) =>
        obj is not null && obj.TryGetPropertyValue(name, out var node) && node is not null && bool.TryParse(node.ToString(), out var value) && value;

    private sealed record RawRecordedClick(long ElapsedMs, InputMouseButton Button, int ClientX, int ClientY);

    private sealed record ParsedNavigationRecord(
        string SourceClientSize,
        string BeforeMap,
        string BeforeCoordinate,
        int? BeforeX,
        int? BeforeY,
        string AfterMap,
        string AfterCoordinate,
        int? AfterX,
        int? AfterY,
        int? TargetX,
        int? TargetY,
        bool HasVerification,
        bool UsableForImport,
        int? InClientDownClickCount,
        int OutOfClientDownClickCount,
        bool OperatorInterferenceRisk,
        bool MovedCloserToTarget,
        bool ArrivedAtTarget,
        IReadOnlyList<RecordedNavigationClick> Clicks);
}
