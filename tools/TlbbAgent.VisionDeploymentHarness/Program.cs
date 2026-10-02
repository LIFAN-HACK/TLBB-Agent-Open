using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TlbbAgent.Contracts;
using TlbbAgent.Core;
using TlbbAgent.Core.Perception;

namespace TlbbAgent.VisionDeploymentHarness;

internal static class Program
{
    private const long MaximumUploadBytes = 32L * 1024 * 1024;
    private const string SecretEnvironmentVariable = "TLBB_VISION_CLOUD_SECRET";
    private const string BuffAnchorSha256 = "07f2f73ef15abd74225980e10719ed83f5216ccd1eaa7bb3b2308a550c332610";
    private const string InteractionWindowSha256 = "9a7273fde40161ced55a82a3b56ba753f3d4dab0a642fafed40a270616fbc7a0";
    private const string QuestLogPanelSha256 = "15ab659c94db902aba18a9ebe146465d4a1baa1accf1223d0dd2964dd9795f4e";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            if (HasFlag(args, "--serve"))
                return await RunServerModeAsync(args);

            return await RunBenchmarkModeAsync(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"VISION_DEPLOYMENT_HARNESS FAIL: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunServerModeAsync(string[] args)
    {
        var listen = GetOption(args, "--listen") ?? "http://127.0.0.1:5088";
        var secret = ResolveSecret(args, allowEphemeral: false);
        await using var server = await VisionCloudServer.StartAsync(listen, secret, CancellationToken.None);

        Console.WriteLine($"VISION_CLOUD_SERVER READY endpoint={listen.TrimEnd('/')}/v1/vision/analyze api={VisionRemoteProtocol.ApiVersion}");
        Console.WriteLine("截图不写入业务日志；按 Ctrl+C 停止。公网部署必须由 TLS 反向代理或 HTTPS 入口保护。");

        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopped.TrySetResult();
        };
        await stopped.Task;
        return 0;
    }

