using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class UnavailableUserSessionService : IUserSessionService
{
    public UserSessionSnapshot Current { get; } = new(
        UserSessionState.SignedOut,
        "未登录",
        "登录 / 账户");

    public bool CanBeginLogin => false;

    public event EventHandler<UserSessionSnapshot>? Changed
    {
        add { }
        remove { }
    }

    public Task BeginLoginAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
