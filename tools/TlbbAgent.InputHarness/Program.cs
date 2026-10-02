using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using TlbbAgent.Contracts;
using TlbbAgent.Core;
using TlbbAgent.Core.Perception;

namespace TlbbAgent.InputHarness;

internal static class Program
{
    private const int WmActivate = 0x0006;
    private const int WmSetFocus = 0x0007;
    private const int WmKillFocus = 0x0008;
    private const int WmActivateApp = 0x001C;
    private const int WmSetCursor = 0x0020;
    private const int WmMouseActivate = 0x0021;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;

    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();
        var systemForegroundBefore = GetForegroundWindow();
        using var target = new RecordingForm("target");
        using var foreground = new RecordingForm("foreground");
        _ = target.Handle;
        _ = foreground.Handle;
        Application.DoEvents();
        target.Clear();

        nint simulatedForeground = target.Handle;
        var inputCapabilities = new BackgroundInputCapabilityService();
        using var input = new InputService(
            new NoopLogService(),
            inputCapabilities,
            () => simulatedForeground);
        var foregroundKey = input.SendKey(target.Handle, "F8", InputDispatchMode.PostMessage);
        var foregroundReceipt = input.DispatchClickForConfirmation(
            target.Handle,
            20,
            20,
            InputDispatchMode.PostMessage);
        Application.DoEvents();

        var foregroundMessages = target.Messages.ToArray();
        var foregroundMessagesPostedWithoutActivation = foregroundKey
                             && foregroundReceipt.Delivered
                             && foregroundReceipt.RequiresOutcomeConfirmation
                             && foregroundReceipt.State == InputDispatchDeliveryState.WindowMessagePostedAwaitingConfirmation
                             && Contains(foregroundMessages, WmKeyDown, WmKeyUp, WmMouseMove, WmLButtonDown, WmLButtonUp)
                             && !ContainsAny(foregroundMessages, WmActivate, WmMouseActivate, WmSetCursor,
                                 WmActivateApp, WmSetFocus, WmKillFocus);

        target.Clear();
        simulatedForeground = foreground.Handle;

        var backgroundKey = input.SendKey(target.Handle, "F8", InputDispatchMode.PostMessage);
        var backgroundReceipt = input.DispatchClickForConfirmation(
            target.Handle,
            20,
            20,
            InputDispatchMode.PostMessage);
        Thread.Sleep(90);
        Application.DoEvents();
        var backgroundMessages = target.Messages.ToArray();
        var backgroundMessagesPostedWithoutActivation = backgroundKey
                                   && backgroundReceipt.Delivered
                                   && backgroundReceipt.RequiresOutcomeConfirmation
                                   && backgroundReceipt.State == InputDispatchDeliveryState.WindowMessagePostedAwaitingConfirmation
                                   && Contains(backgroundMessages, WmKeyDown, WmKeyUp,
                                       WmMouseMove, WmLButtonDown, WmLButtonUp)
                                   && !ContainsAny(backgroundMessages, WmActivate, WmMouseActivate, WmSetCursor,
                                       WmActivateApp, WmSetFocus, WmKillFocus);

        var rejectedReceipt = input.DispatchClickForConfirmation(
            target.Handle,
            -1,
            20,
            InputDispatchMode.PostMessage);
        var rejectedIsExplicit = !rejectedReceipt.Delivered
                                 && !rejectedReceipt.RequiresOutcomeConfirmation
                                 && rejectedReceipt.State == InputDispatchDeliveryState.Rejected;

