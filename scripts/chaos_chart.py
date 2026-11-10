#!/usr/bin/env python3
"""Draws the chaos-run chart from a results file and its timeline, in light and dark versions.

Usage: python3 scripts/chaos_chart.py [docs/results/chaos-latest.json]
Writes chaos-chart-light.svg and chaos-chart-dark.svg next to the results file. No dependencies.
"""
import json
import sys
from pathlib import Path

THEMES = {
    "light": dict(surface="#fcfcfb", ink="#0b0b0b", ink2="#52514e", muted="#898781",
                  grid="#e1e0d9", axis="#c3c2b7", delivered="#2a78d6", waiting="#eb6834"),
    "dark": dict(surface="#1a1a19", ink="#f0efec", ink2="#c3c2b7", muted="#898781",
                 grid="#2c2c2a", axis="#383835", delivered="#3987e5", waiting="#d95926"),
}

W, H = 820, 400
LEFT, RIGHT, TOP, BOTTOM = 64, 128, 132, 52
FONT = 'system-ui, -apple-system, "Segoe UI", sans-serif'


def nice_step(span, target):
    raw = span / target
    for step in (1, 2, 2.5, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000):
        if step >= raw:
            return step
    return raw


def compact(n):
    if n >= 10_000:
        return f"{n / 1000:g}K"
    return f"{n:,.0f}"


