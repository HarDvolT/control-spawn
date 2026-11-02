# Packaging status — 2026-10-29

Packaging source is implemented on branch `package`. No installer was run, no application or service was started/stopped/installed/uninstalled, and no commit was created. Only new files under the authorized paths were added.

**A verified PrismSetup.exe has not been produced.** Build downloads are blocked in this environment, and Inno Setup is absent. Fresh-PC behavior below describes the implementation and still requires validation on a disposable Windows 11 machine.

## Build

Use Windows PowerShell 5.1 on a Windows x64 build machine with .NET 8 SDK and network access. Inno Setup **6.3 or newer** is required to compile the installer; the build only prints installation guidance if it is absent and never installs tools itself.

From the repository root:

```powershell
./build/publish.ps1
# Optional signing, using an existing certificate with its private key:
./build/publish.ps1 -CertificateThumbprint '<thumbprint>'
# Explicit compiler location, if it is not on PATH or in the standard locations:
./build/publish.ps1 -IsccPath 'C:/Program Files (x86)/Inno Setup 6/ISCC.exe'
# Publish/check without compiling an installer:
./build/publish.ps1 -SkipInstaller
```

Output is `build/out/Prism/` and `build/out/PrismSetup.exe`. The brief's `out/Prism/` is nested under `build/` to honor the restriction on new files. An existing output folder causes a failure rather than mixing stale outputs; move it aside before rebuilding. Intermediate source snapshots, restore/build artifacts, dependency downloads and logs remain under `build/`; its new `.gitignore` excludes generated content. No source files are edited. NuGet uses existing feed configuration but stores its packages, HTTP cache and plugin cache under `build/cache/`; environment overrides are restored afterward.

Both Prism.Service and Prism.App publish Release, self-contained, win-x64, ReadyToRun, untrimmed, with debug symbols disabled. WPF is never trimmed. Separate publish outputs merge into one folder with byte-for-byte checks for shared-file collisions. A future `src/Prism.FanGuard/Prism.FanGuard.csproj` is published if present. No .NET runtime installation is needed on the destination.

`build/dependencies.json` pins:

| Dependency | Version | SHA-256 |
| --- | --- | --- |
| OpenRGB portable, Windows x64 | 1.1 / 8e16de2 | `63291726c6fc84eb479ef2ee752e97e1187f6ce9c49fd27eee40ded588a8172a` |
| PawnIO official setup | 2.2.0 | `1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032` |

