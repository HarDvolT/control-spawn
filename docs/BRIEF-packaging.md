# Brief (Codex, parallel branch `package`): one-click installer for a fresh Windows PC

You work in your own git worktree on branch `package`. Others are changing src/ at the same time: **only add new files**
under `installer/`, `build/`, `scripts/package*.ps1` and docs/STATUS-packaging.md; do not edit existing src/ or tests/.
No commits. Never install, uninstall, start or stop anything on this PC; do not run the installer you build.

Goal: a single `PrismSetup.exe` that makes Prism work on a **fresh Windows 11 install with no ASUS, Corsair or AMD
Software installed** (AMD graphics driver from Windows Update only).

1. `build/publish.ps1`: `dotnet publish` Prism.Service and Prism.App **self-contained, win-x64, Release**,
   ReadyToRun, single folder `out/Prism/` (no .NET install needed). Trim only if tests prove it safe (WPF: don't trim).
2. **Bundled dependencies** (downloaded by the build script with pinned versions + SHA-256 checks, cached in
   `build/cache/`, never committed): OpenRGB 1.1 portable (Windows 64) → `out/Prism/openrgb/`; PawnIO installer
   (namazso/PawnIO.Setup, pinned release) → run silently by the installer only if the PawnIO service is missing.
3. **Installer** with Inno Setup 6 (`installer/Prism.iss`; build via `ISCC.exe` if present, else print how to install
   it): per-machine install to `C:\Program Files\Prism`, requires admin, Windows 11 x64 check.
   Steps: install files → install PawnIO if missing → register `Prism.Service` as a Windows service (delayed auto start,
   LocalSystem, recovery restart ×2) and the FanGuard if present → service setting `LaunchOpenRgb=true` with the
   bundled path → ACL `%ProgramData%\Prism` → per-user "Start with Windows" for Prism.App (HKCU Run with `--tray`) as an
   opt-in checkbox → Start menu shortcut → launch app at the end.
   Detect conflicting software and **offer** (checkboxes, default on, clearly worded) to stop/disable: ASUS
   AsusFanControlService, LightingService, ArmouryCrateService; Corsair iCUE services. Do not uninstall them.
   Show a final page telling the user to disable "Download & Install ARMOURY CRATE app" in the BIOS.
4. **Uninstaller**: stop and remove services, hand fans to BIOS (service stop does this), restore any services it
   disabled (record them at install), remove files; keep `%ProgramData%\Prism` unless the user ticks "remove settings".
5. **Code signing hooks**: optional `-CertificateThumbprint` parameter; unsigned builds still work.
6. `scripts/package-check.ps1`: static checks of the output folder (all DLLs present, versions, no debug files,
   OpenRGB hash, size report). Run it, not the installer.

Finish with docs/STATUS-packaging.md: how to build the installer, what it does on a fresh PC, open questions.
