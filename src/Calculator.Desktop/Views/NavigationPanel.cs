// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CalculatorApp.Desktop;

public sealed class NavigationPanel : NavigationView
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
        OpenPaneLength = 256;
        PaneDisplayMode = NavigationViewPaneDisplayMode.LeftMinimal;
        IsPaneOpen = false;
        IsPaneToggleButtonVisible = false;
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        IsSettingsVisible = true;
        AutomationProperties.SetName(this, "Calculator modes and converters");

        MenuItems.Add(CreateHeading("Calculator"));
        MenuItems.Add(CreateItem("Standard", "\uE8EF", new Entry(EntryKind.Mode, 0)));
        MenuItems.Add(CreateItem("Scientific", "\uF196", new Entry(EntryKind.Mode, 1)));
        var graphing = CreateItem("Graphing", "\uF770", new Entry(EntryKind.Mode, 5));
        graphing.IsEnabled = graphingEnabled;
        MenuItems.Add(graphing);
        MenuItems.Add(CreateItem("Programmer", "\uECCE", new Entry(EntryKind.Mode, 2)));
        MenuItems.Add(CreateItem("Date calculation", "\uE787", new Entry(EntryKind.Mode, 4)));
        MenuItems.Add(CreateHeading("Converter"));
        MenuItems.Add(CreateItem(categories[CategoryGlyphs.Length], "\uEB0D", new Entry(EntryKind.Category, CategoryGlyphs.Length)));
        for (var index = 0; index < CategoryGlyphs.Length; index++)
            MenuItems.Add(CreateItem(categories[index], CategoryGlyphs[index], new Entry(EntryKind.Category, index)));

        ItemInvoked += OnItemInvoked;
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
}
