// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace CalculatorApp.Desktop;

public sealed class GraphingKeypad : Grid
{
    private static readonly FontFamily CalculatorFont = new("ms-appx:///Assets/CalculatorIcons.ttf#Calculator Fluent Icons");
    private static readonly (string Caption, string Text, string Name, string? Glyph)[] PrimaryFunctions =
    [
        ("x²", "^2", "Square", "\uF7C8"),
        ("√", "sqrt(", "Square root", "\uF899"),
        ("xʸ", "^", "Power", "\uF7CA"),
        ("10ˣ", "10^", "Ten to the power", "\uF7CC"),
        ("log", "log10(", "Base ten logarithm", null),
        ("ln", "ln(", "Natural logarithm", null)
    ];
    private static readonly (string Caption, string Text, string Name, string? Glyph)[] SecondaryFunctions =
    [
        ("x³", "^3", "Cube", "\uF7CB"),
        ("∛", "cbrt(", "Cube root", "\uF881"),
        ("ⁿ√x", "root(", "Nth root: enter value, comma, then root index", "\uF7CD"),
        ("2ˣ", "2^", "Two to the power", "\uF882"),
        ("logₙ", "log(", "Logarithm: enter value, comma, then base", "\uF883"),
        ("eˣ", "e^", "Euler's number to the power", "\uF7CE")
    ];
    private readonly Action<string> _insertText;
    private readonly Action _backspace;
    private readonly Action _clear;
    private readonly Action _submit;
    private readonly Func<bool> _canInsert;
    private readonly List<ButtonBase> _buttons = new();
    private readonly Button[] _scientificButtons = new Button[6];
    private readonly ToggleButton _second = new() { Content = "2nd", MinHeight = 38, IsTabStop = false, AllowFocusOnInteraction = false };
    private readonly ToggleButton _trigInverse = new() { Content = "2nd", MinHeight = 38, IsTabStop = false, AllowFocusOnInteraction = false };
    private readonly ToggleButton _trigHyperbolic = new() { Content = "hyp", MinHeight = 38, IsTabStop = false, AllowFocusOnInteraction = false };
    private readonly Button[] _trigButtons = new Button[6];
    private readonly Flyout _trigFlyout = new();

