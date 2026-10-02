using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TlbbAgent.Contracts;
using TlbbAgent.Core.Captcha;

namespace TlbbAgent.Core.Perception;

public sealed class VisionV2SlotService : IVisionV2SlotService, IVisionActionPlanner, IDisposable
{
    private const int PlayerRefreshMs = 150;
    private const int PetRefreshMs = 200;
    private const int SkillRefreshMs = 150;
    private const int MinimapRefreshMs = 200;
    private const int SlotTimeoutMs = 5000;
    private const int AuxTimeoutMs = 15000;
    private const int PetAbsenceConfirmationFrames = 3;

    private readonly ILogService _log;
    private readonly VisionV2ArtifactSet _artifacts;
    private readonly object _analysisGate = new();
    private readonly object _workerGate = new();
    private readonly Dictionary<string, CoreCache> _cache = new(StringComparer.Ordinal);
    private Process? _worker;
    private bool _probed;
    private bool _available;
    private string _pythonPath = "";
    private string _hostPath = "";
    private string _lastWorkerError = "";
    private long _requestId;

    public VisionV2SlotService(IModelArtifactRegistry artifacts, ILogService log)
    {
        _log = log;
        _artifacts = artifacts.FindVisionV2Artifacts();
    }

    public bool IsAvailable
    {
        get
        {
            EnsureProbed();
            return _available;
        }
    }

    public string EngineId => "tlbb_vision_v2_host";

    public VisionV2CoreSlots AnalyzeCore(
        string windowId,
        long frameSequence,
        CaptureResult frame,
        LayoutSnapshot layout)
    {
        EnsureProbed();
        if (!_available)
            return UnavailableCore(_artifacts.Message);

        lock (_analysisGate)
        {
            var inferenceWatch = Stopwatch.StartNew();
            var key = string.IsNullOrWhiteSpace(windowId) ? "-" : windowId;
            if (!_cache.TryGetValue(key, out var cache))
            {
                cache = new CoreCache();
                _cache[key] = cache;
            }

            if (frameSequence > 0 && cache.FrameSequence == frameSequence)
                return cache.Snapshot;

            var now = Environment.TickCount64;
            var degradations = new List<PerceptionDegradation>();

            if (now - cache.PlayerTicks >= PlayerRefreshMs)
            {
                cache.PlayerTicks = now;
                cache.SelfBars = AnalyzePlayer(frame, layout, cache.SelfBars, degradations);
            }

            if (now - cache.PetTicks >= PetRefreshMs)
            {
                cache.PetTicks = now;
                cache.SelfBars = AnalyzePet(frame, layout, cache, cache.SelfBars, degradations);
            }

            if (now - cache.SkillTicks >= SkillRefreshMs)
            {
                cache.SkillTicks = now;
                cache.SkillBar = AnalyzeSkillBar(frameSequence, frame, layout, degradations);
            }

            if (now - cache.MinimapTicks >= MinimapRefreshMs)
            {
                cache.MinimapTicks = now;
                cache.Minimap = AnalyzeMinimap(frameSequence, frame, layout, degradations);
            }

            inferenceWatch.Stop();
            cache.LastInferenceMilliseconds = inferenceWatch.Elapsed.TotalMilliseconds;

            var snapshot = new VisionV2CoreSlots
            {
                SelfBars = cache.SelfBars,
                SkillBar = cache.SkillBar,
                Minimap = cache.Minimap,
                Degradations = degradations,
                InferenceMilliseconds = cache.LastInferenceMilliseconds
            };
            cache.FrameSequence = frameSequence;
            cache.Snapshot = snapshot;
            return snapshot;
        }
    }

    public IReadOnlyList<VisionSkillSlotPerception> AnalyzeSkillSurface(
        long frameSequence,
        CaptureResult frame,
        string surface,
        IReadOnlyList<RightSkillSlotGeometry> slots,
        double surfaceConfidence)
    {
        EnsureProbed();
        if (!_available || string.IsNullOrWhiteSpace(surface))
            return Array.Empty<VisionSkillSlotPerception>();

        var complete = slots
            .Where(slot => slot.IsComplete && !slot.Box.IsEmpty)
            .OrderBy(slot => slot.Index)
            .ToArray();
        if (complete.Length == 0)
            return Array.Empty<VisionSkillSlotPerception>();

        var surfaceBox = VisionBox.FromXyxy(
            complete.Min(slot => slot.Box.X),
            complete.Min(slot => slot.Box.Y),
            complete.Max(slot => slot.Box.Right),
            complete.Max(slot => slot.Box.Bottom));
        var crop = OpenTextCropUtil.Crop(frame, surfaceBox, pad: 0);
        if (crop is null)
            return Array.Empty<VisionSkillSlotPerception>();

        try
        {
            return WithTempPng(crop, path =>
            {
                var slotSpecs = complete.Select(slot => new
                {
                    index = slot.Index,
                    slot_id = slot.SlotId,
                    row = slot.Row,
                    column = slot.Column,
                    slot_bbox = new[]
                    {
                        slot.Box.X - surfaceBox.X,
                        slot.Box.Y - surfaceBox.Y,
                        slot.Box.Right - surfaceBox.X,
                        slot.Box.Bottom - surfaceBox.Y
                    }
                }).ToArray();
                var result = CallWorker(new
                {
                    op = "slot",
                    mode = "dynamic_skill_slots",
                    image = path,
                    bbox = ToXyxy(surfaceBox),
                    surface,
                    slots = slotSpecs
                }, SlotTimeoutMs);
                return ParseSkillBarSlots(result, surfaceConfidence, frameSequence);
            });
        }
        catch (Exception ex)
        {
            _log.Warn("Vision", $"Dynamic skill surface '{surface}' failed: {ex.Message}");
            return Array.Empty<VisionSkillSlotPerception>();
        }
    }

