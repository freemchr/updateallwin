using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace UpdateAllWin.App;

/// <summary>
/// UpdateAllWin.exe                    open the main window (and start the tray agent)
/// UpdateAllWin.exe --background       start only the tray agent (sign-in)
/// UpdateAllWin.exe --update-all       open and start Update All
/// UpdateAllWin.exe --page history     open on a page
/// Elevated helper modes (launched by the app with a UAC prompt, no UI):
///   --set-policy &lt;file&gt;, --service-control start|install|enable|disable
/// </summary>
public static class Program
{
    public static AppActivationArguments? LaunchArguments { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        if (ElevatedHelper.TryRun(args, out var exitCode)) return exitCode;

        WinRT.ComWrappersSupport.InitializeComWrappers();

        var current = AppInstance.GetCurrent();
        LaunchArguments = current.GetActivatedEventArgs();
        var main = AppInstance.FindOrRegisterForKey("UpdateAllWin.UI." + Environment.UserName);
        if (!main.IsCurrent)
        {
            // Already running for this user: hand our arguments to that instance and exit.
            RedirectActivation(LaunchArguments, main);
            return 0;
        }
        main.Activated += (_, e) => App.Instance?.OnRedirectedActivation(e);

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <summary>Redirect without blocking COM (pattern from the Windows App SDK docs).</summary>
    private static void RedirectActivation(AppActivationArguments args, AppInstance target)
    {
        var done = CreateEvent(IntPtr.Zero, true, false, null);
        _ = Task.Run(() =>
        {
            target.RedirectActivationToAsync(args).AsTask().Wait();
            SetEvent(done);
        });
        CoWaitForMultipleObjects(0 /* CWMO_DEFAULT */, 0xFFFFFFFF, 1, [done], out _);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint flags, uint timeout, uint count, IntPtr[] handles, out uint index);
}
