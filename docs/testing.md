# Testing status

The spec says not to claim a manufacturer integration works unless it has actually been verified. This
page records what has been verified, how, and what still needs real hardware.

## Automated tests (`tests/UpdateAllWin.Tests`, 58 tests, all passing)

Run with `dotnet test tests/UpdateAllWin.Tests -p:Platform=x64` (Windows), or as part of `build/build.ps1`.

| Area | What the tests cover |
|---|---|
| Manufacturer detection | HP / Hewlett-Packard, Dell, Lenovo, Microsoft, ASUS, MSI, unknown and generic manufacturer strings |
| No updates available | Update All finishes quickly with "Your PC is up to date" and a green status |
| Updates available | The scan finds updates, the status turns orange, and BIOS updates are flagged as needing approval |
| BIOS approval | Update All installs drivers but leaves BIOS **awaiting approval**. Approved BIOS installs and is recorded in history. |
| BIOS safety | Blocked on battery power, below the minimum battery level, or with BitLocker on and no recovery key. BitLocker is suspended for an approved BIOS update when the tool doesn't handle it. |
| Download failures | Updates are marked failed, the status turns red, and the failure is recorded in history |
| Install failures | Per-update failure, with the exit code recorded |
| Network disconnection | The scan fails cleanly with a clear error |
| Several update requests at once | Only one operation runs; later requests are refused while it is busy |
| Background service restarts | Interrupted operations are recovered and recorded; results survive a restart; the restart flag clears after a reboot |
| Policy | Auto-install rules for scheduled runs, exclusions, critical-only mode, low disk space, value clamping |
| Vendor tool grouping | An "all at once" tool is blocked by an unapproved BIOS update; a "by category" tool installs the whole category |
| Schedule and quiet hours | Daily and weekly schedules, manual mode, catching up missed scans, quiet hours that wrap past midnight |
| Report parsers | HPIA XML and JSON reports, SoftPaq list numbers, Dell DCU XML report and exit codes |
| IPC and security | Real named-pipe round trips; service unavailable; standard users can't change policy; HPIA path must be outside user-writable folders; standard users can be blocked from installing; restart refused when none is pending |
| Exports | CSV formula-injection protection, serial number masking |

## Manual verification on Windows 11 (HP Elite x360 1040 G11, build 26300)

Done with the service in **simulation mode** (nothing installed). Screenshots are in `docs/images/`.

* The service starts from a System32 working directory (as Windows starts services) and the pipe accepts
  connections. *Two real bugs were found and fixed here: a content-root stall, and the client
  identification method.*
* Device detection is real: HP Elite x360 1040 14 inch G11 (platform 8C52), BIOS W90 Ver. 01.10.00,
  Core Ultra 7 165H, 64 GB RAM. The serial number is masked by default.
* Dashboard, Available Updates, History, Automation and Settings pages render in light and dark themes.
* The floating toolbar shows the status colour (grey → orange → yellow) and can be dragged and kept on top.
* "Update All" from the command line through the service installed both drivers, held the BIOS update
  for approval, and flagged a restart as required. The restart banner and deferral deadline appeared.
* The BIOS approval dialog shows live safety checks: AC power, battery 97%, disk space, and BitLocker.
  BitLocker reads "access denied" in the unelevated test service; the real service runs as SYSTEM.
* The MSI builds (581 files), and its ServiceInstall, ServiceControl, shortcuts and Run entries were checked.

## Not yet verified (needs real hardware or an elevated install)

| Item | Why | How to verify |
|---|---|---|
| HP Image Assistant scan and install | Needs admin; only simulation has run so far | Install the MSI on an HP PC and click **Check for Updates**. Then check `%ProgramData%\UpdateAllWin\Work\hpia-scan-*` contains an XML/JSON report the parser reads. |
| HPIA report schema | HP doesn't document it; the parser follows community scripts | Capture a real report and compare it with `HpiaReportParser` |
| Dell Command \| Update | No Dell hardware available | Scan, install, `-report` with a folder path, and the exit code `/applyUpdates` returns when nothing applies |
| Lenovo SU Helper / System Update | No Lenovo hardware available | WMI `Lenovo_Updates` timing after `-scanonly`; the meaning of Severity values |
| Toast notifications | Not captured on screen | Install the MSI, then trigger restart required in simulation |
| MSI install / upgrade / uninstall | Not installed on this PC | `msiexec /i`, then upgrade to a higher version, then `msiexec /x` with and without "delete data" |
| Code signing | Needs your certificate | `build.ps1 -CertificateThumbprint ...` |
