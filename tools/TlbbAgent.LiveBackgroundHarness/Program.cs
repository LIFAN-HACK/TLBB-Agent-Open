using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using TlbbAgent.Contracts;
using TlbbAgent.Core;

namespace TlbbAgent.LiveBackgroundHarness;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var pid = ReadInt(args, "--pid");
        var role = ReadString(args, "--role");
        var semantic = ReadString(args, "--semantic");
        var rawKey = ReadString(args, "--raw-key");
        var toggle = args.Contains("--toggle", StringComparer.OrdinalIgnoreCase);
        var verifyPanel = ReadString(args, "--verify-panel");
        var requireVisual = args.Contains("--require-visual", StringComparer.OrdinalIgnoreCase);
        var label = Sanitize(ReadString(args, "--label"));
        if (pid <= 0 || role.Length == 0 || (semantic.Length == 0) == (rawKey.Length == 0))
        {
            Console.Error.WriteLine("Usage: --pid PID --role NAME (--semantic ID|--raw-key KEY) [--toggle] [--label NAME]");
            return 2;
        }

        using var process = Process.GetProcessById(pid);
        process.Refresh();
        var hwnd = process.MainWindowHandle;
        if (hwnd == nint.Zero || !GetClientRect(hwnd, out var client))
            return Fail("target_window_unavailable");
        if (!Path.GetFileName(process.MainModule?.FileName ?? string.Empty).Equals("Game.exe", StringComparison.OrdinalIgnoreCase))
            return Fail("target_process_is_not_game");

        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        var window = new BoundGameWindow
        {
            Hwnd = hwnd,
            Pid = process.Id,
            ProcessPath = process.MainModule?.FileName ?? string.Empty,
            ProcessName = process.ProcessName,
            Title = process.MainWindowTitle,
            ClientWidth = width,
            ClientHeight = height,
            Bitness = ClientBitness.X86
        };

        var log = new RuntimeLogService();
        var services = BuildServices();
        using var servicesDisposer = services as IDisposable;
        services.GetRequiredService<IAppInitService>().Initialize();
        var mapping = services.GetRequiredService<IGameHotkeyProfileService>().BindToRole(window, role);
        if (!mapping.Loaded)
            return Fail($"role_mapping_{mapping.Reason}");

        var key = rawKey;
        GameHotkeyBinding? binding = null;
        if (semantic.Length > 0)
        {
            binding = mapping.Bindings.FirstOrDefault(candidate =>
                candidate.IsDispatchable
                && string.Equals(candidate.SemanticId, semantic, StringComparison.Ordinal));
            if (binding is null)
                return Fail("semantic_current_mapping_unavailable");
            key = binding.Hotkey;
        }

        var evidenceRoot = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "artifacts", "live-background-input",
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{pid}-{(label.Length == 0 ? Sanitize(semantic.Length > 0 ? semantic : rawKey) : label)}");
        evidenceRoot = Path.GetFullPath(evidenceRoot);
        Directory.CreateDirectory(evidenceRoot);

        var capture = new WindowCaptureService(log);
        var before = CaptureRequired(capture, hwnd, "before");
        SavePng(before, Path.Combine(evidenceRoot, "before.png"));
        var foregroundBefore = GetForegroundWindow();
        using var input = new InputService(log, new BackgroundInputCapabilityService(), GetForegroundWindow)
        {
            DefaultDispatchMode = InputDispatchMode.PostMessage,
            YieldToUserInput = false
        };

        var keysSent = 0;
        var sent = input.SendKey(hwnd, key, InputDispatchMode.PostMessage);
        if (sent)
            keysSent++;
        Thread.Sleep(450);
        var after1 = CaptureRequired(capture, hwnd, "after-1");
        SavePng(after1, Path.Combine(evidenceRoot, "after-1.png"));
        Thread.Sleep(220);
        var after2 = CaptureRequired(capture, hwnd, "after-2");
        SavePng(after2, Path.Combine(evidenceRoot, "after-2.png"));
        var openVerification = VerifyPanel(services, verifyPanel, semantic, after1, after2);

        CaptureResult? restored1 = null;
        CaptureResult? restored2 = null;
        PanelVerificationResult? restoredVerification = null;
        var restoreSent = false;
        if (toggle && sent)
        {
            restoreSent = input.SendKey(hwnd, key, InputDispatchMode.PostMessage);
            if (restoreSent)
                keysSent++;
            Thread.Sleep(450);
            restored1 = CaptureRequired(capture, hwnd, "restored-1");
            SavePng(restored1, Path.Combine(evidenceRoot, "restored-1.png"));
            Thread.Sleep(220);
            restored2 = CaptureRequired(capture, hwnd, "restored-2");
            SavePng(restored2, Path.Combine(evidenceRoot, "restored-2.png"));
            restoredVerification = VerifyPanel(services, verifyPanel, semantic, restored1, restored2);
        }

        var foregroundAfter = GetForegroundWindow();
        var report = new
        {
            schemaVersion = 1,
            role,
            pid,
            hwnd = $"0x{hwnd.ToInt64():X}",
            clientSize = $"{width}x{height}",
            semantic = semantic.Length == 0 ? null : semantic,
            actionId = binding?.ActionId,
            currentKey = key,
            mappingSource = semantic.Length == 0 ? "explicit_control_key" : "Acce_Custom.ini:key1",
            mappingFingerprint = mapping.MappingFingerprint,
            dispatch = "PostMessage",
            sent,
            restoreSent,
            keysSent,
            globalSendInputCount = 0,
            systemMouseMoveCount = 0,
            foregroundBefore = $"0x{foregroundBefore.ToInt64():X}",
            foregroundAfter = $"0x{foregroundAfter.ToInt64():X}",
            foregroundUnchanged = foregroundBefore == foregroundAfter,
            targetWasBackground = foregroundBefore != hwnd,
            frameSequence = new
            {
                before = before.Sequence,
                after1 = after1.Sequence,
                after2 = after2.Sequence,
                restored1 = restored1?.Sequence,
                restored2 = restored2?.Sequence
            },
            visualDiff = new
            {
                beforeToAfter1 = Compare(before, after1),
                after1ToAfter2 = Compare(after1, after2),
                beforeToRestored1 = restored1 is null ? null : Compare(before, restored1),
                restored1ToRestored2 = restored1 is null || restored2 is null ? null : Compare(restored1, restored2)
            },
            verification = new
            {
                requested = ResolvePanelVerifier(verifyPanel, semantic),
                requireVisual,
                open = openVerification,
                restored = restoredVerification
            },
            logs = log.Entries.Select(entry => new { entry.Level, entry.Module, entry.Message }).ToArray()
        };
        File.WriteAllText(
            Path.Combine(evidenceRoot, "report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"EVIDENCE={evidenceRoot}");
        Console.WriteLine($"ROLE={role} PID={pid} HWND=0x{hwnd.ToInt64():X} SIZE={width}x{height}");
        Console.WriteLine($"KEY={key} SOURCE={(semantic.Length == 0 ? "explicit_control_key" : "Acce_Custom.ini:key1")} SENT={sent} RESTORE_SENT={restoreSent}");
        Console.WriteLine($"FOREGROUND_UNCHANGED={foregroundBefore == foregroundAfter} TARGET_WAS_BACKGROUND={foregroundBefore != hwnd}");
        Console.WriteLine($"DIFF_OPEN={Compare(before, after1).ChangedRatio:P2} STABLE_OPEN={Compare(after1, after2).ChangedRatio:P2}");
        if (openVerification is not null)
            Console.WriteLine($"VERIFY_OPEN={openVerification.Success} {openVerification.Message}");
        if (restored1 is not null && restored2 is not null)
        {
            Console.WriteLine($"DIFF_RESTORED={Compare(before, restored1).ChangedRatio:P2} STABLE_RESTORED={Compare(restored1, restored2).ChangedRatio:P2}");
            if (restoredVerification is not null)
                Console.WriteLine($"VERIFY_RESTORED={restoredVerification.Success} {restoredVerification.Message}");
        }

        var visualOk = !requireVisual
                       || (openVerification?.Success == true && (!toggle || restoredVerification?.Success == false));
        return sent && (!toggle || restoreSent) && foregroundBefore == foregroundAfter && visualOk ? 0 : 1;
    }

    private static IServiceProvider BuildServices()
    {
        var collection = new ServiceCollection();
        collection.AddTlbbCore();
        return collection.BuildServiceProvider();
    }

    private static PanelVerificationResult? VerifyPanel(
        IServiceProvider services,
        string explicitVerifier,
        string semantic,
        CaptureResult first,
        CaptureResult second)
    {
        var verifier = ResolvePanelVerifier(explicitVerifier, semantic);
        return verifier switch
        {
            "inventory" => VerifyInventory(services, second),
            "quest" => VerifyQuest(services, first, second),
            "" => null,
            _ => new PanelVerificationResult(false, verifier, $"unsupported verifier: {verifier}", null, null)
        };
    }

    private static string ResolvePanelVerifier(string explicitVerifier, string semantic)
    {
        if (!string.IsNullOrWhiteSpace(explicitVerifier))
            return explicitVerifier.Trim().ToLowerInvariant();

        return semantic switch
        {
            GameInputSemanticIds.InventoryPanel => "inventory",
            GameInputSemanticIds.QuestPanel => "quest",
            _ => string.Empty
        };
    }

    private static PanelVerificationResult VerifyInventory(IServiceProvider services, CaptureResult frame)
    {
        var result = services.GetRequiredService<IInventoryRecognitionService>().ProbeGridCandidates(frame, 2);
        var best = result.Candidates.OrderByDescending(item => item.Score).FirstOrDefault();
        return new PanelVerificationResult(
            result.Success,
            "inventory",
            result.Message,
            best?.Source,
            best is null ? null : new
            {
                best.Score,
                best.IsLikelyInventory,
                best.Grid.X,
                best.Grid.Y,
                best.Grid.Columns,
                best.Grid.Rows,
                best.Grid.CellWidth,
                best.Grid.CellHeight
            });
    }

    private static PanelVerificationResult VerifyQuest(IServiceProvider services, CaptureResult first, CaptureResult second)
    {
        var detector = services.GetRequiredService<IInteractionWindowDetectService>();
        var firstDetection = detector.Detect(first, includeQuestLogPanel: true);
        var secondDetection = detector.Detect(second, includeQuestLogPanel: true);
        var success = firstDetection.QuestLogPanelState == VisionTriState.Present
                      && secondDetection.QuestLogPanelState == VisionTriState.Present;
        var best = secondDetection.Detections
            .Where(item => string.Equals(item.ClassName, "quest_log_panel", StringComparison.Ordinal))
            .OrderByDescending(item => item.Confidence)
            .FirstOrDefault();
        return new PanelVerificationResult(
            success,
            "quest",
            $"quest states: {firstDetection.QuestLogPanelState}/{secondDetection.QuestLogPanelState}; {secondDetection.Message}",
            secondDetection.ModelVersion,
            best is null ? null : new
            {
                best.ClassName,
                best.Confidence,
                best.Box.X,
                best.Box.Y,
                best.Box.Width,
                best.Box.Height
            });
    }

    private static CaptureResult CaptureRequired(IWindowCaptureService capture, nint hwnd, string phase) =>
        capture.CaptureClient(hwnd) ?? throw new InvalidOperationException($"capture_failed:{phase}");

    private static FrameDiff Compare(CaptureResult left, CaptureResult right)
    {
        if (left.Width != right.Width || left.Height != right.Height)
            return new FrameDiff(1, 255);

        long changed = 0;
        long samples = 0;
        long absolute = 0;
        for (var y = left.Height / 10; y < left.Height * 9 / 10; y += 2)
        {
            for (var x = left.Width / 10; x < left.Width * 9 / 10; x += 2)
            {
                var offset = (y * left.Width + x) * 4;
                var delta = Math.Abs(left.Pixels[offset] - right.Pixels[offset])
                            + Math.Abs(left.Pixels[offset + 1] - right.Pixels[offset + 1])
                            + Math.Abs(left.Pixels[offset + 2] - right.Pixels[offset + 2]);
                absolute += delta;
                if (delta >= 72)
                    changed++;
                samples++;
            }
        }

        return new FrameDiff(
            samples == 0 ? 0 : changed / (double)samples,
            samples == 0 ? 0 : absolute / (double)(samples * 3));
    }

    private static void SavePng(CaptureResult capture, string path)
    {
        var source = BitmapSource.Create(
            capture.Width,
            capture.Height,
            96,
            96,
            PixelFormats.Bgr32,
            null,
            capture.Pixels,
            capture.Width * 4);
        source.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static int ReadInt(string[] args, string option) =>
        int.TryParse(ReadString(args, option), out var value) ? value : 0;

    private static string ReadString(string[] args, string option)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
                return args[index + 1].Trim();
        }
        return string.Empty;
    }

    private static string Sanitize(string value)
    {
        var chars = (value ?? string.Empty)
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }

    private static int Fail(string reason)
    {
        Console.Error.WriteLine($"FAIL: {reason}");
        return 1;
    }

    private sealed record FrameDiff(double ChangedRatio, double MeanChannelDelta);

    private sealed record PanelVerificationResult(
        bool Success,
        string Verifier,
        string Message,
        string? Source,
        object? Details);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);
}