def draw(result, timeline, t):
    samples = timeline["samples"]
    kills = timeline["kills"]
    x_max = max(result["totalSeconds"], samples[-1]["t"])
    y_max = result["events"]

    pw, ph = W - LEFT - RIGHT, H - TOP - BOTTOM

    def x(sec):
        return LEFT + sec / x_max * pw

    def y(val):
        return TOP + ph - val / y_max * ph

    out = []
    add = out.append
    add(f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}" '
        f'font-family=\'{FONT}\' role="img" aria-labelledby="title desc">')
    title = (f'{result["events"]:,} webhooks, {result["workerKills"]} worker kills, '
             f'{result["lost"]} lost')
    desc = (f'Deliveries completed and deliveries waiting over {result["totalSeconds"]:g} seconds, '
            f'while {result["workers"]} workers were killed with SIGKILL {result["workerKills"]} times. '
            f'All {result["delivered"]:,} events were delivered, {result["duplicates"]} were delivered twice.')
    add(f'<title id="title">{title}</title><desc id="desc">{desc}</desc>')
    add(f'<rect width="{W}" height="{H}" rx="8" fill="{t["surface"]}"/>')

    # Title, subtitle, legend.
    add(f'<text x="{LEFT}" y="34" font-size="17" font-weight="600" fill="{t["ink"]}">{title}</text>')
    sub = (f'{result["workers"]} workers, one SIGKILL every 2 s, consumer failing 20% of requests. '
           f'{result["duplicates"]} duplicates, {result["deadLettered"]} dead letters.')
    add(f'<text x="{LEFT}" y="56" font-size="13" fill="{t["ink2"]}">{sub}</text>')
    lx = LEFT
    for label, color in (("Delivered", t["delivered"]), ("Waiting (pending or leased)", t["waiting"])):
        add(f'<line x1="{lx}" y1="80" x2="{lx + 16}" y2="80" stroke="{color}" stroke-width="2" stroke-linecap="round"/>')
        add(f'<text x="{lx + 22}" y="84" font-size="12" fill="{t["ink2"]}">{label}</text>')
        lx += 22 + len(label) * 6.6 + 24

    # Kill strip: one tick per SIGKILL, above the plot.
    strip_y = 108
    add(f'<text x="{LEFT - 8}" y="{strip_y + 4}" font-size="11" fill="{t["muted"]}" text-anchor="end">kills</text>')
    add(f'<line x1="{LEFT}" y1="{strip_y}" x2="{LEFT + pw}" y2="{strip_y}" stroke="{t["grid"]}" stroke-width="1"/>')
    for k in kills:
        add(f'<line x1="{x(k):.1f}" y1="{strip_y - 6}" x2="{x(k):.1f}" y2="{strip_y + 6}" '
            f'stroke="{t["ink2"]}" stroke-width="1.5" stroke-linecap="round"/>')

    # Gridlines and y ticks.
    y_step = nice_step(y_max, 5)
    v = 0
    while v <= y_max + 1e-9:
        add(f'<line x1="{LEFT}" y1="{y(v):.1f}" x2="{LEFT + pw}" y2="{y(v):.1f}" '
            f'stroke="{t["axis"] if v == 0 else t["grid"]}" stroke-width="1"/>')
        add(f'<text x="{LEFT - 8}" y="{y(v) + 4:.1f}" font-size="11" fill="{t["muted"]}" '
            f'text-anchor="end" style="font-variant-numeric: tabular-nums">{compact(v)}</text>')
        v += y_step

    # X ticks.
    x_step = nice_step(x_max, 6)
    s = 0
    while s <= x_max + 1e-9:
        add(f'<text x="{x(s):.1f}" y="{TOP + ph + 18}" font-size="11" fill="{t["muted"]}" '
            f'text-anchor="middle" style="font-variant-numeric: tabular-nums">{s:g}s</text>')
        s += x_step
    add(f'<text x="{LEFT + pw / 2}" y="{H - 12}" font-size="11" fill="{t["muted"]}" '
        f'text-anchor="middle">seconds since the first event was sent</text>')

    # Series.
    def path(values):
        return " ".join(f'{"M" if i == 0 else "L"}{x(sm["t"]):.1f},{y(val):.1f}'
                        for i, (sm, val) in enumerate(zip(samples, values)))

    waiting = [sm["pending"] + sm["leased"] for sm in samples]
    delivered = [sm["delivered"] for sm in samples]
    for values, color in ((waiting, t["waiting"]), (delivered, t["delivered"])):
        d = path(values)
        area = f'{d} L{x(samples[-1]["t"]):.1f},{y(0):.1f} L{x(samples[0]["t"]):.1f},{y(0):.1f} Z'
        add(f'<path d="{area}" fill="{color}" fill-opacity="0.1"/>')
        add(f'<path d="{d}" fill="none" stroke="{color}" stroke-width="2" stroke-linejoin="round" stroke-linecap="round"/>')

    # Direct labels: delivered at its end, waiting at its peak (set below the line, which falls to the right).
    end_x, end_y = x(samples[-1]["t"]), y(delivered[-1])
    add(f'<circle cx="{end_x:.1f}" cy="{end_y:.1f}" r="4" fill="{t["delivered"]}" stroke="{t["surface"]}" stroke-width="2"/>')
    add(f'<text x="{end_x + 10:.1f}" y="{end_y - 2:.1f}" font-size="12" font-weight="600" fill="{t["ink"]}">{delivered[-1]:,}</text>')
    add(f'<text x="{end_x + 10:.1f}" y="{end_y + 13:.1f}" font-size="11" fill="{t["ink2"]}">delivered</text>')
    peak = max(range(len(waiting)), key=waiting.__getitem__)
    px, py = x(samples[peak]["t"]), y(waiting[peak])
    add(f'<circle cx="{px:.1f}" cy="{py:.1f}" r="4" fill="{t["waiting"]}" stroke="{t["surface"]}" stroke-width="2"/>')
    add(f'<text x="{px + 10:.1f}" y="{py + 60:.1f}" font-size="12" font-weight="600" fill="{t["ink"]}">{waiting[peak]:,} waiting</text>')
    add(f'<text x="{px + 10:.1f}" y="{py + 75:.1f}" font-size="11" fill="{t["ink2"]}">at the peak, {samples[peak]["t"]:g}s in</text>')

    add("</svg>")
    return "\n".join(out) + "\n"


def main():
    results_path = Path(sys.argv[1] if len(sys.argv) > 1 else "docs/results/chaos-latest.json")
    result = json.loads(results_path.read_text())
    timeline = json.loads(results_path.with_name(results_path.stem + "-timeline.json").read_text())
    if timeline["runId"] != result["runId"]:
        sys.exit("The timeline and the results come from different runs.")
    for name, theme in THEMES.items():
        target = results_path.with_name(f"chaos-chart-{name}.svg")
        target.write_text(draw(result, timeline, theme))
        print(f"Wrote {target}")


if __name__ == "__main__":
    main()
