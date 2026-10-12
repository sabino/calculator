// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace CalculatorApp.Desktop;

public sealed class GraphPanel : Grid
{
    private static readonly (string Name, string Color)[] Palette =
    [
        ("Blue", "#0078D4"), ("Red", "#DC2828"), ("Green", "#199141"),
        ("Purple", "#8C46BE"), ("Orange", "#DC8214"), ("Black", "#1E1E1E")
    ];

    private readonly NativeGraph _graph;
    private readonly GraphRange _defaultRange;
    private readonly string _selectionGroup = $"GraphEquations-{Guid.NewGuid():N}";
    private readonly GraphCanvas _canvas = new();
    private readonly Dictionary<int, TextBox> _editors = new();
    private readonly Dictionary<int, RadioButton> _selectionControls = new();
    private readonly Dictionary<int, string> _drafts = new();
    private string? _pendingEquation;
    private TextBox? FocusedEditor => _editors.GetValueOrDefault(_focusedRow);
    private int _focusedRow = -1;
    private readonly GraphingKeypad _keypad;
    private readonly StackPanel _compactTabs = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly ToggleButton _graphTab = new() { Content = "Graph", IsChecked = true };
    private readonly ToggleButton _equationsTab = new() { Content = "Equations" };
    private readonly ToggleButton _traceToggle = new() { Content = "Trace", IsChecked = true };
    private readonly ToggleButton _activeTraceToggle = new() { Content = "Active trace" };
    private readonly Dictionary<uint, CheckBox> _featureControls = new();
    private double _traceX;
    private double _traceY;
    private InMemoryRandomAccessStream? _clipboardImage;
    private readonly StackPanel _equationRows = new() { Spacing = 10 };
    private readonly TextBlock _analysis = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _inspector = new() { Text = "Move over a curve to inspect its coordinates.", TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _angle = new() { ItemsSource = new[] { "Radians", "Degrees", "Gradians" }, SelectedIndex = 0, MinWidth = 125 };
    private readonly TextBox[] _ranges = Enumerable.Range(0, 4).Select(_ => new TextBox { MinWidth = 66, MaxWidth = 100 }).ToArray();
    private readonly ScrollViewer _optionsScroll;
    private readonly Grid _plotArea;
    private GraphSnapshot? _snapshot;
    private readonly Dictionary<(int Row, string Name), (TextBox Value, Slider Slider)> _parameterControls = new();
    private int _rowControlVersion;
    private bool _mutating;
    private bool _refreshing;
    private string _lastMutationError = "";
    private string _sceneError = "";
    private GraphScene? _preparedScene;
    public string LastSmokeFailure { get; private set; } = "";
    private bool _sceneRefreshQueued;
    private bool _dragging;
    private Point _dragPoint;
    private long _lastTraceTime;
    private bool _followAppTheme;

    public GraphPanel(NativeGraph graph)
    {
        _graph = graph;
        RowSpacing = 12;
        ColumnSpacing = 16;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var options = new StackPanel { Spacing = 10 };
        options.Children.Add(Heading("Equations"));
        options.Children.Add(_equationRows);
        options.Children.Add(Button("Add equation", AddEquation));
        options.Children.Add(new TextBlock
        {
            Text = "Use x, y, or a named parameter, such as y=a*x^2.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75
        });
        _keypad = new GraphingKeypad(InsertText, Backspace, ClearEquation, SubmitFocusedEquation, () => FocusedEditor is not null);
        options.Children.Add(_keypad);
        options.Children.Add(Heading("Analysis"));
        var markers = new StackPanel { Spacing = 2 };
        string[] markerNames = ["Zeros", "Y-intercept", "Minima", "Maxima", "Inflection points", "Vertical asymptotes", "Horizontal asymptotes", "Oblique asymptotes"];
        for (var index = 0; index < markerNames.Length; index++)
        {
            var bit = 1u << index;
            var check = new CheckBox { Content = markerNames[index] };
            AutomationProperties.SetName(check, $"Show {markerNames[index].ToLowerInvariant()} on graph");
            check.Click += (_, _) =>
            {
                if (_snapshot is null || _refreshing || _mutating) return;
                var mask = check.IsChecked == true ? _snapshot.Settings.FeaturesMask | bit : _snapshot.Settings.FeaturesMask & ~bit;
                Mutate(() => _graph.SetFeatures(mask), false, false);
            };
            _featureControls[bit] = check;
            markers.Children.Add(check);
        }
        options.Children.Add(new Expander { Header = "Show graph features", Content = markers, HorizontalAlignment = HorizontalAlignment.Stretch });
        options.Children.Add(_analysis);
        _optionsScroll = new ScrollViewer
        {
            Content = options,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 6, 0)
        };

        _plotArea = new Grid { RowSpacing = 8 };
        _plotArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _plotArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _plotArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _plotArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var toolbar = new StackPanel { Spacing = 6 };
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var displayOptions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        AutomationProperties.SetName(_angle, "Graph angle units");
        displayOptions.Children.Add(_angle);
        navigation.Children.Add(Button("+", () => Mutate(() => _graph.Zoom(1 / 1.0625), false, false), "Zoom in"));
        navigation.Children.Add(Button("−", () => Mutate(() => _graph.Zoom(1.0625), false, false), "Zoom out"));
        navigation.Children.Add(Button("Reset", ResetRange, "Reset view"));
        AutomationProperties.SetName(_traceToggle, "Inspect curve coordinates");
        _traceToggle.Unchecked += (_, _) => ClearTrace();
        navigation.Children.Add(_traceToggle);
        AutomationProperties.SetName(_activeTraceToggle, "Trace selected equation with arrow keys. Plus and minus zoom. Escape exits.");
        _activeTraceToggle.Checked += (_, _) =>
        {
            _traceToggle.IsChecked = false;
            _traceX = 0; _traceY = 0;
            UpdateActiveTrace();
            _activeTraceToggle.Focus(FocusState.Programmatic);
        };
        _activeTraceToggle.Unchecked += (_, _) => ClearTrace();
        _activeTraceToggle.KeyDown += OnGraphKeyDown;
        navigation.Children.Add(_activeTraceToggle);
        displayOptions.Children.Add(FlyoutButton("Settings", BuildGraphSettings));
        displayOptions.Children.Add(FlyoutButton("Share", BuildShareControls));
        toolbar.Children.Add(navigation);
        toolbar.Children.Add(displayOptions);
        _plotArea.Children.Add(toolbar);
        var graphBorder = new Border
        {
            Child = _canvas,
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 150, 150, 150)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        SetRow(graphBorder, 1);
        _plotArea.Children.Add(graphBorder);
        SetRow(_inspector, 2);
        _plotArea.Children.Add(_inspector);

        var rangeControls = new Grid { ColumnSpacing = 5 };
        for (var index = 0; index < 4; index++)
            rangeControls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rangeControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var rangeNames = new[] { "X min", "X max", "Y min", "Y max" };
        for (var index = 0; index < _ranges.Length; index++)
        {
            _ranges[index].Header = rangeNames[index];
            AutomationProperties.SetName(_ranges[index], rangeNames[index]);
            _ranges[index].KeyDown += (_, args) =>
            {
                if (args.Key == Windows.System.VirtualKey.Enter)
                {
                    ApplyRanges();
                    args.Handled = true;
                }
            };
            SetColumn(_ranges[index], index);
            rangeControls.Children.Add(_ranges[index]);
        }
        var applyRange = Button("Apply", ApplyRanges);
        applyRange.VerticalAlignment = VerticalAlignment.Bottom;
        SetColumn(applyRange, 4);
        rangeControls.Children.Add(applyRange);
        SetRow(rangeControls, 3);
        _plotArea.Children.Add(rangeControls);
        SetRow(_status, 2);
        SetColumnSpan(_status, 2);
        Children.Add(_status);
        Children.Add(_optionsScroll);
        Children.Add(_plotArea);
        _compactTabs.Children.Add(_graphTab);
        _compactTabs.Children.Add(_equationsTab);
        Children.Add(_compactTabs);
        SetColumnSpan(_compactTabs, 2);
        _graphTab.Click += (_, _) => ShowCompactTab(true);
        _equationsTab.Click += (_, _) => ShowCompactTab(false);
        SetColumn(_plotArea, 1);
        SetRow(_optionsScroll, 1);
        SetRow(_plotArea, 1);

        _angle.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && !_mutating && _snapshot is not null && _angle.SelectedIndex >= 0
                && _angle.SelectedIndex != _snapshot.AngleUnit)
                Mutate(() => _graph.Update(_snapshot.Equations.Select(row => row.Input), _angle.SelectedIndex));
        };
        _canvas.SizeChanged += (_, _) => QueueSceneRefresh();
        _canvas.PointerPressed += BeginPan;
        _canvas.PointerMoved += MovePointer;
        _canvas.PointerReleased += EndPan;
        _canvas.PointerCaptureLost += (_, _) => _dragging = false;
        _canvas.PointerExited += (_, _) =>
        {
            if (!_dragging && _activeTraceToggle.IsChecked != true)
                ClearTrace();
        };
        _canvas.PointerWheelChanged += ZoomPointer;
        KeyDown += OnGraphKeyDown;
        SizeChanged += (_, args) => ArrangePanels(args.NewSize.Width);
        Loaded += (_, _) =>
        {
            ArrangePanels(ActualWidth);
            QueueSceneRefresh();
        };
        ActualThemeChanged += (_, _) =>
        {
            if (!_followAppTheme || _snapshot is null) return;
            var colors = ThemeColors(ActualTheme == ElementTheme.Dark);
            Mutate(() => _graph.SetSettings(_snapshot.Settings.LineWidth, colors[0], colors[1], colors[2], colors[3]), false, false);
        };
        Mutate(() => ReplaceEquations(new[] { "y=x^2-4", "y=x" }, 0));
        _defaultRange = _snapshot?.Range ?? new GraphRange(-10, 10, -10, 10);
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };

    private static Button Button(string text, Action action, string? accessibleName = null)
    {
        var button = new Button { Content = text };
        AutomationProperties.SetName(button, accessibleName ?? text);
        button.Click += (_, _) => action();
        return button;
    }

    private static Button FlyoutButton(string text, Func<UIElement> content)
    {
        var button = new Button { Content = text };
        AutomationProperties.SetName(button, text);
        var flyout = new Flyout();
        flyout.Opening += (_, _) => flyout.Content = content();
        button.Flyout = flyout;
        return button;
    }

    private void ArrangePanels(double width)
    {
        var compact = width < 760;
        ColumnDefinitions[0].Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(330);
        ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        RowDefinitions[0].Height = GridLength.Auto;
        RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
        SetColumn(_plotArea, compact ? 0 : 1);
        _compactTabs.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        _optionsScroll.Visibility = compact && _graphTab.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        _plotArea.Visibility = compact && _graphTab.IsChecked != true ? Visibility.Collapsed : Visibility.Visible;
        _canvas.MinHeight = compact ? 250 : 300;
    }

    private void ShowCompactTab(bool graph)
    {
        _graphTab.IsChecked = graph;
        _equationsTab.IsChecked = !graph;
        ArrangePanels(ActualWidth);
        if (graph) QueueSceneRefresh();
    }

    private void AddEquation()
    {
        if (_snapshot is null) return;
        CaptureEquationDrafts();
        _pendingEquation ??= "y=";
        _focusedRow = _snapshot.Equations.Count;
        BuildEquationRows(_snapshot);
        ShowCompactTab(false);
        FocusedEditor?.Focus(FocusState.Programmatic);
        if (FocusedEditor is { } editor) editor.SelectionStart = editor.Text.Length;
    }

    private void InsertText(string text)
    {
        if (FocusedEditor is not { } editor) return;
        var start = Math.Clamp(editor.SelectionStart, 0, editor.Text.Length);
        var length = Math.Clamp(editor.SelectionLength, 0, editor.Text.Length - start);
        editor.Text = editor.Text.Remove(start, length).Insert(start, text);
        editor.SelectionStart = start + text.Length;
        editor.SelectionLength = 0;
        editor.Focus(FocusState.Programmatic);
    }

    private void Backspace()
    {
        if (FocusedEditor is not { } editor) return;
        if (editor.SelectionLength > 0) { InsertText(""); return; }
        var start = Math.Clamp(editor.SelectionStart, 0, editor.Text.Length);
        if (start == 0) return;
        var length = start > 1 && char.IsLowSurrogate(editor.Text[start - 1]) && char.IsHighSurrogate(editor.Text[start - 2]) ? 2 : 1;
        editor.SelectionStart = start - length;
        editor.SelectionLength = length;
        InsertText("");
    }

    private void ClearEquation()
    {
        if (FocusedEditor is not { } editor) return;
        editor.SelectAll();
        InsertText("");
    }

    private void SubmitFocusedEquation()
    {
        if (FocusedEditor is { } editor) SubmitEquation(_focusedRow, editor.Text);
    }

    private bool SubmitEquation(int row, string text)
    {
        if (_snapshot is null) return false;
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\n') || text.Contains('\r'))
        {
            _status.Text = "Enter one equation in this card. The previous graph is still shown.";
            return false;
        }
        var adding = row == _snapshot.Equations.Count && _pendingEquation is not null;
        var success = Mutate(() =>
        {
            if (adding) _pendingEquation = text;
            else _drafts[row] = text;
            var accepted = adding ? _graph.Update(_snapshot.Equations.Select(item => item.Input).Append(text), _snapshot.AngleUnit)
                : _graph.SetEquation(row, text);
            if (accepted)
            {
                if (adding) _pendingEquation = null;
                else _drafts.Remove(row);
            }
            return accepted;
        });
        if (success && adding) Mutate(() => _graph.SetSelected(row), false);
        return success;
    }

    private bool ReplaceEquations(IEnumerable<string> equations, int angle)
    {
        if (!_graph.Update(equations, angle)) return false;
        _drafts.Clear();
        _pendingEquation = null;
        return true;
    }

    private void CaptureEquationDrafts()
    {
        if (_snapshot is null) return;
        // TextChanged can arrive after a button action. Read live controls before replacing them.
        foreach (var item in _editors)
        {
            if (item.Key < _snapshot.Equations.Count)
            {
                if (item.Value.Text == _snapshot.Equations[item.Key].Input) _drafts.Remove(item.Key);
                else _drafts[item.Key] = item.Value.Text;
            }
            else if (item.Key == _snapshot.Equations.Count && _pendingEquation is not null)
                _pendingEquation = item.Value.Text;
        }
    }

    private void RemoveEquation(int row)
    {
        Mutate(() =>
        {
            if (!_graph.RemoveEquation(row)) return false;
            var remaining = _drafts.Where(item => item.Key != row).ToArray();
            _drafts.Clear();
            foreach (var item in remaining) _drafts[item.Key > row ? item.Key - 1 : item.Key] = item.Value;
            if (_focusedRow > row) _focusedRow--;
            else if (_focusedRow == row) _focusedRow = -1;
            return true;
        });
    }

    private bool Mutate(Func<bool> action, bool rebuildControls = true, bool refreshAnalysis = true)
    {
        if (_refreshing || _mutating)
        {
            _lastMutationError = "A graph update was attempted while controls were refreshing.";
            return false;
        }
        CaptureEquationDrafts();
        _mutating = true;
        _lastMutationError = "";
        try
        {
            if (!action())
            {
                var message = string.IsNullOrWhiteSpace(_graph.LastError)
                    ? "Check the equations or values. The previous graph is still shown."
                    : _graph.LastError;
                _lastMutationError = message;
                if (_snapshot is not null)
                    RefreshSnapshot(true, refreshAnalysis);
                _status.Text = message;
                return false;
            }
            _status.Text = "";
            RefreshSnapshot(rebuildControls, refreshAnalysis);
            RefreshScene();
            return true;
        }
        catch (Exception error)
        {
            _lastMutationError = error.ToString();
            _status.Text = $"The graph could not be updated. {error.Message}";
            return false;
        }
        finally
        {
            _mutating = false;
        }
    }

    private void RefreshSnapshot(bool rebuildControls, bool refreshAnalysis)
    {
        var snapshot = _graph.GetSnapshot();
        _snapshot = snapshot;
        _refreshing = true;
        try
        {
            _angle.SelectedIndex = snapshot.AngleUnit;
            foreach (var feature in _featureControls) feature.Value.IsChecked = (snapshot.Settings.FeaturesMask & feature.Key) != 0;
            double[] values = [snapshot.Range.Left, snapshot.Range.Right, snapshot.Range.Bottom, snapshot.Range.Top];
            for (var index = 0; index < _ranges.Length; index++)
                _ranges[index].Text = values[index].ToString("G8", CultureInfo.CurrentCulture);
            if (rebuildControls)
                BuildEquationRows(snapshot);
            else
                SynchronizeParameters(snapshot);
            if (refreshAnalysis && snapshot.SelectedIndex >= 0)
                _analysis.Text = _graph.Analyze(snapshot.SelectedIndex) ?? "Analysis unavailable for this equation.";
            else if (refreshAnalysis)
                _analysis.Text = "Select an equation to see its analysis.";
            if (!string.IsNullOrWhiteSpace(snapshot.RenderWarning))
                _status.Text = snapshot.RenderWarning;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void BuildEquationRows(GraphSnapshot snapshot)
    {
        // Removed controls may raise delayed LostFocus or selection events. Their callbacks belong to an older snapshot.
        var restoreFocus = FocusedEditor?.FocusState != FocusState.Unfocused && FocusedEditor is not null;
        var caret = FocusedEditor?.SelectionStart ?? 0;
        _rowControlVersion++;
        _parameterControls.Clear();
        _editors.Clear();
        _selectionControls.Clear();
        if (_focusedRow < 0 || _focusedRow > snapshot.Equations.Count
            || (_focusedRow == snapshot.Equations.Count && _pendingEquation is null))
            _focusedRow = snapshot.SelectedIndex;
        _equationRows.Children.Clear();
        var shownParameters = new HashSet<string>(StringComparer.Ordinal);
        for (var row = 0; row < snapshot.Equations.Count; row++)
        {
            var index = row;
            var version = _rowControlVersion;
            var equation = snapshot.Equations[row];
            var rowColor = GraphCanvas.ReadColor(equation.Color);
            var rowBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(rowColor.Alpha, rowColor.Red, rowColor.Green, rowColor.Blue));
            var contents = new StackPanel { Spacing = 5 };
            var select = new RadioButton
            {
                Content = $"Equation {row + 1}",
                GroupName = _selectionGroup,
                IsChecked = equation.Selected,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(select, $"Select equation {row + 1}, {equation.Input}");
            _selectionControls[index] = select;
            select.Click += (_, _) =>
            {
                if (version == _rowControlVersion && !_refreshing && !_mutating)
                    Mutate(() => _graph.SetSelected(index), false);
            };
            contents.Children.Add(select);
            var editor = CreateEquationEditor(index, _drafts.GetValueOrDefault(index, equation.Input), version);
            contents.Children.Add(editor);
            var formatted = new TextBlock { Text = FormatEquation(editor.Text), TextWrapping = TextWrapping.Wrap, FontSize = 20, Opacity = 0.8 };
            editor.TextChanged += (_, _) => formatted.Text = FormatEquation(editor.Text);
            AutomationProperties.SetName(formatted, $"Formatted equation {row + 1}");
            contents.Children.Add(formatted);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            actions.Children.Add(Button("Plot", () =>
            {
                if (version == _rowControlVersion) SubmitEquation(index, editor.Text);
            }, $"Plot equation {row + 1}"));
            actions.Children.Add(Button("Analyze", () =>
            {
                if (version == _rowControlVersion && Mutate(() => _graph.SetSelected(index), false))
                    _analysis.StartBringIntoView();
            }, $"Analyze equation {row + 1}"));
            actions.Children.Add(Button("×", () =>
            {
                if (version == _rowControlVersion) RemoveEquation(index);
            }, $"Remove equation {row + 1}"));
            contents.Children.Add(actions);
            var appearance = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var visible = new CheckBox { Content = "Show curve", IsChecked = equation.Visible };
            AutomationProperties.SetName(visible, $"Show equation {row + 1}");
            visible.Click += (_, _) =>
            {
                if (version == _rowControlVersion && !_refreshing && !_mutating)
                    Mutate(() => _graph.SetVisible(index, visible.IsChecked == true), false);
            };
            appearance.Children.Add(visible);
            appearance.Children.Add(FlyoutButton("Line options", () => BuildLineOptions(index, version, rowBrush)));
            contents.Children.Add(appearance);
            foreach (var parameter in equation.Parameters)
                if (shownParameters.Add(parameter.Name)) contents.Children.Add(BuildParameter(index, parameter));
            var border = new Border
            {
                Child = contents,
                Padding = new Thickness(8, 4, 0, 4),
                BorderThickness = new Thickness(3, 0, 0, 0),
                BorderBrush = rowBrush
            };
            _equationRows.Children.Add(border);
        }
        if (_pendingEquation is { } pending)
        {
            var index = snapshot.Equations.Count;
            var editor = CreateEquationEditor(index, pending, _rowControlVersion);
            var pendingCard = new StackPanel { Spacing = 5 };
            pendingCard.Children.Add(Heading($"Equation {index + 1}"));
            pendingCard.Children.Add(editor);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(Button("Plot", () => SubmitEquation(index, editor.Text)));
            actions.Children.Add(Button("Cancel", () =>
            {
                CaptureEquationDrafts();
                _pendingEquation = null;
                _focusedRow = snapshot.SelectedIndex;
                if (_snapshot is { } current) BuildEquationRows(current);
            }));
            pendingCard.Children.Add(actions);
            _equationRows.Children.Add(pendingCard);
        }
        if (_editors.TryGetValue(_focusedRow, out var focused))
        {
            focused.SelectionStart = Math.Min(caret, focused.Text.Length);
            if (restoreFocus) focused.Focus(FocusState.Programmatic);
        }
        _keypad.RefreshEnabled();
    }

    private TextBox CreateEquationEditor(int row, string text, int version)
    {
        var editor = new TextBox { Text = text, PlaceholderText = "Enter an equation", FontSize = 18 };
        AutomationProperties.SetName(editor, $"Equation {row + 1}");
        AutomationProperties.SetAutomationId(editor, $"graphEquation{row}");
        _editors[row] = editor;
        editor.GotFocus += (_, _) =>
        {
            if (version != _rowControlVersion) return;
            _focusedRow = row;
            _keypad.RefreshEnabled();
        };
        editor.TextChanged += (_, _) =>
        {
            if (version != _rowControlVersion || _refreshing) return;
            if (_snapshot is { } snapshot && row < snapshot.Equations.Count)
            {
                if (editor.Text == snapshot.Equations[row].Input) _drafts.Remove(row);
                else _drafts[row] = editor.Text;
            }
            else _pendingEquation = editor.Text;
        };
        editor.KeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter || version != _rowControlVersion) return;
            SubmitEquation(row, editor.Text);
            args.Handled = true;
        };
        return editor;
    }

    private static string FormatEquation(string input)
    {
        const string superscripts = "⁰¹²³⁴⁵⁶⁷⁸⁹";
        var formatPower = input.Count(character => character == '^') == 1;
        var output = new StringBuilder();
        for (var index = 0; index < input.Length; index++)
        {
            if (input[index] != '^' || !formatPower) { output.Append(input[index] == '*' ? '×' : input[index] == '/' ? '÷' : input[index]); continue; }
            var start = index + 1;
            var end = start;
            if (end < input.Length && input[end] is '+' or '-') end++;
            var digits = end;
            while (end < input.Length && input[end] is >= '0' and <= '9') end++;
            if (end == digits || (end < input.Length && (input[end] == '.' || char.IsLetter(input[end])))) { output.Append('^'); continue; }
            for (var exponent = start; exponent < end; exponent++)
                output.Append(input[exponent] == '-' ? '⁻' : input[exponent] == '+' ? '⁺' : superscripts[input[exponent] - '0']);
            index = end - 1;
        }
        return output.ToString();
    }

    private StackPanel BuildLineOptions(int row, int version, SolidColorBrush rowBrush)
    {
        var contents = new StackPanel { Spacing = 10, MinWidth = 220 };
        if (_snapshot is null || row >= _snapshot.Equations.Count) return contents;
        var equation = _snapshot.Equations[row];
        contents.Children.Add(Heading("Line options"));
        var colors = new ComboBox
        {
            Header = "Color", ItemsSource = Palette.Select(color => color.Name).ToArray(),
            SelectedIndex = Array.FindIndex(Palette, item => string.Equals(item.Color, equation.Color, StringComparison.OrdinalIgnoreCase)),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(colors, $"Equation {row + 1} color");
        colors.SelectionChanged += (_, _) =>
        {
            if (version != _rowControlVersion || _refreshing || _mutating || colors.SelectedIndex < 0) return;
            if (Mutate(() => _graph.SetColor(row, Palette[colors.SelectedIndex].Color), false))
            {
                var color = GraphCanvas.ReadColor(_snapshot!.Equations[row].Color);
                rowBrush.Color = Windows.UI.Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue);
            }
        };
        contents.Children.Add(colors);
        var styles = new ComboBox
        {
            Header = "Line style", ItemsSource = new[] { "Solid", "Dotted", "Dashed", "Dash-dot", "Dash-dot-dot" },
            SelectedIndex = equation.LineStyle, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(styles, $"Equation {row + 1} line style");
        styles.SelectionChanged += (_, _) =>
        {
            if (version == _rowControlVersion && !_refreshing && !_mutating && styles.SelectedIndex >= 0)
                Mutate(() => _graph.SetLineStyle(row, styles.SelectedIndex), false, false);
        };
        contents.Children.Add(styles);
        var customColor = new TextBox { Header = "Custom color", Text = equation.Color, PlaceholderText = "#RRGGBB" };
        AutomationProperties.SetName(customColor, $"Equation {row + 1} custom color");
        contents.Children.Add(customColor);
        contents.Children.Add(Button("Apply color", () =>
        {
            if (version != _rowControlVersion || !Mutate(() => _graph.SetColor(row, customColor.Text), false)) return;
            var color = GraphCanvas.ReadColor(_snapshot!.Equations[row].Color);
            rowBrush.Color = Windows.UI.Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue);
        }));
        return contents;
    }

    private StackPanel BuildParameter(int row, GraphParameter parameter)
    {
        var controls = new StackPanel { Spacing = 3 };
        var version = _rowControlVersion;
        var value = new TextBox { Header = parameter.Name, Text = parameter.Value.ToString("G8", CultureInfo.CurrentCulture) };
        AutomationProperties.SetName(value, $"Equation {row + 1}, parameter {parameter.Name}");
        var slider = new Slider
        {
            Minimum = parameter.Min,
            Maximum = parameter.Max,
            StepFrequency = parameter.Step,
            Value = Math.Clamp(parameter.Value, parameter.Min, parameter.Max)
        };
        AutomationProperties.SetName(slider, $"Equation {row + 1}, {parameter.Name} slider");
        _parameterControls[(row, parameter.Name)] = (value, slider);
        var syncing = false;
        void ApplyValue()
        {
            if (version != _rowControlVersion || _refreshing || _mutating)
                return;
            if (!TryReadNumber(value.Text, out var number))
            {
                _status.Text = $"Enter a finite number for {parameter.Name}.";
                return;
            }
            if (_snapshot is { } current && row < current.Equations.Count
                && current.Equations[row].Parameters.Any(item => item.Name == parameter.Name && item.Value == number))
                return;
            if (Mutate(() => _graph.SetParameter(row, parameter.Name, number), false))
            {
                syncing = true;
                slider.Value = Math.Clamp(number, parameter.Min, parameter.Max);
                syncing = false;
            }
        }
        value.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                ApplyValue();
                args.Handled = true;
            }
        };
        value.LostFocus += (_, _) => ApplyValue();
        slider.ValueChanged += (_, args) =>
        {
            if (version == _rowControlVersion && !_refreshing && !_mutating && !syncing)
            {
                if (_snapshot is { } current && row < current.Equations.Count
                    && current.Equations[row].Parameters.Any(item => item.Name == parameter.Name && item.Value == args.NewValue))
                    return;
                value.Text = args.NewValue.ToString("G8", CultureInfo.CurrentCulture);
                Mutate(() => _graph.SetParameter(row, parameter.Name, args.NewValue), false);
            }
        };
        controls.Children.Add(value);
        controls.Children.Add(slider);
        controls.Children.Add(FlyoutButton("Parameter range", () => BuildParameterBounds(row, parameter.Name, version)));
        return controls;
    }

    private StackPanel BuildParameterBounds(int row, string name, int version)
    {
        var contents = new StackPanel { Spacing = 8, MinWidth = 210 };
        var parameter = _snapshot?.Equations.ElementAtOrDefault(row)?.Parameters.FirstOrDefault(item => item.Name == name);
        if (parameter is null) return contents;
        contents.Children.Add(Heading($"Parameter {name}"));
        var fields = new[]
        {
            new TextBox { Header = "Minimum", Text = parameter.Min.ToString("G8", CultureInfo.CurrentCulture) },
            new TextBox { Header = "Maximum", Text = parameter.Max.ToString("G8", CultureInfo.CurrentCulture) },
            new TextBox { Header = "Step", Text = parameter.Step.ToString("G8", CultureInfo.CurrentCulture) }
        };
        foreach (var field in fields)
        {
            AutomationProperties.SetName(field, $"Parameter {name}, {field.Header}");
            contents.Children.Add(field);
        }
        contents.Children.Add(new TextBlock { Text = "The slider range applies to every equation using this parameter. Its value is kept.", TextWrapping = TextWrapping.Wrap, MaxWidth = 260 });
        contents.Children.Add(Button("Apply", () =>
        {
            if (version != _rowControlVersion) return;
            if (!TryReadNumber(fields[0].Text, out var min) || !TryReadNumber(fields[1].Text, out var max)
                || !TryReadNumber(fields[2].Text, out var step))
            {
                _status.Text = "Enter finite numbers for the parameter range and step.";
                return;
            }
            Mutate(() => _graph.SetParameterBounds(row, name, min, max, step));
        }));
        return contents;
    }

    private StackPanel BuildGraphSettings()
    {
        var contents = new StackPanel { Spacing = 10, MinWidth = 250, MaxWidth = 290 };
        var settings = _snapshot?.Settings ?? new GraphSettings(2, "#FFFFFFFF", "#FF2D2D2D", "#FF646464", "#FFDCDCDC");
        contents.Children.Add(Heading("Graph settings"));
        var width = new ComboBox { Header = "Line thickness", ItemsSource = new[] { "1", "2", "3", "4" }, SelectedIndex = Math.Clamp((int)settings.LineWidth - 1, 0, 3), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(width, "Line thickness");
        contents.Children.Add(width);
        var colors = new[]
        {
            new TextBox { Header = "Background", Text = settings.Background },
            new TextBox { Header = "Text", Text = settings.Text },
            new TextBox { Header = "Axes", Text = settings.Axis },
            new TextBox { Header = "Grid", Text = settings.Grid }
        };
        var theme = new ComboBox { Header = "Graph theme", ItemsSource = new[] { "Custom", "Light", "Dark", "Match app" }, SelectedIndex = _followAppTheme ? 3 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(theme, "Graph theme");
        theme.SelectionChanged += (_, _) =>
        {
            string[]? values = theme.SelectedIndex switch
            {
                1 => ThemeColors(false),
                2 => ThemeColors(true),
                3 => ThemeColors(ActualTheme == ElementTheme.Dark),
                _ => null
            };
            if (values is null) return;
            for (var index = 0; index < colors.Length; index++) colors[index].Text = values[index];
        };
        contents.Children.Add(theme);
        var axes = new CheckBox { Content = "Show axes", IsChecked = settings.ShowAxes };
        var grid = new CheckBox { Content = "Show grid", IsChecked = settings.ShowGrid };
        AutomationProperties.SetName(axes, "Show graph axes");
        AutomationProperties.SetName(grid, "Show graph grid");
        axes.Click += (_, _) => Mutate(() => _graph.SetGrid(axes.IsChecked == true, grid.IsChecked == true), false, false);
        grid.Click += (_, _) => Mutate(() => _graph.SetGrid(axes.IsChecked == true, grid.IsChecked == true), false, false);
        contents.Children.Add(axes);
        contents.Children.Add(grid);
        foreach (var color in colors)
        {
            AutomationProperties.SetName(color, $"Graph {color.Header} color");
            contents.Children.Add(color);
        }
        contents.Children.Add(Button("Apply", () =>
        {
            if (Mutate(() => _graph.SetSettings(width.SelectedIndex + 1, colors[0].Text, colors[1].Text, colors[2].Text, colors[3].Text), false, false))
                _followAppTheme = theme.SelectedIndex == 3;
        }));
        return contents;
    }

    private static string[] ThemeColors(bool dark) => dark
        ? ["#FF1F1F1F", "#FFFFFFFF", "#FFFFFFFF", "#FF4F4F4F"]
        : ["#FFFFFFFF", "#FF000000", "#FF000000", "#FFC6C6C6"];

    private StackPanel BuildShareControls()
    {
        var contents = new StackPanel { Spacing = 8 };
        contents.Children.Add(Button("Copy graph", async () => await CopyGraphAsync()));
        contents.Children.Add(Button("Save graph as PNG", async () => await SaveGraphAsync()));
        return contents;
    }

    private byte[] ReadGraphImage()
    {
        RefreshScene();
        if (_preparedScene is null || !string.IsNullOrWhiteSpace(_sceneError))
            throw new InvalidOperationException("Show the graph before sharing it.");
        return GraphCanvas.EncodePng(_preparedScene);
    }

    private async Task CopyGraphAsync()
    {
        InMemoryRandomAccessStream? stream = null;
        try
        {
            var image = ReadGraphImage();
            stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(image.AsBuffer());
            stream.Seek(0);
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(package);
            var previous = _clipboardImage;
            _clipboardImage = stream;
            stream = null;
            previous?.Dispose();
            _status.Text = "Graph copied.";
        }
        catch (Exception error) { _status.Text = $"The graph could not be copied. {error.Message}"; }
        finally { stream?.Dispose(); }
    }

    private async Task SaveGraphAsync()
    {
        try
        {
            var image = ReadGraphImage();
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, SuggestedFileName = "Graph" };
            picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await FileIO.WriteBytesAsync(file, image);
            _status.Text = "Graph saved.";
        }
        catch (Exception error) { _status.Text = $"The graph could not be saved. {error.Message}"; }
    }

    private void SynchronizeParameters(GraphSnapshot snapshot)
    {
        for (var row = 0; row < snapshot.Equations.Count; row++)
        {
            if (_selectionControls.TryGetValue(row, out var select)) select.IsChecked = snapshot.Equations[row].Selected;
            foreach (var parameter in snapshot.Equations[row].Parameters)
            {
                if (!_parameterControls.TryGetValue((row, parameter.Name), out var controls))
                    continue;
                if (controls.Value.FocusState == FocusState.Unfocused)
                    controls.Value.Text = parameter.Value.ToString("G8", CultureInfo.CurrentCulture);
                controls.Slider.Value = Math.Clamp(parameter.Value, parameter.Min, parameter.Max);
            }
        }
    }

    private static bool TryReadNumber(string text, out double value) =>
        (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
         || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && double.IsFinite(value);

    private void ApplyRanges()
    {
        var values = new double[4];
        for (var index = 0; index < values.Length; index++)
        {
            if (TryReadNumber(_ranges[index].Text, out values[index]))
                continue;
            _status.Text = "Enter finite numbers for the ranges.";
            return;
        }
        if (values[0] >= values[1] || values[2] >= values[3])
        {
            _status.Text = "Each minimum must be smaller than its maximum.";
            return;
        }
        Mutate(() => _graph.SetRange(values[0], values[1], values[2], values[3]), false, false);
    }

    private void ResetRange()
    {
        Mutate(() => _graph.SetRange(_defaultRange), false, false);
    }

    private void QueueSceneRefresh()
    {
        if (_sceneRefreshQueued)
            return;
        _sceneRefreshQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _sceneRefreshQueued = false;
            RefreshScene();
        });
    }

    private void RefreshScene()
    {
        if (_canvas.ActualWidth < 1 || _canvas.ActualHeight < 1)
            return;
        try
        {
            var scene = _graph.GetScene((int)Math.Ceiling(_canvas.ActualWidth), (int)Math.Ceiling(_canvas.ActualHeight));
            _preparedScene = scene;
            _sceneError = "";
            _canvas.SetScene(scene);
            if (_activeTraceToggle.IsChecked == true) UpdateActiveTrace();
            else ClearTrace();
            if (scene.HasMissingData && string.IsNullOrWhiteSpace(_status.Text))
                _status.Text = "Some parts could not be drawn. Check the equations and parameter values.";
        }
        catch (Exception error)
        {
            _sceneError = error.ToString();
            _status.Text = $"The graph could not be drawn. {error.Message}";
        }
    }

    private void BeginPan(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(_canvas);
        if (!point.Properties.IsLeftButtonPressed)
            return;
        if (_activeTraceToggle.IsChecked == true)
        {
            _traceX = Math.Clamp(2 * point.Position.X / Math.Max(1, _canvas.ActualWidth) - 1, -1, 1);
            _traceY = Math.Clamp(1 - 2 * point.Position.Y / Math.Max(1, _canvas.ActualHeight), -1, 1);
            UpdateActiveTrace();
            _activeTraceToggle.Focus(FocusState.Programmatic);
            args.Handled = true;
            return;
        }
        _dragging = _canvas.CapturePointer(args.Pointer);
        _dragPoint = point.Position;
        args.Handled = true;
    }

    private void MovePointer(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(_canvas).Position;
        if (_dragging && _canvas.ActualWidth > 0 && _canvas.ActualHeight > 0)
        {
            var deltaX = (point.X - _dragPoint.X) / _canvas.ActualWidth;
            var deltaY = (point.Y - _dragPoint.Y) / _canvas.ActualHeight;
            _dragPoint = point;
            Mutate(() => _graph.Pan(-2 * deltaX, 2 * deltaY), false, false);
            args.Handled = true;
            return;
        }
        if (_activeTraceToggle.IsChecked == true || _traceToggle.IsChecked != true || Environment.TickCount64 - _lastTraceTime < 33 || _snapshot is null || _canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0)
            return;
        _lastTraceTime = Environment.TickCount64;
        try
        {
            var span = _snapshot.Range.Right - _snapshot.Range.Left;
            var precision = Math.Pow(10, Math.Floor(Math.Log10(span)) - 3);
            var trace = _graph.Trace(2 * point.X / _canvas.ActualWidth - 1, 1 - 2 * point.Y / _canvas.ActualHeight, precision);
            if (trace is null)
            {
                ClearTrace();
                return;
            }
            ShowTrace(trace);
        }
        catch (Exception error)
        {
            _status.Text = $"The curve could not be inspected. {error.Message}";
        }
    }

    private void EndPan(object sender, PointerRoutedEventArgs args)
    {
        _dragging = false;
        _canvas.ReleasePointerCapture(args.Pointer);
        args.Handled = true;
    }

    private void ZoomPointer(object sender, PointerRoutedEventArgs args)
    {
        if (_canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0)
            return;
        var point = args.GetCurrentPoint(_canvas);
        var factor = Math.Clamp(Math.Pow(1.15, -point.Properties.MouseWheelDelta / 120.0), 0.5, 2.0);
        Mutate(() => _graph.Zoom(factor, 2 * point.Position.X / _canvas.ActualWidth - 1, 1 - 2 * point.Position.Y / _canvas.ActualHeight), false, false);
        args.Handled = true;
    }

    private void ClearTrace()
    {
        _canvas.SetTrace(null);
        _inspector.Text = _traceToggle.IsChecked == true ? "Move over a curve to inspect its coordinates." : "Drag to pan. Scroll to zoom.";
    }

    private void ShowTrace(GraphTrace trace)
    {
        var color = _snapshot is { } snapshot && trace.Row >= 0 && trace.Row < snapshot.Equations.Count
            ? snapshot.Equations[trace.Row].Color : "#0078D4";
        _canvas.SetTrace(new GraphCanvasTrace(trace.ScreenX, trace.ScreenY, color));
        _inspector.Text = $"Equation {trace.Row + 1}: x = {trace.X:G6}, y = {trace.Y:G6}"
            + (trace.Rho is { } rho ? $", ρ = {rho:G6}" : "")
            + (trace.Theta is { } theta ? $", θ = {theta:G6}" : "")
            + (trace.T is { } parameter ? $", t = {parameter:G6}" : "");
    }

    private GraphTrace? UpdateActiveTrace()
    {
        if (_snapshot is null || _activeTraceToggle.IsChecked != true) return null;
        try
        {
            var precision = Math.Pow(10, Math.Floor(Math.Log10(_snapshot.Range.Right - _snapshot.Range.Left)) - 3);
            var trace = _graph.TraceSelected(_traceX, _traceY, precision);
            if (trace is null)
            {
                _canvas.SetTrace(null);
                _inspector.Text = _snapshot.SelectedIndex < 0 || !_snapshot.Equations[_snapshot.SelectedIndex].Visible
                    ? "Select a visible equation to trace." : "The selected equation has no curve point in this view.";
                _status.Text = _inspector.Text;
                return null;
            }
            ShowTrace(trace);
            _traceX = Math.Clamp(2 * trace.ScreenX / Math.Max(1, (_preparedScene?.Width ?? 640) - 1) - 1, -1, 1);
            _traceY = Math.Clamp(1 - 2 * trace.ScreenY / Math.Max(1, (_preparedScene?.Height ?? 480) - 1), -1, 1);
            return trace;
        }
        catch (Exception error)
        {
            _canvas.SetTrace(null);
            _status.Text = $"The selected curve could not be traced. {error.Message}";
            return null;
        }
    }

    private void OnGraphKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled || (XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is TextBox or ComboBox or Slider)) return;
        args.Handled = HandleGraphKey(args.Key);
    }

    private bool HandleGraphKey(Windows.System.VirtualKey key)
    {
        if (key is Windows.System.VirtualKey.Add or Windows.System.VirtualKey.Subtract || (int)key is 187 or 189)
        {
            var zoomIn = key == Windows.System.VirtualKey.Add || (int)key == 187;
            Mutate(() => _graph.Zoom(zoomIn ? 1 / 1.0625 : 1.0625, _activeTraceToggle.IsChecked == true ? _traceX : 0,
                _activeTraceToggle.IsChecked == true ? _traceY : 0), false, false);
            return true;
        }
        if (_activeTraceToggle.IsChecked != true) return false;
        if (key == Windows.System.VirtualKey.Escape) { _activeTraceToggle.IsChecked = false; return true; }
        var dx = key == Windows.System.VirtualKey.Left ? -1 : key == Windows.System.VirtualKey.Right ? 1 : 0;
        var dy = key == Windows.System.VirtualKey.Down ? -1 : key == Windows.System.VirtualKey.Up ? 1 : 0;
        if (dx == 0 && dy == 0) return false;
        _traceX = Math.Clamp(_traceX + dx * 10.0 / Math.Max(16, _preparedScene?.Width ?? 640), -1, 1);
        _traceY = Math.Clamp(_traceY + dy * 10.0 / Math.Max(16, _preparedScene?.Height ?? 480), -1, 1);
        UpdateActiveTrace();
        return true;
    }

    private string DescribeSmokeState()
    {
        var snapshot = _snapshot;
        var rows = snapshot is null ? "none" : string.Join("; ", snapshot.Equations.Select((row, index) =>
            $"{index}:input={row.Input},visible={row.Visible},selected={row.Selected},color={row.Color},parameters="
            + string.Join(",", row.Parameters.Select(parameter => $"{parameter.Name}={parameter.Value:G17}"))));
        var controls = string.Join("; ", _parameterControls.Select(item =>
            $"{item.Key.Row}:{item.Key.Name}:text={item.Value.Value.Text},slider={item.Value.Slider.Value:G17}"));
        return $"selected={snapshot?.SelectedIndex},angle={snapshot?.AngleUnit},range={snapshot?.Range},rows=[{rows}],"
            + $"editors={System.Text.Json.JsonSerializer.Serialize(_editors.ToDictionary(item => item.Key, item => item.Value.Text))},"
            + $"controls=[{controls}],refreshing={_refreshing},mutating={_mutating},generation={_rowControlVersion},"
            + $"canvas={_canvas.ActualWidth:G17}x{_canvas.ActualHeight:G17},"
            + $"scene={_preparedScene?.Width}x{_preparedScene?.Height},status={_status.Text},"
            + $"mutationError={_lastMutationError},sceneError={_sceneError},analysis={_analysis.Text}";
    }

    public bool RunSmokeChecks()
    {
        var stage = "initial graph";
        var passed = false;
        var originalSettings = _snapshot?.Settings ?? new GraphSettings(2, "#FFFFFFFF", "#FF2D2D2D", "#FF646464", "#FFDCDCDC");
        var originalAppearance = _snapshot?.Equations.Take(2).ToArray() ?? [];
        var originalFollowTheme = _followAppTheme;
        var originalHoverTrace = _traceToggle.IsChecked == true;
        var originalTab = _graphTab.IsChecked == true;
        LastSmokeFailure = "";
        void Require(bool condition, string expectation)
        {
            if (!condition)
                throw new InvalidOperationException(expectation);
        }
        try
        {
            _drafts.Clear();
            _pendingEquation = null;
            _focusedRow = 0;
            Require(Mutate(() => ReplaceEquations(new[] { "y=a*x^2-4", "y=x" }, 0)), "Plotting valid equations was rejected.");
            Require(_snapshot?.Equations.Count == 2, "The accepted graph must contain two equations.");
            stage = "set parameter";
            Require(Mutate(() => _graph.SetParameter(0, "a", 2)), "Parameter a=2 was rejected.");
            Require(_snapshot!.Equations[0].Parameters.Any(parameter => parameter.Name == "a" && parameter.Value == 2),
                "The parameter snapshot did not retain a=2.");
            stage = "select equation";
            Require(Mutate(() => _graph.SetSelected(0)), "Selecting equation zero was rejected.");
            Require(_snapshot!.SelectedIndex == 0 && _snapshot.Equations[0].Selected, "Selection did not reach the UI snapshot.");
            stage = "selected analysis";
            Require(_analysis.Text.StartsWith("Domain\n", StringComparison.Ordinal), "Polynomial analysis is unavailable or not displayed.");
            stage = "set equation color";
            Require(Mutate(() => _graph.SetColor(0, "#DC2828")), "Changing equation color was rejected.");
            Require(_snapshot!.Equations[0].Color.Equals("#DC2828", StringComparison.OrdinalIgnoreCase), "The color snapshot did not update.");
            stage = "hide selected equation";
            Require(Mutate(() => _graph.SetVisible(0, false)), "Hiding the selected equation was rejected.");
            Require(_snapshot!.SelectedIndex == 0 && !_snapshot.Equations[0].Visible && _snapshot.Equations[0].Selected,
                "Visibility changed the selected row or failed to hide it.");
            Require(_analysis.Text.StartsWith("Domain\n", StringComparison.Ordinal), "Hidden selected equation lost its analysis.");
            stage = "manual viewport";
            Require(Mutate(() => _graph.SetRange(-3, 5, -7, 9), false, false), "The manual viewport was rejected.");
            stage = "hide all equations";
            Require(Mutate(() => _graph.SetVisible(1, false)), "Hiding the second equation was rejected.");
            Require(_snapshot!.Equations.All(equation => !equation.Visible), "The UI snapshot still contains a visible equation.");
            stage = "empty scene";
            if (_canvas.ActualWidth >= 1 && _canvas.ActualHeight >= 1)
            {
                var scene = _graph.GetScene((int)Math.Ceiling(_canvas.ActualWidth), (int)Math.Ceiling(_canvas.ActualHeight));
                Require(!scene.Strokes.Any(stroke => stroke.EquationId >= 0), "The all-hidden scene still contains a curve.");
            }
            stage = "restore first equation";
            Require(Mutate(() => _graph.SetVisible(0, true)), "Showing the first equation was rejected.");
            stage = "restore second equation";
            Require(Mutate(() => _graph.SetVisible(1, true)), "Showing the second equation was rejected.");
            var visible = _graph.GetSnapshot();
            stage = "preserved viewport";
            Require(Math.Abs(visible.Range.Left + 3) < 1e-10 && Math.Abs(visible.Range.Right - 5) < 1e-10
                && Math.Abs(visible.Range.Bottom + 7) < 1e-10 && Math.Abs(visible.Range.Top - 9) < 1e-10,
                "Visibility changes modified the manual viewport.");
            stage = "preserved parameter";
            Require(visible.Equations[0].Parameters.Any(parameter => parameter.Name == "a" && parameter.Value == 2),
                "Controls overwrote a=2 while refreshing.");
            stage = "rejected editor input";
            _editors[1].Text = "y=x+3";
            _editors[0].Text = "y=sin(";
            Require(!SubmitEquation(0, _editors[0].Text), "Invalid row input was accepted.");
            Require(!string.IsNullOrWhiteSpace(_status.Text), "Invalid input did not display an error.");
            var retained = _graph.GetSnapshot();
            Require(retained.Equations.Count == 2 && retained.Equations[0].Input == "y=a*x^2-4",
                "Invalid input replaced the accepted graph.");
            Require(_editors[0].Text == "y=sin(", "The equation card discarded the invalid draft.");
            Require(_editors[1].Text == "y=x+3" && retained.Equations[1].Input == "y=x",
                "Rejecting an edit discarded a neighboring draft or accepted it without submission.");
            stage = "focused keypad editing";
            _focusedRow = 0;
            ClearEquation();
            InsertText("y=a*x^2-4");
            InsertText("+10");
            Backspace();
            SubmitFocusedEquation();
            Require(_snapshot!.Equations[0].Input == "y=a*x^2-4+1" && _snapshot.Equations[1].Input == "y=x"
                && _snapshot.Equations[0].Color.Equals("#DC2828", StringComparison.OrdinalIgnoreCase),
                "Keypad insertion or row editing changed a neighboring equation or row color.");
            Require(_editors[0].Text == _snapshot.Equations[0].Input && _editors[1].Text == "y=x+3",
                "Keypad submission targeted an obsolete editor or discarded a neighboring draft.");
            stage = "parameter bounds";
            Require(Mutate(() => _graph.SetParameterBounds(0, "a", -1, 1, 0.25)), "Parameter slider bounds were rejected.");
            var parameter = _snapshot!.Equations[0].Parameters.Single(item => item.Name == "a");
            Require(parameter.Value == 2 && parameter.Min == -1 && parameter.Max == 1 && parameter.Step == 0.25
                && _parameterControls[(0, "a")].Slider.Maximum == 1,
                "Parameter bounds clamped the equation value or failed to reach the controls.");
            stage = "line style and graph settings";
            Require(Mutate(() => _graph.SetLineStyle(0, 2))
                && Mutate(() => _graph.SetSettings(3, "#FF102030", "#FFAABBCC", "#FF405060", "#FF708090"), false, false),
                "Line style or graph settings were rejected.");
            Require(_snapshot!.Equations[0].LineStyle == 2 && _snapshot.Settings.LineWidth == 3
                && BuildGraphSettings().Children.OfType<ComboBox>().First().SelectedIndex == 2,
                "Appearance snapshots or settings controls did not update.");
            stage = "scene image export";
            var exportScene = _graph.GetScene(320, 240);
            Require(exportScene.Background == "#FF102030" && exportScene.Labels.Any(label => label.Color == "#FFAABBCC")
                && exportScene.Strokes.Any(stroke => stroke.EquationId == 0 && stroke.Style == 2 && stroke.Width == 5),
                "Graph settings did not reach scene geometry and labels.");
            var image = GraphCanvas.EncodePng(exportScene);
            Require(image.Length > 32 && image.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                "The shared scene renderer did not produce a PNG image.");
            stage = "graph feature controls";
            Require(Mutate(() => _graph.SetFeatures(7), false, false)
                && _featureControls[1].IsChecked == true && _featureControls[2].IsChecked == true && _featureControls[4].IsChecked == true,
                "Feature marker flags did not reach the original feature controls.");
            Require(FormatEquation("x^2") == "x²" && FormatEquation("x^2^3") == "x^2^3",
                "Equation display must format simple powers without changing chained-power meaning.");
            stage = "axes and grid controls";
            Require(Mutate(() => _graph.SetFeatures(0), false, false) && Mutate(() => _graph.SetGrid(false, false), false, false),
                "Disabling graph guides failed.");
            Require(_graph.GetScene(320, 240).Labels.Count == 0 && !_snapshot!.Settings.ShowAxes && !_snapshot.Settings.ShowGrid,
                "Hidden axes and grid still display guide labels.");
            Require(Mutate(() => _graph.SetGrid(true, true), false, false), "Restoring graph guides failed.");
            stage = "active selected tracing";
            _activeTraceToggle.IsChecked = true;
            Require(UpdateActiveTrace()?.Row == 0, "Active tracing chose a neighboring curve.");
            foreach (var key in new[] { Windows.System.VirtualKey.Right, Windows.System.VirtualKey.Left, Windows.System.VirtualKey.Up, Windows.System.VirtualKey.Down })
                Require(HandleGraphKey(key) && UpdateActiveTrace()?.Row == 0, "Arrow tracing left the selected curve.");
            var beforeZoom = _snapshot!.Range.Right - _snapshot.Range.Left;
            Require(HandleGraphKey(Windows.System.VirtualKey.Add) && _snapshot.Range.Right - _snapshot.Range.Left < beforeZoom,
                "Keyboard plus did not zoom in while tracing.");
            Require(HandleGraphKey(Windows.System.VirtualKey.Subtract), "Keyboard minus was not handled.");
            Require(Mutate(() => _graph.SetVisible(0, false)) && UpdateActiveTrace() is null
                && !string.IsNullOrWhiteSpace(_status.Text), "A hidden selected curve did not clear active tracing and explain its absence.");
            Require(Mutate(() => _graph.SetVisible(0, true)), "Restoring actively traced curve failed.");
            Require(HandleGraphKey(Windows.System.VirtualKey.Escape) && _activeTraceToggle.IsChecked == false,
                "Escape did not leave active tracing.");
            Require(Mutate(() => _graph.SetRange(-3, 5, -7, 9), false, false), "Restoring the viewport after keyboard tracing failed.");
            stage = "remove neighboring card";
            RemoveEquation(1);
            Require(_snapshot!.Equations.Count == 1 && _snapshot.Equations[0].Input == "y=a*x^2-4+1"
                && _snapshot.Equations[0].LineStyle == 2 && _snapshot.Range.Left == -3,
                "Removing a neighboring card changed the retained equation, style, or range.");
            stage = "zoom";
            Require(Mutate(() => _graph.Zoom(0.5), false, false), "Zoom was rejected.");
            Require(_snapshot!.Range.Right - _snapshot.Range.Left < 8, "Zoom did not reduce the viewport span.");
            stage = "prepared scene";
            if (_canvas.ActualWidth >= 1 && _canvas.ActualHeight >= 1)
                Require(_preparedScene is not null && string.IsNullOrWhiteSpace(_sceneError), "The actual canvas scene could not be prepared.");
            passed = true;
        }
        catch (Exception error)
        {
            LastSmokeFailure = $"GraphPanel smoke stage '{stage}' failed: {error}\n{DescribeSmokeState()}";
            Console.Error.WriteLine(LastSmokeFailure);
        }
        finally
        {
            _activeTraceToggle.IsChecked = false;
            _traceToggle.IsChecked = originalHoverTrace;
            _drafts.Clear();
            _pendingEquation = null;
            _focusedRow = 0;
            if (!Mutate(() => ReplaceEquations(new[] { "y=x^2-4", "y=x" }, 0))
                || !Mutate(() => _graph.SetSettings(originalSettings.LineWidth, originalSettings.Background, originalSettings.Text, originalSettings.Axis, originalSettings.Grid), false, false)
                || !Mutate(() => _graph.SetFeatures(originalSettings.FeaturesMask), false, false)
                || !Mutate(() => _graph.SetGrid(originalSettings.ShowAxes, originalSettings.ShowGrid), false, false)
                || !Mutate(() => _graph.SetRange(_defaultRange), false, false))
            {
                if (passed)
                {
                    LastSmokeFailure = $"GraphPanel smoke cleanup failed.\n{DescribeSmokeState()}";
                    Console.Error.WriteLine(LastSmokeFailure);
                }
                passed = false;
            }
            for (var row = 0; row < 2; row++)
            {
                var index = row;
                var color = row < originalAppearance.Length ? originalAppearance[row].Color : Palette[row].Color;
                var style = row < originalAppearance.Length ? originalAppearance[row].LineStyle : 0;
                if (!Mutate(() => _graph.SetColor(index, color), false, false)
                    || !Mutate(() => _graph.SetLineStyle(index, style), false, false)) passed = false;
            }
            if (!Mutate(() => _graph.SetSelected(0))) passed = false;
            _followAppTheme = originalFollowTheme;
            if (!passed)
            {
                if (string.IsNullOrWhiteSpace(LastSmokeFailure))
                {
                    LastSmokeFailure = $"GraphPanel smoke cleanup failed.\n{DescribeSmokeState()}";
                    Console.Error.WriteLine(LastSmokeFailure);
                }
                _status.Text = "Graph check failed. See the diagnostic output.";
            }
            ShowCompactTab(originalTab);
        }
        return passed;
    }
}
