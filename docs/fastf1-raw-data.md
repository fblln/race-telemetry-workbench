# FastF1 Raw Data Notes

This document explains the raw FastF1 objects used by `scripts/download_session.py`.
It is meant as a working README for the importer, not as a replacement for the
FastF1 API docs.

## Session Loading

```python
import fastf1

fastf1.Cache.enable_cache("data/fastf1-cache")

session = fastf1.get_session(2024, "Monza", "R")
session.load(laps=True, telemetry=True, weather=False, messages=False)
```

`get_session` resolves a year, event, and session type. `session.load` downloads
and parses the timing feeds. With `telemetry=True`, FastF1 also loads car data
and position data.

The project defaults to race sessions (`R`) because replay, lap comparison, and
storage planning are scoped around race data first. Other FastF1 session types
remain useful for later analysis, but they are explicit opt-ins.

## Driver Identity

FastF1's `session.drivers` contains driver numbers as strings:

```python
session.drivers[:5]
# ["16", "81", "4", "55", "44"]
```

The driver metadata maps those numbers to the abbreviations we want in our
database and CLI:

```python
driver = session.get_driver("16")
driver[["DriverNumber", "Abbreviation", "FullName", "TeamName"]].to_dict()
# {
#   "DriverNumber": "16",
#   "Abbreviation": "LEC",
#   "FullName": "Charles Leclerc",
#   "TeamName": "Ferrari"
# }
```

That is why `download_session.py` normalizes FastF1 driver references into
three-letter codes before filtering or writing manifests.

## Lap Data

`session.laps` is a table-like FastF1/Pandas object. Useful columns include:

```text
Time
Driver
DriverNumber
LapTime
LapNumber
Stint
PitOutTime
PitInTime
Sector1Time
Sector2Time
Sector3Time
Compound
TyreLife
FreshTyre
Team
LapStartTime
LapStartDate
TrackStatus
Position
Deleted
DeletedReason
FastF1Generated
IsAccurate
```

Example from Charles Leclerc's first racing lap at Monza 2024:

```python
lap = session.laps.pick_drivers("LEC").iloc[0]
lap[
    [
        "Driver",
        "DriverNumber",
        "LapNumber",
        "LapTime",
        "Sector1Time",
        "Sector2Time",
        "Sector3Time",
        "Compound",
        "TyreLife",
        "PitOutTime",
        "PitInTime",
    ]
].to_dict()
# {
#   "Driver": "LEC",
#   "DriverNumber": "16",
#   "LapNumber": 1.0,
#   "LapTime": Timedelta("0 days 00:01:28.179000"),
#   "Sector1Time": NaT,
#   "Sector2Time": Timedelta("0 days 00:00:29.989000"),
#   "Sector3Time": Timedelta("0 days 00:00:28.398000"),
#   "Compound": "MEDIUM",
#   "TyreLife": 1.0,
#   "PitOutTime": NaT,
#   "PitInTime": NaT
# }
```

Important importer mapping notes:

- `Driver` is already the three-letter code.
- `DriverNumber` is the racing number string.
- `LapTime` and sector columns are Pandas timedeltas.
- Missing values can be `NaT`, `NaN`, or `None` depending on the column.
- `IsAccurate` is FastF1's lap-quality flag; warnings can happen for individual
  drivers even when telemetry and position samples are present.

## Car Telemetry

Per-lap car telemetry comes from:

```python
car = lap.get_car_data()
```

Common columns:

```text
Date
RPM
Speed
nGear
Throttle
Brake
DRS
Source
Time
SessionTime
```

Example rows:

```python
car.head(2).to_dict(orient="records")
# [
#   {
#     "Date": Timestamp("2024-09-01 13:03:34.420000"),
#     "RPM": 10235.0,
#     "Speed": 0.0,
#     "nGear": 1,
#     "Throttle": 35.0,
#     "Brake": True,
#     "DRS": 1,
#     "Source": "car",
#     "Time": Timedelta("0 days 00:00:00.007000"),
#     "SessionTime": Timedelta("0 days 00:55:50.501000")
#   }
# ]
```

Importer mapping:

