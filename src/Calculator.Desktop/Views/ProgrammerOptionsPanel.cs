// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace CalculatorApp.Desktop;

public sealed class ProgrammerOptionsPanel : StackPanel
{
    private static readonly int[] Radices = [16, 10, 8, 2];
    private static readonly int[] WordWidths = [64, 32, 16, 8];
    private static readonly string[] BitwiseTitles = ["AND", "OR", "NOT", "NAND", "NOR", "XOR"];
    private static readonly string[][] ShiftTitles = [["<<", ">>"], ["<<", ">>>"], ["ROL", "ROR"], ["RoLC", "RoRC"]];
    private static readonly string[] ShiftNames = ["Arithmetic shift", "Logical shift", "Rotate circular", "Rotate through carry"];
    private readonly Action<int> _sendCommand;
    private readonly FontFamily _icons = new("ms-appx:///Assets/CalculatorIcons.ttf#Calculator Fluent Icons");
    private readonly Button[] _baseRows = new Button[4];
    private readonly Border[] _baseIndicators = new Border[4];
    public Grid RadixControls { get; } = new();
    public Grid InputControls { get; } = new();
    public Grid BitControls => _bits;
    public Button LeftShiftButton => _shiftButtons[0];
    public Button RightShiftButton => _shiftButtons[1];
    private readonly Border _numberIndicator = Indicator();
    private readonly Border _bitIndicator = Indicator();
    private readonly Button _memoryStore = SubtleButton();
    private readonly Button _memoryList = SubtleButton();
    public event Action? MemoryRequested;
    private readonly TextBlock[] _baseValues = new TextBlock[4];
    private readonly Button _numberMode = SubtleButton();
    private readonly Button _bitMode = SubtleButton();
    private readonly Button _wordWidth = SubtleButton();
    private readonly MenuFlyout _wordMenu = new();
    private readonly Flyout _bitwiseFlyout = new() { Placement = FlyoutPlacementMode.Bottom };
    private readonly Flyout _shiftFlyout = new() { Placement = FlyoutPlacementMode.Bottom };
    private readonly Grid _bitwiseTiles = new() { RowSpacing = 2, ColumnSpacing = 2, MinWidth = 200 };
    private readonly StackPanel _shiftOptions = new() { Spacing = 4, MinWidth = 180 };
    private readonly RadioButton[] _shiftModes = new RadioButton[4];
    private readonly Button[] _shiftButtons = new Button[2];
    private readonly Grid _bits = new() { RowSpacing = 5, Visibility = Visibility.Collapsed };
    private readonly List<Button> _bitButtons = [];
    private CalculatorSnapshot? _snapshot;
    private int _shiftMode;
    private bool _updating;
    private bool _isEnabled;

