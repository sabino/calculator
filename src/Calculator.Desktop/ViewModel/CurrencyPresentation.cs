// Licensed under the MIT License.
using System.Globalization;

namespace CalculatorApp.Desktop;

internal static class CurrencyPresentation
{
    private static readonly IReadOnlyDictionary<string, (string Name, string Symbol)> Currencies = BuildCurrencies();

    private static IReadOnlyDictionary<string, (string Name, string Symbol)> BuildCurrencies()
    {
        var currencies = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures).OrderBy(culture => culture.Name, StringComparer.Ordinal))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                currencies.TryAdd(region.ISOCurrencySymbol, ($"{region.EnglishName} - {region.CurrencyEnglishName}", region.CurrencySymbol));
            }
            catch (ArgumentException) { }
        }
        // Shared currencies and ambiguous region symbols need their standard labels.
        currencies["EUR"] = ("Europe - Euro", "€");
        currencies["USD"] = ("United States - Dollar", "$");
        currencies["GBP"] = ("United Kingdom - Pound", "£");
        currencies["JPY"] = ("Japan - Yen", "¥");
        currencies["CNY"] = ("China - Yuan", "¥");
        currencies["BRL"] = ("Brazil - Real", "R$");
        currencies["AUD"] = ("Australia - Dollar", "$");
        currencies["CAD"] = ("Canada - Dollar", "$");
        currencies["NZD"] = ("New Zealand - Dollar", "$");
        currencies["CHF"] = ("Switzerland - Franc", "CHF");
        return currencies;
    }

    internal static string Name(string code) => Currencies.TryGetValue(code, out var currency) ? currency.Name : code;
    internal static string Symbol(string code) => Currencies.TryGetValue(code, out var currency) ? currency.Symbol : code;
}
