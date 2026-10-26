#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
artifact="${1:-$repo_root/artifacts/snap/openza-tasks-personal-cloud-preview.snap}"
artifact="$(realpath -m "$artifact")"

if [[ -e "$artifact" ]]; then
    printf 'Refusing to overwrite an existing Snap artifact: %s\n' "$artifact" >&2
    exit 2
fi

preview_root="$(mktemp -d "${TMPDIR:-/tmp}/openza-personal-cloud-snap.XXXXXX")"
cleanup() {
    rm -rf -- "$preview_root"
}
trap cleanup EXIT INT TERM

preview_repo="$preview_root/tasks"
mkdir -p "$preview_repo"
rsync -a \
    --exclude '/.git' \
    --exclude '/.snapcraft/' \
    --exclude '/artifacts/' \
    --exclude '/packaging/personal-cloud/artifact/' \
    --exclude 'bin/' \
    --exclude 'obj/' \
    "$repo_root/" "$preview_repo/"

manifest="$preview_repo/snap/snapcraft.yaml"
test "$(grep -Fc 'OpenzaPackagingProfile: Production' "$manifest")" -eq 2
sed -i 's/OpenzaPackagingProfile: Production/OpenzaPackagingProfile: Preview/g' "$manifest"
test "$(grep -Fc 'OpenzaPackagingProfile: Preview' "$manifest")" -eq 2

preview_output="$preview_repo/artifacts/snap/$(basename "$artifact")"
bash "$preview_repo/packaging/snap/build-local-snap.sh" \
    "$preview_output" \
    Preview

mkdir -p "$(dirname "$artifact")"
install -m 0644 "$preview_output" "$artifact"
bash "$repo_root/packaging/snap/inspect-snap-package.sh" "$artifact" Preview
printf 'Isolated Personal Cloud Preview Snap: %s\n' "$artifact"
