# ClipScope

A Windows-first, local-first clipboard history utility that remembers where a clip
came from. C# / .NET 10 / WinUI 3 / Windows App SDK / SQLite FTS5. No Electron,
accounts, telemetry, cloud backend, AI, or runtime network service.

**Status:** Phase 1 foundation implemented; Windows desktop acceptance testing is
still required. This is a draft, not a production release. See
[validation](docs/VALIDATION.md) for what has actually been verified.

## Use

Launch ClipScope, copy content in another application, then press **Ctrl + Shift + V**
to search and restore history. Enter or double-click pastes the selection to the
previous application. Copy restores the clipboard without switching applications.
Use the source/detail pane to inspect timestamps, process, window, formats and images.

- Text (Unicode and legacy ANSI), CF_HTML, RTF, DIB images, copied files/folders.
- Source application icon/name, process/PID/path, top-level window/title, monitor,
  UTC timestamp and original local offset.
- All / Text / Images / Files / Pinned filters; debounced FTS5 prefix search across
  copied text, source information and file paths. Virtualized, paginated results.
- Optional, window-only source screenshots and thumbnails, off by default.
- Pin, delete, retention, media budget and orphan cleanup; persisted local settings.
- Tray: Open, Pause/Resume, Settings, Exit. Closing hides to tray by default.
- Configurable shortcut, startup registration, exclusions and System/Light/Dark themes.

A shortcut conflict produces an in-app notice. Open Settings from the tray and
choose another shortcut. If Windows refuses automatic paste (for example into an
elevated application), the content stays on the clipboard for manual Ctrl+V.

## Requirements and build

Windows 11 x64, the .NET SDK pinned in `global.json`, and the Microsoft Visual C++
x64 runtime used by WinUI. Visual Studio with Windows application development tools
is useful but the projects can be built from the .NET command line. Dependencies
are pinned and their resolved graphs/checksums are committed in lockfiles.

From the repository root, in PowerShell:

```powershell
./scripts/validate.ps1
& ./src/ClipScope.App/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/ClipScope.App.exe
```

The build copies the required Windows App SDK native payload beside the executable.
.NET itself is framework-dependent in this development build. No installer or
release is produced. Run the executable from its output directory, keeping all
adjacent files. Do not run as administrator.

Portable Core/Infrastructure tests also run on Linux/macOS with .NET 10:

```sh
dotnet restore tests/ClipScope.Core.Tests --locked-mode
dotnet restore tests/ClipScope.Infrastructure.Tests --locked-mode
dotnet test --project tests/ClipScope.Core.Tests
dotnet test --project tests/ClipScope.Infrastructure.Tests
```

The full desktop build requires Windows (`mt.exe` and native Windows resources).
A Linux C# compilation with deployment steps disabled does not establish Windows
startup or behavior. GitHub Actions includes full Windows builds and portable tests.

An optional Windows UI smoke script, `scripts/smoke-windows.ps1`, uses UI Automation
and a synthetic Forms clipboard producer to check launch, capture, source-window
attribution, screenshot storage, restoration, pinning, search, hotkey/paste and
deletion/media cleanup. Run it only in a fresh isolated Windows user
profile; it refuses to touch existing ClipScope data. CI reports a distinct skip
notice if its runner has no interactive desktop or cannot activate the producer.
It does not replace the manual Windows 11 checklist.

## Architecture

| Project | Responsibility |
| --- | --- |
| `ClipScope.Core` | Models, privacy, limits, hashing, dedup and search contracts; no UI dependencies |
| `ClipScope.Infrastructure` | SQLite migrations/FTS5, repositories, atomic settings, media, retention and diagnostics |
| `ClipScope.Windows` | Dedicated STA message pump, clipboard, context, screenshots, hotkeys, tray, paste and startup |
| `ClipScope.App` | Programmatic WinUI 3 layout, search/history/detail UI and settings |

See [architecture and ownership rules](docs/ARCHITECTURE.md),
[privacy](docs/PRIVACY.md), [Windows acceptance checklist](docs/VALIDATION.md), and
[Phase 2 roadmap](docs/ROADMAP.md).

## Privacy and storage

Data stays in `%LocalAppData%\ClipScope\`: `clips.db`, `settings.json`, `media\`,
`thumbnails\`, and bounded `logs\`. The database and media are **not encrypted**;
Windows user-profile permissions and disk encryption provide the local boundary.
Clipboard contents, source titles and optional screenshots can be sensitive.

Screenshots default off. Exclude applications by verified executable path or
explicit process name in Settings (one per line). Consider your installed password
manager and credential tools; verify their real executable identities. Unknown
source processes are skipped, and privacy-marked clipboard content is skipped.
Exclusions apply before reading clipboard data and again before persistence and
screenshots. Pause recording quickly from the tray. Existing history is not erased
by adding an exclusion. Pinned clips survive ordinary retention and may exceed the
media budget; delete/unpin them explicitly when needed.
