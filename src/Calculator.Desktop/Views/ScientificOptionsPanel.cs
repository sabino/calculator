// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace CalculatorApp.Desktop;

public sealed class ScientificOptionsPanel : StackPanel
{
    private static readonly string[] FunctionTitles = ["|x|", "floor", "ceil", "rand", "dms", "deg"];
    private static readonly IReadOnlyDictionary<string, string> FunctionGlyphs = new Dictionary<string, string>
    {
        ["|x|"] = "\uF884", ["floor"] = "\uF885", ["ceil"] = "\uF886", ["dms"] = "\uF890", ["deg"] = "\uF891"
    };
    private readonly Action<int> _sendCommand;
    private readonly FontFamily _calculatorIcons;
    private readonly StackPanel _angleRow = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly Flyout _trigonometryFlyout = new() { Placement = FlyoutPlacementMode.Bottom };
    private readonly Flyout _functionFlyout = new() { Placement = FlyoutPlacementMode.Bottom };
    private readonly ToggleButton _inverse = new() { MinWidth = 58, MinHeight = 42 };
    private readonly ToggleButton _hyperbolic = new() { Content = "hyp", MinWidth = 58, MinHeight = 42 };
    private readonly Grid _trigonometryTiles = new() { RowSpacing = 2, ColumnSpacing = 2 };
    private readonly Grid _functionTiles = new() { RowSpacing = 2, ColumnSpacing = 2 };
    private readonly Button _trigonometryButton;
    private readonly Button _functionButton;
    private CalculatorSnapshot? _snapshot;
    private bool _isEnabled;

    public ScientificOptionsPanel(Action<int> sendCommand, FontFamily? calculatorIcons = null)
    {
        ArgumentNullException.ThrowIfNull(sendCommand);
        _sendCommand = sendCommand;
        _calculatorIcons = calculatorIcons ?? new FontFamily("ms-appx:///Assets/CalculatorIcons.ttf#Calculator Fluent Icons");
        Spacing = 6;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(this, "Scientific options");
        Children.Add(_angleRow);

        var menus = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _trigonometryButton = CreatePopupButton("Trigonometry", "\uF892", "trigButton", _trigonometryFlyout);
        _functionButton = CreatePopupButton("Function", "\uF893", "funcButton", _functionFlyout);
        menus.Children.Add(_trigonometryButton);
        menus.Children.Add(_functionButton);
        Children.Add(menus);

        // The two toggles select the four explicit command groups; they send no engine inversion command.
        _inverse.Content = CalculatorIcon("\uF897");
        AutomationProperties.SetName(_inverse, "Inverse trigonometry functions");
        AutomationProperties.SetAutomationId(_inverse, "trigShiftButton");
        ToolTipService.SetToolTip(_inverse, "2nd: inverse trigonometry functions");
        AutomationProperties.SetName(_hyperbolic, "Hyperbolic functions");
        AutomationProperties.SetAutomationId(_hyperbolic, "hypShiftButton");
        _inverse.Checked += (_, _) => RefreshTrigonometry();
        _inverse.Unchecked += (_, _) => RefreshTrigonometry();
        _hyperbolic.Checked += (_, _) => RefreshTrigonometry();
        _hyperbolic.Unchecked += (_, _) => RefreshTrigonometry();

        var trigContent = new Grid { MinWidth = 258, ColumnSpacing = 2, RowSpacing = 2 };
        trigContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        trigContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        trigContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        trigContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        trigContent.Children.Add(_inverse);
        Grid.SetRow(_hyperbolic, 1);
        trigContent.Children.Add(_hyperbolic);
        Grid.SetColumn(_trigonometryTiles, 1);
        Grid.SetRowSpan(_trigonometryTiles, 2);
        trigContent.Children.Add(_trigonometryTiles);
        _trigonometryFlyout.Content = trigContent;

        var functions = new StackPanel { MinWidth = 194, Spacing = 6 };
        functions.Children.Add(_functionTiles);
        _functionFlyout.Content = functions;
        IsHitTestVisible = false;
    }

    public void Update(CalculatorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _isEnabled = snapshot.Mode == 1 && !snapshot.IsError;
        IsHitTestVisible = _isEnabled;
        if (!_isEnabled)
        {
            _trigonometryFlyout.Hide();
            _functionFlyout.Hide();
        }
        RefreshAngles(snapshot);
        var groups = snapshot.ScientificMenus;
        _trigonometryButton.IsEnabled = groups.Any(group => group.Title == "Trigonometry" && group.Buttons.Any(button => button.Enabled));
        _functionButton.IsEnabled = groups.Any(group => group.Title == "Functions" && group.Buttons.Any(button => button.Enabled));
        _inverse.IsEnabled = groups.Any(group => group.Title == "Inverse trigonometry" && group.Buttons.Count > 0);
        _hyperbolic.IsEnabled = groups.Any(group => group.Title == "Hyperbolic functions" && group.Buttons.Count > 0);
        RefreshTrigonometry();
        RefreshFunctions();
    }

