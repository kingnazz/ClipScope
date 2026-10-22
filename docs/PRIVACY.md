# Privacy

ClipScope makes no runtime network requests. There is no account, cloud sync,
telemetry, analytics, advertising, AI, or model download. NuGet and GitHub are
build/development dependencies only.

## Recorded data

When capture is enabled, supported clipboard representations are saved: text,
HTML (including its original headers and potentially SourceURL), RTF, images,
file/folder paths, and clipboard format names. Clipboard file contents are not
opened or copied. Source context includes timestamp/offset, application/executable,
PID/path where available, HWND/window title, monitor and extracted application icon.
Titles and paths can contain confidential information. These are searchable.

Optional screenshots can record **everything visible inside the source window**,
not just the copied fragment. Screenshots default off, capture no entire-desktop
fallback and are skipped for protected/inaccessible/changed windows when detected.
Successful native capture can still yield a blank image for some applications.

ClipScope does not record keystrokes, audio, webcam, browsing history, background
activity sessions or other applications' document contents. It does not inject code
or scrape accessibility trees. Phase 1 obtains document context only from copied
file paths and the ordinary source window title. Copying a password from an
unexcluded application can save it: clipboard managers cannot reliably identify
all secrets. Review exclusions before using this with sensitive workflows.

## Exclusions and pause

Settings accepts full executable paths or explicit process names, one per line.
Paths are preferred to distinguish executables with identical names. Verify the
installed binary in Task Manager rather than guessing. Suggested apps to check:
1Password, Bitwarden, KeePass, KeePassXC and credential-related tools. These names
are suggestions, not assumptions or automatically enabled path rules.

Foreground and clipboard owner must both pass exclusions before clipboard content
is read. Unknown sources fail closed. If a process path is inaccessible, matching
an excluded path's executable basename is conservatively treated as excluded.
The Windows `ExcludeClipboardContentFromMonitorProcessing` and
`CanIncludeInClipboardHistory=0` hints are respected. Settings are checked again
before saving and before/after screenshots. Excluded events are not persisted as
metadata-only entries. Exclusions do not retroactively delete existing history.
Use Pause from the tray to stop recording quickly; the paused state persists.

## Local storage and deletion

`%LocalAppData%\ClipScope\` contains:

- `clips.db` (+ SQLite WAL/SHM): clip content, search index and source metadata.
- `settings.json`: preferences and exclusion identities.
- `media\`, `thumbnails\`: clipboard images, context screenshots and app icons.
- `logs\`: rotating, content-free event diagnostics; no raw exception messages.

Storage is not encrypted by ClipScope. Any process running as the same user may
be able to read it. Use Windows profile controls and device/disk encryption.
Retention deletes old unpinned clips, then old unpinned media for the budget.
Pinned clips can exceed limits. Delete removes database/search records and media;
settings can clear all unpinned history with confirmation. Temporary orphan media
has a one-hour cleanup grace period. Files already missing do not prevent deletion.
Neither normal deletion nor SQLite maintenance guarantees forensic secure erasure,
including copies retained by OS backups or file history.
