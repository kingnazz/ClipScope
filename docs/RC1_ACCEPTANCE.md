# RC1 acceptance record

**READY FOR PHYSICAL WINDOWS 11 ACCEPTANCE.** Both hosted workflows are green.
RC1 release acceptance remains open. The attached execution environment is Linux. No configured
Windows 11 x64 machine is available. Hosted Windows Server automation cannot
certify the physical Windows 11 release gate. Unperformed tests below remain open.

## Environment and build evidence

| Field | Actual environment |
| --- | --- |
| Engineering workspace | Linux x64, .NET SDK 10.0.402 |
| Automated desktop | GitHub-hosted `windows-2025`; Windows Server, not Windows 11 |
| Hardware/VM | AMD EPYC 9V74 hosted VM, 2 cores, 16 GiB RAM; one 1024×768 display at 96 DPI; physical GPU/topology unavailable |
| Build | `feat/windows11-rc1`; exact source SHA in each workflow and build manifest |
| Version | Derived from `Version.props`; initial candidate `0.1.0-rc.1` |
| Install | Per-user self-contained Inno Setup EXE; unsigned development artifact |
| Windows 11 build/hardware/displays/scaling | NOT TESTED: no connected Windows 11 machine |

## Verified on hosted Windows automation

Accepted RC evidence: [40449004143 — PASS](https://github.com/kingnazz/ClipScope/actions/runs/40449004143).
[Build and test — PASS](https://github.com/kingnazz/ClipScope/actions/runs/40449009343):
97 existing test cases (69 Core, 28 Infrastructure), Windows build, profile safety
and desktop smoke. Payload source: `d5ec179748c62a99d07e0b2c661b8527325055d0`.

| Hosted check | Actual result |
| --- | --- |
| Self-contained packaging and exact runtime pin | PASS; .NET 10.0.13, app PRI and runtime payload validated |
| Fresh install | PASS; per-user files, ARP/version, Start Menu and payload hashes |
| Installed functional smoke | PASS with repository source and staging unavailable; capture, context PNG decode, restore, hotkey, paste, pin, FTS, deletion and cleanup |
| Actual-main upgrade | PASS from f488f25344bce85fcef361241797eeed33510968; history/pins/settings/exclusions/media hashes/FTS/integrity retained; installed UI decoded context and restored text |
| Synthetic predecessor | PASS stale owned file removed and unrelated user file preserved |
| Single instance and startup | PASS background/duplicate/foreground/Start Menu; startup path preserved and removed on uninstall |
| Repair | PASS graceful app close, profile/startup preservation and full repaired payload hashes |
| Default uninstall / reinstall / explicit purge | PASS retained profile reused, then deliberate purge removed the owned profile |
| Available applications | PASS Notepad, PowerShell, Paint, Edge, Chrome and Explorer clipboard scenarios; Terminal-hosted shell capture/restore; ownerless Terminal native copy strictly refused as documented |
| Exclusion and pause | PASS Notepad content/metadata/FTS/media absence before/after restart; persisted pause and resumed capture |
| Explorer recovery | PASS actual shell restart, tray recovery and hotkey/search focus; physical tray-icon hit-testing remains open |
| Search benchmark | PASS 100 and 10,000 clips, 100 repository-query samples each; measurements below |
| Endurance/resources | PASS 20.2 minutes, 2,000 generated and persisted operations: 1,800 text / 200 images; SQLite integrity OK, valid counter coverage, image restore dimensions/pixel checked |

Artifacts include per-file hashes and raw evidence; the source SHA identifies
what each run tested. A documentation-only follow-up does not change the payload.

## Accepted artifact and measured results

Download [ClipScope-0.1.0-rc.1-win-x64](https://github.com/kingnazz/ClipScope/actions/runs/40449004143/artifacts/12710284431).
It contains `ClipScope-0.1.0-rc.1-win-x64-setup.exe`, `SHA256SUMS.txt`, the build
manifest and installer/compatibility/search/resource evidence. No Release was published.

Installer EXE SHA256:
`388CD7538A88577510407C2DFA19D5B70FCA01F2DC8F3CED22628FE46ED011A1`.
The archive's separate digest is
`18693e1f3ac6df848f8dc4eddc306dd97d5aa8d9e5674743b2b7d43a031f8c23`.

| Repository FTS history | Samples | Median | p95 | Max |
| --- | --- | --- | --- | --- |
| 100 clips | 100 | 0.433 ms | 0.511 ms | 0.614 ms |
| 10,000 clips | 100 | 24.452 ms | 25.725 ms | 30.230 ms |

These are repository queries returning up to 100 results, excluding UI debounce
and rendering. They do not establish physical Windows 11 search latency.

| Endurance measurement | Observed result |
| --- | --- |
| Installed process launch | 768 ms; already warm OS/file cache, not a clean boot |
| Tray callback reopen | 20 samples; median 80.8 ms, p95 94.9 ms |
| Global hotkey to focused search | 20 samples; median 61.0 ms, p95 118.9 ms |
| Hidden capture plus database probe | 20 samples; median 205.8 ms, p95 337.2 ms; includes diagnostics process startup |
| Image filter/detail timing | 25.8 ms; UIA polling measurement; image restoration separately checks dimensions and sampled pixel |
| Hidden idle CPU | 0.156% of one core over approximately 10 seconds |
| Private working set | 32,673,792 → 90,497,024 bytes |
| Private bytes | 46,608,384 → 127,979,520 bytes |
| Handles / GDI / USER | 903 → 1,097 / 73 → 70 / 42 → 52 |
| GC committed memory | 240 samples; first 12.833 MB, last 28.230 MB, min 12.833 MB, max 32.702 MB |
| Database plus WAL/SHM files | 106,744 → 8,658,640 bytes |
| Media and thumbnails | 0 → 8,992,914 bytes |

The observer collects `gc-committed`, backed by
`GC.GetGCMemoryInfo().TotalCommittedBytes`; this measures committed GC memory,
not live-object size. Negative/non-finite samples and insufficient coverage fail.
The earlier run 40446239402 reported a `gc-heap-size` minimum of −11.604 MB;
that impossible measurement was rejected and replaced, not filtered into a pass.

Raw evidence: `resources.csv`, `managed-heap.csv`, `stress.json`, `search-100.json`,
`search-10000.json`, `compatibility.json`, `installer.json`, environment and
before/after upgrade snapshots. Resource samples cover snapshots enabled then
disabled. After early UI warm-up, sampled handles ranged roughly 1,079–1,110
and private working set fluctuated around 80–94 MiB. Memory grew with activity;
this limited session does not prove absence of leaks or several-hour stability.
UIA polling is approximately 50 ms and contributes measurement overhead.

## Diagnosis of the previous hosted failure

Run [40059033978](https://github.com/kingnazz/ClipScope/actions/runs/40059033978)
failed in `Validate installed application and data policy`. At source
`2e0b29626b3342965f2d6ce4f6977bf70498f17b`,
`scripts/test-installer.ps1:132` invoked `powershell.exe -NoProfile -STA -File
scripts/verify-upgrade-windows.ps1 -TargetProcessId ... -OwnershipReceipt ...`.
The child's `Wait-Rc` assertion **`retained main context PNG decoded in installed
UI`** timed out; the parent threw **`Retained main history did not work in the
installed UI.`** at line 133. The retained pin and FTS result had already appeared.

The old fixture waited for text persistence, then immediately opened history.
`CaptureCoordinator.ScheduleScreenshot` runs asynchronously after text is saved
and deliberately cancels if the source loses foreground focus before attachment.
The fixture's generic media-count check could be satisfied by an application icon.
It therefore did not establish the context screenshot needed by the upgrade test.
The smoke now holds the real source foreground until its list item reports
`has source snapshot`, which is derived from the persisted context reference.
The installed-upgrade assertion still requires a decoded preview and exact text
restoration. The historical application is still built from
`f488f25344bce85fcef361241797eeed33510968`; no synthetic database replaces it.
Run [40163257633](https://github.com/kingnazz/ClipScope/actions/runs/40163257633)
subsequently passed the full installer/data-policy step, including this check.

That run then exposed a separate compatibility-harness failure: **`Timed out:
installed native host`**. PowerShell coerces `$null` to an empty string for a
string method parameter, so its `FindWindow` lookup requested an empty caption
instead of any caption. The actual native host is captioned `ClipScope`.
The harness now declares the caption as `IntPtr` and passes `IntPtr.Zero` to
`FindWindowW`. Product native-window discovery was already using a real C# null.
No assertion was removed or downgraded.

A subsequent run encountered real clipboard contention in the old-build fixture:
`Clipboard.SetText('ClipScope clear pinned sentinel')` threw **`Requested Clipboard
operation did not succeed.`** at `smoke-windows.ps1:272` in run
[40163753938](https://github.com/kingnazz/ClipScope/actions/runs/40163753938).
Test producers and standalone clipboard reads now retry only native
`ExternalException` failures, for at most five seconds. Assertions and other
exceptions still fail; persistent clipboard contention still fails. The local
fault-injection check verified retry-to-success, immediate propagation of other
errors and failure at the busy deadline. Paste automation also waits for actual
search keyboard focus before sending Enter.

Compatibility then reached the available Windows Terminal in run
[40164187125](https://github.com/kingnazz/ClipScope/actions/runs/40164187125),
where the harness incorrectly required a PowerShell `Set-Clipboard` command to
be attributed to `WindowsTerminal`. `ContextPolicy.ResolveClipboardSource`
intentionally retains the clipboard owner when it differs from the foreground
process. The corrected assertion compares the stored source PID with the real
native clipboard-owner PID. A separate Terminal selection / Ctrl+Shift+C scenario
requires attribution to Windows Terminal itself and exact text restoration.

Run [40165352352](https://github.com/kingnazz/ClipScope/actions/runs/40165352352)
proved that Terminal 1.23.2601.21001's native selection copy publishes owner PID
`0` (UnicodeText/Locale/Text/OEMText). The previous matching record was the shell
command's clip, not a Terminal capture. The harness now waits for the expected
publisher when capture is possible. For an ownerless native copy it instead
strictly verifies unchanged clip, payload-hit, metadata-hit, FTS-hit and media
counts, matching the existing fail-closed privacy contract. This is a documented
capture limitation, not a successful native Terminal capture. Identifiable-owner
Terminal-hosted `Set-Clipboard` capture and restoration remain required.

After the hosted toolchain changed, run
[40415082115](https://github.com/kingnazz/ClipScope/actions/runs/40415082115)
correctly failed its published-runtime equality assertion. The app now sets
`RuntimeFrameworkVersion` metadata on the `Microsoft.NETCore.App`
`FrameworkReference`, using the central `ClipScopeDotnetRuntimeVersion`. A global
runtime-version property also overrides Windows SDK framework resolution and is
therefore inappropriate here. A local cross-publish with deployment resource
steps disabled restored in locked mode and emitted .NET runtime `10.0.13`.
The hosted full-resource packaging check remains required.

The browser fixture originally assumed that an active top-level window was ready
for document selection. Native HTML copying timed out or produced an empty
clipboard. The harness now waits for a real accessible, editable HTML element and
its keyboard focus before Ctrl+A/C. Run
[40417225256](https://github.com/kingnazz/ClipScope/actions/runs/40417225256)
passed Edge and Chrome native HTML capture/restoration with decoded context PNGs.
It next failed Explorer CF_HDROP copying: `/select` and a visible window alone
did not establish a focused file item. Explorer automation now explicitly waits
for, selects and focuses the fixture's real file item before Ctrl+C.

Run [40445420103](https://github.com/kingnazz/ClipScope/actions/runs/40445420103)
passed the full compatibility stage, including actual Explorer restart recovery,
then reached endurance. Endurance timed out at `synthetic foreground source`.
The counter observer was launched as a visible console and could steal source
focus asynchronously. Its process now starts hidden; foreground verification,
operation counts, resource collection and managed-heap coverage remain required.

Complete job-log downloads redirect to Azure Blob Storage, which this cloud
workspace's network policy denies. Diagnosis used the Actions step results,
detailed error/notice annotations and immutable source; complete logs were not
available in this workspace. Raw validation evidence is uploaded with each run.

## Diagnostics isolation

`tools/ClipScope.Diagnostics` references Core and Infrastructure, not the WinUI
application or its output directory. Its lock file passes `--locked-mode` restore;
Release build with warnings as errors passes. A clean local rebuild reproduced
the same diagnostics DLL SHA256. The inherited build settings enable deterministic
compilation. Harness paths resolve from `$PSScriptRoot`; diagnostics runtime
dependencies are copied beside its DLL, independently of application output.
Diagnostics is not included in
the shipped payload. Probes require a synthetic-profile ownership marker and
reject reparse points; their reports contain counts, hashes and format metadata.

## Still requires physical Windows 11 testing

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
