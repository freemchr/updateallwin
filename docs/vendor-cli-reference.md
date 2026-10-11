# Vendor CLI / API reference for UpdateAll Win

Research date: 2026-10-11. Rule: **do not invent command-line capabilities.** Anything not marked
*Official doc* must be treated as provisional and guarded in code (feature-detect, log, fail safe).

Confidence levels:

- **Official doc**: vendor documentation (PDF, docs site, or vendor-published module source).
- **Vendor-observed**: taken from live vendor-published artefacts (catalog/descriptor files, winget manifests) rather than prose docs.
- **Vendor forum**: vendor-hosted community or blog content.
- **Community**: third-party scripts or blogs (widely used, but not authoritative).
- **Unverified**: no source found. Do not rely on it.

---

## 1. HP Image Assistant (HPIA) 5.x

Primary sources:

- HPIA landing page (latest **5.3.7**, sp174326): https://ftp.ext.hp.com/pub/caps-softpaq/cmit/HPIA.html
- Download: https://hpia.hpcloud.hp.com/downloads/hpia/hp-hpia-5.3.7.exe
- **HPIA User Guide** (Tenth Edition, April 2026, part no. Q20477-001): https://ftp.hp.com/pub/caps-softpaq/cmit/whitepapers/HPIAUserGuide2.pdf (chapter 6 covers command-line options, chapter 7 covers return codes)

Executable: `HPImageAssistant.exe`. The guide says HPIA "does not update the registry or copy files to the target computer". It runs from the extracted folder.

### 1.1 Command-line switches (all Official doc, User Guide table 6-1, unless noted)

| Switch | Syntax / values | Meaning |
|---|---|---|
| `/Operation` | `/Operation:[Analyze\|DownloadSoftPaqs]` | `Analyze` analyses this system and then downloads, extracts or installs per `/Action`. `DownloadSoftPaqs` downloads by configuration (multi-platform repository building). |
| `/Action` | `/Action:[List\|Download\|Extract\|Install\|UpdateCVA]` | `List` (the **default**) retrieves recommendations. `Download` downloads them. `Extract` downloads and extracts. `Install` downloads, extracts and installs: "Only Auto-installable (SSM-compliant) SoftPaqs will be installed." `UpdateCVA` applies to DownloadSoftPaqs only. |
| `/Category` | `/Category:[All,BIOS,Drivers,Software,Firmware,Accessories]` (comma list) | `All` is the default if omitted, and it **excludes Accessories**. (5.3.0 changed the `/Category:All` definition.) |
| `/SubCategory` | `Graphics, Chipset, Audio, 'Input Devices', Enabling, Network, Storage, Controller` | Driver filter, used with `/Category:Drivers` or `All`. Added in 5.3.6. |
| `/Selection` | `/Selection:[All,Critical,Recommended,Routine]` | `All` is the default. **With `/Operation:Analyze`, only `All` and `Critical` have an effect.** `Recommended` and `Routine` "ha[ve] no effect" for Analyze. |
| `/Silent` | flag | "Runs HP Image Assistant in silent (no UI, non-interactive) mode." `/RunHidden` is an alias. |
| `/Noninteractive` | flag | "Displays a noninteractive progress bar". It is **not** the same as silent. |
| `/ReportFolder` | `/ReportFolder:<folderPath>` | Output folder for reports. |
| `/ReportFilePath` | `/ReportFilePath:<folderPath\filename>` | Folder plus file name of the recommendation outputs (".HTML and .XML"). Overrides `/ReportFolder`. |
| `/ResultFilePath` | `/ResultFilePath:<folderPath\filename>` | Results ("Readme") file. Default is `Readme <datetime>` in the download folder, or `Readme` in `/ReportFolder` if that is set. |
| `/SoftPaqDownloadFolder` | `/SoftPaqDownloadFolder:<folderPath>` | Download folder. The default is the current user's Downloads folder. "If no user account exists, the default download location is `C:\ProgramData\HP\HP Image Assistant\`". This matters for LocalSystem, so **always pass it**. |
| `/SoftPaqExtractFolder` | `/SoftPaqExtractFolder:<folderPath>` | Extract folder. Defaults to the download folder. |
| `/SPList` | `/SPList:"C:\path\list.txt"` | Installs or downloads **specific SoftPaqs**. File format: "a list of raw SoftPaq numbers, without the SP prefix and without .exe extension, one number per line". With `/Operation:Analyze` it "downloads, possibly extracts, and installs ... depending on the /Action". Runs silently. HP recommends `START /WAIT`. |
| `/AutoCleanup` | flag | "Deletes the SoftPaq files used to successfully install a SoftPaq." Not allowed with `/Offlinemode`. |
| `/LogFolder` | `/LogFolder:<folderPath>` | Log or reference-zip folder. Falls back to ReportFolder, then the download folder, then the user's Downloads folder. |
| `/Debug` | flag | Enables logging to `%TEMP%\HP Image Assistant.log`, or to LogFolder, ReportFolder or SoftPaqDownloadFolder (in that order) if set. |
| `/BIOSPwdFile` | `/BIOSPwdFile:<encryptedBIOSPasswordFilePath>` | Encrypted BIOS admin password file, created with HPQPwd64. Mutually exclusive with `/BIOSPwdData` and `/BIOSPwdEnv`. Known issue in 5.2.0 (re-prompt after reboot), fixed in 5.2.1. |
| `/BIOSPwdData` | `/BIOSPwdData:<base64>` | Base64 contents of a password file. |
| `/BIOSPwdEnv` | `/BIOSPwdEnv:<EnvVarName>` | Environment variable holding the **clear-text** BIOS password. |
| `/InstallType` | `/InstallType:[All\|INFInstallable\|AutoInstallable]` | Filter by DPB (INF) or SSM (auto-installable) compliance. |
| `/IgnoreGenericOsError` | flag | Returns the normal codes (3010, 3020) instead of 4104 when a generic reference file is used. Release notes 5.1.11. |
| `/Offlinemode` | `/Offlinemode:<repo path>` | Analyze against a CMSL-built offline repository. |
| `/ReferenceFile` | `/ReferenceFile:<path.xml>` | Custom reference file. |
| `/Force` | flag | Installs all SoftPaqs for the platform/OS from an offline repository **without analysis**. Not for our use. |
| `/Wait` | `/Wait:<seconds>` | Delay before running (default 60). |
| `/VerifySoftPaq` | `/VerifySoftPaq:<path>` | Verifies a SoftPaq's signature. Return codes 512–517 (HPUP wrapper table). |
| `/AutoReport` | flag | Writes `SoftPaqReport.csv` to `%ProgramData%\HP\HP Image Assistant\`. |
| Proxy | `/ProxyURL: /ProxyPort: /ProxyUser: /ProxyPassword: /ProxyDomain:` | Proxy settings. |

HP's own examples (User Guide, chapter 6):

```
HPImageAssistant.exe /Operation:Analyze /Action:List /Silent /ReportFolder:C:\HPIAOut\Recommendations
HPImageAssistant.exe /Operation:Analyze /Category:All /selection:All /action:extract /silent /reportFolder:c:\HPIA\Report /softpaqdownloadfolder:c:\HPIA\download
HPImageAssistant.exe /Operation:Analyze /Category:Drivers /Selection:Critical /Action:Download /Silent
```

Recommended invocation for UpdateAll Win (every switch is documented):

```
Scan:    HPImageAssistant.exe /Operation:Analyze /Action:List /Category:BIOS,Drivers,Firmware /Selection:All /Silent /ReportFolder:"<rep>" /SoftPaqDownloadFolder:"<dl>" /LogFolder:"<log>"
Install: HPImageAssistant.exe /Operation:Analyze /Action:Install /Category:BIOS,Drivers,Firmware /Selection:All /Silent /ReportFolder:"<rep>" /SoftPaqDownloadFolder:"<dl>" /AutoCleanup [/BIOSPwdFile:"<pwd.bin>"]
Specific SoftPaqs: HPImageAssistant.exe /Operation:Analyze /Action:Install /SPList:"<list.txt>" /SoftPaqDownloadFolder:"<dl>" /ReportFolder:"<rep>"
```

Notes from the User Guide:

- "When HP Sure Admin (Enhanced BIOS Authentication Mode) is enabled, you cannot update the BIOS (download only)."
- HPIA needs access to `https://hpia.hpcloud.hp.com/` (primary) and `https://ftp.hp.com/pub/caps-softpaq/cmit/imagepal/ref/` (secondary). Exit code 4102 means TLS 1.2+ is required.

