using TlbbAgent.Contracts;
using TlbbAgent.Core;
using TlbbAgent.Core.Automation;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

var failures = new List<string>();
var log = new RuntimeLogService();
var manager = new RoleSessionManager(log);

try
{
    var windowA = Window(0x101, 1001, "角色甲", ClientBitness.X64);
    var windowAReplacement = Window(0x111, 1011, "角色甲重连", ClientBitness.X64);
    var windowB = Window(0x202, 2002, "角色乙", ClientBitness.X86);
    var windowC = Window(0x303, 3003, "角色丙", ClientBitness.X64);
    var windowD = Window(0x404, 4004, "角色丁", ClientBitness.X86);
    var windowE = Window(0x505, 5005, "角色戊", ClientBitness.X64);

    manager.OpenOrUpdate("session-a", "profile-a", windowA);
    manager.OpenOrUpdate("session-b", "profile-b", windowB);
    manager.OpenOrUpdate("session-c", "profile-c", windowC);
    manager.OpenOrUpdate("session-d", "profile-d", windowD);
    Check(manager.Sessions.Count == RoleSessionLimits.MaxActiveSessions,
        "必须能够建立 1-4 个独立会话", failures);

    ExpectOpenFailure(
        () => manager.OpenOrUpdate("session-e", "profile-e", windowA),
        RoleSessionOpenFailure.WindowAlreadyOwned,
        "同一 HWND 不能属于两个会话",
        failures,
        expectedConflictingSessionId: "session-a");

    ExpectOpenFailure(
        () => manager.OpenOrUpdate("session-a", "profile-a-changed", windowA),
        RoleSessionOpenFailure.CharacterProfileConflict,
        "同一会话不能静默更换 CharacterProfileId",
        failures);

    ExpectOpenFailure(
        () => manager.OpenOrUpdate("session-e", "profile-e", windowE),
        RoleSessionOpenFailure.CapacityReached,
        "第五个有效会话必须被拒绝",
        failures);

    ExpectOpenFailure(
        () => manager.OpenOrUpdate("session-a", "profile-a", windowB),
        RoleSessionOpenFailure.WindowAlreadyOwned,
        "已有会话也不能重绑定到其他会话占用的 HWND",
        failures,
        expectedConflictingSessionId: "session-b");
    Check(manager.TryGetSnapshot("session-a", out var unchangedA)
          && unchangedA.WindowHandle == windowA.Hwnd.ToInt64(),
        "重绑定冲突不得改写原会话窗口", failures);

    manager.TryGetCancellationToken("session-b", out var tokenBeforeClosedRequest);
    Check(!manager.SetLifecycleState("session-b", RoleSessionLifecycleState.Closed),
        "SetLifecycleState 必须拒绝 Closed", failures);
    Check(!tokenBeforeClosedRequest.IsCancellationRequested
          && manager.TryGetSnapshot("session-b", out var afterClosedRequest)
          && afterClosedRequest.LifecycleState == RoleSessionLifecycleState.Ready,
        "拒绝 Closed 后会话必须保持可用", failures);

    var executions = 0;
    var saveA = new FeatureCommand<string>("save-a-1", "session-a", 0, "配置甲");
    var first = manager.Execute(saveA, advancesRevision: true, (_, payload) =>
    {
        Interlocked.Increment(ref executions);
        return payload;
    });
    var replay = manager.Execute(saveA, advancesRevision: true, (_, payload) =>
    {
        Interlocked.Increment(ref executions);
        return payload;
    });
    Check(first.Succeeded && first.CurrentRevision == 1,
        "会话 A 首次保存应成功并递增修订", failures);
    Check(replay.Succeeded && replay.IsReplay && executions == 1,
        "重复 CommandId 不得重复执行", failures);

    var stale = manager.Execute(
        new FeatureCommand<string>("save-a-stale", "session-a", 0, "旧配置"),
        advancesRevision: true,
        (_, payload) => payload);
    Check(stale.Status == SessionCommandStatus.RevisionConflict && stale.CurrentRevision == 1,
        "旧修订必须被拒绝", failures);

    var secondSession = manager.Execute(
        new FeatureCommand<string>("save-b-1", "session-b", 0, "配置乙"),
        advancesRevision: true,
        (_, payload) => payload);
    Check(secondSession.Succeeded && secondSession.CurrentRevision == 1,
        "会话 B 必须拥有独立修订", failures);

    manager.PublishFeatureSnapshot(new FeatureRuntimeSnapshot(
        "session-a", "hang.normal", "Running", "Engage", true, "战斗中", null, DateTimeOffset.MinValue));
    manager.PublishFeatureSnapshot(new FeatureRuntimeSnapshot(
        "session-b", "hang.normal", "Paused", "Idle", true, "已暂停", null, DateTimeOffset.MinValue));
    manager.SetLifecycleState("session-a", RoleSessionLifecycleState.Running);

    Check(manager.TryGetFeatureSnapshot("session-a", "hang.normal", out var featureA)
          && featureA.RunState == "Running", "会话 A 快照错误", failures);
    Check(manager.TryGetFeatureSnapshot("session-b", "hang.normal", out var featureB)
          && featureB.RunState == "Paused", "会话 B 快照错误", failures);

    manager.TryGetCancellationToken("session-a", out var originalTokenA);
    var reboundA = manager.OpenOrUpdate("session-a", "profile-a", windowAReplacement);
    manager.TryGetCancellationToken("session-a", out var replacementTokenA);
    Check(originalTokenA.IsCancellationRequested,
        "更换 HWND 必须取消并释放旧会话令牌", failures);
    Check(!replacementTokenA.IsCancellationRequested && !originalTokenA.Equals(replacementTokenA),
        "更换 HWND 必须创建新的会话令牌", failures);
    Check(reboundA.WindowHandle == windowAReplacement.Hwnd.ToInt64()
          && reboundA.LifecycleState == RoleSessionLifecycleState.Ready,
        "更换 HWND 后生命周期必须回到 Ready", failures);
    Check(!manager.TryGetFeatureSnapshot("session-a", "hang.normal", out _),
        "更换 HWND 必须清空旧功能快照", failures);
    Check(manager.TryGetFeatureSnapshot("session-b", "hang.normal", out var preservedFeatureB)
          && preservedFeatureB.RunState == "Paused",
        "更换会话 A 的 HWND 不得清空会话 B 快照", failures);

    var reusedCommandAfterRebind = manager.Execute(
        new FeatureCommand<string>("save-a-1", "session-a", 1, "重连后配置"),
        advancesRevision: true,
        (_, payload) =>
        {
            Interlocked.Increment(ref executions);
            return payload;
        });
    Check(reusedCommandAfterRebind.Succeeded
          && !reusedCommandAfterRebind.IsReplay
          && reusedCommandAfterRebind.CurrentRevision == 2
          && executions == 2,
        "更换 HWND 必须清空旧命令缓存，同时保留当前配置修订", failures);

    Check(manager.Close("session-a"), "关闭有效会话必须成功", failures);
    Check(replacementTokenA.IsCancellationRequested,
        "关闭会话必须取消并释放当前令牌", failures);
    Check(!manager.TryGetSnapshot("session-a", out _)
          && manager.Sessions.Count == RoleSessionLimits.MaxActiveSessions - 1,
        "关闭后必须移除会话并释放容量", failures);
    Check(!manager.Close("session-a"), "重复关闭同一会话必须安全返回 false", failures);
    ExpectOpenFailure(
        () => manager.OpenOrUpdate("session-a", "profile-a", windowAReplacement),
        RoleSessionOpenFailure.SessionClosed,
        "已关闭 SessionId 不能被复用",
        failures);

    manager.OpenOrUpdate("session-e", "profile-e", windowA);
    Check(manager.Sessions.Count == RoleSessionLimits.MaxActiveSessions,
        "关闭后空出的容量必须可供新会话使用", failures);

    using var commandEntered = new ManualResetEventSlim(false);
    using var allowSnapshotPublish = new ManualResetEventSlim(false);
    var commandTask = Task.Run(() => manager.Execute(
        new FeatureCommand<string>("session-b-lock-order", "session-b", 1, "并发快照"),
        advancesRevision: false,
        (context, payload) =>
        {
            commandEntered.Set();
            allowSnapshotPublish.Wait(context.CancellationToken);
            manager.PublishFeatureSnapshot(new FeatureRuntimeSnapshot(
                context.Session.RoleSessionId,
                "diagnostic.lock-order",
                "Running",
                "Publish",
                true,
                payload,
                null,
                DateTimeOffset.UtcNow));
            return payload;
        }));
    Check(commandEntered.Wait(1000), "并发命令未进入处理器", failures);
    var enumerationTask = Task.Run(() => manager.Sessions.Count);
    await Task.Delay(100);
    allowSnapshotPublish.Set();
    var concurrentWork = Task.WhenAll(commandTask, enumerationTask);
    Check(await Task.WhenAny(concurrentWork, Task.Delay(2000)) == concurrentWork,
        "命令回调发布快照与会话枚举发生锁顺序死锁", failures);
    if (concurrentWork.IsCompletedSuccessfully)
    {
        Check(commandTask.Result.Succeeded
              && manager.TryGetFeatureSnapshot("session-b", "diagnostic.lock-order", out _),
            "并发快照发布未完成", failures);
    }

    manager.TryGetCancellationToken("session-b", out var tokenB);
    manager.TryGetCancellationToken("session-c", out var tokenC);
    manager.TryGetCancellationToken("session-d", out var tokenD);
    manager.TryGetCancellationToken("session-e", out var tokenE);
    manager.Dispose();
    Check(tokenB.IsCancellationRequested
          && tokenC.IsCancellationRequested
          && tokenD.IsCancellationRequested
          && tokenE.IsCancellationRequested,
        "Dispose 必须取消并释放所有剩余会话", failures);
    Check(manager.Sessions.Count == 0, "Dispose 后不应残留有效会话", failures);
    ExpectException<ObjectDisposedException>(
        () => manager.OpenOrUpdate("session-after-dispose", "profile", windowE),
        "Dispose 后不能再打开会话",
        failures);
}
finally
{
    manager.Dispose();
}

