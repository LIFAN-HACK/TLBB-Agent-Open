using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

/// <summary>
/// Return-point runtime intentionally retired for rebuild.
/// Hang combat must never be suspended by legacy return-point logic.
/// </summary>
public sealed class DisabledReturnPointRuntimeService : IReturnPointRuntimeService
{
    public bool HasEnabledReturnPoint => false;

    public void Reset()
    {
    }

    public ReturnPointRuntimeResult Evaluate(BoundGameWindow window) =>
        new(ReturnPointRuntimeAction.None, ShouldSuspendCombat: false, Message: "返点逻辑已下线，等待重做。");
}
