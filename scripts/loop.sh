#!/usr/bin/env bash
# Local verification loop.
#
# Fast path (inner loop): builds only the test project and its dependencies with analyzers
# off, then runs one test class without rebuilding. Measured here at 2.6s build and 2.6s test
# when the tree is warm. Formatting is deliberately not part of this path: a scoped format
# verify costs about 16s and a solution-wide one about 78s, which is 75 percent of an iteration.
#
#   scripts/loop.sh tests/Systemic.Integration.Tests QueryConditionTests
#
# Format check only, scoped to the files this branch changes (opt in when you want it):
#
#   scripts/loop.sh --format [project]
#
# Full gate (before a commit): whole-solution build with analyzers, every test, formatting.
#
#   scripts/loop.sh --full
set -euo pipefail

project="${1:-tests/Systemic.Integration.Tests}"

if [ "$project" = "--format" ]; then
    target="${2:-tests/Systemic.Integration.Tests}"
    mapfile -t changed < <(git diff --name-only --diff-filter=ACM origin/main -- '*.cs')
    if [ "${#changed[@]}" -eq 0 ]; then
        echo 'loop: no changed C# files to format'
        exit 0
    fi

    dotnet format "$target" --verify-no-changes --no-restore --include "${changed[@]}"
    exit
fi

if [ "$project" = "--full" ]; then
    dotnet build Systemic.sln --configuration Release
    dotnet test Systemic.sln --configuration Release --no-build
    # --no-restore: the build above restored already, and format's own restore is most of the wait.
    dotnet format Systemic.sln --verify-no-changes --no-restore
    exit
fi

filter="${2:-}"
dotnet build "$project" --configuration Release --no-restore -p:RunAnalyzers=false
if [ -n "$filter" ]; then
    dotnet test "$project" --configuration Release --no-build --filter "FullyQualifiedName~$filter"
else
    dotnet test "$project" --configuration Release --no-build
fi