RunWindowRegistryIsolation(failures);
RunConfigurationSnapshotIsolation(failures);
RunProductionRegistryActivation(failures);
if (args.Any(arg => string.Equals(arg, "--live-windows", StringComparison.OrdinalIgnoreCase)))
    RunLiveWindowRegistration(failures);

if (failures.Count == 0)
{
    Console.WriteLine(
        "PASS: 1-4 session capacity, HWND ownership, profile identity, replacement reset, " +
        "idempotency, revision guard, configuration snapshots, per-window main/auxiliary controllers, " +
        "independent start/pause/stop, stop-all, production DI, lock ordering, close and disposal");
    return 0;
}

foreach (var failure in failures)
    Console.Error.WriteLine("FAIL: " + failure);
return 1;

static BoundGameWindow Window(long hwnd, int pid, string title, ClientBitness bitness) => new()
{
    Hwnd = new nint(hwnd),
    Pid = pid,
    Title = title,
    ProcessName = "Game",
    ProcessPath = $"C:\\Game\\{pid}\\Game.exe",
    Bitness = bitness,
    ClientType = bitness == ClientBitness.X64 ? ClientType.Tlbb64 : ClientType.Tlbb32,
    ClientWidth = 1920,
    ClientHeight = 1080
};

static void RunWindowRegistryIsolation(ICollection<string> failures)
{
    var prototype = new FakeAutomationController("catalog");
    var factory = new FakeWindowAutomationControllerFactory();
    var registry = new AutomationRegistry(
        prototype,
        prototype,
        prototype,
        prototype,
        prototype,
        prototype,
        prototype,
        prototype,
        prototype,
        prototype,
        prototype,
        prototype,
        prototype,
        prototype,
        prototype,
        prototype,
        prototype,
        EmptyServiceProvider.Instance,
        factory);
    var registrations = 0;
    registry.ControllerRegistered += (_, _) => registrations++;

    var windowA = Window(0xA01, 7101, "window-a", ClientBitness.X64);
    var windowB = Window(0xB02, 7202, "window-b", ClientBitness.X86);
    var controllerA = registry.Prepare(windowA, "hang.normal");
    var controllerB = registry.Prepare(windowB, "hang.normal");

    Check(controllerA is not null && controllerB is not null,
        "Multi-window registry must create a controller for each window.", failures);
    Check(!ReferenceEquals(controllerA, controllerB),
        "The same mode must not share one controller across two windows.", failures);
    Check(registrations == 2,
        "Each window controller must be registered independently.", failures);
    Check(ReferenceEquals(registry.GetWindow(controllerA!), windowA)
          && ReferenceEquals(registry.GetWindow(controllerB!), windowB),
        "Controller ownership must resolve to the correct window.", failures);

    controllerA!.Start();
    controllerB!.Start();
    Check(controllerA.RunState == RunState.Running && controllerB.RunState == RunState.Running,
        "Two windows must be able to run the same mode concurrently.", failures);

    registry.StopWindow(windowA);
    Check(controllerA.RunState == RunState.Stopped,
        "Stopping window A must stop its controller.", failures);
    Check(controllerB.RunState == RunState.Running,
        "Stopping window A must not stop window B.", failures);

    controllerB.Pause();
    controllerA.Start();
    Check(controllerA.RunState == RunState.Running && controllerB.RunState == RunState.Paused,
        "Starting window A must not resume or retarget window B.", failures);

    registry.StopAll();
    Check(controllerA.RunState == RunState.Stopped && controllerB.RunState == RunState.Stopped,
        "Stop all must stop every window session.", failures);

    var shoutA = registry.Prepare(windowA, FeatureKeys.LifeShout);
    var shoutB = registry.Prepare(windowB, FeatureKeys.LifeShout);
    Check(shoutA is not null && shoutB is not null && !ReferenceEquals(shoutA, shoutB),
        "Auxiliary controllers must also be isolated per window.", failures);
    shoutA!.Start();
    shoutB!.Start();
    registry.StopWindow(windowA);
    Check(shoutA.RunState == RunState.Stopped && shoutB.RunState == RunState.Running,
        "Stopping window A must not stop window B auxiliary tasks.", failures);
    registry.StopAll();
}