| FastF1 column | Database field |
|---|---|
| `Date` | `sample_time_utc` |
| `Speed` | `speed_kmh` |
| `Throttle` | `throttle_pct` |
| `Brake` | `brake_pct`, converted from boolean to `0` or `100` |
| `nGear` | `gear` |
| `RPM` | `rpm` |
| `DRS` | `drs` |

FastF1 car samples do not include distance by default from `get_car_data`.
Distance should come from FastF1 telemetry composition when the database importer
adds the final normalization layer.

## Composed Telemetry

The database importer should use FastF1's composed telemetry view for
`telemetry_samples`:

```python
telemetry = lap.get_telemetry()
```

For Monza 2024, that returns columns like:

```text
Date
SessionTime
DriverAhead
DistanceToDriverAhead
Time
RPM
Speed
nGear
Throttle
Brake
DRS
Source
Distance
RelativeDistance
Status
X
Y
Z
```

The useful difference from `get_car_data()` is that this view combines and
interpolates car and position channels. It includes lap distance, relative lap
progress, driver-ahead context, and track coordinates.

Example rows:

```python
telemetry.head(2).to_dict(orient="records")
# [
#   {
#     "Date": Timestamp("2024-09-01 13:03:34.413000"),
#     "SessionTime": Timedelta("0 days 00:55:50.494000"),
#     "DriverAhead": "",
#     "DistanceToDriverAhead": 0.0,
#     "Time": Timedelta("0 days 00:00:00"),
#     "RPM": 10238.92781368889,
#     "Speed": 0.0,
#     "nGear": 1,
#     "Throttle": 35.0,
#     "Brake": True,
#     "DRS": 1,
#     "Source": "interpolation",
#     "Distance": -0.00030438168781159105,
#     "RelativeDistance": -5.5616098948533764e-08,
#     "Status": "OnTrack",
#     "X": -1151.001657127824,
#     "Y": 1892.0473711678944,
#     "Z": 1884.0007008536452
#   }
# ]
```

Importer mapping:

| FastF1 column | Database field |
|---|---|
| `Date` | `sample_time_utc` |
| `SessionTime` | `session_time_ms` |
| `Time` | `lap_time_ms` |
| `Distance` | `distance_m` |
| `RelativeDistance` | `relative_distance` |
| `Speed` | `speed_kmh` |
| `Throttle` | `throttle_pct` |
| `Brake` | `brake_pct`, converted from boolean to `0` or `100` |
| `nGear` | `gear` |
| `RPM` | `rpm` |
| `DRS` | `drs` |
| `DriverAhead` | `driver_ahead` |
| `DistanceToDriverAhead` | `distance_to_driver_ahead_m` |
| `Status` | `track_status` |
| `Source` | `sample_source` |
| `X`, `Y`, `Z` | available for telemetry context, but raw replay position rows still come from `get_pos_data()` |

## Position Data

Per-lap position data comes from:

```python
position = lap.get_pos_data()
```

Common columns:

```text
Date
Status
X
Y
Z
Source
Time
SessionTime
```

Example rows:

```python
position.head(2).to_dict(orient="records")
# [
#   {
#     "Date": Timestamp("2024-09-01 13:03:34.469000"),
#     "Status": "OnTrack",
#     "X": -1151.0,
#     "Y": 1892.0,
#     "Z": 1884.0,
#     "Source": "pos",
#     "Time": Timedelta("0 days 00:00:00.056000"),
#     "SessionTime": Timedelta("0 days 00:55:50.550000")
#   }
# ]
```

Importer mapping:

| FastF1 column | Database field |
|---|---|
| `Date` | `sample_time_utc` |
| `X` | `x` |
| `Y` | `y` |
| `Z` | `z` |
| `Status` | `track_status` |
| `Source` | `sample_source` |

## Circuit Info

FastF1 can provide circuit annotations separately from lap telemetry:

```python
circuit_info = session.get_circuit_info()
```

For Monza 2024, this object contains:

```text
corners
marshal_lights
marshal_sectors
rotation
```

The marker tables have this shape:

```text
X
Y
Number
Letter
Angle
Distance
```

Example corner rows:

```text
             X             Y  Number Letter       Angle  Distance
0  -569.580505   8153.724609       1         153.787332       NaN
1  -146.754578   8474.981445       2         -13.835843       NaN
2   611.600159  13310.620117       3         133.028518       NaN
```

