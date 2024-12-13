# energy-graphs

To set your secret values:

```
dotnet user-secrets set octopus_api_key <octopus api key>
dotnet user-secrets set electricity_mpan <electric meter MPAN>
dotnet user-secrets set electricity_serial <electric meter serial number>
dotnet user-secrets set gas_mprn <gas meter MPRN>
dotnet user-secrets set gas_serial <gas meter serial number>
```

Get these from https://octopus.energy/dashboard/new/accounts/personal-details/api-access

Note that this saves the secrets in a way that won't get included in this repository but they are not encryped and will remain on your disk and in your terminal history.

Before running create your output folder:
```
mkdir output
```

To run:
```
dotnet run
```

## Notes
Gas units are assumed to be m^3 but this is only true on the API if you have a SMETS2 meter. If you have a SMETS1 meter all gas values will be in kWh.