        var confirmationWindow = new BoundGameWindow
        {
            Hwnd = target.Handle,
            Pid = Environment.ProcessId,
            ClientWidth = 160,
            ClientHeight = 100
        };
        var confirmationConfig = new GlobalConfig
        {
            InputDispatchMode = InputDispatchMode.PostMessage,
            YieldToManualInput = false
        };
        var confirmationDecision = new VisionActionDecision(
            true,
            VisionActionKind.Click,
            20,
            20,
            "input-harness-confirmation");
        var confirmationHub = new SequencePerceptionHub(
            Snapshot(1, ""),
            Snapshot(2, "panel_open"),
            Snapshot(3, "panel_open"));
        var confirmationExecutor = new VisionActionExecutor(input, new NoopLogService());
        var confirmationService = new VisualActionConfirmationService(
            confirmationExecutor,
            confirmationHub,
            new NoopLogService(),
            inputCapabilities);
        var confirmation = confirmationService.ExecuteAsync(
                confirmationWindow,
                confirmationConfig,
                new VisualActionConfirmationRequest(
                    confirmationDecision,
                    "panel_open",
                    snapshot => string.Equals(snapshot.Minimap.MapName, "panel_open", StringComparison.Ordinal),
                    RequiredConsecutiveFrames: 2,
                    TimeoutMilliseconds: 500,
                    PollMilliseconds: 20))
            .GetAwaiter()
            .GetResult();
        var freshFrameConfirmation = confirmation.Confirmed
                                     && confirmation.State == VisualActionConfirmationState.Confirmed
                                     && confirmation.BaselineFrameSequence == 1
                                     && confirmation.LastFrameSequence == 3
                                     && confirmation.FreshFramesObserved == 2
                                     && confirmation.ConsecutiveConfirmations == 2
                                     && confirmation.Receipt.State == InputDispatchDeliveryState.WindowMessagePostedAwaitingConfirmation;

        var timeoutHub = new SequencePerceptionHub(
            Snapshot(10, ""),
            Snapshot(11, ""),
            Snapshot(12, ""));
        var timeoutService = new VisualActionConfirmationService(
            confirmationExecutor,
            timeoutHub,
            new NoopLogService(),
            inputCapabilities);
        var timeout = timeoutService.ExecuteAsync(
                confirmationWindow,
                confirmationConfig,
                new VisualActionConfirmationRequest(
                    confirmationDecision,
                    "panel_open",
                    snapshot => string.Equals(snapshot.Minimap.MapName, "panel_open", StringComparison.Ordinal),
                    RequiredConsecutiveFrames: 2,
                    TimeoutMilliseconds: 120,
                    PollMilliseconds: 20))
            .GetAwaiter()
            .GetResult();
        var unconfirmedDeliveryIsNotSuccess = !timeout.Confirmed
                                               && timeout.State == VisualActionConfirmationState.TimedOut
                                               && timeout.Receipt.Delivered
                                               && timeout.FreshFramesObserved == 2
                                               && timeout.InputCapability is
                                               {
                                                   State: BackgroundInputCapabilityState.Unknown,
                                                   ConsecutiveUnconfirmedActions: 1
                                               };

        var secondTimeoutHub = new SequencePerceptionHub(
            Snapshot(20, ""),
            Snapshot(21, ""),
            Snapshot(22, ""));
        var secondTimeoutService = new VisualActionConfirmationService(
            confirmationExecutor,
            secondTimeoutHub,
            new NoopLogService(),
            inputCapabilities);
        var secondTimeout = secondTimeoutService.ExecuteAsync(
                confirmationWindow,
                confirmationConfig,
                new VisualActionConfirmationRequest(
                    confirmationDecision,
                    "panel_open",
                    snapshot => string.Equals(snapshot.Minimap.MapName, "panel_open", StringComparison.Ordinal),
                    RequiredConsecutiveFrames: 2,
                    TimeoutMilliseconds: 120,
                    PollMilliseconds: 20))
            .GetAwaiter()
            .GetResult();
        var repeatedMouseFailureFusesWindow = secondTimeout.State == VisualActionConfirmationState.TimedOut
                                              && secondTimeout.InputCapability is
                                              {
                                                  State: BackgroundInputCapabilityState.Unsupported,
                                                  ConsecutiveUnconfirmedActions: 2
                                              };

        var rejectedAfterFuse = secondTimeoutService.ExecuteAsync(
                confirmationWindow,
                confirmationConfig,
                new VisualActionConfirmationRequest(
                    confirmationDecision,
                    "panel_open",
                    snapshot => string.Equals(snapshot.Minimap.MapName, "panel_open", StringComparison.Ordinal)))
            .GetAwaiter()
            .GetResult();
        var fusedMouseStopsBlindRetry = rejectedAfterFuse.State == VisualActionConfirmationState.Rejected
                                        && !rejectedAfterFuse.Receipt.Delivered
                                        && rejectedAfterFuse.Reason.StartsWith(
                                            "background_input_unsupported:",
                                            StringComparison.Ordinal);
        var keyboardRemainsIndependent = input.SendKey(target.Handle, "F8", InputDispatchMode.PostMessage);
        var anotherWindowRemainsIndependent = inputCapabilities
            .GetStatus(foreground.Handle, BackgroundInputKind.Mouse)
            .CanDispatch;

