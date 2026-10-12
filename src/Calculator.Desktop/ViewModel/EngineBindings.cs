// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace CalculatorApp.Desktop;

public sealed record CalculatorButton(string Title, int Command, string AccessibleName, bool Enabled);
public sealed record CalculatorMenu(string Title, IReadOnlyList<CalculatorButton> Buttons);
public sealed record CalculatorHistory(string Expression, string Result);
public sealed record CalculatorRadixDisplay(int Radix, string Value);
public sealed record CurrencyStatus(bool Available, string Provider, string SourceUrl, string Attribution,
    string ReferenceDate, string FetchedAt, string Status, string Error)
{
    public string ReferenceEndDate { get; init; } = "";
    public int ProviderId { get; init; }
}
public sealed record CalculatorSnapshot(int Mode, string Primary, string Expression, string Converted, string Notice,
    bool IsError, IReadOnlyList<string> Memory, IReadOnlyList<CalculatorHistory> History,
    IReadOnlyList<IReadOnlyList<CalculatorButton>> ButtonRows, IReadOnlyList<CalculatorButton> MemoryButtons,
    IReadOnlyList<CalculatorMenu> ScientificMenus, int Radix, int WordWidth, int AngleCommand,
    IReadOnlyList<CalculatorButton> AngleButtons, IReadOnlyList<CalculatorButton> BaseButtons,
    IReadOnlyList<CalculatorButton> WordButtons, IReadOnlyList<bool> BitValues, IReadOnlyList<string> Categories,
    IReadOnlyList<string> Units, int CategoryIndex, int FromUnitIndex, int ToUnitIndex)
{
    public IReadOnlyList<CalculatorButton> BitButtons { get; init; } = Array.Empty<CalculatorButton>();
    public IReadOnlyList<CalculatorRadixDisplay> RadixDisplays { get; init; } = Array.Empty<CalculatorRadixDisplay>();
    public bool SupportsNegative { get; init; }
    public double CurrencyRatio { get; init; }
    public bool ScientificShift { get; init; }
    public bool ScientificFormat { get; init; }
    public CalculatorButton? ScientificFormatButton { get; init; }
    public CurrencyStatus Currency { get; init; } = new(false, "", "", "", "", "", "unavailable", "");
}
public sealed record CalculatorAbout(string Version, string Copyright, string License,
    string SourceUrl, string PrivacyUrl, string ServicesUrl)
{
    public bool GraphingEnabled { get; init; }
    public string GraphingVersion { get; init; } = "";
}
public sealed record CivilDate(int Year, int Month, int Day)
{
    public DateOnly ToDateOnly() => new(Year, Month, Day);
}
public sealed record DateDifference(int Years, int Months, int Weeks, int Days, int TotalDays);
public sealed record GraphParameter(string Name, double Value, double Min, double Max, double Step);
public sealed record GraphEquation(string Input, string Color, bool Visible, bool Selected, IReadOnlyList<GraphParameter> Parameters)
{
    public int LineStyle { get; init; }
}
public sealed record GraphSettings(double LineWidth, string Background, string Text, string Axis, string Grid)
{
    public uint FeaturesMask { get; init; }
    public bool ShowAxes { get; init; } = true;
    public bool ShowGrid { get; init; } = true;
}
public sealed record GraphRange(double Left, double Right, double Bottom, double Top);
public sealed record GraphRenderCapabilities(string Backend, bool Axes, bool Grid, string SceneLabels, string PngLabels);
public sealed record GraphSnapshot(IReadOnlyList<GraphEquation> Equations, int SelectedIndex, int AngleUnit,
    GraphRange Range, string Analysis, string RenderWarning, GraphRenderCapabilities RenderCapabilities)
{
    public GraphSettings Settings { get; init; } = new(2, "#FFFFFFFF", "#FF2D2D2D", "#FF646464", "#FFDCDCDC");
}
public sealed record GraphPoint(double X, double Y);
public sealed record GraphStroke(IReadOnlyList<GraphPoint> Points, string Color, double Width, int Style, int EquationId);
public sealed record GraphFill(IReadOnlyList<GraphPoint> Points, string Color);
public sealed record GraphLabel(double X, double Y, string Text, string Color);
public sealed record GraphScene(int Width, int Height, string Background, IReadOnlyList<GraphStroke> Strokes,
    IReadOnlyList<GraphFill> Fills, IReadOnlyList<GraphLabel> Labels, bool HasMissingData)
{
    public GraphRange? Range { get; init; }
    public GraphSettings? Settings { get; init; }
}
public sealed record GraphTrace(int Row, double X, double Y, double ScreenX, double ScreenY, double? Rho, double? Theta, double? T);

