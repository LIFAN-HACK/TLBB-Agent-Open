using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

public sealed class DungeonController : AutomationControllerBase<DungeonState>, IDungeonController
{
    private readonly IDungeonConfigService _config;
    private readonly IGlobalConfigService _global;
    private readonly INavigationRecognitionService _navigation;
    private readonly IPerceptionHub _perception;
    private readonly IWindowCaptureService _capture;
    private readonly IOpenTextRecognizer _openText;
    private readonly IReturnNavigationService _stageNavigation;
    private readonly IInputService _input;
    private readonly IStateMachineEngine _engine;
    private IDisposable? _runner;

    public DungeonController(
        ILogService log,
        IDungeonConfigService config,
        IGlobalConfigService global,
        INavigationRecognitionService navigation,
        IPerceptionHub perception,
        IWindowCaptureService capture,
        IOpenTextRecognizer openText,
        IReturnNavigationService stageNavigation,
        IInputService input,
        IStateMachineEngine engine)
        : base(DungeonState.Idle, log)
    {
        _config = config;
        _global = global;
        _navigation = navigation;
        _perception = perception;
        _capture = capture;
        _openText = openText;
        _stageNavigation = stageNavigation;
        _input = input;
        _engine = engine;
    }

    public override string Key => "hang.dungeon";
    public override string Name => "三环副本";
    public override string Group => "挂机";

    protected override void OnStart()
    {
        Machine.Goto(DungeonState.ValidateConfig);
        var cfg = _config.Current;
        if (_config.CurrentSteps.Count == 0)
        {
            RunState = RunState.Error;
            Machine.Goto(DungeonState.Recover);
            Log.Warn(Name, "当前副本模式没有脚本步骤，无法启动三环副本。", Target?.Id ?? "-");
            RaiseStateChanged();
            return;
        }

        Log.Info(Name, $"三环副本配置已就绪：模式 {cfg.Mode}，次数 {cfg.RunCount}，队伍编号 {cfg.TeamNumber}，步骤 {_config.CurrentSteps.Count}。", Target!.Id);
        _runner?.Dispose();
        _runner = _engine.Run(Name, RunLoopAsync, MarkFaulted);
    }

    public override void Stop()
    {
        _runner?.Dispose();
        _runner = null;
        base.Stop();
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        var cfg = _config.Current;
        var totalRuns = Math.Max(1, cfg.RunCount);
        for (var run = 1; run <= totalRuns && !token.IsCancellationRequested && RunState == RunState.Running; run++)
        {
            var window = Target;
            if (window is null)
            {
                RunState = RunState.Error;
                Machine.Goto(DungeonState.Recover);
                Log.Warn(Name, "副本状态机丢失绑定窗口。");
                RaiseStateChanged();
                break;
            }

            Machine.Goto(DungeonState.PrepareTeam);
            Log.Info(Name, $"开始副本第 {run}/{totalRuns} 次：{DescribeMode(cfg)}。", window.Id);
            await Delay(cfg.StepIntervalMs, token).ConfigureAwait(false);

            foreach (var step in _config.CurrentSteps)
            {
                if (token.IsCancellationRequested || RunState != RunState.Running)
                    break;

                ExecuteStep(window, cfg, step);
                await Delay(Math.Max(cfg.StepIntervalMs, step.DelayAfterMs), token).ConfigureAwait(false);
            }

            if (cfg.EnableClearBagAfterFinish)
                ExecuteClearBag(window, cfg);

            if (cfg.EnableChainNextTask)
                ExecuteChainNextTask(window, cfg);
        }

        if (!token.IsCancellationRequested && RunState == RunState.Running)
        {
            RunState = RunState.Stopped;
            Machine.Goto(DungeonState.Stop);
            Log.Info(Name, "三环副本执行次数已完成。", Target?.Id ?? "-");
        }

        RaiseStateChanged();
    }

    private void ExecuteStep(BoundGameWindow window, DungeonConfig cfg, DungeonScriptStep step)
    {
        Machine.Goto(DungeonState.ExecuteScriptStep);
        Log.Info(Name, $"执行副本步骤：{step.Order}. {step.StageName}", window.Id);

        if (!string.IsNullOrWhiteSpace(step.MapName))
        {
            var nav = _navigation.Recognize(window, cfg.NavigationRecognition);
            if (nav.HasCoordinate)
                NavigateStage(window, cfg, step, nav);
        }

        switch (step.Action)
        {
            case DungeonStepAction.HotkeyOnly:
                ExecuteHotkey(window, step.Hotkey, $"步骤 {step.StageName}");
                break;
            case DungeonStepAction.ClickClientPoint:
                ExecuteClick(window, step.ClickX, step.ClickY, $"步骤 {step.StageName}");
                break;
            case DungeonStepAction.HotkeyThenClickClientPoint:
                ExecuteHotkey(window, step.Hotkey, $"步骤 {step.StageName}");
                ExecuteClick(window, step.ClickX, step.ClickY, $"步骤 {step.StageName}");
                break;
            case DungeonStepAction.WaitBattle:
                ExecuteFight(window, cfg, step);
                break;
            case DungeonStepAction.WaitCompletion:
                ConfirmCompletion(window, cfg, step);
                break;
        }
    }

