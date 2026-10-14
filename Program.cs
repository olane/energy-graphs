using System.Drawing;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Diagnostics;
using ImpSoft.OctopusEnergy.Api;
using Microsoft.Extensions.Configuration;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = "output"
});
builder.Configuration.AddUserSecrets<Program>();

var config = builder.Configuration;

var apiKey = config["octopus_api_key"];
var octopusAccount = config["octopus_account"];
var electricityMPAN = config["electricity_mpan"];
var electricitySerial = config["electricity_serial"];
var gasMPRN = config["gas_mprn"];
var gasSerial = config["gas_serial"];

var visualCrossingKey = config["visualcrossing_key"];

var weatherLoc = "Cambridge%20UK";

var cacheDir = config["cache_dir"] ?? "cache";
var refreshHours = int.TryParse(config["refresh_hours"], out var parsedRefreshHours) ? parsedRefreshHours : 24;

// Bump when the cached WeatherDay schema changes so stale entries are refetched.
const string weatherCacheVersion = "v2";

using var httpClient = new HttpClient();

httpClient.SetAuthenticationHeaderFromApiKey(apiKey);

// Create the api wrapper
var octopusClient = new OctopusEnergyClient(httpClient);

// Separate client so the Octopus auth header is never sent to the weather API.
using var weatherHttpClient = new HttpClient();

async Task GenerateAsync()
{
    var electricityMeters = new List<(string Mpan, string Serial)>();
    var gasMeters = new List<(string Mprn, string Serial)>();
    DateTime? propertyMovedIn = null;

    if (!string.IsNullOrWhiteSpace(octopusAccount))
    {
        (electricityMeters, gasMeters, propertyMovedIn) = await DiscoverMeters(octopusAccount, httpClient);
    }
    else
    {
        electricityMeters.Add((electricityMPAN!, electricitySerial!));
        gasMeters.Add((gasMPRN!, gasSerial!));
    }

    var toDate = ParseDate(config["to_date"], DateTime.UtcNow.Date);
    // Default to all time: from the property's move-in date when known, otherwise open-ended.
    var fromDate = TryParseDate(config["from_date"]) ?? propertyMovedIn ?? DateTime.UnixEpoch;
    var from = new DateTimeOffset(fromDate, TimeSpan.Zero);
    var to = new DateTimeOffset(toDate.AddDays(1), TimeSpan.Zero).AddTicks(-1);

    var splitDates = new[] { config["split_dates"], config["split_date"] }
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .SelectMany(value => value!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Select(TryParseDate)
        .Where(date => date.HasValue)
        .Select(date => date!.Value)
        .Distinct()
        .OrderBy(date => date)
        .ToList();

    var electricConsumption = await FetchConsumption(electricityMeters, (mpan, serial) => octopusClient.GetElectricityConsumptionAsync(mpan, serial, from, to, Interval.Day));

    // For SMETS1 gas is kwh equivalent, for SMETS2 it is in m^3
    var gasConsumption = await FetchConsumption(gasMeters, (mprn, serial) => octopusClient.GetGasConsumptionAsync(mprn, serial, from, to, Interval.Day));

    Directory.CreateDirectory("output");
    DrawGasUsage(gasConsumption);
    DrawElectricityUsage(electricConsumption);

    // Only fetch weather for days that actually have consumption data.
    var consumptionStarts = electricConsumption.Select(x => x.Start).Concat(gasConsumption.Select(x => x.Start)).ToList();

    // With no configured splits, colour by calendar year.
    if (splitDates.Count == 0 && consumptionStarts.Count > 0)
    {
        var first = consumptionStarts.Min().Date;
        var last = consumptionStarts.Max().Date;
        splitDates = Enumerable.Range(first.Year + 1, Math.Max(0, last.Year - first.Year))
            .Select(year => new DateTime(year, 1, 1))
            .ToList();
    }

    var weather = consumptionStarts.Count > 0
        ? await GetWeather(visualCrossingKey, weatherHttpClient, weatherLoc, consumptionStarts.Min().Date, consumptionStarts.Max().Date)
        : new List<WeatherDay>();

    var tempDict = weather.ToDictionary(x => x.DateTime, x => x.Temp);
    var daylightDict = weather
        .Where(x => x.SunriseEpoch > 0 && x.SunsetEpoch > 0)
        .ToDictionary(x => x.DateTime, x => (decimal)(x.SunsetEpoch - x.SunriseEpoch) / 3600m);
    DrawGasTempScatterChart(gasConsumption, tempDict);
    DrawElectricityDaylightScatterChart(electricConsumption, daylightDict);

    var electricLookup = electricConsumption.ToDictionary(x => x.Start.ToString("yyyy-MM-dd"), x => x.Quantity);
    if (consumptionStarts.Count > 0)
    {
        DrawTempScatterWithPeriods(gasConsumption, tempDict, splitDates, day => (double)day.Quantity, "Consumption (m^3)", "Gas usage vs temperature", "output/gas-temp-scatter-split.png");
        DrawTempScatterWithPeriods(gasConsumption, tempDict, splitDates, day => TotalKwh(day, electricLookup), "Consumption (kWh)", "Total energy usage vs temperature", "output/total-energy-temp-scatter-split.png");
    }

    DrawGasPlusElectricityTempScatterChart(gasConsumption, electricConsumption, tempDict);
    WriteIndexHtml(consumptionStarts.Count > 0);
}

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Graphs are regenerated in place under stable filenames, so tell browsers to
    // revalidate (ETag/Last-Modified) rather than serving a stale cached copy.
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});

