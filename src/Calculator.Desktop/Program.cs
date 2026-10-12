using Uno.UI.Hosting;

namespace CalculatorApp.Desktop;

internal static class Program
{
    public static string[] Arguments { get; private set; } = [];
    // The macOS bundle launcher does not forward its arguments to the managed entry point.
    public static bool IsSmokeTest => Arguments.Contains("--smoke-test")
        || Environment.GetEnvironmentVariable("CALCULATOR_UI_TEST") == "1";
    public static string InitialView => Arguments.FirstOrDefault(argument => argument.StartsWith("--mode=", StringComparison.Ordinal))?[7..]
        ?? (Arguments.Contains("--graphing") ? "graphing" : Arguments.Contains("--scientific") ? "scientific"
            : Environment.GetEnvironmentVariable("CALCULATOR_START_MODE") ?? "standard");
    public static int InitialMode => InitialView.ToLowerInvariant() switch
    {
        "standard" => 0, "scientific" => 1, "programmer" => 2,
        "date" or "date calculation" => 4, "graphing" => 5, "settings" => 6, _ => 3
    };

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
