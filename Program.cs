using System.Drawing;
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
var electricityMPAN = config["electricity_mpan"];
var electricitySerial = config["electricity_serial"];
var gasMPRN = config["gas_mprn"];
var gasSerial = config["gas_serial"];

var visualCrossingKey = config["visualcrossing_key"];

var weatherLoc = "Cambridge%20UK";

var cacheDir = config["cache_dir"] ?? "cache";

using var httpClient = new HttpClient();

httpClient.SetAuthenticationHeaderFromApiKey(apiKey);

// Create the api wrapper
var octopusClient = new OctopusEnergyClient(httpClient);

var from = new DateTimeOffset(2023, 09, 01, 00, 00, 00, TimeSpan.FromHours(0));
var to = new DateTimeOffset(2024, 12, 13, 23, 59, 00, TimeSpan.FromHours(0));

var electricConsumption = (await octopusClient.GetElectricityConsumptionAsync(electricityMPAN, electricitySerial, from, to, Interval.Day)).ToList();

// For SMETS1 this is kwh equivalent, for SMETS2 it is in m^3
var gasConsumption = (await octopusClient.GetGasConsumptionAsync(gasMPRN, gasSerial, from, to, Interval.Day)).ToList();

Directory.CreateDirectory("output");
DrawGasUsage(gasConsumption);
DrawElectricityUsage(electricConsumption);

var weather = await GetWeather(visualCrossingKey, new HttpClient(), weatherLoc, from.Date, to.Date);

var tempDict = weather.ToDictionary(x => x.DateTime, x => x.Temp);
DrawGasTempScatterChart(gasConsumption, tempDict);
DrawGasTempScatterChartWithSplit(gasConsumption, tempDict, new DateTime(2024, 12, 01));
DrawGasPlusElectricityTempScatterChart(gasConsumption, electricConsumption, tempDict);
WriteIndexHtml();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.Run();

void WriteIndexHtml()
{
    File.WriteAllText("output/index.html", """
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
        <figure><img src="gas-temp-scatter-split.png" alt="Gas usage vs temperature (split)"></figure>
        <figure><img src="total-energy-temp-scatter.png" alt="Total energy usage vs temperature"></figure>
        </body>
        </html>
        """);
}

void DrawGasTempScatterChart(List<Consumption> gasConsumption, Dictionary<string, decimal> tempDict)
{
    var xs = new List<decimal>();
    var ys = new List<decimal>();

    foreach (var gasDay in gasConsumption) {
        var temp = tempDict[gasDay.Start.ToString("yyyy-MM-dd")];
        ys.Add(gasDay.Quantity);
        xs.Add(temp);
    }

    ScottPlot.Plot gasPlot = new();
    gasPlot.Add.ScatterPoints(xs, ys);

    gasPlot.XLabel("Daily temperature average (deg C)");
    gasPlot.YLabel("Consumption (m^3)");
    gasPlot.Title("Gas usage vs temperature");
    gasPlot.SavePng("output/gas-temp-scatter.png", 1000, 800);
}

void DrawGasTempScatterChartWithSplit(List<Consumption> gasConsumption, Dictionary<string, decimal> tempDict, DateTime splitDate)
{
    var xs1 = new List<decimal>();
    var ys1 = new List<decimal>();
    var xs2 = new List<decimal>();
    var ys2 = new List<decimal>();

    foreach (var gasDay in gasConsumption) {
        var temp = tempDict[gasDay.Start.ToString("yyyy-MM-dd")];

        if (gasDay.Start < splitDate) {
            ys1.Add(gasDay.Quantity);
            xs1.Add(temp);
        }
        else {
            ys2.Add(gasDay.Quantity);
            xs2.Add(temp);
        }
    }

    ScottPlot.Plot gasPlot = new();
    gasPlot.Add.ScatterPoints(xs1, ys1, ScottPlot.Color.FromColor(Color.Blue));
    gasPlot.Add.ScatterPoints(xs2, ys2, ScottPlot.Color.FromColor(Color.Red));

    gasPlot.XLabel("Daily temperature average (deg C)");
    gasPlot.YLabel("Consumption (m^3)");
    gasPlot.Title("Gas usage vs temperature");
    gasPlot.SavePng("output/gas-temp-scatter-split.png", 1000, 800);
}

