# Testing status

The spec says not to claim a manufacturer integration works unless it has actually been verified. This
page records what has been verified, how, and what still needs real hardware.

## Automated tests (`tests/UpdateAllWin.Tests`, 60 tests, all passing)

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
| Exports | CSV formula-injection protection, serial number masking (including inside vendor reports) |

## Manual verification on Windows 11 (HP Elite x360 1040 G11, build 26300)

Done with the service in **simulation mode** (nothing installed). Screenshots of that version are in git history; the current ones are in `docs/images/`.

* The service starts from a System32 working directory (as Windows starts services) and the pipe accepts
  connections. *Two real bugs were found and fixed here: a content-root stall, and the client
  identification method.*
* Device detection is real: HP Elite x360 1040 14 inch G11 (platform 8C52), BIOS W90 Ver. 01.10.00,
  Core Ultra 7 165H, 64 GB RAM. The serial number is masked by default.
* Dashboard, Available Updates, History, Automation and Settings pages render in light and dark themes.
  *(Since replaced by the single window, see below.)*
* The floating toolbar shows the status colour (grey → orange → yellow) and can be dragged and kept on top.
  *(The toolbar has since been removed.)*
* "Update All" from the command line through the service installed both drivers, held the BIOS update
  for approval, and flagged a restart as required. The restart banner and deferral deadline appeared.
* The BIOS approval dialog shows live safety checks: AC power, battery 97%, disk space, and BitLocker.
  BitLocker reads "access denied" in the unelevated test service; the real service runs as SYSTEM.
* The MSI builds (581 files), and its ServiceInstall, ServiceControl, shortcuts and Run entries were checked.

## Real HP Image Assistant scan (2026-10-11, same PC, MSI installed, service as SYSTEM)

* `msiexec /i` installed cleanly (exit 0); UpdateAllService started automatically as LocalSystem.
* **Bug found:** the installed app crashed on launch (XamlParseException in `MainWindow.InitializeComponent`)
  because `dotnet publish` left out `UpdateAllWin.pri` and the `.xbf` files. Earlier UI tests ran
  from the build folder, which has them. Fixed with a publish target in the app project, and
  `build.ps1` now fails if they're missing. Reinstalling the fixed MSI over 1.0.0 replaced the old
  copy (a single entry in Programs and Features), and the installed app opens and responds.

## Real HP Image Assistant install (2026-10-11, Update All from the app)

* Re-check 9 min (HPIA run times vary: 70 s earlier), restore point, `/Action:Download` 2.5 min,
  `/Action:Install` 3 min. HPIA exited **3010** (installed, restart required).
* **Bug found:** HPIA 5.3.7 writes `Remediation.ReturnCode` as a string (`"0"`), and the parser
  only accepted a number, so it threw and all three updates were wrongly shown as failed. The
  drivers had actually installed (HP Application Enabling 1.87.4769.0, Intel ME 2546.9 / 2603.9).
  Fixed, with a test using the captured report shape.
* Per-SoftPaq statuses seen: `INSTALL_COMPLETED`, and `INSTALL_UNKNOWN` with ReturnCode 0 (shown as
  installed, "HP will confirm on the next check"). An empty `"Remediation": {}` means not attempted.
* The install-time analysis lists more items than the scan (Accessories such as dock drivers, Poly
  Camera and UEFI diagnostics in Software), because the install run has no `/Category` filter. Only
  the selected SoftPaqs in `/SPList` are installed.
* `UpdateAllService.exe scan` downloaded HPIA 5.3.7 from `hpia.hpcloud.hp.com`, verified the
  signer as HP Inc., extracted it and ran `/Operation:Analyze /Action:List`. HPIA exited 0 after
  about 70 seconds; the whole scan took 77 seconds.
* The parser read the XML report correctly: 3 recommendations, all matching HP's report
  (sp173791 HP Application Enabling Software Driver, sp165987 HP Services Scan for Commercial as
  Driver, sp173750 Intel Management Engine Driver as Software), with installed and new versions.
* Findings: the HPIA self-extractor (`-e -f -s`) returns **1168** even when extraction succeeds, so
  the adapter correctly checks for `HPImageAssistant.exe` instead of trusting the exit code. HPIA's
  JSON report can show a different `RecommendationValue` from the XML (2613.9.28.0 vs 2543.9.0.0
  for Intel ME), so the XML stays the primary source. HPIA reports contain the **unmasked serial
  number and computer name**; the diagnostics export now masks the serial in them and skips
  HPIA's report .zip.

## Simplified single window (2026-10-12, build folder, same PC)

* The app is now one window plus a **What was installed** dialog. It ran from the build folder against
  the installed service and showed "Your PC is up to date", the HP logo and model, and the machine
  details. The dialog listed the real install results from 2026-10-11, failures included.
* Not yet checked: the busy, restart-required and BIOS-approval states in the new window, and the
  installed MSI copy.

## Not yet verified (needs real hardware or an elevated install)

| Item | Why | How to verify |
|---|---|---|
| HP Image Assistant install, after restart | Install ran (exit 3010); the after-restart check that the updates are gone is pending | Restart, then **Check for Updates** from the tray menu and confirm the 3 SoftPaqs no longer appear |
| HPIA BIOS / firmware recommendations | The verified scan had no BIOS or firmware items | Scan a PC with an older BIOS and check the BIOS category and approval gate |
| Dell Command \| Update | No Dell hardware available | Scan, install, `-report` with a folder path, and the exit code `/applyUpdates` returns when nothing applies |
| Lenovo SU Helper / System Update | No Lenovo hardware available | WMI `Lenovo_Updates` timing after `-scanonly`; the meaning of Severity values |
| Toast notifications | Not captured on screen | Install the MSI, then trigger restart required in simulation |
| MSI upgrade / uninstall | Install and same-version reinstall verified; version upgrade and uninstall not yet | Upgrade to a higher version, then `msiexec /x` with and without "delete data" |
| Code signing | Needs your certificate | `build.ps1 -CertificateThumbprint ...` |
