using Avalonia;
using Avalonia.Media;
using System;

namespace FrontLine_Linux;

class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .With(new FontManagerOptions
            {
                FontFallbacks =
                [
                    new FontFallback
                    {
                        UnicodeRange = UnicodeRange.Default,
                        FontFamily = new FontFamily(
                            "avares://FrontLine-Linux/assets/fonts/fallback#Noto Sans CJK JP")
                    }
                ]
            })
            .LogToTrace();
}