        target.Clear();
        nint leaseForeground = foreground.Handle;
        var cursorX = 730;
        var cursorY = 410;
        var cursorWrites = new List<(int X, int Y)>();
        var leaseActivations = 0;
        var leaseRestores = 0;
        var leaseClicks = 0;
        var physicalMouseRecentlyActive = false;
        using var leaseInput = new InputService(
            new NoopLogService(),
            inputCapabilities,
            () => leaseForeground,
            new InputServicePlatformHooks
            {
                ActivateForegroundLease = (hwnd, _) =>
                {
                    leaseActivations++;
                    leaseForeground = hwnd;
                    return true;
                },
                RestoreForegroundWindow = hwnd =>
                {
                    leaseRestores++;
                    leaseForeground = hwnd;
                    return true;
                },
                GetPhysicalMouseActivity = () => physicalMouseRecentlyActive
                    ? (true, 0d, false)
                    : (false, double.PositiveInfinity, false),
                GetCursorPosition = () => (true, cursorX, cursorY),
                SetCursorPosition = (x, y) =>
                {
                    cursorX = x;
                    cursorY = y;
                    cursorWrites.Add((x, y));
                    return true;
                },
                SendMouseClick = _ =>
                {
                    leaseClicks++;
                    return true;
                }
            });
        var adaptiveKey = leaseInput.SendKey(target.Handle, "F8", InputDispatchMode.Adaptive);
        Application.DoEvents();
        var adaptiveKeyStaysBackground = adaptiveKey
                                         && Contains(target.Messages.ToArray(), WmKeyDown, WmKeyUp)
                                         && leaseActivations == 0;

        var adaptiveReceipt = leaseInput.DispatchClickForConfirmation(
            target.Handle,
            20,
            20,
            InputDispatchMode.Adaptive);
        var foregroundLeaseRestoresContext = adaptiveReceipt.Delivered
                                             && adaptiveReceipt.State == InputDispatchDeliveryState.ForegroundInputInjectedAwaitingConfirmation
                                             && leaseActivations == 1
                                             && leaseRestores == 1
                                             && leaseClicks == 1
                                             && leaseForeground == foreground.Handle
                                             && cursorX == 730
                                             && cursorY == 410
                                             && cursorWrites.Count >= 2;
        var activationsBeforeYield = leaseActivations;
        physicalMouseRecentlyActive = true;
        var foregroundLeaseYieldsToPhysicalMouse = !leaseInput.Click(
                                                       target.Handle,
                                                       20,
                                                       20,
                                                       InputDispatchMode.Adaptive)
                                                   && leaseActivations == activationsBeforeYield;
        physicalMouseRecentlyActive = false;

        var adaptiveConfig = new GlobalConfig
        {
            InputDispatchMode = InputDispatchMode.Adaptive,
            YieldToManualInput = false
        };
        var leaseConfirmationHub = new SequencePerceptionHub(
            Snapshot(30, ""),
            Snapshot(31, "panel_open"),
            Snapshot(32, "panel_open"));
        var leaseConfirmationService = new VisualActionConfirmationService(
            new VisionActionExecutor(leaseInput, new NoopLogService()),
            leaseConfirmationHub,
            new NoopLogService(),
            inputCapabilities);
        var leaseConfirmation = leaseConfirmationService.ExecuteAsync(
                confirmationWindow,
                adaptiveConfig,
                new VisualActionConfirmationRequest(
                    confirmationDecision,
                    "panel_open",
                    snapshot => string.Equals(snapshot.Minimap.MapName, "panel_open", StringComparison.Ordinal),
                    RequiredConsecutiveFrames: 2,
                    TimeoutMilliseconds: 500,
                    PollMilliseconds: 20))
            .GetAwaiter()
            .GetResult();
        var foregroundLeaseVisuallyConfirmed = leaseConfirmation.Confirmed
                                               && leaseConfirmation.Receipt.Mode == InputDispatchMode.Adaptive
                                               && leaseConfirmation.InputCapability is null;
        var foregroundLeaseDoesNotResetBackgroundFuse = inputCapabilities
            .GetStatus(target.Handle, BackgroundInputKind.Mouse)
            .State == BackgroundInputCapabilityState.Unsupported;

