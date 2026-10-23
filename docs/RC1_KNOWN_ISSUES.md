# RC1 known issues and acceptance gaps

| Severity | Observed limitation | Consequence / exit criterion |
| --- | --- | --- |
| Release blocker | No configured Windows 11 x64 test machine in this task | Clean install without runtimes, Mica/Snap, real DPI/monitor changes, sign-in/reboot and sleep/lock acceptance remain unperformed. Complete the physical matrix before RC1 readiness. |
| High | Development artifacts are unsigned and use a placeholder publisher | Public certificate trust, timestamping and SmartScreen behavior are not validated. Signing hooks exist; production credentials and signed-artifact acceptance are still required before public distribution. |
| Medium | Hosted automation is Windows Server with limited duration and synthetic workloads | It cannot establish several-hour Windows 11 resource stability, GPU capture quality or physical tray/display behavior. Retain raw metrics and perform the longer physical session. |
| Low | Engineering placeholder icon and development publisher/legal metadata | Replace with approved brand/publisher assets and distribution terms before a production release. |

These are observed environment/distribution limits and missing acceptance evidence,
not claims of unobserved application failures. Resolved implementation/CI failures
belong in the commit history and validation evidence, not this open-issue list.
