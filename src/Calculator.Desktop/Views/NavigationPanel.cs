// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using System.Diagnostics;
using Windows.Foundation;

namespace CalculatorApp.Desktop;

public sealed class NavigationPanel : Grid
{
    // NavCategory.CategoryManifest glyphs, in the native adapter's converter order.
    private static readonly string[] CategoryGlyphs =
    [
        "\uF1AA", "\uECC6", "\uF4C1", "\uE7A3", "\uECAD", "\uE809",
        "\uEADA", "\uE917", "\uE945", "\uF20F", "\uEC4A", "\uF515"
    ];
    private static readonly FontFamily CalculatorFont =
        new("ms-appx:///Assets/CalculatorIcons.ttf#Calculator Fluent Icons");
    private readonly Action<int> _selectMode;
    private readonly Action<int> _selectCategory;
    private readonly Action _showSettings;

    private readonly NavigationView _menu = new()
    {
        PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
        IsPaneOpen = true, IsPaneToggleButtonVisible = false,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        OpenPaneLength = 256, Width = 256, IsSettingsVisible = true
    };
    private readonly ContentPresenter _content = new();
    private readonly Grid _overlay = new() { Opacity = 0, IsHitTestVisible = false };
    private readonly TranslateTransform _offset = new() { X = -256 };
    private readonly Button _toggle = CreateToggle();
    private readonly Border _dismiss = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly DispatcherQueueTimer _transition = DispatcherQueue.GetForCurrentThread().CreateTimer();
    private long _transitionStarted;
    private double _transitionFrom;
    private bool _open;
    private bool _settings;
    public UIElement? Content { get => _content.Content as UIElement; set => _content.Content = value; }
    public bool IsPaneOpen { get => _open; set => SetPaneOpen(value); }
    public void SetSettingsMode(bool settings) { _settings = settings; _toggle.Content = new FontIcon { Glyph = settings ? "\uE72B" : "\uE700", FontSize = settings ? 16 : 12 }; AutomationProperties.SetName(_toggle, settings ? "Back" : "Open navigation"); }
    public event Action? BackRequested;

    private static Button CreateToggle() => new()
    {
        Content = new FontIcon { Glyph = "\uE700", FontSize = 12 }, Width = 48, Height = 48,
        Padding = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
    };

    private void SetPaneOpen(bool open)
    {
        if (_open == open) return;
        _open = open;
        // Keep the pane measured offscreen. Visibility/layout changes between the
        // SplitView's keyframes otherwise expose its untranslated first frame.
        _transition.Stop();
        _transitionFrom = _offset.X;
        _transitionStarted = Stopwatch.GetTimestamp();
        _overlay.Opacity = 1;
        _overlay.IsHitTestVisible = open;
        if (open) _menu.IsEnabled = true;
        _transition.Start();
        AutomationProperties.SetName(_toggle, open ? "Close navigation" : "Open navigation");
    }

    private enum EntryKind { Mode, Category }
    private readonly record struct Entry(EntryKind Kind, int Index = 0);

    public NavigationPanel(
        IReadOnlyList<string> categories,
        bool graphingEnabled,
        Action<int> selectMode,
        Action<int> selectCategory,
        Action showSettings)
    {
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(selectMode);
        ArgumentNullException.ThrowIfNull(selectCategory);
        if (categories.Count != CategoryGlyphs.Length + 1)
            throw new ArgumentException("Converter categories must contain twelve static categories and Currency.", nameof(categories));
        foreach (var category in categories)
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("Converter names must contain text.", nameof(categories));

        _selectMode = selectMode;
        _selectCategory = selectCategory;
        ArgumentNullException.ThrowIfNull(showSettings);
        _showSettings = showSettings;
        _transition.Interval = TimeSpan.FromMilliseconds(16);
        _transition.Tick += (_, _) =>
        {
            double progress = Math.Clamp(Stopwatch.GetElapsedTime(_transitionStarted).TotalMilliseconds / 350, 0, 1);
            double eased = 1 - Math.Pow(1 - progress, 3);
            _offset.X = _transitionFrom + ((_open ? 0 : -256) - _transitionFrom) * eased;
            if (progress >= 1) { _transition.Stop(); _overlay.Opacity = _open ? 1 : 0; _menu.IsEnabled = _open; }
        };
        Unloaded += (_, _) => _transition.Stop();
        _menu.IsEnabled = false;
        Children.Add(_content);
        Children.Add(_toggle);
        Children.Add(_overlay);
        _overlay.Children.Add(_dismiss);
        var drawer = new Grid { Width = 256, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = _offset };
        drawer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        drawer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var close = CreateToggle();
        AutomationProperties.SetName(close, "Close navigation");
        close.Click += (_, _) => IsPaneOpen = false;
        drawer.Children.Add(close);
        Grid.SetRow(_menu, 1);
        drawer.Children.Add(_menu);
        _overlay.Children.Add(drawer);
        _dismiss.PointerPressed += (_, _) => IsPaneOpen = false;
        _toggle.Click += (_, _) => { if (_settings) BackRequested?.Invoke(); else IsPaneOpen = !IsPaneOpen; };
        void RefreshBackground()
        {
            var theme = ActualTheme == ElementTheme.Light ? "Light" : "Dark";
            var resources = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[theme];
            drawer.Background = (Brush)resources["CalculatorBackground"];
            _menu.Background = drawer.Background;
            _menu.Resources["NavigationViewDefaultPaneBackground"] = drawer.Background;
        }
        Loaded += (_, _) => RefreshBackground();
        ActualThemeChanged += (_, _) => RefreshBackground();
        SizeChanged += (_, args) => Clip = new RectangleGeometry { Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height) };
        AutomationProperties.SetName(this, "Calculator modes and converters");

