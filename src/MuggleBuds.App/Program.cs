using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace MuggleBuds.App;

/// <summary>
/// Entry point. Replaces the XAML-generated Main so only one copy of the tray app runs.
/// </summary>
/// <remarks>
/// <c>MuggleBuds.exe --quit</c> asks the running copy to quit the same way the tray menu's Quit
/// does, so it closes its link to the buds properly. Build scripts and installers use it
/// instead of ending the process, which would leave the link's subscriptions open on the buds.
/// It returns once the running copy has exited, so the caller can go on to replace its files.
/// </remarks>
public static class Program
{
    private const string InstanceKey = "MuggleBuds";

    /// <summary>
    /// Signaled by <c>--quit</c>; the running copy waits on it.
    /// </summary>
    public const string QuitEventName = "MuggleBuds.Quit";

    // Quitting closes the link to the buds first, which takes a moment
    private static readonly TimeSpan QuitWait = TimeSpan.FromSeconds(10);

    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Quit Request
        if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            if (EventWaitHandle.TryOpenExisting(QuitEventName, out var quit))
            {
                quit.Set();
                quit.Dispose();
                WaitForOtherCopiesToExit();
            }
            return;
        }

        // A second launch (startup task plus a manual start) exits right away.
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!instance.IsCurrent)
        {
            return;
        }

        Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    private static void WaitForOtherCopiesToExit()
    {
        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
        if (name is null)
        {
            return;
        }

        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                if (process.Id != Environment.ProcessId)
                {
                    _ = process.WaitForExit(QuitWait);
                }
            }
        }
    }
}
