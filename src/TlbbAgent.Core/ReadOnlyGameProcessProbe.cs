using System.Diagnostics;
using System.Security.Cryptography;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class ReadOnlyGameProcessProbe : IReadOnlyGameProcessProbe
{
    private const string Module = "运行态探针";
    private readonly ILogService _log;

    public ReadOnlyGameProcessProbe(ILogService log)
    {
        _log = log;
    }

    public ReadOnlyProcessProbeResult Probe(BoundGameWindow window)
    {
        if (window.Hwnd == 0 || window.Pid <= 0)
            return Fail(window, "InvalidWindow", "窗口绑定信息不完整。");

        try
        {
            using var process = Process.GetProcessById(window.Pid);
            var processPath = SafeMainModulePath(process);
            var version = SafeFileVersion(processPath);
            var sha256 = SafeSha256(processPath);
            var startTime = SafeStartTime(process);
            var isGameExe = string.Equals(Path.GetFileName(processPath), "Game.exe", StringComparison.OrdinalIgnoreCase);

            var success = isGameExe && window.IsAutomationAllowed && File.Exists(processPath);
            var message = success
                ? "只读进程元信息采集完成。"
                : BuildWarningMessage(isGameExe, processPath, window.Bitness);

            var result = new ReadOnlyProcessProbeResult
            {
                Success = success,
                Status = success ? "Ready" : "Warning",
                Message = message,
                Hwnd = window.Hwnd,
                Pid = window.Pid,
                Title = window.Title,
                ProcessName = process.ProcessName,
                ProcessPath = processPath,
                Bitness = window.Bitness,
                ClientType = window.ClientType,
                ClientWidth = window.ClientWidth,
                ClientHeight = window.ClientHeight,
                MainModuleVersion = version,
                MainModuleSha256 = sha256,
                ProcessStartTime = startTime,
                Capabilities = CapabilityMatrix(success)
            };

            if (success)
                _log.Info(Module, $"进程元信息就绪：PID {window.Pid}，{window.ClientSizeText}，SHA256 {ShortHash(sha256)}", window.Id);
            else
                _log.Warn(Module, message, window.Id);

            return result;
        }
        catch (Exception ex)
        {
            _log.Warn(Module, $"只读进程元信息采集失败：{ex.Message}", window.Id);
            return Fail(window, "Error", ex.Message);
        }
    }

    private static ReadOnlyProcessProbeResult Fail(BoundGameWindow window, string status, string message) => new()
    {
        Success = false,
        Status = status,
        Message = message,
        Hwnd = window.Hwnd,
        Pid = window.Pid,
        Title = window.Title,
        ProcessName = window.ProcessName,
        ProcessPath = window.ProcessPath,
        Bitness = window.Bitness,
        ClientType = window.ClientType,
        ClientWidth = window.ClientWidth,
        ClientHeight = window.ClientHeight,
        Capabilities = CapabilityMatrix(false)
    };

    private static IReadOnlyList<RuntimeProbeCapabilityResult> CapabilityMatrix(bool metadataReady) =>
        new[]
        {
            new RuntimeProbeCapabilityResult(
                RuntimeProbeCapability.ProcessMetadata,
                metadataReady ? RuntimeProbeCapabilityStatus.Available : RuntimeProbeCapabilityStatus.Unavailable,
                metadataReady ? "已采集 PID、路径、位数和窗口尺寸。" : "进程元信息未就绪。"),
            new RuntimeProbeCapabilityResult(
                RuntimeProbeCapability.VersionFingerprint,
                metadataReady ? RuntimeProbeCapabilityStatus.Available : RuntimeProbeCapabilityStatus.Unavailable,
                metadataReady ? "已采集主程序版本和 SHA256。" : "版本指纹未就绪。"),
            new RuntimeProbeCapabilityResult(
                RuntimeProbeCapability.PlayerSnapshot,
                RuntimeProbeCapabilityStatus.Planned,
                "后续只读研究项；当前不读取在线运行内存。"),
            new RuntimeProbeCapabilityResult(
                RuntimeProbeCapability.TargetSnapshot,
                RuntimeProbeCapabilityStatus.Planned,
                "后续只读研究项；当前仍以外部截图识别为主。"),
            new RuntimeProbeCapabilityResult(
                RuntimeProbeCapability.PetSnapshot,
                RuntimeProbeCapabilityStatus.Planned,
                "后续只读研究项；当前仍以外部截图识别为主。"),
            new RuntimeProbeCapabilityResult(
                RuntimeProbeCapability.SceneSnapshot,
                RuntimeProbeCapabilityStatus.Planned,
                "后续只读研究项；地图坐标优先用外部识别与静态资产。"),
            new RuntimeProbeCapabilityResult(
                RuntimeProbeCapability.InventorySnapshot,
                RuntimeProbeCapabilityStatus.Planned,
                "后续只读研究项；清包优先用背包截图和白名单策略。")
        };

    private static string BuildWarningMessage(bool isGameExe, string processPath, ClientBitness bitness)
    {
        var parts = new List<string>();
        if (!isGameExe)
            parts.Add($"当前进程不是 Game.exe：{Path.GetFileName(processPath)}");
        if (!File.Exists(processPath))
            parts.Add("进程路径不可读。");
        return parts.Count == 0 ? "只读探针未达到就绪条件。" : string.Join("；", parts);
    }

    private static string SafeMainModulePath(Process process)
    {
        try { return process.MainModule?.FileName ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string SafeFileVersion(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return version.FileVersion ?? version.ProductVersion ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeSha256(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        try
        {
            using var stream = File.OpenRead(path);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeStartTime(Process process)
    {
        try { return process.StartTime.ToString("O"); }
        catch { return string.Empty; }
    }

    private static string ShortHash(string hash) => hash.Length >= 12 ? hash[..12] : hash;
}

public sealed class ReadOnlyRuntimeStateProvider : IRuntimeStateProvider
{
    private readonly IReadOnlyGameProcessProbe _probe;
    private const string NotStartedSource = "read-only-process/not-started";
    private const string NotStartedMessage = "只读状态字段尚未验证，不允许进入业务决策。";

    public ReadOnlyRuntimeStateProvider(IReadOnlyGameProcessProbe probe)
    {
        _probe = probe;
    }

    public RuntimeStateProviderKind Kind => RuntimeStateProviderKind.ReadOnlyProcess;

    public RuntimeSnapshot Read(BoundGameWindow window)
    {
        var result = _probe.Probe(window);
        return new RuntimeSnapshot
        {
            Provider = RuntimeStateProviderKind.ReadOnlyProcess,
            Quality = result.Success ? RuntimeSnapshotQuality.Partial : RuntimeSnapshotQuality.Unavailable,
            Source = "read-only-process",
            ConfidencePercent = result.Success ? 70 : 0,
            CapturedAt = result.CapturedAt,
            Message = result.Success
                ? "只读进程元信息可用；人物、目标、宠物、场景、背包快照尚未接入。"
                : result.Message,
            Player = BuildUnavailablePlayerSnapshot(),
            Target = BuildUnavailableTargetSnapshot(),
            Pet = BuildUnavailablePetSnapshot(),
            Scene = BuildUnavailableSceneSnapshot(),
            Inventory = BuildUnavailableInventorySnapshot()
        };
    }

    private static PlayerRuntimeSnapshot BuildUnavailablePlayerSnapshot() => new()
    {
        Quality = RuntimeSnapshotQuality.Unavailable,
        Source = NotStartedSource,
        ConfidencePercent = 0
    };

    private static TargetRuntimeSnapshot BuildUnavailableTargetSnapshot() => new()
    {
        Quality = RuntimeSnapshotQuality.Unavailable,
        Source = NotStartedSource,
        ConfidencePercent = 0,
        IsLost = null
    };

    private static PetRuntimeSnapshot BuildUnavailablePetSnapshot() => new()
    {
        Quality = RuntimeSnapshotQuality.Unavailable,
        Source = NotStartedSource,
        ConfidencePercent = 0
    };

    private static SceneRuntimeSnapshot BuildUnavailableSceneSnapshot() => new()
    {
        Quality = RuntimeSnapshotQuality.Unavailable,
        Source = NotStartedSource,
        ConfidencePercent = 0,
        SceneUsable = false,
        MapFieldReady = false,
        CoordinateFieldReady = false,
        ReturnPointFieldReady = false,
        GateReason = NotStartedMessage,
        MapNameSource = NotStartedSource,
        MapNameConfidencePercent = 0,
        CoordinateSource = NotStartedSource,
        CoordinateConfidencePercent = 0
    };

    private static InventoryRuntimeSnapshot BuildUnavailableInventorySnapshot() => new()
    {
        Quality = RuntimeSnapshotQuality.Unavailable,
        Source = NotStartedSource,
        ConfidencePercent = 0,
        IsOpen = null,
        UsedSlots = null,
        EmptySlots = null,
        TotalSlots = null,
        SellCandidateCount = 0,
        ProtectedCount = 0,
        Executed = false
    };
}
