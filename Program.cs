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

var from = new DateTimeOffset(2024, 12, 01, 00, 00, 00, TimeSpan.FromHours(1));
var to = new DateTimeOffset(2024, 12, 13, 23, 59, 00, TimeSpan.FromHours(1));

var consumption = await octopusClient.GetElectricityConsumptionAsync(electricityMPAN, electricitySerial, from, to, Interval.Day);
consumption.ToList()
.ForEach(c => Console.WriteLine(
    $"[{c.Start.ToLocalTime()}-{c.End.ToLocalTime()}), {c.Quantity:00.00}"));

// For SMETS1 this is kwh equivalent, for SMETS2 it is in m^3
var gasConsumption = await octopusClient.GetGasConsumptionAsync(gasMPRN, gasSerial, from, to, Interval.Day);
gasConsumption.ToList()
.ForEach(c => Console.WriteLine(
    $"[{c.Start.ToLocalTime()}-{c.End.ToLocalTime()}), {c.Quantity:00.00}"));

