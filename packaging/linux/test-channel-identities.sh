#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$script_dir/channel-identities.sh"
test_dir="$(mktemp -d)"
trap 'rm -rf "$test_dir"' EXIT

OPENZA_PACKAGE_CHANNEL=Production configure_openza_linux_channel
production="$package_name|$cli_package_name|$desktop_id|$app_library_name|$cli_library_name|$app_launcher|$cli_launcher|$artifact_name|$display_name|$cli_display_name"
[[ "$production" == "openza-tasks|openza-cli|com.openza.Tasks|openza-tasks|openza-cli|openza-tasks|openza|Openza_Tasks|Openza Tasks|Openza CLI" ]]
render_openza_desktop_file "$script_dir/com.openza.Tasks.desktop" "$test_dir/$desktop_id.desktop"
cmp "$script_dir/com.openza.Tasks.desktop" "$test_dir/$desktop_id.desktop"

OPENZA_PACKAGE_CHANNEL=Preview configure_openza_linux_channel
preview="$package_name|$cli_package_name|$desktop_id|$app_library_name|$cli_library_name|$app_launcher|$cli_launcher|$artifact_name|$display_name|$cli_display_name"
[[ "$preview" == "openza-tasks-preview|openza-cli-preview|com.openza.Tasks.Preview|openza-tasks-preview|openza-cli-preview|openza-tasks-preview|openza-preview|Openza_Tasks_Preview|Openza Tasks Preview|Openza CLI Preview" ]]

IFS='|' read -r -a production_parts <<< "$production"
IFS='|' read -r -a preview_parts <<< "$preview"
for index in 0 1 2 3 4 5 6 7 8 9; do
  [[ "${production_parts[$index]}" != "${preview_parts[$index]}" ]]
done

render_openza_desktop_file "$script_dir/com.openza.Tasks.desktop" "$test_dir/$desktop_id.desktop"
render_openza_metainfo_file "$script_dir/com.openza.Tasks.metainfo.xml" "$test_dir/$desktop_id.metainfo.xml" "0.2.3" "2026-08-15"
grep -Fqx "Name=Openza Tasks Preview" "$test_dir/$desktop_id.desktop"
grep -Fqx "Exec=openza-tasks-preview" "$test_dir/$desktop_id.desktop"
grep -Fqx "Icon=com.openza.Tasks.Preview" "$test_dir/$desktop_id.desktop"
grep -Fq "<id>com.openza.Tasks.Preview</id>" "$test_dir/$desktop_id.metainfo.xml"
grep -Fq "<name>Openza Tasks Preview</name>" "$test_dir/$desktop_id.metainfo.xml"
grep -Fq "com.openza.Tasks.Preview.desktop" "$test_dir/$desktop_id.metainfo.xml"
! grep -Fq "Openza.Tasks.Cli" "$script_dir/build-appimage.sh"
! grep -Fq "cli_publish_dir" "$script_dir/build-appimage.sh"
grep -Fq "Openza.Tasks.Desktop.Package.csproj" "$script_dir/build-appimage.sh"
grep -Fq "Openza.Tasks.Desktop.Package.csproj" "$script_dir/build-deb.sh"
! grep -Fq "Openza.Tasks.Cli.Package.csproj" "$script_dir/build-deb.sh"
! grep -Fq 'usr/bin/$cli_launcher' "$script_dir/build-deb.sh"
grep -Fq "Openza.Tasks.Cli.Package.csproj" "$script_dir/build-cli-deb.sh"
grep -Fq 'usr/bin/$cli_launcher' "$script_dir/build-cli-deb.sh"
grep -Fq "cli-extraction-cache.sh" "$script_dir/build-cli-deb.sh"
grep -Fq 'TMPDIR:-/tmp' "$script_dir/cli-extraction-cache.sh"
grep -Fq '.openza-write-test-' "$script_dir/cli-extraction-cache.sh"

source "$script_dir/cli-extraction-cache.sh"
cache_test_root="$test_dir/cache-test"
mkdir -p "$cache_test_root/home" "$cache_test_root/tmp"
unset DOTNET_BUNDLE_EXTRACT_BASE_DIR
XDG_CACHE_HOME="$cache_test_root/home"
TMPDIR="$cache_test_root/tmp"
export XDG_CACHE_HOME TMPDIR
configure_openza_cli_extraction_cache
[[ "$DOTNET_BUNDLE_EXTRACT_BASE_DIR" == "$cache_test_root/home/openza/cli" ]]
[[ -w "$DOTNET_BUNDLE_EXTRACT_BASE_DIR" ]]

unset DOTNET_BUNDLE_EXTRACT_BASE_DIR
unwritable_cache="$cache_test_root/unwritable"
mkdir -p "$unwritable_cache"
chmod 500 "$unwritable_cache"
XDG_CACHE_HOME="$unwritable_cache"
export XDG_CACHE_HOME
configure_openza_cli_extraction_cache
[[ "$DOTNET_BUNDLE_EXTRACT_BASE_DIR" == "$cache_test_root/tmp/openza-cli-$(id -u)" ]]
[[ -w "$DOTNET_BUNDLE_EXTRACT_BASE_DIR" ]]
grep -Fq 'Replaces: $package_name (<< 0.1.1)' "$script_dir/build-cli-deb.sh"
grep -Fq 'Depends: libc6, libsecret-tools' "$script_dir/build-cli-deb.sh"
! grep -Fq 'Breaks: $package_name' "$script_dir/build-cli-deb.sh"
! grep -Fq '$cli_package_name (= $version)' "$script_dir/build-deb.sh"

echo "Linux Production and Preview package identities are distinct."
