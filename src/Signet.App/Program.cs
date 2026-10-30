using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Signet.App;

/// <summary>
/// Windows-only entry point, in place of the generated one. Upstream gets two things from
/// Info.plist that Windows has to do in code:
/// <list type="bullet">
/// <item>the <c>signet</c> URL scheme (<c>CFBundleURLTypes</c>): registered under
/// <c>HKCU\Software\Classes\signet</c> at every launch, so it follows whichever copy last ran;</item>
/// <item>one instance only (<c>LSMultipleInstancesProhibited</c>): a second launch, including one a
/// browser makes for a <c>signet://</c> link, hands its activation to the running instance, brings
/// that window forward and exits.</item>
/// </list>
/// </summary>
public static partial class Program
{
    private const string InstanceKey = "Signet.main";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (args.Contains("--preview"))
        {
            // A second, throwaway window on mock data (upstream's #Preview and test-host model): it
            // neither claims the single instance nor the signet:// scheme, and touches no files.
            Application.Start(callback =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new App(preview: true);
            });
            return 0;
        }
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!main.IsCurrent)
        {
            RedirectActivation(activation, main);
            return 0;
        }
        RegisterURLScheme();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            var app = new App(IncomingURL(activation));
            main.Activated += (_, redirected) => app.Activated(IncomingURL(redirected));
        });
        return 0;
    }

    /// <summary>The <c>signet://</c> link this activation carries, if any.</summary>
    private static Uri? IncomingURL(AppActivationArguments args) =>
        args.Kind == ExtendedActivationKind.Protocol && args.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocol
            ? protocol.Uri
            : null;

    private static void RegisterURLScheme()
    {
        try
        {
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Signet.exe");
            ActivationRegistrationManager.RegisterForProtocolActivation(Models.DAppPairingLink.Scheme, $"{exe},0", "Signet", exe);
        }
        catch (Exception e)
        {
            Trace.WriteLine($"Signet: could not register the signet:// scheme: {e.Message}");
        }
    }

    /// <summary>
    /// Passes this launch to the running instance and waits for it to take it. The wait must pump
    /// COM on this STA thread (<c>CoWaitForMultipleObjects</c>), as the Windows App SDK sample does.
    /// </summary>
    private static void RedirectActivation(AppActivationArguments args, AppInstance main)
    {
        using var done = new EventWaitHandle(false, EventResetMode.ManualReset);
        Task.Run(() =>
        {
            try { main.RedirectActivationToAsync(args).AsTask().Wait(); }
            finally { done.Set(); }
        });
        _ = CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, [done.SafeWaitHandle.DangerousGetHandle()], out _);
        // This process was started by the user or the browser, so it may give the window focus.
        try
        {
            var window = Process.GetProcessById((int)main.ProcessId).MainWindowHandle;
            if (window != IntPtr.Zero)
            {
                if (IsIconic(window)) ShowWindow(window, 9);  // SW_RESTORE
                SetForegroundWindow(window);
            }
        }
        catch (ArgumentException) { }
    }

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint flags, uint timeout, uint count, IntPtr[] handles, out uint index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
}
