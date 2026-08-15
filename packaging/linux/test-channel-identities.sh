#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$script_dir/channel-identities.sh"
test_dir="$(mktemp -d)"
trap 'rm -rf "$test_dir"' EXIT

OPENZA_PACKAGE_CHANNEL=Production configure_openza_linux_channel
production="$package_name|$desktop_id|$app_library_name|$cli_library_name|$app_launcher|$cli_launcher|$artifact_name|$display_name"
[[ "$production" == "openza-tasks|com.openza.Tasks|openza-tasks|openza-cli|openza-tasks|openza|Openza_Tasks|Openza Tasks" ]]
render_openza_desktop_file "$script_dir/com.openza.Tasks.desktop" "$test_dir/$desktop_id.desktop"
cmp "$script_dir/com.openza.Tasks.desktop" "$test_dir/$desktop_id.desktop"

OPENZA_PACKAGE_CHANNEL=Preview configure_openza_linux_channel
preview="$package_name|$desktop_id|$app_library_name|$cli_library_name|$app_launcher|$cli_launcher|$artifact_name|$display_name"
[[ "$preview" == "openza-tasks-preview|com.openza.Tasks.Preview|openza-tasks-preview|openza-cli-preview|openza-tasks-preview|openza-preview|Openza_Tasks_Preview|Openza Tasks Preview" ]]

IFS='|' read -r -a production_parts <<< "$production"
IFS='|' read -r -a preview_parts <<< "$preview"
for index in 0 1 2 3 4 5 6 7; do
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
grep -Fq "Openza.Tasks.Cli.Package.csproj" "$script_dir/build-deb.sh"

echo "Linux Production and Preview package identities are distinct."
