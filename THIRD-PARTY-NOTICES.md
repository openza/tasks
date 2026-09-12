# Third-party notices

Openza Tasks depends on third-party packages and platform components. This file summarizes direct dependencies used by the repository; package metadata and distributed license files remain the source of truth for each dependency.

## Runtime and app dependencies

| Component | Version | License metadata | Purpose |
| --- | --- | --- | --- |
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent | 12.1.0 | MIT | Cross-platform desktop UI |
| Avalonia.Fonts.Inter | 12.1.0 | MIT package; Inter font is SIL OFL-1.1 | Bundled UI font |
| FluentIcons.Avalonia | 2.1.335 | MIT | Fluent UI icons |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | MVVM helpers |
| CommunityToolkit.WinUI.Controls.SettingsControls | 8.2.250402 | MIT | Native settings controls |
| Microsoft.Identity.Client | 4.84.0 | MIT | Microsoft account authentication |
| Microsoft.Identity.Client.Broker | 4.84.0 | MIT | Windows authentication broker integration |
| Microsoft.WindowsAppSDK | 2.0.1 | Package license file | WinUI 3 and Windows App SDK runtime |
| Microsoft.Data.Sqlite | 10.0.0 | MIT | SQLite data access |
| Microsoft.Extensions.Logging.Abstractions | 10.0.0 | MIT | Logging abstractions |
| System.CommandLine | 2.0.11 | MIT | CLI parsing, help, and completion support |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5 | Apache-2.0; bundled SQLite is public domain | Native SQLite bundle |
| System.Security.Cryptography.ProtectedData | 10.0.7 | MIT | Windows protected-data integration |

## Test dependencies

| Component | Version | License metadata | Purpose |
| --- | --- | --- | --- |
| Avalonia.Headless | 12.1.0 | MIT | Headless desktop interaction tests |
| xUnit.net | 2.9.3 | Apache-2.0 | Unit testing |
| xUnit.net Visual Studio runner | 3.1.5 | Apache-2.0 | Test discovery and execution |
| Microsoft.NET.Test.Sdk | 18.8.1 | MIT | Test SDK |
| coverlet.collector | 10.0.1 | MIT | Test coverage collection |

## Font and platform notices

The Inter font is copyright the Inter Project Authors and distributed under the [SIL Open Font License 1.1](https://github.com/rsms/inter/blob/master/LICENSE.txt). The NuGet package's MIT license covers its packaging code; it does not replace the font license.

The self-contained apps also include the .NET runtime, native SQLite and rendering libraries, and their transitive dependencies. Snap runtime packages and content snaps include additional platform components. Their own package metadata and distributed notices apply; the direct-dependency table above is not a complete transitive license inventory.

## Project-owned assets

Project-created screenshots are available under the repository's MIT License. Openza names, logos, and official app icons are reserved as described in [BRAND.md](BRAND.md).
