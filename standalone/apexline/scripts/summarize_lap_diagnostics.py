#!/usr/bin/env python3
"""Summarize Apexline lap diagnostics into markdown and SVG artifacts."""

from __future__ import annotations

import argparse
import json
from collections import Counter
from pathlib import Path
from typing import Any
from xml.sax.saxutils import escape


PROJECT_DIR = Path(__file__).resolve().parent.parent


def pct(part: int, total: int) -> float:
    return part / total * 100 if total else 0.0


def fmt_pct(value: float) -> str:
    return f"{value:.1f}%"


def load_rows(path: Path) -> list[dict[str, Any]]:
    with path.open(encoding="utf-8") as handle:
        data = json.load(handle)
    if not isinstance(data, list):
        raise ValueError(f"{path} should contain a list of circuit diagnostics")
    return data


def rejection_signals(reason_counts: dict[str, int]) -> str:
    reasons = [
        (reason, count)
        for reason, count in reason_counts.items()
        if reason != "compliant" and count
    ]
    reasons.sort(key=lambda item: item[1], reverse=True)
    return ", ".join(f"`{reason}` {count}" for reason, count in reasons[:3]) or "--"


def build_markdown(rows: list[dict[str, Any]], year: int) -> str:
    total_laps = sum(row["total_laps"] for row in rows)
    good_laps = sum(row["compliant_laps"] for row in rows)
    bad_laps = sum(row["non_compliant_laps"] for row in rows)
    fitted_laps = sum(row["fitted_laps"] for row in rows)
    shape_bad = sum(row["shape_non_compliant_laps"] for row in rows)

    reasons = Counter()
    for row in rows:
        reasons.update(row["reason_counts"])

    lines = [
        f"# {year} lap compliance summary",
        "",
        "Good laps are laps that pass Apexline's geometry-reference checks.",
        "Bad laps are laps rejected for at least one reason such as FastF1",
        "`IsAccurate == False`, pit in/out, missing time, too few position",
        "samples, path-length outlier, or shape residual over threshold.",
        "",
        "Reason counts are not mutually exclusive: one bad lap can be both a",
        "pit lap and FastF1-inaccurate.",
        "",
        "## Season totals",
        "",
        "| Metric | Value |",
        "|---|---:|",
        f"| Circuits | {len(rows)} |",
        f"| Total laps inspected | {total_laps:,} |",
        f"| Fitted laps | {fitted_laps:,} |",
        f"| Good laps | {good_laps:,} ({fmt_pct(pct(good_laps, total_laps))}) |",
        f"| Bad laps | {bad_laps:,} ({fmt_pct(pct(bad_laps, total_laps))}) |",
        f"| Shape-threshold bad laps | {shape_bad:,} |",
        "",
        "## Rejection signals",
        "",
        "| Reason | Count |",
        "|---|---:|",
    ]
    for reason, count in sorted(reasons.items(), key=lambda item: item[1], reverse=True):
        if reason == "compliant":
            continue
        lines.append(f"| `{reason}` | {count:,} |")

    lines.extend(
        [
            "",
            "## Circuit table",
            "",
            "| Round | Event | Good | Bad | Total | Good % | Shape Bad | Top rejection signals |",
            "|---:|---|---:|---:|---:|---:|---:|---|",
        ]
    )
    for row in sorted(rows, key=lambda item: item["round"]):
        total = row["total_laps"]
        good = row["compliant_laps"]
        bad = row["non_compliant_laps"]
        lines.append(
            "| {round} | {event} | {good:,} | {bad:,} | {total:,} | {good_pct} | {shape_bad:,} | {signals} |".format(
                round=row["round"],
                event=row["event_name"],
                good=good,
                bad=bad,
                total=total,
                good_pct=fmt_pct(pct(good, total)),
                shape_bad=row["shape_non_compliant_laps"],
                signals=rejection_signals(row["reason_counts"]),
            )
        )

    return "\n".join(lines) + "\n"


def text_svg(x: float, y: float, text: str, *, size: int = 13, weight: int = 400, fill: str = "#111827") -> str:
    return (
        f'<text x="{x:.1f}" y="{y:.1f}" font-family="Inter, Arial, sans-serif" '
        f'font-size="{size}" font-weight="{weight}" fill="{fill}">{escape(text)}</text>'
    )


