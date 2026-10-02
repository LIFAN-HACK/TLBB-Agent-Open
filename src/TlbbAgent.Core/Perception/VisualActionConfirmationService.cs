using System.Runtime.InteropServices;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public sealed class VisualActionConfirmationService : IVisualActionConfirmationService
{
    private const string Module = "VisualAction";
    private readonly IVisionActionExecutor _executor;
    private readonly IPerceptionHub _perception;
    private readonly ILogService _log;
    private readonly IBackgroundInputCapabilityService _inputCapabilities;

    public VisualActionConfirmationService(
        IVisionActionExecutor executor,
        IPerceptionHub perception,
        ILogService log,
        IBackgroundInputCapabilityService inputCapabilities)
    {
        _executor = executor;
        _perception = perception;
        _log = log;
        _inputCapabilities = inputCapabilities;
    }

    public async Task<VisualActionConfirmationResult> ExecuteAsync(
        BoundGameWindow window,
        GlobalConfig config,
        VisualActionConfirmationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.IsExpectedOutcome);

        var requiredFrames = Math.Clamp(request.RequiredConsecutiveFrames, 1, 5);
        var timeoutMs = Math.Clamp(request.TimeoutMilliseconds, 100, 10_000);
        var pollMs = Math.Clamp(request.PollMilliseconds, 20, 250);
        var readOptions = request.ReadOptions ?? PerceptionReadOptions.Fast;
        var foregroundBefore = GetForegroundWindow();

        if (!Enum.IsDefined(config.InputDispatchMode))
        {
            return Result(
                VisualActionConfirmationState.Rejected,
                RejectedReceipt(request, config, "unknown_input_dispatch_mode"),
                request,
                "unknown_input_dispatch_mode",
                0,
                0,
                0,
                0,
                foregroundBefore,
                foregroundBefore);
        }

        var usesBackgroundMouse = config.InputDispatchMode == InputDispatchMode.PostMessage;
        if (usesBackgroundMouse
            && !_inputCapabilities.CanDispatch(window.Hwnd, BackgroundInputKind.Mouse, out var capabilityReason))
        {
            var capability = _inputCapabilities.GetStatus(window.Hwnd, BackgroundInputKind.Mouse);
            return Result(
                VisualActionConfirmationState.Rejected,
                RejectedReceipt(request, config, capabilityReason),
                request,
                capabilityReason,
                0,
                0,
                0,
                0,
                foregroundBefore,
                foregroundBefore,
                capability);
        }

        var baseline = _perception.Read(window, config, readOptions);

        if (!IsUsableFrame(baseline))
        {
            return Result(
                VisualActionConfirmationState.CaptureUnavailable,
                RejectedReceipt(request, config, "baseline_frame_unavailable"),
                request,
                "baseline_frame_unavailable",
                baseline.FrameSequence,
                baseline.FrameSequence,
                0,
                0,
                foregroundBefore,
                GetForegroundWindow());
        }

        bool baselineMatches;
        try
        {
            baselineMatches = request.IsExpectedOutcome(baseline);
        }
        catch (Exception ex)
        {
            return Result(
                VisualActionConfirmationState.PreconditionFailed,
                RejectedReceipt(request, config, "baseline_predicate_failed"),
                request,
                $"baseline_predicate_failed:{ex.GetType().Name}",
                baseline.FrameSequence,
                baseline.FrameSequence,
                0,
                0,
                foregroundBefore,
                GetForegroundWindow());
        }

        if (request.RequireStateTransition && baselineMatches)
        {
            return Result(
                VisualActionConfirmationState.PreconditionFailed,
                RejectedReceipt(request, config, "expected_state_already_present"),
                request,
                "expected_state_already_present",
                baseline.FrameSequence,
                baseline.FrameSequence,
                0,
                0,
                foregroundBefore,
                GetForegroundWindow());
        }

        var receipt = _executor.DispatchClick(window, request.Decision, config);
        if (!receipt.Delivered)
        {
            return Result(
                VisualActionConfirmationState.Rejected,
                receipt,
                request,
                receipt.Reason,
                baseline.FrameSequence,
                baseline.FrameSequence,
                0,
                0,
                foregroundBefore,
                GetForegroundWindow());
        }

        var deadline = Environment.TickCount64 + timeoutMs;
        var lastSequence = baseline.FrameSequence;
        var freshFrames = 0;
        var consecutive = 0;

        try
        {
            while (Environment.TickCount64 < deadline)
            {
                await Task.Delay(pollMs, cancellationToken).ConfigureAwait(false);

                var foregroundNow = GetForegroundWindow();
                if (foregroundNow != foregroundBefore)
                {
                    _log.Warn(
                        Module,
                        $"Visual action stopped because foreground changed from 0x{foregroundBefore.ToInt64():X} to 0x{foregroundNow.ToInt64():X}.",
                        window.Id);
                    return Result(
                        VisualActionConfirmationState.ForegroundChanged,
                        receipt,
                        request,
                        "foreground_changed_during_confirmation",
                        baseline.FrameSequence,
                        lastSequence,
                        freshFrames,
                        consecutive,
                        foregroundBefore,
                        foregroundNow);
                }

                var snapshot = _perception.Read(window, config, readOptions);
                if (!IsUsableFrame(snapshot) || snapshot.FrameSequence <= lastSequence)
                    continue;

                lastSequence = snapshot.FrameSequence;
                freshFrames++;

                bool matches;
                try
                {
                    matches = request.IsExpectedOutcome(snapshot);
                }
                catch (Exception ex)
                {
                    _log.Warn(Module, $"Outcome predicate failed: {ex.GetType().Name}.", window.Id);
                    matches = false;
                }

                consecutive = matches ? consecutive + 1 : 0;
                if (consecutive < requiredFrames)
                    continue;

                _log.Info(
                    Module,
                    $"Confirmed '{request.ExpectedOutcome}' on {consecutive} fresh frames after {receipt.Mode} delivery.",
                    window.Id);
                var capability = usesBackgroundMouse
                    ? _inputCapabilities.RecordConfirmed(
                        window.Hwnd,
                        BackgroundInputKind.Mouse,
                        "expected_outcome_confirmed_on_fresh_frames")
                    : null;
                return Result(
                    VisualActionConfirmationState.Confirmed,
                    receipt,
                    request,
                    "expected_outcome_confirmed_on_fresh_frames",
                    baseline.FrameSequence,
                    lastSequence,
                    freshFrames,
                    consecutive,
                    foregroundBefore,
                    foregroundNow,
                    capability);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(
                VisualActionConfirmationState.Cancelled,
                receipt,
                request,
                "confirmation_cancelled",
                baseline.FrameSequence,
                lastSequence,
                freshFrames,
                consecutive,
                foregroundBefore,
                GetForegroundWindow());
        }

        var foregroundAfter = GetForegroundWindow();
        var capabilityAfter = usesBackgroundMouse
            ? freshFrames == 0
                ? _inputCapabilities.GetStatus(window.Hwnd, BackgroundInputKind.Mouse)
                : _inputCapabilities.RecordUnconfirmed(
                    window.Hwnd,
                    BackgroundInputKind.Mouse,
                    "expected_outcome_not_confirmed_before_timeout")
            : null;
        var capabilityText = capabilityAfter is null
            ? "not-applicable"
            : $"{capabilityAfter.State}, failures={capabilityAfter.ConsecutiveUnconfirmedActions}";
        _log.Warn(
            Module,
            $"Click delivery was not confirmed as '{request.ExpectedOutcome}' within {timeoutMs}ms; " +
            $"no success was reported. backgroundMouse={capabilityText}.",
            window.Id);
        return Result(
            freshFrames == 0
                ? VisualActionConfirmationState.CaptureUnavailable
                : VisualActionConfirmationState.TimedOut,
            receipt,
            request,
            freshFrames == 0
                ? "no_fresh_frame_after_delivery"
                : "expected_outcome_not_confirmed_before_timeout",
            baseline.FrameSequence,
            lastSequence,
            freshFrames,
            consecutive,
            foregroundBefore,
            foregroundAfter,
            capabilityAfter);
    }

    private static bool IsUsableFrame(RuntimePerceptionSnapshot snapshot) =>
        snapshot.FrameSequence > 0 && snapshot.Width > 0 && snapshot.Height > 0;

    private static InputDispatchReceipt RejectedReceipt(
        VisualActionConfirmationRequest request,
        GlobalConfig config,
        string reason) =>
        InputDispatchReceipt.Rejected(
            config.InputDispatchMode,
            InputMouseButton.Left,
            request.Decision.ClientX ?? -1,
            request.Decision.ClientY ?? -1,
            reason);

    private static VisualActionConfirmationResult Result(
        VisualActionConfirmationState state,
        InputDispatchReceipt receipt,
        VisualActionConfirmationRequest request,
        string reason,
        long baselineSequence,
        long lastSequence,
        int freshFrames,
        int consecutive,
        nint foregroundBefore,
        nint foregroundAfter,
        BackgroundInputCapabilityStatus? capability = null) =>
        new(
            state,
            receipt,
            request.ExpectedOutcome,
            reason,
            baselineSequence,
            lastSequence,
            freshFrames,
            consecutive,
            foregroundBefore,
            foregroundAfter)
        {
            InputCapability = capability
        };

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
