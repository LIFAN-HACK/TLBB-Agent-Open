using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TlbbAgent.Contracts;
using TlbbAgent.Core.Captcha;

namespace TlbbAgent.Core.Perception;

/// <summary>
/// L2 OpenText via RapidOCR / PP-OCR (Python worker). Open vocabulary: crop → string.
/// Later swap to ONNX in-process; same IOpenTextRecognizer contract.
/// </summary>
public sealed class RapidOcrOpenTextRecognizer : IOpenTextRecognizer, IDisposable
{
    private const string Module = "OpenText";
    private readonly ILogService _log;
    private readonly IModelArtifactRegistry _artifacts;
    private readonly object _gate = new();
    private bool _probed;
    private bool _available;
    private string _pythonPath = "";
    private string _workerPath = "";
    private string _engineId = "rapidocr_onnxruntime";
    private Process? _workerProcess;
    private string _lastWorkerError = "";

    public RapidOcrOpenTextRecognizer(ILogService log, IModelArtifactRegistry artifacts)
    {
        _log = log;
        _artifacts = artifacts;
    }

    public bool IsAvailable
    {
        get
        {
            EnsureProbed();
            return _available;
        }
    }

    public string EngineId
    {
        get
        {
            EnsureProbed();
            return _engineId;
        }
    }

    public OpenTextResult Recognize(CaptureResult crop, OpenTextSlot slot = OpenTextSlot.Generic)
    {
        EnsureProbed();
        if (!_available)
            return OpenTextResult.Empty(slot, "L2 OpenText worker unavailable.", _engineId);

        if (crop.Width < 4 || crop.Height < 4 || crop.Pixels.Length < 16)
            return OpenTextResult.Empty(slot, "Crop too small.", _engineId);

        string? tempPng = null;
        try
        {
            tempPng = Path.Combine(Path.GetTempPath(), $"tlbb_opentext_{Guid.NewGuid():N}.png");
            File.WriteAllBytes(tempPng, PngEncoder.Encode(crop.Width, crop.Height, crop.Pixels));

            var mode = slot is OpenTextSlot.TargetName or OpenTextSlot.MapName or OpenTextSlot.ItemName
                ? "rec_only"
                : "auto";

            lock (_gate)
            {
                if (!EnsureWorkerRunningLocked())
                    return OpenTextResult.Empty(slot, "Failed to start persistent OpenText worker.", _engineId);

                var request = JsonSerializer.Serialize(new
                {
                    image = tempPng,
                    slot = ToSlotArg(slot),
                    mode
                });

                _workerProcess!.StandardInput.WriteLine(request);
                _workerProcess.StandardInput.Flush();
                var stdout = ReadJsonLineWithTimeout(
                    _workerProcess,
                    5000,
                    "ok",
                    out var ignoredOutput);
                if (!string.IsNullOrWhiteSpace(ignoredOutput))
                    _log.Trace(Module, $"Ignored non-protocol worker output: {ignoredOutput}");
                if (string.IsNullOrWhiteSpace(stdout))
                {
                    var workerDetail = string.Join(
                        " | ",
                        new[] { ignoredOutput, _lastWorkerError }
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Select(Trim));
                    var detail = string.IsNullOrWhiteSpace(workerDetail)
                        ? "OpenText worker returned no protocol response."
                        : $"OpenText worker returned no protocol response: {workerDetail}";
                    RestartWorkerLocked();
                    return OpenTextResult.Empty(slot, detail, _engineId);
                }

                return ParseResult(stdout, slot);
            }
        }
        catch (Exception ex)
        {
            _log.Warn(Module, $"OpenText exception: {ex.Message}");
            lock (_gate)
                RestartWorkerLocked();
            return OpenTextResult.Empty(slot, ex.Message, _engineId);
        }
        finally
        {
            if (tempPng is not null)
            {
                try { File.Delete(tempPng); } catch { /* ignore */ }
            }
        }
    }