        var log = new RuntimeLogService();
        log.Log(new LogEntry
        {
            Level = LogLevel.Warn,
            Module = "输入诊断",
            Message = "PostMessage failed. Win32=5",
            Audience = LogAudience.User,
            UserSummary = "游戏窗口未接受本次按键，程序将在下一轮继续尝试。"
        });
        var explicitUserSummary = log.Entries.Count == 1
                                  && UserLogPrivacyPolicy.TryGetUserMessage(log.Entries[0], out var userMessage)
                                  && userMessage == "游戏窗口未接受本次按键，程序将在下一轮继续尝试。";
        var systemForegroundUnchanged = GetForegroundWindow() == systemForegroundBefore;

        input.ConfigureWindowInput(target.Handle, InputDispatchMode.PostMessage, true, 900);
        input.ConfigureWindowInput(foreground.Handle, InputDispatchMode.PostMessage, false, 0);
        var targetYieldPolicy = input.GetYieldStatus(target.Handle, InputDispatchMode.PostMessage);
        var foregroundYieldPolicy = input.GetYieldStatus(foreground.Handle, InputDispatchMode.PostMessage);
        var perWindowYieldPolicy = targetYieldPolicy.Enabled
                                   && targetYieldPolicy.YieldMs == 900
                                   && !foregroundYieldPolicy.Enabled;
        var firstTargetCooldown = input.TryConsumeGlobalCooldown(target.Handle, 60000);
        var repeatedTargetCooldown = input.TryConsumeGlobalCooldown(target.Handle, 60000);
        var independentWindowCooldown = input.TryConsumeGlobalCooldown(foreground.Handle, 60000);
        input.ClearWindowInputConfiguration(target.Handle);
        var clearedTargetCooldown = input.TryConsumeGlobalCooldown(target.Handle, 60000);
        var perWindowCooldown = firstTargetCooldown
                                && !repeatedTargetCooldown
                                && independentWindowCooldown
                                && clearedTargetCooldown;