void DrawGasPlusElectricityTempScatterChart(List<Consumption> gasConsumption, List<Consumption> electricConsumption, Dictionary<string, decimal> tempDict)
{
    var electricLookup = electricConsumption.ToDictionary(x => x.Start.ToString("yyyy-MM-dd"), x => x.Quantity);

    var xs = new List<decimal>();
    var ys = new List<double>();

    foreach (var gasDay in gasConsumption) {
        var day = gasDay.Start.ToString("yyyy-MM-dd");
        var temp = tempDict[day];
        var electricForDay = electricLookup[day];

        // add all the usages together and convert gas m^3 to rough kwh (for exact, need to switch the 38 for our specific caloric value)
        var totalKwh = (double)electricForDay + ((double)gasDay.Quantity * 38 * 1.02264 / 3.6);

        ys.Add(totalKwh);
        xs.Add(temp);
    }

    ScottPlot.Plot gasPlot = new();
    gasPlot.Add.ScatterPoints(xs, ys);

    gasPlot.XLabel("Daily temperature average (deg C)");
    gasPlot.YLabel("Consumption (kWh)");
    gasPlot.Title("Total energy usage vs temperature");
    gasPlot.SavePng("output/total-energy-temp-scatter.png", 1000, 800);
}

void DrawGasUsage(List<Consumption> electricConsumption)
{
    ScottPlot.Plot gasPlot = new();
    gasPlot.Axes.DateTimeTicksBottom();
    gasPlot.Add.Scatter(gasConsumption.Select(x => x.Start.ToLocalTime().DateTime).ToArray(), gasConsumption.Select(x => x.Quantity).ToArray());

    gasPlot.XLabel("Date");
    gasPlot.YLabel("Consumption (m^3)");
    gasPlot.Title("Gas usage");
    gasPlot.Axes.SetLimitsY(0, 12.5);
    gasPlot.SavePng("output/gas-usage.png", 1000, 800);
}

void DrawElectricityUsage(List<Consumption> electricConsumption)
{
    ScottPlot.Plot leccyPlot = new();
    leccyPlot.Axes.DateTimeTicksBottom();
    leccyPlot.Add.Scatter(electricConsumption.Select(x => x.Start.ToLocalTime().DateTime).ToArray(), electricConsumption.Select(x => x.Quantity).ToArray());

    leccyPlot.XLabel("Date");
    leccyPlot.YLabel("Consumption (kWh)");
    leccyPlot.Title("Electricity usage");
    leccyPlot.Axes.SetLimitsY(0, 25);
    leccyPlot.SavePng("output/electric-usage.png", 1000, 800);
}


async Task<List<WeatherDay>> GetWeather(string vcApiKey, HttpClient client, string weatherLocation, DateTime from, DateTime to) {
    var fromString = from.ToString("yyyy-MM-dd");
    var toString = to.ToString("yyyy-MM-dd");

    var uri = $"https://weather.visualcrossing.com/VisualCrossingWebServices/rest/services/timeline/{weatherLocation}/{fromString}/{toString}?unitGroup=metric&key={vcApiKey}&contentType=json";

    string body;

    var hash = CreateMD5(uri);
    var cacheFileName = Path.Combine(cacheDir, hash);
    if(File.Exists(cacheFileName)) {
        body = File.ReadAllText(cacheFileName);
    }
    else {
        //throw new Exception("");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);

        var response = await client.SendAsync(request);
        body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode(); 
        
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(cacheFileName, body);
    }
    
    WeatherResponse? weatherResponse = JsonSerializer.Deserialize<WeatherResponse>(body, new JsonSerializerOptions{
        PropertyNameCaseInsensitive = true
    });

    return weatherResponse.Days;
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
}
