// Licensed under the MIT License.
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Text;

namespace CalculatorApp.Desktop;

public sealed class CalculatorPage : Page
{
    private readonly Window window;
    private readonly NativeCalculator calculator = new();
    private readonly Grid root = new() { Margin = new Thickness(3, 0, 3, 0) };
    private readonly Grid body = new();
    private readonly TextBlock heading = new() { FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, -3, 0, 0) };
    private TextBlock result = new();
    private TextBlock expression = new();
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private TextBlock converted = new();
    private readonly Dictionary<Button, int> commandButtons = [];
    private readonly NavigationPanel navigation;
    private ScientificOptionsPanel? scientificOptions;
    private ProgrammerOptionsPanel? programmerOptions;
    private Grid? numberKeypad;
    private Button? memoryDropdown;
    private int scientificSize;
    private readonly FontFamily calculatorIcons = new("ms-appx:///Assets/CalculatorIcons.ttf#Calculator Fluent Icons");
    private static readonly IReadOnlyDictionary<string, string> ButtonGlyphs = new Dictionary<string, string>
    {
        ["2nd"] = "\uF897", ["π"] = "\uF7CF", ["x²"] = "\uF7C8", ["x³"] = "\uF7CB",
        ["1/x"] = "\uF7C9", ["|x|"] = "\uF884", ["√x"] = "\uF899", ["∛x"] = "\uF881",
        ["n!"] = "\uF887", ["xʸ"] = "\uF7CA", ["ʸ√x"] = "\uF7CD", ["10ˣ"] = "\uF7CC",
        ["2ˣ"] = "\uF882", ["logᵧx"] = "\uF883", ["eˣ"] = "\uF7CE", ["±"] = "\uF898",
        ["⌫"] = "\uE94F", ["÷"] = "\uE94A", ["×"] = "\uE947", ["−"] = "\uE949",
        ["+"] = "\uE948", ["="] = "\uE94E"
    };
    private readonly Button details = new() { Content = new FontIcon { Glyph = "\uE81C", FontSize = 20 }, MinWidth = 40,
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
    private readonly ToggleButton keepOnTop = new() { Content = new FontIcon { Glyph = "\uE8A7", FontSize = 18 }, MinWidth = 36,
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
    private Flyout? detailsFlyout;
    private ComboBox fromUnit = new();
    private ComboBox toUnit = new();
    private GraphPanel? graphPanel;
    private DatePanel? datePanel;
    private NativeGraph? graph;
    private CalculatorSnapshot snapshot;
    private int viewMode;
    private bool refreshing;
    private const int CurrencyCategory = 12;
    private readonly CurrencyRates currencyRates = new();
    private readonly CancellationTokenSource closing = new();
    private bool refreshingRates;
    private bool closed;

    public CalculatorPage(Window window)
    {
        this.window = window;
        FontFamily = (FontFamily)Application.Current.Resources["ContentControlThemeFontFamily"];
        ((FontIcon)details.Content).FontFamily = calculatorIcons;
        ((FontIcon)details.Content).FontSize = 16;
        ((FontIcon)keepOnTop.Content).FontFamily = calculatorIcons;
        ((FontIcon)keepOnTop.Content).Glyph = "\uEE49";
        RequestedTheme = ThemePreferences.LoadTheme();
        snapshot = calculator.GetSnapshot();
        navigation = new NavigationPanel(snapshot.Categories, calculator.GetAbout().GraphingEnabled, SetMode, SetCategory, () => SetMode(6));
        navigation.Content = root;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(CreateHeader());
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Grid.SetRow(notice, 2);
        root.Children.Add(notice);
        Content = navigation;
        KeyDown += OnKeyDown;
        ActualThemeChanged += (_, _) => BuildBody();
        window.Activated += (_, _) => ApplyWindowTheme();
        SetMode(Program.InitialMode);
        Loaded += async (_, _) =>
        {
            ApplyWindowTheme();
            if (Program.IsSmokeTest)
            {
                await Task.Yield();
                try
                {
                    await RunSmokeChecksAsync();
                    Console.WriteLine("Shared Skia controls and native interop passed");
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error);
                    Environment.ExitCode = 1;
                }
                finally
                {
                    window.Close();
                }
            }
        };
    }

    private Grid CreateHeader()
    {
        var header = new Grid { Height = 48, Margin = new Thickness(-3, 0, -3, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(heading, 1);
        header.Children.Add(heading);
        Grid.SetColumn(keepOnTop, 2);
        AutomationProperties.SetName(keepOnTop, "Keep on top");
        ToolTipService.SetToolTip(keepOnTop, "Keep on top");
        keepOnTop.Click += (_, _) =>
        {
            if (window.AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.IsAlwaysOnTop = keepOnTop.IsChecked == true;
        };
        header.Children.Add(keepOnTop);
        Grid.SetColumn(details, 3);
        AutomationProperties.SetName(details, "History and memory");
        details.Click += (_, _) => ShowDetails();
        header.Children.Add(details);
        return header;
    }

    private void SetCategory(int index)
    {
        if (!calculator.SetCategory(index)) { ShowError(calculator.LastError); return; }
        snapshot = calculator.GetSnapshot();
        heading.Text = snapshot.Categories[index];
        navigation.SelectView(3, index);
        BuildBody();
        if (index == CurrencyCategory && !Program.IsSmokeTest) _ = RefreshRatesAsync();
    }

    private void SetMode(int mode)
    {
        if (mode <= 4 && !calculator.SetMode(mode)) { ShowError(calculator.LastError); return; }
        viewMode = mode;
        heading.Text = mode switch
        {
            1 => "Scientific", 2 => "Programmer", 3 => snapshot.Categories[snapshot.CategoryIndex], 4 => "Date calculation", 5 => "Graphing", 6 => "Settings", _ => "Standard"
        };
        details.IsEnabled = mode < 3;
        keepOnTop.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (mode != 0 && keepOnTop.IsChecked == true)
        {
            keepOnTop.IsChecked = false;
            if (window.AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = false;
        }
        snapshot = calculator.GetSnapshot();
        navigation.SelectView(mode, snapshot.CategoryIndex);
        BuildBody();
    }

    private Brush GetBrush(string key)
    {
        var theme = ActualTheme == ElementTheme.Light ? "Light" : "Dark";
        var dictionary = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[theme];
        return (Brush)dictionary[key];
    }

    private bool ApplyWindowTheme()
    {
        return closed || GetBrush("CalculatorBackground") is SolidColorBrush background
            && WindowAppearance.ApplyTheme(window, ActualTheme, background.Color);
    }

    private void BuildBody()
    {
        refreshing = true;
        body.Children.Clear();
        body.RowDefinitions.Clear();
        commandButtons.Clear();
        scientificOptions = null;
        programmerOptions = null;
        numberKeypad = null;
        memoryDropdown = null;
        scientificSize = 0;
        Background = GetBrush("CalculatorBackground");
        navigation.Background = Background;
        root.Background = Background;
        ApplyWindowTheme();
        notice.Text = "";
        notice.Visibility = Visibility.Collapsed;
        try
        {
            if (viewMode == 6)
            {
                body.Children.Add(CreateSettings());
                return;
            }
            if (viewMode == 5)
            {
                graph ??= new NativeGraph();
                graphPanel ??= new GraphPanel(graph);
                body.Children.Add(graphPanel);
                return;
            }
            if (viewMode == 4)
            {
                datePanel ??= new DatePanel(calculator);
                body.Children.Add(datePanel);
                return;
            }
            var controls = new StackPanel { Spacing = 8 };
            result = new TextBlock { FontSize = 46, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, IsTextSelectionEnabled = true };
            expression = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0.7 };
            converted = new TextBlock { FontSize = 30, IsTextSelectionEnabled = true };
            result.ContextFlyout = CreateClipboardMenu(() => snapshot.Primary, true);
            expression.ContextFlyout = CreateClipboardMenu(() => snapshot.Expression, false);
            converted.ContextFlyout = CreateClipboardMenu(() => snapshot.Converted, false);
            if (viewMode == 3) BuildConverter(controls);
            else BuildCalculatorOptions(controls);
            bool calculatorLayout = viewMode is 0 or 1;
            body.RowDefinitions.Add(new RowDefinition { Height = calculatorLayout ? new GridLength(92, GridUnitType.Star) : GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = calculatorLayout
                ? viewMode == 1 ? new GridLength(32, GridUnitType.Star) : GridLength.Auto
                : new GridLength(1, GridUnitType.Star), MinHeight = viewMode == 1 ? 32 : 0 });
            body.RowDefinitions.Add(new RowDefinition { Height = calculatorLayout ? new GridLength(32, GridUnitType.Star) : GridLength.Auto, MinHeight = 32 });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(calculatorLayout ? viewMode == 1 ? 276 : 308 : 3, GridUnitType.Star) });
            if (scientificOptions is not null)
            {
                Grid.SetRow(scientificOptions.AngleControls, 1);
                body.Children.Add(scientificOptions.AngleControls);
            }
            else body.Children.Add(controls);

            var display = new Grid { MinHeight = calculatorLayout ? 72 : viewMode == 3 ? 100 : 64, Padding = new Thickness(6, 0, 10, 0) };
            display.RowDefinitions.Add(new RowDefinition { Height = calculatorLayout ? new GridLength(20, GridUnitType.Star) : GridLength.Auto });
            display.RowDefinitions.Add(new RowDefinition { Height = new GridLength(calculatorLayout ? 72 : 1, GridUnitType.Star) });
            display.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            display.Children.Add(expression);
            var scaledResult = new Viewbox { Child = result, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = calculatorLayout ? VerticalAlignment.Top : VerticalAlignment.Center };
            Grid.SetRow(scaledResult, 1);
            display.Children.Add(scaledResult);
            if (viewMode == 3) { Grid.SetRow(converted, 2); display.Children.Add(converted); }
            expression.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetRow(display, calculatorLayout ? 0 : 1);
            body.Children.Add(display);

            var memoryRow = new Grid();
            if (viewMode < 3)
            {
                for (int index = 0; index < snapshot.MemoryButtons.Count; index++)
                {
                    memoryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 80 });
                    var button = CreateCommandButton(snapshot.MemoryButtons[index], true);
                    Grid.SetColumn(button, index);
                    memoryRow.Children.Add(button);
                }
                memoryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.01, GridUnitType.Star) });
                memoryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 80 });
                var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                caption.Children.Add(new TextBlock { Text = "M", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                caption.Children.Add(new FontIcon { Glyph = "\uE70D", FontSize = 8, VerticalAlignment = VerticalAlignment.Center });
                memoryDropdown = new Button { Content = caption, Style = (Style)Application.Current.Resources["CalculatorKey"],
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), MinHeight = 32, Padding = new Thickness(0) };
                memoryDropdown.Resources["ButtonBackgroundDisabled"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                AutomationProperties.SetName(memoryDropdown, "Show stored memory");
                memoryDropdown.Click += (_, _) => ShowDetails(true);
                Grid.SetColumn(memoryDropdown, snapshot.MemoryButtons.Count + 1);
                memoryRow.Children.Add(memoryDropdown);
            }
            Grid.SetRow(memoryRow, 2);
            body.Children.Add(memoryRow);
            var keypad = new Grid();
            numberKeypad = keypad;
            var keypadRows = GetKeypadRows();
            for (int row = 0; row < keypadRows.Count; row++)
            {
                keypad.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                var rowButtons = keypadRows[row];
                for (int column = 0; column < rowButtons.Count; column++)
                {
                    if (keypad.ColumnDefinitions.Count <= column) keypad.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var button = CreateCommandButton(rowButtons[column]);
                    Grid.SetRow(button, row);
                    Grid.SetColumn(button, column);
                    if (viewMode == 2 && row == 0 && column == 3) Grid.SetColumnSpan(button, 2);
                    keypad.Children.Add(button);
                }
            }
            if (scientificOptions is not null)
            {
                var scientificKeypad = new Grid();
                scientificKeypad.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = 44 });
                scientificKeypad.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                scientificOptions.VerticalAlignment = VerticalAlignment.Center;
                scientificKeypad.Children.Add(scientificOptions);
                Grid.SetRow(keypad, 1);
                scientificKeypad.Children.Add(keypad);
                Grid.SetRow(scientificKeypad, 3);
                body.Children.Add(scientificKeypad);
                scientificKeypad.SizeChanged += (_, _) =>
                {
                    if (viewMode != 1) return;
                    int size = scientificKeypad.ActualWidth >= 878 && scientificKeypad.ActualHeight >= 851 ? 2
                        : scientificKeypad.ActualWidth >= 527 && scientificKeypad.ActualHeight >= 523 ? 1 : 0;
                    if (size == scientificSize) return;
                    scientificSize = size;
                    RefreshDisplay();
                    scientificOptions?.UpdateSize(size);
                };
            }
            else
            {
                Grid.SetRow(keypad, 3);
                body.Children.Add(keypad);
            }
        }
        finally
        {
            refreshing = false;
            RefreshDisplay();
        }
    }

    private Button CreateCommandButton(CalculatorButton spec, bool compact = false)
    {
        var button = new Button { Content = spec.Title, IsEnabled = spec.Enabled, Style = (Style)Application.Current.Resources["CalculatorKey"] };
        AutomationProperties.SetName(button, spec.AccessibleName);
        SetButtonCaption(button, spec, compact);
        if (compact)
        {
            button.MinHeight = 32;
            button.Padding = new Thickness(0);
            button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.Resources["ButtonBackgroundDisabled"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        else
        {
            if (viewMode is 1 or 2) { button.MinHeight = 28; button.Padding = new Thickness(2); button.Margin = new Thickness(1); }
            bool digit = spec.Title.Length == 1 && char.IsDigit(spec.Title[0]) || spec.AccessibleName == "Decimal point"
                || spec.Title == "±" || viewMode == 2 && spec.Title.Length == 1 && spec.Title[0] is >= 'A' and <= 'F';
            button.Background = GetBrush(digit ? "CalculatorDigitBackground" : "CalculatorOperationBackground");
            if (viewMode == 1 && spec.Title == "2nd" && snapshot.ScientificShift)
                button.Background = new SolidColorBrush(ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 62, 69, 75) : Color.FromArgb(255, 215, 231, 247));
            if (spec.Title == "=")
            {
                button.Background = new SolidColorBrush(ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 76, 194, 255) : Color.FromArgb(255, 0, 103, 192));
                button.Foreground = new SolidColorBrush(ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
            }
        }
        button.Click += (_, _) => SendCommand(spec.Command);
        commandButtons.Add(button, spec.Command);
        return button;
    }

    private void SetButtonCaption(Button button, CalculatorButton spec, bool compact = false)
    {
        bool glyph = ButtonGlyphs.TryGetValue(spec.Title, out var symbol);
        button.FontFamily = glyph ? calculatorIcons : FontFamily;
        bool digit = spec.Title.Length == 1 && char.IsDigit(spec.Title[0]) || spec.AccessibleName == "Decimal point";
        bool arithmetic = spec.Title is "+" or "−" or "×" or "÷" or "=";
        button.FontSize = compact ? 12 : viewMode == 1
            ? digit ? scientificSize switch { 2 => 38, 1 => 24, _ => 18 }
                : spec.Title == "±" && scientificSize == 2 ? 48 : scientificSize switch { 2 => 34, 1 => 20, _ => 14 }
            : digit || arithmetic ? 20 : spec.Title is "±" or "(" or ")" ? 16 : glyph ? 20 : 16;
        if (button.Content is not TextBlock caption)
        {
            caption = new TextBlock { IsHitTestVisible = false, FontWeight = Microsoft.UI.Text.FontWeights.Normal };
            button.Content = caption;
        }
        caption.Text = glyph ? symbol : spec.Title;
        caption.FontFamily = button.FontFamily;
        caption.FontSize = button.FontSize;
    }

    private IReadOnlyList<IReadOnlyList<CalculatorButton>> GetKeypadRows()
    {
        if (viewMode != 2) return snapshot.ButtonRows;
        var buttons = snapshot.ButtonRows.SelectMany(row => row).ToDictionary(button => button.Command);
        // The programmer operator flyouts own bitwise commands. The main keypad follows the five-column source layout.
        int[][] commands =
        [
            [140, 82, 81, 83],
            [141, 128, 129, 95, 91],
            [142, 137, 138, 139, 92],
            [143, 134, 135, 136, 94],
            [144, 131, 132, 133, 93],
            [145, 80, 130, 84, 121]
        ];
        return commands.Select(row => (IReadOnlyList<CalculatorButton>)row.Select(command => buttons[command]).ToArray()).ToArray();
    }

    private void BuildCalculatorOptions(StackPanel controls)
    {
        if (viewMode == 1)
        {
            scientificOptions = new ScientificOptionsPanel(SendCommand);
            scientificOptions.Update(snapshot);
        }
        if (viewMode != 2) return;
        programmerOptions = new ProgrammerOptionsPanel(SendCommand);
        programmerOptions.Update(snapshot);
        programmerOptions.BitModeChanged += bits =>
        {
            if (numberKeypad is not null) numberKeypad.Visibility = bits ? Visibility.Collapsed : Visibility.Visible;
        };
        controls.Children.Add(programmerOptions);
    }

    private void BuildConverter(StackPanel controls)
    {
        fromUnit = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        toUnit = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        fromUnit.ItemsSource = snapshot.Units;
        toUnit.ItemsSource = snapshot.Units;
        fromUnit.SelectedIndex = snapshot.FromUnitIndex;
        toUnit.SelectedIndex = snapshot.ToUnitIndex;
        fromUnit.SelectionChanged += (_, _) => SelectUnits();
        toUnit.SelectionChanged += (_, _) => SelectUnits();
        AutomationProperties.SetName(fromUnit, "Convert from unit");
        AutomationProperties.SetName(toUnit, "Convert to unit");
        var units = new Grid { ColumnSpacing = 8 };
        units.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        units.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        units.Children.Add(fromUnit);
        Grid.SetColumn(toUnit, 1);
        units.Children.Add(toUnit);
        controls.Children.Add(units);
        if (snapshot.CategoryIndex == CurrencyCategory)
        {
            var status = snapshot.Currency;
            string dates = status.ReferenceDate == status.ReferenceEndDate || status.ReferenceEndDate.Length == 0
                ? status.ReferenceDate : $"{status.ReferenceDate} to {status.ReferenceEndDate}";
            string description = status.Available ? $"{status.Provider}: {dates}" : "Download currency rates";
            if (status.Status == "cached") description += " (saved)";
            if (status.Status == "loading") description = "Updating rates…";
            var rateInfo = new Grid { ColumnSpacing = 4 };
            rateInfo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rateInfo.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var source = new HyperlinkButton { Content = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(0) };
            if (status.SourceUrl.Length > 0) source.NavigateUri = new Uri(status.SourceUrl);
            ToolTipService.SetToolTip(source, status.Attribution + (status.Error.Length > 0 ? "\n" + status.Error : ""));
            rateInfo.Children.Add(source);
            var update = new Button { Content = new FontIcon { Glyph = "\uE72C", FontSize = 16 }, IsEnabled = !refreshingRates, MinHeight = 28, MinWidth = 28, Padding = new Thickness(4) };
            AutomationProperties.SetName(update, "Update rates");
            ToolTipService.SetToolTip(update, "Update rates");
            update.Click += async (_, _) => await RefreshRatesAsync(true);
            Grid.SetColumn(update, 1);
            rateInfo.Children.Add(update);
            controls.Children.Add(rateInfo);
        }
    }

    private async Task RefreshRatesAsync(bool forceRefresh = false)
    {
        if (closed || refreshingRates) return;
        refreshingRates = true;
        calculator.SetCurrencyStatus(1);
        snapshot = calculator.GetSnapshot();
        if (viewMode == 3 && snapshot.CategoryIndex == CurrencyCategory) BuildBody();
        try
        {
            var response = await currencyRates.LoadAsync(forceRefresh, closing.Token);
            if (closed) return;
            if (response.Snapshot is { } rates)
            {
                if (!calculator.SetCurrencyRates(rates.ProviderId, rates.ReferenceDateText, rates.ReferenceEndDateText,
                    rates.FetchedUtc, rates.ToRateLines(), response.IsFromCache))
                    calculator.SetCurrencyStatus(4, calculator.LastError);
                else if (response.Error.Length > 0)
                    calculator.SetCurrencyStatus(4, response.Error);
            }
            else calculator.SetCurrencyStatus(4, response.Error);
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!closed) calculator.SetCurrencyStatus(4, error.Message);
        }
        finally
        {
            refreshingRates = false;
            if (!closed)
            {
                snapshot = calculator.GetSnapshot();
                if (viewMode == 3 && snapshot.CategoryIndex == CurrencyCategory) BuildBody();
            }
        }
    }

    private void SelectUnits()
    {
        if (refreshing || fromUnit.SelectedIndex < 0 || toUnit.SelectedIndex < 0) return;
        if (!calculator.SetUnits(fromUnit.SelectedIndex, toUnit.SelectedIndex)) ShowError(calculator.LastError);
        else RefreshDisplay();
    }

    private void SendCommand(int command)
    {
        if (!calculator.SendCommand(command)) { ShowError(calculator.LastError); return; }
        RefreshDisplay();
    }

    private void RefreshDisplay()
    {
        if (viewMode >= 4) return;
        var previousShift = snapshot.ScientificShift;
        snapshot = calculator.GetSnapshot();
        if (viewMode == 1 && snapshot.ScientificShift != previousShift)
        {
            BuildBody();
            return;
        }
        result.Text = snapshot.Primary;
        expression.Text = snapshot.Expression;
        converted.Text = snapshot.Converted;
        notice.Text = snapshot.Notice;
        notice.Visibility = notice.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var specs = snapshot.ButtonRows.SelectMany(row => row).Concat(snapshot.MemoryButtons).Concat(snapshot.AngleButtons)
            .Concat(snapshot.BaseButtons).Concat(snapshot.WordButtons).GroupBy(spec => spec.Command).ToDictionary(group => group.Key, group => group.Last());
        foreach (var entry in commandButtons)
            if (specs.TryGetValue(entry.Value, out var spec))
            {
                entry.Key.IsEnabled = spec.Enabled;
                SetButtonCaption(entry.Key, spec, snapshot.MemoryButtons.Any(memory => memory.Command == entry.Value));
            }
        if (memoryDropdown is not null) memoryDropdown.IsEnabled = snapshot.Memory.Count > 0;
        scientificOptions?.Update(snapshot);
        programmerOptions?.Update(snapshot);
    }

    private void ShowError(string message)
    {
        notice.Text = message;
        notice.Visibility = Visibility.Visible;
    }

    private MenuFlyout CreateClipboardMenu(Func<string> getText, bool allowPaste)
    {
        var menu = new MenuFlyout();
        var copy = new MenuFlyoutItem { Text = "Copy" };
        copy.Click += (_, _) => CopyText(getText());
        menu.Items.Add(copy);
        if (allowPaste)
        {
            var paste = new MenuFlyoutItem { Text = "Paste" };
            paste.Click += async (_, _) => await PasteAsync();
            menu.Items.Add(paste);
        }
        return menu;
    }

    private static void CopyText(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }

    private async Task PasteAsync()
    {
        try
        {
            var data = Clipboard.GetContent();
            if (!data.Contains(StandardDataFormats.Text)) return;
            string text = await data.GetTextAsync();
            if (closed) return;
            if (!calculator.PasteNumber(text)) ShowError(calculator.LastError);
            else RefreshDisplay();
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private async void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (navigation.IsPaneOpen)
        {
            if (args.Key == VirtualKey.Escape) { navigation.IsPaneOpen = false; args.Handled = true; }
            return;
        }
        if (viewMode >= 4 || args.Handled) return;
        if (XamlRoot is null || FocusManager.GetFocusedElement(XamlRoot) is TextBox or ComboBox or NumberBox) return;
        var core = CoreWindow.GetForCurrentThread();
        if (core is null) return;
        bool modifier = core.GetKeyState(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down)
            || core.GetKeyState(VirtualKey.LeftWindows).HasFlag(CoreVirtualKeyStates.Down)
            || core.GetKeyState(VirtualKey.RightWindows).HasFlag(CoreVirtualKeyStates.Down);
        if (modifier && args.Key == VirtualKey.C)
        {
            CopyText(snapshot.Primary);
            args.Handled = true;
            return;
        }
        if (modifier && args.Key == VirtualKey.V)
        {
            await PasteAsync();
            args.Handled = true;
            return;
        }
        bool shift = core.GetKeyState(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        if (modifier && (args.Key is VirtualKey.H or VirtualKey.M) && viewMode < 3)
        {
            ShowDetails(args.Key == VirtualKey.M);
            args.Handled = true;
            return;
        }
        if (modifier && shift && args.Key == VirtualKey.D && viewMode < 3)
        {
            calculator.ClearHistory();
            RefreshDisplay();
            args.Handled = true;
            return;
        }
        var shortcut = GetShortcutCommand(args.Key, modifier, shift);
        if (shortcut is { } command)
        {
            SendCommand(command);
            args.Handled = true;
            return;
        }
        if (modifier) return;
        string? key = args.Key switch
        {
            VirtualKey.Number1 when shift => "!", VirtualKey.Number2 when shift => "@", VirtualKey.Number3 when shift => "#",
            VirtualKey.Number5 when shift => "%", VirtualKey.Number6 when shift => "^",
            VirtualKey.Number7 when shift => "&",
            VirtualKey.Number8 when shift => "*", VirtualKey.Number9 when shift => "(", VirtualKey.Number0 when shift => ")",
            >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)('0' + args.Key - VirtualKey.Number0)).ToString(),
            >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 => ((char)('0' + args.Key - VirtualKey.NumberPad0)).ToString(),
            >= VirtualKey.A and <= VirtualKey.Z when viewMode is 0 or 1 or 2 => args.Key.ToString(),
            VirtualKey.Add => "+", VirtualKey.Subtract => "-", VirtualKey.Multiply => "*", VirtualKey.Divide => "/",
            VirtualKey.Decimal => CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator,
            VirtualKey.Enter => "\n", VirtualKey.Escape => "\x1b", VirtualKey.Back => "\b",
            (VirtualKey)187 => shift ? "+" : "=", (VirtualKey)189 when !shift => "-",
            (VirtualKey)190 => shift ? ">" : ".", (VirtualKey)188 => shift ? "<" : ",", (VirtualKey)191 when !shift => "/",
            (VirtualKey)219 when !shift => "[", (VirtualKey)221 when !shift => "]", (VirtualKey)220 => shift ? "|" : "\\",
            (VirtualKey)192 when shift => "~", _ => null
        };
        if (key is null) return;
        if (viewMode == 2 && (key is "<" or ">") && programmerOptions?.TryHandleShiftKey(key[0]) == true)
        {
            args.Handled = true;
            return;
        }
        if (calculator.PressKey(key)) { RefreshDisplay(); args.Handled = true; }
    }

    private int? GetShortcutCommand(VirtualKey key, bool modifier, bool shift)
    {
        if (modifier && !shift && viewMode < 3)
        {
            int? memory = key switch { VirtualKey.L => 122, VirtualKey.R => 123, VirtualKey.P => 125, VirtualKey.Q => 126, _ => null };
            if (memory is not null) return memory;
        }
        if (viewMode == 1)
        {
            int trigIndex = key switch { VirtualKey.S => 0, VirtualKey.O => 1, VirtualKey.T => 2, VirtualKey.U => 3, VirtualKey.I => 4, VirtualKey.J => 5, _ => -1 };
            if (trigIndex >= 0 && (modifier || shift))
            {
                int[] commands = (modifier, shift) switch
                {
                    (true, true) => [206, 207, 208, 407, 409, 411],
                    (true, false) => [105, 106, 107, 406, 408, 410],
                    _ => [202, 203, 204, 401, 403, 405]
                };
                return commands[trigIndex];
            }
            if (modifier && !shift) return key switch { VirtualKey.G => 117, VirtualKey.N => 205, VirtualKey.Y => 96, VirtualKey.D => 324, _ => null };
            if (!modifier && shift) return key switch { VirtualKey.R => 600, VirtualKey.E => 601, VirtualKey.L => 500, _ => null };
            if (!modifier) return key switch { VirtualKey.F3 => 323, VirtualKey.F4 => 321, VirtualKey.F5 => 322, VirtualKey.F9 => 80, VirtualKey.Delete => 82, _ => null };
        }
        if (!modifier && viewMode == 2) return key switch
        {
            VirtualKey.F2 => 317, VirtualKey.F3 => 318, VirtualKey.F4 => 319, VirtualKey.F12 => 320,
            VirtualKey.F5 => 313, VirtualKey.F6 => 314, VirtualKey.F7 => 315, VirtualKey.F8 => 316,
            VirtualKey.F9 => 80, VirtualKey.Delete => 82, _ => null
        };
        if (!modifier) return key switch { VirtualKey.F9 => 80, VirtualKey.Delete => 82, _ => null };
        return null;
    }

    private void ShowDetails(bool selectMemory = false)
    {
        detailsFlyout?.Hide();
        snapshot = calculator.GetSnapshot();
        var entries = new StackPanel { Spacing = 8, Width = 300 };
        var tabs = new Pivot();
        var history = new StackPanel { Spacing = 6 };
        foreach (var item in snapshot.History.Select((value, index) => (value, index)))
        {
            var row = new StackPanel { Spacing = 4 };
            var recall = new Button { Content = item.value.Expression + "\n" + item.value.Result, HorizontalAlignment = HorizontalAlignment.Stretch };
            recall.ContextFlyout = CreateClipboardMenu(() => item.value.Result, false);
            recall.Click += (_, _) => { calculator.RecallHistory(item.index); RefreshDisplay(); };
            var remove = new Button { Content = "Delete", HorizontalAlignment = HorizontalAlignment.Right };
            remove.Click += (_, _) => { calculator.RemoveHistory(item.index); detailsFlyout?.Hide(); RefreshDisplay(); };
            row.Children.Add(recall); row.Children.Add(remove); history.Children.Add(row);
        }
        if (snapshot.History.Count == 0) history.Children.Add(new TextBlock { Text = "No history yet" });
        var clear = new Button { Content = "Clear history" };
        clear.Click += (_, _) => { calculator.ClearHistory(); detailsFlyout?.Hide(); RefreshDisplay(); };
        history.Children.Add(clear);
        tabs.Items.Add(new PivotItem { Header = "History", Content = history });
        var memory = new StackPanel { Spacing = 8 };
        for (int index = 0; index < snapshot.Memory.Count; index++)
        {
            int slot = index;
            memory.Children.Add(new TextBlock { Text = snapshot.Memory[index], FontSize = 24 });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            string[] titles = ["Recall", "M+", "M−", "Delete"];
            for (int operation = 0; operation < titles.Length; operation++)
            {
                int action = operation;
                var button = new Button { Content = titles[operation] };
                button.Click += (_, _) => { calculator.MemoryAction(action, slot); detailsFlyout?.Hide(); RefreshDisplay(); };
                actions.Children.Add(button);
            }
            memory.Children.Add(actions);
        }
        if (snapshot.Memory.Count == 0) memory.Children.Add(new TextBlock { Text = "No stored memory" });
        tabs.Items.Add(new PivotItem { Header = "Memory", Content = memory });
        tabs.SelectedIndex = selectMemory ? 1 : 0;
        entries.Children.Add(tabs);
        detailsFlyout = new Flyout { Content = new ScrollViewer { Content = entries, MaxHeight = 460 } };
        detailsFlyout.ShowAt(details);
    }

    private ScrollViewer CreateSettings()
    {
        var settings = new StackPanel { Spacing = 20, Margin = new Thickness(12, 20, 12, 12) };
        var appearance = new ComboBox { Header = "Appearance", ItemsSource = new[] { "Use system setting", "Light", "Dark" },
            SelectedIndex = RequestedTheme switch { ElementTheme.Light => 1, ElementTheme.Dark => 2, _ => 0 },
            HorizontalAlignment = HorizontalAlignment.Stretch };
        appearance.SelectionChanged += (_, _) =>
        {
            RequestedTheme = appearance.SelectedIndex switch
            {
                1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default
            };
            ThemePreferences.SaveTheme(RequestedTheme);
        };
        settings.Children.Add(appearance);
        var about = new Button { Content = "About Calculator", HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left };
        about.Click += async (_, _) => await ShowAboutAsync();
        settings.Children.Add(about);
        var info = calculator.GetAbout();
        settings.Children.Add(new TextBlock { Text = "Version " + info.Version, Opacity = 0.8 });
        settings.Children.Add(new TextBlock { Text = info.Copyright, FontSize = 13, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        settings.Children.Add(new HyperlinkButton { Content = "Send feedback", NavigateUri = new Uri("https://github.com/sabino/calculator/issues") });
        settings.Children.Add(new HyperlinkButton { Content = "Contribute to Calculator", NavigateUri = new Uri("https://github.com/sabino/calculator") });
        return new ScrollViewer { Content = settings, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private async Task ShowAboutAsync()
    {
        var info = calculator.GetAbout();
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/calculator.png")), Width = 96, Height = 96 });
        content.Children.Add(new TextBlock { Text = $"Version {info.Version}", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = info.Copyright, TextWrapping = TextWrapping.Wrap });
        if (info.GraphingEnabled) content.Children.Add(new TextBlock { Text = $"Portable graphing engine {info.GraphingVersion}" });
        content.Children.Add(new HyperlinkButton { Content = "Microsoft Windows Calculator source", NavigateUri = new Uri(info.SourceUrl) });
        content.Children.Add(new HyperlinkButton { Content = "Microsoft privacy statement", NavigateUri = new Uri(info.PrivacyUrl) });
        content.Children.Add(new HyperlinkButton { Content = "Microsoft services agreement", NavigateUri = new Uri(info.ServicesUrl) });
        content.Children.Add(new Expander { Header = "Microsoft source license", Content = new TextBlock { Text = info.License, TextWrapping = TextWrapping.Wrap } });
        var dialog = new ContentDialog { Title = "About Calculator", Content = new ScrollViewer { Content = content, MaxHeight = 440 },
            CloseButtonText = "Close", XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }

    private static void InvokeButton(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider provider) throw new InvalidOperationException("Button has no invoke provider.");
        provider.Invoke();
    }

    private void InvokeKey(string title)
    {
        int command = snapshot.ButtonRows.SelectMany(row => row).First(button => button.Title == title).Command;
        InvokeButton(commandButtons.First(button => button.Value == command).Key);
    }

    public async Task RunSmokeChecksAsync()
    {
        await navigation.RunSmokeChecksAsync();
        var originalTheme = RequestedTheme;
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                RequestedTheme = theme;
                await Task.Delay(50);
                UpdateLayout();
                if (!ApplyWindowTheme()) throw new InvalidOperationException("Windows could not apply the Calculator title-bar theme.");
            }
        }
        finally { RequestedTheme = originalTheme; }
        await Task.Delay(100);
        SetMode(0);
        InvokeKey("2");
        InvokeKey("+");
        InvokeKey("3");
        InvokeKey("=");
        if (result.Text != "5") throw new InvalidOperationException("Shared keypad arithmetic failed.");
        SetMode(1);
        if (commandButtons.Count != 40 || scientificOptions is null)
            throw new InvalidOperationException("Scientific keypad is incomplete.");
        await scientificOptions.RunSmokeChecksAsync(calculator.GetSnapshot);
        InvokeKey("C");
        InvokeKey("3");
        InvokeKey("2nd");
        InvokeKey("x³");
        if (result.Text != "27") throw new InvalidOperationException("Scientific second-function keys failed.");
        InvokeKey("2nd");
        foreach (var size in new[] { new SizeInt32 { Width = 320, Height = 500 }, new SizeInt32 { Width = 420, Height = 900 },
            new SizeInt32 { Width = 900, Height = 1400 }, new SizeInt32 { Width = 420, Height = 680 } })
        {
            window.AppWindow.Resize(size);
            await Task.Delay(100);
            UpdateLayout();
            var angleBounds = scientificOptions!.AngleControls.TransformToVisual(body).TransformBounds(new Rect(0, 0,
                scientificOptions.AngleControls.ActualWidth, scientificOptions.AngleControls.ActualHeight));
            var menuBounds = scientificOptions.TransformToVisual(body).TransformBounds(new Rect(0, 0,
                scientificOptions.ActualWidth, scientificOptions.ActualHeight));
            var keyBounds = numberKeypad!.TransformToVisual(body).TransformBounds(new Rect(0, 0,
                numberKeypad.ActualWidth, numberKeypad.ActualHeight));
            if (angleBounds.Height < 32 || menuBounds.Top < angleBounds.Bottom || keyBounds.Top < menuBounds.Bottom - 1
                || keyBounds.Height < 7 * 28 || keyBounds.Bottom > body.ActualHeight + 1)
                throw new InvalidOperationException("Scientific controls overlap or the keypad is clipped after resizing.");
        }
        SetMode(2);
        if (programmerOptions?.RunSmokeChecks() != true)
            throw new InvalidOperationException("Programmer controls failed. " + programmerOptions?.LastSmokeFailure);
        SetMode(3);
        if (snapshot.Categories.Count != 13 || fromUnit.Items.Count == 0) throw new InvalidOperationException("Converter controls are incomplete.");
        if (!calculator.SetCurrencyRates("2026-10-09", "2026-10-12T12:00:00Z", "EUR=1\nUSD=1.1206\nBRL=5.6086"))
            throw new InvalidOperationException(calculator.LastError);
        SetCategory(CurrencyCategory);
        fromUnit.SelectedIndex = snapshot.Units.ToList().IndexOf("EUR");
        toUnit.SelectedIndex = snapshot.Units.ToList().IndexOf("USD");
        if (!calculator.PasteNumber("1")) throw new InvalidOperationException(calculator.LastError);
        RefreshDisplay();
        string expectedCurrency = "1" + CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator + "1206";
        if (!converted.Text.StartsWith(expectedCurrency, StringComparison.Ordinal))
            throw new InvalidOperationException($"Currency controls expected {expectedCurrency}, got {converted.Text}.");
        SetMode(4);
        if (datePanel?.RunSmokeChecks() != true) throw new InvalidOperationException("Date controls failed.");
        if (calculator.GetAbout().GraphingEnabled)
        {
            SetMode(5);
            await Task.Delay(100);
            UpdateLayout();
            if (graphPanel?.RunSmokeChecks() != true)
                throw new InvalidOperationException("Shared Skia graph controls failed. " + graphPanel?.LastSmokeFailure);
        }
        SetMode(0);
    }

    public void CloseSession()
    {
        if (closed) return;
        closed = true;
        closing.Cancel();
        currencyRates.Dispose();
        graph?.Dispose();
        calculator.Dispose();
    }
}
