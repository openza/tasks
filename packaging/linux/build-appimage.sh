#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/../.." && pwd)"
version="${1:-0.1.0}"
runtime="linux-x64"
source "$script_dir/channel-identities.sh"
configure_openza_linux_channel
artifacts_dir="$repo_root/artifacts/linux"
publish_dir="$artifacts_dir/publish-$runtime$channel_path_suffix"
app_dir="$artifacts_dir/$artifact_name.AppDir"
output_path="$artifacts_dir/${artifact_name}-${version}-x86_64.AppImage"

appimagetool_command="${APPIMAGETOOL:-$(command -v appimagetool || true)}"
if [[ -z "$appimagetool_command" || ! -x "$appimagetool_command" ]]; then
  echo "appimagetool is required to build the AppImage." >&2
  exit 1
fi

rm -rf "$publish_dir" "$app_dir"
rm -f "$output_path"
mkdir -p "$publish_dir" "$app_dir/usr/lib/$app_library_name" "$app_dir/usr/bin"
mkdir -p "$app_dir/usr/share/applications" "$app_dir/usr/share/icons/hicolor/256x256/apps"
mkdir -p "$app_dir/usr/share/metainfo"

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

cp -a "$publish_dir/." "$app_dir/usr/lib/$app_library_name/"
printf '%s\n' "$version" > "$app_dir/usr/lib/$app_library_name/VERSION"
ln -s "../lib/$app_library_name/Openza.Tasks.Desktop" "$app_dir/usr/bin/$app_launcher"
desktop_file="$app_dir/$desktop_id.desktop"
installed_desktop_file="$app_dir/usr/share/applications/$desktop_id.desktop"
metainfo_file="$app_dir/usr/share/metainfo/$desktop_id.appdata.xml"
render_openza_desktop_file "$script_dir/com.openza.Tasks.desktop" "$desktop_file"
render_openza_desktop_file "$script_dir/com.openza.Tasks.desktop" "$installed_desktop_file"
render_openza_metainfo_file "$script_dir/com.openza.Tasks.metainfo.xml" "$metainfo_file" "$version" "$(date -u +%F)"
cp "$repo_root/assets/icons/icon-256.png" "$app_dir/$desktop_id.png"
cp "$repo_root/assets/icons/icon-256.png" "$app_dir/usr/share/icons/hicolor/256x256/apps/$desktop_id.png"

find "$app_dir" -type d -exec chmod 755 {} +
find "$app_dir/usr/share" -type f -exec chmod 644 {} +
chmod 755 "$app_dir/usr/lib/$app_library_name/Openza.Tasks.Desktop"

cat > "$app_dir/AppRun" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
app_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
exec "$app_dir/usr/bin/@OPENZA_APP_LAUNCHER@" "$@"
EOF
sed -i "s/@OPENZA_APP_LAUNCHER@/$app_launcher/" "$app_dir/AppRun"
chmod +x "$app_dir/AppRun"

runtime_args=()
if [[ -n "${APPIMAGE_RUNTIME_FILE:-}" ]]; then
  if [[ ! -f "$APPIMAGE_RUNTIME_FILE" ]]; then
    echo "APPIMAGE_RUNTIME_FILE does not exist: $APPIMAGE_RUNTIME_FILE" >&2
    exit 1
  fi
  runtime_args=(--runtime-file "$APPIMAGE_RUNTIME_FILE")
fi

ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$appimagetool_command" "${runtime_args[@]}" --no-appstream "$app_dir" "$output_path"
if [[ ! -s "$output_path" ]]; then
  echo "appimagetool did not create a valid output file." >&2
  exit 1
fi
echo "$output_path"