await GenerateAsync();

if (refreshHours > 0)
{
    _ = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(refreshHours));
        try
        {
            while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
            {
                try
                {
                    await GenerateAsync();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Graph regeneration failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    });
}

app.Run();

void WriteIndexHtml(bool includeSplit)
{
    var splitFigures = includeSplit
        ? """
        <figure><img src="gas-temp-scatter-split.png" alt="Gas usage vs temperature (split)"></figure>
        <figure><img src="total-energy-temp-scatter-split.png" alt="Total energy usage vs temperature (split)"></figure>
        """
        : "";

    var html = $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Energy Graphs</title>
        <style>
        body { font-family: system-ui, sans-serif; margin: 0 auto; max-width: 1200px; padding: 1rem; }
        img { max-width: 100%; height: auto; }
        figure { margin: 0 0 2rem; }
        </style>
        </head>
        <body>
        <h1>Energy usage</h1>
        <figure><img src="gas-usage.png" alt="Gas usage"></figure>
        <figure><img src="electric-usage.png" alt="Electricity usage"></figure>
        <figure><img src="gas-temp-scatter.png" alt="Gas usage vs temperature"></figure>
        {{splitFigures}}
        <figure><img src="total-energy-temp-scatter.png" alt="Total energy usage vs temperature"></figure>
        <figure><img src="electric-daylight-scatter.png" alt="Electricity usage vs daylight hours"></figure>
        </body>
        </html>
        """;

    File.WriteAllText("output/index.html.tmp", html);
    File.Move("output/index.html.tmp", "output/index.html", overwrite: true);
}

DateTime? TryParseDate(string? value)
{
    if (value is not null && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
    {
        return parsed.Date;
    }

    return null;
}

DateTime ParseDate(string? value, DateTime fallback) => TryParseDate(value) ?? fallback;

DateTimeOffset ParseInstant(string? value) =>
    DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
        ? parsed
        : DateTimeOffset.MinValue;

async Task<(List<(string Mpan, string Serial)> Electricity, List<(string Mprn, string Serial)> Gas, DateTime? MovedIn)> DiscoverMeters(string account, HttpClient client)
{
    var response = await client.GetAsync($"https://api.octopus.energy/v1/accounts/{account}/");
    response.EnsureSuccessStatusCode();

    var json = await response.Content.ReadAsStringAsync();
    var accountResponse = JsonSerializer.Deserialize<AccountResponse>(json, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    });

    var properties = accountResponse?.Properties ?? new List<AccountProperty>();

    // Prefer the active property (no moved_out_at), most recently moved into.
    var candidates = properties
        .Where(p => p.MovedOutAt is null)
        .OrderByDescending(p => ParseInstant(p.MovedInAt))
        .ToList();

    if (candidates.Count == 0)
    {
        candidates = properties.OrderByDescending(p => ParseInstant(p.MovedInAt)).ToList();
    }

    if (candidates.Count == 0)
    {
        throw new InvalidOperationException($"No meter points found on account {account}");
    }

    var property = candidates[0];

    var electricity = property.ElectricityMeterPoints
        .Where(mp => !mp.IsExport)
        .SelectMany(mp => mp.Meters.Select(m => (Mpan: mp.Mpan, Serial: m.SerialNumber)))
        .ToList();

    var gas = property.GasMeterPoints
        .SelectMany(mp => mp.Meters.Select(m => (Mprn: mp.Mprn, Serial: m.SerialNumber)))
        .ToList();

    var movedIn = ParseInstant(property.MovedInAt);

    return (electricity, gas, movedIn == DateTimeOffset.MinValue ? null : movedIn.UtcDateTime.Date);
}

async Task<List<Consumption>> FetchConsumption(List<(string Id, string Serial)> meters, Func<string, string, Task<IEnumerable<Consumption>>> fetch)
{
    var all = new List<Consumption>();
    foreach (var (id, serial) in meters)
    {
        all.AddRange(await fetch(id, serial));
    }

    // Meter exchanges leave data split across serials; stitch them together.
    return all
        .GroupBy(x => x.Start)
        .Select(g => g.First())
        .OrderBy(x => x.Start)
        .ToList();
}

void DrawGasTempScatterChart(List<Consumption> gasConsumption, Dictionary<string, decimal> tempDict)
{
    var xs = new List<decimal>();
    var ys = new List<decimal>();

    foreach (var gasDay in gasConsumption) {
        if (!tempDict.TryGetValue(gasDay.Start.ToString("yyyy-MM-dd"), out var temp)) {
            continue;
        }

        ys.Add(gasDay.Quantity);
        xs.Add(temp);
    }

    ScottPlot.Plot gasPlot = new();
    gasPlot.Add.ScatterPoints(xs, ys);

    gasPlot.XLabel("Daily temperature average (deg C)");
    gasPlot.YLabel("Consumption (m^3)");
    gasPlot.Title("Gas usage vs temperature");
    SavePlotPng(gasPlot, "output/gas-temp-scatter.png");
}

void DrawTempScatterWithPeriods(List<Consumption> gasConsumption, Dictionary<string, decimal> tempDict, List<DateTime> splitDates, Func<Consumption, double?> value, string yLabel, string title, string outputPath)
{
    var periodColors = new[] { Color.Blue, Color.Red, Color.Green, Color.Orange, Color.Purple, Color.Brown };

    var periodCount = splitDates.Count + 1;
    var xs = new List<decimal>[periodCount];
    var ys = new List<double>[periodCount];
    for (var i = 0; i < periodCount; i++) {
        xs[i] = new List<decimal>();
        ys[i] = new List<double>();
    }

    DateTime? firstData = null;
    foreach (var gasDay in gasConsumption) {
        if (!tempDict.TryGetValue(gasDay.Start.ToString("yyyy-MM-dd"), out var temp)) {
            continue;
        }

        var y = value(gasDay);
        if (y is null) {
            continue;
        }

        var period = splitDates.Count(split => gasDay.Start.DateTime >= split);
        ys[period].Add(y.Value);
        xs[period].Add(temp);
        firstData ??= gasDay.Start.DateTime;
    }

    ScottPlot.Plot gasPlot = new();
    var firstYear = firstData?.Year ?? DateTime.UtcNow.Year;
    for (var i = 0; i < periodCount; i++) {
        if (xs[i].Count == 0) {
            continue;
        }

        var series = gasPlot.Add.ScatterPoints(xs[i], ys[i], ScottPlot.Color.FromColor(periodColors[i % periodColors.Length]));
        series.LegendText = PeriodLabel(splitDates, i, firstYear);
    }

    gasPlot.ShowLegend();

    gasPlot.XLabel("Daily temperature average (deg C)");
    gasPlot.YLabel(yLabel);
    gasPlot.Title(title);
    SavePlotPng(gasPlot, outputPath);
}

string PeriodLabel(List<DateTime> splitDates, int period, int firstYear)
{
    var yearly = splitDates.Count == 0 || splitDates.All(split => split.Month == 1 && split.Day == 1);

    if (yearly) {
        return (period == 0 ? firstYear : splitDates[period - 1].Year).ToString();
    }

    if (period == 0) {
        return $"before {splitDates[0]:yyyy-MM-dd}";
    }

    if (period == splitDates.Count) {
        return $"from {splitDates[^1]:yyyy-MM-dd}";
    }

    return $"{splitDates[period - 1]:yyyy-MM-dd} to {splitDates[period]:yyyy-MM-dd}";
}

double? TotalKwh(Consumption gasDay, Dictionary<string, decimal> electricLookup)
{
    if (!electricLookup.TryGetValue(gasDay.Start.ToString("yyyy-MM-dd"), out var electric)) {
        return null;
    }

    // add all the usages together and convert gas m^3 to rough kwh (for exact, need to switch the 38 for our specific caloric value)
    return (double)electric + ((double)gasDay.Quantity * 38 * 1.02264 / 3.6);
}

void DrawGasPlusElectricityTempScatterChart(List<Consumption> gasConsumption, List<Consumption> electricConsumption, Dictionary<string, decimal> tempDict)
{
    var electricLookup = electricConsumption.ToDictionary(x => x.Start.ToString("yyyy-MM-dd"), x => x.Quantity);

    var xs = new List<decimal>();
    var ys = new List<double>();

    foreach (var gasDay in gasConsumption) {
        if (!tempDict.TryGetValue(gasDay.Start.ToString("yyyy-MM-dd"), out var temp)) {
            continue;
        }

        var totalKwh = TotalKwh(gasDay, electricLookup);
        if (totalKwh is null) {
            continue;
        }

        ys.Add(totalKwh.Value);
        xs.Add(temp);
    }

    ScottPlot.Plot gasPlot = new();
    gasPlot.Add.ScatterPoints(xs, ys);

    gasPlot.XLabel("Daily temperature average (deg C)");
    gasPlot.YLabel("Consumption (kWh)");
    gasPlot.Title("Total energy usage vs temperature");
    SavePlotPng(gasPlot, "output/total-energy-temp-scatter.png");
}

void DrawElectricityDaylightScatterChart(List<Consumption> electricConsumption, Dictionary<string, decimal> daylightDict)
{
    var xs = new List<decimal>();
    var ys = new List<decimal>();

    foreach (var electricDay in electricConsumption) {
        if (!daylightDict.TryGetValue(electricDay.Start.ToString("yyyy-MM-dd"), out var daylightHours)) {
            continue;
        }

        xs.Add(daylightHours);
        ys.Add(electricDay.Quantity);
    }

    ScottPlot.Plot leccyPlot = new();
    leccyPlot.Add.ScatterPoints(xs, ys);

    leccyPlot.XLabel("Daylight hours");
    leccyPlot.YLabel("Consumption (kWh)");
    leccyPlot.Title("Electricity usage vs daylight hours");
    SavePlotPng(leccyPlot, "output/electric-daylight-scatter.png");
}

void DrawGasUsage(List<Consumption> gasConsumption)
{
    ScottPlot.Plot gasPlot = new();
    ConfigureDateAxis(gasPlot);
    gasPlot.Add.Scatter(gasConsumption.Select(x => x.Start.ToLocalTime().DateTime).ToArray(), gasConsumption.Select(x => x.Quantity).ToArray());

    gasPlot.XLabel("Date");
    gasPlot.YLabel("Consumption (m^3)");
    gasPlot.Title("Gas usage");
    SavePlotPng(gasPlot, "output/gas-usage.png");
}

void DrawElectricityUsage(List<Consumption> electricConsumption)
{
    ScottPlot.Plot leccyPlot = new();
    ConfigureDateAxis(leccyPlot);
    leccyPlot.Add.Scatter(electricConsumption.Select(x => x.Start.ToLocalTime().DateTime).ToArray(), electricConsumption.Select(x => x.Quantity).ToArray());

    leccyPlot.XLabel("Date");
    leccyPlot.YLabel("Consumption (kWh)");
    leccyPlot.Title("Electricity usage");
    SavePlotPng(leccyPlot, "output/electric-usage.png");
}

void ConfigureDateAxis(ScottPlot.Plot plot)
{
    plot.Axes.DateTimeTicksBottom();
    plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.DateTimeAutomatic
    {
        LabelFormatter = date => date.Month == 1 && date.Day == 1
            ? date.ToString("yyyy", CultureInfo.InvariantCulture)
            : date.ToString("MMM d", CultureInfo.InvariantCulture)
    };
}

void SavePlotPng(ScottPlot.Plot plot, string path)
{
    var tempPath = path + ".tmp";
    plot.SavePng(tempPath, 1000, 800);
    File.Move(tempPath, path, overwrite: true);
}


async Task<List<WeatherDay>> GetWeather(string vcApiKey, HttpClient client, string weatherLocation, DateTime from, DateTime to) {
    var days = new List<WeatherDay>();
    var missingRuns = new List<(DateTime Start, DateTime End)>();

    DateTime? runStart = null;
    DateTime? runEnd = null;

    // Cache is per day, so a new day only ever fetches that day.
    for (var date = from.Date; date <= to.Date; date = date.AddDays(1)) {
        if (TryReadCachedDay(weatherLocation, date, out var cached)) {
            days.Add(cached!);
            if (runStart is not null) {
                missingRuns.Add((runStart.Value, runEnd!.Value));
                runStart = runEnd = null;
            }
        }
        else {
            runStart ??= date;
            runEnd = date;
        }
    }

    if (runStart is not null) {
        missingRuns.Add((runStart.Value, runEnd!.Value));
    }

    if (missingRuns.Count > 0) {
        Directory.CreateDirectory(cacheDir);
    }

    foreach (var (start, end) in missingRuns) {
        // Keep each request bounded; the per-day cache means each day is billed once.
        for (var chunkStart = start; chunkStart <= end; chunkStart = chunkStart.AddDays(366)) {
            var chunkEnd = chunkStart.AddDays(365);
            if (chunkEnd > end) {
                chunkEnd = end;
            }

            var fetched = await FetchWeatherRange(vcApiKey, client, weatherLocation, chunkStart, chunkEnd);
            foreach (var day in fetched) {
                WriteCachedDay(weatherLocation, day);
                days.Add(day);
            }
        }
    }

    return days
        .GroupBy(x => x.DateTime)
        .Select(g => g.First())
        .OrderBy(x => x.DateTime)
        .ToList();
}

async Task<List<WeatherDay>> FetchWeatherRange(string vcApiKey, HttpClient client, string weatherLocation, DateTime from, DateTime to) {
    var fromString = from.ToString("yyyy-MM-dd");
    var toString = to.ToString("yyyy-MM-dd");

    var uri = $"https://weather.visualcrossing.com/VisualCrossingWebServices/rest/services/timeline/{weatherLocation}/{fromString}/{toString}?unitGroup=metric&key={vcApiKey}&contentType=json";

    var response = await client.GetAsync(uri);
    response.EnsureSuccessStatusCode();

    var body = await response.Content.ReadAsStringAsync();
    var weatherResponse = JsonSerializer.Deserialize<WeatherResponse>(body, new JsonSerializerOptions{
        PropertyNameCaseInsensitive = true
    });

    return weatherResponse?.Days ?? new List<WeatherDay>();
}

string WeatherCacheFile(string weatherLocation, DateTime date) =>
    Path.Combine(cacheDir, CreateMD5($"{weatherLocation}|{date:yyyy-MM-dd}|{weatherCacheVersion}"));

bool TryReadCachedDay(string weatherLocation, DateTime date, out WeatherDay? day) {
    var cacheFileName = WeatherCacheFile(weatherLocation, date);
    if (File.Exists(cacheFileName)) {
        day = JsonSerializer.Deserialize<WeatherDay>(File.ReadAllText(cacheFileName), new JsonSerializerOptions{
            PropertyNameCaseInsensitive = true
        });
        return day is not null;
    }

    day = null;
    return false;
}

void WriteCachedDay(string weatherLocation, WeatherDay day) {
    if (DateTime.TryParse(day.DateTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) {
        File.WriteAllText(WeatherCacheFile(weatherLocation, date), JsonSerializer.Serialize(day));
    }
}

string CreateMD5(string input)
{
    using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
    {
        byte[] inputBytes = System.Text.Encoding.ASCII.GetBytes(input);
        byte[] hashBytes = md5.ComputeHash(inputBytes);

        return Convert.ToHexString(hashBytes);
    }
}

public class WeatherResponse {
    public List<WeatherDay> Days {get; set;}
}

public class WeatherDay {
    public string DateTime {get; set;} // format yyyy-MM-dd
    public int DatetimeEpoch {get; set;}
    public decimal Temp {get; set;}
    public long SunriseEpoch {get; set;}
    public long SunsetEpoch {get; set;}
}

public class AccountResponse {
    public List<AccountProperty> Properties { get; set; } = new();
}

public class AccountProperty {
    public string? MovedInAt { get; set; }
    public string? MovedOutAt { get; set; }
    public List<ElectricityMeterPoint> ElectricityMeterPoints { get; set; } = new();
    public List<GasMeterPoint> GasMeterPoints { get; set; } = new();
}

public class ElectricityMeterPoint {
    public string Mpan { get; set; } = string.Empty;
    public bool IsExport { get; set; }
    public List<Meter> Meters { get; set; } = new();
}

public class GasMeterPoint {
    public string Mprn { get; set; } = string.Empty;
    public List<Meter> Meters { get; set; } = new();
}

public class Meter {
    public string SerialNumber { get; set; } = string.Empty;
}
