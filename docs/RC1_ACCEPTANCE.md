# RC1 acceptance record

**NOT READY FOR RC1.** The attached execution environment is Linux. No configured
Windows 11 x64 machine is available. Hosted Windows Server automation cannot
certify the physical Windows 11 release gate. Unperformed tests below remain open.

## Environment and build evidence

| Field | Actual environment |
| --- | --- |
| Engineering workspace | Linux x64, .NET SDK 10.0.402 |
| Automated desktop | GitHub-hosted `windows-2025`; Windows Server, not Windows 11 |
| Hardware/VM | Ephemeral hosted VM; physical monitor/GPU topology unavailable |
| Build | `feat/windows11-rc1`; exact source SHA in each workflow and build manifest |
| Version | Derived from `Version.props`; initial candidate `0.1.0-rc.1` |
| Install | Per-user self-contained Inno Setup EXE; unsigned development artifact |
| Windows 11 build/hardware/displays/scaling | NOT TESTED: no connected Windows 11 machine |

CI results and measured values will be recorded here after validation completes.
Artifacts include per-file hashes and raw evidence; the source SHA identifies
what each run tested. A documentation-only follow-up does not change the payload.

## Physical acceptance matrix

All rows in this matrix are **NOT TESTED**, not inferred from CI. Record the
Windows build, CPU/RAM/GPU, monitor models/resolution/scaling, install artifact
hash, source SHA, tester/date, observations and evidence before changing a result.

| Test | Required evidence | Current result |
| --- | --- | --- |
| Clean Windows 11 x64 install | Standard user, no .NET/VC/WinAppSDK installed; Start Menu launch, files independent of repository; record SmartScreen/UAC | NOT TESTED |
| Upgrade, repair, uninstall/reinstall | History/pins/settings/exclusions/media/FTS preserved, startup corrected, stale binaries removed, explicit data purge separate | NOT TESTED on Windows 11 |
| Start Menu / EXE / tray / installer launch | One native listener, one process/tray icon, foreground activation and quiet startup | NOT TESTED physically |
| Launch at sign-in | Enable/disable in Settings; real sign out/in and reboot; no duplicate entries, quiet launch after upgrade | NOT TESTED |
| Tray lifecycle | First launch, close, reopen, Pause/Resume, Settings, Exit, no phantom icon; actual Explorer restart | NOT TESTED physically |
| Mica/title bar | Windows 11 Mica, drag, maximize/restore/minimize, Snap Layouts, caption buttons, search hit areas | NOT TESTED |
| Appearance | System/light/dark, live system-theme and accent changes | NOT TESTED |
| 100%, 125%, 150%, 200% | Launch, minimum size, clipping, sharp icon/text, preview/capture dimensions at each scale | NOT TESTED |
| Mixed DPI / topology | Move/maximize/restore, change primary, disconnect/reconnect monitors | NOT TESTED |
| Notepad / Explorer | Unicode/emoji, multiline, URL, files/folders, restore/paste; source identity and context crop | NOT TESTED on Windows 11 |
| Edge / Chrome | Record versions, native CF_HTML/plain text/image copies and paste targets, GPU context quality | NOT TESTED on Windows 11 |
| Paint / Snipping Tool | Native image capture, selection/restore, decoded pixels and thumbnail | NOT TESTED |
| Terminal / PowerShell | Text/Unicode/multiline, host/source attribution, paste, source context | NOT TESTED |
| Office if installed | Word/Excel/Outlook versions, RTF/HTML and paste result; otherwise record unavailable | NOT TESTED |
| Context protection | Source window only, no invisible border/background; Settings/GPU windows; minimized/protected refusal without desktop fallback | NOT TESTED physically |
| Real-app exclusions | Prefer installed password manager; otherwise excluded Notepad; inspect payload/metadata/FTS/media before/after restart | NOT TESTED on Windows 11 |
| Pause / resume | Copy while paused, restart paused, resume and verify storage/exclusion integrity | NOT TESTED physically |
| Workstation lifecycle | Sleep/wake, lock/unlock, sign out/in, monitor power off/on, network changes; capture/tray/hotkey/context/DB afterwards | NOT TESTED |
| Several-hour endurance | Text/images/context, resource time series, idle CPU, database/media growth; compare settled and late-session baselines | NOT TESTED on Windows 11 |
| Performance | Cold launch, tray reopen, hotkey-to-focused-search, 100/10k UI search, image selection, hidden capture, idle CPU | NOT TESTED on Windows 11 |

Use the synthetic hosted harness to reproduce engineering regressions. It is
deliberately guarded against running on a real user's existing data. Physical
testers should use a disposable Windows account/VM and the installed artifact,
with known synthetic clipboard content; do not weaken the harness ownership checks.

## Release gate

The Windows 11 clean-install, display, workstation-lifecycle and endurance gates
are open. Passing automated checks is necessary but does not close those gates.
See [known issues](RC1_KNOWN_ISSUES.md). No public release or merge is authorized
by this record.