    private void NavigateStage(BoundGameWindow window, DungeonConfig cfg, DungeonScriptStep step, NavigationRecognitionResult current)
    {
        Machine.Goto(DungeonState.NavigateStage);
        var distance = current.HasCoordinate ? Distance(current.X!.Value, current.Y!.Value, step.X, step.Y) : 0;
        var target = new ReturnPoint
        {
            MapName = step.MapName,
            X = step.X,
            Y = step.Y,
            Label = step.StageName,
            SourceModule = step.SourceModule,
            SourcePath = step.SourcePath
        };

        var result = _stageNavigation.TryNavigate(window, current, target, distance, cfg.StageNavigation);
        if (result.Executed)
            Log.Info(Name, $"副本阶段导航：{result.Message}", window.Id);
        else
            Log.Trace(Name, $"副本阶段导航未执行：{result.Message}", window.Id);
    }

    private void ExecuteFight(BoundGameWindow window, DungeonConfig cfg, DungeonScriptStep step)
    {
        Machine.Goto(DungeonState.ExecuteFight);
        _ = _perception.Read(window, _global.Current);
        var hotkey = string.IsNullOrWhiteSpace(step.Hotkey) ? cfg.AttackHotkey : step.Hotkey;
        ExecuteHotkey(window, hotkey, $"副本战斗：{step.StageName}");
        Log.Info(Name, $"副本战斗已执行：{step.StageName}", window.Id);
        Machine.Goto(DungeonState.EnterBattleOrDungeon);
    }

    private void ConfirmCompletion(BoundGameWindow window, DungeonConfig cfg, DungeonScriptStep step)
    {
        Machine.Goto(DungeonState.ConfirmCompletion);
        var text = RecognizeCompletion(window, cfg);
        if (string.IsNullOrWhiteSpace(text))
        {
            Log.Info(Name, $"副本完成确认阶段：{step.StageName}，未识别到完成提示，按步骤继续。", window.Id);
            return;
        }

        var matched = SplitKeywords(cfg.CompletionKeywords)
            .FirstOrDefault(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
        if (matched is null)
            Log.Trace(Name, "未匹配完成提示。", window.Id);
        else
            Log.Info(Name, "检测到完成提示。", window.Id);
    }

    private void ExecuteClearBag(BoundGameWindow window, DungeonConfig cfg)
    {
        Machine.Goto(DungeonState.Cleanup);
        if (string.IsNullOrWhiteSpace(cfg.ClearBagHotkey))
        {
            Log.Warn(Name, "已启用结束清包，但未配置清包快捷键。", window.Id);
            return;
        }

        ExecuteHotkey(window, cfg.ClearBagHotkey, "副本结束清包");
    }

    private void ExecuteChainNextTask(BoundGameWindow window, DungeonConfig cfg)
    {
        Machine.Goto(DungeonState.ChainNextTask);
        var next = string.IsNullOrWhiteSpace(cfg.ChainNextModeKey) ? "未指定" : cfg.ChainNextModeKey;
        Log.Info(Name, $"副本完成后任务衔接：{next}。当前仅记录衔接目标，后续由任务队列统一调度。", window.Id);
    }

    private void ExecuteHotkey(BoundGameWindow window, string hotkey, string reason)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
            return;

        if (!_input.TryParseHotkey(hotkey, out var error))
        {
            Log.Warn(Name, $"{reason} 快捷键无效：{hotkey} | {error}", window.Id);
            Machine.Goto(DungeonState.Recover);
            return;
        }

        _input.SendKey(window.Hwnd, hotkey);
        Log.Info(Name, $"{reason}：发送快捷键 {hotkey}", window.Id);
    }

    private void ExecuteClick(BoundGameWindow window, int x, int y, string reason)
    {
        if (x < 0 || y < 0)
        {
            Log.Warn(Name, $"{reason} 点击坐标无效：{x},{y}", window.Id);
            Machine.Goto(DungeonState.Recover);
            return;
        }

        _input.Click(window.Hwnd, x, y);
        Log.Info(Name, $"{reason}：点击客户区 {x},{y}", window.Id);
    }

    private string RecognizeCompletion(BoundGameWindow window, DungeonConfig cfg)
    {
        var region = cfg.CompletionRegion;
        if (!region.Enabled || region.Width <= 0 || region.Height <= 0)
            return string.Empty;

        if (!_openText.IsAvailable)
            return string.Empty;

        var capture = _capture.CaptureRegion(window.Hwnd, region.X, region.Y, region.Width, region.Height);
        return capture is null
            ? string.Empty
            : _openText.Recognize(capture, OpenTextSlot.Toast).Text.Trim();
    }

    private static string DescribeMode(DungeonConfig cfg)
    {
        var mode = cfg.Mode == DungeonMode.OldThreeRings ? "老三环" : "新三环";
        var bridge = cfg.IGoLeftBridge ? "我去左桥" : "不指定左桥";
        var boss = cfg.NewThreeBoss2Position == DungeonBossPosition.Custom
            ? $"新三2 boss 自定义站位({cfg.NewThreeBoss2CustomX},{cfg.NewThreeBoss2CustomY})"
            : $"新三2 boss 站位 {cfg.NewThreeBoss2Position}";
        return $"{mode}，队伍编号 {cfg.TeamNumber}，{bridge}，{boss}";
    }

    private static IEnumerable<string> SplitKeywords(string text) =>
        text.Split(new[] { ';', '；', ',', '，', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s));

    private static double Distance(int x1, int y1, int x2, int y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Task Delay(int milliseconds, CancellationToken token) =>
        Task.Delay(Math.Max(50, milliseconds), token);
}
