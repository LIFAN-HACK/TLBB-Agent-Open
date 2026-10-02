using System.Collections.Concurrent;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public sealed class PerceptionHub : IPerceptionHub
{
    // The HUD anchors are static while resolution is unchanged. Target HP still runs
    // through its dedicated slot model on every combat scan, so a longer L1 cache cuts
    // full-frame ONNX load without delaying death or new-target confirmation.
    private static readonly TimeSpan LayoutRefreshInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan RightSkillRefreshInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan BuffRefreshInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan InteractionWindowRefreshInterval = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan MapNameRefreshInterval = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan TargetNameReuseInterval = TimeSpan.FromSeconds(5);
    private readonly ILatestFrameService _frames;
    private readonly ILayoutDetectService _layout;
    private readonly ITargetHpSlotService _targetHpSlot;
    private readonly ITargetIdentityService _targetIdentity;
    private readonly ITargetJudgementService _targetJudgement;
    private readonly IOpenTextRecognizer _openText;
    private readonly IVisionV2SlotService _slots;
    private readonly IMinimapTemporalFusionService _minimapFusion;
    private readonly IRightSkillBarDetectService? _rightSkillBar;
    private readonly IBuffAnchorDetectService? _buffAnchor;
    private readonly IInteractionWindowDetectService? _interactionWindows;
    private readonly IInteractionControlCatalogService? _interactionControls;
    private readonly IBuffStateFusionService? _buffFusion;
    private readonly ISystemFeedbackRegionDetectService? _systemFeedback;
    private readonly ISystemFeedbackAsyncPipeline? _systemFeedbackAsync;
    private readonly MapNamePolicyService _mapNamePolicy;
    private readonly IMapNameSampleRecognitionService? _mapNameSamples;
    private readonly ConcurrentDictionary<string, LayoutCacheEntry> _layoutCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MapNameCacheEntry> _mapNameCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TargetNameCacheEntry> _targetNameCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RuntimePerceptionSnapshot> _snapshotCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RightSkillCacheEntry> _rightSkillCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BuffCacheEntry> _buffCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, InteractionWindowCacheEntry> _interactionWindowCache = new(StringComparer.Ordinal);
    private readonly QuestLogPanelTemporalGate _questLogPanelGate = new();

    public PerceptionHub(
        ILatestFrameService frames,
        ILayoutDetectService layout,
        ITargetHpSlotService targetHpSlot,
        ITargetIdentityService targetIdentity,
        ITargetJudgementService targetJudgement,
        IOpenTextRecognizer openText,
        IVisionV2SlotService slots,
        IMinimapTemporalFusionService? minimapFusion = null,
        IRightSkillBarDetectService? rightSkillBar = null,
        IBuffAnchorDetectService? buffAnchor = null,
        IInteractionWindowDetectService? interactionWindows = null,
        IInteractionControlCatalogService? interactionControls = null,
        IBuffStateFusionService? buffFusion = null,
        ISystemFeedbackRegionDetectService? systemFeedback = null,
        ISystemFeedbackAsyncPipeline? systemFeedbackAsync = null,
        MapNamePolicyService? mapNamePolicy = null,
        IMapNameSampleRecognitionService? mapNameSamples = null)
    {
        _frames = frames;
        _layout = layout;
        _targetHpSlot = targetHpSlot;
        _targetIdentity = targetIdentity;
        _targetJudgement = targetJudgement;
        _openText = openText;
        _slots = slots;
        _minimapFusion = minimapFusion ?? new MinimapTemporalFusionService();
        _rightSkillBar = rightSkillBar;
        _buffAnchor = buffAnchor;
        _interactionWindows = interactionWindows;
        _interactionControls = interactionControls;
        _buffFusion = buffFusion;
        _systemFeedback = systemFeedback;
        _systemFeedbackAsync = systemFeedbackAsync;
        _mapNamePolicy = mapNamePolicy ?? new MapNamePolicyService();
        _mapNameSamples = mapNameSamples;
    }

    public RuntimePerceptionSnapshot Read(
        BoundGameWindow window,
        GlobalConfig config,
        bool includeOpenText = false) =>
        Read(window, config, new PerceptionReadOptions(IncludeTargetOpenText: includeOpenText));

    public bool TryGetLatest(string windowId, out RuntimePerceptionSnapshot snapshot) =>
        _snapshotCache.TryGetValue(windowId, out snapshot!);

    public RuntimePerceptionSnapshot Read(
        BoundGameWindow window,
        GlobalConfig config,
        PerceptionReadOptions options)
    {
        var frame = _frames.GetLatest(window.Hwnd);
        if (frame is null)
            return EmptySnapshot("capture.empty", "Client-area capture is unavailable.");

        var frameAge = Math.Max(0, (DateTimeOffset.UtcNow - frame.CapturedAt).TotalMilliseconds);
        if (CanUseCachedSnapshot(window.Id, frame.Sequence, options, out var cached))
        {
            if (!options.RequestSystemFeedbackTextAsync && !options.PollSystemFeedbackTextAsync)
                return cached with { FrameAgeMilliseconds = frameAge };

            var feedback = ResolveSystemFeedback(window.Id, frame, options);
            return cached with
            {
                FrameAgeMilliseconds = frameAge,
                SystemFeedback = feedback,
                Degradations = UpdateSystemFeedbackDegradation(cached.Degradations, feedback)
            };
        }

        var layout = ResolveLayout(window.Id, frame);
        var hpRead = ReadTargetHpSlot(frame, layout);
        layout = hpRead.Layout;
        var hpSlot = hpRead.Result;
        var fastDeathCandidate = hpSlot?.Signal == TargetHpSlotSignal.DeadByHpBottomCandidate
                                 && HasReusableDeathSnapshot(window.Id, frame);
        var coreSlots = fastDeathCandidate
            ? ReuseCoreSlotsForDeath(window.Id)
            : _slots.AnalyzeCore(window.Id, frame.Sequence, frame, layout);
        var rightSkillBar = fastDeathCandidate
            ? ReuseRightSkillBarForDeath(window.Id, frame.Sequence)
            : ResolveRightSkillBar(window.Id, frame);
        if (!fastDeathCandidate
            && rightSkillBar.Available
            && rightSkillBar.GeometryGatePassed
            && rightSkillBar.FrameSequence == frame.Sequence)
        {
            var visualSlots = _slots.AnalyzeSkillSurface(
                frame.Sequence,
                frame,
                "right_skill_bar",
                rightSkillBar.Slots,
                rightSkillBar.Confidence);
            rightSkillBar = rightSkillBar with { VisualSlots = visualSlots };
            CacheRightSkillBar(window.Id, frame, rightSkillBar);
        }
        var buffBar = fastDeathCandidate
            ? ReuseBuffForDeath(window.Id, frame.Sequence)
            : ResolveBuff(window.Id, frame);
        if (!fastDeathCandidate && _buffFusion is not null)
        {
            buffBar = _buffFusion.Observe(
                window.Id,
                buffBar,
                VisionTriState.Unknown,
                VisionTriState.Unknown);
        }
        var interactionWindows = fastDeathCandidate
            ? ReuseInteractionWindowsForDeath(window.Id, frame.Sequence)
            : ResolveInteractionWindows(window.Id, frame, options.IncludeQuestLogPanel);
        var interactionControlLayouts = MapInteractionControls(interactionWindows);
        var systemFeedback = ResolveSystemFeedback(window.Id, frame, options);
        var skillBar = MergeSkillBar(coreSlots.SkillBar, rightSkillBar);
        var minimap = fastDeathCandidate
            ? coreSlots.Minimap
            : _minimapFusion.Observe(
                window.Id,
                frame.CapturedAt,
                EnrichMapName(window.Id, frame, coreSlots.Minimap),
                targetPanelVisible: layout.Best(LayoutClass.TargetPanel) is not null);
        var identity = _targetIdentity.Analyze(frame, layout, config);
        var openTextName = fastDeathCandidate
            ? ReuseTargetNameForDeath(window.Id)
            : options.IncludeTargetOpenText
            ? ReadTargetNameOpenText(window.Id, frame, layout)
            : OpenTextResult.Empty(OpenTextSlot.TargetName, "OpenText has not run.", _openText.EngineId);
        var target = _targetJudgement.Judge(
            window.Id,
            frame.Sequence,
            layout,
            hpSlot,
            openTextName,
            identity,
            config);

        var snapshot = new RuntimePerceptionSnapshot
        {
            FrameSequence = frame.Sequence,
            Width = frame.Width,
            Height = frame.Height,
            CapturedAt = frame.CapturedAt,
            FrameAgeMilliseconds = frameAge,
            CaptureMilliseconds = frame.CaptureMilliseconds,
            Layout = layout,
            Target = target,
            TargetHpSlot = hpSlot,
            TargetIdentity = identity,
            TargetNameOpenText = openTextName,
            SelfBars = coreSlots.SelfBars,
            SkillBar = skillBar,
            RightSkillBar = rightSkillBar,
            BuffBar = buffBar,
            InteractionWindows = interactionWindows,
            SystemFeedback = systemFeedback,
            InteractionControlLayouts = interactionControlLayouts,
            Minimap = minimap,
            Degradations = BuildDegradations(
                layout,
                hpSlot,
                identity,
                openTextName,
                coreSlots,
                rightSkillBar,
                buffBar,
                interactionWindows,
                systemFeedback)
        };

        if (frame.Sequence > 0)
            _snapshotCache[window.Id] = snapshot;
        return snapshot;
    }

    private VisionV2CoreSlots ReuseCoreSlotsForDeath(string windowId)
    {
        if (_snapshotCache.TryGetValue(windowId, out var previous))
        {
            return new VisionV2CoreSlots
            {
                SelfBars = previous.SelfBars,
                SkillBar = previous.SkillBar,
                Minimap = previous.Minimap,
                InferenceMilliseconds = 0
            };
        }

        return new VisionV2CoreSlots();
    }

    private bool HasReusableDeathSnapshot(string windowId, CaptureResult frame) =>
        _snapshotCache.TryGetValue(windowId, out var previous)
        && previous.FrameSequence > 0
        && previous.Width == frame.Width
        && previous.Height == frame.Height
        && previous.SkillBar.Source != PerceptionSourceKind.Unavailable;

    private RightSkillBarPerception ResolveRightSkillBar(string windowId, CaptureResult frame)
    {
        if (_rightSkillBar is null)
            return RightSkillBarPerception.Unavailable(frame.Sequence, "Right skill bar service is not registered.");
        var now = DateTimeOffset.UtcNow;
        if (_rightSkillCache.TryGetValue(windowId, out var cached)
            && cached.Width == frame.Width
            && cached.Height == frame.Height
            && now - cached.DetectedAt < RightSkillRefreshInterval)
        {
            return cached.Perception with
            {
                InferenceMilliseconds = 0,
                Message = $"cached right skill bar from frame {cached.Perception.FrameSequence}; {cached.Perception.Message}"
            };
        }

        var detected = _rightSkillBar.Detect(frame);
        CacheRightSkillBar(windowId, frame, detected);
        return detected;
    }

    private void CacheRightSkillBar(string windowId, CaptureResult frame, RightSkillBarPerception perception) =>
        _rightSkillCache[windowId] = new RightSkillCacheEntry(
            frame.Width,
            frame.Height,
            DateTimeOffset.UtcNow,
            perception);

    private BuffAnchorPerception ResolveBuff(string windowId, CaptureResult frame)
    {
        if (_buffAnchor is null)
            return BuffAnchorPerception.Unavailable(frame.Sequence, "Buff anchor service is not registered.");
        var now = DateTimeOffset.UtcNow;
        if (_buffCache.TryGetValue(windowId, out var cached)
            && cached.Width == frame.Width
            && cached.Height == frame.Height
            && now - cached.DetectedAt < BuffRefreshInterval)
        {
            return cached.Perception with
            {
                InferenceMilliseconds = 0,
                Message = $"cached Buff anchor from frame {cached.Perception.FrameSequence}; {cached.Perception.Message}"
            };
        }

        var detected = _buffAnchor.Detect(frame);
        _buffCache[windowId] = new BuffCacheEntry(frame.Width, frame.Height, now, detected);
        return detected;
    }

    private InteractionWindowPerception ResolveInteractionWindows(
        string windowId,
        CaptureResult frame,
        bool includeQuestLogPanel)
    {
        if (_interactionWindows is null)
            return InteractionWindowPerception.Unavailable(frame.Sequence, "Interaction window service is not registered.");
        var now = DateTimeOffset.UtcNow;
        if (_interactionWindowCache.TryGetValue(windowId, out var cached)
            && cached.Width == frame.Width
            && cached.Height == frame.Height
            && (!includeQuestLogPanel || cached.Perception.QuestLogPanelRequested)
            && now - cached.DetectedAt < InteractionWindowRefreshInterval)
        {
            return cached.Perception with
            {
                InferenceMilliseconds = 0,
                Message = $"cached interaction windows from frame {cached.Perception.FrameSequence}; {cached.Perception.Message}"
            };
        }

        var raw = _interactionWindows.Detect(frame, includeQuestLogPanel);
        var detected = includeQuestLogPanel
            ? _questLogPanelGate.Observe(windowId, frame.Width, frame.Height, raw)
            : raw;
        _interactionWindowCache[windowId] = new InteractionWindowCacheEntry(frame.Width, frame.Height, now, detected);
        return detected;
    }

    private IReadOnlyList<InteractionPanelControlLayout> MapInteractionControls(
        InteractionWindowPerception windows)
    {
        if (_interactionControls is null || !_interactionControls.IsAvailable || !windows.Available)
            return Array.Empty<InteractionPanelControlLayout>();
        return windows.Detections
            .Select(panel => _interactionControls.Map(windows.FrameSequence, panel))
            .ToArray();
    }

    private RightSkillBarPerception ReuseRightSkillBarForDeath(string windowId, long frameSequence) =>
        _snapshotCache.TryGetValue(windowId, out var previous)
            ? previous.RightSkillBar
            : RightSkillBarPerception.Unavailable(frameSequence, "Skipped right skill inference on target death fast path.");

    private BuffAnchorPerception ReuseBuffForDeath(string windowId, long frameSequence) =>
        _snapshotCache.TryGetValue(windowId, out var previous)
            ? previous.BuffBar
            : BuffAnchorPerception.Unavailable(frameSequence, "Skipped Buff inference on target death fast path.");

    private InteractionWindowPerception ReuseInteractionWindowsForDeath(string windowId, long frameSequence) =>
        _snapshotCache.TryGetValue(windowId, out var previous)
            ? previous.InteractionWindows
            : InteractionWindowPerception.Unavailable(frameSequence, "Skipped interaction-window inference on target death fast path.");

    private static SkillBarPerception MergeSkillBar(
        SkillBarPerception core,
        RightSkillBarPerception rightSkillBar)
    {
        if (rightSkillBar.VisualSlots.Count == 0)
            return core;
        return core with
        {
            VisualSlots = core.VisualSlots.Concat(rightSkillBar.VisualSlots).ToArray(),
            Message = string.IsNullOrWhiteSpace(core.Message)
                ? "right skill surface"
                : $"{core.Message}; right skill surface"
        };
    }

    private OpenTextResult ReuseTargetNameForDeath(string windowId)
    {
        if (_snapshotCache.TryGetValue(windowId, out var previous)
            && previous.TargetNameOpenText.HasText)
        {
            return previous.TargetNameOpenText;
        }

        return OpenTextResult.Empty(
            OpenTextSlot.TargetName,
            "Skipped non-critical OpenText on empty target HP.",
            "death-fast-path");
    }

    private bool CanUseCachedSnapshot(
        string windowId,
        long frameSequence,
        PerceptionReadOptions options,
        out RuntimePerceptionSnapshot snapshot)
    {
        snapshot = default!;
        if (frameSequence <= 0
            || !_snapshotCache.TryGetValue(windowId, out var cached)
            || cached.FrameSequence != frameSequence)
            return false;

        if (options.IncludeTargetOpenText && IsOpenTextNotRun(cached.TargetNameOpenText))
            return false;
        if (options.IncludeSystemFeedback && !cached.SystemFeedback.DetectionRequested)
            return false;
        if (options.IncludeSystemFeedbackText
            && (!cached.SystemFeedback.TextRecognitionRequested
                || !cached.SystemFeedback.TextRecognitionCompleted))
            return false;
        if (options.IncludeQuestLogPanel && !cached.InteractionWindows.QuestLogPanelRequested)
            return false;
        snapshot = cached;
        return true;
    }

    private SystemFeedbackPerception ResolveSystemFeedback(
        string windowId,
        CaptureResult frame,
        PerceptionReadOptions options)
    {
        if (options.RequestSystemFeedbackTextAsync || options.PollSystemFeedbackTextAsync)
        {
            if (_systemFeedbackAsync is null)
            {
                return SystemFeedbackPerception.Unavailable(
                    frame.Sequence,
                    "System feedback async pipeline is not registered.") with
                {
                    TextRecognitionRequested = true
                };
            }

            var read = options.RequestSystemFeedbackTextAsync
                ? _systemFeedbackAsync.Request(windowId, frame)
                : _systemFeedbackAsync.Read(windowId);
            if (read.Latest is null)
            {
                return read.InFlight
                    ? SystemFeedbackPerception.Pending(read.RequestedFrameSequence, read.Message)
                    : SystemFeedbackPerception.Unavailable(
                        read.RequestedFrameSequence,
                        read.Message) with
                    {
                        TextRecognitionRequested = true
                    };
            }

            return read.Latest with
            {
                RequestedFrameSequence = read.RequestedFrameSequence,
                AsyncRequestInFlight = read.InFlight,
                Message = $"{read.Latest.Message}; async requested={read.RequestedFrameSequence}, in_flight={read.InFlight}"
            };
        }

        if (!options.IncludeSystemFeedback)
            return SystemFeedbackPerception.NotRequested(frame.Sequence);

        return _systemFeedback?.Detect(frame, _openText, options.IncludeSystemFeedbackText)
               ?? SystemFeedbackPerception.Unavailable(
                   frame.Sequence,
                   "System feedback service is not registered.") with
               {
                   TextRecognitionRequested = options.IncludeSystemFeedbackText
               };
    }

    private static IReadOnlyList<PerceptionDegradation> UpdateSystemFeedbackDegradation(
        IReadOnlyList<PerceptionDegradation> current,
        SystemFeedbackPerception feedback)
    {
        var items = current
            .Where(item => !string.Equals(item.Code, "system_feedback.unavailable", StringComparison.Ordinal))
            .ToList();
        if (feedback.DetectionRequested && !feedback.Available && !feedback.AsyncRequestInFlight)
        {
            items.Add(new PerceptionDegradation(
                "system_feedback.unavailable",
                feedback.Message,
                PerceptionSourceKind.Unavailable));
        }
        return items;
    }

    private static bool IsOpenTextNotRun(OpenTextResult result) =>
        result.Source == PerceptionSourceKind.Unavailable
        && result.Message.Contains("has not run", StringComparison.OrdinalIgnoreCase);

    private LayoutSnapshot ResolveLayout(string windowId, CaptureResult frame)
    {
        var now = DateTimeOffset.UtcNow;
        if (_layoutCache.TryGetValue(windowId, out var cached)
            && cached.Width == frame.Width
            && cached.Height == frame.Height
            && now - cached.DetectedAt < LayoutRefreshInterval)
        {
            return cached.Layout with
            {
                CapturedAt = frame.CapturedAt,
                InferenceMilliseconds = 0,
                Message = "cached L1 V2 layout"
            };
        }

        var detected = _layout.Detect(frame);
        _layoutCache[windowId] = new LayoutCacheEntry(
            frame.Width,
            frame.Height,
            DateTimeOffset.UtcNow,
            detected);
        return detected;
    }

    private TargetHpRead ReadTargetHpSlot(CaptureResult frame, LayoutSnapshot layout)
    {
        var panel = layout.Best(LayoutClass.TargetPanel)?.Box;
        if (panel is not { IsEmpty: false })
            return ReadTargetHpFromSafeFallback(frame, layout);

        var crop = OpenTextCropUtil.Crop(frame, panel, pad: 0);
        return new TargetHpRead(
            layout,
            crop is null
                ? TargetHpSlotResult.Unavailable("target_panel crop failed")
                : _targetHpSlot.Analyze(crop));
    }

    private TargetHpRead ReadTargetHpFromSafeFallback(CaptureResult frame, LayoutSnapshot layout)
    {
        // The compact 800px client keeps the native status panels at the same
        // top-left pixel coordinates. This is a crop fallback for the existing
        // HP model, not an old color/OCR detector. A synthetic panel is emitted
        // only when the HP model itself confirms hp_bar_group.
        if (!TargetPanelSafeCropLocator.TryResolve(frame, out var panel))
            return new TargetHpRead(layout, null);
        var crop = OpenTextCropUtil.Crop(frame, panel, pad: 0);
        if (crop is null)
            return new TargetHpRead(layout, TargetHpSlotResult.Unavailable("800px target-panel safe crop failed"));

        var result = _targetHpSlot.Analyze(crop);
        if (!result.HpGroupFound)
            return new TargetHpRead(layout, result);

        var detections = layout.Detections
            .Append(new LayoutDetectionItem(
                LayoutClass.TargetPanel,
                panel,
                result.HpGroupConfidence,
                "target_panel_safe_crop_800"))
            .ToArray();
        var enhanced = layout with
        {
            Detections = detections,
            Message = string.IsNullOrWhiteSpace(layout.Message)
                ? "L1 target_panel missed; verified 800px HP crop used."
                : $"{layout.Message}; verified 800px HP crop used."
        };
        return new TargetHpRead(enhanced, result);
    }

    private OpenTextResult ReadTargetNameOpenText(string windowId, CaptureResult frame, LayoutSnapshot layout)
    {
        if (!_openText.IsAvailable)
            return OpenTextResult.Empty(OpenTextSlot.TargetName, "L2 OpenText unavailable.", _openText.EngineId);

        CaptureResult? crop = null;
        var nameBox = layout.Best(LayoutClass.TargetName)?.Box ?? VisionBox.Empty;
        if (nameBox is { IsEmpty: false })
            crop = OpenTextCropUtil.Crop(frame, nameBox, pad: 0);
        if (crop is null)
        {
            var panel = layout.Best(LayoutClass.TargetPanel)?.Box;
            if (panel is { IsEmpty: false })
            {
                // L1 target_name fallback contract, relative to target_panel.
                // It preserves the full glyph height and excludes the portrait/status area.
                var left = panel.X + (int)Math.Round(panel.Width * 0.1);
                var top = panel.Y + (int)Math.Round(panel.Height * 0.012195122);
                var right = panel.X + (int)Math.Round(panel.Width * 0.6307692308);
                var bottom = panel.Y + (int)Math.Round(panel.Height * 0.3414634146);
                nameBox = new VisionBox(
                    left,
                    top,
                    Math.Max(0, right - left),
                    Math.Max(0, bottom - top));
                crop = OpenTextCropUtil.Crop(frame, nameBox, pad: 0);
            }
        }
        if (crop is null)
        {
            _targetNameCache.TryRemove(windowId, out _);
            return OpenTextResult.Empty(OpenTextSlot.TargetName, "No target-name crop.", _openText.EngineId);
        }

        if (crop.Height < 20)
        {
            _targetNameCache.TryRemove(windowId, out _);
            return OpenTextResult.Empty(
                OpenTextSlot.TargetName,
                $"Target-name crop rejected: height {crop.Height}px is below the 20px safety minimum.",
                _openText.EngineId);
        }

        var now = DateTimeOffset.UtcNow;
        var visualHash = ComputeTargetNameFingerprint(crop);
        if (_targetNameCache.TryGetValue(windowId, out var cached)
            && cached.Box == nameBox
            && now - cached.ReadAt <= TargetNameReuseInterval
            && cached.VisualHash == visualHash)
        {
            return cached.Result;
        }

        var result = _openText.Recognize(crop, OpenTextSlot.TargetName);
        if (result.HasText)
            _targetNameCache[windowId] = new TargetNameCacheEntry(nameBox, visualHash, now, result);
        else
            _targetNameCache.TryRemove(windowId, out _);
        return result;
    }

    private static ulong ComputeTargetNameFingerprint(CaptureResult crop)
    {
        // Target names are short Chinese strings. A coarse 8x8 dHash can treat two
        // different names as the same image and reuse stale OCR for five seconds.
        // FNV-1a over the actual OCR crop keeps the cache for identical frames while
        // invalidating it as soon as any glyph pixel changes.
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        for (var index = 0; index + 3 < crop.Pixels.Length; index += 4)
        {
            hash ^= crop.Pixels[index];
            hash *= prime;
            hash ^= crop.Pixels[index + 1];
            hash *= prime;
            hash ^= crop.Pixels[index + 2];
            hash *= prime;
        }

        hash ^= (uint)crop.Width;
        hash *= prime;
        hash ^= (uint)crop.Height;
        return hash;
    }

    private MinimapPerception EnrichMapName(string windowId, CaptureResult frame, MinimapPerception minimap)
    {
        if (minimap.MapNameBox.IsEmpty)
            return minimap;

        var now = DateTimeOffset.UtcNow;
        if (_mapNameCache.TryGetValue(windowId, out var cached))
        {
            if (cached.Box != minimap.MapNameBox
                || cached.FrameWidth != frame.Width
                || cached.FrameHeight != frame.Height)
            {
                _mapNameCache.TryRemove(windowId, out _);
                _mapNamePolicy.Reset(windowId);
            }
            else if (cached.FrameSequence == frame.Sequence
                     || now - cached.ReadAt < MapNameRefreshInterval)
            {
                return ApplyMapNamePolicy(minimap, cached.Policy);
            }
        }

        var sampleCrop = OpenTextCropUtil.Crop(frame, minimap.MapNameBox, pad: 0);
        if (sampleCrop is null)
            return minimap;

        MapNameCandidateEvaluation? candidate = null;
        var sample = _mapNameSamples?.Match(sampleCrop);
        if (sample is { IsConfident: true })
        {
            var sampleCandidate = _mapNamePolicy.Evaluate(
                sample.MapName,
                sample.Score,
                "sample_match",
                sample.SampleId);
            if (sampleCandidate.CandidateAccepted)
                candidate = sampleCandidate;
        }

        if (candidate is null)
        {
            var openTextCrop = BuildMapNameOpenTextCrop(sampleCrop);
            var openText = _openText.IsAvailable && openTextCrop is not null
                ? _openText.Recognize(openTextCrop, OpenTextSlot.MapName)
                : OpenTextResult.Empty(OpenTextSlot.MapName, "OpenText is unavailable.", _openText.EngineId);
            candidate = _mapNamePolicy.Evaluate(
                openText.Text,
                openText.Confidence,
                openText.HasText ? "open_text" : "unknown");
        }

        var policy = _mapNamePolicy.Observe(windowId, frame.Sequence, candidate);
        _mapNameCache[windowId] = new MapNameCacheEntry(
            minimap.MapNameBox,
            frame.Width,
            frame.Height,
            frame.Sequence,
            now,
            policy);
        return ApplyMapNamePolicy(minimap, policy);
    }

    private static CaptureResult? BuildMapNameOpenTextCrop(CaptureResult exactMapNameCrop)
    {
        const int left = 42;
        var right = Math.Min(exactMapNameCrop.Width, 125);
        if (right <= left || exactMapNameCrop.Height <= 0)
            return null;

        return OpenTextCropUtil.Crop(
            exactMapNameCrop,
            VisionBox.FromXyxy(left, 0, right, exactMapNameCrop.Height),
            pad: 0);
    }

    private static MinimapPerception ApplyMapNamePolicy(
        MinimapPerception minimap,
        MapNamePolicyPerception policy)
    {
        var suffix = policy.Stable
            ? $"map={policy.StableMapName}, map_votes={policy.Votes}/{policy.HistoryCount}"
            : $"map_candidate={policy.NormalizedText}, map_policy={policy.StabilityReason}, map_votes={policy.Votes}/{policy.HistoryCount}";
        return minimap with
        {
            MapName = policy.Stable ? policy.StableMapName : "",
            MapNamePolicy = policy,
            Message = string.IsNullOrWhiteSpace(minimap.Message)
                ? suffix
                : $"{minimap.Message}, {suffix}"
        };
    }

    private static RuntimePerceptionSnapshot EmptySnapshot(string code, string message) => new()
    {
        CapturedAt = DateTimeOffset.Now,
        SelfBars = new SelfBarsPerception { Source = PerceptionSourceKind.Unavailable, Message = message },
        SkillBar = new SkillBarPerception { Source = PerceptionSourceKind.Unavailable, Message = message },
        Minimap = new MinimapPerception { Source = PerceptionSourceKind.Unavailable, Message = message },
        Degradations = new[]
        {
            new PerceptionDegradation(code, message, PerceptionSourceKind.Unavailable)
        }
    };

    private static IReadOnlyList<PerceptionDegradation> BuildDegradations(
        LayoutSnapshot layout,
        TargetHpSlotResult? hpSlot,
        TargetIdentityEvidence identity,
        OpenTextResult openText,
        VisionV2CoreSlots coreSlots,
        RightSkillBarPerception rightSkillBar,
        BuffAnchorPerception buffBar,
        InteractionWindowPerception interactionWindows,
        SystemFeedbackPerception systemFeedback)
    {
        var items = new List<PerceptionDegradation>(coreSlots.Degradations);
        if (layout.Source == PerceptionSourceKind.Unavailable)
        {
            items.Add(new PerceptionDegradation(
                "layout.unavailable",
                string.IsNullOrWhiteSpace(layout.Message) ? "L1 V2 layout unavailable." : layout.Message,
                PerceptionSourceKind.Unavailable));
        }
        if (hpSlot is { Signal: TargetHpSlotSignal.Unknown })
        {
            items.AddRange(hpSlot.DegradedReasons.Select(reason =>
                new PerceptionDegradation("target_hp_slot.ambiguous", reason)));
        }
        if (!identity.IsAvailable)
            items.Add(new PerceptionDegradation("target_identity.unavailable", identity.Message, PerceptionSourceKind.Unavailable));
        if (openText.Source == PerceptionSourceKind.Unavailable
            && !string.IsNullOrWhiteSpace(openText.Message)
            && !IsOpenTextNotRun(openText))
        {
            items.Add(new PerceptionDegradation("opentext.unavailable", openText.Message, PerceptionSourceKind.Unavailable));
        }
        if (!rightSkillBar.Available)
            items.Add(new PerceptionDegradation("right_skill_bar.unavailable", rightSkillBar.Message, PerceptionSourceKind.Unavailable));
        if (!buffBar.Available)
            items.Add(new PerceptionDegradation("buff_anchor.unavailable", buffBar.Message, PerceptionSourceKind.Unavailable));
        if (!interactionWindows.Available)
            items.Add(new PerceptionDegradation("interaction_windows.unavailable", interactionWindows.Message, PerceptionSourceKind.Unavailable));
        if (systemFeedback.DetectionRequested
            && !systemFeedback.Available
            && !systemFeedback.AsyncRequestInFlight)
            items.Add(new PerceptionDegradation("system_feedback.unavailable", systemFeedback.Message, PerceptionSourceKind.Unavailable));
        return items;
    }

    private sealed record LayoutCacheEntry(
        int Width,
        int Height,
        DateTimeOffset DetectedAt,
        LayoutSnapshot Layout);

    private sealed record MapNameCacheEntry(
        VisionBox Box,
        int FrameWidth,
        int FrameHeight,
        long FrameSequence,
        DateTimeOffset ReadAt,
        MapNamePolicyPerception Policy);

    private sealed record TargetNameCacheEntry(
        VisionBox Box,
        ulong VisualHash,
        DateTimeOffset ReadAt,
        OpenTextResult Result);

    private sealed record RightSkillCacheEntry(
        int Width,
        int Height,
        DateTimeOffset DetectedAt,
        RightSkillBarPerception Perception);

    private sealed record BuffCacheEntry(
        int Width,
        int Height,
        DateTimeOffset DetectedAt,
        BuffAnchorPerception Perception);

    private sealed record InteractionWindowCacheEntry(
        int Width,
        int Height,
        DateTimeOffset DetectedAt,
        InteractionWindowPerception Perception);

    private sealed record TargetHpRead(
        LayoutSnapshot Layout,
        TargetHpSlotResult? Result);
}
