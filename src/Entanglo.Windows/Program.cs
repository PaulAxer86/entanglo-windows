using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Entanglo.Windows;

public static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (DecideRedirection()) return 0;

        Application.Start(p =>
        {
            var ctx = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(ctx);
            new App();
        });
        return 0;
    }

    static bool DecideRedirection()
    {
        var activated = AppInstance.GetCurrent().GetActivatedEventArgs();
        var primary   = AppInstance.FindOrRegisterForKey("Entanglo-MainInstance");
        if (primary.IsCurrent) return false;

        primary.RedirectActivationToAsync(activated).AsTask().Wait();
        return true;
    }
}