static void RunConfigurationSnapshotIsolation(ICollection<string> failures)
{
    var source = new FakeGlobalConfigService();
    source.Current.AttackHotkey = "F1";
    source.Current.ManualInputYieldMs = 800;

    var proxyObject = DispatchProxy.Create(typeof(IGlobalConfigService), typeof(SessionConfigProxy));
    var proxy = (SessionConfigProxy)proxyObject;
    proxy.Initialize(typeof(IGlobalConfigService), source, proxyObject);
    proxy.Refresh();
    var sessionConfig = (IGlobalConfigService)proxyObject;

    source.Current.AttackHotkey = "F2";
    source.Current.ManualInputYieldMs = 1200;
    Check(sessionConfig.Current.AttackHotkey == "F1"
          && sessionConfig.Current.ManualInputYieldMs == 800,
        "An active window snapshot must not change when another profile becomes current.", failures);

    proxy.Refresh();
    Check(sessionConfig.Current.AttackHotkey == "F2"
          && sessionConfig.Current.ManualInputYieldMs == 1200,
        "An idle window snapshot must refresh from the selected profile before restart.", failures);

    sessionConfig.Current.AttackHotkey = "F3";
    Check(source.Current.AttackHotkey == "F2",
        "Mutating a session snapshot must not overwrite the shared profile service.", failures);
}