        Console.WriteLine($"FOREGROUND_MESSAGES_POSTED_WITHOUT_ACTIVATION={foregroundMessagesPostedWithoutActivation}");
        Console.WriteLine($"BACKGROUND_MESSAGES_POSTED_WITHOUT_ACTIVATION={backgroundMessagesPostedWithoutActivation}");
        Console.WriteLine("GAME_OUTCOME_REQUIRES_FRESH_FRAME_CONFIRMATION=True");
        Console.WriteLine($"FOREGROUND_DELIVERY_STATE={foregroundReceipt.State}");
        Console.WriteLine($"BACKGROUND_DELIVERY_STATE={backgroundReceipt.State}");
        Console.WriteLine($"REJECTED_IS_EXPLICIT={rejectedIsExplicit}");
        Console.WriteLine($"FRESH_FRAME_CONFIRMATION={freshFrameConfirmation}");
        Console.WriteLine($"UNCONFIRMED_DELIVERY_IS_NOT_SUCCESS={unconfirmedDeliveryIsNotSuccess}");
        Console.WriteLine($"REPEATED_MOUSE_FAILURE_FUSES_WINDOW={repeatedMouseFailureFusesWindow}");
        Console.WriteLine($"FUSED_MOUSE_STOPS_BLIND_RETRY={fusedMouseStopsBlindRetry}");
        Console.WriteLine($"KEYBOARD_REMAINS_INDEPENDENT={keyboardRemainsIndependent}");
        Console.WriteLine($"ANOTHER_WINDOW_REMAINS_INDEPENDENT={anotherWindowRemainsIndependent}");
        Console.WriteLine($"ADAPTIVE_KEY_STAYS_BACKGROUND={adaptiveKeyStaysBackground}");
        Console.WriteLine($"FOREGROUND_LEASE_RESTORES_CONTEXT={foregroundLeaseRestoresContext}");
        Console.WriteLine($"FOREGROUND_LEASE_YIELDS_TO_PHYSICAL_MOUSE={foregroundLeaseYieldsToPhysicalMouse}");
        Console.WriteLine($"FOREGROUND_LEASE_VISUALLY_CONFIRMED={foregroundLeaseVisuallyConfirmed}");
        Console.WriteLine($"FOREGROUND_LEASE_DOES_NOT_RESET_BACKGROUND_FUSE={foregroundLeaseDoesNotResetBackgroundFuse}");
        Console.WriteLine($"SYSTEM_FOREGROUND_UNCHANGED={systemForegroundUnchanged}");
        Console.WriteLine($"EXPLICIT_USER_SUMMARY={explicitUserSummary}");
        Console.WriteLine($"PER_WINDOW_YIELD_POLICY={perWindowYieldPolicy}");
        Console.WriteLine($"PER_WINDOW_COOLDOWN={perWindowCooldown}");
        Console.WriteLine($"FOREGROUND_MESSAGES={string.Join(',', foregroundMessages.Select(Format))}");
        Console.WriteLine($"BACKGROUND_MESSAGES={string.Join(',', backgroundMessages.Select(Format))}");
        return foregroundMessagesPostedWithoutActivation
               && backgroundMessagesPostedWithoutActivation
               && rejectedIsExplicit
               && freshFrameConfirmation
               && unconfirmedDeliveryIsNotSuccess
               && repeatedMouseFailureFusesWindow
               && fusedMouseStopsBlindRetry
               && keyboardRemainsIndependent
               && anotherWindowRemainsIndependent
               && adaptiveKeyStaysBackground
               && foregroundLeaseRestoresContext
               && foregroundLeaseYieldsToPhysicalMouse
               && foregroundLeaseVisuallyConfirmed
               && foregroundLeaseDoesNotResetBackgroundFuse
               && systemForegroundUnchanged
               && explicitUserSummary
               && perWindowYieldPolicy
               && perWindowCooldown
            ? 0
            : 1;
    }

    private static bool Contains(IReadOnlyCollection<int> messages, params int[] expected) =>
        expected.All(messages.Contains);

    private static bool ContainsAny(IReadOnlyCollection<int> messages, params int[] expected) =>
        expected.Any(messages.Contains);

    private static string Format(int message) => $"0x{message:X4}";

    private static RuntimePerceptionSnapshot Snapshot(long sequence, string state) =>
        new()
        {
            FrameSequence = sequence,
            Width = 160,
            Height = 100,
            Minimap = new MinimapPerception { MapName = state }
        };

    private sealed class RecordingForm : Form
    {
        public RecordingForm(string name)
        {
            Text = name;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            Size = new Size(160, 100);
        }

        public ConcurrentQueue<int> Messages { get; } = new();

        public void Clear()
        {
            while (Messages.TryDequeue(out _)) { }
        }

        protected override void WndProc(ref Message m)
        {
            Messages.Enqueue(m.Msg);
            base.WndProc(ref m);
        }
    }

    private sealed class NoopLogService : ILogService
    {
        public event EventHandler<LogEntry>? Appended { add { } remove { } }
        public event EventHandler? Cleared { add { } remove { } }
        public IReadOnlyList<LogEntry> Entries => Array.Empty<LogEntry>();
        public void Log(LogEntry entry) { }
        public void Log(LogLevel level, string module, string message, string window = "-") { }
        public void Trace(string module, string message, string window = "-") { }
        public void Info(string module, string message, string window = "-") { }
        public void Warn(string module, string message, string window = "-") { }
        public void Error(string module, string message, string window = "-") { }
        public IDisposable Suppress(Func<LogEntry, bool> predicate) => NoopDisposable.Instance;
        public void Clear() { }
        public string ExportToText() => string.Empty;
    }

    private sealed class SequencePerceptionHub : IPerceptionHub
    {
        private readonly Queue<RuntimePerceptionSnapshot> _snapshots;
        private RuntimePerceptionSnapshot _last;

        public SequencePerceptionHub(params RuntimePerceptionSnapshot[] snapshots)
        {
            _snapshots = new Queue<RuntimePerceptionSnapshot>(snapshots);
            _last = snapshots.Last();
        }

        public RuntimePerceptionSnapshot Read(
            BoundGameWindow window,
            GlobalConfig config,
            bool includeOpenText = false) =>
            Read(window, config, PerceptionReadOptions.Fast);

        public RuntimePerceptionSnapshot Read(
            BoundGameWindow window,
            GlobalConfig config,
            PerceptionReadOptions options)
        {
            if (_snapshots.Count > 0)
                _last = _snapshots.Dequeue();
            return _last;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
