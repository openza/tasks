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

### 3. Register OAuth Applications

This app requires you to register your own OAuth applications to authenticate with task providers.

#### Todoist

1. Go to [Todoist App Console](https://developer.todoist.com/appconsole.html)
2. Create a new app
3. Note your **Client ID** and **Client Secret**
4. Set OAuth redirect URL to: `openza://auth/callback`

#### Microsoft To-Do (Optional)

1. Go to [Azure Portal - App Registrations](https://portal.azure.com/#blade/Microsoft_AAD_RegisteredApps/ApplicationsListBlade)
2. Register a new application
3. Note your **Application (client) ID**
4. Add redirect URI: `openza://auth/mstodo/callback`
5. Enable "Accounts in any organizational directory and personal Microsoft accounts"

### 4. Build and Run

Clone the repository and build with your OAuth credentials:

```bash
git clone https://github.com/openza/tasks.git
cd tasks

# Install dependencies
flutter pub get

# Run in development
flutter run -d linux \
  --dart-define=TODOIST_CLIENT_ID=your_client_id \
  --dart-define=TODOIST_CLIENT_SECRET=your_client_secret

# Build release
flutter build linux --release \
  --dart-define=TODOIST_CLIENT_ID=your_client_id \
  --dart-define=TODOIST_CLIENT_SECRET=your_client_secret
```

#### Environment Variables

| Variable | Required | Description |
|----------|----------|-------------|
| `TODOIST_CLIENT_ID` | Yes | Todoist OAuth Client ID |
| `TODOIST_CLIENT_SECRET` | Yes | Todoist OAuth Client Secret |
| `MSTODO_CLIENT_ID` | No | Microsoft Azure App Client ID |
| `MSTODO_TENANT_ID` | No | Microsoft Azure Tenant ID (default: `common`) |

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
flutter run -d linux --dart-define=TODOIST_CLIENT_ID=xxx --dart-define=TODOIST_CLIENT_SECRET=xxx
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
