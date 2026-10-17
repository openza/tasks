#!/usr/bin/env bash
set -Eeuo pipefail
trap 'printf "Snap inspection failed at line %s: %s\n" "$LINENO" "$BASH_COMMAND" >&2' ERR

if [[ $# -lt 1 || $# -gt 2 ]]; then
  echo "Usage: $0 <openza-tasks.snap> [Production|Preview]" >&2
  exit 2
fi

snap_path="$1"
expected_channel="${2:-Production}"
if [[ "$expected_channel" != Production && "$expected_channel" != Preview ]]; then
  echo "Expected channel must be Production or Preview." >&2
  exit 2
fi
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
test -f "$root/opt/openza-tasks/desktop/PersonalCloudArtifact/artifact-manifest.json"
test -f "$root/opt/openza-tasks/desktop/PersonalCloudArtifact/worker.js"
grep -Fq 'network-bind' "$metadata"
grep -Fq 'password-manager-service' "$metadata"
test -f "$root/meta/gui/openza-tasks.desktop"
test -f "$root/meta/gui/icon.png"
test -f "$root/usr/share/metainfo/com.openza.Tasks.metainfo.xml"

test "$(tr -d '\r\n' < "$root/opt/openza-tasks/desktop/.openza-channel")" = "$expected_channel"
test "$(tr -d '\r\n' < "$root/opt/openza-tasks/cli/.openza-channel")" = "$expected_channel"

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

echo "Built Snap contains both $expected_channel apps, launchers, metadata, icon, and Secret Portal tooling with no prohibited files."
