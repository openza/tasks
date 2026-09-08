#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
artifact="${1:-$repo_root/artifacts/snap/openza-tasks.snap}"
artifact="$(realpath -m "$artifact")"
packed_artifact="$repo_root/$(basename "$artifact")"

cleanup() {
    trap - EXIT INT TERM
    cd "$repo_root"
    snapcraft clean
}

trap cleanup EXIT INT TERM

cd "$repo_root"
df -h /
mkdir -p "$(dirname "$artifact")"
if [[ -e "$artifact" || -e "$packed_artifact" ]]; then
    printf 'Refusing to overwrite an existing Snap artifact: %s\n' "$artifact" >&2
    exit 2
fi

# Clear disposable state from an earlier interrupted build before creating a
# new instance. The exit trap performs the matching cleanup for this build.
snapcraft clean
snapcraft pack --output "$(basename "$packed_artifact")"
bash packaging/snap/inspect-snap-package.sh "$packed_artifact"
mv "$packed_artifact" "$artifact"

printf 'Snap artifact: %s\n' "$artifact"
