# Invoked ONLY by the installer/uninstaller on the destination PC. Never run during packaging.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Prepare','Install','Uninstall','UserStartup')][string]$Mode,
    [Parameter(Mandatory)][string]$InstallDir,
    [string]$DisableServices = '',
    [switch]$RemoveSettings
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$InstallDir = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
$stateRoot = 'HKLM:\SOFTWARE\ControlSpawn\Installer'
$dataFolder = 'Control Spawn'
$data = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) $dataFolder
$eventSource = 'Control Spawn'
$serviceRoot = 'HKLM:\SYSTEM\CurrentControlSet\Services'
$runKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$shortcutName = 'Control Spawn.lnk'
$approvedKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
# The app and the service live in their own folders: they ship different builds of the same runtime DLLs.
$appDir = Join-Path $InstallDir 'app'
$serviceDir = Join-Path $InstallDir 'service'
# The Prism names below exist only so setup can move older installs over and leave nothing of them behind.
$serviceName = 'ControlSpawn'
$serviceDisplayName = 'Control Spawn Hardware Service'
$serviceExe = 'ControlSpawn.Service.exe'
$appExe = 'ControlSpawn.exe'
$legacyServiceName = 'Prism'
$legacyServiceExe = 'Prism.Service.exe'
$legacyAppExe = 'Prism.App.exe'
$legacyName = 'Prism'
$legacyInstallDir = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) $legacyName
$legacyData = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) $legacyName
$legacyStateKey = "HKLM:\SOFTWARE\$legacyName"
$startupCommand = '"' + (Join-Path $appDir $appExe) + '" --tray'
if ($Mode -eq 'UserStartup') {
    # Inno runs this in the original unelevated user's context, even with alternate UAC credentials.
    # A Startup shortcut, not the Run key: Windows 25H2 never ran that entry on the owner's PC (see ShortcutStartup).
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('Startup')) $shortcutName))
    $link.TargetPath = Join-Path $appDir $appExe
    $link.Arguments = '--tray'
    $link.WorkingDirectory = $appDir
    $link.Save()
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runKey, $true)
    if ($key) { try { $key.DeleteValue('Prism', $false) } finally { $key.Dispose() } }
    exit 0
}
if (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Installer helper requires administrator privileges.' }
# Setup runs this hidden, so without a transcript a failure leaves no reason anywhere.
try { Start-Transcript -LiteralPath (Join-Path $env:TEMP 'ControlSpawn-setup-helper.log') -Append | Out-Null } catch { Write-Warning "No helper log: $_" }
function Invoke-Sc([string[]]$Arguments) {
    & "$env:SystemRoot/System32/sc.exe" @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe $($Arguments -join ' ') failed ($LASTEXITCODE)." }
}
function Stop-ControlSpawnService([string]$Name) {
    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        # Graceful SCM stop waits for Prism's RestoreDefaults/BIOS fan handoff. Never kill it.
        $service.Stop()
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(90))
    }
}
function Stop-InstalledProcess([string]$Path) {
    # Match the full path: only our copy, never another install or a same-named program.
    $name = [IO.Path]::GetFileNameWithoutExtension($Path)
    foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
        if ($process.Path -eq $Path) {
            Stop-Process -Id $process.Id -Force
            if (!$process.WaitForExit(30000)) { throw "$name did not exit." }
        }
    }
}
function Stop-BundledOpenRgb {
    # ServiceRuntime releases its handle without terminating OpenRGB.
    foreach ($root in $InstallDir, $legacyInstallDir) { Stop-InstalledProcess (Join-Path $root 'openrgb\OpenRGB.exe') }
}
function Stop-InstalledApp {
    # Inno's CloseApplications left the tray app running on uninstall (app\ stayed locked on disk), and it can't see an app
    # running from the old folder at all, because setup replaces no file there.
    foreach ($root in $InstallDir, $legacyInstallDir) {
        foreach ($executable in $appExe, $legacyAppExe) { Stop-InstalledProcess (Join-Path $root "app\$executable") }
    }
}
function Assert-OwnedService([string]$Name) {
    if (Test-Path "$serviceRoot/$Name") {
        $image = (Get-ItemProperty "$serviceRoot/$Name").ImagePath
        # Builds before the split ran the service from $InstallDir itself; that is still ours and gets moved.
        # 1.0.0 lost the quotes when reinstalling (see Register-ControlSpawnService): accept that form so this setup can repair it.
        # Either key may point at either exe name: 1.0.0-1.0.2 installed Prism.Service.exe, 1.0.3 ControlSpawn.Service.exe.
        $unquoted = @(foreach ($root in $InstallDir, $legacyInstallDir) {
            foreach ($executable in $serviceExe, $legacyServiceExe) { (Join-Path $root "service\$executable"); (Join-Path $root $executable) }
        })
        $ours = @($unquoted | ForEach-Object { '"' + $_ + '"' }) + $unquoted
        if ($image -notin $ours) { throw "Refusing to modify unrelated service $Name ($image)." }
    }
}
function Restore-Conflicts {
    if (!(Test-Path "$stateRoot/DisabledServices")) { return }
    foreach ($record in Get-ChildItem "$stateRoot/DisabledServices") {
        $saved = Get-ItemProperty $record.PSPath
        $name = $record.PSChildName
        if (!(Test-Path "$serviceRoot/$name")) { continue }
        switch ([int]$saved.Start) {
            2 { Set-Service -Name $name -StartupType Automatic }
            3 { Set-Service -Name $name -StartupType Manual }
            4 { Set-Service -Name $name -StartupType Disabled }
            default { throw "Unexpected saved startup type for $name" }
        }
        if ($saved.HadDelayed) { Set-ItemProperty "$serviceRoot/$name" -Name DelayedAutoStart -Value ([int]$saved.Delayed) }
        else { Remove-ItemProperty "$serviceRoot/$name" -Name DelayedAutoStart -ErrorAction SilentlyContinue }
        if ($saved.Start -eq 2 -and $saved.HadDelayed -and $saved.Delayed -eq 1) { Invoke-Sc @('config',$name,'start=','delayed-auto') }
        if ($saved.WasRunning -and $saved.Start -ne 4) { Start-Service -Name $name }
        Remove-Item -LiteralPath $record.PSPath
    }
}
function Remove-ControlSpawnService([string]$Name) {
    # Graceful stop first (the BIOS fan handoff), then delete. Never kill the service.
    Stop-ControlSpawnService $Name
    if (!(Get-Service $Name -ErrorAction SilentlyContinue)) { return }
    & "$env:SystemRoot/System32/sc.exe" delete $Name | Out-Host
    # 1072: already marked for deletion (an open Services window holds it); Windows removes it once that closes.
    if ($LASTEXITCODE -notin 0,1072) { throw "sc.exe delete $Name failed ($LASTEXITCODE)." }
}
function Register-ControlSpawnService {
    Assert-OwnedService $serviceName
    $binary = '"' + (Join-Path $serviceDir $serviceExe) + '"'
    if (!(Get-Service $serviceName -ErrorAction SilentlyContinue)) {
        New-Service -Name $serviceName -BinaryPathName $binary -DisplayName $serviceDisplayName -StartupType Automatic | Out-Null
    } else {
        # Not through sc.exe: Windows PowerShell drops the quotes inside an argument, leaving a path with spaces unquoted.
        Set-ItemProperty "$serviceRoot/$serviceName" -Name ImagePath -Value $binary
    }
    Invoke-Sc @('config',$serviceName,'DisplayName=',$serviceDisplayName)
    Invoke-Sc @('description',$serviceName,'Control Spawn: fans, lighting, lamps and router in the background')
    # Plain automatic: delayed start left fans and lighting untouched for over 2 minutes after every boot.
    Invoke-Sc @('config',$serviceName,'start=','auto','obj=','LocalSystem')
    Set-ItemProperty "$serviceRoot/$serviceName" -Name DelayedAutoStart -Value 0
    Invoke-Sc @('failure',$serviceName,'reset=','86400','actions=','restart/5000/restart/15000/none/0')
    Invoke-Sc @('failureflag',$serviceName,'1')
}
function Update-StartupShortcuts {
    # Older versions left "Control Spawn.lnk" pointing into the old install folder, which setup removes. The app counts any
    # existing shortcut as "start with Windows", and UserStartup only runs when the tray option is ticked, so repoint it
    # here for every profile; remove it if it can't be rewritten. Only our own shortcut name pointing at our old exe.
    $old = @(foreach ($root in $InstallDir, $legacyInstallDir) { (Join-Path $root "app\$appExe"); (Join-Path $root "app\$legacyAppExe"); (Join-Path $root $legacyAppExe) })
    $shell = New-Object -ComObject WScript.Shell
    foreach ($entry in Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList') {
        $folder = (Get-ItemProperty $entry.PSPath).ProfileImagePath
        if (!$folder) { continue }
        $path = Join-Path $folder "AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\$shortcutName"
        if (!(Test-Path -LiteralPath $path)) { continue }
        try {
            $link = $shell.CreateShortcut($path)
            if ($link.TargetPath -notin $old) { continue }
            $link.TargetPath = Join-Path $appDir $appExe
            $link.WorkingDirectory = $appDir
            $link.Save()
        } catch {
            Write-Warning "Could not repoint $path ($_); removing it."
            Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue
        }
    }
}
function Remove-EmptySoftwareKey([string]$Name) {
    # reg.exe, not Remove-Item: elevated PowerShell gets "Requested registry access is not allowed" on direct children of
    # HKLM\SOFTWARE (live, 2026-11-04). An empty key is only a leftover name, so it never fails an install or an uninstall.
    $key = "HKLM:\SOFTWARE\$Name"
    if (!(Test-Path $key) -or @(Get-ChildItem $key).Count -or (Get-Item $key).ValueCount) { return }
    & "$env:SystemRoot/System32/reg.exe" delete "HKLM\SOFTWARE\$Name" /f | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Warning "Could not remove $key ($LASTEXITCODE)." }
}
function Move-LegacyFolder([string]$From, [string]$To) {
    if (!(Test-Path -LiteralPath $From)) { return }
    if ((Get-Item -LiteralPath $From -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "$From must not be a reparse point." }
    # The stopped service's fan guard can hold a log open for a moment after it hands the fans back.
    for ($try = 1; ; $try++) {
        try {
            if (!(Test-Path -LiteralPath $To)) { Move-Item -LiteralPath $From -Destination $To; return }
            # A retried setup: what is already in the new folder wins.
            foreach ($child in Get-ChildItem -LiteralPath $From -Force) {
                if (!(Test-Path -LiteralPath (Join-Path $To $child.Name))) { Move-Item -LiteralPath $child.FullName -Destination $To }
            }
            Remove-Item -LiteralPath $From -Recurse -Force
            return
        } catch { if ($try -ge 15) { throw }; Start-Sleep -Seconds 1 }
    }
}
function Move-LegacyData {
    Move-LegacyFolder $legacyData $data
    $logs = Join-Path $data 'logs'
    if (Test-Path -LiteralPath $logs) {
        foreach ($log in Get-ChildItem -LiteralPath $logs -Filter 'prism-*.jsonl') { Rename-Item -LiteralPath $log.FullName -NewName ('controlspawn-' + $log.Name.Substring(6)) }
    }
    $state = "$legacyStateKey\Installer"
    if (Test-Path $state) {
        New-Item -Path $stateRoot -Force | Out-Null
        if (Test-Path "$state\DisabledServices") { Copy-Item -Path "$state\DisabledServices" -Destination $stateRoot -Recurse -Force }
        Remove-Item -Path $state -Recurse
    }
    Remove-EmptySoftwareKey $legacyName
    # App settings and widgets of every profile, moved as admin so a user who isn't signed in keeps theirs too. Another
    # program could own a folder called Prism, so only one holding nothing but our files moves.
    $ours = 'settings.json*','widgets.json*','led-presets.json*','lamp.json*','app.log*','router'
    foreach ($entry in Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList') {
        $folder = (Get-ItemProperty $entry.PSPath).ProfileImagePath
        if (!$folder) { continue }
        foreach ($area in 'AppData\Roaming', 'AppData\Local') {
            $from = Join-Path $folder "$area\$legacyName"
            if (!(Test-Path -LiteralPath $from)) { continue }
            $foreign = @(Get-ChildItem -LiteralPath $from -Force | Where-Object { $name = $_.Name; !($ours | Where-Object { $name -like $_ }) })
            if ($foreign.Count) { Write-Warning "Leaving $from in place: it holds files that are not ours."; continue }
            try { Move-LegacyFolder $from (Join-Path $folder "$area\$dataFolder") } catch { Write-Warning "Could not move $from ($_)." }
        }
    }
}
function Remove-LegacyInstall {
    # Runs last, once the new install works: until then the old folder is the way back. Only a folder our setup made.
    if ($legacyInstallDir -eq $InstallDir -or !(Test-Path -LiteralPath $legacyInstallDir)) { return }
    if (!(Test-Path -LiteralPath (Join-Path $legacyInstallDir 'unins000.dat')) -or !(Test-Path -LiteralPath (Join-Path $legacyInstallDir 'package-manifest.json'))) { Write-Warning "Leaving $legacyInstallDir in place: not a Control Spawn install."; return }
    if ((Get-Item -LiteralPath $legacyInstallDir -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The old install folder must not be a reparse point.' }
    if (@(Get-ChildItem -LiteralPath $legacyInstallDir -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'The old install folder contains a reparse point.' }
    if ([Diagnostics.EventLog]::SourceExists($legacyName)) { Remove-EventLog -Source $legacyName }
    for ($try = 1; ; $try++) {
        try { Remove-Item -LiteralPath $legacyInstallDir -Recurse -Force; return }
        catch { if ($try -ge 15) { Write-Warning "Could not remove $legacyInstallDir ($_)."; return }; Start-Sleep -Seconds 1 }
    }
}
try {
    Assert-OwnedService $legacyServiceName
    Assert-OwnedService $serviceName
    if ($Mode -eq 'Prepare') {
        # Both keys: an upgrade from 1.0.0-1.0.2 still runs as "Prism". The graceful stop is what hands the fans back.
        Stop-InstalledApp
        Stop-ControlSpawnService $serviceName
        Stop-ControlSpawnService $legacyServiceName
        Stop-BundledOpenRgb
        exit 0
    }
    if ($Mode -eq 'Install') {
        foreach ($required in @("service/$serviceExe",'service/ControlSpawn.FanGuard.exe',"app/$appExe",'openrgb/OpenRGB.exe','dependencies/PawnIO_setup.exe')) {
            if (!(Test-Path -LiteralPath (Join-Path $InstallDir $required))) { throw "Missing payload: $required" }
        }
        Move-LegacyData
        New-Item -ItemType Directory -Force -Path $data | Out-Null
        if ((Get-Item -LiteralPath $data).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The settings folder must not be a reparse point.' }
        if (@(Get-ChildItem -LiteralPath $data -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'The settings folder contains a reparse point.' }
        # Settings can launch an executable as SYSTEM: users receive read access, not write.
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($entry in @(@('S-1-5-18','FullControl'), @('S-1-5-32-544','FullControl'), @('S-1-5-32-545','ReadAndExecute'))) {
            $sid = New-Object Security.Principal.SecurityIdentifier($entry[0])
            $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid, $entry[1], 'ContainerInherit,ObjectInherit', 'None', 'Allow')
            $acl.AddAccessRule($rule)
        }
        Set-Acl -LiteralPath $data -AclObject $acl
        foreach ($child in Get-ChildItem -LiteralPath $data -Force) {
            & "$env:SystemRoot/System32/icacls.exe" $child.FullName '/reset' '/T' '/Q'
            if ($LASTEXITCODE -ne 0) { throw 'Failed to secure the settings folder.' }
        }
        New-Item -Path "$stateRoot/DisabledServices" -Force | Out-Null
        $settings = Join-Path $data 'settings.json'
        $config = [pscustomobject]@{}
        if (Test-Path -LiteralPath $settings) { $config = Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json }
        if ($null -eq $config -or $config -is [array] -or $config -is [string] -or $config -is [ValueType]) { throw 'Settings must be a JSON object.' }
        # Preserve all other existing settings. JSON web names match PipeContract.Json.
        $config | Add-Member -NotePropertyName launchOpenRgb -NotePropertyValue $true -Force
        $config | Add-Member -NotePropertyName openRgbPath -NotePropertyValue (Join-Path $InstallDir 'openrgb/OpenRGB.exe') -Force
        $config | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath "$settings.installing" -Encoding UTF8
        Move-Item -LiteralPath "$settings.installing" -Destination $settings -Force
        $pins = Get-Content "$InstallDir/dependencies/dependencies.json" -Raw | ConvertFrom-Json
        $pawnio = Join-Path $InstallDir 'dependencies/PawnIO_setup.exe'
        if ((Get-FileHash -LiteralPath $pawnio).Hash -ne $pins.pawnio.sha256) { throw 'PawnIO payload hash mismatch.' }
        $reboot = $false
        if (!(Test-Path "$serviceRoot/PawnIO")) {
            $process = Start-Process -FilePath $pawnio -ArgumentList '-install','-silent' -Wait -PassThru -WindowStyle Hidden
            if ($process.ExitCode -notin 0,3010) { throw "PawnIO setup failed ($($process.ExitCode))." }
            $reboot = $process.ExitCode -eq 3010
            if (!(Test-Path "$serviceRoot/PawnIO")) { throw 'PawnIO setup did not register its service.' }
        }
        foreach ($name in @($DisableServices.Split(',') | Where-Object { $_ })) {
            if ($name -notmatch '^[a-zA-Z0-9_. -]+$') { throw 'Invalid conflict service name.' }
            $key = "$serviceRoot/$name"
            $service = Get-Service -Name $name -ErrorAction SilentlyContinue
            if (!$service) { continue }
            if ($name -notin 'AsusFanControlService','LightingService','ArmouryCrateService' -and $name -notmatch '(?i)^(Corsair|iCUE)' -and $service.DisplayName -notmatch '(?i)\biCUE\b') { throw "Not a supported conflict: $name" }
            $record = "$stateRoot/DisabledServices/$name"
            if (!(Test-Path $record)) {
                $current = Get-ItemProperty $key
                New-Item $record -Force | Out-Null
                New-ItemProperty $record -Name Start -Value $current.Start -PropertyType DWord | Out-Null
                $hadDelayed = [bool]$current.PSObject.Properties['DelayedAutoStart']
                New-ItemProperty $record -Name HadDelayed -Value ([int]$hadDelayed) -PropertyType DWord | Out-Null
                $delayed = 0
                if ($hadDelayed) { $delayed = $current.DelayedAutoStart }
                New-ItemProperty $record -Name Delayed -Value $delayed -PropertyType DWord | Out-Null
                New-ItemProperty $record -Name WasRunning -Value ([int]($service.Status -eq 'Running')) -PropertyType DWord | Out-Null
            }
            Stop-ControlSpawnService $name
            Set-Service -Name $name -StartupType Disabled
        }
        # The service gives commands only to the app whose hashes are recorded here (PipeClientVerifier). The settings folder
        # is writable by administrators and SYSTEM only (set above), so a user can't swap in another app.
        $identity = Join-Path $data 'install'
        New-Item -ItemType Directory -Force -Path $identity | Out-Null
        $hashes = @{}
        foreach ($name in @($appExe, 'ControlSpawn.dll')) { $hashes[$name] = (Get-FileHash -LiteralPath (Join-Path $appDir $name) -Algorithm SHA256).Hash }
        $hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $identity 'app-hashes.json') -Encoding UTF8
        if (-not [Diagnostics.EventLog]::SourceExists($eventSource)) { New-EventLog -LogName Application -Source $eventSource }
        # Move a 1.0.0-1.0.2 install to the new key. Prepare already stopped it gracefully; this stops it again if needed.
        Remove-ControlSpawnService $legacyServiceName
        # The fan guard is not a service: the service starts ControlSpawn.FanGuard.exe itself before any fan write.
        Register-ControlSpawnService
        Update-StartupShortcuts
        if (!$reboot) {
            Start-Service $serviceName
            (Get-Service $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
        }
        Remove-LegacyInstall
        if ($reboot) { exit 3010 }
    }
    if ($Mode -eq 'Uninstall') {
        Stop-InstalledApp
        Stop-ControlSpawnService $serviceName
        Stop-ControlSpawnService $legacyServiceName
        Stop-BundledOpenRgb
        Remove-ControlSpawnService $serviceName
        Remove-ControlSpawnService $legacyServiceName
        Restore-Conflicts
        # Remove matching Run entries in loaded user hives, including an alternate original user.
        foreach ($sid in [Microsoft.Win32.Registry]::Users.GetSubKeyNames()) {
            $key = [Microsoft.Win32.Registry]::Users.OpenSubKey("$sid\$runKey", $true)
            $legacy = @(foreach ($root in $InstallDir, $legacyInstallDir) { foreach ($exe in $legacyAppExe, "app\$legacyAppExe", "app\$appExe") { '"' + (Join-Path $root $exe) + '" --tray' } })
            if ($key) { try { if ($key.GetValue('Prism') -in @($startupCommand) + $legacy) { $key.DeleteValue('Prism') } } finally { $key.Dispose() } }
            $approved = [Microsoft.Win32.Registry]::Users.OpenSubKey("$sid\$approvedKey", $true)
            if ($approved) { try { $approved.DeleteValue('Prism', $false) } finally { $approved.Dispose() } }
        }
        foreach ($profile in Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList') {
            $folder = (Get-ItemProperty $profile.PSPath).ProfileImagePath
            $link = Join-Path $folder "AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\$shortcutName"
            if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link }
        }
        if ($RemoveSettings -and (Test-Path -LiteralPath $data)) {
            $expected = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) $dataFolder))
            if ($data -ne $expected -or (Get-Item -LiteralPath $data).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe settings removal path.' }
            if (@(Get-ChildItem -LiteralPath $data -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Settings contain a reparse point; refusing recursive removal.' }
            Remove-Item -LiteralPath $data -Recurse -Force
        }
        if (Test-Path $stateRoot) { Remove-Item -LiteralPath $stateRoot -Recurse }
        Remove-EmptySoftwareKey 'ControlSpawn'
        if ([Diagnostics.EventLog]::SourceExists($eventSource)) { Remove-EventLog -Source $eventSource }
        # PawnIO is shared with other hardware software and is deliberately retained.
    }
} catch {
    Write-Error -ErrorRecord $_ -ErrorAction Continue
    if ($Mode -eq 'Install') {
        try { Stop-ControlSpawnService $serviceName; Stop-ControlSpawnService $legacyServiceName; Stop-BundledOpenRgb; Restore-Conflicts } catch { Write-Warning "Recovery needs attention: $_" }
    }
    exit 1
}
