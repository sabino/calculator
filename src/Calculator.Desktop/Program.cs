using Uno.UI.Hosting;

namespace CalculatorApp.Desktop;

internal static class Program
{
    public static string[] Arguments { get; private set; } = [];
    // The macOS bundle launcher does not forward its arguments to the managed entry point.
    public static bool IsSmokeTest => Arguments.Contains("--smoke-test")
        || Environment.GetEnvironmentVariable("CALCULATOR_UI_TEST") == "1";

    [STAThread]
    public static void Main(string[] args)
    {
        Arguments = args;
        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseX11()
            .UseMacOS()
            .UseWin32()
            .Build();
        host.Run();
    }
}
