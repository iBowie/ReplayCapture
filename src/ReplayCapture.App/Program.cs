using Avalonia;

namespace ReplayCapture.App;

internal static class Program
{
    // STA: Core's D3D11/WASAPI/COM interop expects the apartment WPF used to provide implicitly.
    [STAThread]
    public static int Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
