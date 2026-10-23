# Premium UI engineering review — PR #2

Scope: the complete diff from `main` (`ff7305d`) to the premium UI baseline
`080313d`, followed by the hardening commits on `feat/premium-ui-v1`. The existing
presentation structure and native capture/restore systems are retained. No Phase 2
features, replacement PR, installer or merge is included.

## Findings and fixes

| Severity | Finding at the reviewed baseline | Resolution / principal files |
| --- | --- | --- |
| High | Search freshness compared text alone. Pin/Delete used shared detail state, and an awaited detail load could retarget a command after selection changed. Filter/page changes and failed/cancelled searches could leave old rows actionable. | All entry points use one command resolver with search and selection generation fences, immutable clip/destination targets and a command busy guard. Pending/failed results are disabled; detail actions disappear immediately. CTS ownership stays with each operation. `MainWindow.cs`, `MainWindow.Layout.cs`, `HistorySession.cs`. |
| High | Physical-only modifier polling could observe Ctrl released before a queued Ctrl+Enter handler and paste instead of copying. | Commands retain this window’s queued Ctrl key events with a physical fallback; activation reconciles the held state so a key-up missed in the source app cannot leave Ctrl stuck. The smoke checks both Ctrl+Enter and ordinary Enter after global activation. `MainWindow.Layout.cs`. |
| High | Settings could be dismissed/reopened while Save continued. The old shortcut was released before persistence and rollback results were ignored. Startup rollback recreated the preference instead of restoring the original registry value. Tray changes could be overwritten by a stale draft. | Save prevents page dismissal/exit until completion, serializes settings mutations, merges unstaged capture changes, reserves the new shortcut while retaining the old registration, and retains the exact startup value/kind until persistence succeeds. Theme captions follow the preview. Rollback failures are explicit; a subsequent Save forces startup reconciliation. `MainWindow.Settings.cs`, `SettingsTransaction.cs`, `NativeHost.cs`, `StartupIntegration.cs`, `LocalStorage.cs`. |
| High | The workflow recursively deleted `%LocalAppData%/ClipScope` before the capture script's existing-profile check. Fixed fixture paths could overwrite unrelated temp files. | Removed the blanket deletion. Both scripts require an isolated GitHub-hosted interactive runner, refuse existing profiles and clean only directories they created, with matching ownership markers and reparse-point checks. Temp fixtures use unique directories. Artifact upload and capture remain non-blocking. `build.yml`, `windows-test-profile.ps1`, desktop scripts. |
| Medium | Clear history deleted media from an earlier maintenance snapshot. A screenshot attached between enumeration and deletion could be orphaned. | Conditional deletion rechecks pinned state and returns current media references under the writer gate and SQLite transaction. Clear and retention use that result; retention refreshes its orphan reference set. FTS deletion triggers remain intact. `Models.cs`, `SqliteClipRepository.cs`, `RetentionService.cs`, `MainWindow.Settings.cs`. |
| Medium | URI image loading could miss an early `ImageOpened` event and leave the preview transparent. File-backed images/header reads had weak retirement/deletion behavior. Width-only decode bounds did not bound extreme aspect ratios. | Await decoding from short-lived delete-sharing streams, validate PNG dimensions, bound both decoded dimensions without upscaling, cancel retired loads, dispose row loads and clear old image sources. Missing/corrupt previews show a semantic warning. `StoredImage.cs`, `ClipRow.cs`, `ClipDetailView.cs`, `PngPreview.cs`. |
| Medium | Minimum window sizes and natural image sizes assumed startup DPI. | Observe `XamlRoot` scale changes and monitor/presenter movement, update pixel constraints against the current work area, and rescale selected images. Detach the scale handler on close. `MainWindow.Layout.cs`, `WindowGeometry.cs`, `ClipDetailView.cs`. |
| Medium | Pending operations/timer could outlive shutdown; shared CTS objects were disposed by their successor. | Stop the timer, invalidate/cancel pending work, reject activation/commands during exit, unsubscribe capture delivery and let operations dispose their own tokens. `MainWindow.cs`. |
| Low | Source snapshots included invisible resize borders. | Crop the existing PrintWindow bitmap to contained DWM extended-frame bounds. Unavailable, empty or expanding bounds keep the original window-only capture. There is no desktop fallback. `WindowImages.cs`, `Native.cs`, `WindowGeometry.cs`. |
| Low | Live-region properties alone did not explicitly announce status changes. | Raise live-region events for history status, settings errors and clear completion. Required smoke automation names are preserved. `MainWindow.Layout.cs`, `MainWindow.Settings.cs`. |

A tray Pause request now carries the requested state through queued saves, and
Resume takes effect only after persistence. A failed pause remains paused for that
session and reports the persistence failure. No implementation can guarantee an
atomic transaction across a settings file and arbitrary failing Windows registry
I/O: an unrecoverable registry rollback is surfaced as a recovery error rather
than silently reported as a successful Save.

## Areas with no additional issue found

- **Clipboard listener and format capture:** no redesign regression found in the
  STA message pump, format limits, private/own-restore filtering or retry behavior.
- **Attribution, exclusions and screenshot privacy:** source PID/window checks,
  pre/post-capture exclusion checks and protected-window rejection are preserved.
