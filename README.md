# Openza Tasks

A unified task manager for Linux that integrates with Todoist and Microsoft To-Do. Built with Flutter.

## Features

- Connect multiple task providers (Todoist, Microsoft To-Do)
- Unified view of all your tasks
- Filter by projects, labels, and due dates
- Native Linux desktop experience

## Requirements

- Flutter SDK 3.10.3 or later
- Linux development dependencies

## Building from Source

### 1. Install Flutter

Follow the [official Flutter installation guide](https://docs.flutter.dev/get-started/install/linux).

### 2. Install Linux Dependencies

```bash
sudo apt-get update
sudo apt-get install -y clang cmake ninja-build pkg-config libgtk-3-dev liblzma-dev libstdc++-12-dev
```

### 3. Build and Run

```bash
git clone https://github.com/openza/tasks.git
cd tasks

# Install dependencies
flutter pub get

# Run in development
flutter run -d linux

# Build release
flutter build linux --release
```

### 4. Connect to Todoist

1. Launch the app
2. Go to **Settings → Todoist**
3. Get your API token from [Todoist Settings → Integrations → Developer](https://todoist.com/app/settings/integrations/developer)
4. Paste your API token and click **Connect**

### 5. Run the Built Application

```bash
./build/linux/x64/release/bundle/openza_flutter
```

## Development

```bash
# Get dependencies
flutter pub get

# Generate code (for Drift, Freezed, etc.)
flutter pub run build_runner build --delete-conflicting-outputs

# Run tests
flutter test

# Run with hot reload
flutter run -d linux
```

## Project Structure

```
lib/
├── core/           # Constants, theme, utilities
├── data/           # Data sources, repositories
├── domain/         # Entities, business logic
└── presentation/   # UI screens, widgets, providers
```

## License

MIT License - see [LICENSE](LICENSE) for details.

## Author

**Deependra Solanky**
- GitHub: [@solankydev](https://github.com/solankydev)
- Email: deependra@solanky.dev
