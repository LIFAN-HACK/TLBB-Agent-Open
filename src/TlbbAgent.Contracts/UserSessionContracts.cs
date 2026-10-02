namespace TlbbAgent.Contracts;

public enum UserSessionState
{
    SignedOut,
    SigningIn,
    SignedIn,
    Expired,
    Error
}

public sealed record UserSessionSnapshot(
    UserSessionState State,
    string DisplayName,
    string StatusText,
    string? AvatarUri = null);

public interface IUserSessionService
{
    UserSessionSnapshot Current { get; }
    bool CanBeginLogin { get; }
    event EventHandler<UserSessionSnapshot>? Changed;

    Task BeginLoginAsync(CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
}