    public bool TryWarmup(out string message)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!IsAvailable)
        {
            stopwatch.Stop();
            message = $"engine={_engineId}, unavailable, elapsed={stopwatch.Elapsed.TotalMilliseconds:0}ms";
            return false;
        }

        var pixels = new byte[32 * 16 * 4];
        for (var index = 3; index < pixels.Length; index += 4)
            pixels[index] = byte.MaxValue;

        var result = Recognize(
            new CaptureResult(32, 16, pixels)
            {
                Sequence = -1,
                CapturedAt = DateTimeOffset.UtcNow,
                CaptureSource = "runtime-warmup"
            },
            OpenTextSlot.Generic);
        stopwatch.Stop();
        var ready = result.Source == PerceptionSourceKind.OpenText;
        message = ready
            ? $"engine={result.Engine}, elapsed={stopwatch.Elapsed.TotalMilliseconds:0}ms"
            : $"engine={_engineId}, elapsed={stopwatch.Elapsed.TotalMilliseconds:0}ms, reason={Trim(result.Message)}";
        return ready;
    }

    private void EnsureProbed()
    {
        if (_probed) return;
        lock (_gate)
        {
            if (_probed) return;
            _probed = true;

            var artifact = _artifacts.FindL2OpenTextModel();
            _workerPath = ResolveWorkerPath(artifact.Directory);
            _pythonPath = ResolvePythonPath();

            if (string.IsNullOrWhiteSpace(_workerPath) || !File.Exists(_workerPath))
            {
                _available = false;
                _log.Warn(Module, "L2 OpenText worker script not found.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_pythonPath) || !File.Exists(_pythonPath))
            {
                _available = false;
                _log.Warn(Module, "Python for L2 OpenText not found (set TLBB_AGENT_PYTHON).");
                return;
            }

            try
            {
                _available = EnsureWorkerRunningLocked();
                if (_available)
                    _log.Info(Module, $"L2 OpenText 常驻进程已就绪：engine={_engineId} python={_pythonPath}");
                else
                    _log.Warn(Module, $"L2 OpenText 常驻进程启动失败：{Trim(_lastWorkerError)}");
            }
            catch (Exception ex)
            {
                _available = false;
                _log.Warn(Module, $"OpenText probe failed: {ex.Message}");
            }
        }
    }

    private OpenTextResult ParseResult(string json, OpenTextSlot slot)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
        var text = root.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "";
        var confidence = root.TryGetProperty("confidence", out var confidenceEl) ? confidenceEl.GetDouble() : 0.0;
        var engine = root.TryGetProperty("engine", out var engineEl) ? engineEl.GetString() ?? _engineId : _engineId;
        var error = root.TryGetProperty("error", out var errorEl) ? errorEl.GetString() ?? "" : "";

        if (!ok && string.IsNullOrWhiteSpace(text))
            return OpenTextResult.Empty(slot, string.IsNullOrWhiteSpace(error) ? "OpenText failed." : error, engine);

        return new OpenTextResult(
            Text: text.Trim(),
            Confidence: confidence,
            Slot: slot,
            Source: PerceptionSourceKind.OpenText,
            Engine: engine,
            Message: string.IsNullOrWhiteSpace(error) ? "ok" : error);
    }

    private bool EnsureWorkerRunningLocked()
    {
        if (_workerProcess is { HasExited: false })
            return true;

        StopWorkerLocked();
        _lastWorkerError = "";
        var psi = new ProcessStartInfo
        {
            FileName = _pythonPath,
            Arguments = $"\"{_workerPath}\" --serve",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var process = Process.Start(psi);
        if (process is null)
        {
            _lastWorkerError = "Process.Start returned null.";
            return false;
        }

        _workerProcess = process;
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
                _lastWorkerError = args.Data;
        };
        process.BeginErrorReadLine();

        var readyLine = ReadJsonLineWithTimeout(
            process,
            20_000,
            "ready",
            out var ignoredOutput);
        if (string.IsNullOrWhiteSpace(readyLine))
        {
            _lastWorkerError = string.Join(
                " | ",
                new[]
                {
                    "Worker ready handshake timed out.",
                    ignoredOutput,
                    _lastWorkerError
                }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(Trim));
            StopWorkerLocked();
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(readyLine);
            var root = doc.RootElement;
            var ready = root.TryGetProperty("ready", out var readyEl) && readyEl.GetBoolean();
            if (root.TryGetProperty("engine", out var engineEl))
                _engineId = engineEl.GetString() ?? _engineId;
            if (!ready && root.TryGetProperty("error", out var errorEl))
                _lastWorkerError = errorEl.GetString() ?? "Worker reported not ready.";
            if (!ready)
                StopWorkerLocked();
            return ready;
        }
        catch (Exception ex)
        {
            _lastWorkerError = $"Invalid worker ready response: {ex.Message}";
            StopWorkerLocked();
            return false;
        }
    }

    private static string? ReadLineWithTimeout(Process process, int timeoutMs)
    {
        var readTask = process.StandardOutput.ReadLineAsync();
        if (!readTask.Wait(timeoutMs))
            return null;
        return readTask.GetAwaiter().GetResult();
    }

    private static string? ReadJsonLineWithTimeout(
        Process process,
        int timeoutMs,
        string requiredProperty,
        out string ignoredOutput)
    {
        var ignored = new List<string>();
        var deadline = Environment.TickCount64 + Math.Max(1, timeoutMs);
        while (Environment.TickCount64 < deadline)
        {
            var remaining = (int)Math.Clamp(deadline - Environment.TickCount64, 1, int.MaxValue);
            var line = ReadLineWithTimeout(process, remaining);
            if (line is null)
                break;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty(requiredProperty, out _))
                {
                    ignoredOutput = string.Join(" | ", ignored);
                    return line;
                }
            }
            catch (JsonException)
            {
                // Native inference libraries can emit one-time diagnostics on stdout.
            }

            if (ignored.Count < 3)
                ignored.Add(Trim(line));
        }

        ignoredOutput = string.Join(" | ", ignored);
        return null;
    }

    private void RestartWorkerLocked() => StopWorkerLocked();

    private void StopWorkerLocked()
    {
        var process = _workerProcess;
        _workerProcess = null;
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                try { process.StandardInput.Close(); } catch { /* ignore */ }
                if (!process.WaitForExit(500))
                    process.Kill(entireProcessTree: true);
            }
        }
        catch { /* ignore */ }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
            StopWorkerLocked();
    }

    private static string ResolveWorkerPath(string artifactDir)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "opentext_worker.py"),
            Path.Combine(AppContext.BaseDirectory, "tools", "opentext_worker", "opentext_worker.py"),
            // repo layout: src/TlbbAgent.Core/bin/... → tools/
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "opentext_worker", "opentext_worker.py")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "tools", "opentext_worker", "opentext_worker.py")),
            Path.Combine(artifactDir, "opentext_worker.py"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "TLBB 视觉模型", "tools", "opentext_worker.py")
        };

        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c) && File.Exists(c))
                return c;
        }

        return candidates[0];
    }

    private static string ResolvePythonPath()
    {
        var env = Environment.GetEnvironmentVariable("TLBB_AGENT_PYTHON");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Python", "Python313", "python.exe");
        if (File.Exists(local))
            return local;

        var local312 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Python", "Python312", "python.exe");
        if (File.Exists(local312))
            return local312;

        return "python";
    }

    private static string ToSlotArg(OpenTextSlot slot) => slot switch
    {
        OpenTextSlot.TargetName => "target_name",
        OpenTextSlot.MapName => "map_name",
        OpenTextSlot.ItemName => "item_name",
        OpenTextSlot.DialogOption => "dialog_option",
        OpenTextSlot.Toast => "toast",
        OpenTextSlot.QuestTracker => "quest_tracker",
        OpenTextSlot.CoordText => "coord_text",
        _ => "generic"
    };

    private static string Trim(string s) =>
        s.Length <= 200 ? s : s[..200];
}