    private FontIcon CalculatorIcon(string glyph) => new()
    {
        Glyph = glyph,
        FontFamily = _calculatorIcons,
        FontSize = 20,
        IsHitTestVisible = false
    };

    private Button CreatePopupButton(string title, string glyph, string automationId, Flyout flyout)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        label.Children.Add(CalculatorIcon(glyph));
        label.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(new FontIcon { Glyph = "\uE70D", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        var button = new Button { Content = label, Flyout = flyout, MinHeight = 36, Padding = new Thickness(8, 5, 8, 5) };
        AutomationProperties.SetName(button, title);
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    private void RefreshAngles(CalculatorSnapshot snapshot)
    {
        _angleRow.Children.Clear();
        foreach (var specification in snapshot.AngleButtons)
        {
            var option = new ToggleButton
            {
                Content = specification.Title,
                IsChecked = specification.Command == snapshot.AngleCommand,
                IsEnabled = specification.Enabled,
                MinWidth = 55,
                Padding = new Thickness(8, 4, 8, 4)
            };
            AutomationProperties.SetName(option, specification.AccessibleName);
            option.Click += (_, _) => _sendCommand(specification.Command);
            _angleRow.Children.Add(option);
        }
        var notation = snapshot.ScientificFormatButton;
        if (notation is not null)
        {
            var button = new ToggleButton { Content = notation.Title, IsChecked = snapshot.ScientificFormat, IsEnabled = notation.Enabled, MinWidth = 55 };
            AutomationProperties.SetName(button, "Toggle scientific notation");
            button.Click += (_, _) => _sendCommand(notation.Command);
            _angleRow.Children.Add(button);
        }
    }

    private void RefreshTrigonometry()
    {
        var title = (_inverse.IsChecked == true, _hyperbolic.IsChecked == true) switch
        {
            (true, true) => "Inverse hyperbolic functions",
            (true, false) => "Inverse trigonometry",
            (false, true) => "Hyperbolic functions",
            _ => "Trigonometry"
        };
        var group = _snapshot?.ScientificMenus.FirstOrDefault(menu => menu.Title == title);
        FillTiles(_trigonometryTiles, group?.Buttons ?? Array.Empty<CalculatorButton>(), 3, false);
    }

    private void RefreshFunctions()
    {
        var group = _snapshot?.ScientificMenus.FirstOrDefault(menu => menu.Title == "Functions");
        var available = group?.Buttons ?? Array.Empty<CalculatorButton>();
        var primary = FunctionTitles.Select(title => available.FirstOrDefault(button => button.Title == title))
            .Where(button => button is not null).Cast<CalculatorButton>().ToArray();
        FillTiles(_functionTiles, primary, 3, true);
    }

    private void FillTiles(Grid grid, IReadOnlyList<CalculatorButton> specifications, int columns, bool functionIcons)
    {
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();
        for (var column = 0; column < columns; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < (specifications.Count + columns - 1) / columns; row++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < specifications.Count; index++)
        {
            var specification = specifications[index];
            object caption = functionIcons && FunctionGlyphs.TryGetValue(specification.Title, out var glyph)
                ? CalculatorIcon(glyph) : TrigCaption(specification.Title);
            var button = new Button
            {
                Content = caption,
                IsEnabled = specification.Enabled,
                MinWidth = 58,
                MinHeight = 42,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(6, 4, 6, 4)
            };
            AutomationProperties.SetName(button, specification.AccessibleName);
            ToolTipService.SetToolTip(button, specification.AccessibleName);
            button.Click += (_, _) => InvokeOperation(specification);
            Grid.SetRow(button, index / columns);
            Grid.SetColumn(button, index % columns);
            grid.Children.Add(button);
        }
    }

    private static string TrigCaption(string title) => title switch
    {
        "asin" => "sin⁻¹", "acos" => "cos⁻¹", "atan" => "tan⁻¹",
        "asec" => "sec⁻¹", "acsc" => "csc⁻¹", "acot" => "cot⁻¹",
        "asinh" => "sinh⁻¹", "acosh" => "cosh⁻¹", "atanh" => "tanh⁻¹",
        "asech" => "sech⁻¹", "acsch" => "csch⁻¹", "acoth" => "coth⁻¹",
        _ => title
    };

    private void InvokeOperation(CalculatorButton specification)
    {
        if (!_isEnabled || !specification.Enabled)
            return;
        _inverse.IsChecked = false;
        _hyperbolic.IsChecked = false;
        _trigonometryFlyout.Hide();
        _functionFlyout.Hide();
        _sendCommand(specification.Command);
    }
}
