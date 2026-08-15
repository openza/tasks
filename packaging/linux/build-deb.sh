#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/../.." && pwd)"
version="${1:-0.1.0}"
architecture="${2:-amd64}"
source "$script_dir/channel-identities.sh"
configure_openza_linux_channel
case "$architecture" in
  amd64) runtime="linux-x64" ;;
  arm64) runtime="linux-arm64" ;;
  *)
    echo "Unsupported Debian architecture: $architecture (supported: amd64, arm64)." >&2
    exit 1
    ;;
esac
artifacts_dir="$repo_root/artifacts/linux"
publish_dir="$artifacts_dir/publish-$runtime$channel_path_suffix"
package_root="$artifacts_dir/deb-root$channel_path_suffix"
output_path="$artifacts_dir/${package_name}_${version}_${architecture}.deb"

rm -rf "$publish_dir" "$package_root"
mkdir -p "$publish_dir" "$package_root/DEBIAN"
mkdir -p "$package_root/usr/lib/$app_library_name" "$package_root/usr/bin"
mkdir -p "$package_root/usr/share/applications" "$package_root/usr/share/icons/hicolor/256x256/apps"
mkdir -p "$package_root/usr/share/metainfo"

cd "$repo_root/src/Openza.Tasks.Desktop"
dotnet publish Openza.Tasks.Desktop.Package.csproj \
  -c Release \
  -r "$runtime" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:Version="$version" \
  -p:OpenzaPackagingProfile="$package_channel" \
  -p:InformationalVersion="$version" \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -o "$publish_dir"

cp -a "$publish_dir/." "$package_root/usr/lib/$app_library_name/"
printf '%s\n' "$version" > "$package_root/usr/lib/$app_library_name/VERSION"
ln -s "/usr/lib/$app_library_name/Openza.Tasks.Desktop" "$package_root/usr/bin/$app_launcher"
desktop_file="$package_root/usr/share/applications/$desktop_id.desktop"
metainfo_file="$package_root/usr/share/metainfo/$desktop_id.metainfo.xml"
render_openza_desktop_file "$script_dir/com.openza.Tasks.desktop" "$desktop_file"
render_openza_metainfo_file "$script_dir/com.openza.Tasks.metainfo.xml" "$metainfo_file" "$version" "$(date -u +%F)"
cp "$repo_root/assets/icons/icon-256.png" "$package_root/usr/share/icons/hicolor/256x256/apps/$desktop_id.png"

find "$package_root" -type d -exec chmod 755 {} +
find "$package_root/usr/share" -type f -exec chmod 644 {} +
chmod 755 "$package_root/usr/lib/$app_library_name/Openza.Tasks.Desktop"

installed_size="$(du -sk "$package_root/usr" | cut -f1)"
cat > "$package_root/DEBIAN/control" <<EOF
Package: $package_name
Version: $version
Section: office
Priority: optional
Architecture: $architecture
Depends: libc6, libfontconfig1, libfreetype6, libice6, libsecret-tools, libsm6, libx11-6
Installed-Size: $installed_size
Maintainer: Openza <deependra@solanky.dev>
Homepage: https://solanky.dev/openza/tasks/
Description: Local-first task management
 $display_name is a private, local-first desktop task manager with optional
 provider synchronization.
EOF

dpkg-deb --root-owner-group --build "$package_root" "$output_path"
echo "$output_path"
