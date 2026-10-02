using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

public abstract class AutomationControllerBase<TState> : IAutomationController
    where TState : struct, Enum
{
    protected readonly ILogService Log;
    protected readonly StateMachine<TState> Machine;
    protected BoundGameWindow? Target;

    protected AutomationControllerBase(TState initial, ILogService log)
    {
        Log = log;
        Machine = new StateMachine<TState>(initial);
        Machine.Changed += (_, _) => RaiseStateChanged();
    }

    public abstract string Key { get; }
    public abstract string Name { get; }
    public abstract string Group { get; }

    public RunState RunState { get; protected set; } = RunState.Idle;
    public string CurrentStateName => Machine.Current.ToString();
    public event EventHandler? StateChanged;

    public void Attach(BoundGameWindow window) => Target = window;

    public virtual void Start()
    {
        if (Target is null)
        {
            Log.Warn(Name, "未绑定窗口，无法启动。");
            return;
        }

        if (!Target.IsAutomationAllowed)
        {
            Log.Warn(Name, Target.AutomationBlockReason, Target.Id);
            return;
        }

        RunState = RunState.Running;
        OnStart();

        if (RunState == RunState.Running)
            Log.Info(Name, $"{Name} 状态机已启动。", Target.Id);

        RaiseStateChanged();
    }

    protected virtual void OnStart() { }

    protected void MarkFaulted(Exception ex)
    {
        RunState = RunState.Error;
        Log.Error(Name, $"已停止：状态机异常。{ex.Message}", Target?.Id ?? "-");
        RaiseStateChanged();
    }

    public virtual void Pause()
    {
        if (RunState != RunState.Running) return;
        RunState = RunState.Paused;
        Log.Info(Name, $"{Name} 已暂停。", Target?.Id ?? "-");
        RaiseStateChanged();
    }

    public virtual void Stop()
    {
        RunState = RunState.Stopped;
        Machine.Reset();
        Log.Info(Name, $"{Name} 已停止。", Target?.Id ?? "-");
        RaiseStateChanged();
    }

    protected void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warn(Name, $"状态通知失败：{ex.Message}", Target?.Id ?? "-");
        }
    }
}
