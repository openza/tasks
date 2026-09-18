#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
artifact="${1:-$repo_root/artifacts/snap/openza-tasks.snap}"
personal_cloud_artifact="${2:-}"
expected_channel="${3:-Production}"
artifact="$(realpath -m "$artifact")"
packed_artifact="$repo_root/$(basename "$artifact")"
staged_personal_cloud_artifact="$repo_root/packaging/personal-cloud/artifact"

if [[ -z "$personal_cloud_artifact" ]]; then
    printf 'Usage: %s <snap-output> <personal-cloud-artifact-directory> [Production|Preview]\n' "$0" >&2
    exit 2
fi
if [[ "$expected_channel" != Production && "$expected_channel" != Preview ]]; then
    printf 'Expected channel must be Production or Preview.\n' >&2
    exit 2
fi
personal_cloud_artifact="$(realpath "$personal_cloud_artifact")"
if [[ ! -f "$personal_cloud_artifact/artifact-manifest.json" ]]; then
    printf 'Personal Cloud artifact manifest not found: %s\n' "$personal_cloud_artifact" >&2
    exit 2
fi

cleanup() {
    trap - EXIT INT TERM
    cd "$repo_root"
    snapcraft clean
    rm -rf -- "$staged_personal_cloud_artifact"
}

trap cleanup EXIT INT TERM

cd "$repo_root"
df -h /
mkdir -p "$(dirname "$artifact")"
if [[ -e "$artifact" || -e "$packed_artifact" ]]; then
    printf 'Refusing to overwrite an existing Snap artifact: %s\n' "$artifact" >&2
    exit 2
fi

rm -rf -- "$staged_personal_cloud_artifact"
mkdir -p "$staged_personal_cloud_artifact"
cp -a "$personal_cloud_artifact/." "$staged_personal_cloud_artifact/"

# Clear disposable state from an earlier interrupted build before creating a
# new instance. The exit trap performs the matching cleanup for this build.
snapcraft clean
snapcraft pack --output "$(basename "$packed_artifact")"
bash packaging/snap/inspect-snap-package.sh "$packed_artifact" "$expected_channel"
mv "$packed_artifact" "$artifact"

printf 'Snap artifact: %s\n' "$artifact"