internal static class BridgeJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        Converters = { new ReadOnlyLists() }
    };

    internal static bool IsValidText(string? text)
    {
        if (text is null || text.Contains('\0')) return false;
        try { StrictUtf8.GetByteCount(text); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    internal static string Copy(IntPtr pointer) => Marshal.PtrToStringUTF8(pointer)
        ?? throw new InvalidOperationException("Native bridge returned a null string.");

    internal static T Read<T>(IntPtr pointer) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(Copy(pointer), Options)
                ?? throw new InvalidOperationException("Native bridge returned a null JSON document.");
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException("Native bridge returned an invalid JSON contract.", error);
        }
    }

    // Freeze every nested collection so snapshots cannot change after a native call.
    private sealed class ReadOnlyLists : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>);
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(ReadOnlyList<>).MakeGenericType(type.GetGenericArguments()[0]), true)!;
    }
    private sealed class ReadOnlyList<T> : JsonConverter<IReadOnlyList<T>>
    {
        public override bool HandleNull => true;
        public override IReadOnlyList<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            Array.AsReadOnly(JsonSerializer.Deserialize<T[]>(ref reader, options) ?? throw new JsonException("Null collection."));
        public override void Write(Utf8JsonWriter writer, IReadOnlyList<T> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.ToArray(), options);
    }
}

internal sealed class CalculatorHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal CalculatorHandle(IntPtr value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle() => NativeMethods.calc_destroy(handle) != 0;
}
internal sealed class GraphHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal GraphHandle(IntPtr value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle() { NativeMethods.calc_graph_destroy(handle); return true; }
}

public sealed class NativeCalculator : IDisposable
{
    private readonly object gate = new();
    private readonly CalculatorHandle handle;
    private string lastError = "";
    public string LastError { get { lock (gate) return lastError; } }

    public NativeCalculator(CultureInfo? culture = null, string? region = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (region is null)
        {
            try { region = new RegionInfo(culture.Name).TwoLetterISORegionName; }
            catch (ArgumentException) { region = "US"; }
        }
        string locale = string.IsNullOrEmpty(culture.Name) ? "en-US" : culture.Name;
        var value = NativeMethods.calc_create(locale, region, culture.NumberFormat.NumberDecimalSeparator,
            culture.NumberFormat.NumberGroupSeparator);
        if (value == IntPtr.Zero)
            throw new InvalidOperationException(BridgeJson.Copy(NativeMethods.calc_creation_error(IntPtr.Zero)));
        handle = new CalculatorHandle(value);
    }

