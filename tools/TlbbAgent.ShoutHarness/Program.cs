using System.Collections.Concurrent;
using TlbbAgent.Contracts;
using TlbbAgent.Core;

var failures = new List<string>();
var profiles = new MemoryProfileService();
var log = new RuntimeLogService();
var input = new RecordingInputService();
var channels = new RecordingChannelSelector();

using (var sessions = new RoleSessionManager(log))
using (var runtime = new ShoutQueueRuntimeService(sessions, profiles, input, channels, log))
{
    sessions.OpenOrUpdate("session-a", "profile-a", Window(0x101, 1001, "角色甲"));
    sessions.OpenOrUpdate("session-b", "profile-b", Window(0x202, 2002, "角色乙"));

    var worldConfig = Config(
        Message("a-world", 1, ShoutChannel.World, "A 世界"));
    var bConfig = Config(
        Message("b-1", 1, ShoutChannel.Nearby, "B 第一条"),
        Message("b-2", 2, ShoutChannel.Team, "B 第二条"));

    var applyA = runtime.ApplyConfig(new FeatureCommand<ShoutConfig>("a-config-1", "session-a", 0, worldConfig));
    var applyB = runtime.ApplyConfig(new FeatureCommand<ShoutConfig>("b-config-1", "session-b", 0, bConfig));
    Check(applyA.Succeeded && applyA.CurrentRevision == 1, "会话 A 配置应写入修订 1", failures);
    Check(applyB.Succeeded && applyB.CurrentRevision == 1, "会话 B 配置应独立写入修订 1", failures);

    var duplicateApply = runtime.ApplyConfig(new FeatureCommand<ShoutConfig>("a-config-1", "session-a", 0, worldConfig));
    Check(duplicateApply.IsReplay && duplicateApply.CurrentRevision == 1,
        "重复配置命令不得再次递增修订", failures);

    runtime.Start(new FeatureCommand<ShoutQueueStartRequest>(
        "b-start-1", "session-b", 1, new ShoutQueueStartRequest(InputDispatchMode.PostMessage)));

    await WaitUntilAsync(() => input.TextsFor(0x202).Count >= 1, 2000);
    Check(input.TextsFor(0x202).FirstOrDefault() == "B 第一条", "会话 B 未按顺序发送第一条", failures);
    Check(channels.Selections.Any(x => x.Hwnd == 0x202 && x.Channel == ShoutChannel.Nearby),
        "会话 B 未选择附近频道", failures);

    runtime.Pause(new FeatureCommand<EmptyFeaturePayload>(
        "b-pause-1", "session-b", 1, new EmptyFeaturePayload()));
    var pausedCount = input.TextsFor(0x202).Count;

    runtime.Start(new FeatureCommand<ShoutQueueStartRequest>(
        "a-start-1", "session-a", 1, new ShoutQueueStartRequest(InputDispatchMode.PostMessage)));
    await WaitUntilAsync(() => input.TextsFor(0x101).Count >= 1, 2000);
    Check(input.TextsFor(0x101).SequenceEqual(new[] { "A 世界" }), "会话 A 文本串线", failures);
    Check(channels.Selections.Any(x => x.Hwnd == 0x101 && x.Channel == ShoutChannel.World),
        "会话 A 未选择世界频道", failures);

    await WaitUntilAsync(
        () => runtime.TryGetSnapshot("session-a", out var snapshot)
              && snapshot.Feature.RunState == ShoutQueueRunState.WaitingCooldown.ToString(),
        3500);
    Check(runtime.TryGetSnapshot("session-a", out var worldSnapshot)
          && worldSnapshot.RemainingWorldCooldownSeconds >= 175,
        "世界频道 180 秒冷却未建立", failures);
    Check(runtime.TryGetSnapshot("session-b", out var bSnapshot)
          && bSnapshot.RemainingWorldCooldownSeconds == 0,
        "会话 A 的世界冷却不得污染会话 B", failures);

    await Task.Delay(1200);
    Check(input.TextsFor(0x202).Count == pausedCount, "暂停后仍然发送了输入", failures);

    var reordered = Config(
        Message("b-2", 1, ShoutChannel.Team, "B 第二条"),
        Message("b-1", 2, ShoutChannel.Nearby, "B 第一条"));
    var reorderResult = runtime.ApplyConfig(new FeatureCommand<ShoutConfig>(
        "b-config-2", "session-b", 1, reordered));
    Check(reorderResult.Succeeded && reorderResult.CurrentRevision == 2, "运行时重排配置失败", failures);
    runtime.Resume(new FeatureCommand<EmptyFeaturePayload>(
        "b-resume-1", "session-b", 2, new EmptyFeaturePayload()));

    await WaitUntilAsync(() => input.TextsFor(0x202).Count > pausedCount, 3500);
    var reorderedSequence = input.TextsFor(0x202);
    Check(reorderedSequence.ElementAtOrDefault(pausedCount) == "B 第二条",
        $"重排后重复了刚发送条目，实际序列：{string.Join(" -> ", reorderedSequence)}", failures);

    runtime.Stop(new FeatureCommand<EmptyFeaturePayload>(
        "a-stop-1", "session-a", 1, new EmptyFeaturePayload()));
    runtime.Stop(new FeatureCommand<EmptyFeaturePayload>(
        "b-stop-1", "session-b", 2, new EmptyFeaturePayload()));
}

