namespace TlbbAgent.Contracts;

public enum InputDispatchDeliveryState
{
    Rejected = 0,
    WindowMessagePostedAwaitingConfirmation = 1,
    ForegroundInputInjectedAwaitingConfirmation = 2
}

public sealed record InputDispatchReceipt(
    InputDispatchDeliveryState State,
    InputDispatchMode Mode,
    InputMouseButton Button,
    int ClientX,
    int ClientY,
    string Reason)
{
    public bool Delivered => State != InputDispatchDeliveryState.Rejected;
    public bool RequiresOutcomeConfirmation => Delivered;

    public static InputDispatchReceipt Rejected(
        InputDispatchMode mode,
        InputMouseButton button,
        int clientX,
        int clientY,
        string reason) =>
        new(
            InputDispatchDeliveryState.Rejected,
            mode,
            button,
            clientX,
            clientY,
            reason);
}

public enum BackgroundInputKind
{
    Keyboard = 0,
    Mouse = 1
}

public enum BackgroundInputCapabilityState
{
    Unknown = 0,
    Available,
    Unsupported
}

public sealed record BackgroundInputCapabilityStatus(
    nint Hwnd,
    BackgroundInputKind Kind,
    BackgroundInputCapabilityState State,
    int ConsecutiveUnconfirmedActions,
    string Reason,
    DateTimeOffset UpdatedAt)
{
    public bool CanDispatch => State != BackgroundInputCapabilityState.Unsupported;
}

public interface IBackgroundInputCapabilityService
{
    BackgroundInputCapabilityStatus GetStatus(nint hwnd, BackgroundInputKind kind);
    bool CanDispatch(nint hwnd, BackgroundInputKind kind, out string reason);
    BackgroundInputCapabilityStatus RecordConfirmed(nint hwnd, BackgroundInputKind kind, string reason);
    BackgroundInputCapabilityStatus RecordUnconfirmed(nint hwnd, BackgroundInputKind kind, string reason);
    void ResetWindow(nint hwnd);
}

public enum VisualActionConfirmationState
{
    Rejected = 0,
    PreconditionFailed,
    DeliveredAwaitingConfirmation,
    Confirmed,
    TimedOut,
    ForegroundChanged,
    CaptureUnavailable,
    Cancelled
}

public sealed record VisualActionConfirmationRequest(
    VisionActionDecision Decision,
    string ExpectedOutcome,
    Func<RuntimePerceptionSnapshot, bool> IsExpectedOutcome,
    bool RequireStateTransition = true,
    int RequiredConsecutiveFrames = 2,
    int TimeoutMilliseconds = 1500,
    int PollMilliseconds = 40,
    PerceptionReadOptions? ReadOptions = null);

public sealed record VisualActionConfirmationResult(
    VisualActionConfirmationState State,
    InputDispatchReceipt Receipt,
    string ExpectedOutcome,
    string Reason,
    long BaselineFrameSequence,
    long LastFrameSequence,
    int FreshFramesObserved,
    int ConsecutiveConfirmations,
    nint ForegroundBefore,
    nint ForegroundAfter)
{
    public bool Confirmed => State == VisualActionConfirmationState.Confirmed;
    public BackgroundInputCapabilityStatus? InputCapability { get; init; }
}

public interface IVisualActionConfirmationService
{
    Task<VisualActionConfirmationResult> ExecuteAsync(
        BoundGameWindow window,
        GlobalConfig config,
        VisualActionConfirmationRequest request,
        CancellationToken cancellationToken = default);
}

public static class InputServiceDispatchExtensions
{
    public static InputDispatchReceipt DispatchClickForConfirmation(
        this IInputService input,
        nint hwnd,
        int clientX,
        int clientY,
        InputDispatchMode mode,
        InputMouseButton button = InputMouseButton.Left)
    {
        ArgumentNullException.ThrowIfNull(input);

        var delivered = input.Click(hwnd, clientX, clientY, mode, button);
        if (!delivered)
        {
            return InputDispatchReceipt.Rejected(
                mode,
                button,
                clientX,
                clientY,
                "native_dispatch_rejected_or_yielded");
        }

        var state = mode == InputDispatchMode.PostMessage
            ? InputDispatchDeliveryState.WindowMessagePostedAwaitingConfirmation
            : InputDispatchDeliveryState.ForegroundInputInjectedAwaitingConfirmation;
        var reason = mode == InputDispatchMode.PostMessage
            ? "window_messages_posted_business_outcome_unconfirmed"
            : "foreground_input_injected_business_outcome_unconfirmed";

        return new InputDispatchReceipt(
            state,
            mode,
            button,
            clientX,
            clientY,
            reason);
    }
}
