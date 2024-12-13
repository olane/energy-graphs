using ImpSoft.OctopusEnergy.Api;
using Microsoft.Extensions.Configuration;
var config = new ConfigurationBuilder().AddUserSecrets<Program>().Build();

var secretProvider = config.Providers.First();

secretProvider.TryGet("octopus_api_key", out var apiKey);
secretProvider.TryGet("electricity_mpan", out var electricityMPAN);
secretProvider.TryGet("electricity_serial", out var electricitySerial);
secretProvider.TryGet("gas_mprn", out var gasMPRN);
secretProvider.TryGet("gas_serial", out var gasSerial);

using var httpClient = new HttpClient();

httpClient.SetAuthenticationHeaderFromApiKey(apiKey);

// Create the api wrapper
var octopusClient = new OctopusEnergyClient(httpClient);

var from = new DateTimeOffset(2023, 8, 01, 00, 00, 00, TimeSpan.FromHours(0));
var to = new DateTimeOffset(2024, 12, 13, 23, 59, 00, TimeSpan.FromHours(0));

var electricConsumption = (await octopusClient.GetElectricityConsumptionAsync(electricityMPAN, electricitySerial, from, to, Interval.Day)).ToList();

// For SMETS1 this is kwh equivalent, for SMETS2 it is in m^3
var gasConsumption = (await octopusClient.GetGasConsumptionAsync(gasMPRN, gasSerial, from, to, Interval.Day)).ToList();

DrawGasUsage(gasConsumption);
DrawElectricityUsage(electricConsumption);

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