### 1.2 Exit / return codes (Official doc, User Guide table 7-1)

| Code | Meaning |
|---|---|
| 0 | No error |
| 1–5 | `/VerifySoftPaq` results: 1 = verification not found, 2 = exception, 3 = invalid company, 4 = authenticode failed, 5 = certificate chain failed |
| 6 | HPIA configuration file corrupted |
| **256** | "The analysis returned no recommendations." |
| **257** | "There were no recommendations selected for the analysis." |
| **3010** | "Install Reboot Required—SoftPaq installations are successful, and at least one requires a reboot." |
| 3011 | "A SoftPaq was not autoinstallable and was skipped because it could not be installed without manual intervention." |
| **3020** | "Install failed—One or more SoftPaq installations failed." |
| 4096 | Platform not supported |
| 4097 | Parameters invalid |
| 4098 | No internet connection |
| 4099 | Invalid SoftPaq number in SPList file |
| 4100 | My Products List empty |
| 4101 | Parameter no longer supported |
| 4102 | TLS 1.2 or higher required |
| 4103 | A complete request could not be sent to a remote server |
| 4104 | No supported OS found for the platform, so a generic reference was used. Use `/IgnoreGenericOsError` to get 3010/3020 instead. |
| 8192 | Operation failed |
| 8193 | Image capture failed |
| 8194 | Output folder not created |
| 8195 | Download folder not created |
| 8196 | Supported platforms list download failed |
| 8197 | KB download failed |
| 8198 | Extract folder not created |
| 8199 | SoftPaq download failed |
| 8200 | SoftPaq extraction failed |
| 12288 / 12289 | Target file failed to open / invalid |
| 16384 / 16385 | Reference file failed to open / invalid |
| 16386 | Reference file not supported on Windows 10 |
| 16387 | Command-line reference file doesn't match the system ID or OS version |
| 16388 | Error processing the command-line reference file |
| 16389 | Command-line reference file not found |
| 20480 | OS migration: architecture not supported |
| 32768 | Internal ("keep running"). Should never be surfaced. |

HP Software Wrapper (HPUP.exe) codes, from SoftPaqs (tables 7-2 and 7-4): 512–517 (VerifySoftPaq), 9527 (requires elevation), 9537 (bypass install.cmd return code), 9547 (DCHU/Fusion driver install failed), 9557 (UWP install failed), 9627 (nothing to install). Wrapper logs: `InstallCmdWrapper*.log`.

### 1.3 Report output

