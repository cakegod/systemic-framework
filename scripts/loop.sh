#!/usr/bin/env bash
# Local verification loop.
#
# Fast path (inner loop): builds only the test project and its dependencies with analyzers
# off, then runs one test class without rebuilding. Roughly 10s cold, 2s warm.
#
#   scripts/loop.sh tests/Systemic.Integration.Tests QueryConditionTests
#
# Full gate (before a commit): whole-solution build with analyzers, every test, formatting.
#
#   scripts/loop.sh --full
set -euo pipefail

project="${1:-tests/Systemic.Integration.Tests}"

if [ "$project" = "--full" ]; then
    dotnet build Systemic.sln --configuration Release
    dotnet test Systemic.sln --configuration Release --no-build
    dotnet format Systemic.sln --verify-no-changes
    exit
fi

filter="${2:-}"
dotnet build "$project" --configuration Release --no-restore -p:RunAnalyzers=false
if [ -n "$filter" ]; then
    dotnet test "$project" --configuration Release --no-build --filter "FullyQualifiedName~$filter"
else
    dotnet test "$project" --configuration Release --no-build
fi
