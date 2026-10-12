// Licensed under the MIT License.
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml;
using CalculatorApp.Desktop;

internal static class CurrencyTests
{
    // ECB 2026-10-09 observations, expressed per euro.
    private const string EcbXml = """
        <gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01" xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref">
          <Cube><Cube time="2026-10-09">
            <Cube currency="USD" rate="1.1206"/><Cube currency="BRL" rate="5.6086"/>
            <Cube currency="GBP" rate="0.84763"/><Cube currency="JPY" rate="177.34"/>
          </Cube></Cube>
        </gesmes:Envelope>
        """;

    // Frankfurter's 2026-10-11/12 bulk EUR observations. Each quote keeps its source date.
    private const string FrankfurterJson = """
        [{"date":"2026-10-12","base":"EUR","quote":"AED","rate":4.1198},
         {"date":"2026-10-11","base":"EUR","quote":"AFN","rate":72.756},
         {"date":"2026-10-11","base":"EUR","quote":"BRL","rate":5.6181},
         {"date":"2026-10-12","base":"EUR","quote":"JPY","rate":177.53},
         {"date":"2026-10-12","base":"EUR","quote":"USD","rate":1.1218},
         {"date":"2026-10-12","base":"EUR","quote":"VND","rate":29002}]
        """;
    private const string BrlQuote = """{"date":"2026-10-11","base":"EUR","quote":"BRL","rate":5.6181}""";
    private static readonly DateTimeOffset FetchTime = new(2026, 10, 12, 3, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync()
    {
        CheckParsers();
        await CheckPrimaryCacheAndFailureAsync();
        await CheckFallbackAndUpgradeAsync();
        await CheckMalformedCacheAsync();
        await CheckCacheWriteFailureAsync();
        await CheckCancellationAndDisposalAsync();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject(Action parse, string message)
    {
        try { parse(); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or JsonException or XmlException or
            FormatException or InvalidOperationException or KeyNotFoundException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void CheckParsers()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var ecb = CurrencyRates.ParseXml(EcbXml, FetchTime);
            Check(ecb.ProviderId == 0 && ecb.ProviderName == "European Central Bank" && ecb.SourceUrl == CurrencyRates.SourceUrl,
                "ECB parser lost source identity");
            Check(ecb.ReferenceDateText == "2026-10-09" && ecb.ReferenceEndDateText == "2026-10-09" &&
                ecb.FetchedUtc == "2026-10-12T03:00:00Z", "ECB observation and UTC fetch dates changed");
            Check(ecb.Rates.Count == 5 && ecb.Rates["EUR"] == 1 && ecb.Rates["USD"] == 1.1206 &&
                ecb.ToRateLines().Contains("USD=1.1206", StringComparison.Ordinal), "Rates must parse and serialize invariant decimals");
            Check(CurrencyRates.ParseXml(EcbXml, FetchTime.ToOffset(TimeSpan.FromHours(-3))).FetchedUtc == ecb.FetchedUtc,
                "Fetch timestamps must normalize to UTC independently of local offset");
            var broad = CurrencyRates.ParseFrankfurterJson(FrankfurterJson, FetchTime);
            Check(broad.ProviderId == 1 && broad.ProviderName == "Frankfurter" && broad.SourceUrl == CurrencyRates.FrankfurterSourceUrl,
                "Frankfurter parser lost source identity");
            Check(broad.ReferenceDateText == "2026-10-11" && broad.ReferenceEndDateText == "2026-10-12" &&
                broad.Rates.Count == 7 && broad.Rates["AED"] == 4.1198 && broad.Rates["VND"] == 29002,
                "Broad currency coverage and mixed observation dates must remain explicit");
            bool immutable = false;
            try { ((IDictionary<string, double>)broad.Rates).Add("USD", broad.Rates["USD"]); }
            catch (NotSupportedException) { immutable = true; }
            Check(immutable, "Published rate dictionaries must remain immutable");
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }

        foreach (var xml in new[]
        {
            EcbXml.Replace("1.1206", "0"), EcbXml.Replace("1.1206", "NaN"), EcbXml.Replace("1.1206", "Infinity"),
            EcbXml.Replace("1.1206", "1e309"), EcbXml.Replace("1.1206", "1,1206"), EcbXml.Replace("USD", "usd"),
            EcbXml.Replace("GBP", "USD"), EcbXml.Replace("2026-10-09", "2026-02-30"),
            EcbXml.Replace("2026-10-09", "2026-10-13"), EcbXml.Replace("time=", "date="),
            EcbXml.Replace("<gesmes:Envelope", "<!DOCTYPE Envelope [<!ENTITY external SYSTEM 'file:///currency-test-must-not-read'>]><gesmes:Envelope"),
            EcbXml + new string(' ', 65537)
        })
            Reject(() => CurrencyRates.ParseXml(xml, FetchTime), "Malformed/unsafe ECB XML was accepted");

        foreach (var json in new[]
        {
            "[]", "{}", "[" + BrlQuote + "," + BrlQuote + "]", FrankfurterJson.Replace("\"EUR\"", "\"USD\""),
            FrankfurterJson.Replace("\"AED\"", "\"aed\""), FrankfurterJson.Replace("\"AED\"", "\"AE1\""),
            FrankfurterJson.Replace("4.1198", "0"), FrankfurterJson.Replace("4.1198", "-4.1198"),
            FrankfurterJson.Replace("4.1198", "1e309"), FrankfurterJson.Replace("4.1198", "\"NaN\""),
            FrankfurterJson.Replace("4.1198", "1e-999"), FrankfurterJson.Replace("2026-10-12", "2026-10-13"),
            FrankfurterJson.Replace("2026-10-11", "2026-02-30"),
            FrankfurterJson.Replace("\"rate\":4.1198", "\"rate\":4.1198,\"rate\":4.1198"),
            "[" + string.Join(',', Enumerable.Repeat(BrlQuote, 256)) + "]", FrankfurterJson + new string(' ', 65537)
        })
            Reject(() => CurrencyRates.ParseFrankfurterJson(json, FetchTime), "Malformed/oversized Frankfurter JSON was accepted");
    }

    private static async Task CheckPrimaryCacheAndFailureAsync()
    {
        var directory = CreateDirectory();
        var path = Path.Combine(directory, "rates.json");
        try
        {
            var clock = new FrozenClock(FetchTime);
            string response = FrankfurterJson;
            bool offline = false;
            using var handler = new ResponseHandler((request, _) =>
            {
                Check(request.RequestUri?.AbsoluteUri == CurrencyRates.FrankfurterSourceUrl, "Primary refresh fetched an unexpected source");
                if (offline) throw new HttpRequestException("Offline fixture");
                return Task.FromResult(Response(response, "application/json"));
            });
            using var client = new HttpClient(handler);
            using var provider = new CurrencyRates(path, client, clock);
            var initial = await provider.LoadAsync();
            Check(initial.Snapshot is { ProviderId: 1 } && !initial.IsFromCache && initial.Error.Length == 0 && handler.Count == 1,
                "Primary request must publish Frankfurter data");
            Check(File.Exists(path) && !Directory.EnumerateFiles(directory, "*.tmp").Any(), "Atomic cache write left incomplete files");
            using (var cache = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
                Check(cache.RootElement.GetProperty("version").GetInt32() == 2 &&
                    cache.RootElement.GetProperty("providerId").GetInt32() == 1 &&
                    cache.RootElement.GetProperty("referenceEndDate").GetString() == "2026-10-12",
                    "Cache must persist provider and mixed-date metadata");
            var original = await File.ReadAllTextAsync(path);
            var cached = await provider.LoadAsync();
            Check(cached.IsFromCache && ReferenceEquals(initial.Snapshot, cached.Snapshot) && handler.Count == 1,
                "Fresh in-memory rates must reuse the cache without HTTP");
            using (var restarted = new CurrencyRates(path, client, clock))
            {
                var diskCached = await restarted.LoadAsync();
                Check(diskCached.IsFromCache && diskCached.Snapshot?.Rates.Count == 7 && handler.Count == 1,
                    "Fresh persistent rates must retain broad coverage without HTTP");
            }
            response = "[" + BrlQuote + "]";
            var older = await provider.LoadAsync(true);
            Check(older.IsFromCache && older.Error.Length > 0 && ReferenceEquals(initial.Snapshot, older.Snapshot) &&
                await File.ReadAllTextAsync(path) == original, "A regressed date must preserve valid rates and the cache file");
            response = FrankfurterJson + new string(' ', 65537);
            var oversized = await provider.LoadAsync(true);
            Check(oversized.IsFromCache && ReferenceEquals(initial.Snapshot, oversized.Snapshot) &&
                await File.ReadAllTextAsync(path) == original, "Oversized HTTP content must preserve valid rates");
            offline = true;
            clock.Now = FetchTime.AddDays(2);
            var beforeFailure = handler.Count;
            var failed = await provider.LoadAsync();
            Check(failed.IsFromCache && failed.Error.Length > 0 && ReferenceEquals(initial.Snapshot, failed.Snapshot) &&
                handler.Count == beforeFailure + 1 && await File.ReadAllTextAsync(path) == original &&
                !Directory.EnumerateFiles(directory, "*.tmp").Any(),
                "A broad stale cache must survive refresh failure without shrinking to ECB coverage");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task CheckFallbackAndUpgradeAsync()
    {
        var directory = CreateDirectory();
        var path = Path.Combine(directory, "rates.json");
        try
        {
            var clock = new FrozenClock(FetchTime);
            bool primaryAvailable = false;
            using var handler = new ResponseHandler((request, _) => Task.FromResult(
                request.RequestUri?.AbsoluteUri == CurrencyRates.FrankfurterSourceUrl
                    ? primaryAvailable ? Response(FrankfurterJson, "application/json") : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : Response(EcbXml, "application/xml")));
            using var client = new HttpClient(handler);
            using var provider = new CurrencyRates(path, client, clock);
            var fallback = await provider.LoadAsync();
            Check(fallback.Snapshot is { ProviderId: 0 } ecb && !fallback.IsFromCache && ecb.Rates.Count == 5 && handler.Count == 2,
                "Unavailable primary service must fall back to genuine ECB rates");
            clock.Now = FetchTime.AddHours(1);
            var refreshed = await provider.LoadAsync(true);
            Check(refreshed.Snapshot is { ProviderId: 0 } updated && updated.FetchedAt == clock.Now && handler.Count == 4,
                "Forced refresh must update ECB fallback data and UTC fetch time");
            primaryAvailable = true;
            var upgraded = await provider.LoadAsync(true);
            Check(upgraded.Snapshot is { ProviderId: 1 } wide && wide.Rates.Count == 7 && handler.Count == 5,
                "Recovered primary service must replace ECB fallback data with broader coverage");
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Successful cache replacement left temporary files");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task CheckMalformedCacheAsync()
    {
        var directory = CreateDirectory();
        var path = Path.Combine(directory, "rates.json");
        const string legacy = """
            {"version":1,"provider":"European Central Bank","referenceDate":"2026-10-09",
             "fetchedAt":"2026-10-12T03:00:00Z","rates":{"EUR":1,"USD":1.1206}}
            """;
        try
        {
            using var handler = new ResponseHandler((_, _) => throw new HttpRequestException("Offline fixture"));
            using var client = new HttpClient(handler);
            foreach (var text in new[]
            {
                "{", legacy.Replace("1.1206", "0"), legacy.Replace("1.1206", "\"NaN\""),
                legacy.Replace("\"USD\":1.1206", "\"USD\":1.1206,\"USD\":1.1206"),
                legacy.Replace("\"version\":1", "\"version\":1,\"version\":1"),
                legacy.Replace("2026-10-12T03:00:00Z", "2026-10-13T03:00:00Z"),
                legacy.Replace("2026-10-12T03:00:00Z", "2026-10-12T03:00:00+03:00"),
                legacy.Replace("2026-10-09", "2026-10-13"), legacy + new string(' ', 65537)
            })
            {
                await File.WriteAllTextAsync(path, text);
                using var provider = new CurrencyRates(path, client, new FrozenClock(FetchTime));
                var result = await provider.LoadAsync();
                Check(result.Snapshot is null && !result.IsFromCache && result.Error.Contains("Cached currency rates", StringComparison.Ordinal) &&
                    await File.ReadAllTextAsync(path) == text, "Malformed cache must not fabricate data or replace its file on failed refresh");
            }
            await File.WriteAllTextAsync(path, legacy);
            using (var provider = new CurrencyRates(path, client, new FrozenClock(FetchTime)))
            {
                var result = await provider.LoadAsync();
                Check(result.IsFromCache && result.Snapshot is { ProviderId: 0 } retained && retained.ReferenceEndDateText == "2026-10-09",
                    "Valid legacy ECB cache must remain available when both providers fail");
            }
            using var upgradedHandler = new ResponseHandler((_, _) => Task.FromResult(Response(FrankfurterJson, "application/json")));
            using var upgradedClient = new HttpClient(upgradedHandler);
            using (var provider = new CurrencyRates(path, upgradedClient, new FrozenClock(FetchTime)))
            {
                var result = await provider.LoadAsync();
                Check(!result.IsFromCache && result.Snapshot?.ProviderId == 1 && upgradedHandler.Count == 1,
                    "Legacy ECB cache must attempt a primary-provider upgrade even within one day");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task CheckCacheWriteFailureAsync()
    {
        var directory = CreateDirectory();
        try
        {
            var blockedDirectory = Path.Combine(directory, "file-blocking-cache-directory");
            await File.WriteAllTextAsync(blockedDirectory, "Keep this file");
            bool offline = false;
            using var handler = new ResponseHandler((_, _) => offline
                ? throw new HttpRequestException("Offline fixture")
                : Task.FromResult(Response(FrankfurterJson, "application/json")));
            using var client = new HttpClient(handler);
            using var provider = new CurrencyRates(Path.Combine(blockedDirectory, "rates.json"), client, new FrozenClock(FetchTime));
            var loaded = await provider.LoadAsync();
            Check(loaded.Snapshot is { ProviderId: 1 } && !loaded.IsFromCache &&
                loaded.Error.Contains("cache could not be saved", StringComparison.Ordinal),
                "Cache write failure must keep valid downloaded rates usable");
            offline = true;
            var failed = await provider.LoadAsync(true);
            Check(failed.IsFromCache && ReferenceEquals(loaded.Snapshot, failed.Snapshot) &&
                await File.ReadAllTextAsync(blockedDirectory) == "Keep this file" &&
                !Directory.EnumerateFiles(directory, "*.tmp").Any(),
                "Cache write failure must preserve existing files and valid in-memory rates");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task CheckCancellationAndDisposalAsync()
    {
        var directory = CreateDirectory();
        try
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            using var handler = new ResponseHandler(async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Canceled request unexpectedly resumed");
            });
            using var client = new HttpClient(handler);
            using var provider = new CurrencyRates(Path.Combine(directory, "rates.json"), client, new FrozenClock(FetchTime));
            var pending = provider.LoadAsync(true, cancellation.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            provider.Dispose();
            Check(!handler.Disposed, "Provider must not dispose a caller-owned HTTP client");
            client.Dispose();
            bool canceled = false;
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && handler.Count == 1 && !Directory.EnumerateFiles(directory).Any(),
                "Closing during refresh must cancel without fallback, partial cache writes, or disposal races");
            bool disposed = false;
            try { await provider.LoadAsync(); }
            catch (ObjectDisposedException) { disposed = true; }
            Check(disposed, "A disposed provider must reject subsequent calls");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "calculator-currency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static HttpResponseMessage Response(string text, string mediaType) => new(HttpStatusCode.OK)
    { Content = new StringContent(text, Encoding.UTF8, mediaType) };

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return respond(request, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
