using Avalonia;
using Avance_Player.Services.Diagnostics;
using System;

namespace Avance_Player;

class Program {
    // 这里别碰 Avalonia、第三方 API，也别用依赖 SynchronizationContext 的东西。
    // AppMain 还没跑，初始化都没做完，容易炸。
    [STAThread]
    public static int Main(string[] args) {
        if (Array.Exists(args, argument => string.Equals(argument, "--diagnostics", StringComparison.OrdinalIgnoreCase))) {
            return PlaybackDiagnostics.Run();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Avalonia 的配置，别删；可视化设计器也要用。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}