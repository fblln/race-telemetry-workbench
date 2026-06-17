#!/usr/bin/env python3
"""Generate real Canada 2025 lap-diagnostic SVGs from FastF1 positions.

The script intentionally emits SVG directly instead of relying on a plotting
library. That keeps the diagnostic figures reproducible in sandboxes and CI.
"""

from __future__ import annotations

import argparse
import math
import sys
from pathlib import Path
from typing import Any
from xml.sax.saxutils import escape


SCRIPT_DIR = Path(__file__).resolve().parent
PROJECT_DIR = SCRIPT_DIR.parent
sys.path.insert(0, str(SCRIPT_DIR))

import analyze_f1_circuit_gps as analyzer  # noqa: E402


XY = tuple[float, float]


def load_session(year: int, event: str, cache_dir: Path) -> Any:
    import fastf1  # type: ignore[import-not-found]

    cache_dir.mkdir(parents=True, exist_ok=True)
    fastf1.Cache.enable_cache(str(cache_dir))
    session = fastf1.get_session(year, event, "R")
    session.load(laps=True, telemetry=True, weather=False, messages=False)
    return session


def find_lap(session: Any, driver: str, lap_number: int) -> Any:
    for _, lap in session.laps.pick_drivers(driver).iterlaps():
        if int(lap.get("LapNumber")) == lap_number:
            return lap
    raise ValueError(f"Could not find {driver} lap {lap_number}")


def lap_transform(points: list[XY], gps_xy: list[XY], sample_count: int = 720) -> tuple[complex, complex, analyzer.FitStats]:
    """Learn one similarity transform from a clean reference lap."""

    fit = analyzer.validate_shape(
        fastf1_xy=points,
        gps_xy=gps_xy,
        sample_count=sample_count,
        offset_step=1,
    )
    source = analyzer.resample_closed(points, sample_count)
    target = analyzer.resample_closed(gps_xy, sample_count)
    target_for_fit = list(reversed(target)) if fit.direction == "reversed" else target
    target_for_fit = analyzer.rotate_samples(target_for_fit, fit.start_offset_samples)

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
    return c, translation, fit


def apply_transform(points: list[XY], scale_rotate: complex, translation: complex) -> list[XY]:
    raw_complex = [complex(x, y) for x, y in points]
    transformed = [scale_rotate * point + translation for point in raw_complex]
    return [(point.real, point.imag) for point in transformed]


def format_ms(ms: int | None) -> str:
    if ms is None:
        return "missing"
    return f"{ms / 1000:.3f}s"


def bbox(points: list[XY]) -> tuple[float, float, float, float]:
    xs = [point[0] for point in points]
    ys = [point[1] for point in points]
    return min(xs), min(ys), max(xs), max(ys)


def merged_bbox(lines: list[list[XY]]) -> tuple[float, float, float, float]:
    boxes = [bbox(line) for line in lines]
    return (
        min(box[0] for box in boxes),
        min(box[1] for box in boxes),
        max(box[2] for box in boxes),
        max(box[3] for box in boxes),
    )


def project(
    point: XY,
    box: tuple[float, float, float, float],
    panel_x: float,
    panel_y: float,
    panel_w: float,
    panel_h: float,
    pad: float,
) -> XY:
    min_x, min_y, max_x, max_y = box
    width = max(max_x - min_x, 1.0)
    height = max(max_y - min_y, 1.0)
    scale = min((panel_w - pad * 2) / width, (panel_h - pad * 2) / height)
    used_w = width * scale
    used_h = height * scale
    offset_x = panel_x + (panel_w - used_w) / 2
    offset_y = panel_y + (panel_h - used_h) / 2
    x = offset_x + (point[0] - min_x) * scale
    y = offset_y + used_h - (point[1] - min_y) * scale
    return x, y


def polyline_svg(
    points: list[XY],
    box: tuple[float, float, float, float],
    panel_x: float,
    panel_y: float,
    panel_w: float,
    panel_h: float,
    *,
    stroke: str,
    width: float,
    opacity: float = 1.0,
) -> str:
    projected = [project(point, box, panel_x, panel_y, panel_w, panel_h, 18) for point in points]
    coords = " ".join(f"{x:.1f},{y:.1f}" for x, y in projected)
    return (
        f'<polyline points="{coords}" fill="none" stroke="{stroke}" '
        f'stroke-width="{width}" stroke-linejoin="round" stroke-linecap="round" opacity="{opacity}"/>'
    )


def text_svg(x: float, y: float, text: str, *, size: int = 14, weight: int = 400, fill: str = "#111827") -> str:
    return (
        f'<text x="{x:.1f}" y="{y:.1f}" font-family="Inter, Arial, sans-serif" '
        f'font-size="{size}" font-weight="{weight}" fill="{fill}">{escape(text)}</text>'
    )


