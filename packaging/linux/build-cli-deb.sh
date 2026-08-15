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
publish_dir="$artifacts_dir/publish-cli-$runtime$channel_path_suffix"
package_root="$artifacts_dir/cli-deb-root$channel_path_suffix"
output_path="$artifacts_dir/${cli_package_name}_${version}_${architecture}.deb"

rm -rf "$publish_dir" "$package_root"
mkdir -p "$publish_dir" "$package_root/DEBIAN"
mkdir -p "$package_root/usr/lib/$cli_library_name" "$package_root/usr/bin"

cd "$repo_root/src/Openza.Tasks.Cli"
dotnet publish Openza.Tasks.Cli.Package.csproj \
  -c Release \
  -r "$runtime" \
  -m:1 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:OpenzaPackagingProfile="$package_channel" \
  -p:Version="$version" \
  -p:InformationalVersion="$version" \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -o "$publish_dir"

cp -a "$publish_dir/." "$package_root/usr/lib/$cli_library_name/"
cp "$script_dir/cli-extraction-cache.sh" "$package_root/usr/lib/$cli_library_name/cli-extraction-cache.sh"
printf '%s\n' "$version" > "$package_root/usr/lib/$cli_library_name/VERSION"
cat > "$package_root/usr/bin/$cli_launcher" <<'EOF'
#!/usr/bin/env sh
set -eu

. "/usr/lib/@OPENZA_CLI_LIBRARY@/cli-extraction-cache.sh"
configure_openza_cli_extraction_cache

exec "/usr/lib/@OPENZA_CLI_LIBRARY@/openza" "$@"
EOF
sed -i "s/@OPENZA_CLI_LIBRARY@/$cli_library_name/" "$package_root/usr/bin/$cli_launcher"

find "$package_root" -type d -exec chmod 755 {} +
find "$package_root/usr/lib/$cli_library_name" -type f -exec chmod 644 {} +
chmod 755 "$package_root/usr/lib/$cli_library_name/openza"
chmod 755 "$package_root/usr/bin/$cli_launcher"

installed_size="$(du -sk "$package_root/usr" | cut -f1)"
cat > "$package_root/DEBIAN/control" <<EOF
Package: $cli_package_name
Version: $version
Section: utils
Priority: optional
Architecture: $architecture
Depends: libc6
Replaces: $package_name (<< 0.1.1)
Installed-Size: $installed_size
Maintainer: Openza <deependra@solanky.dev>
Homepage: https://solanky.dev/openza/tasks/
Description: Command-line interface for Openza Tasks
 $cli_display_name provides local task access for terminals, scripts, and
 automation. It can be installed and used independently of the desktop app.
EOF

dpkg-deb --root-owner-group --build "$package_root" "$output_path"
echo "$output_path"
