#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/../.." && pwd)"
desktop_source="$repo_root/snap/gui/openza-tasks.desktop"
metadata_source="$repo_root/snap/gui/com.openza.Tasks.metainfo.xml"
validation_dir="$(mktemp -d)"
desktop_validation="$validation_dir/openza-tasks.desktop"

cleanup() {
  rm -rf -- "$validation_dir"
}
trap cleanup EXIT

# ${SNAP} is the official runtime form for snapped desktop icons. Resolve it
# to a representative absolute mount path only for freedesktop validation.
sed 's|${SNAP}|/snap/openza-tasks/current|g' "$desktop_source" > "$desktop_validation"

desktop-file-validate "$desktop_validation"
appstreamcli validate --no-net "$metadata_source"
