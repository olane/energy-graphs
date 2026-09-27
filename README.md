# energy-graphs

To set your secret values:

```
dotnet user-secrets set octopus_api_key <octopus api key>
dotnet user-secrets set octopus_account <octopus account number>   # optional, enables meter discovery

# optional fallback used only when octopus_account is not set
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
  -e octopus_account=<octopus account number> \
  -e visualcrossing_key=<VisualCrossing.com API key> \
  energy-graphs
```

Then open http://localhost:8080.

Each environment variable is an alternative to the matching `dotnet user-secrets` entry above.

## Meters

If `octopus_account` is set, meters are discovered from `GET /v1/accounts/<account>/` instead of being configured:

- It selects the **active property** (`moved_out_at` is null; if several, the most recently moved-into). The account endpoint lists every property ever, including old houses, so this filter is what keeps them out.
- It takes all non-export electricity meter points and all gas meter points, **all serials each**, and merges consumption across them. This means a meter exchange keeps the full history (e.g. an old `18P2136356` plus its replacement `22E5239768`).

If `octopus_account` is not set, the app falls back to the explicit `electricity_mpan`/`electricity_serial`/`gas_mprn`/`gas_serial` values.

## Configuration

The date range defaults to **all time**: `from_date` comes from the discovered property's move-in date (falling back to open-ended when unknown), and `to_date` is today. Both can be overridden with settings/config (env vars or user-secrets):

| Setting | Default | Purpose |
| --- | --- | --- |
| `from_date` | property move-in date (or open-ended) | First day to fetch (`yyyy-MM-dd`) |
| `to_date` | today (UTC) | Last day to fetch (`yyyy-MM-dd`) |
| `split_date` | unset | When set, draws `gas-temp-scatter-split.png` colouring data before/after this date; the chart is omitted when unset |
| `cache_dir` | `cache` | Weather cache directory |

Weather is fetched only for the days that actually have consumption data. Usage graphs auto-scale their y-axis.

### Prebuilt image

CI publishes images to GitHub Container Registry on pushes to `main` and on `v*` tags:

```
docker run --rm -p 8080:8080 \
  -v energy-graphs-cache:/app/cache \
  -e octopus_api_key=<octopus api key> \
  ... \
  ghcr.io/olane/energy-graphs:latest
```

`main` pushes also publish the short commit SHA (`sha-<short>`) and the `main` tag; tags publish the semver version. GHCR packages are private by default, so to allow unauthenticated `docker pull` make the package public in the repository's package settings.

### Weather cache

VisualCrossing calls are billed, so weather is cached **per day** as `cache/<md5(location|date)>`. Each run only requests days that aren't cached yet — normally just the newly added day — so extending the range never re-fetches (and re-bills) the whole period. The key is the location and date, not the API key or the request range, so it stays valid as the window grows.

The `-v energy-graphs-cache:/app/cache` mount above is important: without it, the cache lives in the container's writable layer and is lost on every container replacement, forcing fresh (paid) API calls. On first creation the named volume is seeded from whatever `cache/` directory exists at `docker build` time (the local one is copied in if present), and it is never overwritten by later rebuilds.

To reuse your existing host cache directly instead, bind-mount it:

```
docker run --rm -p 8080:8080 -v "$PWD/cache:/app/cache" ... energy-graphs
```

For local runs, the cache directory can be redirected with the `cache_dir` setting (default `cache`).

## Notes
Gas units are assumed to be m^3 but this is only true on the API if you have a SMETS2 meter. If you have a SMETS1 meter all gas values will be in kWh.

