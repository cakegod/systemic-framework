#!/usr/bin/env bash
# Local verification loop.
#
# Fast path (inner loop): builds only the test project and its dependencies with analyzers
# off, then runs one test class without rebuilding. Measured at 2.6s build and 2.6s test warm.
#
#   scripts/loop.sh tests/Systemic.Integration.Tests QueryConditionTests
#
# Full gate (before a commit): every framework suite, built and tested in one pass.
#
#   scripts/loop.sh --full
#
# Formatting, on its own. Not part of the gate: a solution-wide check loads every project and costs
# about 78 seconds, and this branch touches seven projects, so a per-project check at about 16
# seconds each would cost more than the load it avoids.
#
#   scripts/loop.sh --format
#
# The three sample suites drive the playable games and cost about two minutes together, so the gate
# does not run them:
#
#   scripts/loop.sh --samples
set -euo pipefail

phase() { echo "loop: ${SECONDS:-0}s $*"; }

format_changed() {
    local -a changed=()
    mapfile -t changed < <(git diff --name-only --diff-filter=ACM origin/main -- '*.cs')
    if [ "${#changed[@]}" -eq 0 ]; then
        echo 'loop: no changed C# files to format'
        return 0
    fi

    local -A by_project=()
    local file directory candidate project
    for file in "${changed[@]}"; do
        directory=$(dirname "$file")
        project=''
        while [ "$directory" != '.' ]; do
            for candidate in "$directory"/*.csproj; do
                if [ -f "$candidate" ]; then
                    project="$candidate"
                    break
                fi
            done
            if [ -n "$project" ]; then
                break
            fi
            directory=$(dirname "$directory")
        done
        if [ -z "$project" ]; then
            echo "loop: $file has no project" >&2
            return 1
        fi
        by_project["$project"]+=" $file"
    done

    # Loading a project costs more than the files in it: past a handful of projects, one scoped
    # solution check is cheaper than one load per project.
    if [ "${#by_project[@]}" -gt 3 ]; then
        echo "loop: ${#by_project[@]} projects changed, using one scoped solution check"
        dotnet format Systemic.sln --verify-no-changes --no-restore --include ${changed[*]}
        return 0
    fi

    for project in "${!by_project[@]}"; do
        echo "loop: format $project"
        # Unquoted on purpose: --include takes the file list as separate arguments.
        dotnet format "$project" --verify-no-changes --no-restore --include ${by_project[$project]}
    done
}

project="${1:-tests/Systemic.Integration.Tests}"

case "$project" in
    --format)
        format_changed
        exit
        ;;
    --full)
        SECONDS=0
        mapfile -t suites < <(dotnet sln Systemic.sln list \
            | grep -E '^tests/.*\.Tests\.csproj$' \
            | grep -v -F -e Systemic.Boids.Tests -e Systemic.GlowGarden.Tests -e Systemic.PixelMine.Tests)
        # One invocation per suite: it builds the framework projects the suite references, with
        # analyzers on, and runs it. A separate whole-solution build would add the sample games
        # and a second pass over the same projects.
        for suite in "${suites[@]}"; do
            phase "build and test $(basename "$suite" .csproj)"
            dotnet test "$suite" --configuration Release
        done
        phase 'done, gate green (scripts/loop.sh --format before a commit)'
        exit
        ;;
    --samples)
        SECONDS=0
        for suite in tests/Systemic.Boids.Tests tests/Systemic.GlowGarden.Tests tests/Systemic.PixelMine.Tests; do
            phase "build and test $(basename "$suite" .csproj)"
            dotnet test "$suite" --configuration Release
        done
        phase 'done, sample suites green'
        exit
        ;;
esac

filter="${2:-}"
dotnet build "$project" --configuration Release --no-restore -p:RunAnalyzers=false
if [ -n "$filter" ]; then
    dotnet test "$project" --configuration Release --no-build --filter "FullyQualifiedName~$filter"
else
    dotnet test "$project" --configuration Release --no-build
fi
