#!/usr/bin/env bash
# Runs the chaos test end to end: start the stack, send events while killing workers, report, clean up.
# Usage: ./scripts/chaos.sh [events] [results-file]
set -euo pipefail
cd "$(dirname "$0")/.."

events="${1:-10000}"
out="${2:-docs/results/chaos-latest.json}"
compose=(docker compose -f compose.yaml -f compose.chaos.yaml)

"${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
"${compose[@]}" up --build --detach --wait --wait-timeout 300

status=0
dotnet run --project tools/WebhookInbox.Chaos -c Release -- --events "$events" --out "$out" || status=$?

"${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
exit "$status"
