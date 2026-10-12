// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace CalculatorApp.Desktop;

public sealed record CurrencyRatesSnapshot(
    DateOnly ReferenceDate, DateTimeOffset FetchedAt, IReadOnlyDictionary<string, double> Rates)
{
    public int ProviderId { get; init; }
    public DateOnly ReferenceEndDate { get; init; } = ReferenceDate;
    public string ReferenceDateText => ReferenceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string ReferenceEndDateText => ReferenceEndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string ProviderName => ProviderId == 1 ? CurrencyRates.FrankfurterProviderName : CurrencyRates.ProviderName;
    public string SourceUrl => ProviderId == 1 ? CurrencyRates.FrankfurterSourceUrl : CurrencyRates.SourceUrl;
    public string FetchedUtc => FetchedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    public string ToRateLines() => string.Join("\n", Rates.OrderBy(rate => rate.Key, StringComparer.Ordinal)
        .Select(rate => $"{rate.Key}={rate.Value.ToString("R", CultureInfo.InvariantCulture)}"));
}

public sealed record CurrencyRatesResult(CurrencyRatesSnapshot? Snapshot, bool IsFromCache, string Error);

// HTTP and cache effects stay outside the portable conversion engine.
public sealed class CurrencyRates : IDisposable
{
    public const string ProviderName = "European Central Bank";
    public const string SourceUrl = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml";
    public const string Attribution = "Reference rates: European Central Bank. Cross rates calculated from euro reference rates.";
    public const string FrankfurterProviderName = "Frankfurter";
    public const string FrankfurterSourceUrl = "https://api.frankfurter.dev/v2/rates?base=EUR";
    public const string FrankfurterAttribution = "Rates: Frankfurter, from central banks and official sources. Cross rates calculated from blended euro reference rates.";
    private const string EcbNamespace = "http://www.ecb.int/vocabulary/2002-08-01/eurofxref";
    private const string EnvelopeNamespace = "http://www.gesmes.org/xml/2002-08-01";
    private const int MaximumBytes = 65536;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> SupportedCodes = new(StringComparer.Ordinal)
    {
        "EUR", "USD", "JPY", "CZK", "DKK", "GBP", "HUF", "PLN", "RON", "SEK", "CHF", "ISK", "NOK", "TRY", "AUD",
        "BRL", "CAD", "CNY", "HKD", "IDR", "ILS", "INR", "KRW", "MXN", "MYR", "NZD", "PHP", "SGD", "THB", "ZAR"
    };
    private static readonly JsonSerializerOptions CacheJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly string _cachePath;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private CurrencyRatesSnapshot? _current;
    private bool _legacyCache;
    private bool _disposed;

    private sealed record CachedRates(
        int Version, string Provider, int ProviderId, string ReferenceDate, string ReferenceEndDate,
        DateTimeOffset FetchedAt, Dictionary<string, double> Rates);

    public static string DefaultCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Calculator", "ecb-rates.json");

