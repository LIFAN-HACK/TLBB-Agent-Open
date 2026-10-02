using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

public sealed class StateMachine<TState> where TState : struct, Enum
{
    public StateMachine(TState initial)
    {
        Initial = initial;
        Current = initial;
    }

    public TState Initial { get; }
    public TState Current { get; private set; }
    public event EventHandler<TState>? Changed;

    public void Goto(TState state)
    {
        if (EqualityComparer<TState>.Default.Equals(Current, state)) return;
        Current = state;
        Changed?.Invoke(this, state);
    }

    public void Reset() => Goto(Initial);
}

public sealed class StateMachineEngine : IStateMachineEngine
{
    private readonly ILogService _log;
    public StateMachineEngine(ILogService log) => _log = log;

    public IDisposable Run(string name, Func<CancellationToken, Task> loop, Action<Exception>? onFault = null)
    {
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await loop(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* 正常停止 */ }
            catch (Exception ex)
            {
                _log.Error(name, $"状态机异常：{ex.Message}");
                onFault?.Invoke(ex);
            }
        }, cts.Token);
        return new Stopper(cts);
    }

    private sealed class Stopper(CancellationTokenSource cts) : IDisposable
    {
        public void Dispose()
        {
            try { cts.Cancel(); } catch { /* ignore */ }
            cts.Dispose();
        }
    }
}
