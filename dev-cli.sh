#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$script_dir"
exec dotnet run --project src/Openza.Tasks.Cli/Openza.Tasks.Cli.csproj -p:OpenzaChannel=Dev -- "$@"