        _menu.MenuItems.Add(CreateHeading("Calculator"));
        _menu.MenuItems.Add(CreateItem("Standard", "\uE8EF", new Entry(EntryKind.Mode, 0)));
        _menu.MenuItems.Add(CreateItem("Scientific", "\uF196", new Entry(EntryKind.Mode, 1)));
        var graphing = CreateItem("Graphing", "\uF770", new Entry(EntryKind.Mode, 5));
        graphing.IsEnabled = graphingEnabled;
        _menu.MenuItems.Add(graphing);
        _menu.MenuItems.Add(CreateItem("Programmer", "\uECCE", new Entry(EntryKind.Mode, 2)));
        _menu.MenuItems.Add(CreateItem("Date calculation", "\uE787", new Entry(EntryKind.Mode, 4)));
        _menu.MenuItems.Add(CreateHeading("Converter"));
        _menu.MenuItems.Add(CreateItem(categories[CategoryGlyphs.Length], "\uEB0D", new Entry(EntryKind.Category, CategoryGlyphs.Length)));
        for (var index = 0; index < CategoryGlyphs.Length; index++)
            _menu.MenuItems.Add(CreateItem(categories[index], CategoryGlyphs[index], new Entry(EntryKind.Category, index)));

        _menu.ItemInvoked += OnItemInvoked;
    }

    private static NavigationViewItemHeader CreateHeading(string title)
    {
        var header = new NavigationViewItemHeader { Content = title };
        AutomationProperties.SetName(header, title);
        AutomationProperties.SetHeadingLevel(header, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        return header;
    }

    private static NavigationViewItem CreateItem(string title, string glyph, Entry entry, bool selectsOnInvoked = true)
    {
        var icon = new FontIcon { Glyph = glyph };
        if (entry.Kind is EntryKind.Mode or EntryKind.Category)
            icon.FontFamily = CalculatorFont;
        var item = new NavigationViewItem
        {
            Content = title,
            Icon = icon,
            Tag = entry,
            SelectsOnInvoked = selectsOnInvoked
        };
        AutomationProperties.SetName(item, title);
        return item;
    }

    private void OnItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        IsPaneOpen = false;
        if (args.IsSettingsInvoked)
        {
            _showSettings();
            return;
        }
        if (args.InvokedItemContainer is not NavigationViewItem { IsEnabled: true, Tag: Entry entry })
            return;
        switch (entry.Kind)
        {
            case EntryKind.Mode:
                _selectMode(entry.Index);
                break;
            case EntryKind.Category:
                _selectMode(3);
                _selectCategory(entry.Index);
                break;
        }
    }

    internal void SelectView(int mode, int category)
    {
        if (mode == 6) { _menu.SelectedItem = _menu.SettingsItem; return; }
        foreach (var item in _menu.MenuItems)
            if (item is NavigationViewItem { Tag: Entry entry } navigationItem
                && (mode == 3 ? entry.Kind == EntryKind.Category && entry.Index == category
                    : entry.Kind == EntryKind.Mode && entry.Index == mode))
            {
                _menu.SelectedItem = navigationItem;
                return;
            }
    }

    internal async System.Threading.Tasks.Task RunSmokeChecksAsync()
    {
        UpdateLayout();
        var content = Content as FrameworkElement ?? throw new InvalidOperationException("Navigation has no content.");
        var before = content.TransformToVisual(this).TransformPoint(new Point());
        for (var cycle = 0; cycle < 3; cycle++)
        {
            InvokeToggle();
            double previous = -256;
            bool moved = false;
            for (var frame = 0; frame < 26; frame++)
            {
                await System.Threading.Tasks.Task.Delay(16);
                UpdateLayout();
                double current = _offset.X;
                if (current < previous - 1 || current > 1 || current < -257)
                    throw new InvalidOperationException("Navigation jumped during its opening transition.");
                moved |= current > -255 && current < -1;
                previous = current;
                var position = content.TransformToVisual(this).TransformPoint(new Point());
                if (Math.Abs(before.X - position.X) > 1 || Math.Abs(before.Y - position.Y) > 1)
                    throw new InvalidOperationException("Navigation moved calculator content during its transition.");
            }
            if (!IsPaneOpen || !moved || Math.Abs(_offset.X) > 1)
                throw new InvalidOperationException("Navigation did not smoothly open.");
            InvokeToggle();
            await System.Threading.Tasks.Task.Delay(400);
            if (IsPaneOpen || Math.Abs(_offset.X + 256) > 1 || _overlay.IsHitTestVisible)
                throw new InvalidOperationException("Navigation did not close after a second click.");
        }
        InvokeToggle();
        await System.Threading.Tasks.Task.Delay(50);
        InvokeToggle();
        await System.Threading.Tasks.Task.Delay(400);
        if (IsPaneOpen || Math.Abs(_offset.X + 256) > 1)
            throw new InvalidOperationException("Navigation failed to reverse an interrupted transition.");
    }

    private void InvokeToggle()
    {
        var peer = new ButtonAutomationPeer(_toggle);
        if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider provider)
            throw new InvalidOperationException("Navigation toggle has no invoke provider.");
        provider.Invoke();
    }
}
