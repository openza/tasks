# Openza Tasks

**Local First. Open Source.**

A local-first task organizer for Linux. Store tasks locally and optionally sync with Todoist and Microsoft To-Do.

## Features

- **Local-first storage** - Your tasks live on your device, always accessible
- **Provider sync** - Optional integration with Todoist and Microsoft To-Do
- **4-pane GTD layout** - Projects, tasks, and details at a glance
- **Automatic backups** - Daily backups with restore and export options
- **Dark theme** - Easy on the eyes, day or night
- **Markdown import/export** - Bring tasks in, take them out
- **Native Linux desktop** - AppImage & Flatpak packages

## Download

Get the latest release from [GitHub Releases](https://github.com/openza/tasks/releases):

- **AppImage**: Download, make executable (`chmod +x`), and run
- **Flatpak**: Download and install with `flatpak install Openza-*.flatpak`

### System Requirements

- **AppImage**: Ubuntu 22.04+, Fedora 35+, Debian 12+ (GLIBC 2.34+)
- **Flatpak**: Any Linux with Flatpak installed

## Building from Source

### Prerequisites

- Flutter SDK 3.7+
- Rust toolchain (for sync engine)
- Linux development dependencies

### 1. Install Dependencies

```bash
# Flutter (follow https://docs.flutter.dev/get-started/install/linux)

# Rust
curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh

# Linux build dependencies
sudo apt-get update
sudo apt-get install -y clang cmake ninja-build pkg-config libgtk-3-dev liblzma-dev libstdc++-12-dev libsecret-1-dev
```

### 2. Build and Run

```bash
git clone https://github.com/openza/tasks.git
cd tasks

# Install Flutter dependencies
flutter pub get

# Build Rust sync engine (happens automatically via CMake, or manually)
cd rust && cargo build --release && cd ..

# Run in development
flutter run -d linux

# Build release
flutter build linux --release
```

### 3. Run the Built Application

```bash
./build/linux/x64/release/bundle/openza_tasks
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
flutter pub run build_runner build --delete-conflicting-outputs

# Run tests
flutter test

# Analyze code
flutter analyze

# Run with hot reload
flutter run -d linux
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