OpenRGB's download URL comes from its [release page](https://openrgb.org/releases.html); its digest is published in the [upstream release assets](https://github.com/CalcProgrammer1/OpenRGB/releases/expanded_assets/release_1.1). PawnIO's digest is published in its [upstream release assets](https://github.com/namazso/PawnIO.Setup/releases/expanded_assets/2.2.0). Its `-install -silent` switches and success code 3010 are also recorded in the [WinGet manifest](https://github.com/microsoft/winget-pkgs/blob/master/manifests/n/namazso/PawnIO/2.2.0/namazso.PawnIO.installer.yaml). Downloads and cached copies must match the pins before use. Never commit `build/cache/`.

Optional signing signs Prism executables/assemblies, the installer helper and the completed setup executable using the specified existing certificate and a timestamp server. It does not re-sign third-party binaries. Without a thumbprint the build works unsigned. Inno's generated uninstaller currently remains unsigned.

## Destination behavior

The installer requires admin and Windows 11 desktop on native x64, excluding ARM emulation and Windows Server. It installs per machine to Program Files/Prism and creates a Start menu shortcut. Before replacing an existing installation it gracefully stops owned Prism services; unexpected service executable paths abort setup.

After copying the payload, the helper secures `%ProgramData%/Prism`: SYSTEM and Administrators get full control, Users get read/execute. Existing child ACLs are reset to inherit. Users cannot write service settings that contain a privileged executable path. It preserves existing JSON settings while setting `launchOpenRgb=true` and the absolute bundled OpenRGB path. With no existing settings, other values use the service's defaults.

PawnIO runs silently only when its service registry key is missing; the helper verifies the bundled setup hash again. A reboot-required response propagates to Inno; Prism/App startup is deferred until reboot in that case. Existing PawnIO is retained without reinstalling or upgrading it.

The actual service name is **Prism**, matching `ScmLifetime.ServiceName`, with executable Prism.Service.exe. It runs as LocalSystem, delayed automatic start, with two recovery restarts and then no action. Prism.FanGuard.exe, if present, is registered similarly as **Prism.FanGuard**. Services start after successful configuration unless PawnIO requires a reboot.

Detected ASUS AsusFanControlService, LightingService, ArmouryCrateService and Corsair/iCUE services are listed individually with checked-by-default stop/disable choices. No vendor software is uninstalled. Previous startup type, delayed-auto flag and running state are recorded in admin-protected `HKLM/SOFTWARE/Prism/Installer/DisabledServices` before changes; subsequent installs preserve the first recorded state. Failed installation attempts stop Prism and attempt to restore recorded conflicts.

An unchecked startup task adds `"Prism.App.exe" --tray` to the original user's HKCU Run key through an unelevated helper. The final page offers to launch the app as the original user and reminds the user to disable **Download & Install ARMOURY CRATE app** in BIOS. No ASUS, Corsair or AMD Software suite is installed.

Uninstall first gracefully stops Prism/FanGuard, allowing the service's normal BIOS fan handoff. Because the current service only releases its OpenRGB process handle, the helper separately terminates only OpenRGB processes whose executable path matches this installation's bundled copy. It deletes the Prism service registrations, restores recorded conflicts and their previous running state, removes matching startup entries from loaded user hives, and lets Inno remove installed files. A real pre-uninstall dialog provides an unchecked **remove settings** checkbox. Silent uninstall keeps settings. PawnIO remains installed because other applications can share it. Service-stop/restoration failures abort before deleting application files.

## Validation performed

- PowerShell parser checks passed for publish.ps1, package-check.ps1 and Configure-Prism.ps1.
- The PE import reader was exercised against dotnet.exe and Windows kernel32.dll, without executing either file. In-memory ACL construction passed without applying any ACL.
- `build/publish.ps1` was attempted. It reported missing Inno Setup and failed fetching OpenRGB with an HTTPS receive error. Shell networking uses a nonworking proxy (`127.0.0.1:9`). The network restriction was not bypassed. No publish payload or installer resulted.
- `scripts/package-check.ps1` was run. It correctly failed because `build/out/Prism` does not exist. **There is no passing package verification result.**
- Git inspection confirmed only permitted new source files; no existing tracked file was changed.

After a successful build, run:

```powershell
./scripts/package-check.ps1
```

The checker never launches packaged software. It checks publish metadata, required runtime/application files, all DLL assets declared in .deps.json, native PE imports against bundled files/Windows inbox dependencies, executable versions, absence of debug files, every manifest file hash/size, and unexpected files. It checks the pinned OpenRGB ZIP and independently compares its extracted contents, verifies PawnIO's pinned hash, and prints total size and the ten largest files. The build runs it before invoking ISCC.

## Open questions and remaining verification

1. On a network-enabled build machine, complete publish, resolve any real shared-file collisions or missing native DLLs, run package-check to PASS, and compile with Inno Setup. The Inno source has not been compiler-validated here. Do not ship this as a validated binary yet.
2. Test installation, reboot-required PawnIO, startup opt-in, alternate UAC credentials, conflict opt-outs/restoration, failure recovery, retained settings and explicit settings removal on a disposable fresh Windows 11 x64 VM. Test BIOS fan restoration and AMD-driver-only behavior on representative physical hardware. These actions were deliberately not performed on this PC.
3. No FanGuard exists in this checkout. Its future executable must implement the assumed Prism.FanGuard SCM name and graceful stop contract; confirm with its owner before shipping it.
4. OpenRGB's archive must include every non-inbox native dependency, including any required MSVC runtime. The checker rejects missing imports rather than silently assuming vendor software supplied them. Dynamic LoadLibrary dependencies and actual hardware access still need fresh-PC validation.
5. HKCU Run cleanup covers loaded user hives. A user profile that is unloaded at uninstall can retain a stale Prism startup entry; offline hive cleanup is not implemented. Per-user AppData settings are also retained; the remove-settings checkbox specifically targets ProgramData as requested.
6. Configure failure recovery restores vendor services, but is not a complete transactional rollback of previous Prism binaries/settings or PawnIO installation. Validate upgrade/retry behavior; use uninstall to clean up a failed fresh install. Review signing of the generated uninstaller and third-party license/source distribution before a public release.