- **Official doc**: The User Guide only says recommendation outputs are ".HTML and .XML" (`/ReportFilePath`), plus the `Readme` results file (`/ResultFilePath`) and `SoftPaqReport.csv` (`/AutoReport`). **The guide does not document a JSON report or any schema.**
- **Community** (widely used scripts by Gary Blok/garytown, OSDeploy, FlorianSLZ; e.g. https://github.com/gwblok/garytown/blob/master/hardware/HP/Run-HPIA-Function.ps1, https://github.com/gwblok/garytown/blob/master/Intune/EndMgrEnhancedInventoryAddon_HPIACombo.ps1; HP Japan blog https://jp.ext.hp.com/techdevice/wolf/automation-of-driver-updates-with-microsoft-endpoint-manager-and-hp-image-assistant/): HPIA writes **both an `*.xml` and an `*.json`** into `/ReportFolder`. Scripts locate them with `Get-ChildItem -Include *.json / *.xml` because **file names are not fixed** (do the same: glob, pick newest).

XML structure (Community, consistent across several scripts):

```
HPIA
 └─ Recommendations
     ├─ BIOS / Drivers / Software / Firmware   (one element per category)
     │   └─ Recommendation  (repeated)
     │       ├─ TargetComponent      (component name)
     │       ├─ TargetVersion        (installed version)
     │       ├─ ReferenceVersion     (available version)
     │       ├─ Comments
     │       └─ Solution
     │           └─ Softpaq
     │               ├─ Id           (e.g. sp151063)
     │               ├─ Name
     │               ├─ Url          (scripts prefix "https://")
     │               └─ ReleaseNotesUrl
```

JSON structure (Community; partial, property names observed in scripts only):

```
HPIA.HPIAOperation, HPIA.ExitCode, HPIA.LastOperation, HPIA.LastOperationStatus
HPIA.Recommendations[]            (flat array, not grouped by category)
   .Name, .SoftPaqID, .RecommendationValue, .Comments, .Severity(?), .SSMCompliant(?), .DPBCompliant(?)
   .TargetComponent, .TargetVersion, .ReferenceVersion   (seen in some scripts)
   .Remediation.Status, .Remediation.ReturnCode, .Remediation.ReturnDescription   (after /Action:Install)
```

**Unverified**: what `RecommendationValue` means (one script treats it as the available version, another prints it next to the name). The same applies to `Severity`, `SSMCompliant`, `DPBCompliant` and SoftPaq size. **No real sample JSON report could be found publicly.** Action: capture a real report on an HP test box before finalising the parser. Parse the **XML** as primary (more consistent structure) and use the JSON for the per-item `Remediation.ReturnCode`.

### 1.4 Silent extraction of `hp-hpia-x.y.z.exe`

- **Official (vendor module source)**: HP's own `HP.Softpaq` PowerShell module (CMSL 1.9.0, `HP.Softpaq.psm1`, from PowerShell Gallery `https://www.powershellgallery.com/packages/HP.Softpaq`) extracts SoftPaqs with:
  `Start-Process -Wait <softpaq.exe> -ArgumentList "-e -f `"<folder>`"","-s"`
  so the syntax is **`-e -f "<folder>" -s`**: `-e` extracts only, `-f` sets the target folder, `-s` is silent.
- **Vendor forum**: HP Community FAQ (https://h30434.www3.hp.com/t5/Commercial-PC-Software/FAQ-23-Unpacking-downloaded-SoftPaqs/m-p/5853417) documents `-f<path>` (override path) and `-s` (silent).
- **Community**: `/s /e /f <folder>` (slash form) is used by garytown and others for the HPIA SoftPaq and works in practice. Prefer HP's own `-e -f "<dir>" -s`.

### 1.5 BitLocker and HP BIOS SoftPaqs

- **HPIA User Guide: no mention of BitLocker.** Whether HPIA or the BIOS SoftPaq suspends BitLocker is **Unverified**.
- Official (HP CMSL `HP.Firmware` module 1.9.0 source, `Update-HPFirmware -BitLocker stop|ignore|suspend`): the **default is `stop`**, meaning it refuses to flash if BitLocker is active and not suspended. `suspend` calls `Suspend-BitLocker -RebootCount 1`. This shows HP's tooling expects the caller to handle suspension.
- Community: `HpFirmwareUpdRec(64).exe -b` is reported to suspend BitLocker, but SoftPaq CVA `SilentInstall` lines vary by platform. An older BIOS SoftPaq checked (sp99306.cva) uses `HPWINGUI.exe /Q` with no BitLocker flag.
- **Recommendation**: UpdateAll Win should **suspend BitLocker itself** (WMI `DisableKeyProtectors(1)`, section 4b) before any HP BIOS or firmware install, and log it.

---

## 2. Dell Command | Update CLI (`dcu-cli.exe`) 5.x

Primary source: *Dell Command | Update Version 5.x Reference Guide*, CLI commands page:
https://www.dell.com/support/manuals/en-us/command-update/dcu_rg/dell-command-update-cli-commands?guid=guid-92619086-5f7c-4a05-bce2-0d560c15e8ed&lang=en-us
Error codes page:
https://www.dell.com/support/manuals/en-us/command-update/dcu_rg/command-line-interface-error-codes?guid=guid-fbb96b06-4603-423a-baec-cbf5963d8948&lang=en-us

Syntax is `dcu-cli.exe /<command> -option=value`, with no space after `/`. Values containing spaces go in double quotes. Options apply to that run only. With no options, saved settings are used.

### 2.1 Commands and options (Official doc)

| Command | Options allowed (per guide) |
|---|---|
| `/scan` | `-silent -outputLog -updateSeverity -updateType -updateDeviceCategory -catalogLocation -report` |
| `/applyUpdates` | `-silent -outputLog -updateSeverity -updateType -updateDeviceCategory -catalogLocation -reboot -encryptedPassword -encryptedPasswordFile -encryptionKey -autoSuspendBitLocker -forceupdate` |
| `/configure` | Persistent settings, e.g. `-biosPassword`, `-autoSuspendBitLocker`, `-updatesNotification`, `-catalogLocation`, `-allowXML`, schedule, proxy and throttle options |
| `/driverInstall` | Base drivers on a fresh OS (Advanced Driver Restore) |
| `/generateEncryptedPassword` | `-encryptionKey -password` (or the `-secure*` variants) |
| `/customnotification` | `-heading -body -timestamp` |
| `/version`, `/help`, `/?` | Version and usage |

| Option | Values | Meaning (quoted or condensed from the guide) |
|---|---|---|
| `-updateType` | `bios, firmware, driver, application, others` | Filter by type (comma list) |
| `-updateSeverity` | `security, critical, recommended, optional` | Filter by severity |
| `-updateDeviceCategory` | `audio, video, network, storage, input, chipset, others` | Filter by device category |
| `-report` | **"An .xml file path"**. Example: `dcu-cli.exe /scan -report=C:\Temp\UpdatesReport.xml` | "create an XML report of the applicable updates" (`/scan` only) |
| `-outputLog` | "A file path, with .log extension" | Status and progress log |
| `-silent` | none | Hides console status and progress |
| `-reboot` | `enable\|disable` | "Reboot the system automatically" (`/applyUpdates`) |
| `-autoSuspendBitLocker` | `enable\|disable` | "automatic suspension of BitLocker, when applying BIOS updates" |
| `-forceupdate` | `enable\|disable` | "override the pause functionality of Dell Command \| Update during conference calls". **It does not force reinstall or downgrade.** |
| `-biosPassword` | `"<pwd>"` (quoted), `""` clears it | `/configure` only. Stores an unencrypted BIOS password. Prefer `-encryptedPassword` + `-encryptionKey` (or `-encryptedPasswordFile`) on `/applyUpdates`. |
| `-catalogLocation` | path(s) | Custom catalog. Only one path is allowed with `/applyUpdates`. |
| `-updatesNotification` | `enable\|disable` | DCU's own toast notifications |

### 2.2 Report file

- **Official doc**: `-report=<file>.xml`, a file path. **The guide does not document the XML schema.**
- **Community** (KelvinTegelaar/PowerShellMonitoring, gwblok/garytown Dell-EMPS.ps1, FlorianSLZ, nickj76): scripts pass a **folder** (`-report=C:\Temp\DCU`) and read **`DCUApplicableUpdates.xml`** inside it. Observed schema:

```xml
<updates>
  <update>
    <name/> <version/> <date/> <urgency/>   <!-- urgency e.g. "Urgent" -->
    <type/>                                  <!-- BIOS | Driver | Firmware | Application | Utility | Patch | Other -->
    <category/> <file/>                      <!-- file = path relative to https://dl.dell.com -->
    <release/>                               <!-- (Unverified) -->
  </update>
</updates>
```

Action: support both forms. Pass a folder and glob `*.xml`. Confirm on a Dell test box.

### 2.3 Exit codes (Official doc, CLI error codes page)

| Code | Meaning |
|---|---|
| 0 | Success |
| **1** | "A reboot was required from the execution of an operation" (reboot to finish) |
| 2 | Unknown application error |
| 3 | Not a Dell system |
| 4 | Not run with administrative privileges |
| **5** | "A reboot was pending from a previous operation." **You must reboot before DCU will proceed.** This does not mean the updates you just applied need a reboot. |
| 6 | Another instance (UI or CLI) is running |
| 7 | System model not supported by the catalog |
| 8 | No update filters were applied/configured |
| 100–113 | Input validation errors: 100 no parameters, 101 no command, 102 invalid command, 103 duplicate commands, 104 bad command syntax, 105 bad option syntax, 106 invalid options, 107 invalid option value, 108 missing required option, 109 conflicting options, 110 multiple commands, 111 duplicate options, 112 invalid catalog, 113 value too long |
| **500** | (`/scan`) "No updates were found for the system" (up to date, or filters matched nothing) |
| 501 | `/scan`: error determining updates |
| 502 | `/scan`: cancelled |
| 503 | `/scan`: download error |
| 1000 | `/applyUpdates`: error retrieving the result |
| 1001 | `/applyUpdates`: cancelled |
| 1002 | `/applyUpdates`: download error |
| 1505 / 1506 | `/configure` export / import settings failed |
| 2000–2007 | `/driverInstall` (Advanced Driver Restore) errors |
| 2500–2502 | Password encryption errors |
| 3000–3005 | Dell Client Management Service: 3000 not running, 3001 not installed, 3002 disabled, 3003 busy, 3004 self-updating, 3005 installing pending updates |

**Unverified**: the exit code `/applyUpdates` returns when nothing is applicable. 500 is documented only under `/scan`.

### 2.4 Install paths and packages

- **Community** (garytown Dell-EMPS.ps1): the registry `HKLM\SOFTWARE\Dell\UpdateService\Clients\CommandUpdate\Preferences\Settings`, value `AppCode` = `Universal` or `Classic`, plus `ProductVersion`.
  - Universal (UWP) app: `C:\Program Files\Dell\CommandUpdate\dcu-cli.exe`
  - Classic (Win32): `C:\Program Files (x86)\Dell\CommandUpdate\dcu-cli.exe`
  - Probe both.
- **Vendor-observed (winget-pkgs manifests)**: `Dell.CommandUpdate` (classic) and `Dell.CommandUpdate.Universal`, both currently at 5.7.0. Silent switch: `/passthrough /S /V/quiet /V/norestart`. Manifests: https://github.com/microsoft/winget-pkgs/tree/master/manifests/d/Dell/CommandUpdate

---

## 3. Lenovo

Primary source: Lenovo CDRT docs (docs.lenovocdrt.com). The site source is on GitHub at https://github.com/CDRT/mk_docs, and the files used here are:

- SU Helper: `docs/guides/lcv/suhelper.md`
- System Update Suite Deployment Guide, chapter 5 "Command Line Reference": `docs/guides/sus/su_dg/su_dg_ch5.md`
- Chapter 4 "Troubleshooting": `su_dg_ch4.md`
- Chapter 6 "Advanced Configurations": `su_dg_ch6.md`
- Commercial Vantage configuration: `docs/guides/lcv/configuration.md`

### 3a. Commercial Vantage SU Helper (`SUHelper.exe`) — Official doc

- **Path**: `C:\Program Files\Lenovo\SUHelper\suhelper.exe`. It is a separate installer in the Commercial Vantage Enterprise package (`\SystemUpdate` subfolder): `[installer] /VERYSILENT`, or `VantageInstaller.exe -Vantage -SuHelper`. Uninstall with `C:\Program Files\Lenovo\SUHelper\unins000.exe /VERYSILENT`. It is a **companion to Commercial Vantage**: it drives CV's "System Update Addin", so CV must be installed.
- Parameters:

| Param | Values | Notes |
|---|---|---|
| `-autoupdate` | — | **Required.** Triggers an Auto Update session. Used alone, Group Policy filters apply. CLI filters override GP. |
| `-include` | `n3uj12w,n3jcd08w` | Only these package IDs. Cannot be combined with `-exclude`, `-packagetype` or `-reboottype`. |
| `-exclude` | package IDs | **Persistent** exclusion list. |
| `-packagetype` | `0`=Others, `1`=Application, `2`=Driver, `3`=BIOS, `4`=Firmware | Comma list. ANDed with `-reboottype`. |
| `-reboottype` | `0` none, `1` forced by update, `3` requires reboot, `4` forced shutdown, `5` delayed forced reboot (5 min) | Comma list |
| `-allowdefer` | — | Applies GP deferral rules |
| `-noreboot` | — | Skips the reboot **only for Reboot Type 5** updates. "No effect for other Reboot Types." |
| `-scanonly` | — | Checks only. "The results will be recorded in the Lenovo_Updates WMI class under root\Lenovo." |
| `-help`, `/?` | — | Help |

- **Return codes**: 0 = session triggered successfully, 1 = parameter error, 2 = System Update Addin busy, 3 = unexpected error. "Return codes represent only the result of calling suhelper.exe, not the result of the update session itself. Check the Lenovo_Updates WMI class for actual update results." The call is **asynchronous**, so UpdateAll Win must poll WMI.
- **Results**: WMI namespace `root\Lenovo`, class `Lenovo_Updates`. Properties: `PackageID, AdditionalInfo, Severity, Status, Title, Version`. Status values include `Applicable`, `Installed`, `NotApplicable`; Severity values are `Critical`, `Recommended`, `Optional`. CV add-in logs are in the `LenovoSystemUpdateAddin` subfolder of the CV logs.
- **Policy registry**: `HKLM\SOFTWARE\Policies\Lenovo\Commercial Vantage`. `AutoUpdateEnabled` (REG_DWORD) is the "Disable Auto Update" policy; Lenovo's baseline sets it to `0`. Other filters ("Configure System Update", "Configure Auto System Update", schedule, deferral) live in the CV ADMX. Exact value names are listed in configuration.md. By default CV auto-installs all Critical updates plus Recommended drivers weekly.
- Example (Official): `suhelper.exe -autoupdate -packagetype 2,3 -reboottype 0 -allowdefer`.

### 3b. Lenovo System Update `tvsu.exe /CM` — Official doc (SU Deployment Guide, chapter 5.1)

- Path: `C:\Program Files (x86)\Lenovo\System Update\tvsu.exe`.
- The guide says the scheduled task runs `tvsu.exe /CM` and "**the rest of the command line parameters would be specified as a Policy**": `HKLM\Software\Policies\Lenovo\System Update\UserSettings\General`, value `AdminCommandLine` (REG_SZ). Example value: `/CM -search A -action INSTALL -includerebootpackages 3 -nolicense -exporttowmi`. If you replace the default scheduled task, set `SchedulerAbility=NO` under `HKLM\SOFTWARE\WOW6432Node\Lenovo\System Update\Preferences\UserSettings\Scheduler`. Inline arguments do work: the chapter 4 log excerpt shows `-search R -action INSTALL -exporttowmi -nolicense -noicon -noreboot -includerebootpackages 1,3,4,5` being parsed. The **documented, supported** route is still `AdminCommandLine`.

| Param | Values | Meaning |
|---|---|---|
| `/CM` | — | Required. Command-line mode. |
| `-search` | `C` critical only, `R` recommended + critical, `A` all | Required |
| `-action` | `DOWNLOAD`, `LIST`, `INSTALL` | Required, one only. **`LIST` shows the user a pick list (interactive). It is not a silent "report" mode.** |
| `-includerebootpackages` | `1,3,4,5` (comma list; 2 never used) | **Without it, only Reboot Type 0 packages are included.** 1 = update forces its own reboot, 3 = SU reboots, 4 = update forces shutdown, 5 = delayed forced reboot with countdown. |
| `-packagetypes` | `1` App, `2` Driver, `3` BIOS, `4` Firmware (0 reserved) | Intersected with the reboot filter |
| `-noreboot` | — | Suppresses the reboot **for Reboot Type 3 only**. Types 1 and 4 reboot or shut down by themselves. Type 5 is always forced by SU. |
| `-noicon` | — | Suppresses the tray balloon |
| `-nolicense` | — | Suppresses the license notice |
| `-rebootprompt` | — | Forces a reboot prompt (with `-noicon` and without `-noreboot`) |
| `-scheduler` | — | Suppresses the pick-list license notice |
| `-repository` | path/UNC/URL | Local repository |
| `-exporttowmi` | — | Writes history to `root\Lenovo` `Lenovo_Updates` |

- Prompt behaviour (chapter 5.1.1): if Reboot Type 1, 4 or 5 updates are found, "System Update will always prompt the user before proceeding". With `-noreboot -noicon` and only type 0/3 updates, it installs with no prompt and no restart.
- **Exit codes: not documented** for tvsu.exe. Use `-exporttowmi`, then read `Lenovo_Updates`.
- Logs: `C:\ProgramData\Lenovo\SystemUpdate\logs\tvsu_<timestamp>.log`. **Since 2024 builds, logging is off by default.** Enable it with `HKLM\SOFTWARE\WOW6432Node\Lenovo\System Update\Preferences\UCSettings\Log`, value `FileName` = `tvsu.log`.

### 3c. Lenovo Thin Installer — Official doc (chapter 5.2)

`ThinInstaller.exe /CM -search R -action INSTALL -includerebootpackages 3 -noreboot -noicon -repository <path> -exporttowmi`

- Same `/CM -search -action -includerebootpackages -packagetypes -noreboot -noicon -repository -exporttowmi` semantics as tvsu.
- Extra actions: `SCAN` (applicability check, writes `Update_ApplicabilityRulesTrace.txt`) and `INSTALLDEFERRED` (installs what DOWNLOAD fetched; filters ignored).
- Extra flags: `-nocancel`, `-nocontinueafterreboot`, `-debug`, `-showprogress`, `-scheduler`, `-log <path>`, `-ignorexmlsignature`. Do not use the last one: it skips Lenovo's XML signature check.
- Must run elevated. Log default: `<ThinInstaller dir>\logs\Update_log_<ts>.txt`.
- Return codes:

| Action | Code | Meaning |
|---|---|---|
| INSTALL | 3010 | Reboot required (type 3 installed with `-noreboot`) |
| SCAN | 10000 | No applicable updates |
| SCAN | 10001 | Applicable updates found |
| DOWNLOAD | 20000 | All downloaded |
| DOWNLOAD | 20001 | Some failed |
| DOWNLOAD | 20002 | Found but none downloaded |
| DOWNLOAD | 20003 | Nothing to download |

### 3d. Lenovo catalog and package descriptor — Vendor-observed (live files; no formal schema doc found)

Catalog: `https://download.lenovo.com/catalog/<MT>_Win11.xml` (also `_Win10`). Observed on `20XW_Win11.xml`:

```xml
<packages count="48">
  <package>
    <location>https://download.lenovo.com/pccbbs/mobiles/n32uj36w_2_.xml</location>  <!-- descriptor URL -->
    <category>BIOS UEFI</category>
    <oslangs></oslangs>
    <checksum type="sha256">…descriptor-xml sha256…</checksum>
  </package>
```

Package descriptor (observed on `n32uj36w_2_.xml`, a ThinkPad BIOS):

| Element / attribute | Example | Meaning (inferred unless stated) |
|---|---|---|
| `Package@id`, `@name`, `@version` | `n32uj36w`, `BIOS_N32UJ`, `1.78` | Package ID (what `-include` uses), name, version |
| `Title/Desc` | `BIOS Update Utility - 10/11` | Display title |
| `Severity@type` | `2` | Severity code. Mapping 1/2/3 to Critical/Recommended/Optional is **Unverified**. |
| `Reboot@type` | `5` | Reboot type, 0–5 as in Lenovo docs (Official meaning) |
| `PackageType@type` | `3` | 1 App, 2 Driver, 3 BIOS, 4 Firmware (Official meaning) |
| `ExtractCommand` | `n32uj36w.exe /VERYSILENT /DIR=%PACKAGEPATH% /EXTRACT="YES"` | Extraction command line |
| `Install@type="cmd"`, `@rc="0,1"`, `@rccancel` | `Cmdline: %PACKAGEPATH%\winuptp.exe -s -f` | Install command; `rc` lists the return codes treated as success |
| `DetectInstall` | `<_Bios><Level>N32ETA2*</Level></_Bios>` | Rule meaning "already installed". Other rule types exist (`_Driver`, `_File`, `_Registry...`, unverified list). |
| `Dependencies` | `And/Or` tree of `_OS`, `_CPUAddressWidth`, `_Bios` … | Applicability rules |
| `DetectVersion` | — | Version detection |
| `ReleaseDate`, `DiskSpaceNeeded`, `Vendor` | — | Metadata |
| `Files/Installer/File/{Name,CRC,Size}` | `CRC` = 64 hex chars | **CRC holds a SHA-256** of the installer (observed) |
| `Files/Readme/File` | — | Readme file |
| `Signature` (XML-DSig, RSA-SHA256, Lenovo cert) | — | Signed descriptor. Thin Installer verifies it by default. |

Re-implementing the Lenovo rule engine is risky. Prefer SU, Thin Installer or SU Helper, and use the catalog only for display or "what's available".

### 3e. Package IDs (Vendor-observed, winget-pkgs)

| Tool | winget ID | Notes |
|---|---|---|
| System Update | `Lenovo.SystemUpdate` | Latest 5.08.04.85, Inno installer. Silent: `/VERYSILENT /NORESTART` (Official, SU guide chapter 2). |
| Thin Installer | `Lenovo.ThinInstaller` | 1.3.0043 |
| Update Retriever | `Lenovo.UpdateRetriever` | — |
| SU Helper | `Lenovo.SUHelper` | 1.0.0.22. Doc URL in the manifest. |
| Commercial Vantage | **not in winget-pkgs** | Microsoft Store ID `9NR5B8GVVM13` (https://apps.microsoft.com/detail/9NR5B8GVVM13), or the Lenovo Enterprise package (https://support.lenovo.com/us/en/solutions/hf003321). `winget install --source msstore 9NR5B8GVVM13` follows from the standard msstore syntax but is **Unverified**. |

---

## 4. Windows

### 4a. Windows Update Agent (WUA) API — Official doc

- `IUpdateSearcher::Search(criteria)`: https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdatesearcher-search
  - Criteria are SQL-WHERE-like. `Type` supports `=` and `!=` with `'Driver'` or `'Software'`. Other criteria: `IsInstalled`, `IsHidden`, `IsPresent`, `IsAssigned`, `BrowseOnly`, `AutoSelectOnWebSites`, `RebootRequired`, `UpdateID`, `RevisionNumber`, `CategoryIDs contains`, `DeploymentAction`.
  - **`OR` is allowed only at the top level.**
  - Default criteria: `IsInstalled = 0 and IsHidden = 0`.
  - Driver search: **`"IsInstalled=0 and Type='Driver'"`**.
  - Error codes: `WU_E_INVALID_CRITERIA`, `WU_E_LEGACYSERVER`, `E_POINTER`.
  - "IsAssigned=1 ... At most, one assigned Windows-based driver update is returned for each local device."
- `IWindowsDriverUpdate` (QueryInterface from `IUpdate` when it is a driver): https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nn-wuapi-iwindowsdriverupdate
  - Properties: `DeviceProblemNumber`, `DeviceStatus`, `DriverClass`, `DriverHardwareID`, `DriverManufacturer`, `DriverModel`, `DriverProvider`, `DriverVerDate`.
- From `IUpdate`: `Title`, `MaxDownloadSize`/`MinDownloadSize` (DECIMAL), `InstallationBehavior.RebootBehavior`, `IsDownloaded`, `Identity.UpdateID`.
- `InstallationRebootBehavior` (https://learn.microsoft.com/en-us/windows/win32/api/wuapi/ne-wuapi-installationrebootbehavior): `irbNeverReboots=0`, `irbAlwaysRequiresReboot=1`, `irbCanRequestReboot=2`.
- `OperationResultCode` (https://learn.microsoft.com/en-us/windows/win32/api/wuapi/ne-wuapi-operationresultcode): `orcNotStarted=0`, `orcInProgress=1`, `orcSucceeded=2`, `orcSucceededWithErrors=3`, `orcFailed=4`, `orcAborted=5`.
- `IInstallationResult` exposes `ResultCode`, `RebootRequired` and `GetUpdateResult(i)` (per-update `ResultCode` and `HResult`).
- Note (not researched in depth, **Unverified**): when WSUS or WUfB policy excludes drivers, driver searches against the default server may return nothing. Consider `ServerSelection = ssWindowsUpdate` and check policy before reporting "no drivers".

### 4b. BitLocker via WMI — Official doc

Namespace `root\CIMV2\Security\MicrosoftVolumeEncryption`, class `Win32_EncryptableVolume`. Requires admin; LocalSystem is fine.

- `GetProtectionStatus([out] uint32 ProtectionStatus)` (https://learn.microsoft.com/en-us/windows/win32/secprov/getprotectionstatus-win32-encryptablevolume): `0` = Unprotected (includes **suspended**: "the volume's encryption key is available in the clear"), `1` = Protected, `2` = Unknown (e.g. locked).
- `GetKeyProtectors([in, optional] uint32 KeyProtectorType, [out] string VolumeKeyProtectorID[])` (https://learn.microsoft.com/en-us/windows/win32/secprov/getkeyprotectors-win32-encryptablevolume). Types:
  - 0 all
  - 1 TPM
  - 2 external key
  - **3 numeric password (the recovery password)**
  - 4 TPM+PIN
  - 5 TPM+startup key
  - 6 TPM+PIN+startup key
  - 7 public key
  - 8 passphrase
  - 9 TPM certificate
  - 10 SID

  Returns `FVE_E_NOT_ACTIVATED` (0x80310008) if BitLocker is off. To read the 48-digit recovery password, call `GetKeyProtectorNumericalPassword(VolumeKeyProtectorID)`.
- `DisableKeyProtectors([in, optional] uint32 DisableCount)` (https://learn.microsoft.com/en-us/windows/win32/secprov/disablekeyprotectors-win32-encryptablevolume): **the parameter is named `DisableCount`, not `RebootCount`**. It is the "number of reboots for which the key protectors will be disabled. This parameter is only available on OS volumes." Minimum client: **Windows 8.1**. Return codes: `0`, or `FVE_E_LOCKED_VOLUME` (0x80310000). Resume with `EnableKeyProtectors()`.
- Range: `Suspend-BitLocker -RebootCount` documents **0–15, where 0 = suspended until resumed** (https://learn.microsoft.com/en-us/powershell/module/bitlocker/suspend-bitlocker). Use `DisableCount=1` for a BIOS flash and **never 0** from an unattended service.

### 4c. Battery / AC — Official doc

- `GetSystemPowerStatus` / `SYSTEM_POWER_STATUS` (https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-system_power_status). Preferred because it is cheap and needs no WMI.
  - `ACLineStatus`: 0 offline, 1 online, 255 unknown.
  - `BatteryFlag`: 1 high (>66%), 2 low (<33%), 4 critical (<5%), 8 charging, **128 no system battery**, 255 unknown.
  - `BatteryLifePercent`: 0–100, or 255 if unknown.
  - `SystemStatusFlag`: 1 = battery saver on.
- `Win32_Battery` (`root\CIMV2`, https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-battery):
  - `EstimatedChargeRemaining` (uint16, percent).
  - `BatteryStatus`: 1 Other ("discharging"), 2 Unknown ("system has access to AC ... not necessarily charging"), 3 Fully Charged, 4 Low, 5 Critical, 6 Charging, 7 Charging and High, 8 Charging and Low, 9 Charging and Critical, 10 Undefined, 11 Partially Charged.
  - No instances means no battery (desktop).
- Suggested gate before a BIOS flash: `ACLineStatus==1` **and** (no battery, or `BatteryLifePercent` >= vendor minimum). Vendors often require ~10–25%; the exact threshold is per vendor and **Unverified**.

### 4d. Pending-reboot markers

| Marker | Source / confidence |
|---|---|
| `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager` value `PendingFileRenameOperations` (and `PendingFileRenameOperations2`) | **Official** (`MoveFileEx` with `MOVEFILE_DELAY_UNTIL_REBOOT` stores renames there: https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw) |
| `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending` (key exists) | **Community** (de-facto standard, used by ConfigMgr scripts and PendingReboot modules). Not formally documented. |
| `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired` (key exists) | **Community** (same caveat) |
| WUA `ISystemInformation.RebootRequired` (`Microsoft.Update.SystemInfo`) | **Official** WUA API. Preferred over the WU registry key. |
| `HKLM\SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName` ≠ `...\ComputerName` | Community |
| Vendor signals: DCU exit 1/5, HPIA 3010, Thin Installer 3010 | Official (vendor docs above) |

### 4e. Windows 11 taskbar embedding

- **There is no supported API to embed a toolbar or deskband in the Windows 11 taskbar.**
  - `IDeskBand2` docs (Official): "You should use thumbnail toolbars in new development in place of desk bands, which are not supported as of Windows 7." https://learn.microsoft.com/en-us/windows/win32/api/shobjidl/nn-shobjidl-ideskband2
  - The Windows 11 removal of taskbar toolbars/deskbands and the statement that apps can no longer customize areas of the taskbar come from Microsoft's Windows 11 removed-features list, as reported by **Community** sources (https://en.wikipedia.org/wiki/List_of_features_removed_in_Windows_11, https://winaero.com/microsoft-to-remove-some-popular-taskbar-customization-options-in-windows-11). I did not re-fetch the Microsoft wording.
- Supported alternatives (Official, Taskbar Extensions: https://learn.microsoft.com/en-us/windows/win32/shell/taskbar-extensions):
  - **Notification area icon** (`Shell_NotifyIcon`). "All notification icons are now hidden by default and that visibility cannot be programmatically controlled." Only the user can promote the icon.
  - **Jump List tasks** (`ICustomDestinationList::AddUserTasks`, IShellLink with arguments). This is a plain Win32 COM API and works for unpackaged desktop apps. Use a consistent explicit **AppUserModelID** (https://learn.microsoft.com/en-us/windows/win32/shell/appids). Recent/Frequent destinations require file-type registration; custom tasks do not.
  - **Thumbnail toolbar buttons** (max 7), **taskbar progress bar** and **overlay icon** (`ITaskbarList3`). These need a window with a taskbar button.
  - **"Programmatic pinning by an application is not permitted."**
- **Widgets**: "Currently you can implement a widget provider using a **packaged** Win32 desktop app or a PWA" (https://learn.microsoft.com/en-us/windows/apps/develop/widgets/widget-providers). So widgets require MSIX or package identity and do not work for a plain unpackaged app.
- All of the above must run **in the interactive user session**. A LocalSystem service (session 0) cannot show tray icons, jump lists or toasts. UpdateAll Win needs a per-user, non-elevated tray companion that talks to the service over IPC.

### 4f. Windows App SDK `AppNotificationManager` — Official doc

- Overview (https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/), Limitations: "**Apps running with administrator privileges (elevated) cannot send or receive app notifications.**" The send-local-toast guide adds that `Show` **fails silently** when elevated.
- Unpackaged apps are supported (WPF, WinForms and console guides). For unpackaged apps, `AppNotificationManager.Default.Register()` sets up COM activation registration automatically. Buttons and inputs are supported via `AppNotificationBuilder`, and activation arrives via `NotificationInvoked`.
- Consequence: toasts must come from the **non-elevated** user-session tray companion, not from the service or an elevated UI.

---

## 5. System Restore point — Official doc

`SystemRestore.CreateRestorePoint` (namespace **`root\default`**): https://learn.microsoft.com/en-us/windows/win32/sr/createrestorepoint-systemrestore

```
uint32 CreateRestorePoint([in] string Description, [in] uint32 RestorePointType, [in] uint32 EventType)
```

- RestorePointType: `0` APPLICATION_INSTALL, `1` APPLICATION_UNINSTALL, **`10` DEVICE_DRIVER_INSTALL**, `12` MODIFY_SETTINGS, `13` CANCELLED_OPERATION.
- EventType: `100` BEGIN_SYSTEM_CHANGE, `101` END_SYSTEM_CHANGE, `102` BEGIN_NESTED_SYSTEM_CHANGE, `103` END_NESTED_SYSTEM_CHANGE.
- Returns S_OK or a COM error.
- **Throttle (Windows 8+)**: "Windows skips creating this new restore point if any restore points have been created in the last 24 hours. The CreateRestorePoint method returns **S_OK**." The skip is silent.
  - Override: DWORD **`SystemRestorePointCreationFrequency`** under `HKLM\Software\Microsoft\Windows NT\CurrentVersion\SystemRestore`. The value is in **minutes**. `0` means never skip; N means skip if one was created in the previous N minutes. The key "will not preexist".
- Implementation: after the call, enumerate `SystemRestore` instances (`root\default`) to confirm a new point exists. Do not treat S_OK as proof.
- System Protection must be enabled on C:. The WMI `Enable` method exists but is out of scope here (**Unverified** for usage).
- Changing `SystemRestorePointCreationFrequency` is a machine-wide policy change. Make it opt-in and consider restoring the previous value afterwards.

---

## Unverified / open items (test on real hardware before shipping)

1. The HPIA JSON report schema, the meaning of `RecommendationValue`, file names, and SoftPaq size/severity fields. The User Guide documents none of this.
2. Whether HPIA or the HP BIOS SoftPaqs suspend BitLocker themselves.
3. The DCU `-report` behaviour when given a folder (`DCUApplicableUpdates.xml`) versus the documented `.xml` file path, and the XML element list.
4. The DCU exit code from `/applyUpdates` when nothing is applicable.
5. `tvsu.exe` exit codes (none documented) and the reliability of inline arguments versus the `AdminCommandLine` policy.
6. The Lenovo descriptor `Severity@type` mapping, and the full list of `DetectInstall` rule types.
7. The winget msstore install of Commercial Vantage.
8. The exact battery-percentage thresholds each vendor's BIOS flasher enforces.
