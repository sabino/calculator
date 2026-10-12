using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Uno.Resizetizer;
using Windows.Graphics;

namespace CalculatorApp.Desktop;

public sealed partial class App : Application
{
    private Window? window;

    public App()
    {
        Uno.UI.FeatureConfiguration.Font.DefaultTextFontFamily = OperatingSystem.IsWindows()
            ? "Segoe UI" : "ms-appx:///Assets/Fonts/selawk.ttf";
        InitializeComponent();
        Resources["ContentControlThemeFontFamily"] = new Microsoft.UI.Xaml.Media.FontFamily(Uno.UI.FeatureConfiguration.Font.DefaultTextFontFamily);
        DispatcherShutdownMode = DispatcherShutdownMode.OnLastWindowClose;
        UnhandledException += (_, args) => Console.Error.WriteLine(args.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new Window { Title = "Calculator" };
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 320;
            presenter.PreferredMinimumHeight = 500;
        }
        window.AppWindow.Resize(new SizeInt32 { Width = 420, Height = 680 });
        var page = new CalculatorPage(window);
        window.Content = page;
        window.SetWindowIcon();
        if (OperatingSystem.IsWindows())
            page.Loaded += (_, _) => window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Calculator.ico"));
        window.Closed += (_, _) => page.CloseSession();
        window.Activate();
    }
}
