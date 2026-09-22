# energy-graphs

To set your secret values:

```
dotnet user-secrets set octopus_api_key <octopus api key>
dotnet user-secrets set electricity_mpan <electric meter MPAN>
dotnet user-secrets set electricity_serial <electric meter serial number>
dotnet user-secrets set gas_mprn <gas meter MPRN>
dotnet user-secrets set gas_serial <gas meter serial number>

dotnet user-secrets set visualcrossing_key <VisualCrossing.com API key>
```

Get Octopus values from https://octopus.energy/dashboard/new/accounts/personal-details/api-access

Get weather API key from visualcrossing.com

Note that this saves the secrets in a way that won't get included in this repository but they are not encryped and will remain on your disk and in your terminal history.

To run:
```
dotnet run
```

Then open http://localhost:5000 (or the URL printed on startup).

## Run with Docker

Build and run:
```
docker build -t energy-graphs .
docker run --rm -p 8080:8080 \
  -v energy-graphs-cache:/app/cache \
  -e octopus_api_key=<octopus api key> \
  -e electricity_mpan=<electric meter MPAN> \
  -e electricity_serial=<electric meter serial number> \
  -e gas_mprn=<gas meter MPRN> \
  -e gas_serial=<gas meter serial number> \
  -e visualcrossing_key=<VisualCrossing.com API key> \
  energy-graphs
```

Then open http://localhost:8080.

Each environment variable is an alternative to the matching `dotnet user-secrets` entry above.

### Weather cache

VisualCrossing calls are billed, so responses are cached as `cache/<md5-of-url>` and reused on later runs. The cache key includes the API key and the hard-coded date range, so it stays valid across restarts.

The `-v energy-graphs-cache:/app/cache` mount above is important: without it, the cache lives in the container's writable layer and is lost on every container replacement, forcing a fresh (paid) API call. On first creation the named volume is seeded from whatever `cache/` directory exists at `docker build` time (the local one is copied in if present), and it is never overwritten by later rebuilds.

To reuse your existing host cache directly instead, bind-mount it:

```
docker run --rm -p 8080:8080 -v "$PWD/cache:/app/cache" ... energy-graphs
```

For local runs, the cache directory can be redirected with the `cache_dir` setting (default `cache`).

## Notes
Gas units are assumed to be m^3 but this is only true on the API if you have a SMETS2 meter. If you have a SMETS1 meter all gas values will be in kWh.

