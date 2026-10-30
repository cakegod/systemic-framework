#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$root"

dotnet build Systemic.sln --configuration Release

scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT

cli=(dotnet run --no-build --project src/tools/Systemic.Cli --configuration Release --)
game="$scratch/bootstrap-game"

"${cli[@]}" new "$game"
"${cli[@]}" validate "$game"
"${cli[@]}" run "$game" --ticks 600
"${cli[@]}" verify "$game" --ticks 600
"${cli[@]}" simulate "$game" --seconds 10 --csv "$scratch/simulate.csv"

echo "Bootstrap OK: $game"
