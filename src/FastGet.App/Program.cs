using FastGet.App.UI;
using FastGet.Core;

namespace FastGet.App;

static class Program
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastGet");

    [STAThread]
    static int Main(string[] args)
    {
        if (BrowserIntegration.IsNativeHostLaunch(args))
            return NativeHost.Run();

        // Used by the installer: register or remove the native messaging host without showing any UI.
        if (args.Contains("--register-browsers"))
        {
            BrowserIntegration.Install();
            return 0;
        }
        if (args.Contains("--unregister-browsers"))
        {
            BrowserIntegration.Uninstall();
            return 0;
        }

        bool background = args.Contains("--background");
        var url = args.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                           || a.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        using var mutex = new Mutex(true, Ipc.MutexName, out bool first);
        if (!first)
        {
            // Hand the request to the running instance instead of opening a second window.
            Ipc.TrySend(url != null ? new BrowserMessage { Type = "download", Url = url } : new BrowserMessage { Type = "show" },
                launchIfMissing: false);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        using var manager = new DownloadManager(DataDir);
        using var form = new MainForm(manager, startHidden: background);
        using var server = new PipeServer(msg => form.HandleMessage(msg));
        if (url != null) form.HandleMessage(new BrowserMessage { Type = "download", Url = url });
        Application.Run(form);
        return 0;
    }
}