    public ProgrammerOptionsPanel(Action<int> sendCommand)
    {
        ArgumentNullException.ThrowIfNull(sendCommand);
        _sendCommand = sendCommand;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(this, "Programmer options");
        for (var index = 0; index < Radices.Length; index++)
        {
            var slot = index;
            RadixControls.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 24 });
            var row = new Grid { Padding = new Thickness(12, 0, 12, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = index switch { 0 => "HEX", 1 => "DEC", 2 => "OCT", _ => "BIN" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var value = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            _baseValues[index] = value;
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            var option = SubtleButton();
            option.Content = row;
            option.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            option.Padding = new Thickness(0);
            AutomationProperties.SetName(option, $"Base {Radices[index]}");
            AutomationProperties.SetAutomationId(option, $"radix{Radices[index]}");
            option.Click += (_, _) => SelectBase(slot);
            _baseRows[index] = option;
            var indicator = Indicator();
            indicator.Width = 3; indicator.Height = 16;
            indicator.HorizontalAlignment = HorizontalAlignment.Left;
            indicator.VerticalAlignment = VerticalAlignment.Center;
            _baseIndicators[index] = indicator;
            var container = new Grid();
            container.Children.Add(option); container.Children.Add(indicator);
            Grid.SetRow(container, index);
            RadixControls.Children.Add(container);
        }
        foreach (var width in new[] { new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MaxWidth = 160 },
            new ColumnDefinition { Width = new GridLength(0.01, GridUnitType.Star) },
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 80 },
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 80 },
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 80 } }) InputControls.ColumnDefinitions.Add(width);
        var inputModes = new Grid();
        inputModes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputModes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _numberMode.Content = Icon("\uE75F"); _bitMode.Content = Icon("\uF7D0");
        _numberIndicator.Visibility = Visibility.Visible;
        foreach (var entry in new[] { (_numberMode, _numberIndicator, 0), (_bitMode, _bitIndicator, 1) })
        {
            var tile = new Grid(); tile.Children.Add(entry.Item1); tile.Children.Add(entry.Item2);
            Grid.SetColumn(tile, entry.Item3); inputModes.Children.Add(tile);
        }
        AutomationProperties.SetName(_numberMode, "Number input");
        AutomationProperties.SetName(_bitMode, "Bit input");
        AutomationProperties.SetAutomationId(_numberMode, "fullKeypad");
        AutomationProperties.SetAutomationId(_bitMode, "bitFlip");
        ToolTipService.SetToolTip(_numberMode, "Number input"); ToolTipService.SetToolTip(_bitMode, "Bit input");
        _numberMode.Click += (_, _) => SelectInput(false); _bitMode.Click += (_, _) => SelectInput(true);
        InputControls.Children.Add(inputModes);
        AutomationProperties.SetName(_wordWidth, "Cycle word size");
        _wordWidth.Click += (_, _) => CycleWordWidth();
        _wordWidth.ContextFlyout = _wordMenu;
        Grid.SetColumn(_wordWidth, 2); InputControls.Children.Add(_wordWidth);
        _memoryStore.Content = new TextBlock { Text = "MS", FontSize = 12 };
        AutomationProperties.SetName(_memoryStore, "Memory store");
        _memoryStore.Click += (_, _) => Invoke(_snapshot?.MemoryButtons.FirstOrDefault(button => button.Title == "MS"));
        Grid.SetColumn(_memoryStore, 3); InputControls.Children.Add(_memoryStore);
        var memoryLabel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        memoryLabel.Children.Add(new TextBlock { Text = "M", FontSize = 12 });
        memoryLabel.Children.Add(new FontIcon { Glyph = "\uE70D", FontSize = 8, VerticalAlignment = VerticalAlignment.Center });
        _memoryList.Content = memoryLabel;
        _memoryList.Resources["ButtonBackgroundDisabled"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _memoryStore.Resources["ButtonBackgroundDisabled"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutomationProperties.SetName(_memoryList, "Show stored memory");
        _memoryList.Click += (_, _) => MemoryRequested?.Invoke();
        Grid.SetColumn(_memoryList, 4); InputControls.Children.Add(_memoryList);
        var operators = new StackPanel { Orientation = Orientation.Horizontal, Height = 40, BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 128, 128, 128)) };
        operators.Children.Add(CreatePopup("Bitwise", "\uF895", _bitwiseFlyout));
        operators.Children.Add(CreatePopup("Bit shift", "\uE301", _shiftFlyout));
        for (var index = 0; index < _shiftButtons.Length; index++)
        {
            var direction = index;
            var button = new Button { Content = Icon(index == 0 ? "\uF88E" : "\uF88F"), Style = (Style)Application.Current.Resources["CalculatorKey"], Margin = new Thickness(1), MinHeight = 28, Padding = new Thickness(2) };
            button.Click += (_, _) => InvokeShift(direction);
            _shiftButtons[index] = button;
        }
        Children.Add(operators);
        _bitwiseFlyout.Content = _bitwiseTiles;
        for (var index = 0; index < ShiftNames.Length; index++)
        {
            var mode = index;
            var option = new RadioButton { Content = ShiftNames[index], GroupName = "ProgrammerShift", IsChecked = index == 0 };
            option.Checked += (_, _) => SelectShift(mode);
            _shiftModes[index] = option;
            _shiftOptions.Children.Add(option);
        }
        _shiftFlyout.Content = _shiftOptions;
        for (var row = 0; row < 4; row++) _bits.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var column = 0; column < 16; column++) _bits.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        IsHitTestVisible = false;
    }

    public event Action<bool>? BitModeChanged;
    public bool IsBitFlipSelected { get; private set; }
    public int BitCount => _bitButtons.Count;
    public int SelectedRadix => _snapshot?.Radix ?? 10;
    public int SelectedWordWidth => _snapshot?.WordWidth ?? 64;
    public string LastSmokeFailure { get; private set; } = "";

    public bool TryHandleShiftKey(char key)
    {
        if (key is not '<' and not '>') return false;
        var specification = FindCommand(ShiftTitles[_shiftMode][key == '<' ? 0 : 1]);
        if (!_isEnabled || specification?.Enabled != true) return false;
        Invoke(specification);
        return true;
    }

    public void Update(CalculatorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _updating = true;
        try
        {
            _isEnabled = snapshot.Mode == 2;
            IsHitTestVisible = _isEnabled;
            for (var index = 0; index < _baseRows.Length; index++)
            {
                var specification = snapshot.BaseButtons.ElementAtOrDefault(index);
                _baseRows[index].IsEnabled = specification?.Enabled == true;
                _baseIndicators[index].Visibility = snapshot.Radix == Radices[index] ? Visibility.Visible : Visibility.Collapsed;
                _baseValues[index].Text = snapshot.RadixDisplays.FirstOrDefault(display => display.Radix == Radices[index])?.Value ?? "";
                AutomationProperties.SetName(_baseRows[index], $"{specification?.Title}, {_baseValues[index].Text}");
            }
            var word = FindWordWidth(snapshot.WordWidth);
            _wordWidth.Content = word?.Title ?? $"{snapshot.WordWidth} bits";
            _wordWidth.IsEnabled = word?.Enabled == true;
            ToolTipService.SetToolTip(_wordWidth, $"{snapshot.WordWidth} bits. Select to cycle word size.");
            _memoryStore.IsEnabled = snapshot.MemoryButtons.FirstOrDefault(button => button.Title == "MS")?.Enabled == true;
            _memoryList.IsEnabled = snapshot.Memory.Count > 0;
            RefreshWordMenu();
            RefreshBitwise();
            RefreshShift();
            RefreshBits();
        }
        finally { _updating = false; }
    }

    private FontIcon Icon(string glyph) => new() { Glyph = glyph, FontFamily = _icons, FontSize = 16, IsHitTestVisible = false };

    private Button CreatePopup(string title, string glyph, Flyout flyout)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        label.Children.Add(Icon(glyph));
        label.Children.Add(new TextBlock { Text = title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(new FontIcon { Glyph = "\uE70D", FontSize = 10, IsHitTestVisible = false });
        var button = SubtleButton(); button.Content = label; button.Flyout = flyout; button.Padding = new Thickness(10, 0, 10, 0);
        AutomationProperties.SetName(button, title);
        return button;
    }

    private CalculatorButton? FindCommand(string title) => _snapshot?.ButtonRows.SelectMany(row => row).FirstOrDefault(button => button.Title == title);
    private CalculatorButton? FindWordWidth(int width) => _snapshot?.WordButtons.ElementAtOrDefault(Array.IndexOf(WordWidths, width));

    private void SelectBase(int index)
    {
        if (_updating || _snapshot is null || _snapshot.Radix == Radices[index]) return;
        Invoke(_snapshot.BaseButtons.ElementAtOrDefault(index));
    }

    private void SelectInput(bool bits)
    {
        if (_updating || IsBitFlipSelected == bits) return;
        IsBitFlipSelected = bits;
        _bits.Visibility = bits ? Visibility.Visible : Visibility.Collapsed;
        _numberIndicator.Visibility = bits ? Visibility.Collapsed : Visibility.Visible;
        _bitIndicator.Visibility = bits ? Visibility.Visible : Visibility.Collapsed;
        BitModeChanged?.Invoke(bits);
    }

    private void CycleWordWidth()
    {
        if (_snapshot is null) return;
        var next = (Array.IndexOf(WordWidths, _snapshot.WordWidth) + 1) % WordWidths.Length;
        Invoke(_snapshot.WordButtons.ElementAtOrDefault(next));
    }

    private void RefreshWordMenu()
    {
        _wordMenu.Items.Clear();
        if (_snapshot is null) return;
        for (var index = 0; index < _snapshot.WordButtons.Count && index < WordWidths.Length; index++)
        {
            var specification = _snapshot.WordButtons[index];
            var item = new ToggleMenuFlyoutItem { Text = specification.Title, IsChecked = _snapshot.WordWidth == WordWidths[index], IsEnabled = specification.Enabled };
            item.Click += (_, _) => Invoke(specification);
            _wordMenu.Items.Add(item);
        }
    }

    private void RefreshBitwise()
    {
        _bitwiseTiles.Children.Clear();
        _bitwiseTiles.RowDefinitions.Clear();
        _bitwiseTiles.ColumnDefinitions.Clear();
        for (var column = 0; column < 3; column++) _bitwiseTiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < 2; row++) _bitwiseTiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < BitwiseTitles.Length; index++)
        {
            var specification = FindCommand(BitwiseTitles[index]);
            var button = new Button { Content = BitwiseTitles[index], IsEnabled = specification?.Enabled == true, MinWidth = 58, MinHeight = 40 };
            AutomationProperties.SetName(button, specification?.AccessibleName ?? BitwiseTitles[index]);
            button.Click += (_, _) => { _bitwiseFlyout.Hide(); Invoke(specification); };
            Grid.SetRow(button, index / 3);
            Grid.SetColumn(button, index % 3);
            _bitwiseTiles.Children.Add(button);
        }
    }

    private void SelectShift(int mode)
    {
        if (_updating || _shiftMode == mode) return;
        _shiftMode = mode;
        RefreshShift();
        _shiftFlyout.Hide();
    }

    private void RefreshShift()
    {
        for (var direction = 0; direction < _shiftButtons.Length; direction++)
        {
            var specification = FindCommand(ShiftTitles[_shiftMode][direction]);
            _shiftButtons[direction].IsEnabled = specification?.Enabled == true;
            _shiftButtons[direction].Content = _shiftMode == 0
                ? new FontIcon { Glyph = direction == 0 ? "\uF88E" : "\uF88F", FontFamily = _icons, FontSize = 14 }
                : new TextBlock { Text = ShiftTitles[_shiftMode][direction], FontSize = 14 };
            var theme = ActualTheme == ElementTheme.Light ? "Light" : "Dark";
            _shiftButtons[direction].Background = (Brush)((ResourceDictionary)Application.Current.Resources.ThemeDictionaries[theme])["CalculatorOperationBackground"];
            var name = $"{ShiftNames[_shiftMode]}, {(direction == 0 ? "left" : "right")}";
            AutomationProperties.SetName(_shiftButtons[direction], name);
            ToolTipService.SetToolTip(_shiftButtons[direction], name);
        }
    }

    private void InvokeShift(int direction) => Invoke(FindCommand(ShiftTitles[_shiftMode][direction]));

    private void RefreshBits()
    {
        if (_snapshot is null) return;
        if (_bitButtons.Count != _snapshot.BitButtons.Count)
        {
            _bits.Children.Clear();
            _bitButtons.Clear();
            for (var index = 0; index < _snapshot.BitButtons.Count; index++)
            {
                var bitIndex = index;
                var button = new Button { MinWidth = 0, MinHeight = 30, Padding = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(index % 4 == 0 ? 3 : 0, 0, 0, 0) };
                button.Click += (_, _) => Invoke(_snapshot?.BitButtons.ElementAtOrDefault(bitIndex));
                Grid.SetRow(button, index / 16);
                Grid.SetColumn(button, index % 16);
                _bitButtons.Add(button);
                _bits.Children.Add(button);
            }
        }
        for (var index = 0; index < _bitButtons.Count; index++)
        {
            var specification = _snapshot.BitButtons[index];
            var button = _bitButtons[index];
            button.Content = specification.Title;
            button.IsEnabled = specification.Enabled;
            AutomationProperties.SetName(button, specification.AccessibleName);
            AutomationProperties.SetAutomationId(button, $"bit{63 - index}");
            ToolTipService.SetToolTip(button, specification.AccessibleName);
        }
    }

    private void Invoke(CalculatorButton? specification)
    {
        if (_isEnabled && specification?.Enabled == true) _sendCommand(specification.Command);
    }

    public bool RunSmokeChecks()
    {
        LastSmokeFailure = "";
        var stage = "snapshot";
        var original = _snapshot;
        var originalInput = IsBitFlipSelected;
        var originalShift = _shiftMode;
        try
        {
            if (original is null || original.Mode != 2) throw new InvalidOperationException("Programmer mode is required.");
            stage = "bit records";
            if (BitCount != 64 || _bitButtons.Count(button => button.IsEnabled) != original.WordWidth)
                throw new InvalidOperationException("Bit controls do not match the word size.");
            stage = "radix selection";
            InvokeButton(_baseRows[0]);
            if (_snapshot?.Radix != 16 || _baseIndicators[0].Visibility != Visibility.Visible) throw new InvalidOperationException("HEX selection was not reflected.");
            stage = "word size cycle";
            var nextWidth = WordWidths[(Array.IndexOf(WordWidths, _snapshot!.WordWidth) + 1) % WordWidths.Length];
            InvokeButton(_wordWidth);
            if (_snapshot?.WordWidth != nextWidth || _bitButtons.Count(button => button.IsEnabled) != nextWidth)
                throw new InvalidOperationException("Word-size selection was not reflected.");
            stage = "bit input";
            InvokeButton(_bitMode);
            if (!IsBitFlipSelected || _bits.Visibility != Visibility.Visible) throw new InvalidOperationException("Bit input was not shown.");
            var previousBit = _snapshot!.BitButtons[63].Title;
            InvokeButton(_bitButtons[63]);
            if (_snapshot.BitButtons[63].Title == previousBit) throw new InvalidOperationException("Bit zero did not change.");
            stage = "shift modes";
            _shiftModes[1].IsChecked = true;
            if (_shiftMode != 1 || !_shiftButtons[1].IsEnabled) throw new InvalidOperationException("Logical shift was not enabled.");
            stage = "number input";
            InvokeButton(_numberMode);
            if (IsBitFlipSelected || _bits.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Number input was not restored.");
            return true;
        }
        catch (Exception error)
        {
            LastSmokeFailure = $"Programmer {stage}: {error.Message}";
            Console.Error.WriteLine(LastSmokeFailure);
            return false;
        }
        finally
        {
            if (original is not null)
            {
                Invoke(FindWordWidth(original.WordWidth));
                for (var index = 0; index < original.BitButtons.Count; index++)
                    if (original.BitButtons[index].Enabled && _snapshot?.BitButtons.ElementAtOrDefault(index)?.Title != original.BitButtons[index].Title)
                        Invoke(_snapshot?.BitButtons.ElementAtOrDefault(index));
                Invoke(_snapshot?.BaseButtons.ElementAtOrDefault(Array.IndexOf(Radices, original.Radix)));
            }
            _shiftModes[originalShift].IsChecked = true;
            SelectInput(originalInput);
        }
    }

    public void SetDocked(bool docked) => _memoryList.Visibility = docked ? Visibility.Collapsed : Visibility.Visible;

    private static Button SubtleButton() => new()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
        MinWidth = 0, MinHeight = 28, Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch, FontSize = 12
    };
    private static Border Indicator() => new()
    {
        Background = (Brush)Application.Current.Resources["SystemControlHighlightAccentBrush"], Width = 16, Height = 3,
        CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Bottom,
        HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed, IsHitTestVisible = false
    };

    private static void InvokeButton(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider provider)
            throw new InvalidOperationException("Button has no invoke provider.");
        provider.Invoke();
    }
}
