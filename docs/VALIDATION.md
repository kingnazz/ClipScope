# Validation and release gate

## Evidence from cloud validation and Windows CI (2026-10-22)

Linux x64 and GitHub Actions Windows runner, .NET SDK 10.0.402 / runtime 10.0.13.
Dependencies restored in locked mode. Full Windows evidence: [CI run 39823053915](https://github.com/kingnazz/ClipScope/actions/runs/39823053915),
implementation commit `a4fe6cb50b5f1b1ac8d81f8ed7becb9c3dafd868`.

| Check | Observed result |
| --- | --- |
| Core policy tests | 17 passed, 0 failed, 0 skipped |
| Infrastructure integration tests, real SQLite/FTS5 and filesystem | 21 passed, 0 failed, 0 skipped |
| Core / Infrastructure / Windows builds | Passed; no warnings or errors |
| WinUI C# diagnostic build | Passed; no warnings or errors |
| Full Release desktop/solution build with app-local SDK payload/manifest | Passed on Windows CI with warnings treated as errors; 0 warnings, 0 errors |
| Automated tests on Windows CI | 38 passed, 0 failed, 0 skipped |
| Default full desktop build on Linux | Unsupported: Windows `mt.exe` exec format error; use Windows |
| Windows desktop functional smoke | Passed: launch, text capture, source window, screenshot PNG, restoration, pin, FTS search, global shortcut, focus/paste, deletion and media cleanup |
| Windows 11 visual/compatibility/long-session acceptance | Not run; physical review remains required |
| New cloud-task snapshot restoration | Not verified |

The C# diagnostic build used:

```sh
dotnet build src/ClipScope.App -p:Platform=x64 \
  -p:WindowsAppSDKSelfContained=false -p:AppxGeneratePriEnabled=false -warnaserror
```

These flags only allow compilation in Linux. They are not the Windows deployment
configuration and do not prove that the application launches. The committed
PowerShell validation script and the passing Windows CI use the real default configuration.
No warnings are suppressed globally. xUnit tests use .NET 10's Microsoft Testing
Platform and honor test cancellation.

Tests cover text normalization, rich/image/file hashing, recent/context-aware
dedup, executable/path exclusions, unknown identity handling, UTF-8 CF_HTML offsets,
malformed HTML, settings limits, migrations/reopen, concurrent insert/dedup, all
search sources, filters, hostile search syntax, pagination/cancellation, deletion
and FTS synchronization, late context attachment, age and budget eviction, pinned
budget overflow, pin/cleanup race prevention, settings corruption/serialization,
orphan grace/path containment and content-free diagnostic log rotation.

## Premium UI pass (2026-10-23, branch `feat/premium-ui-v1`)

Windows evidence comes from GitHub Actions `windows-latest` runs of `scripts/validate.ps1`,
`scripts/smoke-windows.ps1` and the new `scripts/capture-ui-windows.ps1` on the PR head.

| Check | Observed result |
| --- | --- |
| Full Release desktop/solution build, warnings as errors | Passed on Windows CI; 0 warnings, 0 errors |
| Core tests (policies + new presentation helpers) | 56 passed, 0 failed (17 existing + 39 new cases) |
| Infrastructure tests (real SQLite/FTS5) | 21 passed, 0 failed (one test extended for the snapshot flag) |
| Automated tests on Windows CI | 77 passed, 0 failed, 0 skipped (baseline was 38) |
| Windows desktop functional smoke | Passed against the redesigned UI (same coverage as before) |
| UI screenshot capture | 18 screenshots: dark/light, empty, populated, text, code, image, files, source snapshot, pinned, search, no results, settings pages, ~900 px and ~1700 px widths |
| Linux C# diagnostic build of the WinUI app | Passed; 0 warnings, 0 errors |

The smoke script now retries a transient `ExternalException` from `Clipboard.GetText`
while another process (including ClipScope checking its own restore) holds the
clipboard; the assertion and deadline are unchanged. Smoke also caught a real
regression during the pass (Enter copying instead of pasting after the global
shortcut because of stale thread key state), fixed by reading physical key state.

Screenshots were reviewed on a 1920×1080, 100% scale runner (Windows Server, where
Mica falls back to a solid backdrop). Not verified: Windows 11 Mica appearance,
high-DPI/mixed-DPI monitors, high contrast, Narrator/screen-reader output, touch,
real third-party source applications and long-running sessions. These require a
physical Windows 11 review.

## Desktop functional evidence

`scripts/smoke-windows.ps1` ran successfully (not skipped) on the Windows CI runner.
It starts a real WinUI process and a synthetic WinForms source, verifies text in
history and source-window attribution, validates a window-sized screenshot PNG,
restores the clipboard, pins, searches, invokes Ctrl+Shift+V and Enter, verifies
pasted text in the external source application, deletes the clip, and confirms
media cleanup. A fresh profile is mandatory; existing ClipScope data is protected.
The script reports an explicit skip when no interactive desktop/foreground access
exists; neither skip occurred in the linked successful run.

This check exposed and led to correction of code-first WinUI metadata/resource
initialization. Compilation alone had not detected that startup failure. The
current application explicitly initializes XamlControlsXamlMetaDataProvider and
implements IXamlMetadataProvider before loading control resources.

## Required Windows 11 acceptance checks

Run `scripts/validate.ps1`, then launch the actual built executable. Record Windows
build, display scale, application versions and pass/fail evidence. These checks
remain open until exercised on an interactive Windows desktop.

1. Launch/exit/relaunch; verify the second instance activates the first and history,
   exclusions, shortcut and paused state survive restart.
2. Copy exact Unicode/emoji, multiline/whitespace, ANSI text, Word RTF and browser
   HTML (including non-ASCII fragments); verify original content on restoration.
3. Copy PNG/bitmap/DIBV5 with alpha, top-down images, and Explorer files/folders;
   verify previews, correct content types and paste into compatible targets.
4. Copy a value in Notepad, inspect app/PID/path/title/monitor/icon and both UTC and
   original local offset. Repeat from a different window/application and later;
   ensure useful context survives dedup.
5. Hold the clipboard open in a test producer, then release; verify bounded retry
   and continued operation. Exercise delayed rendering, producer exit and rapid
   copy bursts. Drops under overload should be bounded, observable in diagnostics,
   and never mixed with the wrong sequence/context.
6. Verify maximum sizes skip oversized representations; malformed HTML, malformed
   image headers and inaccessible processes do not crash the app.
7. Enable screenshots; verify only the source window is captured after storage,
   thumbnails render, alpha is opaque, other windows/desktop are not included and
   native resources remain stable. Test GPU apps, minimized/protected windows,
   title/foreground changes and source process exit; missing screenshots are allowed.
8. Exclude a verified executable and repeat from foreground and owner windows.
   Inspect database, FTS and media to confirm neither content nor context is stored.
   Confirm Windows monitor-exclusion hints and Pause prevent capture and screenshots.
9. Test Ctrl+Shift+V with current foreground destination, Enter/double click, manual
   Copy, history restoration suppression, held modifiers, hotkey conflict/change,
   elevated/blocked target and target exit. A failed paste must leave usable clipboard
   content and communicate manual Ctrl+V without sending to a different window.
10. Search by text, filename, title/app/process; test large histories, rapid typing,
    immediate Enter after typing, all filters, pagination and stale-result cancellation.
11. Pin/unpin/delete; exercise retention/budget overflow and pin concurrent with
    cleanup. Delete while context capture is running. Remove/corrupt a media file
    and verify previews/cleanup/other clip actions continue working.
12. Verify all tray commands, hide-on-close, explicit Exit, Explorer restart, and
    failure to create a tray icon. Startup registration must use the actual quoted
    executable, start quietly and be removable from Settings.
13. Test System/Light/Dark themes, keyboard focus/navigation, screen reader names,
    long content, small/resized windows, high DPI/mixed monitors and date/time changes.
14. Observe idle CPU over several minutes: no clipboard polling loop or ongoing
    wake timer should run. Inspect memory and GDI handle counts during repeated
    image/screenshot capture and after long sessions.
15. Test low disk space, transient SQLite locks, invalid settings and newer schema;
    preserve existing data and surface actionable failures without dialog loops.

## Current limitations

- The full Windows build and the functional subset above pass on CI. The broader
  Windows 11 acceptance matrix, native image/HTML/RTF compatibility, visual polish,
  accessibility, DPI, protected windows, tray lifecycle and long-session behavior
  still require hands-on validation.
- PrintWindow is best effort; GPU/protected apps may fail or return a black image.
  Broken native delayed renderers can occupy the clipboard STA or screenshot worker.
- Copy bursts faster than rendering/storage and the bounded queue may lose events;
  there is no attempt to recover replaced clipboard content by invasive means.
- Rich preview is plain text; arbitrary vendor formats, clipboard cut/move effects,
  CF_BITMAP-only owners without a DIB representation and virtual file streams are
  not reconstructed. Explorer path drops are copied, never moved.
- Files are referenced by path, not snapshotted; later file changes/deletion matter.
- Screenshot exclusion is best effort for Windows protection signals; app exclusions
  and pause are the user's control. No claim of universal secret/password detection.
- Data is unencrypted. Pinned media can exceed budget; ordinary deletion is not
  secure erasure. Old orphan media has a one-hour grace period.
- Tray icon is currently the shared Windows application icon. UI accessibility,
  DPI, Mica on Windows 11 and startup behavior need physical review.
- Source snapshots crop contained DWM visible-frame bounds; invalid or unavailable
  DWM bounds retain the original window-only capture. GPU/protected-window capture
  remains best-effort.
- Document context beyond copied paths/window titles and all Phase 2 features are
  deferred. There is no installer, release, auto-update, AI or network service.

**Phase 1 readiness: 85/100.** The implementation, full Windows deployment build,
38 automated tests and the core desktop workflow pass. Broader hands-on Windows 11
acceptance remains necessary before calling the Phase 1 definition of done complete. This score is an engineering estimate, not
an automated test metric.

Representative captures from the premium UI baseline are in `docs/screenshots/`
(synthetic clips; the source window is a test form hosted by Windows PowerShell).
Hardening evidence and remaining checks are in [PREMIUM_UI_REVIEW.md](PREMIUM_UI_REVIEW.md).
