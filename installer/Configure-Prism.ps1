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
$stateRoot = 'HKLM:\SOFTWARE\Prism\Installer'
$data = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Prism'
$serviceRoot = 'HKLM:\SYSTEM\CurrentControlSet\Services'
$runKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
# The app and the service live in their own folders: they ship different builds of the same runtime DLLs.
$appDir = Join-Path $InstallDir 'app'
$serviceDir = Join-Path $InstallDir 'service'
$startupCommand = '"' + (Join-Path $appDir 'Prism.App.exe') + '" --tray'
if ($Mode -eq 'UserStartup') {
    # Inno runs this in the original unelevated user's context, even with alternate UAC credentials.
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($runKey)
    try { $key.SetValue('Prism', $startupCommand, [Microsoft.Win32.RegistryValueKind]::String) } finally { $key.Dispose() }
    exit 0
}
if (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Installer helper requires administrator privileges.' }
function Invoke-Sc([string[]]$Arguments) {
    & "$env:SystemRoot/System32/sc.exe" @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe $($Arguments -join ' ') failed ($LASTEXITCODE)." }
}
function Stop-PrismService([string]$Name) {
    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        # Graceful SCM stop waits for Prism's RestoreDefaults/BIOS fan handoff. Never kill it.
        $service.Stop()
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(90))
    }
}
function Stop-BundledOpenRgb {
    # ServiceRuntime releases its handle without terminating OpenRGB. Remove only our copy.
    $expected = Join-Path $InstallDir 'openrgb\OpenRGB.exe'
    foreach ($process in @(Get-Process -Name OpenRGB -ErrorAction SilentlyContinue)) {
        if ($process.Path -eq $expected) {
            Stop-Process -Id $process.Id -Force
            if (!$process.WaitForExit(30000)) { throw 'Bundled OpenRGB did not exit.' }
        }
    }
}
function Assert-OwnedService([string]$Name, [string]$Executable) {
    if (Test-Path "$serviceRoot/$Name") {
        $image = (Get-ItemProperty "$serviceRoot/$Name").ImagePath
        # Builds before the split ran the service from $InstallDir itself; that is still ours and gets moved.
        # 1.0.0 lost the quotes when reinstalling (see Register-PrismService): accept that form so this setup can repair it.
        $unquoted = @((Join-Path $serviceDir $Executable), (Join-Path $InstallDir $Executable))
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
function Register-PrismService([string]$Name, [string]$Executable) {
    Assert-OwnedService $Name $Executable
    $binary = '"' + (Join-Path $serviceDir $Executable) + '"'
    if (!(Get-Service $Name -ErrorAction SilentlyContinue)) {
        New-Service -Name $Name -BinaryPathName $binary -DisplayName $Name -StartupType Automatic | Out-Null
    } else {
        # Not through sc.exe: Windows PowerShell drops the quotes inside an argument, leaving a path with spaces unquoted.
        Set-ItemProperty "$serviceRoot/$Name" -Name ImagePath -Value $binary
    }
    if ($Name -eq 'Prism') {
        Invoke-Sc @('config',$Name,'DisplayName=','Control Spawn')
        Invoke-Sc @('description',$Name,'Control Spawn: fans, lighting, lamps and router in the background')
    }
    Invoke-Sc @('config',$Name,'start=','delayed-auto','obj=','LocalSystem')
    Invoke-Sc @('failure',$Name,'reset=','86400','actions=','restart/5000/restart/15000/none/0')
    Invoke-Sc @('failureflag',$Name,'1')
}
try {
    Assert-OwnedService 'Prism' 'Prism.Service.exe'
    Assert-OwnedService 'Prism.FanGuard' 'Prism.FanGuard.exe'
    if ($Mode -eq 'Prepare') {
        Stop-PrismService 'Prism.FanGuard'
        Stop-PrismService 'Prism'
        Stop-BundledOpenRgb
        exit 0
    }
    if ($Mode -eq 'Install') {
        foreach ($required in @('service/Prism.Service.exe','app/Prism.App.exe','openrgb/OpenRGB.exe','dependencies/PawnIO_setup.exe')) {
            if (!(Test-Path -LiteralPath (Join-Path $InstallDir $required))) { throw "Missing payload: $required" }
        }
        New-Item -ItemType Directory -Force -Path $data | Out-Null
        if ((Get-Item -LiteralPath $data).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'ProgramData/Prism must not be a reparse point.' }
        if (@(Get-ChildItem -LiteralPath $data -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'ProgramData/Prism contains a reparse point.' }
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
            if ($LASTEXITCODE -ne 0) { throw 'Failed to secure ProgramData/Prism children.' }
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
            Stop-PrismService $name
            Set-Service -Name $name -StartupType Disabled
        }
        # The service gives commands only to the app whose hashes are recorded here (PipeClientVerifier). ProgramData/Prism
        # is writable by administrators and SYSTEM only (set above), so a user can't swap in another app.
        $identity = Join-Path $data 'install'
        New-Item -ItemType Directory -Force -Path $identity | Out-Null
        $hashes = @{}
        foreach ($name in @('Prism.App.exe', 'Prism.App.dll')) { $hashes[$name] = (Get-FileHash -LiteralPath (Join-Path $appDir $name) -Algorithm SHA256).Hash }
        $hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $identity 'app-hashes.json') -Encoding UTF8
        if (-not [Diagnostics.EventLog]::SourceExists('Prism')) { New-EventLog -LogName Application -Source Prism }
        Register-PrismService 'Prism' 'Prism.Service.exe'
        if (Test-Path "$serviceDir/Prism.FanGuard.exe") { Register-PrismService 'Prism.FanGuard' 'Prism.FanGuard.exe' }
        if (!$reboot) {
            Start-Service 'Prism'
            (Get-Service 'Prism').WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
            if (Test-Path "$serviceDir/Prism.FanGuard.exe") { Start-Service 'Prism.FanGuard' }
        }
        if ($reboot) { exit 3010 }
    }
    if ($Mode -eq 'Uninstall') {
        Stop-PrismService 'Prism.FanGuard'
        Stop-PrismService 'Prism'
        Stop-BundledOpenRgb
        foreach ($name in @('Prism.FanGuard','Prism')) {
            if (Get-Service $name -ErrorAction SilentlyContinue) { Invoke-Sc @('delete',$name) }
        }
        Restore-Conflicts
        # Remove matching Run entries in loaded user hives, including an alternate original user.
        foreach ($sid in [Microsoft.Win32.Registry]::Users.GetSubKeyNames()) {
            $key = [Microsoft.Win32.Registry]::Users.OpenSubKey("$sid\$runKey", $true)
            $legacy = '"' + (Join-Path $InstallDir 'Prism.App.exe') + '" --tray'
            if ($key) { try { if ($key.GetValue('Prism') -in $startupCommand, $legacy) { $key.DeleteValue('Prism') } } finally { $key.Dispose() } }
        }
        if ($RemoveSettings -and (Test-Path -LiteralPath $data)) {
            $expected = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Prism'))
            if ($data -ne $expected -or (Get-Item -LiteralPath $data).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe settings removal path.' }
            if (@(Get-ChildItem -LiteralPath $data -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Settings contain a reparse point; refusing recursive removal.' }
            Remove-Item -LiteralPath $data -Recurse -Force
        }
        if (Test-Path $stateRoot) { Remove-Item -LiteralPath $stateRoot -Recurse }
        # PawnIO is shared with other hardware software and is deliberately retained.
    }
} catch {
    Write-Error -ErrorRecord $_ -ErrorAction Continue
    if ($Mode -eq 'Install') {
        try { Stop-PrismService 'Prism.FanGuard'; Stop-PrismService 'Prism'; Stop-BundledOpenRgb; Restore-Conflicts } catch { Write-Warning "Recovery needs attention: $_" }
    }
    exit 1
}
