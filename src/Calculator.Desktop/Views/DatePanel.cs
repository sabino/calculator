// Licensed under the MIT License.
using System;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CalculatorApp.Desktop;

public sealed class DatePanel : Grid
{
    private static readonly DateOnly MinimumDate = new(1601, 1, 1);
    private static readonly DateOnly MaximumDate = new(9999, 12, 31);
    private readonly NativeCalculator _calculator;
    private readonly ComboBox _operation = new()
    {
        ItemsSource = new[] { "Difference between dates", "Add or subtract days" },
        SelectedIndex = 0,
        HorizontalAlignment = HorizontalAlignment.Left,
        MinWidth = 0, BorderThickness = new Thickness(0), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent)
    };
    private readonly CalendarDatePicker _from = CreateDatePicker("From");
    private readonly CalendarDatePicker _to = CreateDatePicker("To");
    private readonly NumberBox _years = CreateOffset("Years");
    private readonly NumberBox _months = CreateOffset("Months");
    private readonly NumberBox _days = CreateOffset("Days");
    private readonly Grid _duration = new() { ColumnSpacing = 12 };
    private readonly TextBlock _result = new()
    {
        FontSize = 14,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true
    };
    private readonly TextBlock _totalDays = new()
    {
        FontSize = 14,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true
    };
    private readonly RadioButton _add = new() { Content = "Add", GroupName = "DateOffset", IsChecked = true, FontSize = 14 };
    private readonly RadioButton _subtract = new() { Content = "Subtract", GroupName = "DateOffset", FontSize = 14 };
    private readonly TextBlock _resultLabel = new() { Text = "Difference", FontSize = 12, Opacity = 0.75 };
    private bool _updatingControls;
    private bool _hasError;
    private CivilDate? _shiftResult;
    private DateDifference? _difference;

    public DatePanel(NativeCalculator calculator)
    {
        ArgumentNullException.ThrowIfNull(calculator);
        _calculator = calculator;
        Margin = new Thickness(16, 0, 16, 0);
        MaxWidth = 760;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Top;
        for (var row = 0; row < 6; row++) RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AutomationProperties.SetName(_operation, "Date calculation operation");
        CalculatorPage.SubtlePicker(_operation);
        _operation.Margin = new Thickness(-12, 0, 0, 0);
        Children.Add(_operation);
        _from.Margin = new Thickness(0, 17, 0, 0);
        _to.Margin = new Thickness(0, 24, 0, 0);
        SetRow(_from, 1); Children.Add(_from);
        SetRow(_to, 2); Children.Add(_to);
        var today = DateOnly.FromDateTime(DateTime.Today);
        _from.Date = _to.Date = ToPickerDate(today);

        var offsets = new[] { _years, _months, _days };
        for (var index = 0; index < offsets.Length; index++)
        {
            _duration.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            SetColumn(offsets[index], index);
            _duration.Children.Add(offsets[index]);
        }
        var direction = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        direction.Children.Add(_add); direction.Children.Add(_subtract);
        _duration.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _duration.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SetColumnSpan(direction, 3); _duration.Children.Add(direction);
        foreach (var offset in offsets) { SetRow(offset, 1); offset.Margin = new Thickness(0, 24, 0, 0); }
        _duration.Margin = new Thickness(0, 24, 0, 0);
        SetRow(_duration, 3);
        Children.Add(_duration);
        AutomationProperties.SetName(_result, "Date calculation result");
        var answer = new StackPanel { Spacing = 4, Margin = new Thickness(0, 24, 0, 0) };
        answer.Children.Add(_resultLabel); answer.Children.Add(_result);
        SetRow(answer, 4); Children.Add(answer);
        AutomationProperties.SetName(_totalDays, "Total difference in days");
        _totalDays.Margin = new Thickness(0, 8, 0, 0);
        SetRow(_totalDays, 5);
        Children.Add(_totalDays);

        _add.Checked += (_, _) => RefreshResult();
        _subtract.Checked += (_, _) => RefreshResult();
        _operation.SelectionChanged += (_, _) => RefreshResult();
        _from.DateChanged += (_, _) => RefreshResult();
        _to.DateChanged += (_, _) => RefreshResult();
        _years.ValueChanged += (_, _) => RefreshResult();
        _months.ValueChanged += (_, _) => RefreshResult();
        _days.ValueChanged += (_, _) => RefreshResult();
        RefreshResult();
    }

    private static DateTimeOffset ToPickerDate(DateOnly date)
    {
        // Pickers display local calendar dates; the native engine receives date fields only.
        var localDate = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate));
    }

    private static CalendarDatePicker CreateDatePicker(string label)
    {
        var picker = new CalendarDatePicker
        {
            Header = new TextBlock { Text = label, FontSize = 12, Opacity = 0.75 },
            PlaceholderText = "Choose a date",
            CalendarIdentifier = "GregorianCalendar",
            MinDate = ToPickerDate(MinimumDate),
            MaxDate = ToPickerDate(MaximumDate),
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 0, MaxWidth = 220, FontSize = 14,
            BorderThickness = new Thickness(0), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Padding = new Thickness(0),
            DateFormat = "{month.full} {day.integer}, {year.full}"
        };
        picker.Resources["CalendarDatePickerBackground"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        picker.Resources["CalendarDatePickerBorderBrush"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutomationProperties.SetName(picker, label);
        return picker;
    }

    private static NumberBox CreateOffset(string label)
    {
        var box = new NumberBox
        {
            Header = label,
            Minimum = 0,
            Maximum = 9999,
            Value = 0,
            SmallChange = 1,
            LargeChange = 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(box, label);
        return box;
    }

    private static string FormatDate(DateOnly date)
    {
        // Keep the civil date Gregorian even where the region uses another calendar.
        var culture = (CultureInfo)CultureInfo.CurrentCulture.Clone();
        var calendar = culture.OptionalCalendars.OfType<GregorianCalendar>().FirstOrDefault();
        if (calendar is null)
            return date.ToString("D", CultureInfo.InvariantCulture);
        culture.DateTimeFormat.Calendar = calendar;
        return date.ToString("D", culture);
    }

    private static string FormatDuration(DateDifference difference)
    {
        if (difference.TotalDays == 0)
            return "Same dates";
        var units = new[]
        {
            (difference.Years, "year"), (difference.Months, "month"),
            (difference.Weeks, "week"), (difference.Days, "day")
        };
        return string.Join(", ", units.Where(unit => unit.Item1 != 0)
            .Select(unit => $"{unit.Item1.ToString(CultureInfo.CurrentCulture)} {unit.Item2}{(unit.Item1 == 1 ? "" : "s")}"));
    }

    private static bool TryReadDate(CalendarDatePicker picker, out DateOnly date)
    {
        if (picker.Date is not { } value)
        {
            date = default;
            return false;
        }
        var localDate = value.LocalDateTime;
        date = new DateOnly(localDate.Year, localDate.Month, localDate.Day);
        return date >= MinimumDate && date <= MaximumDate;
    }

    private static bool TryReadOffset(NumberBox box, out int offset)
    {
        var value = box.Value;
        if (!double.IsFinite(value) || value < 0 || value > 9999 || Math.Truncate(value) != value)
        {
            offset = 0;
            return false;
        }
        offset = (int)value;
        return true;
    }

    private void ShowError(string message)
    {
        _hasError = true;
        _result.Text = message;
        _totalDays.Text = "";
    }

    private void RefreshResult()
    {
        if (_updatingControls)
            return;
        _hasError = false;
        _shiftResult = null;
        _difference = null;
        _totalDays.Text = "";
        var isDifference = _operation.SelectedIndex == 0;
        _from.Header = new TextBlock { Text = "From", FontSize = 12, Opacity = 0.75 };
        _resultLabel.Text = isDifference ? "Difference" : "Date";
        AutomationProperties.SetName(_from, isDifference ? "From date" : "Start date");
        _to.Visibility = isDifference ? Visibility.Visible : Visibility.Collapsed;

        _duration.Visibility = isDifference ? Visibility.Collapsed : Visibility.Visible;
        if (_operation.SelectedIndex < 0 || _operation.SelectedIndex > 1)
        {
            ShowError("Choose a date calculation operation.");
            return;
        }
        if (!TryReadDate(_from, out var from))
        {
            ShowError("Choose a start date from January 1, 1601 through December 31, 9999.");
            return;
        }
        try
        {
            if (isDifference)
            {
                if (!TryReadDate(_to, out var to))
                {
                    ShowError("Choose an end date from January 1, 1601 through December 31, 9999.");
                    return;
                }
                _difference = _calculator.GetDateDifference(from, to);
                if (_difference is null)
                {
                    ShowError(_calculator.LastError);
                    return;
                }
                _result.Text = FormatDuration(_difference);
                _totalDays.Text = _difference.TotalDays > 0 && (_difference.Years != 0 || _difference.Months != 0 || _difference.Weeks != 0)
                    ? $"{_difference.TotalDays.ToString(CultureInfo.CurrentCulture)} " + (_difference.TotalDays == 1 ? "day" : "days") : "";
                return;
            }
            if (!TryReadOffset(_years, out var years) || !TryReadOffset(_months, out var months) || !TryReadOffset(_days, out var days))
            {
                ShowError("Enter whole numbers from 0 through 9999 for years, months, and days.");
                return;
            }
            _shiftResult = _calculator.ShiftDate(from, years, months, days, _subtract.IsChecked == true);
            if (_shiftResult is null)
            {
                ShowError(_calculator.LastError);
                return;
            }
            _result.Text = FormatDate(_shiftResult.ToDateOnly());
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    private void SetSmokeInputs(int operation, DateOnly from, DateOnly to, int years, int months, int days)
    {
        _updatingControls = true;
        try
        {
            _operation.SelectedIndex = operation == 0 ? 0 : 1;
            _add.IsChecked = operation != 2;
            _subtract.IsChecked = operation == 2;
            _from.Date = ToPickerDate(from);
            _to.Date = ToPickerDate(to);
            _years.Value = years;
            _months.Value = months;
            _days.Value = days;
        }
        finally
        {
            _updatingControls = false;
        }
        RefreshResult();
    }

    public bool RunSmokeChecks()
    {
        var saved = (Operation: _operation.SelectedIndex, From: _from.Date, To: _to.Date,
            Years: _years.Value, Months: _months.Value, Days: _days.Value, Subtract: _subtract.IsChecked);
        try
        {
            SetSmokeInputs(1, new DateOnly(2008, 1, 31), new DateOnly(2008, 1, 31), 0, 1, 0);
            if (_hasError || _shiftResult?.ToDateOnly() != new DateOnly(2008, 2, 29)
                || _result.Text != FormatDate(new DateOnly(2008, 2, 29)))
                return false;
            SetSmokeInputs(2, new DateOnly(2008, 3, 10), new DateOnly(2008, 3, 10), 0, 1, 10);
            if (_hasError || _shiftResult?.ToDateOnly() != new DateOnly(2008, 1, 29))
                return false;
            SetSmokeInputs(0, new DateOnly(2008, 3, 31), new DateOnly(2008, 2, 29), 0, 0, 0);
            if (_hasError || _difference is not { Months: 1, Days: 2, TotalDays: 31 } || string.IsNullOrWhiteSpace(_totalDays.Text))
                return false;
            SetSmokeInputs(2, MinimumDate, MinimumDate, 0, 0, 1);
            if (!_hasError || string.IsNullOrWhiteSpace(_calculator.LastError) || _result.Text != _calculator.LastError)
                return false;
            SetSmokeInputs(1, MaximumDate, MaximumDate, 0, 0, 1);
            if (!_hasError || string.IsNullOrWhiteSpace(_calculator.LastError) || _result.Text != _calculator.LastError)
                return false;
            SetSmokeInputs(0, MinimumDate, MaximumDate, 0, 0, 0);
            return !_hasError && _difference is { Years: 8398, Months: 11, Weeks: 4, Days: 2, TotalDays: 3067670 };
        }
        catch (Exception error)
        {
            ShowError($"Date check failed: {error.Message}");
            return false;
        }
        finally
        {
            _updatingControls = true;
            try
            {
                _operation.SelectedIndex = saved.Operation;
                _subtract.IsChecked = saved.Subtract;
                _add.IsChecked = saved.Subtract != true;
                _from.Date = saved.From;
                _to.Date = saved.To;
                _years.Value = saved.Years;
                _months.Value = saved.Months;
                _days.Value = saved.Days;
            }
            finally
            {
                _updatingControls = false;
            }
            RefreshResult();
        }
    }
}