    public AuxPanelSnapshot AnalyzeAuxPanels(CaptureResult frame, IReadOnlyCollection<string>? classAllowlist = null)
    {
        EnsureProbed();
        if (!_available)
            return AuxPanelSnapshot.Unavailable(_artifacts.Message);

        try
        {
            return WithTempPng(frame, path =>
            {
                var result = CallWorker(new
                {
                    op = "aux",
                    image = path,
                    classes = classAllowlist?.ToArray() ?? Array.Empty<string>()
                }, AuxTimeoutMs);
                return ParseAuxPanels(result);
            });
        }
        catch (Exception ex)
        {
            return AuxPanelSnapshot.Unavailable(ex.Message);
        }
    }

    public VisionPanelAnalysis AnalyzePanel(
        CaptureResult panelCrop,
        VisionBox panelBox,
        string panelClass,
        string requestedAction,
        string ocrText = "",
        string itemPolicy = "unknown",
        double l1Confidence = 1.0)
    {
        EnsureProbed();
        if (!_available)
            return VisionPanelAnalysis.Unavailable(panelClass, requestedAction, _artifacts.Message);

        try
        {
            return WithTempPng(panelCrop, path =>
            {
                var result = CallWorker(new
                {
                    op = "panel",
                    image = path,
                    bbox = ToXyxy(panelBox),
                    panel_class = panelClass,
                    requested_action = requestedAction,
                    ocr_text = ocrText,
                    item_policy = itemPolicy,
                    l1_confidence = l1Confidence
                }, SlotTimeoutMs);
                return ParsePanelAnalysis(result, panelClass, requestedAction);
            });
        }
        catch (Exception ex)
        {
            return VisionPanelAnalysis.Unavailable(panelClass, requestedAction, ex.Message);
        }
    }

