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
| `split_dates` | one split per calendar year | Comma-separated `yyyy-MM-dd` split points for the `*-split.png` graphs (one colour per period, with a legend). When unset the periods default to calendar-year boundaries, so each year gets its own colour. Applied to both the gas and total-energy scatter graphs. `split_date` is accepted as an alias for a single split. |
| `half_hourly_from` | 179 days before `to_date` (clamped to `from_date`) | First day of the half-hourly electricity window used for the time-of-day analysis. |
| `change_date` | auto-detected | Date to compare the before/after half-hourly profiles around. Set this (`yyyy-MM-dd`) if the detected step is wrong. |
| `time_zone` | `Europe/London` | IANA time zone used to bucket half-hourly readings into local dates and times of day. |
| `cache_dir` | `cache` | Weather cache directory |
| `refresh_hours` | `24` | Hours between automatic graph regenerations. Set to `0` to generate only once at startup. |

Weather is fetched only for the days that actually have consumption data. Usage graphs auto-scale their y-axis. Date axes are labelled like `Oct 26` (with the year shown at January boundaries).

The graphs are generated once at startup and then regenerated every `refresh_hours` (default 24). Because weather is cached per day, a daily regeneration only ever fetches the newly added day — normally a single weather API call per day.

Graphs produced in `output/`:

| File | Description |
| --- | --- |
| `gas-usage.png` | Gas consumption over time |
| `electric-usage.png` | Electricity consumption over time |
| `gas-temp-scatter.png` | Gas usage vs daily average temperature |
| `gas-temp-scatter-split.png` | Gas usage vs temperature, one colour per period (see `split_dates`) with a dashed trend line per period, fitted to days at or below 16 °C and excluding the Christmas break |
| `electric-temp-scatter.png` | Electricity usage vs daily average temperature |
| `electric-temp-scatter-split.png` | Electricity usage vs temperature, one colour per period with a dashed trend line per period, fitted to days at or below 16 °C and excluding the Christmas break |
| `total-energy-temp-scatter.png` | Gas + electricity (kWh) vs temperature |
| `total-energy-temp-scatter-split.png` | Gas + electricity (kWh) vs temperature, one colour per period with a dashed trend line per period, fitted to days at or below 16 °C and excluding the Christmas break |
| `electric-daylight-scatter.png` | Electricity usage vs daylight hours (from weather sunrise/sunset) |
| `electric-halfhourly-heatmap.png` | Electricity use by date and half hour (see below) |
| `electric-profile-before-after.png` | Average half-hourly profile before vs after the step change |
| `electric-profile-change.png` | Increase per half hour after the step, to show whether it is baseload or time-specific |

## Investigating a step change in electricity use

The time-of-day analysis fetches half-hourly electricity for the recent window and compares
the shape of the day before and after a step change. It is meant to distinguish:

- **an always-on load** (a pump, fridge/freezer, server, immersion heater left on, a new
  appliance): the increase appears at every half hour, and the per-half-hour change chart is
  roughly flat;
- **a time-specific load** (heating/hot water schedule, EV charging, cooking, tumble dryer,
  lighting): the change is concentrated in a particular part of the day.

The generated `index.html` also prints a short text summary: the detected step date, the
median daily change, whether the increase is broad or concentrated, and how gas and outdoor
temperature changed over the same windows. It also compares the same calendar weeks a year
earlier, so a genuine new load can be told apart from the normal autumn heating step.

How to read the result:

1. **Heatmap** — each row is a local day and each column is a local half hour. Look for the
   date the colour gets brighter. If the whole row brightens, it is a step across the day; if
   only some columns brighten, the cause is time-specific.
2. **Before/after profile** — the average day either side of the step. A parallel shift means
   baseload; a wider gap in one hump means a timed appliance.
3. **Change-by-half-hour** — the delta directly. A flat bar across the day is the signature of
   something that switched on and stayed on.
4. **Gas and temperature** — if gas fell while electricity rose, suspect fuel switching (for
   example an immersion heater or electric heating replacing gas). If both gas and temperature
   moved a lot, the comparison is partly seasonal; extend `half_hourly_from` and set
   `change_date` to compare like-for-like weeks.
5. **Same weeks last year** — if electricity did not step up by a comparable amount then, the
   rise is beyond the normal seasonal change. If it did, the step is mostly seasonal.

`electric-temp-scatter-split.png` is the clearest visual check: if this year's points sit above
last year's trend line at the same temperature, the load has genuinely increased.

Common causes worth checking once the timing is known: a new always-on appliance or pump, an
immersion heater or underfloor heating left on, EV charging, a dehumidifier, a tariff/meter
change (including estimated readings), and the clocks changing (a local day has 46 or 50 half
hours on the DST transition days; the analysis averages duplicate half hours and leaves the
missing one as a gap).

`change_date` overrides the automatically detected date. The detector looks for the largest
sustained rise in the 14-day median either side of a candidate day, so a holiday return or a
cold snap can occasionally be picked instead of a genuine appliance change.

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

VisualCrossing calls are billed, so weather is cached **per day** as `cache/<md5(location|date|version)>`. Each run only requests days that aren't cached yet — normally just the newly added day — so extending the range never re-fetches (and re-bills) the whole period. The key is the location, date and cache schema version, not the API key or the request range, so it stays valid as the window grows.

The `-v energy-graphs-cache:/app/cache` mount above is important: without it, the cache lives in the container's writable layer and is lost on every container replacement, forcing fresh (paid) API calls. On first creation the named volume is seeded from whatever `cache/` directory exists at `docker build` time (the local one is copied in if present), and it is never overwritten by later rebuilds.

To reuse your existing host cache directly instead, bind-mount it:

```
docker run --rm -p 8080:8080 -v "$PWD/cache:/app/cache" ... energy-graphs
```

For local runs, the cache directory can be redirected with the `cache_dir` setting (default `cache`).

## Notes
Gas units are assumed to be m^3 but this is only true on the API if you have a SMETS2 meter. If you have a SMETS1 meter all gas values will be in kWh.

