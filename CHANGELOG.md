# Changelog

## 1.0.0 (preview), 2026-10-11

First version.

### Added
- WinUI 3 app: Dashboard, Available Updates, History, Automation and Settings pages, light and dark themes.
- Floating **Update All** toolbar with status colours, a right-click menu and a **×** button to hide it.
- Notification-area icon, restart reminders and Windows notifications.
- UpdateAllService (LocalSystem) with a locked-down named-pipe API and a command line for scripts and Intune.
- Manufacturer adapters: HP Image Assistant, Dell Command | Update, Lenovo SU Helper / System Update, and Windows Update as a fallback.
- BIOS safety: approval gate, AC power and battery checks, BitLocker handling, a restore point, and sleep and shutdown blocking.
- SQLite history, diagnostics export (serial number masked), CSV export.
- WiX MSI installer and a GitHub Actions build.

### Fixed during real-hardware testing (HP Elite x360 1040 G11)
- The installed app crashed on launch because the publish step left out the app's XAML resources (`.pri` / `.xbf`).
- HP install results were shown as failed: HPIA 5.3.7 reports `ReturnCode` as text, not a number.
- Raw error text is no longer shown in History; a plain-English message is shown instead.
- Diagnostics export now masks the serial number inside HP's own report files.
- The service hung when Windows started it (wrong content root), and the pipe could fail to identify the caller.
