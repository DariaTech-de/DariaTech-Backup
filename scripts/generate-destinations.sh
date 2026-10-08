#!/usr/bin/env bash
# Regenerates DariaTech/Contracts/Destinations.json from the OSS engine's backend modules.
# --check fails when the committed catalog no longer matches the engine (run after engine updates).
set -euo pipefail
cd "$(dirname "$0")/.."
target=DariaTech/Contracts/Destinations.json
out=$target
[[ ${1:-} == --check ]] && out=$(mktemp)
dotnet run --project DariaTech/CatalogGen -c Release -p:DariaTechOssOnly=true -- "$out" >/dev/null
if [[ ${1:-} == --check ]]; then
  cmp -s "$out" "$target" || { echo "Destination catalog drift: run scripts/generate-destinations.sh" >&2; rm -f "$out"; exit 1; }
  rm -f "$out"; echo "Destination catalog up to date."
else
  echo "Destination catalog written to $target."
fi