    private void EnsureOpen() => ObjectDisposedException.ThrowIf(handle.IsClosed, this);
    private bool Apply(Func<int> action)
    {
        lock (gate)
        {
            EnsureOpen();
            bool success = action() != 0;
            lastError = success ? "" : BridgeJson.Copy(NativeMethods.calc_last_error(handle));
            return success;
        }
    }
    private bool ApplyText(string text, Func<int> action) => ApplyTexts([text], action);
    private bool ApplyTexts(IReadOnlyList<string> inputs, Func<int> action)
    {
        lock (gate)
        {
            EnsureOpen();
            if (inputs.Any(text => !BridgeJson.IsValidText(text))) { lastError = "Input must contain valid UTF-8 text without NUL characters."; return false; }
            return Apply(action);
        }
    }
    private T Get<T>(Func<IntPtr> action) where T : class
    {
        lock (gate)
        {
            EnsureOpen();
            var pointer = action();
            if (pointer == IntPtr.Zero)
            {
                lastError = BridgeJson.Copy(NativeMethods.calc_last_error(handle));
                throw new InvalidOperationException(lastError);
            }
            var result = BridgeJson.Read<T>(pointer);
            lastError = "";
            return result;
        }
    }
    private T? TryGet<T>(Func<IntPtr> action) where T : class
    {
        lock (gate)
        {
            EnsureOpen();
            var pointer = action();
            if (pointer == IntPtr.Zero) { lastError = BridgeJson.Copy(NativeMethods.calc_last_error(handle)); return null; }
            var result = BridgeJson.Read<T>(pointer);
            lastError = "";
            return result;
        }
    }
    public CalculatorSnapshot GetSnapshot() => Get<CalculatorSnapshot>(() => NativeMethods.calc_get_state(handle));
    public CalculatorAbout GetAbout() => Get<CalculatorAbout>(() => NativeMethods.calc_get_about(handle));
    public bool SetMode(int mode) => Apply(() => NativeMethods.calc_set_mode(handle, mode));
    public bool SendCommand(int command) => Apply(() => NativeMethods.calc_send_command(handle, command));
    public bool PressKey(string key) => ApplyText(key, () => NativeMethods.calc_press_key(handle, key));
    public bool PasteNumber(string number) => ApplyText(number, () => NativeMethods.calc_paste_number(handle, number));
    public bool SetCategory(int category) => Apply(() => NativeMethods.calc_set_category(handle, category));
    public bool SetUnits(int from, int to) => Apply(() => NativeMethods.calc_set_units(handle, from, to));
    public bool SetCurrencyRates(string referenceDate, string fetchedUtc, string rates, bool fromCache = false) =>
        ApplyTexts([referenceDate, fetchedUtc, rates], () =>
            NativeMethods.calc_currency_set_rates(handle, referenceDate, fetchedUtc, rates, fromCache ? 1 : 0));
    public bool SetCurrencyStatus(int status, string message = "") =>
        ApplyText(message, () => NativeMethods.calc_currency_set_status(handle, status, message));
    public bool SetCurrencyRates(int provider, string referenceDate, string referenceEndDate, string fetchedUtc, string rates, bool fromCache) =>
        ApplyTexts([referenceDate, referenceEndDate, fetchedUtc, rates], () =>
            NativeMethods.calc_currency_set_rates_ex(handle, provider, referenceDate, referenceEndDate, fetchedUtc, rates, fromCache ? 1 : 0));
    public bool MemoryAction(int operation, int index) => Apply(() => NativeMethods.calc_memory_action(handle, operation, index));
    public bool HistoryAction(int operation, int index = 0) => Apply(() => NativeMethods.calc_history_action(handle, operation, index));
    public bool RecallMemory(int index) => MemoryAction(0, index);
    public bool AddToMemory(int index) => MemoryAction(1, index);
    public bool SubtractFromMemory(int index) => MemoryAction(2, index);
    public bool RemoveMemory(int index) => MemoryAction(3, index);
    public bool RecallHistory(int index) => HistoryAction(0, index);
    public bool RemoveHistory(int index) => HistoryAction(1, index);
    public bool ClearHistory() => HistoryAction(2);
    // Date operations do not mutate mode or display. Null means invalid input; inspect LastError.
    public DateDifference? GetDateDifference(DateOnly first, DateOnly second) => TryGet<DateDifference>(() =>
        NativeMethods.calc_date_difference(handle, first.Year, first.Month, first.Day, second.Year, second.Month, second.Day));
    public CivilDate? ShiftDate(DateOnly date, int years, int months, int days, bool subtract = false) => TryGet<CivilDate>(() =>
        NativeMethods.calc_date_shift(handle, date.Year, date.Month, date.Day, years, months, days, subtract ? 1 : 0));
    public void Dispose() { lock (gate) handle.Dispose(); }
}

