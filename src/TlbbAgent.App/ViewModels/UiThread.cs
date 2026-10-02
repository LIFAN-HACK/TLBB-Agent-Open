using System.Windows;
using System.Windows.Threading;

namespace TlbbAgent.App.ViewModels;

internal static class UiThread
{
    public static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action, DispatcherPriority.DataBind);
    }
}