static void RunProductionRegistryActivation(ICollection<string> failures)
{
    var services = new ServiceCollection();
    services.AddTlbbCore();
    using var provider = services.BuildServiceProvider();
    var registry = provider.GetRequiredService<IAutomationRegistry>();
    var windowA = Window(0xC03, 7303, "production-a", ClientBitness.X64);
    var windowB = Window(0xD04, 7404, "production-b", ClientBitness.X86);

    var controllerA = registry.Prepare(windowA, "hang.normal");
    var controllerB = registry.Prepare(windowB, "hang.normal");
    Check(controllerA is INormalHangController && controllerB is INormalHangController,
        "Production DI must create the real normal-hang controller for each window.", failures);
    Check(!ReferenceEquals(controllerA, controllerB),
        "Production DI must not reuse one normal-hang controller across windows.", failures);
    Check(controllerA?.RunState == RunState.Idle && controllerB?.RunState == RunState.Idle
          && ReferenceEquals(registry.GetWindow(controllerA!), windowA)
          && ReferenceEquals(registry.GetWindow(controllerB!), windowB),
        "Production controllers must bind independently without starting automation.", failures);

    var shoutA = registry.Prepare(windowA, FeatureKeys.LifeShout);
    var shoutB = registry.Prepare(windowB, FeatureKeys.LifeShout);
    var loginA = registry.Prepare(windowA, FeatureKeys.UtilityAutoLogin);
    var loginB = registry.Prepare(windowB, FeatureKeys.UtilityAutoLogin);
    var reconnectA = registry.Prepare(windowA, FeatureKeys.UtilityAutoReconnect);
    var reconnectB = registry.Prepare(windowB, FeatureKeys.UtilityAutoReconnect);
    Check(shoutA is IShoutController && shoutB is IShoutController
          && loginA is IAutoLoginController && loginB is IAutoLoginController
          && reconnectA is IAutoReconnectController && reconnectB is IAutoReconnectController,
        "Production DI must create all auxiliary controllers per window.", failures);
    Check(!ReferenceEquals(shoutA, shoutB)
          && !ReferenceEquals(loginA, loginB)
          && !ReferenceEquals(reconnectA, reconnectB),
        "Production DI must not share auxiliary controller instances across windows.", failures);
}