    public CurrencyRates(string? cachePath = null, HttpClient? client = null, TimeProvider? timeProvider = null)
    {
        _cachePath = Path.GetFullPath(cachePath ?? DefaultCachePath);
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CurrencyRatesResult> LoadAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string cacheError = "";
            if (_current is null)
            {
                try { _current = await ReadCacheAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (IsDataOrIoFailure(error))
                { cacheError = "Cached currency rates are invalid or unreadable."; }
            }
            var now = _timeProvider.GetUtcNow();
            if (!forceRefresh && !_legacyCache && _current is not null && now - _current.FetchedAt < TimeSpan.FromDays(1))
                return new CurrencyRatesResult(_current, true, "");

            try
            {
                var downloaded = await FetchAsync(cancellationToken).ConfigureAwait(false);
                if (_current is not null && downloaded.ProviderId == _current.ProviderId && downloaded.ReferenceEndDate < _current.ReferenceEndDate)
                    throw new InvalidDataException("Currency provider returned an older reference date.");
                _current = downloaded;
                _legacyCache = false;
                string cacheWriteError = "";
                try { await WriteCacheAsync(downloaded, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { cacheWriteError = "Rates updated, but the currency cache could not be saved."; }
                return new CurrencyRatesResult(downloaded, false, cacheWriteError);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new CurrencyRatesResult(_current, _current is not null, "Currency rate request timed out.");
            }
            catch (Exception error) when (IsDataOrIoFailure(error) || error is HttpRequestException)
            {
                var message = string.IsNullOrEmpty(cacheError)
                    ? "Currency rates could not be updated."
                    : cacheError + " Currency rates could not be updated.";
                return new CurrencyRatesResult(_current, _current is not null, message);
            }
        }
        finally { _loadGate.Release(); }
    }

    public static CurrencyRatesSnapshot ParseXml(string xml, DateTimeOffset fetchedAt)
    {
        ArgumentNullException.ThrowIfNull(xml);
        if (StrictUtf8.GetByteCount(xml) > MaximumBytes)
            throw new InvalidDataException("Currency response exceeds the size limit.");
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumBytes,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        DateOnly? referenceDate = null;
        var rates = new Dictionary<string, double>(StringComparer.Ordinal) { ["EUR"] = 1 };
        bool hasEnvelope = false;
        bool insideReferenceDate = false;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 2 && reader.LocalName == "Cube")
                insideReferenceDate = false;
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (!hasEnvelope)
            {
                if (reader.LocalName != "Envelope" || reader.NamespaceURI != EnvelopeNamespace || reader.Depth != 0)
                    throw new InvalidDataException("Currency response must contain the ECB envelope.");
                hasEnvelope = true;
                continue;
            }
            if (reader.LocalName != "Cube" || reader.NamespaceURI != EcbNamespace) continue;
            var date = reader.GetAttribute("time");
            var code = reader.GetAttribute("currency");
            var rate = reader.GetAttribute("rate");
            if (date is not null)
            {
                if (referenceDate is not null || reader.Depth != 2 || code is not null || rate is not null)
                    throw new InvalidDataException("Currency response must contain one reference date.");
                referenceDate = ParseDate(date);
                insideReferenceDate = !reader.IsEmptyElement;
                continue;
            }
            if (code is null && rate is null) continue;
            if (!insideReferenceDate || reader.Depth != 3 || code is null || rate is null ||
                rate.Length > 32 || rate.Length == 0 || rate[0] < '0' || rate[0] > '9' ||
                !double.TryParse(rate, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture, out var value) || !rates.TryAdd(code, value))
                throw new InvalidDataException("Currency response contains a malformed or duplicate rate.");
        }
        if (!hasEnvelope || referenceDate is null)
            throw new InvalidDataException("Currency response has no reference date.");
        return CreateSnapshot(referenceDate.Value, referenceDate.Value, fetchedAt, rates, 0);
    }

