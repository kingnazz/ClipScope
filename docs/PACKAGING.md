# RC1 packaging

ClipScope targets Windows 11 x64. `Version.props` owns its semantic version,
numeric assembly/file versions and development publisher metadata. The EXE,
generated Win32 manifest, Settings version label, installer, Apps & Features
entry and artifact manifest derive from it. RC sequence becomes the fourth
numeric file-version component. `ClipScope.Desktop` is the stable AppUserModelID,
shortcut identity and installer registration ID. This is an unpackaged app:
AppUserModelID does not grant MSIX package identity.

## Decision: per-user Inno Setup EXE

MSIX supports full-trust WinUI desktop processes, tray icons, hotkeys, clipboard
listeners and native capture. Those APIs alone do not rule it out. Its identity
and lifecycle would, however, change this existing application's deployment:

| Requirement | MSIX consequence | RC1 choice |
| --- | --- | --- |
| Existing unvirtualized `%LocalAppData%\ClipScope` | Requires an explicit data/virtualization migration policy and tests | Preserve the exact existing location |
| Existing transactional HKCU Run preference | Packaged startup should use StartupTask and migrate the existing Run entry; versioned WindowsApps paths cannot be retained in Run | Keep one stable Run value, rewrite its path on install and enabled app startup |
| Normal per-user development installation without credentials, PowerShell or admin | A sideloaded development MSIX normally needs a trusted signing chain/certificate provisioning | Unsigned development EXE installation is possible; physical SmartScreen behavior remains unverified |
| No manually installed runtimes | MSIX can use framework dependencies or self-contained payloads | Ship app-local .NET, WinAppSDK and release VC CRT |

Inno Setup avoids making identity/startup/data migration an unverified RC1
requirement, while preserving the established native application architecture.
It is not a workaround for unsupported MSIX clipboard/tray APIs. Revisit MSIX
when a trusted distribution identity and tested startup/data migration exist.
There is no updater service, browser wrapper or Phase 2 feature.