    public GraphingKeypad(Action<string> insertText, Action backspace, Action clear, Action submit, Func<bool>? canInsert = null)
    {
        _insertText = insertText ?? throw new ArgumentNullException(nameof(insertText));
        _backspace = backspace ?? throw new ArgumentNullException(nameof(backspace));
        _clear = clear ?? throw new ArgumentNullException(nameof(clear));
        _submit = submit ?? throw new ArgumentNullException(nameof(submit));
        _canInsert = canInsert ?? (() => true);
        _buttons.AddRange(new ButtonBase[] { _second, _trigInverse, _trigHyperbolic });
        RowSpacing = 3;
        ColumnSpacing = 3;
        MinWidth = 250;
        for (var index = 0; index < 5; index++)
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 1; index < 8; index++)
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 38 });
        AutomationProperties.SetName(this, "Graphing equation keypad");

        var groups = new Grid { ColumnSpacing = 4 };
        for (var index = 0; index < 3; index++)
            groups.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddGroup(groups, CreateTrigonometry(), 0);
        AddGroup(groups, CreateInequalities(), 1);
        AddGroup(groups, CreateFunctions(), 2);
        Place(groups, 0, 0, 5);

        AutomationProperties.SetName(_second, "Second graphing function set");
        AutomationProperties.SetAutomationId(_second, "graphShiftButton");
        _second.Checked += (_, _) => RefreshScientificFunctions();
        _second.Unchecked += (_, _) => RefreshScientificFunctions();
        Place(_second, 1, 0);
        Place(InsertButton("π", "pi", "Pi", "graphPiButton", "\uF7CF"), 1, 1);
        Place(InsertButton("e", "e", "Euler's number", "graphEulerButton"), 1, 2);
        Place(ActionButton("C", _clear, "Clear equation", "graphClearButton"), 1, 3);
        Place(ActionButton("⌫", _backspace, "Backspace in equation", "graphBackspaceButton", "\uE94F"), 1, 4);

        for (var index = 0; index < _scientificButtons.Length; index++)
        {
            var functionIndex = index;
            var button = ActionButton("", () => InsertScientificFunction(functionIndex), "", $"graphScientific{index}");
            _scientificButtons[index] = button;
            Place(button, index + 2, 0);
        }
        RefreshScientificFunctions();

        Place(InsertButton("1/x", "1/", "Reciprocal", "graphInvertButton", "\uF7C9"), 2, 1);
        Place(InsertButton("|x|", "abs(", "Absolute value", "graphAbsButton", "\uF884"), 2, 2);
        Place(InsertButton("x", "x", "Variable x", "graphXButton"), 2, 3);
        Place(InsertButton("y", "y", "Variable y", "graphYButton"), 2, 4);
        Place(InsertButton("(", "(", "Open parenthesis", "graphOpenParenthesisButton"), 3, 1);
        Place(InsertButton(")", ")", "Close parenthesis", "graphCloseParenthesisButton"), 3, 2);
        Place(InsertButton("=", "=", "Equation equals", "graphEqualButton"), 3, 3);
        Place(InsertButton("÷", "/", "Divide", "graphDivideButton"), 3, 4);
        var digits = new[] { "789", "456", "123" };
        for (var row = 0; row < digits.Length; row++)
            for (var column = 0; column < digits[row].Length; column++)
            {
                var digit = digits[row][column].ToString();
                Place(InsertButton(digit, digit, digit, $"graphNum{digit}Button", numeric: true), row + 4, column + 1);
            }
        Place(InsertButton("×", "*", "Multiply", "graphMultiplyButton"), 4, 4);
        Place(InsertButton("−", "-", "Subtract", "graphMinusButton"), 5, 4);
        Place(InsertButton("+", "+", "Add", "graphPlusButton"), 6, 4);
        Place(InsertButton("+/−", "-", "Negate", "graphNegateButton"), 7, 1);
        Place(InsertButton("0", "0", "Zero", "graphNum0Button", numeric: true), 7, 2);
        Place(InsertButton(".", ".", "Decimal point", "graphDecimalSeparatorButton", numeric: true), 7, 3);
        var submitButton = ActionButton("↵", _submit, "Submit equation", "graphSubmitButton");
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accentStyle) && accentStyle is Style style)
            submitButton.Style = style;
        Place(submitButton, 7, 4);
        RefreshEnabled();
    }

    public void RefreshEnabled()
    {
        var enabled = _canInsert();
        IsHitTestVisible = enabled;
        Opacity = enabled ? 1 : 0.5;
        foreach (var button in _buttons) button.IsEnabled = enabled;
    }

    private void Place(FrameworkElement control, int row, int column, int columnSpan = 1)
    {
        SetRow(control, row);
        SetColumn(control, column);
        SetColumnSpan(control, columnSpan);
        Children.Add(control);
    }

    private static void AddGroup(Grid grid, Button button, int column)
    {
        SetColumn(button, column);
        grid.Children.Add(button);
    }

    private Button ActionButton(string caption, Action action, string accessibleName, string id, string? glyph = null, bool numeric = false)
    {
        var button = new Button
        {
            Content = Content(caption, glyph),
            MinHeight = 38,
            MinWidth = 32,
            Padding = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            FontSize = numeric ? 21 : 17,
            IsTabStop = false,
            AllowFocusOnInteraction = false
        };
        if (numeric)
            button.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetName(button, accessibleName);
        AutomationProperties.SetAutomationId(button, id);
        ToolTipService.SetToolTip(button, accessibleName);
        button.Click += (_, _) => { if (_canInsert()) action(); };
        _buttons.Add(button);
        return button;
    }

    private Button InsertButton(string caption, string text, string name, string id, string? glyph = null, bool numeric = false) =>
        ActionButton(caption, () => _insertText(text), name, id, glyph, numeric);

    private static object Content(string caption, string? glyph) => glyph is null ? caption : new FontIcon
    {
        FontFamily = CalculatorFont,
        Glyph = glyph,
        FontSize = 20
    };

    private void InsertScientificFunction(int index)
    {
        var specification = (_second.IsChecked == true ? SecondaryFunctions : PrimaryFunctions)[index];
        _insertText(specification.Text);
    }

    private void RefreshScientificFunctions()
    {
        var specifications = _second.IsChecked == true ? SecondaryFunctions : PrimaryFunctions;
        for (var index = 0; index < _scientificButtons.Length; index++)
        {
            var specification = specifications[index];
            var button = _scientificButtons[index];
            button.Content = Content(specification.Caption, specification.Glyph);
            AutomationProperties.SetName(button, specification.Name);
            ToolTipService.SetToolTip(button, specification.Name);
        }
    }

    private Button CreateTrigonometry()
    {
        var panel = new StackPanel { Spacing = 6, MinWidth = 228 };
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        AutomationProperties.SetName(_trigInverse, "Inverse trigonometric functions");
        AutomationProperties.SetName(_trigHyperbolic, "Hyperbolic trigonometric functions");
        toggles.Children.Add(_trigInverse);
        toggles.Children.Add(_trigHyperbolic);
        panel.Children.Add(toggles);
        var functions = new Grid { RowSpacing = 3, ColumnSpacing = 3 };
        for (var index = 0; index < 3; index++)
            functions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < 2; index++)
            functions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < _trigButtons.Length; index++)
        {
            var functionIndex = index;
            var button = ActionButton("", () => InsertTrigFunction(functionIndex), "", $"graphTrig{index}");
            button.MinWidth = 70;
            _trigButtons[index] = button;
            SetRow(button, index / 3);
            SetColumn(button, index % 3);
            functions.Children.Add(button);
        }
        panel.Children.Add(functions);
        _trigInverse.Checked += (_, _) => RefreshTrigFunctions();
        _trigInverse.Unchecked += (_, _) => RefreshTrigFunctions();
        _trigHyperbolic.Checked += (_, _) => RefreshTrigFunctions();
        _trigHyperbolic.Unchecked += (_, _) => RefreshTrigFunctions();
        _trigFlyout.Content = panel;
        _trigFlyout.Opening += (_, _) => _trigFlyout.ShowMode = FlyoutShowMode.Transient;
        RefreshTrigFunctions();
        var trigger = ActionButton("Trig", () => { }, "Trigonometry", "graphTrigButton");
        trigger.Flyout = _trigFlyout;
        return trigger;
    }

    private (string Caption, string Text, string Name) TrigFunction(int index)
    {
        string[] names = ["sin", "cos", "tan", "sec", "csc", "cot"];
        var name = names[index];
        var hyperbolic = _trigHyperbolic.IsChecked == true;
        var inverse = _trigInverse.IsChecked == true;
        var function = (inverse ? "a" : "") + name + (hyperbolic ? "h" : "");
        var caption = name + (hyperbolic ? "h" : "") + (inverse ? "⁻¹" : "");
        return (caption, function + "(", (inverse ? "Inverse " : "") + (hyperbolic ? "hyperbolic " : "") + name);
    }

    private void RefreshTrigFunctions()
    {
        for (var index = 0; index < _trigButtons.Length; index++)
        {
            var function = TrigFunction(index);
            var button = _trigButtons[index];
            button.Content = function.Caption;
            AutomationProperties.SetName(button, function.Name);
            ToolTipService.SetToolTip(button, function.Name);
        }
    }

    private void InsertTrigFunction(int index)
    {
        _insertText(TrigFunction(index).Text);
        _trigFlyout.Hide();
    }

    private Button CreateInequalities()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        var flyout = new Flyout { Content = panel };
        (string Caption, string Text, string Name)[] relations =
        [
            ("<", "<", "Less than"), ("≤", "<=", "Less than or equal"), ("=", "=", "Equals"),
            ("≥", ">=", "Greater than or equal"), (">", ">", "Greater than")
        ];
        for (var index = 0; index < relations.Length; index++)
        {
            var relation = relations[index];
            panel.Children.Add(ActionButton(relation.Caption, () => { _insertText(relation.Text); flyout.Hide(); }, relation.Name, $"graphInequality{index}"));
        }
        flyout.Opening += (_, _) => flyout.ShowMode = FlyoutShowMode.Transient;
        var trigger = ActionButton("≤ ≥", () => { }, "Inequalities", "graphInequalityButton");
        trigger.Flyout = flyout;
        return trigger;
    }

    private Button CreateFunctions()
    {
        var panel = new Grid { ColumnSpacing = 3, RowSpacing = 3 };
        for (var index = 0; index < 3; index++)
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var flyout = new Flyout { Content = panel };
        (string Caption, string Text, string Name)[] functions =
        [
            ("|x|", "abs(", "Absolute value"), ("⌊x⌋", "floor(", "Floor"), ("⌈x⌉", "ceil(", "Ceiling"),
            ("ⁿ√x", "root(", "Nth root: enter value, comma, then root index"),
            ("logₙ", "log(", "Logarithm: enter value, comma, then base"), (",", ",", "Argument separator")
        ];
        for (var index = 0; index < functions.Length; index++)
        {
            var function = functions[index];
            var button = ActionButton(function.Caption, () => { _insertText(function.Text); flyout.Hide(); }, function.Name, $"graphFunction{index}");
            button.MinWidth = 68;
            SetRow(button, index / 3);
            SetColumn(button, index % 3);
            panel.Children.Add(button);
        }
        flyout.Opening += (_, _) => flyout.ShowMode = FlyoutShowMode.Transient;
        var trigger = ActionButton("f(x)", () => { }, "Functions", "graphFunctionButton");
        trigger.Flyout = flyout;
        return trigger;
    }
}
