#!/usr/bin/env python3
"""Validate bacinger/f1-circuits GPS shapes against FastF1 lap shapes.

The repository at https://github.com/bacinger/f1-circuits provides circuit
GeoJSON LineStrings in WGS84 longitude/latitude. FastF1 provides local circuit
X/Y/Z positions, not GPS. This script compares the two by:

1. Loading the 2025 championship circuit list from f1-circuits.
2. Loading one clean FastF1 race lap for the same round.
3. Projecting the repository GPS line into local meters.
4. Resampling both shapes by lap progress.
5. Fitting FastF1 local X/Y onto the GPS shape with scale + rotation +
   translation.
6. Reporting fit errors and generating a conservative encoded polyline.

The fit errors are shape-validation metrics, not absolute GPS telemetry error.
FastF1 has no lat/lon channel, so we cannot measure absolute geodetic error
from the car. We can measure whether the repository GPS outline has the same
geometry as the actual FastF1 lap shape.
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import subprocess
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any


LatLon = tuple[float, float]
XY = tuple[float, float]


@dataclass(frozen=True)
class FitStats:
    direction: str
    start_offset_samples: int
    sample_count: int
    rmse_m: float
    p50_m: float
    p95_m: float
    max_m: float
    scale_m_per_fastf1_unit: float
    rotation_degrees: float


@dataclass(frozen=True)
class SimplificationStats:
    source_points: int
    simplified_points: int
    encoded_chars: int
    tolerance_m: float
    rmse_m: float
    p95_m: float
    max_m: float
    source_length_m: float
    simplified_length_m: float
    length_delta_m: float
    length_delta_pct: float


@dataclass(frozen=True)
class FastF1Lap:
    driver: str
    lap_number: int
    points: list[XY]
    path_length_m: float


@dataclass(frozen=True)
class FastF1Candidate:
    """Candidate lap before expensive GPS-shape fitting."""

    lap: FastF1Lap
    length_error_m: float


@dataclass(frozen=True)
class ScoredFastF1Lap:
    """A FastF1 lap after fitting it to the repository GPS shape."""

    lap: FastF1Lap
    fit: FitStats
    length_error_m: float


@dataclass(frozen=True)
class DirectLineStats:
    """Direct pointwise distance between two already-aligned meter-space lines."""

    sample_count: int
    rmse_m: float
    p50_m: float
    p95_m: float
    max_m: float


def ensure_f1_circuits_repo(repo_dir: Path, repo_url: str) -> None:
    """Clone bacinger/f1-circuits if the requested local path is missing."""

    if (repo_dir / "championships").is_dir() and (repo_dir / "circuits").is_dir():
        return

    repo_dir.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(
        ["git", "clone", "--depth", "1", repo_url, str(repo_dir)],
        check=True,
    )


def load_json(path: Path) -> Any:
    with path.open(encoding="utf-8") as handle:
        return json.load(handle)


def load_circuit_latlon(repo_dir: Path, circuit_id: str) -> tuple[list[LatLon], dict[str, Any]]:
    """Load one f1-circuits GeoJSON LineString as [lat, lon] points."""

    geojson = load_json(repo_dir / "circuits" / f"{circuit_id}.geojson")
    feature = geojson["features"][0]
    geometry = feature["geometry"]
    if geometry["type"] != "LineString":
        raise ValueError(f"{circuit_id} geometry is {geometry['type']}, expected LineString")

    points = [(float(lat), float(lon)) for lon, lat in geometry["coordinates"]]
    if points[-1] != points[0]:
        points.append(points[0])

    return points, feature.get("properties", {})


def projection_origin(points: list[LatLon]) -> LatLon:
    return (
        sum(lat for lat, _ in points) / len(points),
        sum(lon for _, lon in points) / len(points),
    )


def latlon_to_xy(point: LatLon, origin: LatLon) -> XY:
    lat, lon = point
    origin_lat, origin_lon = origin
    radius_m = 6_371_000.0
    cos_origin = math.cos(math.radians(origin_lat))
    return (
        math.radians(lon - origin_lon) * radius_m * cos_origin,
        math.radians(lat - origin_lat) * radius_m,
    )


def xy_to_latlon(point: XY, origin: LatLon) -> LatLon:
    x, y = point
    origin_lat, origin_lon = origin
    radius_m = 6_371_000.0
    cos_origin = math.cos(math.radians(origin_lat))
    return (
        math.degrees(y / radius_m) + origin_lat,
        math.degrees(x / (radius_m * cos_origin)) + origin_lon,
    )


def path_length(points: list[XY]) -> float:
    return sum(math.hypot(b[0] - a[0], b[1] - a[1]) for a, b in zip(points, points[1:]))


def without_duplicate_closure(points: list[XY]) -> list[XY]:
    if len(points) >= 2 and points[0] == points[-1]:
        return points[:-1]
    return points


def closed_path(points: list[XY]) -> list[XY]:
    points = without_duplicate_closure(points)
    return points if points[-1] == points[0] else points + [points[0]]


def resample_closed(points: list[XY], sample_count: int) -> list[XY]:
    loop = closed_path(points)
    distances = [0.0]
    total = 0.0
    for a, b in zip(loop, loop[1:]):
        total += math.hypot(b[0] - a[0], b[1] - a[1])
        distances.append(total)

    sampled: list[XY] = []
    segment_index = 0
    for i in range(sample_count):
        target = total * i / sample_count
        while segment_index < len(distances) - 2 and distances[segment_index + 1] < target:
            segment_index += 1

        segment_length = max(distances[segment_index + 1] - distances[segment_index], 1e-9)
        t = (target - distances[segment_index]) / segment_length
        ax, ay = loop[segment_index]
        bx, by = loop[segment_index + 1]
        sampled.append((ax + t * (bx - ax), ay + t * (by - ay)))

    return sampled


def rotate_samples(points: list[XY], offset: int) -> list[XY]:
    offset = offset % len(points)
    return points[offset:] + points[:offset]


def percentile(values: list[float], pct: float) -> float:
    ordered = sorted(values)
    if not ordered:
        return 0.0
    index = (len(ordered) - 1) * pct
    lower = math.floor(index)
    upper = math.ceil(index)
    if lower == upper:
        return ordered[lower]
    weight = index - lower
    return ordered[lower] * (1 - weight) + ordered[upper] * weight


def similarity_fit(source: list[XY], target: list[XY]) -> FitStats:
    """Fit source onto target with scale + rotation + translation."""

    source_complex = [complex(x, y) for x, y in source]
    target_complex = [complex(x, y) for x, y in target]
    source_mean = sum(source_complex) / len(source_complex)
    target_mean = sum(target_complex) / len(target_complex)
    centered_source = [point - source_mean for point in source_complex]
    centered_target = [point - target_mean for point in target_complex]
    denominator = sum(abs(point) ** 2 for point in centered_source)
    if denominator == 0:
        raise ValueError("source shape has no extent")

    c = sum(t * s.conjugate() for s, t in zip(centered_source, centered_target)) / denominator
    translation = target_mean - c * source_mean
    transformed = [c * point + translation for point in source_complex]
    errors = [abs(a - b) for a, b in zip(transformed, target_complex)]

    return FitStats(
        direction="forward",
        start_offset_samples=0,
        sample_count=len(source),
        rmse_m=math.sqrt(sum(error * error for error in errors) / len(errors)),
        p50_m=percentile(errors, 0.50),
        p95_m=percentile(errors, 0.95),
        max_m=max(errors),
        scale_m_per_fastf1_unit=abs(c),
        rotation_degrees=math.degrees(math.atan2(c.imag, c.real)),
    )


def validate_shape(
    fastf1_xy: list[XY],
    gps_xy: list[XY],
    sample_count: int,
    offset_step: int,
) -> FitStats:
    """Find best FastF1-vs-GPS fit across direction and start offset."""

    source = resample_closed(fastf1_xy, sample_count)
    target_forward = resample_closed(gps_xy, sample_count)
    target_reversed = list(reversed(target_forward))
    best: FitStats | None = None

    def score_offset(direction: str, target: list[XY], offset: int) -> FitStats:
        stats = similarity_fit(source, rotate_samples(target, offset))
        return FitStats(
            direction=direction,
            start_offset_samples=offset,
            sample_count=stats.sample_count,
            rmse_m=stats.rmse_m,
            p50_m=stats.p50_m,
            p95_m=stats.p95_m,
            max_m=stats.max_m,
            scale_m_per_fastf1_unit=stats.scale_m_per_fastf1_unit,
            rotation_degrees=stats.rotation_degrees,
        )

    for direction, target in (("forward", target_forward), ("reversed", target_reversed)):
        direction_best: FitStats | None = None
        tested_offsets: set[int] = set()
        for offset in range(0, sample_count, max(1, offset_step)):
            tested_offsets.add(offset)
            stats = score_offset(direction, target, offset)
            if direction_best is None or stats.rmse_m < direction_best.rmse_m:
                direction_best = stats

        # A coarse offset grid can miss the correct phase badly on compact,
        # chicane-heavy tracks. Refine around the best coarse phase so
        # diagnostics can use a larger offset step without false failures.
        if direction_best is not None and offset_step > 1:
            for delta in range(-offset_step, offset_step + 1):
                offset = (direction_best.start_offset_samples + delta) % sample_count
                if offset in tested_offsets:
                    continue
                stats = score_offset(direction, target, offset)
                if stats.rmse_m < direction_best.rmse_m:
                    direction_best = stats

        if direction_best is not None and (best is None or direction_best.rmse_m < best.rmse_m):
            best = direction_best

    if best is None:
        raise RuntimeError("could not validate shape")

    return best


def fit_aligned_samples(
    fastf1_xy: list[XY],
    gps_xy: list[XY],
    fit: FitStats,
    sample_count: int,
) -> list[XY]:
    """Transform a FastF1 lap into the repository GPS meter-space phase.

    `validate_shape` allows circular start offsets and reversed direction while
    fitting. This helper applies the same similarity transform, then undoes the
    offset/direction so returned samples line up with the repository GPS line's
    start/progress order. That makes averaging multiple fitted laps meaningful.
    """

    source = resample_closed(fastf1_xy, sample_count)
    target = resample_closed(gps_xy, sample_count)
    target_for_fit = list(reversed(target)) if fit.direction == "reversed" else target
    offset = round(fit.start_offset_samples / fit.sample_count * sample_count)
    target_for_fit = rotate_samples(target_for_fit, offset)

    source_complex = [complex(x, y) for x, y in source]
    target_complex = [complex(x, y) for x, y in target_for_fit]
    source_mean = sum(source_complex) / len(source_complex)
    target_mean = sum(target_complex) / len(target_complex)
    centered_source = [point - source_mean for point in source_complex]
    centered_target = [point - target_mean for point in target_complex]
    c = sum(t * s.conjugate() for s, t in zip(centered_source, centered_target)) / sum(
        abs(point) ** 2 for point in centered_source
    )
    translation = target_mean - c * source_mean
    transformed = [(c * point + translation) for point in source_complex]
    transformed_xy = [(point.real, point.imag) for point in transformed]

    aligned = rotate_samples(transformed_xy, sample_count - offset)
    if fit.direction == "reversed":
        aligned = list(reversed(aligned))

    return aligned


def average_fitted_laps(
    scored_laps: list[ScoredFastF1Lap],
    gps_xy: list[XY],
    sample_count: int,
) -> list[XY]:
    """Average fitted FastF1 lap samples in repository GPS meter-space."""

    if not scored_laps:
        raise ValueError("at least one scored lap is required")

    aligned_laps = [
        fit_aligned_samples(item.lap.points, gps_xy, item.fit, sample_count)
        for item in scored_laps
    ]
    averaged: list[XY] = []
    for sample_index in range(sample_count):
        x = sum(lap[sample_index][0] for lap in aligned_laps) / len(aligned_laps)
        y = sum(lap[sample_index][1] for lap in aligned_laps) / len(aligned_laps)
        averaged.append((x, y))

    averaged.append(averaged[0])
    return averaged


def direct_line_stats(a_xy: list[XY], b_xy: list[XY], sample_count: int) -> DirectLineStats:
    """Compare two meter-space lines by equal-progress pointwise distance."""

    a_samples = resample_closed(a_xy, sample_count)
    b_samples = resample_closed(b_xy, sample_count)
    errors = [
        math.hypot(a[0] - b[0], a[1] - b[1])
        for a, b in zip(a_samples, b_samples)
    ]
    return DirectLineStats(
        sample_count=sample_count,
        rmse_m=math.sqrt(sum(error * error for error in errors) / len(errors)),
        p50_m=percentile(errors, 0.50),
        p95_m=percentile(errors, 0.95),
        max_m=max(errors),
    )


def perpendicular_distance(point: XY, start: XY, end: XY) -> float:
    px, py = point
    ax, ay = start
    bx, by = end
    dx = bx - ax
    dy = by - ay
    if dx == 0 and dy == 0:
        return math.hypot(px - ax, py - ay)
    t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)))
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))


def rdp(points: list[XY], tolerance_m: float) -> list[XY]:
    if len(points) <= 2:
        return points

    start = points[0]
    end = points[-1]
    index = -1
    max_distance = -1.0
    for i in range(1, len(points) - 1):
        distance = perpendicular_distance(points[i], start, end)
        if distance > max_distance:
            index = i
            max_distance = distance

    if max_distance > tolerance_m:
        return rdp(points[: index + 1], tolerance_m)[:-1] + rdp(points[index:], tolerance_m)
    return [start, end]


def distance_to_polyline(point: XY, polyline: list[XY]) -> float:
    return min(
        perpendicular_distance(point, a, b)
        for a, b in zip(polyline, polyline[1:])
    )


def simplification_stats(source_xy: list[XY], simplified_xy: list[XY], tolerance_m: float, encoded: str) -> SimplificationStats:
    errors = [distance_to_polyline(point, simplified_xy) for point in source_xy]
    source_length = path_length(source_xy)
    simplified_length = path_length(simplified_xy)
    delta = simplified_length - source_length
    return SimplificationStats(
        source_points=len(source_xy),
        simplified_points=len(simplified_xy),
        encoded_chars=len(encoded),
        tolerance_m=tolerance_m,
        rmse_m=math.sqrt(sum(error * error for error in errors) / len(errors)),
        p95_m=percentile(errors, 0.95),
        max_m=max(errors),
        source_length_m=source_length,
        simplified_length_m=simplified_length,
        length_delta_m=delta,
        length_delta_pct=(delta / source_length * 100) if source_length else 0.0,
    )


def encode_signed(value: int) -> str:
    value = ~(value << 1) if value < 0 else value << 1
    chunks: list[str] = []
    while value >= 0x20:
        chunks.append(chr((0x20 | (value & 0x1F)) + 63))
        value >>= 5
    chunks.append(chr(value + 63))
    return "".join(chunks)


def encode_polyline(points: list[LatLon], precision: int) -> str:
    factor = 10**precision
    last_lat = 0
    last_lon = 0
    output: list[str] = []
    for lat, lon in points:
        encoded_lat = int(round(lat * factor))
        encoded_lon = int(round(lon * factor))
        output.append(encode_signed(encoded_lat - last_lat))
        output.append(encode_signed(encoded_lon - last_lon))
        last_lat = encoded_lat
        last_lon = encoded_lon
    return "".join(output)


def score_fastf1_laps(
    year: int,
    event: str,
    cache_dir: Path,
    session_name: str,
    preferred_driver: str | None,
    reference_xy: list[XY],
    validation_samples: int,
    validation_offset_step: int,
    max_shape_candidates: int,
) -> list[ScoredFastF1Lap]:
    """Return the best FastF1 laps scored against the reference circuit shape.

    FastF1's `IsAccurate` flag is not sufficient for geometry validation. Some
    early race laps can include noisy or odd position slices while still being
    marked accurate. We therefore rank clean non-pit candidate laps by path
    length first, then run the expensive shape fit on the best length matches.
    """

    try:
        import fastf1  # type: ignore[import-not-found]
    except ModuleNotFoundError as exc:
        raise RuntimeError("FastF1 is not installed") from exc

    fastf1.Cache.enable_cache(str(cache_dir))
    session = fastf1.get_session(year, event, session_name)
    session.load(laps=True, telemetry=True, weather=False, messages=False)

    driver_codes: list[str] = []
    if preferred_driver:
        driver_codes.append(preferred_driver.upper())

    for driver_ref in getattr(session, "drivers", []):
        try:
            code = str(session.get_driver(driver_ref).get("Abbreviation")).upper()
        except Exception:
            code = str(driver_ref).upper()
        if code not in driver_codes:
            driver_codes.append(code)

    candidates: list[FastF1Candidate] = []
    for driver in driver_codes:
        laps = session.laps.pick_drivers(driver)
        for _, lap in laps.iterlaps():
            if not bool(lap.get("IsAccurate")):
                continue
            if str(lap.get("PitOutTime")) != "NaT" or str(lap.get("PitInTime")) != "NaT":
                continue

            position = lap.get_pos_data()[["X", "Y"]].dropna()
            if len(position) < 100:
                continue

            points: list[XY] = []
            for row in position.itertuples():
                point = (float(row.X), float(row.Y))
                if not points or point != points[-1]:
                    points.append(point)

            path_length_m = path_length(closed_path(points)) * 0.1
            # Cheap sanity guard before the expensive offset search. FastF1 X/Y
            # are documented as 1/10 m, so a representative lap should be near
            # the GPS track length.
            reference_length_m = path_length(reference_xy)
            if reference_length_m and abs(path_length_m - reference_length_m) / reference_length_m > 0.15:
                continue

            candidate = FastF1Lap(
                driver=driver,
                lap_number=int(lap.get("LapNumber")),
                points=points,
                path_length_m=path_length_m,
            )
            candidates.append(
                FastF1Candidate(
                    lap=candidate,
                    length_error_m=abs(path_length_m - reference_length_m),
                )
            )

        if preferred_driver and candidates:
            break

    if not candidates:
        raise RuntimeError(f"could not find a clean FastF1 lap for {year} {event}")

    scored: list[ScoredFastF1Lap] = []
    for candidate in sorted(candidates, key=lambda item: item.length_error_m)[:max_shape_candidates]:
        fit = validate_shape(
            fastf1_xy=candidate.lap.points,
            gps_xy=reference_xy,
            sample_count=validation_samples,
            offset_step=validation_offset_step,
        )
        scored.append(
            ScoredFastF1Lap(
                lap=candidate.lap,
                fit=fit,
                length_error_m=candidate.length_error_m,
            )
        )

    if not scored:
        raise RuntimeError(f"could not score FastF1 candidate laps for {year} {event}")

    return sorted(scored, key=lambda item: item.fit.rmse_m)


def load_fastf1_lap(
    year: int,
    event: str,
    cache_dir: Path,
    session_name: str,
    preferred_driver: str | None,
    reference_xy: list[XY],
    validation_samples: int,
    validation_offset_step: int,
    max_shape_candidates: int,
) -> tuple[FastF1Lap, FitStats]:
    """Compatibility wrapper for callers that only need the best lap."""

    best = score_fastf1_laps(
        year=year,
        event=event,
        cache_dir=cache_dir,
        session_name=session_name,
        preferred_driver=preferred_driver,
        reference_xy=reference_xy,
        validation_samples=validation_samples,
        validation_offset_step=validation_offset_step,
        max_shape_candidates=max_shape_candidates,
    )[0]
    return best.lap, best.fit


def flatten_for_csv(row: dict[str, Any]) -> dict[str, Any]:
    flattened: dict[str, Any] = {}
    for key, value in row.items():
        if isinstance(value, dict):
            for inner_key, inner_value in value.items():
                flattened[f"{key}_{inner_key}"] = inner_value
        elif key != "encoded_polyline":
            flattened[key] = value
    return flattened


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--year", type=int, default=2025)
    parser.add_argument("--session", default="R")
    parser.add_argument("--fastf1-cache-dir", type=Path, default=Path("data/fastf1-cache"))
    parser.add_argument("--circuits-repo", type=Path, default=Path("/tmp/f1-circuits"))
    parser.add_argument("--circuits-repo-url", default="https://github.com/bacinger/f1-circuits.git")
    parser.add_argument("--output-json", type=Path, default=Path("data/circuit-polylines-2025.json"))
    parser.add_argument("--output-csv", type=Path, default=Path("data/circuit-polylines-2025.csv"))
    parser.add_argument("--polyline-tolerance-m", type=float, default=1.0)
    parser.add_argument("--polyline-precision", type=int, default=5)
    parser.add_argument("--validation-samples", type=int, default=720)
    parser.add_argument("--validation-offset-step", type=int, default=4)
    parser.add_argument(
        "--max-shape-candidates",
        type=int,
        default=12,
        help="Only the N FastF1 laps with path length closest to GPS length get full shape fitting.",
    )
    parser.add_argument(
        "--average-laps",
        type=int,
        default=5,
        help="Average the best N fitted FastF1 laps into a comparison line.",
    )
    parser.add_argument(
        "--average-samples",
        type=int,
        default=720,
        help="Equal-progress samples used for the averaged FastF1 line.",
    )
    parser.add_argument("--preferred-driver", default=None)
    parser.add_argument("--limit", type=int, default=None, help="Debug only: process first N rounds")
    args = parser.parse_args()

    ensure_f1_circuits_repo(args.circuits_repo, args.circuits_repo_url)
    championship = load_json(args.circuits_repo / "championships" / f"f1-locations-{args.year}.json")

    try:
        import fastf1  # type: ignore[import-not-found]
    except ModuleNotFoundError as exc:
        raise RuntimeError("FastF1 is not installed") from exc

    fastf1.Cache.enable_cache(str(args.fastf1_cache_dir))
    schedule = fastf1.get_event_schedule(args.year, include_testing=False)

    results: list[dict[str, Any]] = []
    for round_index, circuit in enumerate(championship[: args.limit], start=1):
        event = schedule.iloc[round_index - 1]
        event_name = str(event["EventName"])
        circuit_id = str(circuit["id"])
        circuit_points, properties = load_circuit_latlon(args.circuits_repo, circuit_id)
        origin = projection_origin(circuit_points)
        gps_xy = [latlon_to_xy(point, origin) for point in circuit_points]

        scored_laps = score_fastf1_laps(
            year=args.year,
            event=event_name,
            cache_dir=args.fastf1_cache_dir,
            session_name=args.session,
            preferred_driver=args.preferred_driver,
            reference_xy=gps_xy,
            validation_samples=args.validation_samples,
            validation_offset_step=args.validation_offset_step,
            max_shape_candidates=args.max_shape_candidates,
        )
        best_scored_lap = scored_laps[0]
        fastf1_lap = best_scored_lap.lap
        fit = best_scored_lap.fit

        averaged_source_xy = average_fitted_laps(
            scored_laps[: args.average_laps],
            gps_xy=gps_xy,
            sample_count=args.average_samples,
        )
        averaged_simplified_xy = rdp(averaged_source_xy, args.polyline_tolerance_m)
        averaged_latlon = [xy_to_latlon(point, origin) for point in averaged_simplified_xy]
        averaged_encoded = encode_polyline(averaged_latlon, args.polyline_precision)
        averaged_simplification = simplification_stats(
            averaged_source_xy,
            averaged_simplified_xy,
            args.polyline_tolerance_m,
            averaged_encoded,
        )
        averaged_vs_repo = direct_line_stats(
            averaged_source_xy,
            gps_xy,
            sample_count=args.validation_samples,
        )

        simplified_xy = rdp(gps_xy, args.polyline_tolerance_m)
        simplified_latlon = [xy_to_latlon(point, origin) for point in simplified_xy]
        encoded = encode_polyline(simplified_latlon, args.polyline_precision)
        simp = simplification_stats(gps_xy, simplified_xy, args.polyline_tolerance_m, encoded)
        simplified_fit = validate_shape(
            fastf1_xy=fastf1_lap.points,
            gps_xy=simplified_xy,
            sample_count=args.validation_samples,
            offset_step=args.validation_offset_step,
        )

        row = {
            "round": round_index,
            "event_name": event_name,
            "fastf1_location": str(event["Location"]),
            "circuit_id": circuit_id,
            "circuit_name": circuit["name"],
            "repo_location": circuit["location"],
            "declared_length_m": properties.get("length"),
            "fastf1_driver": fastf1_lap.driver,
            "fastf1_lap": fastf1_lap.lap_number,
            "fastf1_points": len(fastf1_lap.points),
            "fastf1_path_length_m": fastf1_lap.path_length_m,
            "repo_vs_fastf1": asdict(fit),
            "averaged_fastf1_laps": [
                {
                    "driver": item.lap.driver,
                    "lap": item.lap.lap_number,
                    "path_length_m": item.lap.path_length_m,
                    "length_error_m": item.length_error_m,
                    "rmse_m": item.fit.rmse_m,
                    "p95_m": item.fit.p95_m,
                }
                for item in scored_laps[: args.average_laps]
            ],
            "repo_vs_fastf1_average": asdict(averaged_vs_repo),
            "averaged_fastf1_polyline_vs_source": asdict(averaged_simplification),
            "polyline_vs_source": asdict(simp),
            "polyline_vs_fastf1": asdict(simplified_fit),
            "encoded_polyline": encoded,
            "averaged_fastf1_encoded_polyline": averaged_encoded,
        }
        results.append(row)
        print(
            f"{round_index:02d} {event_name}: "
            f"best RMSE {fit.rmse_m:.1f} m, avg RMSE {averaged_vs_repo.rmse_m:.1f} m; "
            f"polyline {simp.simplified_points} pts, max simplification error {simp.max_m:.2f} m"
        )

    args.output_json.parent.mkdir(parents=True, exist_ok=True)
    args.output_json.write_text(json.dumps(results, indent=2), encoding="utf-8")

    csv_rows = [flatten_for_csv(row) for row in results]
    if csv_rows:
        with args.output_csv.open("w", encoding="utf-8", newline="") as handle:
            writer = csv.DictWriter(handle, fieldnames=list(csv_rows[0].keys()))
            writer.writeheader()
            writer.writerows(csv_rows)

    print()
    print(f"Wrote {args.output_json}")
    print(f"Wrote {args.output_csv}")


if __name__ == "__main__":
    main()
