using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public sealed class InteractionReadOnlyPlanService : IInteractionReadOnlyPlanService
{
    private const double MinimumPanelConfidence = 0.75;
    private static readonly HashSet<string> PublishedPanelClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "bag_panel",
        "bank_panel",
        "material_panel",
        "monthly_card_panel",
        "shop_panel"
    };
    private static readonly HashSet<string> CaptureRequiredPanelClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "npc_dialog_panel",
        "quest_log_panel",
        "death_dialog",
        "confirm_dialog",
        "auto_nav_panel"
    };

    private readonly IInteractionWindowDetectService _windows;
    private readonly IInteractionControlCatalogService _controls;

    public InteractionReadOnlyPlanService(
        IInteractionWindowDetectService windows,
        IInteractionControlCatalogService controls)
    {
        _windows = windows;
        _controls = controls;
    }

    public string FeatureFlag => "TLBB_AGENT_INTERACTION_ACTIONS_ENABLED";

    public bool RuntimeActionsEnabled =>
        ReadBooleanEnvironmentVariable(FeatureFlag) && _controls.RuntimeAuthorized;

    public InteractionReadOnlyPlan Build(
        string windowId,
        CaptureResult frame,
        string panelClass)
    {
        panelClass = panelClass?.Trim() ?? "";
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Pixels.Length < frame.Width * frame.Height * 4)
            return Unavailable(windowId, frame, panelClass, InteractionReadOnlyPlanStatus.Unavailable, "当前帧无效。", false);

        if (CaptureRequiredPanelClasses.Contains(panelClass))
        {
            return Unavailable(
                windowId,
                frame,
                panelClass,
                InteractionReadOnlyPlanStatus.CaptureRequired,
                $"{panelClass} 尚未达到发布门槛；需要补充独立会话后训练，当前不生成点击候选。",
                false);
        }

        if (!PublishedPanelClasses.Contains(panelClass))
        {
            return Unavailable(
                windowId,
                frame,
                panelClass,
                InteractionReadOnlyPlanStatus.Unavailable,
                $"不支持的交互面板类：{panelClass}。",
                false);
        }

        var perception = _windows.Detect(frame);
        var panel = perception.Detections
            .Where(item => string.Equals(item.ClassName, panelClass, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Confidence)
            .FirstOrDefault();
        if (!perception.Available || panel is null || panel.Confidence < MinimumPanelConfidence)
        {
            return Unavailable(
                windowId,
                frame,
                panelClass,
                InteractionReadOnlyPlanStatus.PanelNotFound,
                $"当前帧未可靠检测到 {panelClass}。",
                false);
        }

        if (_controls is not { IsAvailable: true })
        {
            return Unavailable(
                windowId,
                frame,
                panelClass,
                InteractionReadOnlyPlanStatus.Unavailable,
                $"交互控件资产不可用：{_controls.Message}",
                false);
        }

        var layout = _controls.Map(frame.Sequence, panel);
        if (layout.FrameSequence != frame.Sequence
            || !string.Equals(layout.PanelClass, panelClass, StringComparison.OrdinalIgnoreCase)
            || layout.PanelBox.IsEmpty
            || !InsideFrame(layout.PanelBox, frame.Width, frame.Height)
            || !HasValidGeometry(layout))
        {
            return Unavailable(
                windowId,
                frame,
                panelClass,
                InteractionReadOnlyPlanStatus.InvalidGeometry,
                $"{panelClass} 的控件映射无效或帧已过期。",
                false);
        }

        var featureFlagEnabled = ReadBooleanEnvironmentVariable(FeatureFlag);
        var runtimeAuthorized = featureFlagEnabled && layout.RuntimeAuthorized;
        var actions = new List<InteractionReadOnlyActionCandidate>();
        actions.AddRange(layout.Controls.Select(control => new InteractionReadOnlyActionCandidate
        {
            WindowId = windowId,
            FrameSequence = frame.Sequence,
            PanelClass = panelClass,
            PanelBoxPx = layout.PanelBox,
            ControlId = control.ControlId,
            SafeClickBoxPx = control.SafeClickBox,
            ClickPointX = control.ClickX,
            ClickPointY = control.ClickY,
            IdentityConfidence = panel.Confidence,
            PolicyDecision = PolicyDecision(featureFlagEnabled, layout.RuntimeAuthorized, control.RuntimeDefault),
            ExpectedPostconditions = control.ExpectedPostconditions,
            RuntimeAuthorized = runtimeAuthorized && ControlPolicyAllowsRuntime(control.RuntimeDefault)
        }));
        actions.AddRange(layout.Regions.SelectMany(region => region.Cells.Select(cell =>
            new InteractionReadOnlyActionCandidate
            {
                WindowId = windowId,
                FrameSequence = frame.Sequence,
                PanelClass = panelClass,
                PanelBoxPx = layout.PanelBox,
                SlotId = $"{region.RegionId}:{cell.Row}:{cell.Column}",
                SafeClickBoxPx = cell.SafeClickBox,
                ClickPointX = cell.ClickX,
                ClickPointY = cell.ClickY,
                IdentityConfidence = 0,
                PolicyDecision = cell.IsDisabled
                    ? "blocked_disabled_cell"
                    : "blocked_requires_slot_identity_and_business_policy",
                RuntimeAuthorized = false
            })));

        return new InteractionReadOnlyPlan
        {
            Available = true,
            Status = InteractionReadOnlyPlanStatus.ObservationOnly,
            WindowId = windowId,
            FrameSequence = frame.Sequence,
            PanelClass = panelClass,
            PanelBoxPx = layout.PanelBox,
            PanelConfidence = panel.Confidence,
            Actions = actions,
            Regions = layout.Regions,
            FeatureFlag = FeatureFlag,
            FeatureFlagEnabled = featureFlagEnabled,
            RuntimeAuthorized = runtimeAuthorized,
            AssetVersion = layout.AssetVersion,
            Message = runtimeAuthorized
                ? "只读计划已生成；任何动作仍需业务策略、稳定帧和新帧后置验证。"
                : "只读计划已生成；交互动作 feature flag 或控件资产授权未开启，不会执行输入。"
        };
    }

    private InteractionReadOnlyPlan Unavailable(
        string windowId,
        CaptureResult frame,
        string panelClass,
        InteractionReadOnlyPlanStatus status,
        string message,
        bool available) =>
        new()
        {
            Available = available,
            Status = status,
            WindowId = windowId,
            FrameSequence = frame.Sequence,
            PanelClass = panelClass,
            FeatureFlag = FeatureFlag,
            FeatureFlagEnabled = ReadBooleanEnvironmentVariable(FeatureFlag),
            RuntimeAuthorized = false,
            AssetVersion = _controls.AssetVersion,
            Message = message
        };

    private static bool HasValidGeometry(InteractionPanelControlLayout layout)
    {
        foreach (var control in layout.Controls)
        {
            if (control.SafeClickBox.IsEmpty
                || !Contains(layout.PanelBox, control.SafeClickBox)
                || !control.ClickX.HasValue
                || !control.ClickY.HasValue
                || !ContainsPoint(control.SafeClickBox, control.ClickX.Value, control.ClickY.Value))
                return false;
        }

        foreach (var region in layout.Regions)
        {
            if (region.Box.IsEmpty || !Contains(layout.PanelBox, region.Box))
                return false;
            foreach (var cell in region.Cells)
            {
                if (cell.CellBox.IsEmpty || !Contains(region.Box, cell.CellBox))
                    return false;
                if (cell.IsDisabled)
                    continue;
                if (cell.SafeClickBox.IsEmpty
                    || !Contains(cell.CellBox, cell.SafeClickBox)
                    || !cell.ClickX.HasValue
                    || !cell.ClickY.HasValue
                    || !ContainsPoint(cell.SafeClickBox, cell.ClickX.Value, cell.ClickY.Value))
                    return false;
            }
        }

        return true;
    }

    private static string PolicyDecision(
        bool featureFlagEnabled,
        bool catalogAuthorized,
        string runtimeDefault)
    {
        if (!featureFlagEnabled)
            return "blocked_feature_flag_disabled";
        if (!catalogAuthorized)
            return "blocked_catalog_not_authorized";
        if (!ControlPolicyAllowsRuntime(runtimeDefault))
            return "blocked_control_policy";
        return "candidate_requires_business_policy";
    }

    private static bool ControlPolicyAllowsRuntime(string runtimeDefault) =>
        !runtimeDefault.Contains("disabled", StringComparison.OrdinalIgnoreCase)
        && !runtimeDefault.Contains("observation", StringComparison.OrdinalIgnoreCase);

    private static bool InsideFrame(VisionBox box, int width, int height) =>
        box.X >= 0 && box.Y >= 0 && box.Right <= width && box.Bottom <= height;

    private static bool Contains(VisionBox outer, VisionBox inner) =>
        inner.X >= outer.X
        && inner.Y >= outer.Y
        && inner.Right <= outer.Right
        && inner.Bottom <= outer.Bottom;

    private static bool ContainsPoint(VisionBox box, int x, int y) =>
        x >= box.X && y >= box.Y && x < box.Right && y < box.Bottom;

    private static bool ReadBooleanEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
    }
}