    public VisionActionDecision Plan(string mode, string requestJson)
    {
        EnsureProbed();
        if (!_available)
            return VisionActionDecision.Noop(_artifacts.Message);

        try
        {
            using var payloadDocument = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(requestJson) ? "{}" : requestJson);
            var result = CallWorker(new
            {
                op = "plan",
                mode,
                payload = payloadDocument.RootElement.Clone()
            }, SlotTimeoutMs);
            return ParseActionDecision(result);
        }
        catch (Exception ex)
        {
            return VisionActionDecision.Noop($"planner failed: {ex.Message}");
        }
    }

    private SelfBarsPerception AnalyzePlayer(
        CaptureResult frame,
        LayoutSnapshot layout,
        SelfBarsPerception previous,
        ICollection<PerceptionDegradation> degradations)
    {
        var detection = layout.Best(LayoutClass.PlayerStatusPanel);
        var usedSafeCrop = false;
        VisionBox panel;
        if (detection is not null)
        {
            panel = detection.Box;
        }
        else if (PlayerPanelSafeCropLocator.TryResolve(frame, out var safePanel))
        {
            panel = safePanel;
            usedSafeCrop = true;
            degradations.Add(new PerceptionDegradation(
                "v2.player_status.l1_fallback",
                "L1 V2 missed player_status_panel; fixed client ROI is being verified by the player slot."));
        }
        else
        {
            return previous with
            {
                PlayerHpPercent = null,
                PlayerMpPercent = null,
                PlayerPanelBox = VisionBox.Empty,
                PlayerPanelGeometrySource = "unavailable",
                PlayerHpRelativeBox = VisionBox.Empty,
                PlayerMpRelativeBox = VisionBox.Empty,
                PlayerHpSourceBox = VisionBox.Empty,
                PlayerMpSourceBox = VisionBox.Empty,
                PlayerConfidence = 0,
                Source = previous.PetState == PetDeploymentState.Unknown
                    ? PerceptionSourceKind.Unavailable
                    : PerceptionSourceKind.Degraded,
                Message = "player_status_panel missing"
            };
        }

        var crop = OpenTextCropUtil.Crop(frame, panel, pad: 0);
        if (crop is null)
            return previous with
            {
                PlayerHpPercent = null,
                PlayerMpPercent = null,
                PlayerPanelBox = panel,
                PlayerPanelGeometrySource = usedSafeCrop ? "safe_fallback" : "l1_detection",
                PlayerHpRelativeBox = VisionBox.Empty,
                PlayerMpRelativeBox = VisionBox.Empty,
                PlayerHpSourceBox = VisionBox.Empty,
                PlayerMpSourceBox = VisionBox.Empty,
                Message = "player crop failed"
            };

        try
        {
            var result = RunSlot("player", crop, panel);
            var hpRelativeBox = ParseBox(result, "hp_strip_bbox");
            var mpRelativeBox = ParseBox(result, "mp_strip_bbox");
            if (!ReadBoolean(result, "panel_present"))
            {
                return previous with
                {
                    PlayerHpPercent = null,
                    PlayerMpPercent = null,
                    PlayerPanelBox = panel,
                    PlayerPanelGeometrySource = usedSafeCrop ? "safe_fallback" : "l1_detection",
                    PlayerHpRelativeBox = hpRelativeBox,
                    PlayerMpRelativeBox = mpRelativeBox,
                    PlayerHpSourceBox = ToSourceBox(panel, hpRelativeBox),
                    PlayerMpSourceBox = ToSourceBox(panel, mpRelativeBox),
                    PlayerConfidence = 0,
                    Source = PerceptionSourceKind.Degraded,
                    Message = "player ROI has no status-bar evidence"
                };
            }
            return previous with
            {
                PlayerHpPercent = RatioToPercent(ReadNullableDouble(result, "hp_ratio")),
                PlayerMpPercent = RatioToPercent(ReadNullableDouble(result, "mp_ratio")),
                PlayerPanelBox = panel,
                PlayerPanelGeometrySource = usedSafeCrop ? "safe_fallback" : "l1_detection",
                PlayerHpRelativeBox = hpRelativeBox,
                PlayerMpRelativeBox = mpRelativeBox,
                PlayerHpSourceBox = ToSourceBox(panel, hpRelativeBox),
                PlayerMpSourceBox = ToSourceBox(panel, mpRelativeBox),
                PlayerConfidence = detection?.Confidence ?? 0.70,
                Source = usedSafeCrop ? PerceptionSourceKind.Degraded : PerceptionSourceKind.Fused,
                Message = usedSafeCrop
                    ? "fixed player ROI + player slot (L1 fallback)"
                    : "L1 V2 + player slot"
            };
        }
        catch (Exception ex)
        {
            degradations.Add(new PerceptionDegradation("v2.player_status.failed", ex.ToString()));
            return previous with
            {
                PlayerHpPercent = null,
                PlayerMpPercent = null,
                PlayerPanelBox = panel,
                PlayerPanelGeometrySource = usedSafeCrop ? "safe_fallback" : "l1_detection",
                PlayerHpRelativeBox = VisionBox.Empty,
                PlayerMpRelativeBox = VisionBox.Empty,
                PlayerHpSourceBox = VisionBox.Empty,
                PlayerMpSourceBox = VisionBox.Empty,
                PlayerConfidence = 0,
                Source = PerceptionSourceKind.Degraded,
                Message = ex.Message
            };
        }
    }

    private SelfBarsPerception AnalyzePet(
        CaptureResult frame,
        LayoutSnapshot layout,
        CoreCache cache,
        SelfBarsPerception previous,
        ICollection<PerceptionDegradation> degradations)
    {
        var detection = layout.Best(LayoutClass.PetStatusPanel);
        if (detection is null)
        {
            if (PetPanelSafeCropLocator.TryResolve(frame, ResolveHudScale(layout), out var safePanel))
            {
                degradations.Add(new PerceptionDegradation(
                    "v2.pet_status.l1_fallback_diagnostic_only",
                    "L1 V2 missed pet_status_panel; fixed client ROI is diagnostic-only and cannot authorize deployed state."));
                return ConfirmPetAbsence(
                    cache,
                    previous,
                    "pet_status_panel missing; safe fallback is diagnostic-only",
                    safePanel,
                    "safe_fallback");
            }

            return ConfirmPetAbsence(
                cache,
                previous,
                "pet_status_panel and safe pet ROI unavailable",
                VisionBox.Empty,
                "unavailable");
        }

        var panel = detection.Box;

        var crop = OpenTextCropUtil.Crop(frame, panel, pad: 0);
        if (crop is null)
            return previous with
            {
                PetHpPercent = null,
                PetState = PetDeploymentState.Unknown,
                PetPanelBox = panel,
                PetPanelGeometrySource = "l1_detection",
                PetHpRelativeBox = VisionBox.Empty,
                PetHpSourceBox = VisionBox.Empty,
                Message = "pet crop failed"
            };

        try
        {
            var result = RunSlot("pet", crop, panel);
            var hpRelativeBox = ParseBox(result, "hp_strip_bbox");
            var stateText = ReadString(result, "state");
            var state = stateText == "deployed"
                ? PetDeploymentState.Deployed
                : PetDeploymentState.Unknown;

            if (state != PetDeploymentState.Deployed)
            {
                cache.ConsecutivePetAbsence = 0;
                return previous with
                {
                    PetHpPercent = null,
                    PetState = PetDeploymentState.Unknown,
                    PetPanelBox = panel,
                    PetPanelGeometrySource = "l1_detection",
                    PetHpRelativeBox = hpRelativeBox,
                    PetHpSourceBox = ToSourceBox(panel, hpRelativeBox),
                    PetConfidence = 0,
                    Source = PerceptionSourceKind.Degraded,
                    Message = "L1 pet crop exists but pet slot state is unknown"
                };
            }

            var hpPercent = RatioToPercent(ReadNullableDouble(result, "hp_ratio"));
            if (hpPercent == 0)
            {
                hpPercent = null;
                degradations.Add(new PerceptionDegradation(
                    "v2.pet_status.hp_ambiguous",
                    "Pet is deployed but the color-fill slot returned zero; HP is kept unknown to prevent a false low-HP action."));
            }

            cache.ConsecutivePetAbsence = 0;

            var redPixels = ReadInt(result, "red_pixels");
            var greenPixels = ReadInt(result, "green_pixels");
            var fallbackConfidence = Math.Clamp(
                0.55 + Math.Min(1.0, Math.Max(redPixels / 120.0, greenPixels / 90.0)) * 0.35,
                0.65,
                0.90);
            return previous with
            {
                PetHpPercent = hpPercent,
                PetState = state,
                PetPanelBox = panel,
                PetPanelGeometrySource = "l1_detection",
                PetHpRelativeBox = hpRelativeBox,
                PetHpSourceBox = ToSourceBox(panel, hpRelativeBox),
                PetConfidence = detection.Confidence > 0 ? detection.Confidence : fallbackConfidence,
                Source = PerceptionSourceKind.Fused,
                Message = "L1 V2 + pet slot"
            };
        }
        catch (Exception ex)
        {
            degradations.Add(new PerceptionDegradation("v2.pet_status.failed", ex.Message));
            return previous with
            {
                PetHpPercent = null,
                PetState = PetDeploymentState.Unknown,
                PetPanelBox = panel,
                PetPanelGeometrySource = "l1_detection",
                PetHpRelativeBox = VisionBox.Empty,
                PetHpSourceBox = VisionBox.Empty,
                PetConfidence = 0,
                Source = PerceptionSourceKind.Degraded,
                Message = ex.Message
            };
        }
    }

    private static SelfBarsPerception ConfirmPetAbsence(
        CoreCache cache,
        SelfBarsPerception previous,
        string reason,
        VisionBox diagnosticPanel,
        string geometrySource)
    {
        cache.ConsecutivePetAbsence++;
        var confirmedAbsent = cache.ConsecutivePetAbsence >= PetAbsenceConfirmationFrames;
        return previous with
        {
            PetHpPercent = null,
            PetState = confirmedAbsent ? PetDeploymentState.RestingOrAbsent : PetDeploymentState.Unknown,
            PetPanelBox = diagnosticPanel,
            PetPanelGeometrySource = geometrySource,
            PetHpRelativeBox = VisionBox.Empty,
            PetHpSourceBox = VisionBox.Empty,
            PetConfidence = confirmedAbsent ? 0.75 : 0,
            Source = PerceptionSourceKind.Degraded,
            Message = confirmedAbsent
                ? $"{reason}; confirmed after {PetAbsenceConfirmationFrames} observations"
                : $"{reason}; absence pending {cache.ConsecutivePetAbsence}/{PetAbsenceConfirmationFrames}"
        };
    }

    private SkillBarPerception AnalyzeSkillBar(
        long frameSequence,
        CaptureResult frame,
        LayoutSnapshot layout,
        ICollection<PerceptionDegradation> degradations)
    {
        var detection = layout.Best(LayoutClass.SkillBar);
        var usedSafeCrop = false;
        VisionBox contentBox;
        double anchorConfidence;
        if (detection is not null)
        {
            contentBox = SkillBarSafeCropLocator.ResolveFromAnchor(detection.Box, frame.Width, frame.Height);
            anchorConfidence = detection.Confidence;
        }
        else if (SkillBarSafeCropLocator.TryResolveContent(frame, ResolveHudScale(layout), out var safeContent))
        {
            contentBox = safeContent;
            anchorConfidence = 0.55;
            usedSafeCrop = true;
            degradations.Add(new PerceptionDegradation(
                "v2.skill_bar.l1_fallback",
                "L1 V2 missed skill_bar; fixed bottom-center ROI is being verified by identity/CD slots."));
        }
        else
        {
            return new SkillBarPerception
            {
                Source = PerceptionSourceKind.Unavailable,
                Message = "skill_bar and safe content ROI unavailable"
            };
        }

        var crop = OpenTextCropUtil.Crop(frame, contentBox, pad: 0);
        if (crop is null)
            return new SkillBarPerception { Source = PerceptionSourceKind.Unavailable, Message = "skill crop failed" };

        try
        {
            return WithTempPng(crop, path =>
            {
                var bbox = ToXyxy(contentBox);
                var skillSlots = CallWorker(new { op = "slot", mode = "skill_bar_slots", image = path, bbox }, SlotTimeoutMs);
                var petRestore = CallWorker(new { op = "fixed_pet_restore", image = path, bbox }, SlotTimeoutMs);
                return new SkillBarPerception
                {
                    VisualSlots = ParseSkillBarSlots(skillSlots, anchorConfidence, frameSequence),
                    PetRestoreSkills = ParsePetRestoreSlots(petRestore),
                    Source = usedSafeCrop ? PerceptionSourceKind.Degraded : PerceptionSourceKind.Fused,
                    Message = usedSafeCrop
                        ? "fixed skill ROI + normalized identity/CD slots (L1 fallback)"
                        : "L1 V2 anchor + normalized skill-grid identity/CD + pet-restore slots"
                };
            });
        }
        catch (Exception ex)
        {
            degradations.Add(new PerceptionDegradation("v2.skill_bar.failed", ex.Message));
            return new SkillBarPerception { Source = PerceptionSourceKind.Degraded, Message = ex.Message };
        }
    }

    private MinimapPerception AnalyzeMinimap(
        long frameSequence,
        CaptureResult frame,
        LayoutSnapshot layout,
        ICollection<PerceptionDegradation> degradations)
    {
        var detection = layout.Best(LayoutClass.Minimap);
        if (detection is null)
        {
            degradations.Add(new PerceptionDegradation(
                "v2.minimap.missing",
                "L1 V2 did not return minimap."));
            return new MinimapPerception
            {
                ObservationSequence = frameSequence,
                Source = PerceptionSourceKind.Unavailable,
                Message = "minimap missing"
            };
        }

        var crop = OpenTextCropUtil.Crop(frame, detection.Box, pad: 0);
        if (crop is null)
            return new MinimapPerception
            {
                ObservationSequence = frameSequence,
                Source = PerceptionSourceKind.Unavailable,
                Message = "minimap crop failed"
            };

        try
        {
            return WithTempPng(crop, path =>
            {
                var bbox = ToXyxy(detection.Box);
                var tracking = CallWorker(new { op = "slot", mode = "minimap", image = path, bbox }, SlotTimeoutMs);
                var coordinate = CallWorker(new { op = "minimap_coordinate", image = path, bbox }, SlotTimeoutMs);
                var frameState = ReadString(tracking, "frame_marker_state") switch
                {
                    "present" => VisionTriState.Present,
                    "absent" => VisionTriState.Absent,
                    _ => ReadString(tracking, "nearby_monster_state") switch
                    {
                        "present" => VisionTriState.Present,
                        "absent" => VisionTriState.Absent,
                        _ => VisionTriState.Unknown
                    }
                };
                var coordinateObject = TryGetObject(coordinate, "coordinate");
                var x = coordinateObject is { } coordinateValue
                    ? ReadNullableInt(coordinateValue, "x")
                    : null;
                var y = coordinateObject is { } coordinateValue2
                    ? ReadNullableInt(coordinateValue2, "y")
                    : null;

                return new MinimapPerception
                {
                    ObservationSequence = frameSequence,
                    DecisionScope = ReadString(tracking, "decision_scope"),
                    FrameMarkerState = frameState,
                    X = x,
                    Y = y,
                    NearbyMonsterState = frameState,
                    NearbyMonsterCount = ReadInt(tracking, "nearby_monster_count"),
                    MonsterMarkers = ParseBoxes(tracking, "monster_markers", "screen_bbox"),
                    UuTargetRingCount = ReadInt(tracking, "uu_target_ring_count"),
                    NativeOrUuOrangeDotCount = ReadInt(tracking, "native_or_uu_orange_dot_count"),
                    ResourceMarkerCount = ReadInt(tracking, "resource_marker_count"),
                    ResourceMarkers = ParseBoxes(tracking, "resource_markers", "screen_bbox"),
                    CoordinateBox = ParseBox(coordinate, "coord_text_screen_roi"),
                    MapNameBox = ParseBox(coordinate, "map_name_screen_roi"),
                    Confidence = detection.Confidence,
                    CoordinateEngine = ReadString(coordinate, "recognition_engine"),
                    Source = PerceptionSourceKind.Fused,
                    Message = $"frame_marker={frameState}, monster={ReadInt(tracking, "nearby_monster_count")}, resource={ReadInt(tracking, "resource_marker_count")}, coordinate={(x is not null && y is not null ? $"{x},{y}" : "unknown")}"
                };
            });
        }
        catch (Exception ex)
        {
            degradations.Add(new PerceptionDegradation("v2.minimap.failed", ex.Message));
            return new MinimapPerception
            {
                ObservationSequence = frameSequence,
                Source = PerceptionSourceKind.Degraded,
                Message = ex.Message
            };
        }
    }

    private JsonElement RunSlot(string mode, CaptureResult crop, VisionBox box) =>
        WithTempPng(crop, path => CallWorker(new
        {
            op = "slot",
            mode,
            image = path,
            bbox = ToXyxy(box)
        }, SlotTimeoutMs));

    private JsonElement CallWorker(object request, int timeoutMs)
    {
        lock (_workerGate)
        {
            if (!EnsureWorkerRunningLocked())
                throw new InvalidOperationException($"Vision V2 worker unavailable: {_lastWorkerError}");

            var requestId = Interlocked.Increment(ref _requestId);
            var envelope = JsonSerializer.Serialize(new
            {
                id = requestId,
                payload = request
            });
            using var envelopeDocument = JsonDocument.Parse(envelope);
            var requestRoot = envelopeDocument.RootElement;
            var payload = requestRoot.GetProperty("payload");
            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["id"] = JsonSerializer.SerializeToElement(requestId)
            };
            foreach (var property in payload.EnumerateObject())
                properties[property.Name] = property.Value.Clone();
            var line = JsonSerializer.Serialize(properties);

            _worker!.StandardInput.WriteLine(line);
            _worker.StandardInput.Flush();
            var stdout = ReadLineWithTimeout(_worker, timeoutMs);
            if (string.IsNullOrWhiteSpace(stdout))
            {
                var message = string.IsNullOrWhiteSpace(_lastWorkerError)
                    ? "worker returned no response"
                    : _lastWorkerError;
                StopWorkerLocked();
                throw new TimeoutException(message);
            }

            using var response = JsonDocument.Parse(stdout);
            var root = response.RootElement;
            if (!root.TryGetProperty("id", out var id) || id.GetInt64() != requestId)
                throw new InvalidDataException("Vision V2 worker response id mismatch.");
            if (!root.TryGetProperty("result", out var result))
                throw new InvalidDataException("Vision V2 worker response has no result.");
            if (result.TryGetProperty("ok", out var ok) && !ok.GetBoolean())
            {
                var error = result.TryGetProperty("error", out var errorValue)
                    ? errorValue.GetString() ?? "worker failed"
                    : "worker failed";
                throw new InvalidOperationException(error);
            }
            return result.Clone();
        }
    }

    private void EnsureProbed()
    {
        if (_probed)
            return;
        lock (_workerGate)
        {
            if (_probed)
                return;
            _probed = true;
            if (!_artifacts.IsAvailable)
            {
                _available = false;
                _log.Warn("VisionV2", _artifacts.Message);
                return;
            }

            _pythonPath = ResolvePythonPath();
            _hostPath = ResolveHostPath();
            if (string.IsNullOrWhiteSpace(_pythonPath) || !File.Exists(_pythonPath))
            {
                _available = false;
                _log.Warn("VisionV2", "Python runtime not found. Set TLBB_AGENT_PYTHON.");
                return;
            }
            if (!File.Exists(_hostPath))
            {
                _available = false;
                _log.Warn("VisionV2", $"Persistent host not found: {_hostPath}");
                return;
            }

            _available = EnsureWorkerRunningLocked();
            if (_available)
                _log.Info("VisionV2", $"Persistent slot host ready: {_pythonPath}");
            else
                _log.Warn("VisionV2", $"Persistent slot host failed: {_lastWorkerError}");
        }
    }

    private bool EnsureWorkerRunningLocked()
    {
        if (_worker is { HasExited: false })
            return true;

        StopWorkerLocked();
        _lastWorkerError = "";
        var psi = new ProcessStartInfo
        {
            FileName = _pythonPath,
            Arguments = $"\"{_hostPath}\" --serve --artifacts-root \"{_artifacts.RootDirectory}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        var process = Process.Start(psi);
        if (process is null)
        {
            _lastWorkerError = "Process.Start returned null.";
            return false;
        }

        _worker = process;
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
                _lastWorkerError = args.Data;
        };
        process.BeginErrorReadLine();
        var readyLine = ReadLineWithTimeout(process, 20_000);
        if (string.IsNullOrWhiteSpace(readyLine))
        {
            _lastWorkerError = string.IsNullOrWhiteSpace(_lastWorkerError)
                ? "worker ready handshake timed out"
                : _lastWorkerError;
            StopWorkerLocked();
            return false;
        }

        try
        {
            using var ready = JsonDocument.Parse(readyLine);
            return ready.RootElement.TryGetProperty("ready", out var value) && value.GetBoolean();
        }
        catch (Exception ex)
        {
            _lastWorkerError = $"invalid ready response: {ex.Message}";
            StopWorkerLocked();
            return false;
        }
    }

    private void StopWorkerLocked()
    {
        var process = _worker;
        _worker = null;
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
            {
                try { process.StandardInput.Close(); } catch { }
                if (!process.WaitForExit(500))
                    process.Kill(entireProcessTree: true);
            }
        }
        catch { }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_workerGate)
            StopWorkerLocked();
    }

    private static T WithTempPng<T>(CaptureResult capture, Func<string, T> action)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tlbb_v2_{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(path, PngEncoder.Encode(capture.Width, capture.Height, capture.Pixels));
            return action(path);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static VisionActionDecision ParseActionDecision(JsonElement result)
    {
        var raw = result.GetRawText();
        var ok = result.TryGetProperty("ok", out var okValue) && okValue.GetBoolean();
        var actionText = ReadString(result, "action");
        var reason = ReadString(result, "reason");
        var action = actionText switch
        {
            "click" => VisionActionKind.Click,
            "allow_find_monster_or_attack_loop" => VisionActionKind.AllowFindMonsterOrAttackLoop,
            "stop_find_monster_polling" => VisionActionKind.StopFindMonsterPolling,
            "continue_navigation" => VisionActionKind.ContinueNavigation,
            "arrived" => VisionActionKind.Arrived,
            "maybe_stuck" => VisionActionKind.MaybeStuck,
            "keep_item" => VisionActionKind.KeepItem,
            "allow_sell_item" => VisionActionKind.AllowSellItem,
            _ => VisionActionKind.Noop
        };
        var point = ParsePoint(result, "screen_click_point");
        if (!ok || action == VisionActionKind.Click && point is null)
            return VisionActionDecision.Noop(string.IsNullOrWhiteSpace(reason) ? "invalid planner result" : reason, raw);
        return new VisionActionDecision(ok, action, point?.X, point?.Y, reason, raw);
    }

    private static AuxPanelSnapshot ParseAuxPanels(JsonElement result)
    {
        var raw = result.GetRawText();
        var detections = new List<AuxPanelDetection>();
        if (result.TryGetProperty("detections", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
            {
                var box = ParseBox(value, "bbox");
                if (box.IsEmpty)
                    continue;
                detections.Add(new AuxPanelDetection(
                    ReadString(value, "class_name"),
                    box,
                    ReadNullableDouble(value, "confidence") ?? 0,
                    ReadString(value, "template")));
            }
        }
        return new AuxPanelSnapshot(
            true,
            detections,
            ReadString(result, "engine"),
            $"detections={detections.Count}",
            raw);
    }

    private static VisionPanelAnalysis ParsePanelAnalysis(
        JsonElement result,
        string panelClass,
        string requestedAction)
    {
        var point = result.TryGetProperty("primary_button_candidate", out var button)
                    && button.ValueKind == JsonValueKind.Object
            ? ParsePoint(button, "screen_center")
            : null;
        var keywords = result.TryGetProperty("matched_ocr_keywords", out var keywordValues)
                       && keywordValues.ValueKind == JsonValueKind.Array
            ? keywordValues.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString() ?? "")
                .Where(value => value.Length > 0)
                .ToArray()
            : Array.Empty<string>();
        return new VisionPanelAnalysis(
            true,
            panelClass,
            requestedAction,
            ReadString(result, "policy_state"),
            result.TryGetProperty("button_candidate_usable", out var usable)
            && usable.ValueKind == JsonValueKind.True,
            point?.X,
            point?.Y,
            keywords,
            ReadString(result, "safety_rule"),
            result.GetRawText());
    }

    private static IReadOnlyList<VisionSkillSlotPerception> ParseSkillBarSlots(
        JsonElement result,
        double confidence,
        long frameSequence)
    {
        if (!result.TryGetProperty("slots", out var slots) || slots.ValueKind != JsonValueKind.Array)
            return Array.Empty<VisionSkillSlotPerception>();
        var output = new List<VisionSkillSlotPerception>();
        var index = 0;
        foreach (var slot in slots.EnumerateArray())
        {
            var point = ParsePoint(slot, "screen_click_point");
            var identityText = ReadString(slot, "identity_state");
            if (identityText.Length == 0)
                identityText = ReadString(slot, "status");
            var skillName = ReadString(slot, "skill_name");
            if (skillName.Length == 0)
                skillName = ReadString(slot, "name");
            output.Add(new VisionSkillSlotPerception
            {
                FrameSequence = frameSequence,
                Index = ReadNullableInt(slot, "index") ?? index,
                SlotId = ReadString(slot, "slot_id"),
                Row = ReadNullableInt(slot, "row") ?? 0,
                Column = ReadNullableInt(slot, "column") ?? 0,
                Surface = ReadString(slot, "surface"),
                Key = ReadString(slot, "key_hint"),
                ActivationKind = ReadString(slot, "activation_kind"),
                SlotBox = ParseBox(slot, "screen_slot_bbox"),
                ClickX = point?.X,
                ClickY = point?.Y,
                Cooldown = ParseCooldown(ReadString(slot, "cd_state")),
                SkillId = ReadString(slot, "skill_id"),
                SkillName = skillName,
                Identity = ParseSkillIdentity(identityText),
                IdentitySource = ReadString(slot, "identity_source"),
                MatchScore = ReadNullableDouble(slot, "score") ?? 0,
                MatchMargin = ReadNullableDouble(slot, "margin") ?? 0,
                HangUsable = ReadBoolean(slot, "hang_usable"),
                Castable = ReadBoolean(slot, "castable"),
                PolicyClass = ReadString(slot, "policy_class"),
                ReleaseRule = ReadString(slot, "release_rule"),
                Confidence = confidence,
                Message = $"{identityText}/{ReadString(slot, "cd_state")}"
            });
            index++;
        }
        return output;
    }

    private static VisionSkillIdentityState ParseSkillIdentity(string value) => value switch
    {
        "matched" => VisionSkillIdentityState.Matched,
        "candidate" => VisionSkillIdentityState.Candidate,
        "empty" => VisionSkillIdentityState.Empty,
        _ => VisionSkillIdentityState.Unknown
    };

    private static IReadOnlyList<PetRestoreSkillPerception> ParsePetRestoreSlots(JsonElement result)
    {
        if (!result.TryGetProperty("slots", out var slots) || slots.ValueKind != JsonValueKind.Array)
            return Array.Empty<PetRestoreSkillPerception>();
        var output = new List<PetRestoreSkillPerception>();
        foreach (var slot in slots.EnumerateArray())
        {
            var point = ParsePoint(slot, "screen_click_point");
            output.Add(new PetRestoreSkillPerception
            {
                Slot = ReadString(slot, "slot"),
                Variant = ReadString(slot, "variant"),
                BusinessClass = ReadString(slot, "business_class"),
                Cooldown = ParseCooldown(ReadString(slot, "cd_state")),
                ClickX = point?.X,
                ClickY = point?.Y,
                Confidence = ReadNullableDouble(slot, "score") ?? 0,
                IsMatched = ReadString(slot, "identity_state") == "matched"
            });
        }
        return output;
    }

    private static IReadOnlyList<VisionBox> ParseBoxes(JsonElement root, string arrayName, string boxName)
    {
        if (!root.TryGetProperty(arrayName, out var values) || values.ValueKind != JsonValueKind.Array)
            return Array.Empty<VisionBox>();
        return values.EnumerateArray()
            .Select(value => ParseBox(value, boxName))
            .Where(box => !box.IsEmpty)
            .ToArray();
    }

    private static VisionBox ParseBox(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() != 4)
            return VisionBox.Empty;
        var values = value.EnumerateArray().Select(item => item.GetInt32()).ToArray();
        return VisionBox.FromXyxy(values[0], values[1], values[2], values[3]);
    }

    private static VisionBox ToSourceBox(VisionBox panel, VisionBox relative)
    {
        if (panel.IsEmpty || relative.IsEmpty)
            return VisionBox.Empty;

        return VisionBox.FromXyxy(
            panel.X + relative.X,
            panel.Y + relative.Y,
            panel.X + relative.Right,
            panel.Y + relative.Bottom);
    }

    private static (int X, int Y)? ParsePoint(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() != 2)
            return null;
        var values = value.EnumerateArray().Select(item => item.GetInt32()).ToArray();
        return (values[0], values[1]);
    }

    private static JsonElement? TryGetObject(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
            return null;
        return value;
    }

    private static string ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int ReadInt(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

    private static int? ReadNullableInt(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;

    private static double? ReadNullableDouble(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return value.TryGetDouble(out var parsed) ? parsed : null;
    }

    private static bool ReadBoolean(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static int? RatioToPercent(double? ratio) =>
        ratio is null ? null : Math.Clamp((int)Math.Round(ratio.Value * 100), 0, 100);

    private static VisionCooldownState ParseCooldown(string value) => value switch
    {
        "ready" => VisionCooldownState.Ready,
        "cooling" => VisionCooldownState.Cooling,
        _ => VisionCooldownState.Unknown
    };

    private static int[] ToXyxy(VisionBox box) => new[] { box.X, box.Y, box.Right, box.Bottom };

    private static double ResolveHudScale(LayoutSnapshot layout)
    {
        var player = layout.Best(LayoutClass.PlayerStatusPanel)?.Box;
        if (player is null || player.IsEmpty)
            return 1.0;
        var scale = player.Width / 240.0;
        return scale is >= 0.45 and <= 1.60 ? scale : 1.0;
    }

    private static string? ReadLineWithTimeout(Process process, int timeoutMs)
    {
        var read = process.StandardOutput.ReadLineAsync();
        return read.Wait(timeoutMs) ? read.GetAwaiter().GetResult() : null;
    }

    private static string ResolvePythonPath()
    {
        var configured = Environment.GetEnvironmentVariable("TLBB_AGENT_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Python", "Python313", "python.exe");
        if (File.Exists(local))
            return local;
        var local312 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Python", "Python312", "python.exe");
        return File.Exists(local312) ? local312 : "python";
    }

    private static string ResolveHostPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "vision_v2_host.py"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "vision_v2_worker", "vision_v2_host.py")),
            Path.Combine(Environment.CurrentDirectory, "tools", "vision_v2_worker", "vision_v2_host.py")
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static VisionV2CoreSlots UnavailableCore(string reason) => new()
    {
        SelfBars = new SelfBarsPerception { Source = PerceptionSourceKind.Unavailable, Message = reason },
        SkillBar = new SkillBarPerception { Source = PerceptionSourceKind.Unavailable, Message = reason },
        Minimap = new MinimapPerception { Source = PerceptionSourceKind.Unavailable, Message = reason },
        Degradations = new[]
        {
            new PerceptionDegradation("v2.runtime.unavailable", reason, PerceptionSourceKind.Unavailable)
        }
    };

    private sealed class CoreCache
    {
        public long FrameSequence { get; set; }
        public long PlayerTicks { get; set; }
        public long PetTicks { get; set; }
        public long SkillTicks { get; set; }
        public long MinimapTicks { get; set; }
        public int ConsecutivePetAbsence { get; set; }
        public double LastInferenceMilliseconds { get; set; }
        public SelfBarsPerception SelfBars { get; set; } = new();
        public SkillBarPerception SkillBar { get; set; } = new();
        public MinimapPerception Minimap { get; set; } = new();
        public VisionV2CoreSlots Snapshot { get; set; } = new();
    }
}

