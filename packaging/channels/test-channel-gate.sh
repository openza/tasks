#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/../.." && pwd)"
cli_project="$repo_root/src/Openza.Tasks.Cli/Openza.Tasks.Cli.csproj"
desktop_project="$repo_root/src/Openza.Tasks.Desktop/Openza.Tasks.Desktop.csproj"
cli_package_project="$repo_root/src/Openza.Tasks.Cli/Openza.Tasks.Cli.Package.csproj"
desktop_package_project="$repo_root/src/Openza.Tasks.Desktop/Openza.Tasks.Desktop.Package.csproj"
test_dir="$(mktemp -d)"
output_file="$test_dir/output"
trap 'rm -rf "$test_dir"' EXIT

expect_failure() {
  if "$@" >"$output_file" 2>&1; then
    echo "Expected command to fail: $*" >&2
    return 1
  fi
  grep -Fq "packaging" "$output_file"
}

expect_failure dotnet msbuild "$cli_project" -t:ValidateOpenzaChannel -p:OpenzaPackagingProfile=Production -p:_IsPublishing=true
expect_failure dotnet msbuild "$desktop_project" -t:ValidateOpenzaChannel -p:OpenzaPackagingProfile=Preview -p:_IsPublishing=true
expect_failure dotnet msbuild "$cli_project" -t:ValidateOpenzaChannel -p:OpenzaChannel=Production -p:_IsPublishing=true
expect_failure dotnet msbuild "$desktop_project" -t:ValidateOpenzaChannel -p:OpenzaChannel=Preview -p:_IsPublishing=true
expect_failure dotnet msbuild "$cli_project" -t:Run -p:OpenzaPackagingProfile=Production -p:_IsPublishing=true -p:RunCommand=/usr/bin/false
expect_failure dotnet msbuild "$desktop_project" -t:Run -p:OpenzaPackagingProfile=Preview -p:_IsPublishing=true -p:RunCommand=/usr/bin/false
expect_failure dotnet msbuild "$cli_package_project" -t:Run -p:OpenzaPackagingProfile=Production -p:_IsPublishing=true -p:RunCommand=/usr/bin/false
expect_failure dotnet msbuild "$desktop_package_project" -t:Run -p:OpenzaPackagingProfile=Preview -p:_IsPublishing=true -p:RunCommand=/usr/bin/false

dotnet msbuild "$cli_package_project" -t:ValidateOpenzaChannel -p:OpenzaPackagingProfile=Production -p:_IsPublishing=true -v:quiet
dotnet msbuild "$desktop_package_project" -t:ValidateOpenzaChannel -p:OpenzaPackagingProfile=Preview -p:_IsPublishing=true -v:quiet

dotnet build "$cli_package_project" -c Release --no-restore -m:1 -p:OpenzaPackagingProfile=Production -p:_IsPublishing=true -v:quiet
production_build_dir="$repo_root/src/Openza.Tasks.Cli/bin/packaging/Production/Release/net10.0"
[[ ! -e "$production_build_dir/.openza-channel" ]]
OPENZA_TASKS_DEV_DATA_DIR="$test_dir/production-spoof-dev" dotnet "$production_build_dir/openza.dll" --format json status >"$output_file"
grep -Eq '"channel"[[:space:]]*:[[:space:]]*"dev"' "$output_file"

dotnet build "$cli_package_project" -c Release --no-restore -m:1 -p:OpenzaPackagingProfile=Preview -p:_IsPublishing=true -v:quiet
preview_build_dir="$repo_root/src/Openza.Tasks.Cli/bin/packaging/Preview/Release/net10.0"
[[ ! -e "$preview_build_dir/.openza-channel" ]]
OPENZA_TASKS_DEV_DATA_DIR="$test_dir/preview-spoof-dev" dotnet "$preview_build_dir/openza.dll" --format json status >"$output_file"
grep -Eq '"channel"[[:space:]]*:[[:space:]]*"dev"' "$output_file"

echo "Source channel spoofing is rejected; unpublished packaging builds fail closed to Dev."