static void RunLiveWindowRegistration(ICollection<string> failures)
{
    var services = new ServiceCollection();
    services.AddTlbbCore();
    using var provider = services.BuildServiceProvider();
    var binding = provider.GetRequiredService<IWindowBindingService>();
    var registry = provider.GetRequiredService<IAutomationRegistry>();
    var windows = binding.DiscoverGameWindows().Take(4).ToArray();
    Check(windows.Length >= 2,
        $"Live multi-window verification requires at least two Game windows; found {windows.Length}.", failures);
    if (windows.Length < 2)
        return;

    var controllers = windows
        .Select(window => (Window: window, Controller: registry.Prepare(window, FeatureKeys.HangNormal)))
        .ToArray();
    Check(controllers.All(item => item.Controller is INormalHangController),
        "Every live Game HWND must receive a normal-hang controller.", failures);
    Check(controllers.Select(item => item.Controller).Distinct(ReferenceEqualityComparer.Instance).Count() == controllers.Length,
        "Live Game HWNDs must not share controller instances.", failures);
    Check(controllers.All(item => item.Controller is not null
                                  && item.Controller.RunState is RunState.Idle or RunState.Ready
                                  && registry.GetWindow(item.Controller)?.Hwnd == item.Window.Hwnd),
        "Live controllers must remain idle and owned by the matching HWND without starting input.", failures);

    Console.WriteLine("LIVE_WINDOWS_READ_ONLY=True");
    foreach (var item in controllers)
    {
        Console.WriteLine(
            $"LIVE_WINDOW PID={item.Window.Pid} HWND=0x{item.Window.Hwnd.ToInt64():X} " +
            $"SIZE={item.Window.ClientWidth}x{item.Window.ClientHeight} STATE={item.Controller?.RunState}");
    }
}

