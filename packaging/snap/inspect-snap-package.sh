#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <openza-tasks.snap>" >&2
  exit 2
fi

snap_path="$1"
if [[ ! -f "$snap_path" ]]; then
  echo "Snap not found: $snap_path" >&2
  exit 2
fi

command -v unsquashfs >/dev/null 2>&1 || {
  echo "unsquashfs is required to inspect the built Snap." >&2
  exit 2
}

inspection_root="$(mktemp -d "${TMPDIR:-/tmp}/openza-snap-inspect.XXXXXX")"
trap 'rm -rf -- "$inspection_root"' EXIT
root="$inspection_root/root"

unsquashfs -no-progress -d "$root" "$snap_path" >/dev/null

metadata="$root/meta/snap.yaml"
grep -Fqx 'name: openza-tasks' "$metadata"
grep -Fqx 'title: Openza Tasks' "$metadata"
grep -Fqx 'base: core24' "$metadata"
grep -Fqx 'confinement: strict' "$metadata"
grep -Fq 'command: bin/openza-tasks-launch' "$metadata"
grep -Fq 'command: bin/openza-cli-launch' "$metadata"
awk '/^  openza:$/ { cli = 1; next } cli && /^[^ ]|^  [^ ]/ { exit } cli { print }' "$metadata" \
  | grep -Fq 'snap/command-chain/gpu-2404-wrapper'
test -x "$root/snap/command-chain/gpu-2404-wrapper"

test -x "$root/bin/openza-tasks-launch"
test -x "$root/bin/openza-cli-launch"
test -x "$root/opt/openza-tasks/desktop/Openza.Tasks.Desktop"
test -x "$root/opt/openza-tasks/cli/openza"
test -x "$root/usr/bin/secret-tool"
test -f "$root/meta/gui/openza-tasks.desktop"
test -f "$root/meta/gui/icon.png"
test -f "$root/usr/share/metainfo/com.openza.Tasks.metainfo.xml"

test "$(tr -d '\r\n' < "$root/opt/openza-tasks/desktop/.openza-channel")" = Production
test "$(tr -d '\r\n' < "$root/opt/openza-tasks/cli/.openza-channel")" = Production

if find "$root" -type f \( \
  -iname '*.db' -o \
  -iname '*.sqlite' -o \
  -iname '*.sqlite3' -o \
  -iname '*.pfx' -o \
  -iname '*.cer' -o \
  -iname '.env' -o \
  -iname '.env.*' -o \
  -iname '*.log' \
\) -print -quit | grep -q .; then
  echo "The Snap contains a prohibited private or generated file." >&2
  exit 1
fi

echo "Built Snap contains both Production apps, launchers, metadata, icon, and Secret Portal tooling with no prohibited files."
