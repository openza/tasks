#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/../.." && pwd)"
version="${1:-0.1.0}"
runtime="linux-x64"
artifacts_dir="$repo_root/artifacts/linux"
publish_dir="$artifacts_dir/publish-$runtime"
app_dir="$artifacts_dir/Openza_Tasks.AppDir"
output_path="$artifacts_dir/Openza_Tasks-${version}-x86_64.AppImage"

appimagetool_command="${APPIMAGETOOL:-$(command -v appimagetool || true)}"
if [[ -z "$appimagetool_command" || ! -x "$appimagetool_command" ]]; then
  echo "appimagetool is required to build the AppImage." >&2
  exit 1
fi

rm -rf "$publish_dir" "$app_dir"
rm -f "$output_path"
mkdir -p "$publish_dir" "$app_dir/usr/lib/openza-tasks" "$app_dir/usr/bin"
mkdir -p "$app_dir/usr/share/applications" "$app_dir/usr/share/icons/hicolor/256x256/apps"
mkdir -p "$app_dir/usr/share/metainfo"

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

cp -a "$publish_dir/." "$app_dir/usr/lib/openza-tasks/"
printf '%s\n' "$version" > "$app_dir/usr/lib/openza-tasks/VERSION"
ln -s ../lib/openza-tasks/Openza.Tasks.Desktop "$app_dir/usr/bin/openza-tasks"
cp "$script_dir/com.openza.Tasks.desktop" "$app_dir/"
cp "$script_dir/com.openza.Tasks.desktop" "$app_dir/usr/share/applications/"
cp "$script_dir/com.openza.Tasks.metainfo.xml" "$app_dir/usr/share/metainfo/com.openza.Tasks.appdata.xml"
sed -i "s/version=\"0.1.0\"/version=\"$version\"/" "$app_dir/usr/share/metainfo/com.openza.Tasks.appdata.xml"
sed -i "s/date=\"[0-9-]*\"/date=\"$(date -u +%F)\"/" "$app_dir/usr/share/metainfo/com.openza.Tasks.appdata.xml"
cp "$repo_root/assets/icons/icon-256.png" "$app_dir/com.openza.Tasks.png"
cp "$repo_root/assets/icons/icon-256.png" "$app_dir/usr/share/icons/hicolor/256x256/apps/com.openza.Tasks.png"

find "$app_dir" -type d -exec chmod 755 {} +
find "$app_dir/usr/share" -type f -exec chmod 644 {} +
chmod 755 "$app_dir/usr/lib/openza-tasks/Openza.Tasks.Desktop"

cat > "$app_dir/AppRun" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
app_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
exec "$app_dir/usr/bin/openza-tasks" "$@"
EOF
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
