#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$script_dir/src/Openza.Tasks.Desktop"
exec dotnet run --project Openza.Tasks.Desktop.csproj "$@"
