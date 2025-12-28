# Openza Tasks

**Local First. Open Source.**

A local-first task organizer for Linux and Windows. Store tasks locally and optionally sync with Todoist and Microsoft To-Do.

## Features

- **Local-first storage** - Your tasks live on your device, always accessible
- **Provider sync** - Optional integration with Todoist and Microsoft To-Do
- **4-pane GTD layout** - Projects, tasks, and details at a glance
- **Automatic backups** - Daily backups with restore and export options
- **Dark theme** - Easy on the eyes, day or night
- **Markdown import/export** - Bring tasks in, take them out
- **Native desktop apps** - Linux (AppImage & Flatpak) and Windows (Installer)

## Download

Get the latest release from [GitHub Releases](https://github.com/openza/tasks/releases):

### Linux

- **AppImage**: Download, make executable (`chmod +x`), and run
- **Flatpak**: Download and install with `flatpak install Openza-*.flatpak`

**System Requirements:** Ubuntu 22.04+, Fedora 35+, Debian 12+ (GLIBC 2.34+), or any Linux with Flatpak

### Windows

- **Installer**: Download `Openza-*-Setup.exe` and run to install

**System Requirements:** Windows 10 or later, x64 architecture

## Building from Source

### Prerequisites

- Flutter SDK 3.7+
- Rust toolchain (for sync engine)
- Platform-specific build dependencies (see below)

### Linux

```bash
# Install Flutter (https://docs.flutter.dev/get-started/install/linux)

# Install Rust
curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh

# Install Linux build dependencies
sudo apt-get update
sudo apt-get install -y clang cmake ninja-build pkg-config libgtk-3-dev liblzma-dev libstdc++-12-dev libsecret-1-dev

# Clone and build
git clone https://github.com/openza/tasks.git
cd tasks
flutter pub get
cd rust && cargo build --release && cd ..
flutter build linux --release

# Run
./build/linux/x64/release/bundle/openza_tasks
```

### Windows

```powershell
# Install Flutter (https://docs.flutter.dev/get-started/install/windows)
# Install Rust (https://rustup.rs)
# Install Visual Studio 2022 with "Desktop development with C++" workload

# Clone and build
git clone https://github.com/openza/tasks.git
cd tasks
flutter pub get
dart run build_runner build --delete-conflicting-outputs
cd rust; cargo build --release; cd ..
copy rust\target\release\openza_sync.dll .
flutter build windows --release

# Create installer (requires Inno Setup: winget install JRSoftware.InnoSetup)
dart run inno_bundle --release --no-app

# Run
.\build\windows\x64\runner\Release\openza_tasks.exe
```

## Connecting Task Providers

### Todoist

1. Go to **Settings → Todoist**
2. Get your API token from [Todoist Settings → Integrations → Developer](https://todoist.com/app/settings/integrations/developer)
3. Paste your API token and click **Connect**

### Microsoft To-Do

1. Go to **Settings → Microsoft To-Do**
2. Click **Sign in with Microsoft**
3. Authorize the app with your Microsoft account

## Development

```bash
# Get dependencies
flutter pub get

# Generate code (Drift, Freezed, Riverpod)
dart run build_runner build --delete-conflicting-outputs

# Run tests
flutter test

# Analyze code
flutter analyze

# Run with hot reload
flutter run -d linux    # Linux
flutter run -d windows  # Windows
```

## Architecture

```
lib/
├── core/           # Constants, theme, utilities
├── data/           # Repositories, API clients, database, sync engine
├── domain/         # Entities, repository interfaces
└── presentation/   # Screens, widgets, Riverpod providers

rust/
└── src/            # Rust sync engine with FFI bindings
```

### Tech Stack

- **Frontend**: Flutter 3.7+, Riverpod, GoRouter, Drift (SQLite)
- **Sync Engine**: Rust with FFI bridge
- **Code Generation**: Freezed, JsonSerializable

## License

MIT License - see [LICENSE](LICENSE) for details.

## Author

**Deependra Solanky**
- GitHub: [@solankydev](https://github.com/solankydev)