var sentBeforeRestart = input.TextsFor(0x101).Count;
using (var sessions = new RoleSessionManager(log))
using (var runtime = new ShoutQueueRuntimeService(sessions, profiles, input, channels, log))
{
    sessions.OpenOrUpdate("session-a-restart", "profile-a", Window(0x303, 3003, "角色甲重连"));
    var start = runtime.Start(new FeatureCommand<ShoutQueueStartRequest>(
        "a-start-after-restart",
        "session-a-restart",
        0,
        new ShoutQueueStartRequest(InputDispatchMode.PostMessage)));
    Check(start.Succeeded && start.Value?.Accepted == true, "重启后未加载角色喊话配置", failures);

    await WaitUntilAsync(
        () => runtime.TryGetSnapshot("session-a-restart", out var snapshot)
              && snapshot.Feature.RunState == ShoutQueueRunState.WaitingCooldown.ToString(),
        2500);
    Check(runtime.TryGetSnapshot("session-a-restart", out var restored)
          && restored.RemainingWorldCooldownSeconds >= 170,
        "世界频道冷却未跨运行时恢复", failures);
    Check(input.TextsFor(0x303).Count == 0 && input.TextsFor(0x101).Count == sentBeforeRestart,
        "重启后绕过世界频道冷却重复发送", failures);

    runtime.Stop(new FeatureCommand<EmptyFeaturePayload>(
        "a-stop-after-restart",
        "session-a-restart",
        0,
        new EmptyFeaturePayload()));
}

if (failures.Count == 0)
{
    Console.WriteLine("PASS: per-session queue, ordering, pause, revision, idempotency, persistent world cooldown");
    return 0;
}

foreach (var failure in failures)
    Console.Error.WriteLine("FAIL: " + failure);
return 1;

static ShoutConfig Config(params ShoutMessage[] messages) => new()
{
    Enabled = true,
    OpenChatHotkey = "Enter",
    CommitHotkey = "Enter",
    IntervalMs = 1000,
    MessageDelayMs = 0,
    CommitDelayMs = 0,
    Messages = messages.ToList()
};

static ShoutMessage Message(string id, int order, ShoutChannel channel, string text) => new()
{
    Id = id,
    Order = order,
    Enabled = true,
    Channel = channel,
    Text = text
};

static BoundGameWindow Window(long hwnd, int pid, string title) => new()
{
    Hwnd = new nint(hwnd),
    Pid = pid,
    Title = title,
    ProcessName = "Game",
    ProcessPath = $"C:\\Game\\{pid}\\Game.exe",
    Bitness = ClientBitness.X64,
    ClientType = ClientType.Tlbb64,
    ClientWidth = 1920,
    ClientHeight = 1080
};

static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs)
{
    var deadline = Environment.TickCount64 + timeoutMs;
    while (!condition() && Environment.TickCount64 < deadline)
        await Task.Delay(25);
}

static void Check(bool condition, string message, ICollection<string> failures)
{
    if (!condition)
        failures.Add(message);
}

sealed class MemoryProfileService : IProfileService
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
    public string? CurrentProfileName { get; private set; }
    public IReadOnlyList<string> ListProfileNames() => _values.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    public void Save(string name, string jsonPayload) { _values[name] = jsonPayload; CurrentProfileName = name; }
    public string? Load(string name) { CurrentProfileName = name; return _values.TryGetValue(name, out var value) ? value : null; }
    public void Delete(string name) { _values.TryRemove(name, out _); }
}

sealed class RecordingChannelSelector : IShoutChannelSelector
{
    public bool IsReady => true;
    public string Status => "Ready";
    public ConcurrentQueue<(long Hwnd, ShoutChannel Channel)> Selections { get; } = new();
    public bool CanSelect(ShoutChannel channel) => Enum.IsDefined(channel);
    public bool TrySelect(BoundGameWindow window, ShoutChannel channel, InputDispatchMode mode)
    {
        Selections.Enqueue((window.Hwnd.ToInt64(), channel));
        return true;
    }
}

sealed class RecordingInputService : IInputService
{
    private readonly ConcurrentQueue<(long Hwnd, string Text)> _texts = new();
    public InputDispatchMode DefaultDispatchMode { get; set; } = InputDispatchMode.PostMessage;
    public bool YieldToUserInput { get; set; }
    public int UserInputYieldMs { get; set; }
    public IReadOnlyList<string> TextsFor(long hwnd) => _texts.Where(item => item.Hwnd == hwnd).Select(item => item.Text).ToArray();
    public InputYieldStatus GetYieldStatus(nint hwnd, InputDispatchMode mode) => new(false, true, false, 0, 0, 0, false, false, false, false, "test", "test");
    public bool TryParseHotkey(string hotkey, out string error) { error = ""; return !string.IsNullOrWhiteSpace(hotkey); }
    public bool SendKey(nint hwnd, string hotkey) => true;
    public bool SendKey(nint hwnd, string hotkey, InputDispatchMode mode) => true;
    public bool SendText(nint hwnd, string text) { _texts.Enqueue((hwnd.ToInt64(), text)); return true; }
    public bool SendText(nint hwnd, string text, InputDispatchMode mode) { _texts.Enqueue((hwnd.ToInt64(), text)); return true; }
    public bool Click(nint hwnd, int x, int y) => true;
    public bool Click(nint hwnd, int x, int y, InputDispatchMode mode) => true;
    public bool Click(nint hwnd, int x, int y, InputDispatchMode mode, InputMouseButton button) => true;
    public bool TryConsumeGlobalCooldown(int cooldownMs) => true;
}