def draw_lap_examples(
    *,
    session: Any,
    gps_xy: list[XY],
    output: Path,
) -> None:
    reference_length = analyzer.path_length(gps_xy)
    examples = [
        {
            "title": "Fastest valid lap",
            "subtitle": "RUS lap 63, race fastest lap",
            "driver": "RUS",
            "lap": 63,
            "reason": "compliant",
            "color": "#059669",
            "show_fit": True,
        },
        {
            "title": "Path-length outlier",
            "subtitle": "VER lap 2, projected with clean-lap transform",
            "driver": "VER",
            "lap": 2,
            "reason": "path_length_outlier",
            "color": "#dc2626",
            "show_fit": False,
        },
        {
            "title": "Pit/inaccurate lap",
            "subtitle": "RUS lap 13, projected with clean-lap transform",
            "driver": "RUS",
            "lap": 13,
            "reason": "pit_lap + fastf1_inaccurate",
            "color": "#f97316",
            "show_fit": False,
        },
    ]
    anchor_lap = find_lap(session, "RUS", 63)
    anchor_points = analyzer.lap_position_points(anchor_lap)
    anchor_scale_rotate, anchor_translation, anchor_fit = lap_transform(anchor_points, gps_xy)

    width = 1500
    height = 650
    panel_w = 448
    panel_h = 500
    panel_y = 92
    gap = 26
    left = 26
    parts = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">',
        '<rect width="100%" height="100%" fill="#f8fafc"/>',
        text_svg(28, 42, "Canada 2025: real FastF1 position traces against f1-circuits GPS", size=24, weight=700),
        text_svg(28, 68, "Gray is the repository GPS outline. Color is FastF1 projected with one transform learned from clean RUS lap 63.", size=14, fill="#475569"),
    ]

    for index, example in enumerate(examples):
        panel_x = left + index * (panel_w + gap)
        lap = find_lap(session, example["driver"], example["lap"])
        points = analyzer.lap_position_points(lap)
        transformed = apply_transform(points, anchor_scale_rotate, anchor_translation)
        path_length_m = analyzer.path_length(analyzer.closed_path(points)) * 0.1
        length_error_m = path_length_m - reference_length
        length_error_pct = length_error_m / reference_length * 100
        lap_time_ms = analyzer.value_to_ms(lap.get("LapTime"))
        box = merged_bbox([gps_xy, transformed])
        fit_text = (
            f"fit: RMSE {anchor_fit.rmse_m:.1f} m, p95 {anchor_fit.p95_m:.1f} m"
            if example["show_fit"]
            else "shape fit: skipped after pre-fit rejection"
        )

        parts.extend(
            [
                f'<rect x="{panel_x}" y="{panel_y}" width="{panel_w}" height="{panel_h}" rx="10" fill="#ffffff" stroke="#cbd5e1"/>',
                text_svg(panel_x + 20, panel_y + 34, example["title"], size=17, weight=700),
                text_svg(panel_x + 20, panel_y + 56, example["subtitle"], size=13, fill="#475569"),
                polyline_svg(gps_xy, box, panel_x + 18, panel_y + 80, panel_w - 36, 280, stroke="#94a3b8", width=3.0, opacity=0.85),
                polyline_svg(transformed, box, panel_x + 18, panel_y + 80, panel_w - 36, 280, stroke=example["color"], width=3.2, opacity=0.95),
            ]
        )

        start = project(transformed[0], box, panel_x + 18, panel_y + 80, panel_w - 36, 280, 18)
        end = project(transformed[-1], box, panel_x + 18, panel_y + 80, panel_w - 36, 280, 18)
        parts.extend(
            [
                f'<circle cx="{start[0]:.1f}" cy="{start[1]:.1f}" r="4.5" fill="#111827"/>',
                f'<circle cx="{end[0]:.1f}" cy="{end[1]:.1f}" r="4.5" fill="#64748b"/>',
                text_svg(panel_x + 20, panel_y + 392, f"time: {format_ms(lap_time_ms)}", size=13),
                text_svg(panel_x + 20, panel_y + 416, f"length: {path_length_m:.1f} m ({length_error_pct:+.1f}%)", size=13),
                text_svg(panel_x + 20, panel_y + 440, fit_text, size=13),
                text_svg(panel_x + 20, panel_y + 464, f"reason: {example['reason']}", size=13, fill=example["color"]),
            ]
        )

    parts.extend(
        [
            '<line x1="586" y1="622" x2="636" y2="622" stroke="#94a3b8" stroke-width="4" stroke-linecap="round"/>',
            text_svg(646, 627, "repo GPS", size=13, fill="#475569"),
            '<line x1="742" y1="622" x2="792" y2="622" stroke="#059669" stroke-width="4" stroke-linecap="round"/>',
            text_svg(802, 627, "FastF1 trace, anchor-projected", size=13, fill="#475569"),
            "</svg>",
        ]
    )

    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text("\n".join(parts), encoding="utf-8")


