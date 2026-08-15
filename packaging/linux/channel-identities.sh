#!/usr/bin/env bash

configure_openza_linux_channel() {
  case "${OPENZA_PACKAGE_CHANNEL:-Production}" in
    Production)
      package_channel="Production"
      package_name="openza-tasks"
      cli_package_name="openza-cli"
      display_name="Openza Tasks"
      cli_display_name="Openza CLI"
      desktop_id="com.openza.Tasks"
      app_library_name="openza-tasks"
      cli_library_name="openza-cli"
      app_launcher="openza-tasks"
      cli_launcher="openza"
      artifact_name="Openza_Tasks"
      channel_path_suffix=""
      ;;
    Preview)
      package_channel="Preview"
      package_name="openza-tasks-preview"
      cli_package_name="openza-cli-preview"
      display_name="Openza Tasks Preview"
      cli_display_name="Openza CLI Preview"
      desktop_id="com.openza.Tasks.Preview"
      app_library_name="openza-tasks-preview"
      cli_library_name="openza-cli-preview"
      app_launcher="openza-tasks-preview"
      cli_launcher="openza-preview"
      artifact_name="Openza_Tasks_Preview"
      channel_path_suffix="-preview"
      ;;
    *)
      echo "OPENZA_PACKAGE_CHANNEL must be Production or Preview." >&2
      return 1
      ;;
  esac
}

render_openza_desktop_file() {
  local template_path="$1"
  local output_path="$2"
  cp "$template_path" "$output_path"
  sed -i \
    -e "s/^Name=.*/Name=$display_name/" \
    -e "s/^Exec=.*/Exec=$app_launcher/" \
    -e "s/^Icon=.*/Icon=$desktop_id/" \
    -e "s/^StartupWMClass=.*/StartupWMClass=$display_name/" \
    "$output_path"
}

render_openza_metainfo_file() {
  local template_path="$1"
  local output_path="$2"
  local version="$3"
  local release_date="$4"
  cp "$template_path" "$output_path"
  sed -i \
    -e "s#<id>com.openza.Tasks</id>#<id>$desktop_id</id>#" \
    -e "s#<name>Openza Tasks</name>#<name>$display_name</name>#" \
    -e "s#com.openza.Tasks.desktop#$desktop_id.desktop#" \
    -e "s/version=\"0.1.0\"/version=\"$version\"/" \
    -e "s/date=\"[0-9-]*\"/date=\"$release_date\"/" \
    "$output_path"
}
