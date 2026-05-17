# Microsoft Store Submission Notes

Openza Tasks is Store-first for WinUI V1.

## Package Defaults

- App name: Openza Tasks
- Package identity: Openza.OpenzaTasks
- Architecture: x64
- Target OS: Windows 10 22H2+ and Windows 11
- Restricted capability: `runFullTrust`, required by packaged WinUI desktop apps

## Packaging

Use Visual Studio's Store packaging flow after associating the project with Partner Center. Do not commit generated certificates, `.msixupload` files, or `AppPackages/` output.

## Privacy

Use `PRIVACY.md` as the public privacy policy URL. The app does not collect telemetry or analytics in V1.
