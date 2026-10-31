using Avalonia;
using System;
using System.Linq;

namespace Nexus;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // Headless end-to-end verification of the data layer.
        // "--selftest" = full PostgreSQL suite; "--selftest <provider>" = generic engine matrix;
        // "--selftest secrets" = OS keyring round-trip + connections.json secrecy checks;
        // "--selftest updater" = self-update logic (+ install detection / optional package apply).
        var selftestIdx = Array.IndexOf(args, "--selftest");
        if (selftestIdx >= 0)
        {
            var providerArg = args.Length > selftestIdx + 1 ? args[selftestIdx + 1] : null;
            return providerArg switch
            {
                null or "postgresql" => SelfTest.RunAsync().GetAwaiter().GetResult(),
                "secrets" => SelfTestSecrets.Run(),
                "updater" => SelfTestUpdater.RunAsync().GetAwaiter().GetResult(),
                _ => SelfTestMatrix.RunAsync(providerArg).GetAwaiter().GetResult()
            };
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Matches StartupWMClass in the Linux .desktop file so docks show the Nexus icon.
            .With(new X11PlatformOptions { WmClass = "Nexus" })
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
