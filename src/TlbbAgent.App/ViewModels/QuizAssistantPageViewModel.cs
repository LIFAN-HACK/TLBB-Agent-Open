namespace TlbbAgent.App.ViewModels;

public sealed class QuizAssistantPageViewModel : PageViewModel
{
    public override string Title => "答题助手";

    public string Status => "未就绪";

    public string RemainingUses => "--";

    public bool IsBackendReady => false;

    public bool IsEnabled => false;
}