    public static CurrencyRatesSnapshot ParseFrankfurterJson(string json, DateTimeOffset fetchedAt)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (StrictUtf8.GetByteCount(json) > MaximumBytes)
            throw new InvalidDataException("Currency response exceeds the size limit.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() is < 1 or > 255)
            throw new InvalidDataException("Frankfurter rates must contain a bounded array of currency quotes.");
        DateOnly? firstDate = null;
        DateOnly? lastDate = null;
        var rates = new Dictionary<string, double>(StringComparer.Ordinal) { ["EUR"] = 1 };
        foreach (var row in document.RootElement.EnumerateArray())
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in row.EnumerateObject())
                if (!fields.Add(field.Name))
                    throw new InvalidDataException("Frankfurter quote contains duplicate fields.");
            if (row.GetProperty("base").GetString() != "EUR")
                throw new InvalidDataException("Frankfurter rates must be quoted against EUR.");
            var code = row.GetProperty("quote").GetString()
                ?? throw new InvalidDataException("Frankfurter quote has no currency code.");
            var dateText = row.GetProperty("date").GetString()
                ?? throw new InvalidDataException("Frankfurter quote has no reference date.");
            var date = ParseDate(dateText);
            var rate = row.GetProperty("rate").GetDouble();
            if (!rates.TryAdd(code, rate))
                throw new InvalidDataException("Frankfurter rates contain duplicate currency codes.");
            firstDate = firstDate is null || date < firstDate.Value ? date : firstDate;
            lastDate = lastDate is null || date > lastDate.Value ? date : lastDate;
        }
        if (firstDate is null || lastDate is null)
            throw new InvalidDataException("Frankfurter rates have no observation dates.");
        return CreateSnapshot(firstDate.Value, lastDate.Value, fetchedAt, rates, 1);
    }

    private static DateOnly ParseDate(string text)
    {
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
            date.Year < 1601)
            throw new InvalidDataException("Currency reference date must be YYYY-MM-DD.");
        return date;
    }

    private static CurrencyRatesSnapshot CreateSnapshot(DateOnly referenceDate, DateOnly referenceEndDate,
        DateTimeOffset fetchedAt, Dictionary<string, double> rates, int providerId)
    {
        fetchedAt = fetchedAt.ToUniversalTime();
        if (referenceDate.Year < 1601 || fetchedAt.Year < 1601 || referenceEndDate < referenceDate ||
            referenceEndDate > DateOnly.FromDateTime(fetchedAt.UtcDateTime))
            throw new InvalidDataException("Currency reference date cannot follow the UTC fetch date.");
        if (providerId is not (0 or 1) || rates.Count is < 2 or > 256 || !rates.TryGetValue("EUR", out var euro) || euro != 1)
            throw new InvalidDataException("Currency rates require EUR=1 and at least one quoted currency.");
        if (providerId == 0 && referenceEndDate != referenceDate)
            throw new InvalidDataException("ECB rates must have one common reference date.");
        foreach (var rate in rates)
            if (rate.Key.Length != 3 || rate.Key.Any(character => character is < 'A' or > 'Z') ||
                (providerId == 0 && !SupportedCodes.Contains(rate.Key)) ||
                !double.IsFinite(rate.Value) || rate.Value < 1e-12 || rate.Value > 1e12)
                throw new InvalidDataException("Currency rates contain an unsupported code or invalid numeric value.");
        var frozen = new ReadOnlyDictionary<string, double>(new Dictionary<string, double>(rates, StringComparer.Ordinal));
        return new CurrencyRatesSnapshot(referenceDate, fetchedAt, frozen) { ProviderId = providerId, ReferenceEndDate = referenceEndDate };
    }

    private async Task<CurrencyRatesSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await FetchTextAsync(FrankfurterSourceUrl, "application/json", cancellationToken).ConfigureAwait(false);
            return ParseFrankfurterJson(json, _timeProvider.GetUtcNow());
        }
        catch (Exception error) when (IsDataOrIoFailure(error) || error is HttpRequestException ||
            (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Preserve a broader valid cache instead of dropping currencies during a provider outage.
            if (_current?.ProviderId == 1) throw;
            var xml = await FetchTextAsync(SourceUrl, "application/xml", cancellationToken).ConfigureAwait(false);
            return ParseXml(xml, _timeProvider.GetUtcNow());
        }
    }

    private async Task<string> FetchTextAsync(string url, string accept, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd(accept);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes)
            throw new InvalidDataException("Currency response exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false);
        return StrictUtf8.GetString(bytes);
    }

    private async Task<CurrencyRatesSnapshot?> ReadCacheAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cachePath)) return null;
        await using var stream = new FileStream(_cachePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var bytes = await ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false);
        // Deserialize manually to reject duplicate rate codes instead of accepting last-value wins.
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
            if (!fields.Add(field.Name))
                throw new InvalidDataException("Currency cache contains duplicate fields.");
        var version = root.GetProperty("version").GetInt32();
        var providerId = version == 1 ? 0 : root.GetProperty("providerId").GetInt32();
        var provider = providerId == 1 ? FrankfurterProviderName : ProviderName;
        if (version is not (1 or 2) || providerId is not (0 or 1) || root.GetProperty("provider").GetString() != provider)
            throw new InvalidDataException("Currency cache has an unsupported schema or provider.");
        var dateText = root.GetProperty("referenceDate").GetString()
            ?? throw new InvalidDataException("Currency cache has no reference date.");
        var fetchedAt = root.GetProperty("fetchedAt").GetDateTimeOffset();
        if (fetchedAt.Offset != TimeSpan.Zero || fetchedAt > _timeProvider.GetUtcNow())
            throw new InvalidDataException("Currency cache has an invalid UTC fetch timestamp.");
        var rates = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var rate in root.GetProperty("rates").EnumerateObject())
            if (!rates.TryAdd(rate.Name, rate.Value.GetDouble()))
                throw new InvalidDataException("Currency cache contains duplicate codes.");
        var endDateText = version == 1 ? dateText : root.GetProperty("referenceEndDate").GetString()
            ?? throw new InvalidDataException("Currency cache has no reference end date.");
        var snapshot = CreateSnapshot(ParseDate(dateText), ParseDate(endDateText), fetchedAt, rates, providerId);
        _legacyCache = version == 1;
        return snapshot;
    }

    private async Task WriteCacheAsync(CurrencyRatesSnapshot snapshot, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_cachePath)
            ?? throw new IOException("Currency cache directory is unavailable.");
        Directory.CreateDirectory(directory);
        var data = new CachedRates(2, snapshot.ProviderName, snapshot.ProviderId, snapshot.ReferenceDateText,
            snapshot.ReferenceEndDateText, snapshot.FetchedAt,
            snapshot.Rates.ToDictionary(rate => rate.Key, rate => rate.Value, StringComparer.Ordinal));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, CacheJson);
        var temporaryPath = _cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _cachePath, true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > MaximumBytes)
                throw new InvalidDataException("Currency data exceeds the size limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static bool IsDataOrIoFailure(Exception error) => error is
        InvalidDataException or IOException or UnauthorizedAccessException or XmlException or JsonException or
        FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsClient) _client.Dispose();
        // The gate may still have an in-flight caller; its finally block must remain usable.
    }
}
