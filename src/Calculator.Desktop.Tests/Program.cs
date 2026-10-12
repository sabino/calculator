// Licensed under the MIT License.
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using CalculatorApp.Desktop;

internal static class Program
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Type(NativeCalculator calculator, string text)
    {
        foreach (char key in text)
            Check(calculator.PressKey(key.ToString()), $"Key '{key}' rejected: {calculator.LastError}");
    }
    private static void Command(NativeCalculator calculator, int command) =>
        Check(calculator.SendCommand(command), $"Command {command} rejected: {calculator.LastError}");

    private static void CheckCalculator()
    {
        using var calculator = new NativeCalculator(CultureInfo.GetCultureInfo("en-US"));
        Type(calculator, "2+3*4=");
        var retained = calculator.GetSnapshot();
        Check(retained.Primary == "20", $"Standard arithmetic expected 20, got {retained.Primary}");
        Check(retained.History.Count > 0 && retained.History[^1].Result == "20", "History missed arithmetic result");
        Command(calculator, 124); // CommandSTORE from CalcManager/Command.h.
        Command(calculator, 81); // CommandCLEAR.
        Check(calculator.RecallMemory(0), calculator.LastError);
        Check(calculator.GetSnapshot().Primary == "20", "Memory recall lost stored value");
        Check(calculator.RemoveMemory(0) && calculator.GetSnapshot().Memory.Count == 0, "Memory removal failed");
        Check(!calculator.RecallMemory(0) && calculator.LastError.Length > 0, "Invalid memory index was accepted");
        Check(calculator.RecallHistory(0), calculator.LastError);
        Check(calculator.ClearHistory() && calculator.GetSnapshot().History.Count == 0, "History clearing failed");
        Check(retained.History.Count > 0, "Retained snapshot changed after clearing native history");

        Check(calculator.SetMode(1), calculator.LastError);
        Command(calculator, 81);
        Type(calculator, "2+3*4=");
        Check(calculator.GetSnapshot().Primary == "14", "Scientific mode lost arithmetic precedence");
        Command(calculator, 81);
        Check(calculator.PasteNumber("5"), calculator.LastError);
        Command(calculator, 412); // CommandPOW2.
        Check(calculator.GetSnapshot().Primary == "32", "Scientific 2^x failed");
        Command(calculator, 146);
        Check(calculator.GetSnapshot().ScientificShift, "Second functions were not selected");
        Check(calculator.PasteNumber("8"), calculator.LastError);
        Command(calculator, 116);
        Check(calculator.GetSnapshot().Primary == "2" && !calculator.GetSnapshot().ScientificShift,
            "Second-function cube root did not restore the primary keypad");
        Check(calculator.PasteNumber("2+3*4="), calculator.LastError);
        Check(calculator.GetSnapshot().Primary == "14", "Expression paste lost scientific precedence");
        Check(!calculator.PasteNumber("2+") && calculator.GetSnapshot().Primary == "14", "Malformed paste changed accepted state");

        Check(calculator.SetMode(2), calculator.LastError);
        Command(calculator, 313); // CommandHex.
        Command(calculator, 81);
        Type(calculator, "FF");
        Command(calculator, 314); // CommandDec.
        Check(calculator.GetSnapshot().Primary == "255", "Programmer hexadecimal conversion failed");
        Check(calculator.GetSnapshot().RadixDisplays.Single(display => display.Radix == 16).Value == "FF",
            "Programmer radix labels lost the native hexadecimal result");
        Command(calculator, 316); // CommandBin.
        Check(!calculator.PressKey("2") && calculator.LastError.Length > 0, "Binary mode accepted digit 2");
        Check(calculator.GetSnapshot().BitValues.Count == 64, "Programmer bit snapshot is incomplete");
        Check(!calculator.SendCommand(200), "Internal mode command crossed the public command boundary");
        Check(!calculator.SetMode(99) && calculator.LastError.Length > 0, "Invalid mode was accepted");

        Check(calculator.SetMode(3) && calculator.SetCategory(1), calculator.LastError);
        var units = calculator.GetSnapshot().Units;
        int meters = units.ToList().IndexOf("Meters"), feet = units.ToList().IndexOf("Feet");
        Check(meters >= 0 && feet >= 0, "Length conversion unit names missing");
        Check(calculator.SetUnits(meters, feet) && calculator.PasteNumber("1"), calculator.LastError);
        Check(calculator.GetSnapshot().Converted.StartsWith("3.280", StringComparison.Ordinal), "Meters-to-feet conversion failed");
        Check(!calculator.SetUnits(-1, feet), "Invalid converter unit accepted");
        Check(calculator.GetSnapshot().Categories.Count == 13, "All converter categories must be available");
        Check(!calculator.GetSnapshot().SupportsNegative, "Length must hide the converter sign key");
        Check(calculator.SetCategory(3) && calculator.GetSnapshot().SupportsNegative, "Temperature must allow signed converter input");
        Check(calculator.SetCategory(12), calculator.LastError);
        Check(!calculator.GetSnapshot().Currency.Available && !calculator.PasteNumber("1"), "Unloaded currency rates must not yield a fake conversion");
        Check(calculator.SetCurrencyRates(1, "2026-10-09", "2026-10-12", "2026-10-12T12:00:00Z",
            "EUR=1\nUSD=1.1218\nBRL=5.6181\nAED=4.1198", false), calculator.LastError);
        var currencies = calculator.GetSnapshot();
        int dollars = currencies.Units.ToList().IndexOf("USD"), euros = currencies.Units.ToList().IndexOf("EUR");
        Check(calculator.SetUnits(euros, dollars) && calculator.PasteNumber("1"), calculator.LastError);
        Check(calculator.GetSnapshot().Converted.StartsWith("1.1218", StringComparison.Ordinal), "Currency direction/rate precision was lost");
        Check(Math.Abs(calculator.GetSnapshot().CurrencyRatio - 1.1218) < 1e-12 && !calculator.GetSnapshot().SupportsNegative,
            "Displayed currency ratio or sign-key visibility was lost");
        Check(calculator.GetSnapshot().Currency.ReferenceEndDate == "2026-10-12"
            && calculator.GetSnapshot().Currency.ProviderId == 1, "Currency provider/date range was lost across interop");
        Check(!calculator.SetCurrencyRates(1, "2026-10-13", "2026-10-12", "2026-10-12T12:00:00Z", "EUR=1\nUSD=0", false),
            "Invalid dated currency data was accepted");
        Check(calculator.GetSnapshot().Converted.StartsWith("1.1218", StringComparison.Ordinal), "Rejected rates destroyed the usable conversion");
        Check(calculator.SetCurrencyStatus(4, "Offline") && calculator.GetSnapshot().Currency.Available,
            "Refresh failure must preserve usable currency data");

        var about = calculator.GetAbout();
        Check(about.Version.Split('.').Length == 4 && about.License.Contains("MIT License", StringComparison.Ordinal), "About package identity/license missing");
        var difference = calculator.GetDateDifference(new DateOnly(2024, 2, 28), new DateOnly(2024, 3, 1));
        Check(difference?.TotalDays == 2, "Leap-year date difference failed");
        var shifted = calculator.ShiftDate(new DateOnly(2024, 1, 31), 0, 1, 0);
        Check(shifted?.ToDateOnly() == new DateOnly(2024, 2, 29), "Month-end date clamping failed");
        Check(calculator.ShiftDate(new DateOnly(2024, 1, 1), -1, 0, 0) is null && calculator.LastError.Length > 0,
            "Negative date duration accepted");
        Check(calculator.GetDateDifference(new DateOnly(1600, 1, 1), new DateOnly(2024, 1, 1)) is null, "Out-of-range date accepted");

        using var localized = new NativeCalculator(CultureInfo.GetCultureInfo("fr-FR"));
        Check(localized.PasteNumber("12,5"), localized.LastError);
        Check(localized.GetSnapshot().Primary == "12,5", "UTF-8 locale/decimal setting was lost");
        Check(localized.GetSnapshot().ButtonRows.SelectMany(row => row).Any(button => button.Title.Contains(',')), "Decimal button did not reflect locale");
        Check(!localized.PasteNumber("12\0,5") && localized.LastError.Length > 0, "Embedded NUL input was truncated silently");
        Check(!localized.PasteNumber("\ud800"), "Unpaired surrogate input was replaced silently");
        Check(localized.GetSnapshot().Primary == "12,5", "Invalid managed text changed native display");
        calculator.Dispose();
        bool disposed = false;
        try { calculator.GetSnapshot(); } catch (ObjectDisposedException) { disposed = true; }
        Check(disposed, "Disposed calculator still exposed native state");
    }

    private static void CheckGraph()
    {
        using var graph = new NativeGraph();
        Check(graph.Update(new[] { "y=a*x", "y<1" }, 1), graph.LastError);
        var original = graph.GetSnapshot();
        Check(original.Equations.Count == 2 && original.AngleUnit == 1, "Graph equation/angle snapshot mismatch");
        Check(original.Equations[0].Parameters.Any(parameter => parameter.Name == "a"), "Graph parameter discovery failed");
        Check(graph.SetParameter(0, "a", 2), graph.LastError);
        Check(graph.GetSnapshot().Equations[0].Parameters.Single(parameter => parameter.Name == "a").Value == 2, "Graph parameter mutation failed");
        Check(!graph.Update("y="), "Invalid equation was accepted");
        Check(graph.LastError.Length > 0, "Invalid graph omitted actionable error");
        Check(graph.GetSnapshot().Equations[0].Input == "y=a*x", "Invalid graph destroyed the previous equation");
        Check(original.Equations[0].Parameters.Single(parameter => parameter.Name == "a").Value != 2, "Retained graph snapshot changed after mutation");
        Check(graph.SetVisible(1, false) && graph.SetSelected(0) && graph.SetColor(0, "#12AB34"), graph.LastError);
        Check(graph.GetSnapshot().Equations[0].Color == "#12AB34", "Graph color conversion changed channels");
        Check(graph.SetRange(-4, 4, -4, 4) && graph.Zoom(0.5), graph.LastError);
        Check(graph.GetSnapshot().Range.Right - graph.GetSnapshot().Range.Left == 4, "Graph zoom factor changed semantics");
        Check(graph.Pan(0.5, 0), graph.LastError);
        Check(graph.GetSnapshot().Range.Left == -1, "Graph normalized panning failed");
        Check(!graph.SetRange(1, -1, -2, 2) && graph.LastError.Length > 0, "Inverted graph range was accepted");
        Check(graph.SetRange(-4, 4, -4, 4), graph.LastError);
        var scene = graph.GetScene(320, 240);
        Check(scene.Width == 320 && scene.Height == 240 && scene.Strokes.Any(stroke => stroke.EquationId == 0), "Typed graph scene lost curve geometry");
        Check(scene.Strokes.All(stroke => stroke.Color.Length == 9) && scene.Background.Length == 9, "Scene ARGB schema mismatch");
        var hit = graph.Trace(0, 0, 0.01);
        Check(hit is not null && hit.Row == 0 && Math.Abs(hit.X) < 0.1, "Graph tracing failed at the origin");
        Check(!string.IsNullOrWhiteSpace(graph.Analyze(0)), "Graph analysis report missing");
        var png = graph.RenderPng(320, 240);
        Check(png is not null && png.Length > 24 && png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "PNG export has no valid signature");
        Check(BinaryPrimitives.ReadUInt32BigEndian(png!.AsSpan(16, 4)) == 320 && BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)) == 240,
            "PNG IHDR dimensions mismatch");
        Check(graph.RenderPng(-1, 240) is null && graph.LastError.Length > 0, "Invalid PNG dimensions were accepted");
        Check(!graph.SetParameter(0, "a", double.NaN), "Nonfinite graph parameter accepted");
        Check(!graph.SetColor(0, "bad"), "Invalid color accepted");
        Check(graph.SetVisible(1, true), graph.LastError);
        Check(graph.GetScene(320, 240).Fills.Count > 0, "Inequality scene lost fill polygons");
        Check(graph.SetLineStyle(0, 2) && graph.SetParameterBounds(0, "a", -1, 1, 0.25), graph.LastError);
        Check(graph.GetSnapshot().Equations[0].Parameters.Single(parameter => parameter.Name == "a").Value == 2,
            "Changing parameter bounds must preserve an out-of-range value");
        Check(graph.SetEquation(1, "y=3*x"), graph.LastError);
        Check(graph.GetSnapshot().Equations[0].LineStyle == 2, "Row edits changed the neighbor's appearance");
        Check(graph.SetFeatures(3) && graph.SetGrid(true, false), graph.LastError);
        Check(graph.GetSnapshot().Settings.FeaturesMask == 3 && !graph.GetSnapshot().Settings.ShowGrid, "Graph feature/grid settings contract failed");
        Check(graph.SetSettings(3, "#FF111122", "#FFEEEEEE", "#FFAAAAAA", "#FF333333"), graph.LastError);
        Check(graph.GetScene(320, 240).Background == "#FF111122", "Graph settings did not reach the renderer");
        Check(graph.RemoveEquation(1) && graph.GetSnapshot().Equations.Count == 1, graph.LastError);
        graph.Dispose();
        bool disposed = false;
        try { graph.GetSnapshot(); } catch (ObjectDisposedException) { disposed = true; }
        Check(disposed, "Disposed graph still exposed native state");
    }

    public static async Task<int> Main(string[] arguments)
    {
        try
        {
            if (arguments is ["--currency-provider"])
            {
                await CurrencyTests.RunAsync();
                Console.WriteLine("Currency providers and offline cache passed.");
                return 0;
            }
            if (arguments.Length > 1) throw new ArgumentException("Usage: Calculator.Desktop.Tests [absolute-native-library-path]");
            if (arguments.Length == 1)
            {
                string library = Path.GetFullPath(arguments[0]);
                NativeLibrary.SetDllImportResolver(typeof(NativeCalculator).Assembly,
                    (name, assembly, searchPath) => name == "CalculatorNative" ? NativeLibrary.Load(library) : IntPtr.Zero);
            }
            CheckCalculator();
            await CurrencyTests.RunAsync();
            using (var calculator = new NativeCalculator())
            {
                if (calculator.GetAbout().GraphingEnabled) CheckGraph();
            }
            Console.WriteLine("Calculator/graph native C ABI interop passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Native interop failed: {error}");
            return 1;
        }
    }
}