def draw_offset_refinement(
    *,
    session: Any,
    gps_xy: list[XY],
    output: Path,
) -> None:
    lap = find_lap(session, "RUS", 63)
    points = analyzer.lap_position_points(lap)
    source = analyzer.resample_closed(points, 240)
    target = analyzer.resample_closed(gps_xy, 240)
    offsets = list(range(4, 21))
    rmses = [
        analyzer.similarity_fit(source, analyzer.rotate_samples(target, offset)).rmse_m
        for offset in offsets
    ]
    best_index = min(range(len(offsets)), key=lambda index: rmses[index])
    max_rmse = max(rmses)

    width = 980
    height = 520
    chart_x = 76
    chart_y = 82
    chart_w = 850
    chart_h = 330
    baseline = chart_y + chart_h
    bar_gap = 8
    bar_w = (chart_w - bar_gap * (len(offsets) - 1)) / len(offsets)

    parts = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">',
        '<rect width="100%" height="100%" fill="#f8fafc"/>',
        text_svg(34, 42, "Why local offset refinement matters", size=24, weight=700),
        text_svg(34, 68, "Russell lap 63 was falsely rejected when the coarse search skipped the correct phase.", size=14, fill="#475569"),
        f'<rect x="{chart_x}" y="{chart_y}" width="{chart_w}" height="{chart_h}" fill="#ffffff" stroke="#cbd5e1"/>',
    ]

    for tick in range(0, int(math.ceil(max_rmse / 50)) * 50 + 1, 50):
        y = baseline - (tick / max_rmse) * chart_h
        parts.append(f'<line x1="{chart_x}" y1="{y:.1f}" x2="{chart_x + chart_w}" y2="{y:.1f}" stroke="#e2e8f0"/>')
        parts.append(text_svg(chart_x - 44, y + 4, str(tick), size=11, fill="#64748b"))

    threshold_y = baseline - (25 / max_rmse) * chart_h
    parts.append(f'<line x1="{chart_x}" y1="{threshold_y:.1f}" x2="{chart_x + chart_w}" y2="{threshold_y:.1f}" stroke="#f97316" stroke-width="2" stroke-dasharray="6 5"/>')
    parts.append(text_svg(chart_x + chart_w - 118, threshold_y - 8, "25 m threshold", size=12, fill="#c2410c"))

    for index, (offset, rmse) in enumerate(zip(offsets, rmses)):
        x = chart_x + index * (bar_w + bar_gap)
        bar_h = (rmse / max_rmse) * chart_h
        y = baseline - bar_h
        color = "#2563eb" if index == best_index else "#94a3b8"
        parts.append(f'<rect x="{x:.1f}" y="{y:.1f}" width="{bar_w:.1f}" height="{bar_h:.1f}" fill="{color}"/>')
        parts.append(text_svg(x + bar_w / 2 - 6, baseline + 22, str(offset), size=11, fill="#475569"))
        if offset in (8, 12, 16):
            parts.append(text_svg(x + bar_w / 2 - 18, y - 8, f"{rmse:.1f}m", size=11, fill="#334155" if offset != 12 else "#1d4ed8"))

    parts.extend(
        [
            text_svg(chart_x + chart_w / 2 - 130, 470, "Start offset at 240 resampled points", size=13, fill="#334155"),
            text_svg(18, 260, "RMSE (m)", size=13, fill="#334155"),
            text_svg(590, 456, "Offsets 8 and 16 looked bad; offset 12 is the correct phase.", size=13, fill="#475569"),
            "</svg>",
        ]
    )

    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text("\n".join(parts), encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--year", type=int, default=2025)
    parser.add_argument("--event", default="Canadian Grand Prix")
    parser.add_argument("--circuit-id", default="ca-1978")
    parser.add_argument("--fastf1-cache-dir", type=Path, default=PROJECT_DIR / "data" / "fastf1-cache")
    parser.add_argument("--circuits-repo", type=Path, default=Path("/tmp/f1-circuits"))
    parser.add_argument("--circuits-repo-url", default="https://github.com/bacinger/f1-circuits.git")
    parser.add_argument("--output-dir", type=Path, default=PROJECT_DIR / "docs" / "assets")
    args = parser.parse_args()

    analyzer.ensure_f1_circuits_repo(args.circuits_repo, args.circuits_repo_url)
    circuit_points, _ = analyzer.load_circuit_latlon(args.circuits_repo, args.circuit_id)
    origin = analyzer.projection_origin(circuit_points)
    gps_xy = [analyzer.latlon_to_xy(point, origin) for point in circuit_points]
    session = load_session(args.year, args.event, args.fastf1_cache_dir)

    draw_lap_examples(
        session=session,
        gps_xy=gps_xy,
        output=args.output_dir / "canada-2025-lap-diagnostic-overlays.svg",
    )
    draw_offset_refinement(
        session=session,
        gps_xy=gps_xy,
        output=args.output_dir / "canada-2025-offset-refinement.svg",
    )

    print(f"Wrote {args.output_dir / 'canada-2025-lap-diagnostic-overlays.svg'}")
    print(f"Wrote {args.output_dir / 'canada-2025-offset-refinement.svg'}")


if __name__ == "__main__":
    main()