References: [packaged desktop deployment](https://learn.microsoft.com/windows/apps/package-and-deploy/),
[Windows App SDK self-contained deployment](https://learn.microsoft.com/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps),
[MSIX desktop runtime behavior](https://learn.microsoft.com/windows/msix/desktop/desktop-to-uwp-behind-the-scenes),
[startup tasks](https://learn.microsoft.com/windows/apps/develop/launch/launch-on-startup),
[Visual C++ app-local redistribution](https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files),
[Inno Setup](https://jrsoftware.org/isinfo.php).

## Installed layout and lifecycle

| Location | Contents / policy |
| --- | --- |
| `%LocalAppData%\Programs\ClipScope` | EXE, app-local runtime DLLs, resources, multi-size icon, dependency notices, uninstaller and installed-file manifest |
| `%LocalAppData%\ClipScope` | Existing `clips.db`, `settings.json`, `media`, `thumbnails`, `logs`; never inside the install directory |
| User Start Menu | One ClipScope shortcut, consistent AppUserModelID and icon |
| User Desktop | Only when the unchecked optional installer task is selected |
| HKCU `...\CurrentVersion\Run\ClipScope` | One quoted installed EXE path plus `--background`; no scheduled task, Startup-folder link or second mechanism |

The manifest requests ordinary user privileges. Inno `PrivilegesRequired=lowest`
installs for the current user without elevating. Windows 11 build 22000 or later
is the installer minimum; server CI is only an engineering test environment.

An upgrade/repair gracefully requests exit from the native host, waits for
process termination, and refuses to overwrite a running instance that cannot
finish a Settings save. `--shutdown` is a maintenance command with an exit status;
it does not kill or launch the main application. The old current-main build has
no maintenance command: exit that development build from its tray before installing.

Install over the same directory to repair. Previously recorded payload files
that no longer ship are removed; unrelated files are not adopted or erased.
Install directories containing junctions/symlinks are rejected. The installer
migrates an existing startup entry to the installed EXE, including an old
development output path. When the persisted startup preference is enabled,
launching a moved EXE repairs that same entry to its current path. Settings Save
continues to enable/disable it transactionally. Quiet duplicate launches do not
activate an already running instance; foreground launches activate its history.

Uninstall removes installed payloads, owned shortcuts and installer registration.
It removes the startup entry only when it still points to that installation.
**User data is preserved by default**, including silent uninstall. Interactive
uninstall asks separately about permanent removal, defaulting to No. Explicit
automation may opt in with `/PURGEUSERDATA=1`; users need no command line.
Purge refuses trees containing reparse points and preserves them instead.
Reinstall uses the retained profile. No database schema migration is introduced.

## Build the installer (developers only)

On Windows with the SDK pinned in `global.json`, licensed Visual Studio release
VC redistribution files, PowerShell 7, and Inno Setup **6.7.1**:

```powershell
./scripts/package-rc.ps1
```

It runs the existing locked restore, Release build and both test suites, publishes
`win-x64 --self-contained true` with WindowsAppSDKSelfContained enabled, copies
the x64 **release** CRT from Visual Studio's redistributable directory, and checks
runtime files, EXE version and a self-contained runtimeconfig. It retains the
runtime licenses/notices in the payload. Windows supplies its supported UCRT.
No VC redistributable bootstrapper or runtime installer is run on the user's PC.
No .NET trimming or single-file extraction is used for WinUI's native resources.
The script carries the regular Windows build's merged `ClipScope.App.pri` into
the publish payload explicitly: this SDK's unpackaged publish path omits the
PRI-generation targets. Framework PRI files alone are insufficient. An installed
smoke with the source tree unavailable verifies the complete deployment.

`artifacts/rc` contains the versioned setup EXE, `SHA256SUMS.txt`, a per-file
`build-manifest.json` and CI evidence. Existing staging directories are refused;
use a new output directory for another local build. Compiler/SDK/source SHA and
payload hashes are recorded. Dependencies and diagnostic tools are pinned in
lockfiles/tool manifests. `Version.props` also pins the validated .NET runtime,
release CRT and installer compiler versions; packaging rejects unexpected versions.
Rebuilding from the same source/toolchain is supported;
**bit-identical installer output is not established** (compiler/signature timestamp
and hosted image changes must be accounted for). Verify hashes of downloaded
artifacts against the manifest from the same trusted workflow run.

The workflow has read-only repository permissions and only uploads Actions
artifacts. It never creates a Release or merges a PR. Manual dispatch is available
after the workflow exists on the default branch. The RC branch path-filtered
push trigger bootstraps testing of this new workflow; remove that temporary
branch trigger when RC1 is accepted. Regular build/test/smoke CI remains separate.

## Signing-ready, unsigned by default

No signing credential is required or committed. Before public distribution, use
a publicly trusted organizational Authenticode code-signing certificate or a
compatible managed/HSM signing service. Protect its private key; the development
publisher is a placeholder, not a certificate identity.

```powershell
# Trusted release machine only; certificate is already in CurrentUser\My.
$env:CLIPSCOPE_SIGNING_THUMBPRINT = '<certificate selector, not a private key>'
$env:CLIPSCOPE_TIMESTAMP_URL = 'https://<your RFC3161 timestamp service>'
./scripts/package-rc.ps1 -SigningCommand ./scripts/sign-release.ps1
```

The hook signs ClipScope's EXE before packaging, Inno signs the embedded
uninstaller through the same hook, then the completed installer is signed.
The example uses SHA-256 file digests and RFC 3161 SHA-256 timestamping, verifies
the trust chain and requires a timestamp. Use Windows SDK `signtool.exe` on PATH.
A trusted custom hook with the same `-Path` contract can use managed signing.
Keep Microsoft/runtime signatures intact. Revalidate fresh install, upgrade,
uninstall and SmartScreen with the signed artifact: unsigned CI cannot verify
certificate provisioning, signing credentials or reputation.

Replace the SVG and regenerate all icon sizes using [the icon pipeline](../assets/README.md).
The checked-in ICO is an engineering placeholder; no final brand identity or
production legal terms have been invented.

## Automated versus physical acceptance

`test-installer.ps1`, `compat-windows.ps1`, `stress-windows.ps1` and
`benchmark-search.ps1` are developer automation, never shipped. They refuse
non-hosted desktops and pre-existing profiles/installs. The baseline upgrade uses
the exact recorded pre-RC main SHA; an additional lower-version installer is an
explicit synthetic predecessor fixture, not an invented released RC.

Stress defaults to 20 minutes/2,000 text and image operations, with source
snapshots on for half. Manual workflow input permits up to 180 minutes.
It records private working set, private bytes, handles, GDI/USER counts,
EventCounters managed heap, database/media growth, idle CPU and latency samples.
It does not declare leak thresholds before observing results. Repository FTS
benchmarks measure 100 and 10,000 clips separately from UI debounce/render.
All physical checks and measured results belong in [RC1_ACCEPTANCE.md](RC1_ACCEPTANCE.md).
