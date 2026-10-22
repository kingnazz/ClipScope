# Phase 1 implementation plan

The repository was inspected on 2026-10-22: no commits, files, branches, issues
accessible through the API, or existing project conventions. Work is on
`feat/phase1-foundation`; no application changes will be made on `main`.

1. Establish .NET 10 projects, domain contracts, bounded capture policies, and tests.
2. Implement versioned SQLite/FTS5 storage, atomic settings, disk media and retention.
3. Implement a dedicated STA message pump using AddClipboardFormatListener,
   bounded contention retries, source metadata, privacy gates, and restoration.
4. Add asynchronous window-only screenshots, hotkeys, tray and startup integration.
5. Build a programmatic WinUI 3 three-pane interface and persistent settings.
6. Run portable tests, compile all available targets, add Windows CI and document
   physical Windows validation. Make logical commits and open a draft PR if access
   permits. An empty baseline branch is required before GitHub can accept a PR.

## Windows assumptions to validate

Windows 11 x64, interactive unelevated desktop, .NET 10 and a compatible Windows
App SDK. GetClipboardOwner is preferred over the foreground window; both are
checked for exclusions. Clipboard notifications cannot promise recovery of values
replaced before delayed rendering completes. PrintWindow is the window-only
capture backend: it can return black/unsupported results for GPU/protected apps;
there is no desktop fallback. Native capture is single-flight and never blocks
clipboard persistence. SendInput obeys integrity boundaries and foreground rules;
failed focus/paste leaves the historical content on the clipboard for manual paste.
No hooks, injection, network calls, polling clipboard loop, OCR or AI.
