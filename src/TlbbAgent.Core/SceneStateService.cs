using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class SceneStateService : ISceneStateService
{
    private readonly INavigationRecognitionService _navigation;

    public SceneStateService(INavigationRecognitionService navigation)
    {
        _navigation = navigation;
    }

    public NavigationRecognitionResult Recognize(BoundGameWindow window, NavigationRecognitionConfig config) =>
        _navigation.Recognize(window, config);
}

public sealed class NavigationSceneStateService : ISceneStateService
{
    private readonly INavigationRecognitionService _navigation;

    public NavigationSceneStateService(INavigationRecognitionService navigation)
    {
        _navigation = navigation;
    }

    public NavigationRecognitionResult Recognize(BoundGameWindow window, NavigationRecognitionConfig config) =>
        _navigation.Recognize(window, config);
}
