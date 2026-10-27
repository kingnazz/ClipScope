# RC1 known issues and acceptance gaps

| Severity | Observed limitation | Consequence / exit criterion |
| --- | --- | --- |
| Release blocker | No configured Windows 11 x64 test machine in this task | Clean install without runtimes, Mica/Snap, real DPI/monitor changes, sign-in/reboot and sleep/lock acceptance remain unperformed. Complete the physical matrix before RC1 readiness. |
| High | Development artifacts are unsigned and use a placeholder publisher | Public certificate trust, timestamping and SmartScreen behavior are not validated. Signing hooks exist; production credentials and signed-artifact acceptance are still required before public distribution. |
| Medium | Hosted automation is Windows Server; accepted endurance is 20.2 minutes / 2,000 synthetic operations | It cannot establish several-hour Windows 11 resource stability, GPU capture quality or physical tray/display behavior. Raw metrics are retained; perform the longer physical session. |
| Medium | Windows Terminal 1.23.2601.21001 native selection copy on the hosted image publishes owner PID 0 | ClipScope intentionally refuses unresolved owners. Terminal-hosted PowerShell `Set-Clipboard` capture/restore works with its real publisher. Native ownerless copy capture remains unavailable; verify behavior on physical Windows 11. |
| Low | Engineering placeholder icon and development publisher/legal metadata | Replace with approved brand/publisher assets and distribution terms before a production release. |

These are observed environment/distribution limits and missing acceptance evidence,
not claims of unobserved application failures. Resolved implementation/CI failures
belong in the commit history and validation evidence, not this open-issue list.