public sealed class VisionActionExecutor : IVisionActionExecutor
{
    private readonly IInputService _input;
    private readonly ILogService _log;

    public VisionActionExecutor(IInputService input, ILogService log)
    {
        _input = input;
        _log = log;
    }

    public InputDispatchReceipt DispatchClick(
        BoundGameWindow window,
        VisionActionDecision decision,
        GlobalConfig config)
    {
        if (!decision.Ok || decision.Action != VisionActionKind.Click)
        {
            return InputDispatchReceipt.Rejected(
                config.InputDispatchMode,
                InputMouseButton.Left,
                decision.ClientX ?? -1,
                decision.ClientY ?? -1,
                "planner_decision_is_not_an_authorized_click");
        }
        if (decision.ClientX is not { } x || decision.ClientY is not { } y)
        {
            return InputDispatchReceipt.Rejected(
                config.InputDispatchMode,
                InputMouseButton.Left,
                -1,
                -1,
                "planner_click_has_no_client_coordinates");
        }
        if (x < 0 || y < 0 || x >= window.ClientWidth || y >= window.ClientHeight)
        {
            _log.Warn("VisionAction", $"Planner click rejected outside client area: {x},{y}.", window.Id);
            return InputDispatchReceipt.Rejected(
                config.InputDispatchMode,
                InputMouseButton.Left,
                x,
                y,
                "planner_click_outside_client_area");
        }
        _input.ConfigureWindowInput(
            window.Hwnd,
            config.InputDispatchMode,
            config.YieldToManualInput,
            config.ManualInputYieldMs);
        var receipt = _input.DispatchClickForConfirmation(
            window.Hwnd,
            x,
            y,
            config.InputDispatchMode);
        if (receipt.Delivered)
        {
            _log.Trace(
                "VisionAction",
                $"Click delivered at ({x},{y}) by {receipt.Mode}; business outcome still requires fresh-frame confirmation.",
                window.Id);
        }

        return receipt;
    }

    public bool TryExecuteClick(BoundGameWindow window, VisionActionDecision decision, GlobalConfig config) =>
        DispatchClick(window, decision, config).Delivered;
}