    private static async Task<int> RunBenchmarkModeAsync(string[] args)
    {
        var auxiliaryArtifactsPassed = ValidatePackagedAuxiliaryArtifacts();
        var imagePath = ResolveImagePath(args);
        var iterations = ParsePositiveInt(GetOption(args, "--iterations"), 5);
        var encoding = ParseEncoding(GetOption(args, "--encoding"));
        var jpegQuality = Math.Clamp(ParsePositiveInt(GetOption(args, "--jpeg-quality"), 85), 40, 100);
        var endpointOption = GetOption(args, "--endpoint");
        var secret = ResolveSecret(args, allowEphemeral: endpointOption is null);

        Console.WriteLine($"FIXTURE path={imagePath}");
        var sourceFrame = ImageCodec.Decode(File.ReadAllBytes(imagePath)) with
        {
            Sequence = 1,
            CapturedAt = DateTimeOffset.UtcNow,
            CaptureSource = "deployment-benchmark"
        };

        var local = RunLocalBenchmark(sourceFrame, iterations);
        Console.WriteLine(
            $"LOCAL iterations={iterations} total_p50={Percentile(local.TotalMilliseconds, 0.50):0.0}ms " +
            $"total_p95={Percentile(local.TotalMilliseconds, 0.95):0.0}ms " +
            $"l1_p50={Percentile(local.LayoutMilliseconds, 0.50):0.0}ms " +
            $"l2_p50={Percentile(local.SlotMilliseconds, 0.50):0.0}ms model={local.ModelSha256}");

        VisionCloudServer? loopbackServer = null;
        Uri endpoint;
        if (endpointOption is null)
        {
            var port = FindFreeTcpPort();
            var listen = $"http://127.0.0.1:{port}";
            loopbackServer = await VisionCloudServer.StartAsync(listen, secret, CancellationToken.None);
            endpoint = new Uri(listen);
        }
        else
        {
            endpoint = new Uri(endpointOption, UriKind.Absolute);
            if (!endpoint.IsLoopback && !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("非本机云端地址必须使用 HTTPS。");
        }

        await using (loopbackServer)
        {
            using var client = new VisionCloudClient(endpoint, secret);
            var remote = await RunRemoteBenchmarkAsync(client, sourceFrame, encoding, jpegQuality, iterations);
            var parity = Compare(local.LastLayout, local.LastCore, remote.LastResponse.Layout, remote.LastResponse.Core);

            Console.WriteLine(
                $"REMOTE transport={(endpoint.IsLoopback ? "loopback" : "external")} encoding={encoding} " +
                $"payload_avg={FormatBytes(remote.PayloadBytes.Average())} encode_p50={Percentile(remote.EncodeMilliseconds, 0.50):0.0}ms " +
                $"server_decode_p50={Percentile(remote.ServerDecodeMilliseconds, 0.50):0.0}ms " +
                $"server_infer_p50={Percentile(remote.ServerInferenceMilliseconds, 0.50):0.0}ms " +
                $"e2e_p50={Percentile(remote.EndToEndMilliseconds, 0.50):0.0}ms " +
                $"e2e_p95={Percentile(remote.EndToEndMilliseconds, 0.95):0.0}ms response_avg={FormatBytes(remote.ResponseBytes.Average())}");
            Console.WriteLine(
                $"PARITY operational={parity.OperationallyEquivalent} exact={parity.ExactlyEquivalent} " +
                $"details={parity.Message}");

            PrintNetworkProjections(remote);

            var security = await RunSecurityTestsAsync(client, sourceFrame, encoding, jpegQuality, remote.LastRequest, remote.LastResponse);
            Console.WriteLine(
                $"SECURITY signed={security.ValidSignedResponse} tampered={security.TamperedUploadRejected} " +
                $"replay={security.ReplayRejected} stale={security.StaleRequestRejected} " +
                $"expired_action={security.ExpiredActionRejected} bounds={security.OutOfBoundsRejected} " +
                $"model_pin={security.ModelMismatchRejected}");

            var passed = auxiliaryArtifactsPassed && parity.OperationallyEquivalent && security.AllPassed;
            Console.WriteLine(passed ? "VISION_DEPLOYMENT_HARNESS PASS" : "VISION_DEPLOYMENT_HARNESS FAIL");
            return passed ? 0 : 1;
        }
    }

    private static bool ValidatePackagedAuxiliaryArtifacts()
    {
        var log = new RuntimeLogService();
        var registry = new ModelArtifactRegistry();
        var right = registry.FindRightSkillBarModels();
        var buff = registry.FindBuffAnchorModel();
        var interaction = registry.FindInteractionWindowModel();
        var questLogPanel = registry.FindQuestLogPanelModel();
        var controls = new InteractionControlCatalogService(log);

        var right640 = right.IsAvailable && HashMatches(right.Model640Path, right.Sha256640);
        var right1280 = right.IsAvailable && HashMatches(right.Model1280Path, right.Sha2561280);
        var buffOk = buff.IsAvailable && HashMatches(buff.ModelPath, BuffAnchorSha256);
        var interactionOk = interaction.IsAvailable
                            && HashMatches(interaction.ModelPath, InteractionWindowSha256);
        var questLogPanelOk = questLogPanel.IsAvailable
                              && HashMatches(questLogPanel.ModelPath, QuestLogPanelSha256)
                              && !ReadRuntimeAuthorized(questLogPanel.ConfigPath);
        var controlsOk = controls.IsAvailable && !controls.RuntimeAuthorized;
        var passed = right640 && right1280 && buffOk && interactionOk && questLogPanelOk && controlsOk;

        Console.WriteLine(
            $"AUXILIARY_ARTIFACTS right640={right640} right1280={right1280} " +
            $"buff={buffOk} interaction={interactionOk} quest_log_panel={questLogPanelOk} controls={controlsOk} " +
            $"controls_authorized={controls.RuntimeAuthorized}");
        return passed;
    }

    private static bool HashMatches(string path, string expected) =>
        File.Exists(path)
        && string.Equals(Sha256(File.ReadAllBytes(path)), expected, StringComparison.OrdinalIgnoreCase);

    private static bool ReadRuntimeAuthorized(string configPath)
    {
        if (!File.Exists(configPath))
            return false;
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        return document.RootElement.TryGetProperty("runtimeAuthorized", out var value)
               && value.ValueKind == JsonValueKind.True;
    }

    private static LocalBenchmarkResult RunLocalBenchmark(CaptureResult sourceFrame, int iterations)
    {
        var log = new RuntimeLogService();
        var registry = new ModelArtifactRegistry();
        using var layout = new OnnxLayoutDetectService(registry, log);
        using var slots = new VisionV2SlotService(registry, log);
        if (!slots.IsAvailable)
            throw new InvalidOperationException("本地 V2 插槽宿主不可用。");

        var artifact = registry.FindL1LayoutModel();
        var modelHash = Sha256(File.ReadAllBytes(artifact.ModelPath));
        var expectedModelHash = ReadDeclaredModelSha256(artifact);
        if (!string.Equals(modelHash, expectedModelHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"本地模型哈希不匹配：{modelHash}");

        var warmFrame = sourceFrame with { Sequence = 1, CapturedAt = DateTimeOffset.UtcNow };
        var warmLayout = layout.Detect(warmFrame);
        _ = slots.AnalyzeCore("local-benchmark", warmFrame.Sequence, warmFrame, warmLayout);

        var totals = new List<double>();
        var layoutTimes = new List<double>();
        var slotTimes = new List<double>();
        LayoutSnapshot lastLayout = warmLayout;
        VisionV2CoreSlots lastCore = new();

        for (var index = 0; index < iterations; index++)
        {
            var frame = sourceFrame with
            {
                Sequence = index + 10,
                CapturedAt = DateTimeOffset.UtcNow
            };
            var totalWatch = Stopwatch.StartNew();
            var layoutWatch = Stopwatch.StartNew();
            lastLayout = layout.Detect(frame);
            layoutWatch.Stop();
            var slotWatch = Stopwatch.StartNew();
            lastCore = slots.AnalyzeCore("local-benchmark", frame.Sequence, frame, lastLayout);
            slotWatch.Stop();
            totalWatch.Stop();

            totals.Add(totalWatch.Elapsed.TotalMilliseconds);
            layoutTimes.Add(layoutWatch.Elapsed.TotalMilliseconds);
            slotTimes.Add(slotWatch.Elapsed.TotalMilliseconds);
        }

        return new LocalBenchmarkResult(totals, layoutTimes, slotTimes, lastLayout, lastCore, modelHash);
    }

    private static async Task<RemoteBenchmarkResult> RunRemoteBenchmarkAsync(
        VisionCloudClient client,
        CaptureResult sourceFrame,
        VisionFrameEncoding encoding,
        int jpegQuality,
        int iterations)
    {
        var warm = await client.AnalyzeAsync(sourceFrame with
        {
            Sequence = 1,
            CapturedAt = DateTimeOffset.UtcNow
        }, encoding, jpegQuality);
        EnsureSuccessful(warm);

        var result = new RemoteBenchmarkResult();
        for (var index = 0; index < iterations; index++)
        {
            var frame = sourceFrame with
            {
                Sequence = index + 100,
                CapturedAt = DateTimeOffset.UtcNow
            };
            var call = await client.AnalyzeAsync(frame, encoding, jpegQuality);
            EnsureSuccessful(call);
            result.EncodeMilliseconds.Add(call.EncodeMilliseconds);
            result.EndToEndMilliseconds.Add(call.EndToEndMilliseconds);
            result.ServerDecodeMilliseconds.Add(call.Response!.Timing.DecodeMilliseconds);
            result.ServerInferenceMilliseconds.Add(
                call.Response.Timing.LayoutMilliseconds + call.Response.Timing.SlotMilliseconds);
            result.PayloadBytes.Add(call.PayloadBytes);
            result.ResponseBytes.Add(call.ResponseBytes);
            result.LastRequest = call.Request!;
            result.LastResponse = call.Response;
        }
        return result;
    }

    private static async Task<SecurityTestResult> RunSecurityTestsAsync(
        VisionCloudClient client,
        CaptureResult frame,
        VisionFrameEncoding encoding,
        int jpegQuality,
        VisionRemoteFrameDescriptor request,
        VisionRemoteInferenceResponse response)
    {
        var signed = await client.AnalyzeAsync(frame with
        {
            Sequence = frame.Sequence + 1000,
            CapturedAt = DateTimeOffset.UtcNow
        }, encoding, jpegQuality);
        var validSignedResponse = signed.IsSuccess && signed.Safety?.IsAccepted == true;

        var tamperedUploadRejected = !await client.SendAdversarialAsync(
            frame,
            encoding,
            jpegQuality,
            AdversarialRequest.TamperedBody);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var firstReplayAttempt = await client.SendAdversarialAsync(
            frame,
            encoding,
            jpegQuality,
            AdversarialRequest.None,
            nonce);
        var replayRejected = firstReplayAttempt && !await client.SendAdversarialAsync(
            frame,
            encoding,
            jpegQuality,
            AdversarialRequest.None,
            nonce);
        var staleRequestRejected = !await client.SendAdversarialAsync(
            frame,
            encoding,
            jpegQuality,
            AdversarialRequest.StaleTimestamp);

        var gate = new VisionRemoteResponseSafetyGate();
        var now = DateTimeOffset.UtcNow;
        var currentRequest = request with { CapturedAt = now };
        var validActionResponse = response with
        {
            CapturedAt = now,
            ProcessedAt = now,
            ValidUntil = now.AddSeconds(1),
            ActionProposals = new[]
            {
                new VisionRemoteActionProposal
                {
                    Action = VisionActionKind.Click,
                    ClientX = 100,
                    ClientY = 100,
                    Confidence = 0.95,
                    ExpiresAt = now.AddMilliseconds(500),
                    Reason = "offline safety test"
                }
            }
        };
        var validGate = gate.Validate(
            currentRequest,
            validActionResponse,
            response.Width,
            response.Height,
            response.ModelSha256,
            now);
        validSignedResponse &= validGate.IsAccepted && validGate.AuthorizedActions.Count == 1;

        var expiredActionRejected = !gate.Validate(
            currentRequest,
            validActionResponse with
            {
                ActionProposals = new[]
                {
                    validActionResponse.ActionProposals[0] with { ExpiresAt = now.AddMilliseconds(-1) }
                }
            },
            response.Width,
            response.Height,
            response.ModelSha256,
            now).IsAccepted;
        var outOfBoundsRejected = !gate.Validate(
            currentRequest,
            validActionResponse with
            {
                ActionProposals = new[]
                {
                    validActionResponse.ActionProposals[0] with { ClientX = response.Width + 1 }
                }
            },
            response.Width,
            response.Height,
            response.ModelSha256,
            now).IsAccepted;
        var modelMismatchRejected = !gate.Validate(
            currentRequest,
            validActionResponse,
            response.Width,
            response.Height,
            new string('0', 64),
            now).IsAccepted;

        return new SecurityTestResult(
            validSignedResponse,
            tamperedUploadRejected,
            replayRejected,
            staleRequestRejected,
            expiredActionRejected,
            outOfBoundsRejected,
            modelMismatchRejected);
    }

    private static void PrintNetworkProjections(RemoteBenchmarkResult remote)
    {
        var payloadBytes = remote.PayloadBytes.Average();
        var encodeMs = Percentile(remote.EncodeMilliseconds, 0.50);
        var serverMs = Percentile(remote.ServerDecodeMilliseconds, 0.50)
            + Percentile(remote.ServerInferenceMilliseconds, 0.50);

        foreach (var bandwidthMbps in new[] { 10, 20, 50, 100 })
        {
            foreach (var roundTripMs in new[] { 20, 50 })
            {
                var uploadMs = payloadBytes * 8.0 / (bandwidthMbps * 1_000_000.0) * 1000.0;
                var projected = encodeMs + uploadMs + roundTripMs + serverMs;
                Console.WriteLine(
                    $"PROJECTION bandwidth={bandwidthMbps}Mbps rtt={roundTripMs}ms " +
                    $"upload={uploadMs:0.0}ms e2e={projected:0.0}ms");
            }
        }
    }

    private static ParityResult Compare(
        LayoutSnapshot localLayout,
        VisionV2CoreSlots localCore,
        LayoutSnapshot remoteLayout,
        VisionV2CoreSlots remoteCore)
    {
        var exactDifferences = new List<string>();
        var blockingDifferences = new List<string>();
        foreach (var @class in new[]
                 {
                     LayoutClass.PlayerStatusPanel,
                     LayoutClass.PetStatusPanel,
                     LayoutClass.SkillBar,
                     LayoutClass.Minimap
                 })
        {
            var local = localLayout.Best(@class)?.Box;
            var remote = remoteLayout.Best(@class)?.Box;
            if (!BoxesEquivalent(local, remote, 0))
                exactDifferences.Add($"{@class} bbox");
            if (!BoxesEquivalent(local, remote, 3))
                blockingDifferences.Add($"{@class} bbox drift >3px");
        }

        if (localCore.SelfBars.PlayerHpPercent != remoteCore.SelfBars.PlayerHpPercent)
            exactDifferences.Add($"player hp {localCore.SelfBars.PlayerHpPercent}->{remoteCore.SelfBars.PlayerHpPercent}");
        if (!PercentEquivalent(localCore.SelfBars.PlayerHpPercent, remoteCore.SelfBars.PlayerHpPercent, 2))
            blockingDifferences.Add("player hp drift >2pp");
        if (localCore.SelfBars.PlayerMpPercent != remoteCore.SelfBars.PlayerMpPercent)
            exactDifferences.Add($"player mp {localCore.SelfBars.PlayerMpPercent}->{remoteCore.SelfBars.PlayerMpPercent}");
        if (!PercentEquivalent(localCore.SelfBars.PlayerMpPercent, remoteCore.SelfBars.PlayerMpPercent, 2))
            blockingDifferences.Add("player mp drift >2pp");
        if (localCore.SelfBars.PetHpPercent != remoteCore.SelfBars.PetHpPercent)
            exactDifferences.Add($"pet hp {localCore.SelfBars.PetHpPercent}->{remoteCore.SelfBars.PetHpPercent}");
        if (!PercentEquivalent(localCore.SelfBars.PetHpPercent, remoteCore.SelfBars.PetHpPercent, 2))
            blockingDifferences.Add("pet hp drift >2pp");
        if (localCore.SelfBars.PetState != remoteCore.SelfBars.PetState)
        {
            exactDifferences.Add("pet state");
            blockingDifferences.Add("pet state");
        }
        if (localCore.SkillBar.VisualSlots.Count != remoteCore.SkillBar.VisualSlots.Count)
        {
            exactDifferences.Add("skill slot count");
            blockingDifferences.Add("skill slot count");
        }
        else
        {
            for (var index = 0; index < localCore.SkillBar.VisualSlots.Count; index++)
            {
                if (localCore.SkillBar.VisualSlots[index].Cooldown == remoteCore.SkillBar.VisualSlots[index].Cooldown)
                    continue;
                exactDifferences.Add($"skill {index + 1} cooldown");
                blockingDifferences.Add($"skill {index + 1} cooldown");
            }
        }
        if (localCore.Minimap.NearbyMonsterState != remoteCore.Minimap.NearbyMonsterState)
        {
            exactDifferences.Add("minimap monster state");
            blockingDifferences.Add("minimap monster state");
        }
        if (localCore.Minimap.X != remoteCore.Minimap.X || localCore.Minimap.Y != remoteCore.Minimap.Y)
        {
            exactDifferences.Add("minimap coordinate");
            blockingDifferences.Add("minimap coordinate");
        }

        if (exactDifferences.Count == 0)
            return new ParityResult(true, true, "same semantic output");
        if (blockingDifferences.Count == 0)
            return new ParityResult(true, false, $"within runtime tolerance: {string.Join(", ", exactDifferences)}");
        return new ParityResult(
            false,
            false,
            $"blocking: {string.Join(", ", blockingDifferences)}; exact drift: {string.Join(", ", exactDifferences)}");
    }

    private static bool PercentEquivalent(int? left, int? right, int tolerance)
    {
        if (left is null || right is null)
            return left is null && right is null;
        return Math.Abs(left.Value - right.Value) <= tolerance;
    }

    private static bool BoxesEquivalent(VisionBox? left, VisionBox? right, int tolerance)
    {
        if (left is null || right is null)
            return left is null && right is null;
        return Math.Abs(left.X - right.X) <= tolerance
            && Math.Abs(left.Y - right.Y) <= tolerance
            && Math.Abs(left.Width - right.Width) <= tolerance
            && Math.Abs(left.Height - right.Height) <= tolerance;
    }

    private static void EnsureSuccessful(RemoteCallResult result)
    {
        if (!result.IsSuccess || result.Response is null || result.Safety?.IsAccepted != true)
            throw new InvalidOperationException(result.Error ?? result.Safety?.Reason ?? "云端推理调用失败。");
    }

    private static string ResolveImagePath(string[] args)
    {
        var explicitImage = GetOption(args, "--image");
        if (!string.IsNullOrWhiteSpace(explicitImage))
        {
            var fullPath = Path.GetFullPath(explicitImage);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("指定测试截图不存在。", fullPath);
            return fullPath;
        }

        var visionRoot = GetOption(args, "--vision-root")
            ?? Environment.GetEnvironmentVariable("TLBB_VISION_ROOT")
            ?? Path.Combine(Environment.CurrentDirectory, "vision-workspace");
        var datasetRoot = Path.Combine(
            visionRoot,
            "train",
            "packages",
            "layout_v2_new_modules_r3_pet_clean",
            "dataset");
        var testList = Path.Combine(datasetRoot, "splits", "test.txt");
        if (!File.Exists(testList))
            throw new FileNotFoundException("未找到 V2 测试集索引。", testList);

        var image = File.ReadLines(testList)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => Path.Combine(datasetRoot, line.Trim().Replace('/', Path.DirectorySeparatorChar)))
            .FirstOrDefault(File.Exists);
        return image ?? throw new FileNotFoundException("V2 测试集中没有可用截图。", testList);
    }

    private static VisionFrameEncoding ParseEncoding(string? value) =>
        string.Equals(value, "jpeg", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "jpg", StringComparison.OrdinalIgnoreCase)
            ? VisionFrameEncoding.Jpeg
            : VisionFrameEncoding.Png;

    private static string ResolveSecret(string[] args, bool allowEphemeral)
    {
        var secret = GetOption(args, "--secret")
            ?? Environment.GetEnvironmentVariable(SecretEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(secret) && Encoding.UTF8.GetByteCount(secret) >= 32)
            return secret;
        if (allowEphemeral)
            return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        throw new InvalidOperationException(
            $"云端服务必须通过 --secret 或环境变量 {SecretEnvironmentVariable} 提供至少 32 字节密钥。");
    }

    private static int FindFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static int ParsePositiveInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;

    private static bool HasFlag(string[] args, string flag) =>
        args.Contains(flag, StringComparer.OrdinalIgnoreCase);

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }
        return null;
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ReadDeclaredModelSha256(ModelArtifactInfo artifact)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(artifact.ConfigPath));
        var value = config.RootElement
            .GetProperty("hashes")
            .GetProperty("onnx_sha256")
            .GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
            throw new InvalidDataException("推理配置缺少有效的 ONNX SHA256。");
        return value.ToLowerInvariant();
    }

    private static double Percentile(IReadOnlyCollection<double> values, double percentile)
    {
        if (values.Count == 0)
            return 0;
        var ordered = values.OrderBy(value => value).ToArray();
        var index = (int)Math.Ceiling(percentile * ordered.Length) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private static string FormatBytes(double bytes)
    {
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024 * 1024):0.00}MiB";
        return $"{bytes / 1024:0.0}KiB";
    }

    private sealed record LocalBenchmarkResult(
        List<double> TotalMilliseconds,
        List<double> LayoutMilliseconds,
        List<double> SlotMilliseconds,
        LayoutSnapshot LastLayout,
        VisionV2CoreSlots LastCore,
        string ModelSha256);

    private sealed class RemoteBenchmarkResult
    {
        public List<double> EncodeMilliseconds { get; } = new();
        public List<double> EndToEndMilliseconds { get; } = new();
        public List<double> ServerDecodeMilliseconds { get; } = new();
        public List<double> ServerInferenceMilliseconds { get; } = new();
        public List<double> PayloadBytes { get; } = new();
        public List<double> ResponseBytes { get; } = new();
        public VisionRemoteFrameDescriptor LastRequest { get; set; } = new();
        public VisionRemoteInferenceResponse LastResponse { get; set; } = new();
    }

    private sealed record ParityResult(
        bool OperationallyEquivalent,
        bool ExactlyEquivalent,
        string Message);

    private sealed record SecurityTestResult(
        bool ValidSignedResponse,
        bool TamperedUploadRejected,
        bool ReplayRejected,
        bool StaleRequestRejected,
        bool ExpiredActionRejected,
        bool OutOfBoundsRejected,
        bool ModelMismatchRejected)
    {
        public bool AllPassed => ValidSignedResponse
            && TamperedUploadRejected
            && ReplayRejected
            && StaleRequestRejected
            && ExpiredActionRejected
            && OutOfBoundsRejected
            && ModelMismatchRejected;
    }

    private enum AdversarialRequest
    {
        None = 0,
        TamperedBody,
        StaleTimestamp
    }

    private sealed record RemoteCallResult(
        bool IsSuccess,
        VisionRemoteFrameDescriptor? Request,
        VisionRemoteInferenceResponse? Response,
        VisionRemoteValidationResult? Safety,
        double EncodeMilliseconds,
        double EndToEndMilliseconds,
        long PayloadBytes,
        long ResponseBytes,
        string? Error = null);

    private sealed class VisionCloudClient : IDisposable
    {
        private readonly Uri _endpoint;
        private readonly string _secret;
        private readonly string _expectedModelSha256;
        private readonly HttpClient _http;
        private readonly VisionRemoteResponseSafetyGate _safetyGate = new();

        public VisionCloudClient(Uri endpoint, string secret)
        {
            _endpoint = new Uri(endpoint, "/v1/vision/analyze");
            _secret = secret;
            _expectedModelSha256 = ReadDeclaredModelSha256(new ModelArtifactRegistry().FindL1LayoutModel());
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        public async Task<RemoteCallResult> AnalyzeAsync(
            CaptureResult frame,
            VisionFrameEncoding encoding,
            int jpegQuality)
        {
            var encodeWatch = Stopwatch.StartNew();
            var payload = ImageCodec.Encode(frame, encoding, jpegQuality);
            encodeWatch.Stop();
            var request = CreateDescriptor(frame, encoding, payload);

            using var message = CreateRequest(request, payload, DateTimeOffset.UtcNow, NewNonce());
            var totalWatch = Stopwatch.StartNew();
            using var responseMessage = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead);
            var responseBytes = await responseMessage.Content.ReadAsByteArrayAsync();
            totalWatch.Stop();
            if (!responseMessage.IsSuccessStatusCode)
            {
                return new RemoteCallResult(
                    false,
                    request,
                    null,
                    null,
                    encodeWatch.Elapsed.TotalMilliseconds,
                    encodeWatch.Elapsed.TotalMilliseconds + totalWatch.Elapsed.TotalMilliseconds,
                    payload.LongLength,
                    responseBytes.LongLength,
                    $"HTTP {(int)responseMessage.StatusCode}: {Encoding.UTF8.GetString(responseBytes)}");
            }

            if (!TransportAuthentication.VerifyResponse(_secret, request.RequestId, responseMessage, responseBytes, out var signatureError))
            {
                return new RemoteCallResult(
                    false,
                    request,
                    null,
                    null,
                    encodeWatch.Elapsed.TotalMilliseconds,
                    encodeWatch.Elapsed.TotalMilliseconds + totalWatch.Elapsed.TotalMilliseconds,
                    payload.LongLength,
                    responseBytes.LongLength,
                    signatureError);
            }

            var response = JsonSerializer.Deserialize<VisionRemoteInferenceResponse>(responseBytes, JsonOptions)
                ?? throw new InvalidOperationException("云端返回了空响应。");
            var safety = _safetyGate.Validate(
                request,
                response,
                frame.Width,
                frame.Height,
                _expectedModelSha256,
                DateTimeOffset.UtcNow);
            return new RemoteCallResult(
                safety.IsAccepted,
                request,
                response,
                safety,
                encodeWatch.Elapsed.TotalMilliseconds,
                encodeWatch.Elapsed.TotalMilliseconds + totalWatch.Elapsed.TotalMilliseconds,
                payload.LongLength,
                responseBytes.LongLength,
                safety.IsAccepted ? null : safety.Reason);
        }

        public async Task<bool> SendAdversarialAsync(
            CaptureResult frame,
            VisionFrameEncoding encoding,
            int jpegQuality,
            AdversarialRequest adversarial,
            string? nonce = null)
        {
            var payload = ImageCodec.Encode(frame, encoding, jpegQuality);
            var request = CreateDescriptor(frame with
            {
                Sequence = RandomNumberGenerator.GetInt32(1, int.MaxValue),
                CapturedAt = DateTimeOffset.UtcNow
            }, encoding, payload);
            var sentAt = adversarial == AdversarialRequest.StaleTimestamp
                ? DateTimeOffset.UtcNow.AddMinutes(-1)
                : DateTimeOffset.UtcNow;
            using var message = CreateRequest(request, payload, sentAt, nonce ?? NewNonce());
            if (adversarial == AdversarialRequest.TamperedBody)
            {
                var tampered = payload.ToArray();
                tampered[^1] ^= 0x01;
                message.Content = new ByteArrayContent(tampered);
                message.Content.Headers.ContentType = new MediaTypeHeaderValue(ContentType(encoding));
            }

            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode;
        }

        private HttpRequestMessage CreateRequest(
            VisionRemoteFrameDescriptor descriptor,
            byte[] payload,
            DateTimeOffset sentAt,
            string nonce)
        {
            var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new ByteArrayContent(payload)
            };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue(ContentType(descriptor.Encoding));
            TransportAuthentication.AddRequestHeaders(message, descriptor, sentAt, nonce, _secret);
            return message;
        }

        private static VisionRemoteFrameDescriptor CreateDescriptor(
            CaptureResult frame,
            VisionFrameEncoding encoding,
            byte[] payload) =>
            new()
            {
                RequestId = Guid.NewGuid().ToString("N"),
                SessionId = "deployment-harness",
                FrameSequence = frame.Sequence,
                CapturedAt = DateTimeOffset.FromUnixTimeMilliseconds(frame.CapturedAt.ToUnixTimeMilliseconds()),
                Width = frame.Width,
                Height = frame.Height,
                Encoding = encoding,
                ImageSha256 = Sha256(payload)
            };

        private static string NewNonce() =>
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        public void Dispose() => _http.Dispose();
    }

    private sealed class VisionCloudServer : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly CloudInferenceEngine _engine;

        private VisionCloudServer(WebApplication application, CloudInferenceEngine engine)
        {
            _application = application;
            _engine = engine;
        }

        public static async Task<VisionCloudServer> StartAsync(
            string listen,
            string secret,
            CancellationToken cancellationToken)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls(listen);
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = MaximumUploadBytes;
                options.AddServerHeader = false;
            });

            var application = builder.Build();
            var engine = new CloudInferenceEngine();
            var replayGuard = new ReplayGuard();

            application.MapGet("/healthz", () => Results.Json(new
            {
                ok = true,
                api_version = VisionRemoteProtocol.ApiVersion,
                model_sha256 = engine.ModelSha256
            }, JsonOptions));
            application.MapPost("/v1/vision/analyze", async context =>
            {
                if (!TransportAuthentication.TryReadAndValidateRequest(
                        context.Request,
                        secret,
                        replayGuard,
                        out var descriptor,
                        out var authError))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsync(authError);
                    return;
                }

                await using var buffer = new MemoryStream();
                await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
                var payload = buffer.ToArray();
                if (payload.LongLength == 0 || payload.LongLength > MaximumUploadBytes)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }

                var actualHash = Sha256(payload);
                if (!string.Equals(actualHash, descriptor.ImageSha256, StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsync("上传内容哈希不一致。");
                    return;
                }

                VisionRemoteInferenceResponse inference;
                try
                {
                    var decodeWatch = Stopwatch.StartNew();
                    var frame = ImageCodec.Decode(payload) with
                    {
                        Sequence = descriptor.FrameSequence,
                        CapturedAt = descriptor.CapturedAt,
                        CaptureSource = "remote-http"
                    };
                    decodeWatch.Stop();
                    if (frame.Width != descriptor.Width || frame.Height != descriptor.Height)
                        throw new InvalidDataException("上传图片尺寸与签名元数据不一致。");
                    inference = engine.Analyze(descriptor, frame, decodeWatch.Elapsed.TotalMilliseconds);
                }
                catch (Exception ex)
                {
                    context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                    await context.Response.WriteAsync(ex.Message);
                    return;
                }

                var responseBytes = JsonSerializer.SerializeToUtf8Bytes(inference, JsonOptions);
                TransportAuthentication.AddResponseHeaders(
                    context.Response,
                    secret,
                    descriptor.RequestId,
                    responseBytes);
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength = responseBytes.LongLength;
                await context.Response.Body.WriteAsync(responseBytes, context.RequestAborted);
            });

            await application.StartAsync(cancellationToken);
            return new VisionCloudServer(application, engine);
        }

        public async ValueTask DisposeAsync()
        {
            await _application.StopAsync();
            await _application.DisposeAsync();
            _engine.Dispose();
        }
    }

    private sealed class CloudInferenceEngine : IDisposable
    {
        private readonly OnnxLayoutDetectService _layout;
        private readonly VisionV2SlotService _slots;

        public CloudInferenceEngine()
        {
            var log = new RuntimeLogService();
            var registry = new ModelArtifactRegistry();
            _layout = new OnnxLayoutDetectService(registry, log);
            _slots = new VisionV2SlotService(registry, log);
            if (!_slots.IsAvailable)
                throw new InvalidOperationException("云端 V2 插槽宿主不可用。");
            var artifact = registry.FindL1LayoutModel();
            ModelSha256 = Sha256(File.ReadAllBytes(artifact.ModelPath));
            var expectedModelHash = ReadDeclaredModelSha256(artifact);
            if (!string.Equals(ModelSha256, expectedModelHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"云端模型哈希不匹配：{ModelSha256}");
        }

        public string ModelSha256 { get; }

        public VisionRemoteInferenceResponse Analyze(
            VisionRemoteFrameDescriptor descriptor,
            CaptureResult frame,
            double decodeMilliseconds)
        {
            var totalWatch = Stopwatch.StartNew();
            var layoutWatch = Stopwatch.StartNew();
            var layout = _layout.Detect(frame);
            layoutWatch.Stop();
            var slotWatch = Stopwatch.StartNew();
            var core = _slots.AnalyzeCore(
                $"remote-{descriptor.SessionId}",
                descriptor.FrameSequence,
                frame,
                layout);
            slotWatch.Stop();
            totalWatch.Stop();
            var now = DateTimeOffset.UtcNow;

            return new VisionRemoteInferenceResponse
            {
                RequestId = descriptor.RequestId,
                SessionId = descriptor.SessionId,
                FrameSequence = descriptor.FrameSequence,
                CapturedAt = descriptor.CapturedAt,
                ProcessedAt = now,
                ValidUntil = now.AddSeconds(5),
                Width = frame.Width,
                Height = frame.Height,
                ImageSha256 = descriptor.ImageSha256,
                ModelSha256 = ModelSha256,
                Layout = layout,
                Core = core,
                ActionProposals = Array.Empty<VisionRemoteActionProposal>(),
                Timing = new VisionRemoteServerTiming
                {
                    DecodeMilliseconds = decodeMilliseconds,
                    LayoutMilliseconds = layoutWatch.Elapsed.TotalMilliseconds,
                    SlotMilliseconds = slotWatch.Elapsed.TotalMilliseconds,
                    TotalMilliseconds = totalWatch.Elapsed.TotalMilliseconds + decodeMilliseconds
                }
            };
        }

        public void Dispose()
        {
            _slots.Dispose();
            _layout.Dispose();
        }
    }

    private sealed class ReplayGuard
    {
        private readonly ConcurrentDictionary<string, long> _nonces = new(StringComparer.Ordinal);

        public bool TryUse(string nonce, DateTimeOffset now)
        {
            var cutoff = now.AddMinutes(-2).ToUnixTimeMilliseconds();
            foreach (var entry in _nonces)
            {
                if (entry.Value < cutoff)
                    _nonces.TryRemove(entry.Key, out _);
            }
            return _nonces.TryAdd(nonce, now.ToUnixTimeMilliseconds());
        }
    }

    private static class TransportAuthentication
    {
        private static readonly TimeSpan RequestFreshness = TimeSpan.FromSeconds(15);

        public static void AddRequestHeaders(
            HttpRequestMessage message,
            VisionRemoteFrameDescriptor descriptor,
            DateTimeOffset sentAt,
            string nonce,
            string secret)
        {
            Set(message, "X-LBX-Api-Version", descriptor.ApiVersion);
            Set(message, "X-LBX-Request-Id", descriptor.RequestId);
            Set(message, "X-LBX-Session-Id", descriptor.SessionId);
            Set(message, "X-LBX-Frame-Sequence", descriptor.FrameSequence.ToString(CultureInfo.InvariantCulture));
            Set(message, "X-LBX-Captured-At", descriptor.CapturedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
            Set(message, "X-LBX-Width", descriptor.Width.ToString(CultureInfo.InvariantCulture));
            Set(message, "X-LBX-Height", descriptor.Height.ToString(CultureInfo.InvariantCulture));
            Set(message, "X-LBX-Encoding", descriptor.Encoding.ToString().ToLowerInvariant());
            Set(message, "X-LBX-Image-Sha256", descriptor.ImageSha256);
            Set(message, "X-LBX-Sent-At", sentAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
            Set(message, "X-LBX-Nonce", nonce);
            Set(message, "X-LBX-Signature", Sign(secret, CanonicalRequest(descriptor, sentAt, nonce)));
        }

        public static bool TryReadAndValidateRequest(
            HttpRequest request,
            string secret,
            ReplayGuard replayGuard,
            out VisionRemoteFrameDescriptor descriptor,
            out string error)
        {
            descriptor = new VisionRemoteFrameDescriptor();
            error = "请求认证失败。";
            try
            {
                var apiVersion = Header(request, "X-LBX-Api-Version");
                var requestId = Header(request, "X-LBX-Request-Id");
                var sessionId = Header(request, "X-LBX-Session-Id");
                var frameSequence = long.Parse(Header(request, "X-LBX-Frame-Sequence"), CultureInfo.InvariantCulture);
                var capturedAt = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(
                    Header(request, "X-LBX-Captured-At"), CultureInfo.InvariantCulture));
                var width = int.Parse(Header(request, "X-LBX-Width"), CultureInfo.InvariantCulture);
                var height = int.Parse(Header(request, "X-LBX-Height"), CultureInfo.InvariantCulture);
                var encoding = Enum.Parse<VisionFrameEncoding>(Header(request, "X-LBX-Encoding"), true);
                var imageSha256 = Header(request, "X-LBX-Image-Sha256");
                var sentAt = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(
                    Header(request, "X-LBX-Sent-At"), CultureInfo.InvariantCulture));
                var nonce = Header(request, "X-LBX-Nonce");
                var signature = Header(request, "X-LBX-Signature");
                descriptor = new VisionRemoteFrameDescriptor
                {
                    ApiVersion = apiVersion,
                    RequestId = requestId,
                    SessionId = sessionId,
                    FrameSequence = frameSequence,
                    CapturedAt = capturedAt,
                    Width = width,
                    Height = height,
                    Encoding = encoding,
                    ImageSha256 = imageSha256
                };

                if (!string.Equals(apiVersion, VisionRemoteProtocol.ApiVersion, StringComparison.Ordinal))
                {
                    error = "协议版本不匹配。";
                    return false;
                }
                var now = DateTimeOffset.UtcNow;
                if ((now - sentAt).Duration() > RequestFreshness)
                {
                    error = "请求时间戳已过期。";
                    return false;
                }
                var expected = Sign(secret, CanonicalRequest(descriptor, sentAt, nonce));
                if (!FixedEquals(signature, expected))
                {
                    error = "请求签名无效。";
                    return false;
                }
                if (!replayGuard.TryUse(nonce, now))
                {
                    error = "请求 nonce 已使用。";
                    return false;
                }
                if (width <= 0 || height <= 0 || width > 7680 || height > 4320)
                {
                    error = "截图尺寸不在允许范围内。";
                    return false;
                }
                if (imageSha256.Length != 64)
                {
                    error = "截图哈希格式无效。";
                    return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void AddResponseHeaders(
            HttpResponse response,
            string secret,
            string requestId,
            byte[] body)
        {
            var bodyHash = Sha256(body);
            response.Headers["X-LBX-Body-Sha256"] = bodyHash;
            response.Headers["X-LBX-Response-Signature"] = Sign(secret, CanonicalResponse(requestId, bodyHash));
            response.Headers.CacheControl = "no-store";
            response.Headers.Pragma = "no-cache";
        }

        public static bool VerifyResponse(
            string secret,
            string requestId,
            HttpResponseMessage response,
            byte[] body,
            out string error)
        {
            error = "";
            if (!response.Headers.TryGetValues("X-LBX-Body-Sha256", out var hashValues)
                || !response.Headers.TryGetValues("X-LBX-Response-Signature", out var signatureValues))
            {
                error = "云端响应缺少签名头。";
                return false;
            }
            var bodyHash = hashValues.Single();
            var signature = signatureValues.Single();
            var actualHash = Sha256(body);
            if (!FixedEquals(bodyHash, actualHash))
            {
                error = "云端响应内容哈希不一致。";
                return false;
            }
            var expected = Sign(secret, CanonicalResponse(requestId, bodyHash));
            if (!FixedEquals(signature, expected))
            {
                error = "云端响应签名无效。";
                return false;
            }
            return true;
        }

        private static string CanonicalRequest(
            VisionRemoteFrameDescriptor descriptor,
            DateTimeOffset sentAt,
            string nonce) =>
            string.Join('\n',
                descriptor.ApiVersion,
                descriptor.RequestId,
                descriptor.SessionId,
                descriptor.FrameSequence.ToString(CultureInfo.InvariantCulture),
                descriptor.CapturedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                descriptor.Width.ToString(CultureInfo.InvariantCulture),
                descriptor.Height.ToString(CultureInfo.InvariantCulture),
                descriptor.Encoding.ToString().ToLowerInvariant(),
                descriptor.ImageSha256,
                sentAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                nonce);

        private static string CanonicalResponse(string requestId, string bodyHash) =>
            $"{VisionRemoteProtocol.ApiVersion}\n{requestId}\n{bodyHash}";

        private static string Sign(string secret, string value)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        }

        private static bool FixedEquals(string left, string right)
        {
            try
            {
                var leftBytes = Convert.FromHexString(left);
                var rightBytes = Convert.FromHexString(right);
                return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
            }
            catch
            {
                return false;
            }
        }

        private static void Set(HttpRequestMessage message, string name, string value) =>
            message.Headers.TryAddWithoutValidation(name, value);

        private static string Header(HttpRequest request, string name)
        {
            var value = request.Headers[name].ToString();
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException($"缺少请求头 {name}。");
            return value;
        }
    }

    private static class ImageCodec
    {
        public static byte[] Encode(CaptureResult frame, VisionFrameEncoding encoding, int jpegQuality)
        {
            var source = BitmapSource.Create(
                frame.Width,
                frame.Height,
                96,
                96,
                PixelFormats.Bgr32,
                null,
                frame.Pixels,
                frame.Width * 4);
            BitmapEncoder encoder = encoding == VisionFrameEncoding.Jpeg
                ? new JpegBitmapEncoder { QualityLevel = jpegQuality }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }

        public static CaptureResult Decode(byte[] imageBytes)
        {
            using var input = new MemoryStream(imageBytes, writable: false);
            var decoder = BitmapDecoder.Create(
                input,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            BitmapSource source = decoder.Frames[0];
            if (source.Format != PixelFormats.Bgr32)
            {
                var converted = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
                converted.Freeze();
                source = converted;
            }
            var stride = source.PixelWidth * 4;
            var pixels = new byte[stride * source.PixelHeight];
            source.CopyPixels(pixels, stride, 0);
            return new CaptureResult(source.PixelWidth, source.PixelHeight, pixels);
        }
    }

    private static string ContentType(VisionFrameEncoding encoding) =>
        encoding == VisionFrameEncoding.Jpeg ? "image/jpeg" : "image/png";
}