Example rotation:

```text
95.0
```

Importer mapping:

| FastF1 field | Database field |
|---|---|
| `rotation` | `circuit_metadata.rotation_degrees` |
| `corners` rows | `circuit_markers` with `marker_type = "corner"` |
| `marshal_lights` rows | `circuit_markers` with `marker_type = "marshal_light"` |
| `marshal_sectors` rows | `circuit_markers` with `marker_type = "marshal_sector"` |
| `X` | `circuit_markers.x` |
| `Y` | `circuit_markers.y` |
| `Number` | `circuit_markers.marker_number` |
| `Letter` | `circuit_markers.marker_letter` |
| `Angle` | `circuit_markers.angle_degrees` |
| `Distance` | `circuit_markers.distance_m` |

The application should not depend on external track image or vector assets for
replay correctness. The track outline should be derived from imported
`position_samples`, while circuit info provides annotations such as corners and
marshal markers.

## Weather Data

FastF1 weather data is loaded with:

```python
session.load(weather=True)
weather = session.weather_data
```

Columns:

```text
Time
AirTemp
Humidity
Pressure
Rainfall
TrackTemp
WindDirection
WindSpeed
```

For Monza 2024 Race, the cached data contains 133 weather rows from
`00:00:26.141` to `02:12:26.670` session time. The sample spacing is almost
exactly one minute:

```text
minimum delta: 59.940 s
median delta: 60.004 s
maximum delta: 60.057 s
```

Observed Monza 2024 ranges:

```text
AirTemp:       32.2 to 34.1 C
TrackTemp:     43.5 to 54.6 C
Humidity:      30.0 to 38.0 %
Pressure:      992.7 to 993.9 mbar
WindDirection: 0 to 352 degrees
WindSpeed:     0.4 to 3.2 m/s
Rainfall:      False only
```

Importer mapping:

| FastF1 column | Database field |
|---|---|
| `Time` | `weather_samples.session_time_ms`, plus calculated `sample_time_utc` from session start |
| `AirTemp` | `air_temp_c` |
| `TrackTemp` | `track_temp_c` |
| `Humidity` | `humidity_pct` |
| `Pressure` | `pressure_mbar` |
| `Rainfall` | `rainfall` |
| `WindDirection` | `wind_direction_deg` |
| `WindSpeed` | `wind_speed_mps` |

Weather is contextual data, not high-frequency car telemetry. In the UI it is
best shown as timeline overlays, current-at-replay-time values, or chart
background/context rather than as per-car samples.

## Track Status And Race Control

FastF1 can load status and race-control timelines with:

```python
session.load(messages=True)
track_status = session.track_status
session_status = session.session_status
messages = session.race_control_messages
```

`track_status` is a compact timeline:

```text
Time
Status
Message
```

Known status codes include:

```text
1 = track clear
2 = yellow flag
4 = safety car
5 = red flag
6 = virtual safety car deployed
7 = virtual safety car ending
```

`race_control_messages` is more verbose:

```text
Time
Category
Message
Status
Flag
Scope
Sector
RacingNumber
Lap
```

These messages are the right source for timeline annotations such as DRS status,
yellow flags, safety car, virtual safety car, pit-exit status, investigations,
sector-specific messages, and driver-specific messages.

## Cache Behavior

FastF1 uses a persistent HTTP cache. In this project the cache lives at:

```text
data/fastf1-cache/fastf1_http_cache.sqlite
```

The first run downloads data from FastF1/F1 timing endpoints and writes to that
SQLite cache. Later runs can reuse cached responses and are much faster. A warm
cache still may attempt to revalidate schedule data; if the network is blocked,
FastF1 can fall back to cached responses for data that has already been fetched.

Useful checks:

```bash
python3 scripts/download_session.py --year 2024 --event Monza
python3 scripts/download_session.py --year 2024 --event Monza --drivers LEC --limit-laps 3
du -sh data/fastf1-cache
```

The second command writes a subset manifest, leaving the full-session manifest
intact.

The script has unit coverage for cache directory creation and enabling. The
end-to-end cache behavior is best tested by running the same session twice: the
second run should log `Using cached data` for session, timing, car, and position
data.
