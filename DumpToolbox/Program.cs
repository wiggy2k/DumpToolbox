using DumpToolbox.Core;
using Avalonia;

namespace DumpToolbox;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Create the editable mastering-rule database beside the executable on first run.
        // Failure is non-fatal; the shared resurrection service reports it when a rule is needed.
        EofSlackRuleService.EnsureDefaultFileBesideExecutable(out _);
        JolietNamingRuleService.EnsureDefaultFileBesideExecutable(out _);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
