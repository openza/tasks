#!/bin/bash
set -e

# Openza Tasks Flatpak Build Script
# This script builds a Flatpak bundle from the Flutter Linux build

APP_ID="com.openza.tasks"
VERSION=$(grep 'version:' pubspec.yaml | head -1 | sed 's/version: //' | sed 's/+.*//')

echo "Building Flatpak for Openza Tasks v$VERSION..."

# Ensure Flutter build exists
if [ ! -d "build/linux/x64/release/bundle" ]; then
    echo "Error: Flutter build not found. Run 'flutter build linux --release' first."
    exit 1
fi

# Create flatpak desktop file with correct icon path
mkdir -p flatpak/build
cat > flatpak/build/com.openza.tasks.desktop << EOF
[Desktop Entry]
Name=Openza Tasks
Comment=Local-first task organizer for Linux
Exec=openza_tasks
Icon=com.openza.tasks
Type=Application
Categories=Office;ProjectManagement;
Terminal=false
StartupWMClass=openza_tasks
EOF

# Build the flatpak
cd flatpak
flatpak-builder --force-clean --repo=repo build-dir com.openza.tasks.yml

# Create bundle
flatpak build-bundle repo "../Openza-Tasks-$VERSION.flatpak" "$APP_ID"

echo "Flatpak bundle created: Openza-Tasks-$VERSION.flatpak"