def build_svg(rows: list[dict[str, Any]], year: int) -> str:
    rows = sorted(rows, key=lambda item: item["round"])
    total_laps = sum(row["total_laps"] for row in rows)
    good_laps = sum(row["compliant_laps"] for row in rows)
    bad_laps = sum(row["non_compliant_laps"] for row in rows)

    width = 1300
    row_h = 30
    header_h = 112
    bottom_h = 44
    height = header_h + len(rows) * row_h + bottom_h
    label_x = 34
    bar_x = 365
    bar_w = 720
    pct_x = 1110

    parts = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">',
        '<rect width="100%" height="100%" fill="#f8fafc"/>',
        text_svg(34, 42, f"{year} FastF1 lap compliance by circuit", size=24, weight=700),
        text_svg(
            34,
            68,
            f"{good_laps:,} good laps ({fmt_pct(pct(good_laps, total_laps))}) vs {bad_laps:,} bad laps ({fmt_pct(pct(bad_laps, total_laps))}) across {total_laps:,} inspected laps.",
            size=14,
            fill="#475569",
        ),
        '<line x1="34" y1="92" x2="1264" y2="92" stroke="#cbd5e1"/>',
        '<rect x="365" y="80" width="18" height="8" fill="#059669"/>',
        text_svg(390, 88, "good", size=12, fill="#475569"),
        '<rect x="452" y="80" width="18" height="8" fill="#dc2626"/>',
        text_svg(477, 88, "bad", size=12, fill="#475569"),
    ]

    for index, row in enumerate(rows):
        y = header_h + index * row_h
        total = row["total_laps"]
        good = row["compliant_laps"]
        bad = row["non_compliant_laps"]
        good_w = bar_w * good / total if total else 0
        bad_w = bar_w - good_w
        label = f"{row['round']:02d} {row['event_name']}"
        parts.extend(
            [
                text_svg(label_x, y + 18, label, size=12, fill="#334155"),
                f'<rect x="{bar_x}" y="{y + 6}" width="{bar_w}" height="16" rx="3" fill="#e2e8f0"/>',
                f'<rect x="{bar_x}" y="{y + 6}" width="{good_w:.1f}" height="16" rx="3" fill="#059669"/>',
                f'<rect x="{bar_x + good_w:.1f}" y="{y + 6}" width="{bad_w:.1f}" height="16" rx="3" fill="#dc2626"/>',
                text_svg(pct_x, y + 18, f"{fmt_pct(pct(good, total))} good", size=12, fill="#334155"),
                text_svg(pct_x + 92, y + 18, f"{bad:,} bad", size=12, fill="#64748b"),
            ]
        )

    parts.append("</svg>")
    return "\n".join(parts)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--year", type=int, default=2025)
    parser.add_argument("--diagnostics-json", type=Path, default=None)
    parser.add_argument("--output-md", type=Path, default=None)
    parser.add_argument("--output-svg", type=Path, default=None)
    args = parser.parse_args()

    diagnostics_json = args.diagnostics_json or PROJECT_DIR / "data" / f"lap-diagnostics-{args.year}.json"
    output_md = args.output_md or PROJECT_DIR / "docs" / f"lap-compliance-{args.year}.md"
    output_svg = args.output_svg or PROJECT_DIR / "docs" / "assets" / f"lap-compliance-{args.year}.svg"

    rows = load_rows(diagnostics_json)
    output_md.parent.mkdir(parents=True, exist_ok=True)
    output_svg.parent.mkdir(parents=True, exist_ok=True)
    output_md.write_text(build_markdown(rows, args.year), encoding="utf-8")
    output_svg.write_text(build_svg(rows, args.year), encoding="utf-8")

    total = sum(row["total_laps"] for row in rows)
    good = sum(row["compliant_laps"] for row in rows)
    bad = sum(row["non_compliant_laps"] for row in rows)
    print(f"{args.year}: {good:,} good / {bad:,} bad / {total:,} total")
    print(f"Wrote {output_md}")
    print(f"Wrote {output_svg}")


if __name__ == "__main__":
    main()
