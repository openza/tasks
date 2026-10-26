#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/../.." && pwd)"
manifest="$repo_root/snap/snapcraft.yaml"

bash "$script_dir/validate-metadata.sh"

grep -Fqx 'name: openza-tasks' "$manifest"
grep -Fqx 'base: core24' "$manifest"
grep -Fqx 'confinement: strict' "$manifest"
grep -Fqx 'contact: https://github.com/openza/tasks/issues' "$manifest"
grep -Fq 'Openza.Tasks.Desktop.Package.csproj' "$manifest"
grep -Fq 'Openza.Tasks.Cli.Package.csproj' "$manifest"
grep -Fq 'OpenzaPackagingProfile: Production' "$manifest"
grep -Fq 'desktop: meta/gui/openza-tasks.desktop' "$manifest"
grep -Fq 'openza:' "$manifest"
grep -Fq 'command: bin/openza-cli-launch' "$manifest"
awk '/^  openza:$/ { cli = 1; next } cli && /^[^ ]|^  [^ ]/ { exit } cli { print }' "$manifest" \
  | grep -Fq 'snap/command-chain/gpu-2404-wrapper'
grep -Fq 'libsecret-tools' "$manifest"
grep -Fq 'network-bind' "$manifest"
grep -Fq 'password-manager-service' "$manifest"
# Personal Cloud ships as signed cloud releases, not inside the Snap.
! grep -Fq 'PersonalCloudArtifact' "$manifest"
grep -Fq 'libicu74' "$manifest"
grep -Fq 'extensions: [gnome]' "$manifest"
test "$(grep -Fc "version: '1.3.0'" "$manifest")" -eq 1
test "$(grep -Ec "^[[:space:]]+Version: '1.3.0'$" "$manifest")" -eq 2
test "$(grep -Ec "^[[:space:]]+InformationalVersion: '1.3.0'$" "$manifest")" -eq 2
test "$(grep -Fc "'.openza-channel': opt/openza-tasks/" "$manifest")" -eq 2
grep -Fq "'Openza.Tasks.Desktop': opt/openza-tasks/desktop/Openza.Tasks.Desktop" "$manifest"
grep -Fq "'openza': opt/openza-tasks/cli/openza" "$manifest"
grep -Fq 'expected_channel="${2:-Production}"' "$repo_root/packaging/snap/build-local-snap.sh"
grep -Fq 'OpenzaPackagingProfile: Preview' "$repo_root/packaging/snap/build-personal-cloud-preview-snap.sh"
grep -Fq 'openza-tasks_pcdev' "$repo_root/docs/snap-release.md"
grep -Fq 'SNAP_USER_COMMON/dotnet/desktop' "$repo_root/snap/local/openza-tasks-launch"
grep -Fq 'SNAP_USER_COMMON/dotnet/cli' "$repo_root/snap/local/openza-cli-launch"
grep -Fq 'opt/openza-tasks/desktop/Openza.Tasks.Desktop' "$repo_root/snap/local/openza-tasks-launch"
grep -Fq 'opt/openza-tasks/cli/openza' "$repo_root/snap/local/openza-cli-launch"
grep -Fqx 'Exec=openza-tasks' "$repo_root/snap/gui/openza-tasks.desktop"
grep -Fqx 'Icon=${SNAP}/meta/gui/openza-tasks.png' "$repo_root/snap/gui/openza-tasks.desktop"
grep -Fq '<launchable type="desktop-id">openza-tasks.desktop</launchable>' "$repo_root/snap/gui/com.openza.Tasks.metainfo.xml"
grep -Fq '<release version="1.3.0" date="2026-10-26" />' "$repo_root/snap/gui/com.openza.Tasks.metainfo.xml"
grep -Fq 'release: edge' "$repo_root/.github/workflows/snap-store.yml"
! grep -Eq 'release: (stable|candidate|beta)' "$repo_root/.github/workflows/snap-store.yml"

! grep -R -E 'build-(deb|appimage)\.sh|\.deb|\.AppImage' \
  "$repo_root/README.md" \
  "$repo_root/docs/architecture.md" \
  "$repo_root/docs/cli.md" \
  "$repo_root/docs/development/environment.md" \
  "$repo_root/docs/snap-release.md" \
  "$repo_root/.github/workflows/ci.yml" \
  "$repo_root/.github/workflows/release.yml" \
  "$repo_root/.github/workflows/snap-store.yml"

echo "Openza Tasks Snap metadata and Snap-only public Linux references are consistent."
