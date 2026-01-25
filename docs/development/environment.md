# Development Environment

This project targets Flutter (Dart) with a Rust sync engine. Use the latest stable toolchains where possible.

## Toolchains

- Flutter: 3.7+ (stable)
- Dart: comes with Flutter
- Rust: stable (rustup)
- Node.js: 20+ (for website docs)
- pnpm: 9+

## Linux Dependencies

```bash
sudo apt-get install -y clang cmake ninja-build pkg-config libgtk-3-dev liblzma-dev libstdc++-12-dev
```

## Optional Local Hooks

To enable repo hooks:

```bash
git config core.hooksPath .githooks
```

The default pre-commit hook runs gitleaks and Dart formatting checks.
