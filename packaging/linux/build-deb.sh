#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/../.." && pwd)"
version="${1:-0.1.0}"
architecture="${2:-amd64}"
case "$architecture" in
  amd64) runtime="linux-x64" ;;
  arm64) runtime="linux-arm64" ;;
  *)
    echo "Unsupported Debian architecture: $architecture (supported: amd64, arm64)." >&2
    exit 1
    ;;
esac
artifacts_dir="$repo_root/artifacts/linux"
publish_dir="$artifacts_dir/publish-$runtime"
package_root="$artifacts_dir/deb-root"
output_path="$artifacts_dir/openza-tasks_${version}_${architecture}.deb"

rm -rf "$publish_dir" "$package_root"
mkdir -p "$publish_dir" "$package_root/DEBIAN"
mkdir -p "$package_root/usr/lib/openza-tasks" "$package_root/usr/bin"
mkdir -p "$package_root/usr/share/applications" "$package_root/usr/share/icons/hicolor/256x256/apps"
mkdir -p "$package_root/usr/share/metainfo"

cd "$repo_root/src/Openza.Tasks.Desktop"
dotnet publish Openza.Tasks.Desktop.csproj \
  -c Release \
  -r "$runtime" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:Version="$version" \
  -p:InformationalVersion="$version" \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -o "$publish_dir"

cp -a "$publish_dir/." "$package_root/usr/lib/openza-tasks/"
printf '%s\n' "$version" > "$package_root/usr/lib/openza-tasks/VERSION"
ln -s /usr/lib/openza-tasks/Openza.Tasks.Desktop "$package_root/usr/bin/openza-tasks"
cp "$script_dir/com.openza.Tasks.desktop" "$package_root/usr/share/applications/"
cp "$script_dir/com.openza.Tasks.metainfo.xml" "$package_root/usr/share/metainfo/"
sed -i "s/version=\"0.1.0\"/version=\"$version\"/" "$package_root/usr/share/metainfo/com.openza.Tasks.metainfo.xml"
sed -i "s/date=\"[0-9-]*\"/date=\"$(date -u +%F)\"/" "$package_root/usr/share/metainfo/com.openza.Tasks.metainfo.xml"
cp "$repo_root/assets/icons/icon-256.png" "$package_root/usr/share/icons/hicolor/256x256/apps/com.openza.Tasks.png"

find "$package_root" -type d -exec chmod 755 {} +
find "$package_root/usr/share" -type f -exec chmod 644 {} +
chmod 755 "$package_root/usr/lib/openza-tasks/Openza.Tasks.Desktop"

installed_size="$(du -sk "$package_root/usr" | cut -f1)"
cat > "$package_root/DEBIAN/control" <<EOF
Package: openza-tasks
Version: $version
Section: office
Priority: optional
Architecture: $architecture
Depends: libc6, libfontconfig1, libfreetype6, libice6, libsecret-tools, libsm6, libx11-6
Installed-Size: $installed_size
Maintainer: Openza <deependra@solanky.dev>
Homepage: https://solanky.dev/openza/tasks/
Description: Local-first task management
 Openza Tasks is a private, local-first desktop task manager with optional
 provider synchronization.
EOF

dpkg-deb --root-owner-group --build "$package_root" "$output_path"
echo "$output_path"