- **SQLite/FTS5:** parameterization, search grammar, paging, migrations and FTS
  triggers are preserved; the clear workflow uses the existing repository writer
  serialization and adds an atomic media-reference read.
- **Media path containment:** no additional defect found in the product path
  resolver; copied file-list paths are not media references and are never deleted
  by clear/retention. Existing containment/orphan tests remain green.
- **Global activation, second instance, tray and paste:** no additional defect
  found beyond the command/settings races fixed above. Modifier handling retains this window’s key events with physical fallback and activation reconciliation; the clipboard/paste implementation remains.
- **Runtime XAML:** resources load inside the startup exception boundary before
  native capture starts. Startup failure uses the existing diagnostic/fallback
  window. Template bindings and semantic theme resources resolve in Windows CI.
  No missing-resource or malformed-XAML defect was found in the reviewed literals.
- **Mica, theme resources and custom TitleBar:** no additional code defect found.
  Windows Server exercises solid fallback. Windows 11 Mica, drag regions and
  high-contrast appearance still require physical review.
- **Accessibility structure:** useful names, heading levels, navigation/search/main
  landmarks and native control semantics remain. No required smoke name changed.
  Narrator output and full focus order still require physical review.

## Validation

The 77-test baseline was rerun before editing: Core 56, Infrastructure 21, all
passed. The hardened suite has **97 tests**: Core 69, Infrastructure 28. The 20
new cases cover command generation/selection races, DWM crop/fallback coordinates,
100/125/150/200% size policies, PNG decode bounds, settings failure/rollback, late
screenshot deletion and pin/capture boundaries during clear.

Local Release diagnostic app build passes with warnings as errors (0 warnings,
0 errors). Linux diagnostic compilation disables Windows manifest/PRI tooling;
only the real Windows workflow validates the full desktop Release build.

Windows functional smoke retains the original capture/context/screenshot/copy/pin/
FTS/global shortcut/paste/delete/media checks and adds immediate search commands,
Ctrl+Enter, privacy Cancel, a locked settings file forcing real startup/hotkey
rollback, retry and shortcut replacement, and clear-history confirmation preserving
a pinned clip. A separate script checks existing-profile refusal, owner mismatch,
junction refusal and owned-directory cleanup.

CI screenshot capture exercises 18 synthetic states: both themes, empty/populated,
text/code/image/files/snapshot, pinned, search/no results, settings and narrow/wide
windows. Window-only rendering rejects flat/black results. Capture and upload are
review aids with non-blocking failures. Artifact downloads from the Azure storage
host are blocked by this cloud environment's network policy; fresh captures have
not been visually inspected here. The four baseline representative PNGs remain
labelled as baseline evidence in `docs/screenshots/`.

Windows evidence and final outcomes are recorded in [VALIDATION.md](VALIDATION.md).

## Remaining Windows 11 acceptance and recommendation

Physical checks remain: Mica and title-bar drag/snap behavior; 125/150/200% and
mixed-monitor movement/maximize/restore; constrained work areas; high contrast;
Narrator/focus order; touch; foreground handoff to real third-party apps; startup
at sign-in; protected/GPU sources; and all-day memory/handle stability. Geometry
unit tests and CI screenshots at 100% do not substitute for these checks.

With green Windows build, tests, functional smoke and profile-safety checks, this
PR is suitable to merge as a Windows 11 candidate. Production release acceptance
remains pending the physical checks. PR #2 remains draft and unmerged.

Engineering readiness: **94/100**. UI readiness: **87/100**. Release readiness:
**74/100**. These are engineering estimates, not automated metrics.


## Files changed during hardening

```text
.github/workflows/build.yml
docs/ARCHITECTURE.md
docs/PREMIUM_UI_REVIEW.md
docs/VALIDATION.md
scripts/capture-ui-windows.ps1
scripts/smoke-windows.ps1
scripts/test-windows-test-profile.ps1
scripts/windows-test-profile.ps1
src/ClipScope.App/ClipDetailView.cs
src/ClipScope.App/ClipRow.cs
src/ClipScope.App/MainWindow.Layout.cs
src/ClipScope.App/MainWindow.Settings.cs
src/ClipScope.App/MainWindow.cs
src/ClipScope.App/StoredImage.cs
src/ClipScope.Core/HistorySession.cs
src/ClipScope.Core/Models.cs
src/ClipScope.Core/PngPreview.cs
src/ClipScope.Core/WindowGeometry.cs
src/ClipScope.Infrastructure/LocalStorage.cs
src/ClipScope.Infrastructure/RetentionService.cs
src/ClipScope.Infrastructure/SettingsTransaction.cs
src/ClipScope.Infrastructure/SqliteClipRepository.cs
src/ClipScope.Windows/Native.cs
src/ClipScope.Windows/NativeHost.cs
src/ClipScope.Windows/StartupIntegration.cs
src/ClipScope.Windows/WindowImages.cs
tests/ClipScope.Core.Tests/HistorySessionTests.cs
tests/ClipScope.Core.Tests/PngPreviewTests.cs
tests/ClipScope.Core.Tests/WindowGeometryTests.cs
tests/ClipScope.Infrastructure.Tests/SettingsTransactionTests.cs
tests/ClipScope.Infrastructure.Tests/StorageTests.cs
```