static void ExpectOpenFailure(
    Action action,
    RoleSessionOpenFailure expectedFailure,
    string message,
    ICollection<string> failures,
    string? expectedConflictingSessionId = null)
{
    try
    {
        action();
        failures.Add(message + "（未拒绝）");
    }
    catch (RoleSessionOpenException ex)
    {
        if (ex.Failure != expectedFailure)
            failures.Add($"{message}（实际失败类型：{ex.Failure}）");
        if (expectedConflictingSessionId is not null
            && !string.Equals(ex.ConflictingRoleSessionId, expectedConflictingSessionId, StringComparison.Ordinal))
        {
            failures.Add($"{message}（冲突会话应为 {expectedConflictingSessionId}，实际为 {ex.ConflictingRoleSessionId ?? "-"}）");
        }
    }
    catch (Exception ex)
    {
        failures.Add($"{message}（异常类型错误：{ex.GetType().Name}）");
    }
}

static void ExpectException<TException>(
    Action action,
    string message,
    ICollection<string> failures)
    where TException : Exception
{
    try
    {
        action();
        failures.Add(message + "（未抛出异常）");
    }
    catch (TException)
    {
    }
    catch (Exception ex)
    {
        failures.Add($"{message}（异常类型错误：{ex.GetType().Name}）");
    }
}

static void Check(bool condition, string message, ICollection<string> failures)
{
    if (!condition)
        failures.Add(message);
}

sealed class FakeAutomationController :
    INormalHangController,
    IReturnPointController,
    IHorseBanditModeController,
    IDungeonController,
    INpcGrabController,
    IAutoRebelController,
    IAutoMonkController,
    ILifeCollectController,
    ITradeController,
    IMaterialCraftController,
    ISchoolTaskController,
    ITreasureMapController,
    IGuildContributionController,
    IUtilityController,
    IShoutController,
    IAutoLoginController,
    IAutoReconnectController
{
    public FakeAutomationController(string key)
    {
        Key = key;
    }

    public string Key { get; }
    public string Name => Key;
    public string Group => "test";
    public RunState RunState { get; private set; } = RunState.Idle;
    public string CurrentStateName => RunState.ToString();
    public BoundGameWindow? Target { get; private set; }
    public bool IsChannelRoutingReady => true;
    public string ChannelRoutingStatus => "ready";
    public event EventHandler? StateChanged;

    public void Attach(BoundGameWindow window)
    {
        Target = window;
        SetState(RunState.Ready);
    }

    public void Start() => SetState(RunState.Running);
    public void Pause() => SetState(RunState.Paused);
    public void Stop() => SetState(RunState.Stopped);

    private void SetState(RunState state)
    {
        RunState = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}

sealed class FakeWindowAutomationControllerFactory : IWindowAutomationControllerFactory
{
    public IAutomationController Create(string key, IServiceProvider services) =>
        new FakeAutomationController(key);
}

sealed class EmptyServiceProvider : IServiceProvider
{
    public static EmptyServiceProvider Instance { get; } = new();
    public object? GetService(Type serviceType) => null;
}

sealed class FakeGlobalConfigService : IGlobalConfigService
{
    public GlobalConfig Current { get; } = new();
    public event EventHandler? Changed;
    public void SetSchool(School school) => Current.School = school;
    public bool SchoolProvidesHeal(School school) => school == School.Emei;
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
    public void Save(string profileName) { }
    public bool Load(string profileName) => false;
    public void ExportToFile(string path) { }
    public bool ImportFromFile(string path) => false;
}