public sealed class NativeGraph : IDisposable
{
    private readonly object gate = new();
    private readonly GraphHandle handle;
    private string lastError = "";
    public string LastError { get { lock (gate) return lastError; } }
    public NativeGraph()
    {
        var value = NativeMethods.calc_graph_create();
        if (value == IntPtr.Zero) throw new InvalidOperationException(BridgeJson.Copy(NativeMethods.calc_graph_creation_error(IntPtr.Zero)));
        handle = new GraphHandle(value);
    }
    private void EnsureOpen() => ObjectDisposedException.ThrowIf(handle.IsClosed, this);
    private bool Apply(Func<int> action)
    {
        lock (gate)
        {
            EnsureOpen();
            bool success = action() != 0;
            lastError = success ? "" : BridgeJson.Copy(NativeMethods.calc_graph_last_error(handle));
            return success;
        }
    }
    private bool ApplyText(string text, Func<int> action)
    {
        lock (gate)
        {
            EnsureOpen();
            if (!BridgeJson.IsValidText(text)) { lastError = "Input must contain valid UTF-8 text without NUL characters."; return false; }
            return Apply(action);
        }
    }
    private T? Read<T>(Func<IntPtr> action, bool required) where T : class
    {
        lock (gate)
        {
            EnsureOpen();
            var pointer = action();
            if (pointer == IntPtr.Zero)
            {
                lastError = BridgeJson.Copy(NativeMethods.calc_graph_last_error(handle));
                if (required) throw new InvalidOperationException(lastError);
                return null;
            }
            var result = BridgeJson.Read<T>(pointer);
            lastError = "";
            return result;
        }
    }
    public bool Update(string equations, int angleUnit = 0) => ApplyText(equations, () => NativeMethods.calc_graph_update(handle, equations, angleUnit));
    public bool Update(IEnumerable<string> equations, int angleUnit = 0) => Update(string.Join("\n", equations), angleUnit);
    public bool SetEquation(int row, string equation) => ApplyText(equation, () => NativeMethods.calc_graph_set_equation(handle, row, equation));
    public bool RemoveEquation(int row) => Apply(() => NativeMethods.calc_graph_remove_equation(handle, row));
    public bool SetLineStyle(int row, int style) => Apply(() => NativeMethods.calc_graph_set_line_style(handle, row, style));
    public bool SetFeatures(uint mask) => Apply(() => NativeMethods.calc_graph_set_features(handle, mask));
    public bool SetGrid(bool axes, bool grid) => Apply(() => NativeMethods.calc_graph_set_grid(handle, axes ? 1 : 0, grid ? 1 : 0));
    public bool SetParameterBounds(int row, string name, double min, double max, double step) =>
        ApplyText(name, () => NativeMethods.calc_graph_set_parameter_bounds(handle, row, name, min, max, step));
    public bool SetSettings(double lineWidth, string background, string text, string axis, string grid)
    {
        lock (gate)
        {
            EnsureOpen();
            if (!TryReadRgba(background, out var backgroundRgba) || !TryReadRgba(text, out var textRgba)
                || !TryReadRgba(axis, out var axisRgba) || !TryReadRgba(grid, out var gridRgba))
            { lastError = "Graph colors must be #RRGGBB or #AARRGGBB."; return false; }
            return Apply(() => NativeMethods.calc_graph_set_settings(handle, lineWidth, backgroundRgba, textRgba, axisRgba, gridRgba));
        }
    }
    private static bool TryReadRgba(string color, out uint rgba)
    {
        rgba = 0;
        if (color is null || (color.Length != 7 && color.Length != 9) || color[0] != '#'
            || !uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return false;
        rgba = color.Length == 7 ? (value << 8) | 255u : (value << 8) | (value >> 24);
        return true;
    }
    public GraphSnapshot GetSnapshot() => Read<GraphSnapshot>(() => NativeMethods.calc_graph_get_state(handle), true)!;
    public GraphScene GetScene(int width, int height)
    {
        lock (gate)
        {
            var state = GetSnapshot();
            return Read<GraphScene>(() => NativeMethods.calc_graph_get_scene(handle, width, height), true)!
                with { Range = state.Range, Settings = state.Settings };
        }
    }
    public bool SetParameter(int row, string name, double value) => ApplyText(name, () => NativeMethods.calc_graph_set_parameter(handle, row, name, value));
    public bool SetVisible(int row, bool visible) => Apply(() => NativeMethods.calc_graph_set_visible(handle, row, visible ? 1 : 0));
    public bool SetSelected(int row) => Apply(() => NativeMethods.calc_graph_set_selected(handle, row));
    public bool SetColor(int row, uint rgba) => Apply(() => NativeMethods.calc_graph_set_color(handle, row, rgba));
    public bool SetColor(int row, string color)
    {
        lock (gate)
        {
            EnsureOpen();
            if (!TryReadRgba(color, out var rgba))
            { lastError = "Color must be #RRGGBB or #AARRGGBB."; return false; }
            return SetColor(row, rgba);
        }
    }
    public bool SetRange(double left, double right, double bottom, double top) => Apply(() => NativeMethods.calc_graph_set_range(handle, left, right, bottom, top));
    public bool SetRange(GraphRange range) => SetRange(range.Left, range.Right, range.Bottom, range.Top);
    // Coordinates are normalized [-1,1]. A pan of +1 moves half the viewport span.
    public bool Zoom(double factor, double centerXNormalized = 0, double centerYNormalized = 0) =>
        Apply(() => NativeMethods.calc_graph_zoom(handle, factor, centerXNormalized, centerYNormalized));
    public bool Pan(double xNormalized, double yNormalized) => Apply(() => NativeMethods.calc_graph_pan(handle, xNormalized, yNormalized));
    public GraphTrace? Trace(double xNormalized, double yNormalized, double precision = 0.001) =>
        Read<GraphTrace>(() => NativeMethods.calc_graph_trace(handle, xNormalized, yNormalized, precision), false);
    public GraphTrace? TraceSelected(double xNormalized, double yNormalized, double precision = 0.001) =>
        Read<GraphTrace>(() => NativeMethods.calc_graph_trace_selected(handle, xNormalized, yNormalized, precision), false);
    public string? Analyze(int row)
    {
        lock (gate)
        {
            EnsureOpen();
            var pointer = NativeMethods.calc_graph_analyze(handle, row);
            if (pointer == IntPtr.Zero) { lastError = BridgeJson.Copy(NativeMethods.calc_graph_last_error(handle)); return null; }
            string report = BridgeJson.Copy(pointer);
            lastError = "";
            return report;
        }
    }
    public byte[]? RenderPng(int width, int height)
    {
        lock (gate)
        {
            EnsureOpen();
            if (NativeMethods.calc_graph_render_png(handle, width, height, out var bytes, out var length) == 0)
            { lastError = BridgeJson.Copy(NativeMethods.calc_graph_last_error(handle)); return null; }
            if (bytes == IntPtr.Zero || length == 0 || length > int.MaxValue)
                throw new InvalidOperationException("Native bridge returned an invalid PNG buffer.");
            var result = new byte[(int)length];
            Marshal.Copy(bytes, result, 0, result.Length);
            lastError = "";
            return result;
        }
    }
    public void Dispose() { lock (gate) handle.Dispose(); }
}

internal static class NativeMethods
{
    private const string Library = "CalculatorNative";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr calc_create([MarshalAs(UnmanagedType.LPUTF8Str)] string locale, [MarshalAs(UnmanagedType.LPUTF8Str)] string region, [MarshalAs(UnmanagedType.LPUTF8Str)] string decimalSeparator, [MarshalAs(UnmanagedType.LPUTF8Str)] string groupSeparator);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_destroy(IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr calc_last_error(CalculatorHandle handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr calc_get_state(CalculatorHandle handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr calc_get_about(CalculatorHandle handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_set_mode(CalculatorHandle handle, int mode);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_send_command(CalculatorHandle handle, int command);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_press_key(CalculatorHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_paste_number(CalculatorHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string number);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_set_category(CalculatorHandle handle, int category);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_set_units(CalculatorHandle handle, int from, int to);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_currency_set_rates(CalculatorHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string referenceDate,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fetchedUtc, [MarshalAs(UnmanagedType.LPUTF8Str)] string rates, int fromCache);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_currency_set_status(CalculatorHandle handle, int status, [MarshalAs(UnmanagedType.LPUTF8Str)] string message);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_currency_set_rates_ex(CalculatorHandle handle, int provider,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string referenceDate, [MarshalAs(UnmanagedType.LPUTF8Str)] string referenceEndDate,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fetchedUtc, [MarshalAs(UnmanagedType.LPUTF8Str)] string rates, int fromCache);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_memory_action(CalculatorHandle handle, int operation, int index);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_history_action(CalculatorHandle handle, int operation, int index);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr calc_date_difference(CalculatorHandle handle, int year1, int month1, int day1, int year2, int month2, int day2);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr calc_date_shift(CalculatorHandle handle, int year, int month, int day, int years, int months, int days, int subtract);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr calc_graph_create();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void calc_graph_destroy(IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr calc_graph_last_error(GraphHandle handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr calc_graph_get_state(GraphHandle handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_graph_update(GraphHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string equations, int angleUnit);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_graph_set_equation(GraphHandle handle, int row, [MarshalAs(UnmanagedType.LPUTF8Str)] string equation);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_graph_remove_equation(GraphHandle handle, int row);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_graph_set_line_style(GraphHandle handle, int row, int style);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_graph_set_features(GraphHandle handle, uint mask);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_graph_set_grid(GraphHandle handle, int axes, int grid);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_graph_set_parameter_bounds(GraphHandle handle, int row, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        double min, double max, double step);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_graph_set_settings(GraphHandle handle, double lineWidth, uint background, uint text, uint axis, uint grid);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_graph_set_parameter(GraphHandle handle, int row, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, double value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_graph_set_visible(GraphHandle handle, int row, int visible);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_graph_set_selected(GraphHandle handle, int row);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_graph_set_color(GraphHandle handle, int row, uint rgba);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_graph_set_range(GraphHandle handle, double left, double right, double bottom, double top);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_graph_zoom(GraphHandle handle, double factor, double centerXNormalized, double centerYNormalized);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_graph_pan(GraphHandle handle, double xNormalized, double yNormalized);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr calc_graph_get_scene(GraphHandle handle, int width, int height);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int calc_graph_render_png(GraphHandle handle, int width, int height, out IntPtr bytes, out nuint length);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr calc_graph_trace(GraphHandle handle, double xNormalized, double yNormalized, double precision);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr calc_graph_trace_selected(GraphHandle handle, double xNormalized, double yNormalized, double precision);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr calc_graph_analyze(GraphHandle handle, int row);
    [DllImport(Library, EntryPoint = "calc_last_error", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr calc_creation_error(IntPtr handle);
    [DllImport(Library, EntryPoint = "calc_graph_last_error", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr calc_graph_creation_error(IntPtr handle);
}
