using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TlbbAgent.Contracts;
using TlbbAgent.Core;
using TlbbAgent.Core.Perception;

namespace TlbbAgent.VisionHarness;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var visionRoot = ResolveVisionRoot(args);
        var failures = new List<string>();
        var liveOnly = args.Contains("--live-only", StringComparer.OrdinalIgnoreCase);
        var v2Only = args.Contains("--v2-only", StringComparer.OrdinalIgnoreCase);
        var auxiliaryOnly = args.Contains("--auxiliary-only", StringComparer.OrdinalIgnoreCase);
        var questLogOnly = args.Contains("--quest-log-only", StringComparer.OrdinalIgnoreCase);
        var imagePath = ReadOption(args, "--image");
        var overlayPath = ReadOption(args, "--overlay-out");
        var livePid = int.TryParse(ReadOption(args, "--pid"), out var parsedLivePid)
            ? parsedLivePid
            : (int?)null;
        var expectNoTarget = args.Contains("--expect-no-target", StringComparer.OrdinalIgnoreCase);

        var log = new RuntimeLogService();
        var registry = new ModelArtifactRegistry();

        var l1Load = Stopwatch.StartNew();
        using var layout = new OnnxLayoutDetectService(registry, log);
        l1Load.Stop();

        var hpLoad = Stopwatch.StartNew();
        using var hpSlot = new TargetHpSlotService(registry, log);
        hpLoad.Stop();

        Console.WriteLine($"MODEL_LOAD L1={l1Load.Elapsed.TotalMilliseconds:0.0}ms HP_SLOT={hpLoad.Elapsed.TotalMilliseconds:0.0}ms");

        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            RunImageProbe(imagePath, overlayPath, expectNoTarget, layout, hpSlot, registry, log, failures);
            return Complete(failures);
        }

        if (v2Only)
        {
            RunVisionV2Fixtures(visionRoot, layout, registry, log, failures);
            RunXiaoyaoSkillLibraryFixture(visionRoot, layout, registry, log, failures);
            return Complete(failures);
        }

        if (auxiliaryOnly)
        {
            RunAuxiliaryModelFixtures(visionRoot, registry, log, failures);
            return Complete(failures);
        }

        if (questLogOnly)
        {
            RunQuestLogPanelFixtures(visionRoot, registry, log, failures);
            return Complete(failures);
        }

        if (liveOnly)
        {
            RunLiveSmoke(layout, hpSlot, registry, log, failures, livePid);
            return Complete(failures);
        }

        RunHpFixtures(visionRoot, hpSlot, failures);
        RunLayoutFixtures(visionRoot, layout, hpSlot, failures);
        RunOpenTextFixture(visionRoot, layout, registry, log, failures);
        RunMapNamePolicyFixtures(visionRoot, failures);
        RunDegradedTests(layout, hpSlot, failures);
        RunTemporalTests(failures);
        RunAuxiliaryModelFixtures(visionRoot, registry, log, failures);

        return Complete(failures);
    }

    private static void RunAuxiliaryModelFixtures(
        string visionRoot,
        IModelArtifactRegistry registry,
        ILogService log,
        ICollection<string> failures)
    {
        RunSystemFeedbackRegionFixtures(visionRoot, registry, log, failures);
        RunRightSkillBarFixtures(visionRoot, registry, log, failures);
        RunInteractionWindowFixtures(visionRoot, registry, log, failures);
        RunQuestLogPanelFixtures(visionRoot, registry, log, failures);
        RunInteractionControlCatalogFixtures(visionRoot, registry, log, failures);
        RunBuffAnchorFixtures(visionRoot, registry, log, failures);
    }

    private static void RunSystemFeedbackRegionFixtures(
        string visionRoot,
        IModelArtifactRegistry registry,
        ILogService log,
        ICollection<string> failures)
    {
        var artifact = registry.FindSystemFeedbackRegionModel();
        var packageRoot = Path.Combine(
            visionRoot,
            "train",
            "packages",
            "system_feedback_region_l1_v2");
        var imageRoot = Path.Combine(packageRoot, "images", "test");
        var labelRoot = Path.Combine(packageRoot, "labels", "test");
        var images = Directory.Exists(imageRoot)
            ? Directory.EnumerateFiles(imageRoot, "*.png", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();
        var positiveCount = 0;
        var positiveHit = 0;
        var negativeCount = 0;
        var negativeRawPositive = 0;
        var negativeFalsePositive = 0;
        var unavailable = 0;
        var latencies = new List<double>();
        using var detector = new SystemFeedbackRegionDetectService(registry, log);
        var openText = new UnavailableHarnessOpenTextRecognizer();

        for (var index = 0; index < images.Length; index++)
        {
            var image = images[index];
            var label = Path.Combine(labelRoot, Path.GetFileNameWithoutExtension(image) + ".txt");
            var expectedPositive = File.Exists(label)
                                   && !string.IsNullOrWhiteSpace(File.ReadAllText(label));
            var frame = LoadCapture(image) with
            {
                Sequence = index + 1,
                CapturedAt = DateTimeOffset.UtcNow,
                CaptureSource = "vision-harness:system-feedback-test"
            };
            var result = detector.Detect(frame, openText);
            if (!result.Available)
                unavailable++;
            latencies.Add(result.InferenceMilliseconds);
            var detected = result.Observations.Count > 0;
            if (expectedPositive)
            {
                positiveCount++;
                if (detected)
                    positiveHit++;
            }
            else
            {
                negativeCount++;
                if (result.RawRegions.Count > 0)
                    negativeRawPositive++;
                if (detected)
                    negativeFalsePositive++;
            }
        }

        var geometryInput = new[]
        {
            new SystemFeedbackRegionDetection(new VisionBox(850, 70, 90, 24), 0.91, "fixture"),
            new SystemFeedbackRegionDetection(new VisionBox(945, 72, 95, 24), 0.93, "fixture"),
            new SystemFeedbackRegionDetection(new VisionBox(875, 100, 140, 24), 0.94, "fixture"),
            new SystemFeedbackRegionDetection(new VisionBox(40, 70, 180, 24), 0.99, "off-lane")
        };
        var geometry = SystemFeedbackRegionDetectService.FilterAndMerge(geometryInput, 1920, 1080);
        var geometryPass = geometry.Count == 1
                           && geometry[0].Box.X == 850
                           && geometry[0].Box.Y == 70
                           && geometry[0].Box.Right == 1040
                           && geometry[0].Box.Bottom == 124;
        var actualSha256 = artifact.IsAvailable
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(artifact.ModelPath)))
                .ToLowerInvariant()
            : string.Empty;
        var hashPass = artifact.IsAvailable
                       && actualSha256.Equals(
                           "56fb1054252da8583be9b34ea136363a2a0d5809e9add4f7c48235b7d3768bae",
                           StringComparison.OrdinalIgnoreCase);
        var positiveFixture = images.FirstOrDefault(image =>
        {
            var label = Path.Combine(labelRoot, Path.GetFileNameWithoutExtension(image) + ".txt");
            return File.Exists(label) && !string.IsNullOrWhiteSpace(File.ReadAllText(label));
        });
        var countingOpenText = new CountingHarnessOpenTextRecognizer();
        var regionsOnly = positiveFixture is null
            ? SystemFeedbackPerception.Unavailable(0, "No positive fixture.")
            : detector.Detect(
                LoadCapture(positiveFixture) with
                {
                    Sequence = 9001,
                    CapturedAt = DateTimeOffset.UtcNow,
                    CaptureSource = "vision-harness:system-feedback-regions-only"
                },
                countingOpenText,
                includeOpenText: false);
        var regionsOnlyPass = regionsOnly.Available
                              && regionsOnly.DetectionRequested
                              && !regionsOnly.TextRecognitionRequested
                              && !regionsOnly.TextRecognitionCompleted
                              && regionsOnly.RawRegions.Count > 0
                              && regionsOnly.Observations.Count == 0
                              && countingOpenText.RecognizeCalls == 0;
        var ok = hashPass
                 && images.Length == 80
                 && positiveCount == 50
                 && positiveHit == 50
                 && negativeCount == 30
                 && negativeFalsePositive == 0
                 && unavailable == 0
                 && geometryPass
                 && regionsOnlyPass;

        var evidenceRoot = Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            "verification",
            "system-feedback-l1-v2");
        Directory.CreateDirectory(evidenceRoot);
        var reportPath = Path.Combine(evidenceRoot, "fixture-report.json");
        File.WriteAllText(
            reportPath,
            JsonSerializer.Serialize(
                new
                {
                    generatedAt = DateTimeOffset.Now,
                    model = new { artifact.Key, artifact.ModelPath, sha256 = actualSha256 },
                    images = images.Length,
                    positive = new { total = positiveCount, hit = positiveHit },
                    negative = new
                    {
                        total = negativeCount,
                        rawPositive = negativeRawPositive,
                        filteredFalsePositive = negativeFalsePositive
                    },
                    unavailable,
                    geometryPass,
                    regionsOnlyPass,
                    regionsOnlyOcrCalls = countingOpenText.RecognizeCalls,
                    latencyP50Ms = Percentile(latencies, 0.50),
                    latencyP95Ms = Percentile(latencies, 0.95),
                    passed = ok
                },
                new JsonSerializerOptions { WriteIndented = true }));

        Assert(ok, "System feedback L1 V2 must pass 50 positive, 30 negative, hash and central merge gates.", failures);
        Console.WriteLine(
            $"SYSTEM_FEEDBACK_L1 status={(ok ? "PASS" : "FAIL")} positive={positiveHit}/{positiveCount} negative_raw={negativeRawPositive}/{negativeCount} negative_filtered_fp={negativeFalsePositive}/{negativeCount} geometry={geometryPass} regions_only={regionsOnlyPass}/ocr_calls={countingOpenText.RecognizeCalls} p50={Percentile(latencies, 0.50):0.0}ms p95={Percentile(latencies, 0.95):0.0}ms evidence={reportPath}");
    }

    private static void RunInteractionControlCatalogFixtures(
        string visionRoot,
        IModelArtifactRegistry registry,
        ILogService log,
        ICollection<string> failures)
    {
        var annotationRoot = Path.Combine(
            visionRoot,
            "train",
            "review",
            "interaction_window_l2_controls_v3_precise",
            "annotations");
        var files = Directory.Exists(annotationRoot)
            ? Directory.EnumerateFiles(annotationRoot, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();
        var service = new InteractionControlCatalogService(log);
        using var windowDetector = new InteractionWindowDetectService(registry, log);
        var readOnlyPlanner = new InteractionReadOnlyPlanService(windowDetector, service);
        var mappedSamples = 0;
        var mappedControls = 0;
        var mappedRegions = 0;
        var geometryErrors = 0;
        var readOnlyPlans = 0;
        var readOnlyPlanErrors = 0;
        var renderedClasses = new HashSet<string>(StringComparer.Ordinal);
        var reviewRoot = Path.Combine(
            visionRoot,
            "train",
            "reports",
            "remaining_goal_20260714",
            "review");
        Directory.CreateDirectory(reviewRoot);
        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            var panelClass = root.GetProperty("class_id").GetString() ?? "";
            var panelBox = ParseJsonRect(root.GetProperty("l1_bbox_source_xyxy"));
            var detection = new InteractionWindowDetection
            {
                ClassName = panelClass,
                Box = ToVisionBox(panelBox),
                Confidence = 0.99
            };
            var layout = service.Map(++mappedSamples, detection);
            var expectedControls = root.GetProperty("controls")
                .EnumerateArray()
                .ToDictionary(
                    value => value.GetProperty("control_id").GetString() ?? "",
                    value => value,
                    StringComparer.Ordinal);
            mappedControls += layout.Controls.Count;
            mappedRegions += layout.Regions.Count;
            if (layout.RuntimeAuthorized
                || layout.FrameSequence != mappedSamples
                || layout.Controls.Count != expectedControls.Count)
            {
                geometryErrors++;
            }

            foreach (var control in layout.Controls)
            {
                if (!expectedControls.TryGetValue(control.ControlId, out var expected))
                {
                    geometryErrors++;
                    continue;
                }
                var visual = ParseJsonRect(expected.GetProperty("visual_bbox_xyxy"));
                var safe = ParseJsonRect(expected.GetProperty("safe_click_bbox_xyxy"));
                var point = expected.GetProperty("click_point_xy").EnumerateArray().Select(value => value.GetInt32()).ToArray();
                var expectedVisual = OffsetVisionBox(panelBox, visual);
                var expectedSafe = OffsetVisionBox(panelBox, safe);
                if (BoxMaxError(control.VisualBox, expectedVisual) > 1
                    || BoxMaxError(control.SafeClickBox, expectedSafe) > 1
                    || !ContainsBox(control.VisualBox, control.SafeClickBox)
                    || !control.ClickX.HasValue
                    || !control.ClickY.HasValue
                    || !ContainsPoint(control.SafeClickBox, control.ClickX.Value, control.ClickY.Value)
                    || Math.Abs(control.ClickX.Value - (panelBox.X + point[0])) > 1
                    || Math.Abs(control.ClickY.Value - (panelBox.Y + point[1])) > 1)
                {
                    geometryErrors++;
                }
            }

            var expectedCells = panelClass switch
            {
                "bag_panel" => 60,
                "bank_panel" => 160,
                "shop_panel" => 19,
                _ => 0
            };
            if (layout.Regions.Sum(region => region.Cells.Count) != expectedCells)
                geometryErrors++;

            if (renderedClasses.Add(panelClass))
            {
                var source = ResolveVisionSourcePath(visionRoot, root.GetProperty("source_image").GetString() ?? "");
                if (!File.Exists(source))
                {
                    geometryErrors++;
                    continue;
                }
                var frame = LoadCapture(source) with { Sequence = 100000 + mappedSamples };
                var sampleId = root.GetProperty("sample_id").GetString() ?? panelClass;
                var overlayPath = Path.Combine(reviewRoot, $"interaction_l2_{sampleId}.png");
                if (!service.TryRenderDebugOverlay(frame, new[] { layout }, overlayPath, out var error))
                {
                    failures.Add($"Interaction L2 overlay failed for {sampleId}: {error}");
                    geometryErrors++;
                }
                File.WriteAllText(
                    Path.Combine(reviewRoot, $"interaction_l2_{sampleId}.json"),
                    JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true }));

                var readOnlyPlan = readOnlyPlanner.Build("vision-harness", frame, panelClass);
                readOnlyPlans++;
                var expectedActions = layout.Controls.Count + layout.Regions.Sum(region => region.Cells.Count);
                if (!readOnlyPlan.Available
                    || readOnlyPlan.Status != InteractionReadOnlyPlanStatus.ObservationOnly
                    || readOnlyPlan.FrameSequence != frame.Sequence
                    || readOnlyPlan.Actions.Count != expectedActions
                    || readOnlyPlan.RuntimeAuthorized
                    || readOnlyPlan.Actions.Any(action => action.RuntimeAuthorized))
                {
                    Console.WriteLine(
                        $"INTERACTION_READ_ONLY_PLAN_ERROR class={panelClass} available={readOnlyPlan.Available} status={readOnlyPlan.Status} frame={readOnlyPlan.FrameSequence}/{frame.Sequence} actions={readOnlyPlan.Actions.Count}/{expectedActions} authorized={readOnlyPlan.RuntimeAuthorized} message={readOnlyPlan.Message}");
                    readOnlyPlanErrors++;
                }
            }
        }

        var captureRequired = readOnlyPlanner.Build(
            "vision-harness",
            new CaptureResult(1, 1, new byte[4]) { Sequence = 200001 },
            "npc_dialog_panel");
        if (captureRequired.Available
            || captureRequired.Status != InteractionReadOnlyPlanStatus.CaptureRequired
            || captureRequired.Actions.Count != 0
            || captureRequired.RuntimeAuthorized)
        {
            readOnlyPlanErrors++;
        }

        var ok = service.IsAvailable
                 && !service.RuntimeAuthorized
                 && files.Length == 25
                 && mappedSamples == 25
                 && mappedControls == 230
                  && mappedRegions == 50
                  && geometryErrors == 0
                  && renderedClasses.Count == 5
                  && readOnlyPlans == 5
                  && readOnlyPlanErrors == 0;
        Assert(ok, "Interaction L2 V3 must parse and map all 25 annotations without geometry or authorization errors.", failures);
        Console.WriteLine(
            $"INTERACTION_CONTROL_L2 status={(ok ? "PASS" : "FAIL")} samples={mappedSamples}/25 controls={mappedControls}/230 regions={mappedRegions}/50 errors={geometryErrors} authorized={service.RuntimeAuthorized}");
        Console.WriteLine(
            $"INTERACTION_READ_ONLY_PLAN status={(readOnlyPlanErrors == 0 ? "PASS" : "FAIL")} published={readOnlyPlans}/5 capture_required_npc={captureRequired.Status} errors={readOnlyPlanErrors} runtime_authorized={readOnlyPlanner.RuntimeActionsEnabled}");
    }

    private static int BoxMaxError(VisionBox left, VisionBox right) =>
        new[]
        {
            Math.Abs(left.X - right.X),
            Math.Abs(left.Y - right.Y),
            Math.Abs(left.Right - right.Right),
            Math.Abs(left.Bottom - right.Bottom)
        }.Max();

    private static VisionBox ToVisionBox(VisionRectF value) =>
        new(
            (int)Math.Round(value.X),
            (int)Math.Round(value.Y),
            (int)Math.Round(value.Width),
            (int)Math.Round(value.Height));

    private static VisionBox OffsetVisionBox(VisionRectF panel, VisionRectF local) =>
        new(
            (int)Math.Round(panel.X + local.X),
            (int)Math.Round(panel.Y + local.Y),
            (int)Math.Round(local.Width),
            (int)Math.Round(local.Height));

    private static bool ContainsBox(VisionBox outer, VisionBox inner) =>
        !outer.IsEmpty
        && !inner.IsEmpty
        && inner.X >= outer.X
        && inner.Y >= outer.Y
        && inner.Right <= outer.Right
        && inner.Bottom <= outer.Bottom;

    private static bool ContainsPoint(VisionBox box, int x, int y) =>
        !box.IsEmpty && x >= box.X && x < box.Right && y >= box.Y && y < box.Bottom;

    private static void RunRightSkillBarFixtures(
        string visionRoot,
        IModelArtifactRegistry registry,
        ILogService log,
        ICollection<string> failures)
    {
        var artifactRoot = Path.Combine(
            visionRoot,
            "train",
            "artifacts",
            "right_skill_bar_l1_v2_dual_profile_20260713",
            "diagnostics");
        var suites = new[]
        {
            (Name: "blind", File: Path.Combine(artifactRoot, "v2_test_onnx", "per_image.jsonl"), Expected: 33),
            (Name: "manual", File: Path.Combine(artifactRoot, "all_manual_onnx", "per_image.jsonl"), Expected: 40)
        };
        using var service = new RightSkillBarDetectService(registry, log);
        foreach (var suite in suites)
        {
            if (!File.Exists(suite.File))
            {
                failures.Add($"Right skill bar {suite.Name} manifest missing: {suite.File}");
                continue;
            }

            var seen = 0;
            var passed = 0;
            var profileCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(suite.File).Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var source = ResolveVisionSourcePath(visionRoot, root.GetProperty("source_image").GetString() ?? "");
                if (!File.Exists(source))
                {
                    failures.Add($"Right skill bar source missing: {source}");
                    continue;
                }

                var frame = LoadCapture(source) with { Sequence = ++seen, CaptureSource = "right-skill-harness" };
                var expected = ParseJsonRect(root.GetProperty("ground_truth_xyxy"));
                var result = service.Detect(frame);
                var iou = result.Available ? IoU(ToRect(result.BoundingBoxPx), expected) : 0;
                var firstComplete = result.Slots.FirstOrDefault(slot => slot.IsComplete);
                var anchorError = firstComplete is null
                    ? double.MaxValue
                    : Math.Max(
                        Math.Abs(firstComplete.Box.X - expected.X),
                        Math.Abs(firstComplete.Box.Y - expected.Y));
                var geometryOk = result.Available
                                 && result.GeometryGatePassed
                                 && result.Slots.Count == 20
                                 && result.Slots.Count(slot => slot.IsComplete) >= 10
                                 && result.Slots.All(slot => slot.IsComplete
                                     ? slot.Box.Width == 32
                                       && slot.Box.Height == 32
                                       && slot.Box.X >= 0
                                       && slot.Box.Y >= 0
                                       && slot.Box.Right <= frame.Width
                                       && slot.Box.Bottom <= frame.Height
                                     : slot.Box.IsEmpty);
                if (geometryOk && iou >= 0.5 && anchorError <= 8)
                    passed++;
                else
                    failures.Add($"Right skill bar {suite.Name} failed: {Path.GetFileName(source)} available={result.Available} iou={iou:0.000} anchor_error={anchorError:0.0} complete={result.Slots.Count(slot => slot.IsComplete)}/20 message={result.Message}");

                if (result.SourceProfile.Length > 0)
                    profileCounts[result.SourceProfile] = profileCounts.GetValueOrDefault(result.SourceProfile) + 1;
            }

            Assert(seen == suite.Expected, $"Right skill bar {suite.Name} fixture count expected {suite.Expected}, actual {seen}.", failures);
            Assert(passed == suite.Expected, $"Right skill bar {suite.Name} pass expected {suite.Expected}, actual {passed}.", failures);
            Console.WriteLine(
                $"RIGHT_SKILL_L1 suite={suite.Name} pass={passed}/{seen} profiles={string.Join(',', profileCounts.Select(item => item.Key + ':' + item.Value))}");
        }

        var protocolSource = Path.Combine(
            visionRoot,
            "train",
            "manual_tests",
            "l1_external_windows_20260712",
            "inputs",
            "input_07.png");
        if (!File.Exists(protocolSource))
        {
            failures.Add($"Right skill L2 protocol source missing: {protocolSource}");
            return;
        }

        var protocolFrame = LoadCapture(protocolSource) with
        {
            Sequence = 9001,
            CaptureSource = "right-skill-l2-protocol"
        };
        var protocolL1 = service.Detect(protocolFrame);
        using var slotService = new VisionV2SlotService(registry, log);
        var protocolL2 = slotService.AnalyzeSkillSurface(
            protocolFrame.Sequence,
            protocolFrame,
            "right_skill_bar",
            protocolL1.Slots,
            protocolL1.Confidence);
        var completeGeometry = protocolL1.Slots
            .Where(slot => slot.IsComplete)
            .ToDictionary(slot => slot.SlotId, StringComparer.Ordinal);
        var protocolOk = protocolL1.Available
                         && slotService.IsAvailable
                         && protocolL2.Count == completeGeometry.Count
                         && protocolL2.All(slot => slot.FrameSequence == protocolFrame.Sequence)
                         && protocolL2.All(slot => string.Equals(slot.Surface, "right_skill_bar", StringComparison.Ordinal))
                         && protocolL2.All(slot => completeGeometry.TryGetValue(slot.SlotId, out var geometry)
                                                   && slot.SlotBox == geometry.Box
                                                   && slot.ClickX == geometry.Box.X + geometry.Box.Width / 2
                                                   && slot.ClickY == geometry.Box.Y + geometry.Box.Height / 2)
                         && protocolL2.All(slot => slot.Identity != VisionSkillIdentityState.Candidate)
                         && protocolL2.All(slot => slot.Identity != VisionSkillIdentityState.Matched
                                                   || string.Equals(slot.IdentitySource, "icons_ingame", StringComparison.Ordinal))
                         && protocolL2.All(slot => !slot.Castable
                                                   || slot.Identity == VisionSkillIdentityState.Matched
                                                   && slot.Cooldown == VisionCooldownState.Ready
                                                   && slot.HangUsable);
        Assert(protocolOk, "Right skill L1 crop -> L2 dynamic-slot protocol must preserve current-frame geometry and safety gates.", failures);
        Console.WriteLine(
            $"RIGHT_SKILL_L2 protocol={(protocolOk ? "PASS" : "FAIL")} slots={protocolL2.Count}/{completeGeometry.Count} matched={protocolL2.Count(slot => slot.Identity == VisionSkillIdentityState.Matched)} ready={protocolL2.Count(slot => slot.Cooldown == VisionCooldownState.Ready)}");
    }

    private static void RunInteractionWindowFixtures(
        string visionRoot,
        IModelArtifactRegistry registry,
        ILogService log,
        ICollection<string> failures)
    {
        var interactionWindowClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "bag_panel",
            "shop_panel",
            "monthly_card_panel",
            "bank_panel",
            "material_panel"
        };
        var reportRoot = Path.Combine(
            visionRoot,
            "train",
            "reports",
            "interaction_window_l1_v3_5class_eval");
        var positiveFile = Path.Combine(reportRoot, "interaction_window_l1_v3_onnx_conf075", "per_image.jsonl");
        var negativeFile = Path.Combine(reportRoot, "interaction_window_l1_v3_negative_gate_onnx_conf075", "per_image.jsonl");
        if (!File.Exists(positiveFile) || !File.Exists(negativeFile))
        {
            failures.Add("Interaction window V3 native gate manifests are missing.");
            return;
        }

        using var service = new InteractionWindowDetectService(registry, log);
        var positiveImages = 0;
        var expectedInstances = 0;
        var matchedInstances = 0;
        var falsePositives = 0;
        foreach (var line in File.ReadLines(positiveFile).Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var source = ResolveVisionSourcePath(visionRoot, root.GetProperty("source_image").GetString() ?? "");
            if (!File.Exists(source))
            {
                failures.Add($"Interaction window positive source missing: {source}");
                continue;
            }

            var frame = LoadCapture(source) with { Sequence = ++positiveImages, CaptureSource = "interaction-window-harness" };
            var result = service.Detect(frame);
            var interactionDetections = result.Detections
                .Where(item => interactionWindowClasses.Contains(item.ClassName))
                .ToArray();
            var expected = root.GetProperty("ground_truth")
                .EnumerateArray()
                .Select(value => new
                {
                    ClassName = value.GetProperty("class_name").GetString() ?? "",
                    Box = ParseJsonRect(value.GetProperty("bbox_xyxy"))
                })
                .ToArray();
            expectedInstances += expected.Length;
            var used = new HashSet<int>();
            foreach (var target in expected)
            {
                var match = interactionDetections
                    .Select((item, index) => new { Item = item, Index = index, Iou = IoU(ToRect(item.Box), target.Box) })
                    .Where(item => !used.Contains(item.Index)
                                   && item.Item.ClassName == target.ClassName
                                   && item.Iou >= 0.5)
                    .OrderByDescending(item => item.Iou)
                    .FirstOrDefault();
                if (match is null)
                {
                    failures.Add($"Interaction window missed {target.ClassName}: {Path.GetFileName(source)} result={result.Message}");
                    continue;
                }

                used.Add(match.Index);
                matchedInstances++;
            }

            falsePositives += interactionDetections.Length - used.Count;
        }

        var negativeImages = 0;
        var negativeFalsePositives = 0;
        foreach (var line in File.ReadLines(negativeFile).Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var source = ResolveVisionSourcePath(visionRoot, root.GetProperty("source_image").GetString() ?? "");
            if (!File.Exists(source))
            {
                failures.Add($"Interaction window negative source missing: {source}");
                continue;
            }

            var frame = LoadCapture(source) with { Sequence = ++negativeImages, CaptureSource = "interaction-window-negative" };
            var result = service.Detect(frame);
            var interactionDetections = result.Detections
                .Where(item => interactionWindowClasses.Contains(item.ClassName))
                .ToArray();
            negativeFalsePositives += interactionDetections.Length;
            if (interactionDetections.Length > 0)
                failures.Add($"Interaction window negative false positive: {Path.GetFileName(source)} classes={string.Join(',', interactionDetections.Select(item => item.ClassName))}");
        }

        Assert(positiveImages == 54, $"Interaction window positive image count expected 54, actual {positiveImages}.", failures);
        Assert(expectedInstances == 104, $"Interaction window expected instance count expected 104, actual {expectedInstances}.", failures);
        Assert(matchedInstances == 104, $"Interaction window matched instance count expected 104, actual {matchedInstances}.", failures);
        Assert(falsePositives == 0, $"Interaction window positive gate false positives: {falsePositives}.", failures);
        Assert(negativeImages == 50, $"Interaction window negative image count expected 50, actual {negativeImages}.", failures);
        Assert(negativeFalsePositives == 0, $"Interaction window negative gate false positives: {negativeFalsePositives}.", failures);
        Console.WriteLine(
            $"INTERACTION_WINDOW_L1 positive={matchedInstances}/{expectedInstances} positive_fp={falsePositives} negative={negativeImages} negative_fp={negativeFalsePositives}");
    }

    private static void RunQuestLogPanelFixtures(
        string visionRoot,
        IModelArtifactRegistry registry,
        ILogService log,
        ICollection<string> failures)
    {
        const string expectedSha256 = "15ab659c94db902aba18a9ebe146465d4a1baa1accf1223d0dd2964dd9795f4e";
        var artifact = registry.FindQuestLogPanelModel();
        var actualSha256 = artifact.IsAvailable
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(artifact.ModelPath)))
                .ToLowerInvariant()
            : "";
        Assert(artifact.IsAvailable, $"Quest log panel artifact unavailable: {artifact.Message}", failures);
        Assert(actualSha256 == expectedSha256, $"Quest log panel SHA256 mismatch: {actualSha256}", failures);
        if (!artifact.IsAvailable || actualSha256 != expectedSha256)
            return;

        var packageRoot = Path.Combine(visionRoot, "train", "packages", "quest_log_panel_l1_v2");
        var manifestPath = Path.Combine(packageRoot, "manifest.jsonl");
        if (!File.Exists(manifestPath))
        {
            failures.Add($"Quest log panel manifest missing: {manifestPath}");
            return;
        }

        using var service = new InteractionWindowDetectService(registry, log);
        var positiveCount = 0;
        var matchedCount = 0;
        var positiveFalsePositives = 0;
        var negativeCount = 0;
        var negativeFalsePositives = 0;
        var materialMissingPassed = false;
        CaptureResult? firstPositiveFrame = null;
        InteractionWindowPerception? firstPositiveResult = null;
        var fastBypassPassed = false;
        var temporalPassed = false;
        var noopPassed = false;
        var fixtureRecords = new List<object>();
        var evidenceRoot = Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            "vision-harness",
            "quest-log-panel-l1-v3");
        Directory.CreateDirectory(evidenceRoot);
        var fixtureReportPath = Path.Combine(evidenceRoot, "fixture-report.json");

        var frameSequence = 0L;
        foreach (var line in File.ReadLines(manifestPath).Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!string.Equals(root.GetProperty("split").GetString(), "test", StringComparison.Ordinal))
                continue;

            var relativeImage = root.GetProperty("dataset_image").GetString() ?? "";
            var imagePath = Path.Combine(packageRoot, relativeImage.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(imagePath))
            {
                failures.Add($"Quest log panel fixture missing: {imagePath}");
                continue;
            }

            var frame = LoadCapture(imagePath) with
            {
                Sequence = ++frameSequence,
                CapturedAt = DateTimeOffset.UnixEpoch.AddMilliseconds(frameSequence * 100),
                CaptureSource = "quest-log-panel-harness"
            };
            var result = service.Detect(frame, includeQuestLogPanel: true);
            var questDetections = result.Detections
                .Where(item => string.Equals(item.ClassName, "quest_log_panel", StringComparison.Ordinal))
                .ToArray();
            var isNegative = root.GetProperty("is_negative").GetBoolean();
            fixtureRecords.Add(new
            {
                image = Path.GetFileName(imagePath),
                is_negative = isNegative,
                frame_sequence = result.FrameSequence,
                observed_at = result.ObservedAt,
                inference_ms = result.InferenceMilliseconds,
                source_width = frame.Width,
                source_height = frame.Height,
                detections = questDetections.Select(item => new
                {
                    item.ClassName,
                    item.Confidence,
                    item.Box.X,
                    item.Box.Y,
                    item.Box.Width,
                    item.Box.Height
                }).ToArray()
            });
            if (isNegative)
            {
                negativeCount++;
                negativeFalsePositives += questDetections.Length;
                if (questDetections.Length > 0)
                    failures.Add($"Quest log panel negative false positive: {Path.GetFileName(imagePath)} count={questDetections.Length}");
                if (string.Equals(Path.GetFileName(imagePath), "external_negative_02.png", StringComparison.OrdinalIgnoreCase))
                    materialMissingPassed = questDetections.Length == 0;
                continue;
            }

            positiveCount++;
            var annotation = root.GetProperty("annotations").EnumerateArray().Single();
            var coordinates = annotation.GetProperty("bbox_xyxy").EnumerateArray().Select(item => item.GetInt32()).ToArray();
            var expected = VisionBox.FromXyxy(coordinates[0], coordinates[1], coordinates[2], coordinates[3]);
            var match = questDetections
                .Select(item => new { Detection = item, Iou = IoU(ToRect(item.Box), ToRect(expected)) })
                .OrderByDescending(item => item.Iou)
                .FirstOrDefault();
            if (match is null || match.Iou < 0.5)
            {
                failures.Add($"Quest log panel positive missed: {Path.GetFileName(imagePath)} result={result.Message}");
                continue;
            }

            matchedCount++;
            positiveFalsePositives += Math.Max(0, questDetections.Length - 1);
            Assert(result.FrameSequence == frame.Sequence, "Quest log panel frame_sequence must match the source frame.", failures);
            Assert(result.ObservedAt == frame.CapturedAt, "Quest log panel timestamp must match the source frame.", failures);
            Assert(match.Detection.Box.X >= 0
                   && match.Detection.Box.Y >= 0
                   && match.Detection.Box.Right <= frame.Width
                   && match.Detection.Box.Bottom <= frame.Height,
                "Quest log panel coordinates must be mapped to source pixels.",
                failures);

            if (firstPositiveFrame is null)
            {
                firstPositiveFrame = frame;
                firstPositiveResult = result;
                SaveQuestPanelOverlayPng(
                    frame,
                    match.Detection,
                    Path.Combine(evidenceRoot, "quest_log_panel_positive_overlay.png"));
            }
        }

        Assert(positiveCount == 14, $"Quest log panel positive count expected 14, actual {positiveCount}.", failures);
        Assert(matchedCount == 14, $"Quest log panel matched count expected 14, actual {matchedCount}.", failures);
        Assert(positiveFalsePositives == 0, $"Quest log panel positive false positives: {positiveFalsePositives}.", failures);
        Assert(negativeCount == 20, $"Quest log panel negative count expected 20, actual {negativeCount}.", failures);
        Assert(negativeFalsePositives == 0, $"Quest log panel negative false positives: {negativeFalsePositives}.", failures);
        Assert(materialMissingPassed, "Quest log panel material_missing_0501x1008 regression failed.", failures);

        // The real in-game screenshot is not distributed with the public repository (it contains
        // player names and chat). Provide your own 1920x1080 NPC-dialog frame via
        // TLBB_QUEST_LOG_HARD_NEGATIVE_FIXTURE or fixtures/quest_log_panel/long_batian_npc_dialog_1920x1080.png.
        var longBatianFixturePath = Environment.GetEnvironmentVariable("TLBB_QUEST_LOG_HARD_NEGATIVE_FIXTURE");
        if (string.IsNullOrWhiteSpace(longBatianFixturePath))
        {
            longBatianFixturePath = Path.Combine(
                AppContext.BaseDirectory,
                "fixtures",
                "quest_log_panel",
                "long_batian_npc_dialog_1920x1080.png");
        }

        var longBatianNegativePassed = false;
        if (!File.Exists(longBatianFixturePath))
        {
            Console.WriteLine($"SKIP quest log panel Long Batian hard-negative: fixture not present ({longBatianFixturePath}).");
        }
        else
        {
            var longBatianFrame = LoadCapture(longBatianFixturePath) with
            {
                Sequence = ++frameSequence,
                CapturedAt = DateTimeOffset.UnixEpoch.AddMilliseconds(frameSequence * 100),
                CaptureSource = "quest-log-panel-long-batian-hard-negative"
            };
            var longBatianResult = service.Detect(longBatianFrame, includeQuestLogPanel: true);
            var longBatianDetections = longBatianResult.Detections
                .Where(item => string.Equals(item.ClassName, "quest_log_panel", StringComparison.Ordinal))
                .ToArray();
            longBatianNegativePassed = longBatianResult.QuestLogPanelState == VisionTriState.Absent
                                       && longBatianDetections.Length == 0;
            Assert(
                longBatianNegativePassed,
                $"Quest log panel Long Batian NPC-dialog hard-negative failed: state={longBatianResult.QuestLogPanelState}, detections={longBatianDetections.Length}.",
                failures);
            File.Copy(
                longBatianFixturePath,
                Path.Combine(evidenceRoot, "long_batian_npc_dialog_hard_negative.png"),
                overwrite: true);
        }

        if (firstPositiveFrame is not null && firstPositiveResult is not null)
        {
            var fastResult = service.Detect(firstPositiveFrame);
            fastBypassPassed = !fastResult.QuestLogPanelRequested
                               && fastResult.Detections.All(item => item.ClassName != "quest_log_panel");
            Assert(fastBypassPassed, "Default interaction-window detection must not run the quest candidate.", failures);

            var gate = new QuestLogPanelTemporalGate();
            var first = gate.Observe(
                "quest-fixture",
                firstPositiveFrame.Width,
                firstPositiveFrame.Height,
                firstPositiveResult);
            var secondFrame = firstPositiveFrame with
            {
                Sequence = firstPositiveFrame.Sequence + 1000,
                CapturedAt = firstPositiveFrame.CapturedAt.AddMilliseconds(100)
            };
            var secondRaw = service.Detect(secondFrame, includeQuestLogPanel: true);
            var second = gate.Observe("quest-fixture", secondFrame.Width, secondFrame.Height, secondRaw);
            temporalPassed = first.QuestLogPanelState == VisionTriState.Unknown
                             && first.Detections.All(item => item.ClassName != "quest_log_panel")
                             && second.QuestLogPanelState == VisionTriState.Present
                             && second.Detections.Count(item => item.ClassName == "quest_log_panel") == 1;
            Assert(temporalPassed, "Quest log panel two-frame temporal gate failed.", failures);

            var controls = new InteractionControlCatalogService(log);
            var readOnlyPlan = new InteractionReadOnlyPlanService(service, controls)
                .Build("quest-fixture", secondFrame, "quest_log_panel");
            noopPassed = readOnlyPlan.Status == InteractionReadOnlyPlanStatus.CaptureRequired
                         && !readOnlyPlan.RuntimeAuthorized
                         && readOnlyPlan.Actions.Count == 0;
            Assert(noopPassed, "Quest log panel candidate must remain read-only/noop.", failures);
            Console.WriteLine(
                $"QUEST_LOG_PANEL_L1 positive={matchedCount}/{positiveCount} positive_fp={positiveFalsePositives} " +
                $"negative={negativeCount} negative_fp={negativeFalsePositives} fast_bypass={fastBypassPassed} " +
                $"temporal={temporalPassed} noop={noopPassed} " +
                $"sha256={actualSha256} overlay={Path.Combine(evidenceRoot, "quest_log_panel_positive_overlay.png")} " +
                $"report={fixtureReportPath}");
        }

        File.WriteAllText(
            fixtureReportPath,
            JsonSerializer.Serialize(new
            {
                model = artifact.ModelPath,
                sha256 = actualSha256,
                runtime_authorized = false,
                positive = new { expected = positiveCount, matched = matchedCount, false_positives = positiveFalsePositives },
                negative = new { expected = negativeCount, false_positives = negativeFalsePositives },
                material_missing_negative_passed = materialMissingPassed,
                long_batian_npc_dialog_negative_passed = longBatianNegativePassed,
                default_fast_bypass_passed = fastBypassPassed,
                temporal_two_frame_passed = temporalPassed,
                read_only_noop_passed = noopPassed,
                fixtures = fixtureRecords
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RunBuffAnchorFixtures(
        string visionRoot,
        IModelArtifactRegistry registry,
        ILogService log,
        ICollection<string> failures)
    {
        var auditPath = Path.Combine(
            visionRoot,
            "train",
            "artifacts",
            "l1_buff_anchor_r3_precise_reviewed_candidate",
            "prediction_geometry_audit.json");
        if (!File.Exists(auditPath))
        {
            failures.Add($"Buff anchor geometry audit missing: {auditPath}");
            return;
        }

        var oldFlag = Environment.GetEnvironmentVariable("TLBB_AGENT_BUFF_ANCHOR_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("TLBB_AGENT_BUFF_ANCHOR_ENABLED", "1");
            using var service = new BuffAnchorDetectService(registry, log);
            using var audit = JsonDocument.Parse(File.ReadAllText(auditPath));
            var seen = 0;
            var passed = 0;
            foreach (var row in audit.RootElement.GetProperty("rows_worst_first").EnumerateArray())
            {
                var source = ResolveVisionSourcePath(visionRoot, row.GetProperty("source_image").GetString() ?? "");
                if (!File.Exists(source))
                {
                    failures.Add($"Buff anchor source missing: {source}");
                    continue;
                }

                var frame = LoadCapture(source) with { Sequence = ++seen, CaptureSource = "buff-anchor-harness" };
                var expected = ParseJsonRect(row.GetProperty("gt_bbox_xyxy"));
                var result = service.Detect(frame);
                var iou = result.AnchorBox.IsEmpty ? 0 : IoU(ToRect(result.AnchorBox), expected);
                var safe = result.Mounted == VisionTriState.Unknown
                           && result.Fatigue == VisionTriState.Unknown
                           && result.PetActiveSkillBlocked;
                if (result.Available && iou >= 0.8 && result.CandidateRows is >= 1 and <= 5 && result.CandidateColumns is >= 3 and <= 6 && safe)
                    passed++;
                else
                    failures.Add($"Buff anchor failed: {Path.GetFileName(source)} available={result.Available} iou={iou:0.000} grid={result.CandidateRows}x{result.CandidateColumns} message={result.Message}");
            }

            Assert(seen == 26, $"Buff anchor fixture count expected 26, actual {seen}.", failures);
            Assert(passed == 26, $"Buff anchor pass expected 26, actual {passed}.", failures);
            Console.WriteLine($"BUFF_ANCHOR_L1 pass={passed}/{seen} identity=unknown feature_flag=enabled_for_harness");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TLBB_AGENT_BUFF_ANCHOR_ENABLED", oldFlag);
        }

        using var disabled = new BuffAnchorDetectService(registry, log);
        var blank = new CaptureResult(64, 64, new byte[64 * 64 * 4]) { Sequence = 9001 };
        var disabledResult = disabled.Detect(blank);
        Assert(!disabledResult.Available
               && disabledResult.Mounted == VisionTriState.Unknown
               && disabledResult.Fatigue == VisionTriState.Unknown
               && disabledResult.PetActiveSkillBlocked,
            "Buff anchor must remain unavailable/unknown when the feature flag is disabled.",
            failures);

        var fusion = new BuffStateFusionService();
        var anchor = new BuffAnchorPerception
        {
            Available = true,
            AnchorBox = new VisionBox(100, 50, 100, 32),
            Source = PerceptionSourceKind.OnnxLayout
        };
        var firstPresent = fusion.Observe("buff-temporal", anchor with { FrameSequence = 1 }, VisionTriState.Unknown, VisionTriState.Present);
        var confirmedPresent = fusion.Observe("buff-temporal", anchor with { FrameSequence = 2 }, VisionTriState.Unknown, VisionTriState.Present);
        var firstUnknown = fusion.Observe("buff-temporal", anchor with { FrameSequence = 3 }, VisionTriState.Unknown, VisionTriState.Unknown);
        var secondUnknown = fusion.Observe("buff-temporal", anchor with { FrameSequence = 4 }, VisionTriState.Unknown, VisionTriState.Unknown);
        var expiredUnknown = fusion.Observe("buff-temporal", anchor with { FrameSequence = 5 }, VisionTriState.Unknown, VisionTriState.Unknown);
        var firstAbsent = fusion.Observe("buff-temporal", anchor with { FrameSequence = 6 }, VisionTriState.Unknown, VisionTriState.Absent);
        var confirmedAbsent = fusion.Observe("buff-temporal", anchor with { FrameSequence = 7 }, VisionTriState.Unknown, VisionTriState.Absent);
        var temporalOk = firstPresent.Fatigue == VisionTriState.Unknown
                         && firstPresent.PetActiveSkillBlocked
                         && confirmedPresent.Fatigue == VisionTriState.Present
                         && confirmedPresent.PetActiveSkillBlocked
                         && firstUnknown.Fatigue == VisionTriState.Present
                         && firstUnknown.PetActiveSkillBlocked
                         && secondUnknown.Fatigue == VisionTriState.Present
                         && secondUnknown.PetActiveSkillBlocked
                         && expiredUnknown.Fatigue == VisionTriState.Unknown
                         && expiredUnknown.PetActiveSkillBlocked
                         && firstAbsent.Fatigue == VisionTriState.Unknown
                         && firstAbsent.PetActiveSkillBlocked
                         && confirmedAbsent.Fatigue == VisionTriState.Absent
                         && !confirmedAbsent.PetActiveSkillBlocked;
        Assert(temporalOk, "Buff fatigue temporal fusion must debounce presence/absence and tolerate two unknown frames.", failures);
        Console.WriteLine($"BUFF_TEMPORAL status={(temporalOk ? "PASS" : "FAIL")} present={confirmedPresent.Fatigue} unknown={expiredUnknown.Fatigue} absent={confirmedAbsent.Fatigue}");
    }

    private static string ResolveVisionSourcePath(string visionRoot, string relativePath) =>
        Path.Combine(
            visionRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar));

    private static VisionRectF ParseJsonRect(JsonElement value)
    {
        var coordinates = value.EnumerateArray().Select(item => item.GetSingle()).ToArray();
        if (coordinates.Length != 4)
            throw new InvalidDataException("Expected an xyxy array with four values.");
        return new VisionRectF(
            coordinates[0],
            coordinates[1],
            Math.Max(0, coordinates[2] - coordinates[0]),
            Math.Max(0, coordinates[3] - coordinates[1]));
    }

    private static void RunImageProbe(
        string imagePath,
        string? overlayPath,
        bool expectNoTarget,
        ILayoutDetectService layoutService,
        ITargetHpSlotService hpSlotService,
        IModelArtifactRegistry registry,
        ILogService log,
        ICollection<string> failures)
    {
        imagePath = Path.GetFullPath(imagePath);
        if (!File.Exists(imagePath))
        {
            failures.Add($"Image probe file missing: {imagePath}");
            return;
        }

        var frame = LoadCapture(imagePath) with
        {
            Sequence = 1,
            CapturedAt = DateTimeOffset.UtcNow,
            CaptureSource = "image-probe"
        };
        var layout = layoutService.Detect(frame);
        Console.WriteLine($"IMAGE path={imagePath} size={frame.Width}x{frame.Height} frame_sequence={frame.Sequence} L1={layout.InferenceMilliseconds:0.0}ms source={layout.Source} message={layout.Message}");
        foreach (var detection in layout.Detections.OrderBy(item => item.Class).ThenByDescending(item => item.Confidence))
        {
            Console.WriteLine(
                $"DETECTION class={detection.Class} conf={detection.Confidence:0.000} " +
                $"box={detection.Box.X:0.0},{detection.Box.Y:0.0},{detection.Box.Width:0.0},{detection.Box.Height:0.0}");
        }

        var panel = layout.Best(LayoutClass.TargetPanel)?.Box;
        var usedSafeCrop = false;
        if (panel is not { IsEmpty: false }
            && TargetPanelSafeCropLocator.TryResolve(frame, out var safePanel))
        {
            var safeCrop = Crop(frame, safePanel);
            var safeHp = safeCrop is null ? null : hpSlotService.Analyze(safeCrop);
            if (safeHp?.HpGroupFound == true)
            {
                panel = safePanel;
                usedSafeCrop = true;
                layout = layout with
                {
                    Detections = layout.Detections
                        .Append(new LayoutDetectionItem(
                            LayoutClass.TargetPanel,
                            safePanel,
                            safeHp.HpGroupConfidence,
                            "target_panel_safe_crop_800"))
                        .ToArray()
                };
            }
        }
        if (panel is not { IsEmpty: false })
        {
            if (!expectNoTarget)
                failures.Add("Image probe did not locate target_panel.");
        }
        else
        {
            if (expectNoTarget)
                failures.Add("Image probe unexpectedly located target_panel.");
            var crop = Crop(frame, panel);
            if (crop is null)
            {
                failures.Add("Image probe target_panel crop failed.");
            }
            else
            {
                var hp = hpSlotService.Analyze(crop);
                var judgement = new TargetJudgementService();
                var name = new OpenTextResult("probe-target", 0.9, OpenTextSlot.TargetName, PerceptionSourceKind.OpenText, "probe");
                var identity = new TargetIdentityEvidence(TargetIdentity.Monster, 0.9, PerceptionSourceKind.Fused, "probe");
                var first = judgement.Judge("image-probe", 1, layout, hp, name, identity, new GlobalConfig());
                var second = judgement.Judge("image-probe", 2, layout, hp, name, identity, new GlobalConfig());
                Console.WriteLine(
                    $"HP signal={hp.Signal} group={hp.HpGroupFound}/{hp.HpGroupConfidence:0.000} " +
                    $"fill={hp.FillVisible}/{hp.FillConfidence:0.000} ratio={hp.ActiveRowFillRatio?.ToString("0.000", CultureInfo.InvariantCulture) ?? "-"} " +
                    $"first={first.Life}/{first.HpPercent?.ToString() ?? "-"} second={second.Life}/{second.HpPercent?.ToString() ?? "-"} " +
                    $"safe_crop={usedSafeCrop}");
            }
        }

        using var slots = new VisionV2SlotService(registry, log);
        var core = slots.AnalyzeCore("image-probe", frame.Sequence, frame, layout);
        var matched = core.SkillBar.VisualSlots.Where(item => item.Identity == VisionSkillIdentityState.Matched).ToArray();
        var ready = matched.Where(item => item.Cooldown == VisionCooldownState.Ready).ToArray();
        var castable = core.SkillBar.VisualSlots.Where(item => item.Castable).ToArray();
        Console.WriteLine(
            $"CORE player={core.SelfBars.PlayerHpPercent}/{core.SelfBars.PlayerMpPercent} " +
            $"pet={core.SelfBars.PetState}/{core.SelfBars.PetHpPercent} " +
            $"self_source={core.SelfBars.Source} self_message='{core.SelfBars.Message}' " +
            $"skills={core.SkillBar.VisualSlots.Count} matched={matched.Length} ready={ready.Length} castable={castable.Length} " +
            $"skill_source={core.SkillBar.Source} skill_message='{core.SkillBar.Message}' " +
            $"pet_restore={core.SkillBar.PetRestoreSkills.Count} " +
            $"monsters={core.Minimap.NearbyMonsterState}/{core.Minimap.NearbyMonsterCount}");
        Console.WriteLine("DEGRADATIONS " + string.Join(" | ", core.Degradations.Select(item =>
            $"{item.Code}:{item.Message}")));
        Console.WriteLine("SKILL_MATCHES " + string.Join(" | ", matched.Select(item =>
            $"{item.SlotId}:{item.SkillName}/{item.SkillId}:{item.Key}:{item.Cooldown}:usable={item.HangUsable}:castable={item.Castable}:score={item.MatchScore:0.000}")));
        Console.WriteLine("PET_RESTORE " + string.Join(" | ", core.SkillBar.PetRestoreSkills.Select(item =>
            $"{item.BusinessClass}:{item.Variant}:{item.Cooldown}:matched={item.IsMatched}:click={item.ClickX},{item.ClickY}:score={item.Confidence:0.000}")));

        if (!string.IsNullOrWhiteSpace(overlayPath))
        {
            overlayPath = Path.GetFullPath(overlayPath);
            Directory.CreateDirectory(Path.GetDirectoryName(overlayPath)!);
            SaveLayoutOverlayPng(frame, layout, overlayPath);
            Console.WriteLine($"OVERLAY {overlayPath}");
        }
    }

    private static string? ReadOption(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }

        return null;
    }

    private static void RunVisionV2Fixtures(
        string visionRoot,
        ILayoutDetectService layoutService,
        IModelArtifactRegistry registry,
        ILogService log,
        ICollection<string> failures)
    {
        const string packageName = "layout_v2_new_modules_r3_pet_clean";
        var datasetRoot = Path.Combine(visionRoot, "train", "packages", packageName, "dataset");
        var testList = Path.Combine(datasetRoot, "splits", "test.txt");
        if (!File.Exists(testList))
        {
            failures.Add($"V2 dataset split missing: {testList}");
            return;
        }

        var imagePath = File.ReadLines(testList)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => Path.Combine(datasetRoot, line.Trim().Replace('/', Path.DirectorySeparatorChar)))
            .FirstOrDefault(path => File.Exists(path) && HasAllCoreV2Labels(datasetRoot, path));
        if (imagePath is null)
        {
            failures.Add("No V2 fixture with player, pet, skill bar, and minimap labels was found.");
            return;
        }

        var frame = LoadCapture(imagePath) with
        {
            Sequence = 1,
            CapturedAt = DateTimeOffset.UtcNow,
            CaptureSource = "v2-harness"
        };
        var l1Watch = Stopwatch.StartNew();
        var layout = layoutService.Detect(frame);
        l1Watch.Stop();
        var required = new[]
        {
            LayoutClass.PlayerStatusPanel,
            LayoutClass.PetStatusPanel,
            LayoutClass.SkillBar,
            LayoutClass.Minimap
        };
        foreach (var @class in required)
        {
            if (layout.Best(@class) is null)
                failures.Add($"V2 L1 fixture is missing {@class}.");
        }

        using var slots = new VisionV2SlotService(registry, log);
        if (!slots.IsAvailable)
        {
            failures.Add("Vision V2 persistent slot host is unavailable.");
            return;
        }

        var slotWatch = Stopwatch.StartNew();
        var core = slots.AnalyzeCore("v2-fixture", frame.Sequence, frame, layout);
        slotWatch.Stop();
        var l1Times = new List<double> { l1Watch.Elapsed.TotalMilliseconds };
        var l1InferenceTimes = new List<double> { layout.InferenceMilliseconds };
        for (var index = 0; index < 5; index++)
        {
            var watch = Stopwatch.StartNew();
            var repeated = layoutService.Detect(frame);
            watch.Stop();
            l1Times.Add(watch.Elapsed.TotalMilliseconds);
            l1InferenceTimes.Add(repeated.InferenceMilliseconds);
        }

        var slotTimes = new List<double> { slotWatch.Elapsed.TotalMilliseconds };
        for (var index = 0; index < 3; index++)
        {
            Thread.Sleep(375);
            var nextFrame = frame with { Sequence = index + 2, CapturedAt = DateTimeOffset.UtcNow };
            var watch = Stopwatch.StartNew();
            core = slots.AnalyzeCore("v2-fixture", nextFrame.Sequence, nextFrame, layout);
            watch.Stop();
            slotTimes.Add(watch.Elapsed.TotalMilliseconds);
        }
        if (core.SelfBars.PlayerHpPercent is null || core.SelfBars.PlayerMpPercent is null)
            failures.Add("V2 player HP/MP slot returned unknown on the fixture.");
        if (core.SelfBars.PetState != PetDeploymentState.Deployed || core.SelfBars.PetHpPercent is null)
            failures.Add("V2 pet slot did not confirm a deployed pet on the fixture.");
        if (core.SkillBar.VisualSlots.Count != 42)
            failures.Add($"V2 multi-row skill slot count expected 42, actual {core.SkillBar.VisualSlots.Count}.");
        var bottomKeys = core.SkillBar.VisualSlots
            .Where(item => item.Row == 2 && item.Key.Length > 0)
            .Select(item => item.Key)
            .ToArray();
        if (!bottomKeys.SequenceEqual(new[] { "Q", "W", "E", "R", "A", "S", "D", "Z", "X", "C" }))
            failures.Add($"V2 bottom-row key hints are invalid: {string.Join(',', bottomKeys)}.");
        if (core.SkillBar.VisualSlots.Any(item =>
                item.Castable
                && (item.Identity != VisionSkillIdentityState.Matched
                    || !item.HangUsable
                    || item.Cooldown != VisionCooldownState.Ready
                    || item.Key.Length == 0)))
            failures.Add("V2 skill safety invariant failed: castable requires matched + hang_usable + ready + key.");
        if (core.Minimap.Source == PerceptionSourceKind.Unavailable)
            failures.Add("V2 minimap slot is unavailable on the fixture.");

        var unknown = slots.Plan("minimap_tracking", "{\"minimap\":{\"nearby_monster_state\":\"unknown\"}}");
        var present = slots.Plan("minimap_tracking", "{\"minimap\":{\"nearby_monster_state\":\"present\",\"nearby_monster_count\":1}}");
        var singleAbsent = slots.Plan("minimap_tracking", "{\"minimap\":{\"decision_scope\":\"single_frame_observation\",\"nearby_monster_state\":\"absent\"}}");
        var confirmedAbsent = slots.Plan("minimap_tracking", "{\"minimap\":{\"decision_scope\":\"temporal_fusion\",\"nearby_monster_state\":\"absent\",\"absence_confirmed\":true}}");
        var ready = slots.Plan("hotkey", "{\"hotkeys\":{\"slots\":[{\"key\":\"F1\",\"cd_state\":\"ready\",\"screen_click_point\":[100,120]}]},\"key\":\"F1\"}");
        var cooling = slots.Plan("hotkey", "{\"hotkeys\":{\"slots\":[{\"key\":\"F1\",\"cd_state\":\"cooling\",\"screen_click_point\":[100,120]}]},\"key\":\"F1\"}");
        var playerHealth = slots.Plan("player_health", "{\"player\":{\"hp_ratio\":0.2},\"pet_restore\":{\"clickable\":[{\"business_class\":\"health_restore_skill\",\"identity_state\":\"matched\",\"cd_state\":\"ready\",\"screen_click_point\":[130,140]}]},\"hp_threshold\":0.5}");
        Assert(unknown.Action == VisionActionKind.Noop, "Unknown minimap state must plan noop.", failures);
        Assert(present.Action == VisionActionKind.AllowFindMonsterOrAttackLoop, "Present minimap state must allow polling.", failures);
        Assert(singleAbsent.Action == VisionActionKind.Noop, "Single-frame minimap absence must not stop polling.", failures);
        Assert(confirmedAbsent.Action == VisionActionKind.StopFindMonsterPolling, "Only temporally confirmed minimap absence may stop polling.", failures);
        Assert(ready.Action == VisionActionKind.Click && ready.ClientX == 100 && ready.ClientY == 120, "Ready hotkey must expose the planner click.", failures);
        Assert(cooling.Action == VisionActionKind.Noop, "Cooling hotkey must plan noop.", failures);
        Assert(playerHealth.Action == VisionActionKind.Click && playerHealth.ClientX == 130, "Ready player-health restore must plan a click.", failures);
        RunVisionClickExecutorTests(log, failures);

        var materialImage = Directory
            .EnumerateFiles(Path.Combine(visionRoot, "datasets", "99_其他杂项", "images"), "*000128.png")
            .FirstOrDefault();
        VisionActionDecision materialAction = VisionActionDecision.Noop("fixture missing");
        VisionActionDecision materialScrollAction = VisionActionDecision.Noop("fixture missing");
        var materialScrollState = "fixture missing";
        VisionActionDecision invalidMaterialAction = VisionActionDecision.Noop("fixture missing");
        if (materialImage is null)
        {
            failures.Add("V2 material-panel fixture was not found.");
        }
        else
        {
            var materialFrame = LoadCapture(materialImage);
            var aux = slots.AnalyzeAuxPanels(materialFrame, new[] { "material_panel" });
            if (aux.Detections.Count == 0)
            {
                failures.Add("L1-Aux did not locate the material panel fixture.");
            }
            else
            {
                var located = aux.Detections[0];
                var panelBox = located.Box;
                var panelCrop = Crop(materialFrame, panelBox);
                if (panelCrop is null)
                {
                    failures.Add("L1-Aux material panel crop failed.");
                }
                else
                {
                    var panel = slots.AnalyzePanel(
                        panelCrop,
                        panelBox,
                        "material_panel",
                        "manufacture",
                        l1Confidence: located.Confidence);
                    using var panelDocument = JsonDocument.Parse(panel.RawJson);
                    materialAction = slots.Plan("policy", JsonSerializer.Serialize(new
                    {
                        slot_result = panelDocument.RootElement.Clone(),
                        policy_mode = "material"
                    }));

                    var scroll = slots.AnalyzePanel(
                        panelCrop,
                        panelBox,
                        "material_panel",
                        "scroll_down",
                        "配方 活力",
                        l1Confidence: located.Confidence);
                    using var scrollDocument = JsonDocument.Parse(scroll.RawJson);
                    materialScrollState = $"{scroll.PolicyState}/{scroll.ButtonCandidateUsable}/{scroll.Message}";
                    materialScrollAction = slots.Plan("policy", JsonSerializer.Serialize(new
                    {
                        slot_result = scrollDocument.RootElement.Clone(),
                        policy_mode = "material"
                    }));

                    var invalid = slots.AnalyzePanel(
                        panelCrop,
                        panelBox,
                        "material_panel",
                        "delete_all",
                        l1Confidence: located.Confidence);
                    using var invalidDocument = JsonDocument.Parse(invalid.RawJson);
                    invalidMaterialAction = slots.Plan("policy", JsonSerializer.Serialize(new
                    {
                        slot_result = invalidDocument.RootElement.Clone(),
                        policy_mode = "material"
                    }));
                }
            }
        }
        Assert(materialAction.Action == VisionActionKind.Click, "Confirmed material control must plan a click.", failures);
        Assert(materialScrollAction.Action == VisionActionKind.Click, "Confirmed material recipe scroll must plan a click.", failures);
        Assert(invalidMaterialAction.Action == VisionActionKind.Noop, "Illegal material action must plan noop.", failures);
        var unknownItem = slots.Plan("shop_item_policy", "{\"item_ocr_text\":\"unlisted item\",\"keep_items\":[],\"sell_items\":[]}");
        Assert(unknownItem.Action == VisionActionKind.KeepItem, "Unknown shop item must be protected.", failures);
        RunMinimapTemporalFusionTests(failures);

        var artifact = registry.FindL1LayoutModel();
        var packagedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(artifact.ModelPath))).ToLowerInvariant();
        using var artifactConfig = JsonDocument.Parse(File.ReadAllText(artifact.ConfigPath));
        var expectedHash = artifactConfig.RootElement
            .GetProperty("hashes")
            .GetProperty("onnx_sha256")
            .GetString()?
            .ToLowerInvariant() ?? "";
        Assert(packagedHash == expectedHash, $"V2 L1 SHA256 mismatch: {packagedHash}", failures);

        Console.WriteLine(
            $"V2_CORE fixture={Path.GetFileName(imagePath)} l1={l1Watch.Elapsed.TotalMilliseconds:0.0}ms " +
            $"slots={slotWatch.Elapsed.TotalMilliseconds:0.0}ms hp={core.SelfBars.PlayerHpPercent}% " +
            $"mp={core.SelfBars.PlayerMpPercent}% pet={core.SelfBars.PetState}/{core.SelfBars.PetHpPercent}% " +
            $"skills={core.SkillBar.VisualSlots.Count} restore={core.SkillBar.PetRestoreSkills.Count} " +
            $"monsters={core.Minimap.NearbyMonsterState}/{core.Minimap.NearbyMonsterCount} " +
            $"coord={core.Minimap.X},{core.Minimap.Y} sha256={packagedHash} " +
            $"self='{core.SelfBars.Message}' degradation='{string.Join(" | ", core.Degradations.Select(item => item.Code + ":" + item.Message))}'");
        Console.WriteLine(
            $"V2_LATENCY l1_total_p50={Percentile(l1Times, 0.50):0.0}ms " +
            $"l1_total_p95={Percentile(l1Times, 0.95):0.0}ms " +
            $"l1_ort_p50={Percentile(l1InferenceTimes, 0.50):0.0}ms " +
            $"slots_p50={Percentile(slotTimes, 0.50):0.0}ms slots_p95={Percentile(slotTimes, 0.95):0.0}ms");
        Console.WriteLine(
            "V2_BOXES " + string.Join(" ", required.Select(@class =>
            {
                var item = layout.Best(@class);
                return item is null
                    ? $"{@class}=missing"
                    : $"{@class}={item.Box.X},{item.Box.Y},{item.Box.Right},{item.Box.Bottom}@{item.Confidence:0.000}";
            })));
        Console.WriteLine(
            $"V2_POLICY unknown={unknown.Action} present={present.Action} single_absent={singleAbsent.Action} confirmed_absent={confirmedAbsent.Action} " +
            $"ready={ready.Action}@{ready.ClientX},{ready.ClientY} cooling={cooling.Action} " +
            $"player_health={playerHealth.Action}@{playerHealth.ClientX},{playerHealth.ClientY} " +
            $"material={materialAction.Action}@{materialAction.ClientX},{materialAction.ClientY} " +
            $"material_scroll={materialScrollAction.Action}@{materialScrollAction.ClientX},{materialScrollAction.ClientY}:{materialScrollAction.Reason}:{materialScrollState} " +
            $"illegal={invalidMaterialAction.Action} unknown_item={unknownItem.Action}");
    }

    private static void RunVisionClickExecutorTests(ILogService log, ICollection<string> failures)
    {
        var input = new RecordingVisionInputService();
        var executor = new VisionActionExecutor(input, log);
        var window = new BoundGameWindow
        {
            Hwnd = new nint(0x7101),
            ClientWidth = 1920,
            ClientHeight = 1080,
            Bitness = ClientBitness.X64
        };
        var config = new GlobalConfig
        {
            InputDispatchMode = InputDispatchMode.PostMessage,
            YieldToManualInput = true,
            ManualInputYieldMs = 750
        };

        var precise = executor.TryExecuteClick(
            window,
            new VisionActionDecision(true, VisionActionKind.Click, 812, 1038, "ready skill slot"),
            config);
        Assert(precise, "Vision executor must accept an in-client L1/L2 click.", failures);
        Assert(input.Clicks.Count == 1
               && input.Clicks[0] == (812, 1038, InputDispatchMode.PostMessage),
            "Vision executor must forward the exact client coordinates and dispatch mode.", failures);
        Assert(input.YieldToUserInput && input.UserInputYieldMs == 750,
            "Vision executor must preserve the manual-input yield policy.", failures);

        var rejected = executor.TryExecuteClick(
            window,
            new VisionActionDecision(true, VisionActionKind.Click, 1920, 1038, "outside client"),
            config);
        Assert(!rejected && input.Clicks.Count == 1,
            "Vision executor must reject an out-of-client coordinate without clicking.", failures);
        Console.WriteLine($"V2_EXECUTOR precise={precise}@812,1038 rejected_outside={!rejected} clicks={input.Clicks.Count}");
    }

    private static void RunXiaoyaoSkillLibraryFixture(
        string visionRoot,
        ILayoutDetectService layoutService,
        IModelArtifactRegistry registry,
        ILogService log,
        ICollection<string> failures)
    {
        var imagePath = Path.Combine(
            visionRoot,
            "datasets",
            "04_技能",
            "images",
            "20260709_182648_842_04_技能_2560x1369_000001.png");
        if (!File.Exists(imagePath))
        {
            failures.Add($"Xiaoyao skill fixture missing: {imagePath}");
            return;
        }

        var frame = LoadCapture(imagePath) with
        {
            Sequence = 1001,
            CapturedAt = DateTimeOffset.UtcNow,
            CaptureSource = "xiaoyao-skill-harness"
        };
        var layout = layoutService.Detect(frame);
        var detection = layout.Best(LayoutClass.SkillBar);
        if (detection is null)
        {
            failures.Add("L1 did not locate skill_bar on the Xiaoyao fixture.");
            return;
        }

        using var slots = new VisionV2SlotService(registry, log);
        var core = slots.AnalyzeCore("xiaoyao-skill-fixture", frame.Sequence, frame, layout);
        var matched = core.SkillBar.VisualSlots
            .Where(item => item.Identity == VisionSkillIdentityState.Matched)
            .ToArray();
        if (core.SkillBar.VisualSlots.Count != 42)
            failures.Add($"Xiaoyao skill fixture expected 42 slots, actual {core.SkillBar.VisualSlots.Count}.");
        if (matched.Length < 12)
            failures.Add($"Xiaoyao skill fixture expected at least 12 matched slots, actual {matched.Length}.");
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["r1c1"] = "xy_001",
            ["r1c3"] = "xy_004",
            ["r3c1"] = "xy_004",
            ["r3c3"] = "xy_016",
            ["r3c5"] = "xy_001"
        };
        foreach (var pair in expected)
        {
            var slot = core.SkillBar.VisualSlots.FirstOrDefault(item => item.SlotId == pair.Key);
            if (slot?.SkillId != pair.Value || slot.Identity != VisionSkillIdentityState.Matched)
                failures.Add($"Xiaoyao skill fixture mismatch {pair.Key}: expected {pair.Value}, actual {slot?.SkillId}/{slot?.Identity}.");
        }
        if (core.SkillBar.VisualSlots.Any(item => item.Castable
                && (item.Identity != VisionSkillIdentityState.Matched
                    || !item.HangUsable
                    || item.Cooldown != VisionCooldownState.Ready
                    || item.Key.Length == 0)))
            failures.Add("Xiaoyao skill fixture violated the castable safety invariant.");
        Console.WriteLine(
            $"SKILL_LIBRARY fixture={Path.GetFileName(imagePath)} " +
            $"box={detection.Box.X},{detection.Box.Y},{detection.Box.Right},{detection.Box.Bottom}@{detection.Confidence:0.000} " +
            $"slots={core.SkillBar.VisualSlots.Count} matched={matched.Length} " +
            $"castable={core.SkillBar.VisualSlots.Count(item => item.Castable)} " +
            $"ids={string.Join(',', matched.Select(item => item.SlotId + ':' + item.SkillId))}");
        Console.WriteLine(
            "SKILL_EXPECTED " + string.Join(" | ", expected.Select(pair =>
            {
                var slot = core.SkillBar.VisualSlots.FirstOrDefault(item => item.SlotId == pair.Key);
                return $"{pair.Key}:{slot?.SkillId}/{slot?.Identity}:score={slot?.MatchScore:0.000}:margin={slot?.MatchMargin:0.000}:cd={slot?.Cooldown}";
            })));
        Console.WriteLine(
            "SKILL_UNKNOWN_TOP " + string.Join(" | ", core.SkillBar.VisualSlots
                .Where(item => item.Identity == VisionSkillIdentityState.Unknown && item.SkillId.Length > 0)
                .OrderByDescending(item => item.MatchScore)
                .Take(10)
                .Select(item => $"{item.SlotId}:{item.SkillId}:score={item.MatchScore:0.000}:margin={item.MatchMargin:0.000}")));

        foreach (var scale in new[] { 0.75, 0.50 })
        {
            var scaled = ResizeCapture(frame, scale) with
            {
                Sequence = frame.Sequence + (long)(scale * 100),
                CapturedAt = DateTimeOffset.UtcNow,
                CaptureSource = $"xiaoyao-skill-harness-{scale:0.00}"
            };
            var scaledLayout = layoutService.Detect(scaled);
            var scaledDetection = scaledLayout.Best(LayoutClass.SkillBar);
            if (scaledDetection is null)
            {
                failures.Add($"L1 did not locate skill_bar on the Xiaoyao fixture at scale {scale:0.00}.");
                continue;
            }
            var scaledCore = slots.AnalyzeCore("xiaoyao-skill-fixture", scaled.Sequence, scaled, scaledLayout);
            var scaledMatched = scaledCore.SkillBar.VisualSlots
                .Where(item => item.Identity == VisionSkillIdentityState.Matched)
                .ToArray();
            foreach (var pair in expected)
            {
                var slot = scaledCore.SkillBar.VisualSlots.FirstOrDefault(item => item.SlotId == pair.Key);
                if (slot?.SkillId != pair.Value || slot.Identity != VisionSkillIdentityState.Matched)
                    failures.Add($"Xiaoyao scale {scale:0.00} mismatch {pair.Key}: expected {pair.Value}, actual {slot?.SkillId}/{slot?.Identity}.");
            }
            Console.WriteLine(
                $"SKILL_LIBRARY_SCALE scale={scale:0.00} size={scaled.Width}x{scaled.Height} " +
                $"box={scaledDetection.Box.X},{scaledDetection.Box.Y},{scaledDetection.Box.Right},{scaledDetection.Box.Bottom}@{scaledDetection.Confidence:0.000} " +
                $"matched={scaledMatched.Length} castable={scaledCore.SkillBar.VisualSlots.Count(item => item.Castable)}");
        }
    }

    private static void RunMinimapTemporalFusionTests(ICollection<string> failures)
    {
        var fusion = new MinimapTemporalFusionService();
        var start = DateTimeOffset.UtcNow;
        MinimapPerception observed = new();
        var blinkingStates = new[]
        {
            VisionTriState.Present,
            VisionTriState.Present,
            VisionTriState.Absent,
            VisionTriState.Absent,
            VisionTriState.Absent
        };
        for (var index = 0; index < blinkingStates.Length; index++)
        {
            observed = fusion.Observe(
                "blink",
                start.AddMilliseconds(index * 200),
                new MinimapPerception { FrameMarkerState = blinkingStates[index] },
                targetPanelVisible: false);
        }
        Assert(observed.NearbyMonsterState == VisionTriState.Present,
            "Blinking minimap marker must remain present within 1200ms.", failures);

        var singleAbsent = fusion.Observe(
            "single-absent",
            start,
            new MinimapPerception { FrameMarkerState = VisionTriState.Absent },
            targetPanelVisible: false);
        Assert(singleAbsent.NearbyMonsterState == VisionTriState.Unknown && !singleAbsent.AbsenceConfirmed,
            "Single absent minimap frame must remain unknown.", failures);

        fusion.ReportTargetSearchAttempt("confirmed-absent");
        fusion.ReportTargetSearchAttempt("confirmed-absent");
        fusion.ReportTargetSearchAttempt("confirmed-absent");
        MinimapPerception confirmedAbsent = new();
        for (var index = 0; index < 6; index++)
        {
            confirmedAbsent = fusion.Observe(
                "confirmed-absent",
                start.AddMilliseconds(index * 200),
                new MinimapPerception { FrameMarkerState = VisionTriState.Absent },
                targetPanelVisible: false);
        }
        Assert(confirmedAbsent.NearbyMonsterState == VisionTriState.Absent
               && confirmedAbsent.AbsenceConfirmed
               && confirmedAbsent.TargetSearchMissCount == 3,
            "Five absent frames over 800ms plus three search misses must confirm absence.", failures);

        var targetOverride = fusion.Observe(
            "confirmed-absent",
            start.AddMilliseconds(1100),
            new MinimapPerception { FrameMarkerState = VisionTriState.Absent },
            targetPanelVisible: true);
        Assert(targetOverride.NearbyMonsterState == VisionTriState.Present
               && targetOverride.TargetSearchMissCount == 0,
            "Visible target panel must override minimap absence and reset search misses.", failures);

        var resourceOnly = fusion.Observe(
            "resource-only",
            start,
            new MinimapPerception
            {
                FrameMarkerState = VisionTriState.Absent,
                ResourceMarkerCount = 3,
                ResourceMarkers = new[] { new VisionBox(10, 10, 5, 5) }
            },
            targetPanelVisible: false);
        Assert(resourceOnly.NearbyMonsterState == VisionTriState.Unknown,
            "Resource markers must never become monster-positive evidence.", failures);

        var replayFusion = new MinimapTemporalFusionService();
        replayFusion.ReportTargetSearchAttempt("cached-replay");
        replayFusion.ReportTargetSearchAttempt("cached-replay");
        replayFusion.ReportTargetSearchAttempt("cached-replay");
        MinimapPerception cachedReplay = new();
        for (var index = 0; index < 6; index++)
        {
            cachedReplay = replayFusion.Observe(
                "cached-replay",
                start.AddMilliseconds(index * 200),
                new MinimapPerception
                {
                    ObservationSequence = 42,
                    FrameMarkerState = VisionTriState.Absent
                },
                targetPanelVisible: false);
        }
        Assert(cachedReplay.NearbyMonsterState == VisionTriState.Unknown
               && cachedReplay.TemporalSampleCount == 1,
            "Replaying one cached L2 absence must not fabricate multiple absent frames.", failures);
    }

    private static void RunMapNamePolicyFixtures(string visionRoot, ICollection<string> failures)
    {
        var policy = new MapNamePolicyService();
        var cases = new[]
        {
            new { Raw = "洛阳", Confidence = 0.85, Source = "sample_match", Expected = "洛阳", Accepted = true, Reason = "exact" },
            new { Raw = "苏州", Confidence = 0.90, Source = "open_text", Expected = "苏州", Accepted = true, Reason = "exact" },
            new { Raw = "大理", Confidence = 0.90, Source = "open_text", Expected = "大理", Accepted = true, Reason = "exact" },
            new { Raw = "楼兰", Confidence = 0.90, Source = "open_text", Expected = "楼兰", Accepted = true, Reason = "exact" },
            new { Raw = "楼兰藏宝洞", Confidence = 0.90, Source = "open_text", Expected = "楼兰藏宝洞", Accepted = true, Reason = "exact" },
            new { Raw = "洛阳门", Confidence = 0.99, Source = "open_text", Expected = "", Accepted = false, Reason = "unknown" },
            new { Raw = "洛阳7", Confidence = 0.99, Source = "open_text", Expected = "", Accepted = false, Reason = "unknown" },
            new { Raw = "洛阳司", Confidence = 0.99, Source = "open_text", Expected = "", Accepted = false, Reason = "unknown" },
            new { Raw = "洛阳城", Confidence = 0.99, Source = "open_text", Expected = "", Accepted = false, Reason = "unknown" },
            new { Raw = "苏洲", Confidence = 0.99, Source = "open_text", Expected = "", Accepted = false, Reason = "unknown" },
            new { Raw = "洛阳", Confidence = 0.60, Source = "open_text", Expected = "", Accepted = false, Reason = "unknown" }
        };
        var caseResults = cases.Select(item =>
        {
            var actual = policy.Evaluate(item.Raw, item.Confidence, item.Source);
            var passed = actual.CanonicalMapName == item.Expected
                         && actual.CandidateAccepted == item.Accepted
                         && actual.Reason == item.Reason;
            return new { item.Raw, item.Expected, Actual = actual, Passed = passed };
        }).ToArray();

        var temporalCases = new[]
        {
            new { Sequence = new[] { "洛阳", "洛阳", "洛阳门" }, Expected = "洛阳", Stable = true },
            new { Sequence = new[] { "洛阳门", "洛阳7", "洛阳司" }, Expected = "", Stable = false },
            new { Sequence = new[] { "苏州", "洛阳", "大理" }, Expected = "", Stable = false },
            new { Sequence = new[] { "楼兰藏宝洞", "楼兰藏宝洞", "楼兰" }, Expected = "楼兰藏宝洞", Stable = true }
        };
        var temporalResults = temporalCases.Select((item, caseIndex) =>
        {
            var windowId = $"map-policy-{caseIndex}";
            MapNamePolicyPerception actual = new();
            for (var index = 0; index < item.Sequence.Length; index++)
            {
                var candidate = policy.Evaluate(item.Sequence[index], 0.91, "open_text");
                actual = policy.Observe(windowId, index + 1, candidate);
            }

            var passed = actual.StableMapName == item.Expected && actual.Stable == item.Stable;
            return new { item.Sequence, item.Expected, Actual = actual, Passed = passed };
        }).ToArray();

        var duplicateWindow = "map-policy-duplicate-frame";
        var duplicateCandidate = policy.Evaluate("洛阳", 0.91, "open_text");
        var first = policy.Observe(duplicateWindow, 100, duplicateCandidate);
        var replay = policy.Observe(duplicateWindow, 100, duplicateCandidate);
        var second = policy.Observe(duplicateWindow, 101, duplicateCandidate);
        var duplicatePass = !first.Stable
                            && !replay.Stable
                            && replay.HistoryCount == 1
                            && second.Stable
                            && second.StableMapName == "洛阳"
                            && second.HistoryCount == 2;
        var hashPass = policy.IsAvailable
                       && policy.AssetSha256.Equals(MapNamePolicyService.ExpectedSha256, StringComparison.OrdinalIgnoreCase);
        var runtimeGatePass = !policy.RuntimeAuthorized;
        var sampleOverride = policy.Evaluate(
            "冰原",
            0.91,
            "sample_match",
            "20260705-140849-冰原");
        var sampleOverridePass = sampleOverride.CandidateAccepted
                                 && sampleOverride.CanonicalMapName == "武夷"
                                 && sampleOverride.Reason == "exact";
        var exactCropPath = Path.Combine(
            visionRoot,
            "train",
            "review",
            "map_name_v2",
            "live_crop_regression",
            "map-name-exact-105x20.png");
        MapNameSampleMatchResult? exactCropMatch = null;
        if (File.Exists(exactCropPath))
        {
            var matcher = new MapNameSampleRecognitionService();
            exactCropMatch = matcher.Match(LoadCapture(exactCropPath));
        }
        var exactCropPass = exactCropMatch is
        {
            IsConfident: true,
            MapName: "洛阳",
            Score: >= 0.70
        };
        var passed = hashPass
                     && runtimeGatePass
                     && caseResults.All(item => item.Passed)
                     && temporalResults.All(item => item.Passed)
                     && duplicatePass
                     && sampleOverridePass
                     && exactCropPass;

        var evidenceRoot = Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            "verification",
            "map-name-policy-v2");
        Directory.CreateDirectory(evidenceRoot);
        var reportPath = Path.Combine(evidenceRoot, "harness-report.json");
        File.WriteAllText(
            reportPath,
            JsonSerializer.Serialize(
                new
                {
                    generatedAt = DateTimeOffset.Now,
                    policy.AssetPath,
                    policy.AssetVersion,
                    policy.AssetSha256,
                    policy.RuntimeAuthorized,
                    policy.LoadMessage,
                    hashPass,
                    runtimeGatePass,
                    sampleOverride = new { actual = sampleOverride, passed = sampleOverridePass },
                    exactCrop = new { path = exactCropPath, match = exactCropMatch, passed = exactCropPass },
                    cases = caseResults,
                    temporalCases = temporalResults,
                    duplicateFrame = new { first, replay, second, passed = duplicatePass },
                    passed
                },
                new JsonSerializerOptions { WriteIndented = true }));

        Assert(passed, "Map-name policy must pass hash, 11 candidate, 4 temporal and duplicate-frame gates.", failures);
        Console.WriteLine(
            $"MAP_NAME_POLICY status={(passed ? "PASS" : "FAIL")} hash={hashPass} runtime_authorized={policy.RuntimeAuthorized} " +
            $"cases={caseResults.Count(item => item.Passed)}/{caseResults.Length} temporal={temporalResults.Count(item => item.Passed)}/{temporalResults.Length} " +
            $"duplicate_frame={duplicatePass} sample_override={sampleOverridePass} exact_crop={exactCropPass} evidence={reportPath}");
    }

    private static bool HasAllCoreV2Labels(string datasetRoot, string imagePath)
    {
        var labelPath = Path.Combine(datasetRoot, "labels", Path.GetFileNameWithoutExtension(imagePath) + ".txt");
        if (!File.Exists(labelPath))
            return false;
        var classes = File.ReadLines(labelPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])
            .ToHashSet(StringComparer.Ordinal);
        return classes.Contains("3") && classes.Contains("4") && classes.Contains("5") && classes.Contains("6");
    }

    private static int Complete(IReadOnlyCollection<string> failures)
    {
        if (failures.Count == 0)
        {
            Console.WriteLine("VISION_HARNESS PASS");
            return 0;
        }

        Console.WriteLine($"VISION_HARNESS FAIL count={failures.Count}");
        foreach (var failure in failures)
            Console.WriteLine($"FAIL {failure}");
        return 1;
    }

    private static void RunLiveSmoke(
        ILayoutDetectService layoutService,
        ITargetHpSlotService hpSlotService,
        IModelArtifactRegistry registry,
        ILogService log,
        ICollection<string> failures,
        int? requestedPid)
    {
        var captureService = new WindowCaptureService(log);
        using var frameService = new LatestFrameService(captureService, log);
        var judgement = new TargetJudgementService();
        var config = new GlobalConfig();
        var targetName = new OpenTextResult("live-target", 0.9, OpenTextSlot.TargetName, PerceptionSourceKind.OpenText, "harness");
        var identity = new TargetIdentityEvidence(TargetIdentity.Monster, 0.9, PerceptionSourceKind.Fused, "harness");
        var candidates = Process.GetProcessesByName("Game")
            .Where(process => process.MainWindowHandle != nint.Zero)
            .Where(process => requestedPid is null || process.Id == requestedPid.Value)
            .Select(process => new
            {
                Process = process,
                Frame = captureService.CaptureClient(process.MainWindowHandle)
            })
            .Where(item => item.Frame is not null)
            .OrderByDescending(item => item.Frame!.Width * item.Frame.Height)
            .ToArray();

        if (candidates.Length == 0)
        {
            failures.Add(requestedPid is null
                ? "No visible Game.exe window was available for live smoke test."
                : $"Game.exe PID {requestedPid.Value} was not available for live smoke test.");
            return;
        }

        var inspectedCandidates = candidates
            .Select(item => new
            {
                item.Process,
                item.Frame,
                Layout = layoutService.Detect(item.Frame!)
            })
            .ToArray();
        foreach (var candidate in inspectedCandidates)
        {
            var panel = candidate.Layout.Best(LayoutClass.TargetPanel);
            Console.WriteLine(
                $"LIVE_CANDIDATE pid={candidate.Process.Id} size={candidate.Frame!.Width}x{candidate.Frame.Height} " +
                $"panel={(panel is null ? "none" : panel.Confidence.ToString("0.000", CultureInfo.InvariantCulture))} " +
                $"message={candidate.Layout.Message}");
        }

        var selected = inspectedCandidates.FirstOrDefault(item =>
            item.Layout.Best(LayoutClass.TargetPanel) is not null) ?? inspectedCandidates[0];
        var outputRoot = Path.Combine(
            Directory.GetCurrentDirectory(),
            "artifacts",
            "vision-smoke",
            DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(outputRoot);

        var records = new List<object>();
        var hubRecords = new List<object>();
        CaptureResult? evidenceFrame = null;
        LayoutSnapshot? evidenceLayout = null;
        TargetHpSlotResult? evidenceHp = null;
        VisionBox? evidencePanel = null;

        for (var index = 0; index < 16; index++)
        {
            var total = Stopwatch.StartNew();
            var capture = Stopwatch.StartNew();
            var frame = frameService.GetLatest(selected.Process.MainWindowHandle);
            capture.Stop();
            if (frame is null)
            {
                failures.Add($"Live capture failed at frame {index}.");
                break;
            }

            var layout = layoutService.Detect(frame);
            var panel = layout.Best(LayoutClass.TargetPanel)?.Box;
            TargetHpSlotResult? hp = null;
            if (panel is { IsEmpty: false })
            {
                var crop = Crop(frame, panel);
                if (crop is not null)
                    hp = hpSlotService.Analyze(crop);
            }
            var target = judgement.Judge(
                $"live-{selected.Process.Id}",
                frame.Sequence,
                layout,
                hp,
                targetName,
                identity,
                config);
            total.Stop();

            if (evidenceFrame is null)
            {
                evidenceFrame = frame;
                evidenceLayout = layout;
                evidenceHp = hp;
                evidencePanel = panel;
            }

            records.Add(new
            {
                index,
                frame_sequence = frame.Sequence,
                captured_at = DateTimeOffset.Now,
                frame_age_ms = Math.Max(0, (DateTimeOffset.UtcNow - frame.CapturedAt).TotalMilliseconds),
                fetch_ms = capture.Elapsed.TotalMilliseconds,
                capture_ms = frame.CaptureMilliseconds,
                l1_ms = layout.InferenceMilliseconds,
                total_ms = total.Elapsed.TotalMilliseconds,
                panel = panel is null ? null : new { panel.X, panel.Y, panel.Width, panel.Height },
                panel_conf = layout.Best(LayoutClass.TargetPanel)?.Confidence,
                hp_signal = hp?.Signal.ToString() ?? "not-run",
                life = target.Life.ToString(),
                should_switch = target.ShouldSwitchTarget,
                hp_ms = hp?.InferenceMilliseconds,
                group_conf = hp?.HpGroupConfidence,
                fill_conf = hp?.FillConfidence
            });

            Console.WriteLine(
                $"LIVE frame={index} seq={frame.Sequence} age={Math.Max(0, (DateTimeOffset.UtcNow - frame.CapturedAt).TotalMilliseconds):0.0}ms " +
                $"fetch={capture.Elapsed.TotalMilliseconds:0.0}ms capture={frame.CaptureMilliseconds:0.0}ms " +
                $"l1={layout.InferenceMilliseconds:0.0}ms panel={layout.Best(LayoutClass.TargetPanel)?.Confidence:0.000} " +
                $"hp={hp?.InferenceMilliseconds:0.0}ms signal={hp?.Signal.ToString() ?? "not-run"} " +
                $"life={target.Life} switch={target.ShouldSwitchTarget} total={total.Elapsed.TotalMilliseconds:0.0}ms");
        }

        var liveWindow = new BoundGameWindow
        {
            Hwnd = selected.Process.MainWindowHandle,
            Pid = selected.Process.Id,
            Title = selected.Process.MainWindowTitle,
            ProcessName = selected.Process.ProcessName,
            Bitness = ClientBitness.X64,
            ClientWidth = selected.Frame!.Width,
            ClientHeight = selected.Frame.Height
        };
        using var openText = new RapidOcrOpenTextRecognizer(log, registry);
        var warmupWatch = Stopwatch.StartNew();
        var warmupReady = openText.TryWarmup(out var warmupMessage);
        warmupWatch.Stop();
        if (!warmupReady)
            failures.Add($"Live OpenText warmup failed: {warmupMessage}");
        using var systemFeedback = new SystemFeedbackRegionDetectService(registry, log);
        using var systemFeedbackAsync = new SystemFeedbackAsyncPipeline(systemFeedback, openText, log);
        using var interactionWindows = new InteractionWindowDetectService(registry, log);
        var evidenceFeedback = evidenceFrame is null
            ? null
            : systemFeedback.Detect(evidenceFrame, openText);
        var evidenceInteraction = evidenceFrame is null
            ? null
            : interactionWindows.Detect(evidenceFrame, includeQuestLogPanel: true);
        var hub = new PerceptionHub(
            frameService,
            layoutService,
            hpSlotService,
            new UnavailableTargetIdentityService(),
            new TargetJudgementService(),
            openText,
            new NullVisionV2SlotService(),
            interactionWindows: interactionWindows,
            systemFeedback: systemFeedback,
            systemFeedbackAsync: systemFeedbackAsync);

        for (var index = 0; index < 16; index++)
        {
            var includeFeedbackText = index >= 12;
            var total = Stopwatch.StartNew();
            var snapshot = hub.Read(
                liveWindow,
                config,
                includeFeedbackText
                    ? new PerceptionReadOptions(
                        IncludeSystemFeedback: true,
                        IncludeSystemFeedbackText: true,
                        IncludeQuestLogPanel: true)
                    : PerceptionReadOptions.QuestLogPanelShadow);
            total.Stop();
            hubRecords.Add(new
            {
                index,
                mode = includeFeedbackText ? "feedback_text" : "fast",
                frame_sequence = snapshot.FrameSequence,
                frame_age_ms = snapshot.FrameAgeMilliseconds,
                capture_ms = snapshot.CaptureMilliseconds,
                l1_ms = snapshot.Layout.InferenceMilliseconds,
                hp_ms = snapshot.TargetHpSlot?.InferenceMilliseconds,
                life = snapshot.Target.Life.ToString(),
                should_switch = snapshot.Target.ShouldSwitchTarget,
                feedback_frame_sequence = snapshot.SystemFeedback.FrameSequence,
                feedback_available = snapshot.SystemFeedback.Available,
                feedback_ms = snapshot.SystemFeedback.InferenceMilliseconds,
                interaction_frame_sequence = snapshot.InteractionWindows.FrameSequence,
                quest_log_panel_state = snapshot.InteractionWindows.QuestLogPanelState.ToString(),
                quest_log_panel = snapshot.InteractionWindows.Detections
                    .Where(item => string.Equals(item.ClassName, "quest_log_panel", StringComparison.Ordinal))
                    .Select(item => new
                    {
                        item.Box.X,
                        item.Box.Y,
                        item.Box.Width,
                        item.Box.Height,
                        item.Confidence
                    })
                    .ToArray(),
                feedback_regions = snapshot.SystemFeedback.Observations.Select(item => new
                {
                    item.SourceBox.X,
                    item.SourceBox.Y,
                    item.SourceBox.Width,
                    item.SourceBox.Height,
                    item.RegionConfidence,
                    text = item.Text.Text,
                    semantic = item.Semantic.ToString()
                }).ToArray(),
                total_ms = total.Elapsed.TotalMilliseconds
            });
            Console.WriteLine(
                $"HUB mode={(includeFeedbackText ? "feedback_text" : "fast")} frame={index} " +
                $"seq={snapshot.FrameSequence} age={snapshot.FrameAgeMilliseconds:0.0}ms " +
                $"capture={snapshot.CaptureMilliseconds:0.0}ms l1={snapshot.Layout.InferenceMilliseconds:0.0}ms " +
                $"hp={snapshot.TargetHpSlot?.InferenceMilliseconds:0.0}ms life={snapshot.Target.Life} " +
                $"switch={snapshot.Target.ShouldSwitchTarget} quest={snapshot.InteractionWindows.QuestLogPanelState}/" +
                $"{snapshot.InteractionWindows.Detections.Count(item => item.ClassName == "quest_log_panel")} " +
                $"feedback={snapshot.SystemFeedback.Observations.Count}/" +
                $"{snapshot.SystemFeedback.InferenceMilliseconds:0.0}ms total={total.Elapsed.TotalMilliseconds:0.0}ms");
        }

        var asyncReturnTimes = new List<double>();
        for (var index = 0; index < 12; index++)
        {
            var requestFeedback = index == 0;
            var total = Stopwatch.StartNew();
            var snapshot = hub.Read(
                liveWindow,
                config,
                requestFeedback
                    ? new PerceptionReadOptions(
                        RequestSystemFeedbackTextAsync: true,
                        IncludeQuestLogPanel: true)
                    : new PerceptionReadOptions(
                        PollSystemFeedbackTextAsync: true,
                        IncludeQuestLogPanel: true));
            total.Stop();
            asyncReturnTimes.Add(total.Elapsed.TotalMilliseconds);
            hubRecords.Add(new
            {
                index,
                mode = requestFeedback ? "feedback_async_request" : "feedback_async_poll",
                frame_sequence = snapshot.FrameSequence,
                frame_age_ms = snapshot.FrameAgeMilliseconds,
                capture_ms = snapshot.CaptureMilliseconds,
                l1_ms = snapshot.Layout.InferenceMilliseconds,
                hp_ms = snapshot.TargetHpSlot?.InferenceMilliseconds,
                life = snapshot.Target.Life.ToString(),
                should_switch = snapshot.Target.ShouldSwitchTarget,
                feedback_requested_frame_sequence = snapshot.SystemFeedback.RequestedFrameSequence,
                feedback_completed_frame_sequence = snapshot.SystemFeedback.FrameSequence,
                feedback_in_flight = snapshot.SystemFeedback.AsyncRequestInFlight,
                feedback_available = snapshot.SystemFeedback.Available,
                feedback_regions = snapshot.SystemFeedback.Observations.Count,
                total_ms = total.Elapsed.TotalMilliseconds
            });
            Console.WriteLine(
                $"HUB mode={(requestFeedback ? "feedback_async_request" : "feedback_async_poll")} frame={index} seq={snapshot.FrameSequence} " +
                $"requested={snapshot.SystemFeedback.RequestedFrameSequence} completed={snapshot.SystemFeedback.FrameSequence} " +
                $"in_flight={snapshot.SystemFeedback.AsyncRequestInFlight} feedback={snapshot.SystemFeedback.Observations.Count} " +
                $"total={total.Elapsed.TotalMilliseconds:0.0}ms");
            Thread.Sleep(80);
        }

        var asyncDeadline = Environment.TickCount64 + 7000;
        var finalAsyncState = systemFeedbackAsync.Read(liveWindow.Id);
        while (finalAsyncState.InFlight && Environment.TickCount64 < asyncDeadline)
        {
            Thread.Sleep(50);
            finalAsyncState = systemFeedbackAsync.Read(liveWindow.Id);
        }
        var asyncP50 = Percentile(asyncReturnTimes, 0.50);
        var asyncP95 = Percentile(asyncReturnTimes, 0.95);
        if (asyncP50 > 750)
            failures.Add($"Live async feedback request p50 was too slow: {asyncP50:0.0}ms.");
        if (finalAsyncState.InFlight || finalAsyncState.Latest is null)
            failures.Add("Live async feedback did not complete within 7 seconds.");
        Console.WriteLine(
            $"HUB_ASYNC_SUMMARY request_p50={asyncP50:0.0}ms request_p95={asyncP95:0.0}ms " +
            $"requested={finalAsyncState.RequestedFrameSequence} completed={finalAsyncState.Latest?.FrameSequence ?? 0} " +
            $"in_flight={finalAsyncState.InFlight} observations={finalAsyncState.Latest?.Observations.Count ?? 0}");

        File.WriteAllText(
            Path.Combine(outputRoot, "result.json"),
            JsonSerializer.Serialize(new
            {
                pid = selected.Process.Id,
                selected.Process.MainWindowTitle,
                open_text_warmup = new
                {
                    ready = warmupReady,
                    elapsed_ms = warmupWatch.Elapsed.TotalMilliseconds,
                    message = warmupMessage
                },
                frames = records,
                hub_frames = hubRecords,
                async_feedback = new
                {
                    request_p50_ms = asyncP50,
                    request_p95_ms = asyncP95,
                    requested_frame_sequence = finalAsyncState.RequestedFrameSequence,
                    completed_frame_sequence = finalAsyncState.Latest?.FrameSequence ?? 0,
                    in_flight = finalAsyncState.InFlight,
                    observations = finalAsyncState.Latest?.Observations.Count ?? 0
                },
                evidence_feedback = evidenceFeedback,
                evidence_interaction = evidenceInteraction
            }, new JsonSerializerOptions { WriteIndented = true }));

        if (evidenceFrame is not null && evidenceLayout is not null)
        {
            SavePng(evidenceFrame, Path.Combine(outputRoot, "frame.png"));
            SaveAnnotatedPng(
                evidenceFrame,
                evidenceLayout,
                evidencePanel,
                evidenceHp,
                evidenceFeedback,
                Path.Combine(outputRoot, "annotated.png"));

            var questPanel = evidenceInteraction?.Detections.FirstOrDefault(item =>
                string.Equals(item.ClassName, "quest_log_panel", StringComparison.Ordinal));
            if (questPanel is not null)
            {
                SaveQuestPanelOverlayPng(
                    evidenceFrame,
                    questPanel,
                    Path.Combine(outputRoot, "quest_log_panel_overlay.png"));
            }
        }

        Console.WriteLine($"LIVE_OUTPUT {outputRoot}");
    }

    private static void RunHpFixtures(
        string visionRoot,
        ITargetHpSlotService service,
        ICollection<string> failures)
    {
        var fixtureRoot = Path.Combine(visionRoot, "train", "artifacts", "target_hp_slot", "fixtures");
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureRoot, "expected.json")));
        var fixtures = expected.RootElement.GetProperty("fixtures").EnumerateArray().ToArray();
        var totalTimes = new List<double>();
        var inferenceTimes = new List<double>();
        var passed = 0;

        foreach (var fixture in fixtures)
        {
            var imageName = fixture.GetProperty("image").GetString()!;
            var labelName = fixture.GetProperty("label").GetString()!;
            var expectedSignal = fixture.GetProperty("expected_slot_signal").GetString()!;
            var image = LoadCapture(Path.Combine(fixtureRoot, imageName));

            var stopwatch = Stopwatch.StartNew();
            var result = service.Analyze(image);
            stopwatch.Stop();
            totalTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
            inferenceTimes.Add(result.InferenceMilliseconds);

            var actualSignal = result.Signal == TargetHpSlotSignal.DeadByHpBottomCandidate
                ? "DeadByHpBottom"
                : result.Signal.ToString();
            var ok = actualSignal == expectedSignal;

            var labels = LoadLabels(Path.Combine(fixtureRoot, labelName), image.Width, image.Height);
            var groupLabel = labels.FirstOrDefault(item => item.ClassId == 0);
            var fillLabel = labels.FirstOrDefault(item => item.ClassId == 1);
            var groupIou = result.HpGroupBox is null || groupLabel is null
                ? 0
                : IoU(result.HpGroupBox, groupLabel.Box);
            var fillIou = result.FillBox is null || fillLabel is null
                ? 0
                : IoU(result.FillBox, fillLabel.Box);

            if (fixture.TryGetProperty("minimum_group_iou", out var minimumGroup))
                ok &= groupIou >= minimumGroup.GetDouble();
            if (fixture.TryGetProperty("minimum_fill_iou", out var minimumFill))
                ok &= fillIou >= minimumFill.GetDouble();

            if (ok)
                passed++;
            else
                failures.Add($"HP fixture {imageName}: expected={expectedSignal}, actual={actualSignal}, groupIoU={groupIou:0.000}, fillIoU={fillIou:0.000}");

            Console.WriteLine(
                $"HP_FIXTURE {imageName} signal={actualSignal} group={result.HpGroupConfidence:0.000} fill={result.FillConfidence:0.000} groupIoU={groupIou:0.000} fillIoU={fillIou:0.000} total={stopwatch.Elapsed.TotalMilliseconds:0.0}ms ort={result.InferenceMilliseconds:0.0}ms");
        }

        var benchmarkImage = LoadCapture(Path.Combine(fixtureRoot, "normal_alive_full.jpg"));
        for (var i = 0; i < 30; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = service.Analyze(benchmarkImage);
            stopwatch.Stop();
            totalTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
            inferenceTimes.Add(result.InferenceMilliseconds);
        }

        Console.WriteLine(
            $"HP_SUMMARY pass={passed}/{fixtures.Length} total_p50={Percentile(totalTimes, 0.50):0.0}ms total_p95={Percentile(totalTimes, 0.95):0.0}ms ort_p50={Percentile(inferenceTimes, 0.50):0.0}ms ort_p95={Percentile(inferenceTimes, 0.95):0.0}ms");
    }

    private static void RunLayoutFixtures(
        string visionRoot,
        ILayoutDetectService layout,
        ITargetHpSlotService hpSlot,
        ICollection<string> failures)
    {
        var datasetRoot = Path.Combine(visionRoot, "train", "packages", "target_p0_yolo_reviewed", "dataset");
        var imageRoot = Path.Combine(datasetRoot, "images");
        var labelRoot = Path.Combine(datasetRoot, "labels");
        var splitPath = Path.Combine(datasetRoot, "splits", "test.txt");
        var imagePaths = File.ReadLines(splitPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => Path.Combine(datasetRoot, line.Trim().Replace('/', Path.DirectorySeparatorChar)))
            .Where(File.Exists)
            .Take(5)
            .ToArray();
        var totalTimes = new List<double>();
        var inferenceTimes = new List<double>();
        var passed = 0;

        foreach (var imagePath in imagePaths)
        {
            var image = LoadCapture(imagePath);
            var stopwatch = Stopwatch.StartNew();
            var result = layout.Detect(image);
            stopwatch.Stop();
            totalTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
            inferenceTimes.Add(result.InferenceMilliseconds);

            var labelPath = Path.Combine(labelRoot, Path.GetFileNameWithoutExtension(imagePath) + ".txt");
            var labels = LoadLabels(labelPath, image.Width, image.Height)
                .Where(item => item.ClassId > 2)
                .Concat(new[]
                {
                    new LabelBox(0, new VisionRectF(248, 0, 260, 82)),
                    new LabelBox(1, new VisionRectF(276, 26, 134, 24)),
                    new LabelBox(2, new VisionRectF(274, 1, 138, 27))
                })
                .ToArray();
            var classIous = new List<double>();
            var panelOk = false;
            for (var classId = 0; classId < 3; classId++)
            {
                var expected = labels.FirstOrDefault(item => item.ClassId == classId);
                var actual = result.Best((LayoutClass)(classId + 1));
                var iou = expected is null || actual is null ? 0 : IoU(ToRect(actual.Box), expected.Box);
                classIous.Add(iou);
                if (classId == 0)
                    panelOk = expected is null ? actual is null : iou >= 0.85;
            }

            var panel = result.Best(LayoutClass.TargetPanel)?.Box;
            TargetHpSlotResult? hpResult = null;
            if (panel is { IsEmpty: false })
            {
                var crop = Crop(image, panel);
                if (crop is not null)
                    hpResult = hpSlot.Analyze(crop);
            }

            if (panelOk)
                passed++;
            else
                failures.Add($"L1 target_panel {Path.GetFileName(imagePath)} IoU={classIous[0]:0.000}");

            Console.WriteLine(
                $"L1_FIXTURE {Path.GetFileName(imagePath)} detections={result.Detections.Count} iou={string.Join('/', classIous.Select(value => value.ToString("0.000")))} total={stopwatch.Elapsed.TotalMilliseconds:0.0}ms ort={result.InferenceMilliseconds:0.0}ms pipeline_hp={hpResult?.Signal.ToString() ?? "not-run"} {result.Message}");
        }

        Console.WriteLine(
            $"L1_SUMMARY pass={passed}/{imagePaths.Length} total_p50={Percentile(totalTimes, 0.50):0.0}ms total_p95={Percentile(totalTimes, 0.95):0.0}ms ort_p50={Percentile(inferenceTimes, 0.50):0.0}ms ort_p95={Percentile(inferenceTimes, 0.95):0.0}ms");
    }

    private static void RunDegradedTests(
        ILayoutDetectService layout,
        ITargetHpSlotService hpSlot,
        ICollection<string> failures)
    {
        var blankPanel = new CaptureResult(260, 82, new byte[260 * 82 * 4]);
        var hp = hpSlot.Analyze(blankPanel);
        Assert(hp.Signal == TargetHpSlotSignal.Unknown, "Blank target crop must be Unknown.", failures);

        var blankFrame = new CaptureResult(1920, 1080, new byte[1920 * 1080 * 4]);
        var l1 = layout.Detect(blankFrame);
        Assert(l1.Best(LayoutClass.TargetPanel) is null, "Blank frame must not contain target_panel.", failures);

        var blank800 = new CaptureResult(800, 600, new byte[800 * 600 * 4]);
        Assert(TargetPanelSafeCropLocator.TryResolve(blank800, out var safePanel), "800px safe target crop must resolve.", failures);
        Assert(safePanel == new VisionBox(242, 0, 270, 88), "Client-only target safe crop geometry changed.", failures);
        Assert(PlayerPanelSafeCropLocator.TryResolve(blank800, out var safePlayer), "800px safe player crop must resolve.", failures);
        Assert(safePlayer == new VisionBox(0, 0, 240, 66), "Client-only player safe crop geometry changed.", failures);
        Assert(PetPanelSafeCropLocator.TryResolve(blank800, out var safePet), "800px safe pet crop must resolve.", failures);
        Assert(safePet == new VisionBox(78, 70, 162, 68), "Client-only pet safe crop geometry changed.", failures);
        Assert(SkillBarSafeCropLocator.TryResolveContent(blank800, out var safeSkill), "800px safe skill crop must resolve.", failures);
        Assert(safeSkill == new VisionBox(220, 442, 520, 142), "Client-only skill safe crop geometry changed.", failures);

        var titledPixels = new byte[800 * 625 * 4];
        for (var y = 0; y < 25; y++)
        {
            for (var x = 0; x < 800; x++)
            {
                var offset = (y * 800 + x) * 4;
                titledPixels[offset] = 245;
                titledPixels[offset + 1] = 245;
                titledPixels[offset + 2] = 245;
            }
        }
        var titled800 = new CaptureResult(800, 625, titledPixels);
        Assert(TargetPanelSafeCropLocator.TryResolve(titled800, out var titledTarget)
               && titledTarget.Y == 25, "Title-bar target offset must resolve.", failures);
        Assert(PlayerPanelSafeCropLocator.TryResolve(titled800, out var titledPlayer)
               && titledPlayer.Y == 25, "Title-bar player offset must resolve.", failures);
        Assert(PetPanelSafeCropLocator.TryResolve(titled800, out var titledPet)
               && titledPet.Y == 95, "Title-bar pet offset must resolve.", failures);
        var safeCrop = Crop(blank800, safePanel);
        var safeHp = safeCrop is null ? null : hpSlot.Analyze(safeCrop);
        Assert(safeHp is { HpGroupFound: false }, "Blank 800px safe crop must not fabricate a target panel.", failures);
        Console.WriteLine($"DEGRADED blank_hp={hp.Signal} blank_l1_detections={l1.Detections.Count} blank800_group={safeHp?.HpGroupFound}");
    }

    private static void RunTemporalTests(ICollection<string> failures)
    {
        var judgement = new TargetJudgementService();
        var config = new GlobalConfig();
        var targetName = new OpenTextResult("fixture-target", 0.9, OpenTextSlot.TargetName, PerceptionSourceKind.OpenText, "harness");
        var identity = new TargetIdentityEvidence(TargetIdentity.Monster, 0.9, PerceptionSourceKind.Fused, "harness");
        var combat = new CombatRecognitionResult("怪物", "", "", "", "", null, null, null, null)
        {
            TargetIsAttackable = true,
            TargetSampleKind = "monster",
            TargetSampleScore = 0.9
        };
        var layout = new LayoutSnapshot(
            1920,
            1080,
            PerceptionSourceKind.OnnxLayout,
            DateTimeOffset.Now,
            new[]
            {
                new LayoutDetectionItem(LayoutClass.TargetPanel, new VisionBox(250, 20, 260, 82), 0.99, "target_panel")
            });
        var dead = new TargetHpSlotResult(
            TargetHpSlotSignal.DeadByHpBottomCandidate,
            true,
            new VisionRectF(45, 25, 134, 24),
            0.98f,
            false,
            null,
            0.2f,
            null,
            "fixture",
            Array.Empty<string>());

        var first = judgement.Judge("fixture-window", 101, layout, dead, targetName, identity, config);
        var duplicate = judgement.Judge("fixture-window", 101, layout, dead, targetName, identity, config);
        Assert(first.Life == TargetLifeState.DeadByHpBottom, "A high-confidence empty HP slot must confirm death on the first frame.", failures);
        Assert(duplicate.Life == TargetLifeState.DeadByHpBottom, "A repeated confirmed-dead frame must remain dead.", failures);

        var weakDead = dead with { HpGroupConfidence = 0.60f };
        var weakJudgement = new TargetJudgementService();
        var weakFirst = weakJudgement.Judge("weak-fixture-window", 111, layout, weakDead, targetName, identity, config);
        var weakSecond = weakJudgement.Judge("weak-fixture-window", 112, layout, weakDead, targetName, identity, config);
        Assert(weakFirst.Life == TargetLifeState.Ambiguous, "A weak cold-start empty candidate still needs temporal confirmation.", failures);
        Assert(weakSecond.Life == TargetLifeState.DeadByHpBottom, "The second weak empty candidate must confirm death.", failures);

        var alive = new TargetHpSlotResult(
            TargetHpSlotSignal.Alive,
            true,
            new VisionRectF(45, 25, 134, 24),
            0.98f,
            true,
            new VisionRectF(47, 28, 90, 5),
            0.97f,
            0.69f,
            "fixture",
            Array.Empty<string>());
        var liveBeforeDeath = judgement.Judge("live-then-dead", 201, layout, alive, targetName, identity, config);
        var immediateDead = judgement.Judge("live-then-dead", 202, layout, dead, targetName, identity, config);
        Assert(liveBeforeDeath.Life == TargetLifeState.Live, "Alive slot must establish a live target history.", failures);
        Assert(immediateDead.Life == TargetLifeState.DeadByHpBottom, "A confident empty slot after confirmed live must switch immediately.", failures);

        var noPanel = layout with { Detections = Array.Empty<LayoutDetectionItem>() };
        var gone = judgement.Judge("fixture-window", 103, noPanel, null, targetName, identity, config);
        Assert(gone.Life == TargetLifeState.Gone, "Present then missing panel must be Gone.", failures);
        Console.WriteLine($"TEMPORAL strong_first={first.Life} strong_duplicate={duplicate.Life} weak_first={weakFirst.Life} weak_second={weakSecond.Life} live_then_empty={immediateDead.Life} missing={gone.Life}");
    }

    private static void RunOpenTextFixture(
        string visionRoot,
        ILayoutDetectService layoutService,
        IModelArtifactRegistry registry,
        ILogService log,
        ICollection<string> failures)
    {
        const string expectedText = "徐欣";
        var imagePath = Path.Combine(
            visionRoot,
            "train",
            "packages",
            "target_p0_yolo_reviewed",
            "dataset",
            "images",
            "target_000438.png");
        var labelPath = Path.Combine(
            visionRoot,
            "train",
            "packages",
            "target_p0_yolo_reviewed",
            "dataset",
            "labels",
            "target_000438.txt");

        if (!File.Exists(imagePath) || !File.Exists(labelPath))
        {
            failures.Add("L2 OpenText fixture target_000438 is missing.");
            return;
        }

        var image = LoadCapture(imagePath);
        var label = LoadLabels(labelPath, image.Width, image.Height).FirstOrDefault(item => item.ClassId == 2);
        if (label is null)
        {
            failures.Add("L2 OpenText fixture has no target_name label.");
            return;
        }

        var labelBox = new VisionBox(
            Math.Max(0, (int)Math.Floor(label.Box.X)),
            Math.Max(0, (int)Math.Floor(label.Box.Y)),
            Math.Max(1, (int)Math.Ceiling(label.Box.Width)),
            Math.Max(1, (int)Math.Ceiling(label.Box.Height)));
        var layout = layoutService.Detect(image);
        var predictedBox = layout.Best(LayoutClass.TargetName)?.Box;

        using var recognizer = new RapidOcrOpenTextRecognizer(log, registry);
        var startup = Stopwatch.StartNew();
        var available = recognizer.TryWarmup(out var warmupMessage);
        startup.Stop();
        if (!available)
        {
            failures.Add($"L2 OpenText warmup failed: {warmupMessage}");
            return;
        }

        var groundTruthCrop = Crop(image, labelBox);
        if (groundTruthCrop is null)
        {
            failures.Add("L2 OpenText ground-truth crop is empty.");
            return;
        }

        var times = new List<double>();
        OpenTextResult? groundTruthResult = null;
        for (var index = 0; index < 5; index++)
        {
            var watch = Stopwatch.StartNew();
            groundTruthResult = recognizer.Recognize(groundTruthCrop, OpenTextSlot.TargetName);
            watch.Stop();
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        var predictedResult = predictedBox is null
            ? OpenTextResult.Empty(OpenTextSlot.TargetName, "L1 target_name missing.")
            : recognizer.Recognize(Crop(image, predictedBox)!, OpenTextSlot.TargetName);
        var p50 = Percentile(times, 0.50);
        var p95 = Percentile(times, 0.95);
        var groundTruthOk = string.Equals(groundTruthResult?.Text, expectedText, StringComparison.Ordinal);
        var predictedOk = string.Equals(predictedResult.Text, expectedText, StringComparison.Ordinal);

        if (!groundTruthOk)
            failures.Add($"L2 OpenText ground-truth crop expected '{expectedText}', actual '{groundTruthResult?.Text}'.");
        if (!predictedOk)
            failures.Add($"L1→L2 target-name pipeline expected '{expectedText}', actual '{predictedResult.Text}'.");
        if (p95 > 500)
            failures.Add($"L2 OpenText warm p95 is too slow: {p95:0.0}ms.");

        Console.WriteLine(
            $"L2_OPENTEXT startup={startup.Elapsed.TotalMilliseconds:0.0}ms " +
            $"warmup='{warmupMessage}' " +
            $"ground_truth='{groundTruthResult?.Text}' conf={groundTruthResult?.Confidence:0.000} " +
            $"predicted='{predictedResult.Text}' conf={predictedResult.Confidence:0.000} " +
            $"warm_p50={p50:0.0}ms warm_p95={p95:0.0}ms");
    }

    private static CaptureResult LoadCapture(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return new CaptureResult(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }

    private static CaptureResult ResizeCapture(CaptureResult source, double scale)
    {
        var transformed = new TransformedBitmap(
            CreateBitmap(source),
            new ScaleTransform(scale, scale));
        var bitmap = new FormatConvertedBitmap(transformed, PixelFormats.Bgra32, null, 0);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return new CaptureResult(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }

    private static CaptureResult? Crop(CaptureResult source, VisionBox box)
    {
        var x = Math.Clamp(box.X, 0, source.Width);
        var y = Math.Clamp(box.Y, 0, source.Height);
        var right = Math.Clamp(box.Right, x, source.Width);
        var bottom = Math.Clamp(box.Bottom, y, source.Height);
        var width = right - x;
        var height = bottom - y;
        if (width <= 0 || height <= 0)
            return null;

        var pixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(
                source.Pixels,
                ((y + row) * source.Width + x) * 4,
                pixels,
                row * width * 4,
                width * 4);
        }

        return new CaptureResult(width, height, pixels);
    }

    private static void SavePng(CaptureResult capture, string path)
    {
        var bitmap = CreateBitmap(capture);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void SaveAnnotatedPng(
        CaptureResult capture,
        LayoutSnapshot layout,
        VisionBox? panel,
        TargetHpSlotResult? hp,
        SystemFeedbackPerception? feedback,
        string path)
    {
        var bitmap = CreateBitmap(capture);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(bitmap, new Rect(0, 0, capture.Width, capture.Height));
            DrawLayoutBox(drawing, layout.Best(LayoutClass.TargetPanel)?.Box, Brushes.Magenta, 3);
            DrawLayoutBox(drawing, layout.Best(LayoutClass.TargetHpBar)?.Box, Brushes.Cyan, 2);
            DrawLayoutBox(drawing, layout.Best(LayoutClass.TargetName)?.Box, Brushes.Yellow, 2);

            if (panel is not null && hp?.HpGroupBox is { } group)
                DrawRect(drawing, Offset(group, panel), Brushes.Lime, 2);
            if (panel is not null && hp?.FillBox is { } fill)
                DrawRect(drawing, Offset(fill, panel), Brushes.Red, 2);
            if (feedback is not null)
            {
                foreach (var observation in feedback.Observations)
                    DrawLayoutBox(drawing, observation.SourceBox, Brushes.Orange, 3);
            }
        }

        var rendered = new RenderTargetBitmap(capture.Width, capture.Height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void SaveQuestPanelOverlayPng(
        CaptureResult capture,
        InteractionWindowDetection detection,
        string path)
    {
        var bitmap = CreateBitmap(capture);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(bitmap, new Rect(0, 0, capture.Width, capture.Height));
            DrawLayoutBox(drawing, detection.Box, Brushes.Lime, 4);
            var label = $"quest_log_panel {detection.Confidence:0.000} " +
                        $"[{detection.Box.X},{detection.Box.Y},{detection.Box.Right},{detection.Box.Bottom}]";
            var text = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas"),
                16,
                Brushes.Lime,
                1.0);
            var labelX = Math.Clamp(detection.Box.X, 0, Math.Max(0, capture.Width - text.Width - 4));
            var labelY = detection.Box.Y >= text.Height + 4
                ? detection.Box.Y - text.Height - 3
                : detection.Box.Bottom + 3;
            drawing.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)),
                null,
                new Rect(labelX - 2, labelY - 1, text.Width + 4, text.Height + 2));
            drawing.DrawText(text, new Point(labelX, labelY));
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var rendered = new RenderTargetBitmap(capture.Width, capture.Height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void SaveLayoutOverlayPng(CaptureResult capture, LayoutSnapshot layout, string path)
    {
        var bitmap = CreateBitmap(capture);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(bitmap, new Rect(0, 0, capture.Width, capture.Height));
            foreach (var detection in layout.Detections.OrderBy(item => item.Class))
            {
                var brush = detection.Class switch
                {
                    LayoutClass.TargetPanel => Brushes.Lime,
                    LayoutClass.TargetHpBar => Brushes.Red,
                    LayoutClass.TargetName => Brushes.Cyan,
                    LayoutClass.PlayerStatusPanel => Brushes.Gold,
                    LayoutClass.PetStatusPanel => Brushes.Orange,
                    LayoutClass.SkillBar => Brushes.Magenta,
                    LayoutClass.Minimap => Brushes.DeepSkyBlue,
                    _ => Brushes.White
                };
                DrawLayoutBox(drawing, detection.Box, brush, 3);
                var label = $"{detection.Class} {detection.Confidence:0.000} " +
                            $"[{detection.Box.X},{detection.Box.Y},{detection.Box.Right},{detection.Box.Bottom}]";
                var text = new FormattedText(
                    label,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Consolas"),
                    13,
                    brush,
                    1.0);
                var labelY = detection.Box.Y >= text.Height + 3
                    ? detection.Box.Y - text.Height - 2
                    : detection.Box.Bottom + 2;
                var labelX = Math.Clamp(detection.Box.X, 0, Math.Max(0, capture.Width - text.Width - 4));
                drawing.DrawRectangle(
                    new SolidColorBrush(Color.FromArgb(210, 0, 0, 0)),
                    null,
                    new Rect(labelX - 2, labelY - 1, text.Width + 4, text.Height + 2));
                drawing.DrawText(text, new Point(labelX, labelY));
            }

            var skillAnchor = layout.Best(LayoutClass.SkillBar)?.Box;
            VisionBox skillContent;
            var skillLabel = "SkillL2Content";
            if (skillAnchor is { IsEmpty: false })
            {
                skillContent = SkillBarSafeCropLocator.ResolveFromAnchor(
                    skillAnchor,
                    capture.Width,
                    capture.Height);
            }
            else
            {
                var player = layout.Best(LayoutClass.PlayerStatusPanel)?.Box;
                var scale = player is null || player.IsEmpty ? 1.0 : player.Width / 240.0;
                SkillBarSafeCropLocator.TryResolveContent(capture, scale, out skillContent);
                skillLabel = "SkillL2FallbackCandidate";
            }

            if (!skillContent.IsEmpty)
            {
                DrawLayoutBox(drawing, skillContent, Brushes.White, 2);
                var label = $"{skillLabel} [{skillContent.X},{skillContent.Y},{skillContent.Right},{skillContent.Bottom}]";
                var text = new FormattedText(
                    label,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Consolas"),
                    13,
                    Brushes.White,
                    1.0);
                var labelY = skillContent.Y >= text.Height + 3
                    ? skillContent.Y - text.Height - 2
                    : skillContent.Bottom + 2;
                var labelX = Math.Clamp(skillContent.X, 0, Math.Max(0, capture.Width - text.Width - 4));
                drawing.DrawRectangle(
                    new SolidColorBrush(Color.FromArgb(210, 0, 0, 0)),
                    null,
                    new Rect(labelX - 2, labelY - 1, text.Width + 4, text.Height + 2));
                drawing.DrawText(text, new Point(labelX, labelY));
            }
        }

        var rendered = new RenderTargetBitmap(capture.Width, capture.Height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static BitmapSource CreateBitmap(CaptureResult capture)
    {
        var bitmap = BitmapSource.Create(
            capture.Width,
            capture.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            capture.Pixels,
            capture.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawLayoutBox(DrawingContext drawing, VisionBox? box, Brush brush, double thickness)
    {
        if (box is null || box.IsEmpty)
            return;
        drawing.DrawRectangle(null, new Pen(brush, thickness), new Rect(box.X, box.Y, box.Width, box.Height));
    }

    private static void DrawRect(DrawingContext drawing, VisionRectF box, Brush brush, double thickness) =>
        drawing.DrawRectangle(null, new Pen(brush, thickness), new Rect(box.X, box.Y, box.Width, box.Height));

    private static VisionRectF Offset(VisionRectF box, VisionBox panel) =>
        new(panel.X + box.X, panel.Y + box.Y, box.Width, box.Height);

    private static IReadOnlyList<LabelBox> LoadLabels(string path, int width, int height)
    {
        return File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(parts =>
            {
                var classId = int.Parse(parts[0], CultureInfo.InvariantCulture);
                var cx = float.Parse(parts[1], CultureInfo.InvariantCulture) * width;
                var cy = float.Parse(parts[2], CultureInfo.InvariantCulture) * height;
                var w = float.Parse(parts[3], CultureInfo.InvariantCulture) * width;
                var h = float.Parse(parts[4], CultureInfo.InvariantCulture) * height;
                return new LabelBox(classId, new VisionRectF(cx - w / 2, cy - h / 2, w, h));
            })
            .ToArray();
    }

    private static VisionRectF ToRect(VisionBox box) => new(box.X, box.Y, box.Width, box.Height);

    private static double IoU(VisionRectF a, VisionRectF b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var union = a.Width * a.Height + b.Width * b.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
            return 0;
        var index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private static void Assert(bool condition, string message, ICollection<string> failures)
    {
        if (!condition)
            failures.Add(message);
    }

    private static string ResolveVisionRoot(string[] args)
    {
        var optionIndex = Array.IndexOf(args, "--vision-root");
        if (optionIndex >= 0 && optionIndex + 1 < args.Length)
            return Path.GetFullPath(args[optionIndex + 1]);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "TLBB 视觉模型");
    }

    private sealed class LiveCombatRecognitionService : ICombatRecognitionService, ICombatFrameRecognitionService
    {
        public CombatRecognitionResult Recognize(BoundGameWindow window, CombatRecognitionConfig config) => Create();

        public CombatRecognitionResult Recognize(
            BoundGameWindow window,
            CombatRecognitionConfig config,
            CaptureResult clientFrame) => Create();

        private static CombatRecognitionResult Create() =>
            new("live-target", "", "", "", "", null, null, null, null)
            {
                TargetIsAttackable = true,
                TargetSampleKind = "monster",
                TargetSampleScore = 0.9
            };
    }

    private sealed class NullVisionV2SlotService : IVisionV2SlotService
    {
        public bool IsAvailable => false;

        public string EngineId => "vision-harness-null";

        public VisionV2CoreSlots AnalyzeCore(
            string windowId,
            long frameSequence,
            CaptureResult frame,
            LayoutSnapshot layout) => new();

        public IReadOnlyList<VisionSkillSlotPerception> AnalyzeSkillSurface(
            long frameSequence,
            CaptureResult frame,
            string surface,
            IReadOnlyList<RightSkillSlotGeometry> slots,
            double surfaceConfidence) => Array.Empty<VisionSkillSlotPerception>();

        public AuxPanelSnapshot AnalyzeAuxPanels(
            CaptureResult frame,
            IReadOnlyCollection<string>? classAllowlist = null) =>
            AuxPanelSnapshot.Unavailable("Vision V2 slots are not exercised by this harness path.");

        public VisionPanelAnalysis AnalyzePanel(
            CaptureResult panelCrop,
            VisionBox panelBox,
            string panelClass,
            string requestedAction,
            string ocrText = "",
            string itemPolicy = "unknown",
            double l1Confidence = 1.0) =>
            VisionPanelAnalysis.Unavailable(panelClass, requestedAction, "Vision V2 panels are not exercised by this harness path.");
    }

    private sealed class RecordingVisionInputService : IInputService
    {
        public List<(int X, int Y, InputDispatchMode Mode)> Clicks { get; } = new();
        public InputDispatchMode DefaultDispatchMode { get; set; } = InputDispatchMode.PostMessage;
        public bool YieldToUserInput { get; set; }
        public int UserInputYieldMs { get; set; }

        public InputYieldStatus GetYieldStatus(nint hwnd, InputDispatchMode mode) =>
            new(false, true, false, 0, 0, 0, false, false, false, false, "vision-harness", "disabled");

        public bool TryParseHotkey(string hotkey, out string error)
        {
            error = string.Empty;
            return !string.IsNullOrWhiteSpace(hotkey);
        }

        public bool SendKey(nint hwnd, string hotkey) => true;
        public bool SendKey(nint hwnd, string hotkey, InputDispatchMode mode) => true;
        public bool SendText(nint hwnd, string text) => true;
        public bool SendText(nint hwnd, string text, InputDispatchMode mode) => true;
        public bool Click(nint hwnd, int x, int y) => Click(hwnd, x, y, DefaultDispatchMode);

        public bool Click(nint hwnd, int x, int y, InputDispatchMode mode)
        {
            Clicks.Add((x, y, mode));
            return true;
        }

        public bool Click(nint hwnd, int x, int y, InputDispatchMode mode, InputMouseButton button) =>
            Click(hwnd, x, y, mode);

        public bool TryConsumeGlobalCooldown(int cooldownMs) => true;
    }

    private sealed record LabelBox(int ClassId, VisionRectF Box);
}

internal sealed class UnavailableHarnessOpenTextRecognizer : IOpenTextRecognizer
{
    public bool IsAvailable => false;
    public string EngineId => "vision-harness-unavailable";

    public OpenTextResult Recognize(CaptureResult crop, OpenTextSlot slot = OpenTextSlot.Generic) =>
        OpenTextResult.Empty(slot, "OpenText intentionally disabled for L1 detector fixture.", EngineId);
}

internal sealed class CountingHarnessOpenTextRecognizer : IOpenTextRecognizer
{
    public int RecognizeCalls { get; private set; }
    public bool IsAvailable => true;
    public string EngineId => "vision-harness-counting";

    public OpenTextResult Recognize(CaptureResult crop, OpenTextSlot slot = OpenTextSlot.Generic)
    {
        RecognizeCalls++;
        return new OpenTextResult(
            "fixture",
            1.0,
            slot,
            PerceptionSourceKind.OpenText,
            EngineId,
            "ok");
    }
}
