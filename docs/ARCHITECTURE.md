# Architecture

## Capture path

A dedicated STA thread creates an invisible top-level HWND and message pump. It
subscribes with `AddClipboardFormatListener`, never polls. `WM_CLIPBOARDUPDATE`
snapshots the sequence number, local timestamp and source context. `GetClipboardOwner`
plus `GetAncestor(GA_ROOT)` identifies the owner process. If its window is a hidden helper and its process matches the foreground process,
the foreground window supplies the document/window context. A visible owner
retains its specific window, even if foreground has changed within the same process.
Different-process owners retain their own identity. Foreground and owner
processes must both pass privacy checks. Missing owners/unknown processes fail
closed. Process paths use `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` and
`QueryFullProcessImageName`; inaccessible optional metadata is tolerated. Window
title, monitor and cached executable description are obtained with documented APIs.
No hooks, process injection, accessibility scraping or document interrogation.

`OpenClipboard` contention gets at most five delayed retries (10,25,50,100,200 ms),
posted back to the STA. Each verifies the expected sequence. Data is copied into
owned memory under the clipboard lock and the lock is released before persistence.
Unicode, ANSI, HTML, RTF, DIB/DIBV5, CF_HDROP and format names are captured together.
Representations above configured limits are skipped rather than silently truncated.
Native delayed rendering is supported by `GetClipboardData`; Windows may block
that call in a broken producer. Keeping it on a background STA isolates the UI.
Shutdown never waits indefinitely for that thread. No claim is made that every
intermediate value in a faster-than-rendering copy burst can be recovered.

A bounded channel (16 entries, 256 MiB aggregate estimated payload memory) feeds one
persistence worker. Overload drops arrivals and logs a content-free diagnostic.
No history-sized image cache lives in RAM. Image dimensions/stride/header are
validated before GDI+ decoding to PNG and generating a 320-pixel thumbnail.
Media filenames are derived solely from clip GUID + fixed role. Plain text is
preserved byte-for-byte at the string level; only hashing normalizes Unicode/newlines.

## Deduplication

SHA-256 hashes length-prefixed text, HTML, RTF, ordered file paths and DIB bytes.
Text hash normalization uses NFC and LF and removes trailing NUL terminators;
meaningful whitespace is retained. Rich representation differences survive.
An incoming clip is suppressed only when the latest saved event has the same hash,
executable/process/PID/window/title identity and a timestamp within two seconds.
Different application/window context, intervening content, or later copies survive.
Hashing policy and actual repository behavior are covered by tests.

## Screenshots

After the clip is committed, a single-flight background task uses `PrintWindow`
with `PW_RENDERFULLCONTENT` into a window-sized GDI bitmap. No desktop capture
fallback exists. The source must still be foreground, have the same PID/HWND/title,
and pass current exclusions; settings and a two-second freshness bound are checked
before and after native work. Minimized, protected, oversized or failed captures
are skipped. Some GPU windows can return black pixels despite a success status;
physical testing is necessary. `PrintWindow` can hang in another application's
renderer: at most one screenshot worker can be occupied, clipboard storage continues,
and process shutdown remains bounded. Windows Graphics Capture can replace this
backend behind the existing context/image boundary if target-app testing warrants it.

Original context images are scaled to at most 2560 pixels on the longest side,
encoded as PNG; thumbnails are 320 pixels. Large clipboard images retain their
original decoded resolution (up to 40 million pixels and the configured byte limit).
Files are written atomically, then attached to the existing row; deleted rows are
never recreated. Failed attachments remove newly written media.

## Persistence and search

`PRAGMA user_version` migrations are transactional; newer schema versions are
rejected rather than destroyed. SQLite WAL, a busy timeout and serialized writes
support capture, UI queries and maintenance. FTS5 external-content triggers keep
search in sync with insert/delete. User input becomes bounded quoted prefix tokens,
never raw SQL or FTS operators. Search runs off the UI thread with cancellation
and a 160 ms debounce. Summaries have a 240-character preview and no full payload;
full representations load only for selection. Results are virtualized and paginated.

Rows contain a versionable JSON domain payload plus indexed/filterable summary
columns. Media stays on disk. Fields are ready to extend with additional migrations;
Phase 2 tags, sessions, OCR and embeddings have no placeholder runtime systems.

Maintenance runs at startup, each 50 successful captures, and when storage settings
are saved. It loads media references instead of full content, deletes oldest
unpinned entries for age/budget, then cleans owned orphan files older than one hour.
Conditional deletion rechecks pin status atomically, preserving clips pinned since
the maintenance snapshot. Deleted content remains subject to SQLite free-page reuse;
this is not forensic erasure. Passive checkpoints, incremental vacuum and optimize
avoid long blocking startup vacuum operations. Pinned media can exceed the budget.

## Native ownership and lifecycle

- Clipboard: each successful `OpenClipboard` is paired with `CloseClipboard`.
  `GetClipboardData` handles are borrowed; locks are paired with `GlobalUnlock`.
- Restoration: all `GMEM_MOVEABLE` handles are allocated before `EmptyClipboard`;
  ownership transfers only after successful `SetClipboardData`; untransferred
  allocations are freed. A per-process private marker suppresses restore feedback.
- Clipboard images and files are restored with CF_DIB and Unicode DROPFILES;
  HTML/RTF keep their original representations. Cut/move effects and arbitrary
  vendor-specific formats are deliberately not restored.
- `RegisterHotKey(MOD_NOREPEAT)` conflict returns a notice; new shortcuts are
  registered before the previous one is released. Changes persist only after success.
- `Shell_NotifyIcon` owns no shared icon handle; shared `LoadIcon` is not destroyed.
  The icon is re-added after Explorer's `TaskbarCreated`. Explicit Exit removes it,
  unregisters the hotkey/listener, drains capture briefly and terminates WinUI.
- Window capture: `Graphics.GetHdc` pairs with `ReleaseHdc`; all GDI+ Image/Bitmap/
  Icon/Graphics/Stream objects use deterministic disposal. Process handles use
  `CloseHandle`. No foreign COM pointer is retained.
- Paste targets must be visible external windows. `SetForegroundWindow` is checked;
  `SendInput` runs only after physical modifiers/Enter are released and target focus
  is verified. It respects UIPI; partial injections release only synthesized keys.
- Startup uses the current user's Run registry key with a quoted executable and
  `--background`. A named local-session mutex prevents duplicate app instances.

Settings use atomic replace with validation. Malformed settings disable capture,
rather than silently clearing exclusions and re-enabling it. Diagnostics record
fixed event names, exception types and HRESULTs only. Four approximately 1 MiB
files bound logging; clipboard values, exception messages and stack traces are omitted.
