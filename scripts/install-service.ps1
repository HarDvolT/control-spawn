[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path -LiteralPath $Executable).Path
if ([IO.Path]::GetFileName($resolvedExecutable) -ne 'ControlSpawn.Service.exe') { throw 'Expected ControlSpawn.Service.exe.' }
# 1.0.3 names; Setup (installer/Configure-ControlSpawn.ps1) is the supported install.
$serviceName = 'ControlSpawn'
$displayName = 'Control Spawn Hardware Service'
$installDirectory = Split-Path -Parent $resolvedExecutable
if ((Split-Path -Leaf $installDirectory) -ne 'service') { throw 'Use a dedicated installation directory named service.' }
for ($ancestor = [IO.DirectoryInfo]$installDirectory; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Installation path cannot contain a reparse point.' }
}
foreach ($name in @('ControlSpawn.exe', 'ControlSpawn.dll', 'ControlSpawn.FanGuard.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $installDirectory $name))) { throw "Missing installed app file: $name" }
}
function Set-ControlSpawnDirectoryAcl([string]$Path, [bool]$AllowRead) {
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $identity = New-Object System.Security.Principal.SecurityIdentifier($sid)
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    if ($AllowRead) {
        $identity = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-545')
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($identity, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    if (-not (Test-Path -LiteralPath $Path)) { [IO.Directory]::CreateDirectory($Path, $acl) | Out-Null }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Get-ControlSpawnChildren([string]$Path) {
    foreach ($entry in Get-ChildItem -LiteralPath $Path -Force) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points are not supported in Control Spawn directories.' }
        $entry
        if ($entry.PSIsContainer) { Get-ControlSpawnChildren $entry.FullName }
    }
}
if ($PSCmdlet.ShouldProcess($serviceName, 'Secure installation, record app hashes, and create automatic service with recovery')) {
    # Two keys would fight over the pipe and the fans. Remove the 1.0.0-1.0.2 key first (uninstall-service.ps1 does both).
    if (Get-Service -Name Prism -ErrorAction SilentlyContinue) { throw 'The old Prism service is still installed. Run scripts/uninstall-service.ps1 first.' }
    $existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -ne $existingService -and $existingService.Status -ne 'Stopped') { throw "Stop $serviceName before updating its installation." }
    # Install into a dedicated directory: regular users must never be able to replace trusted binaries or dependencies.
    Set-ControlSpawnDirectoryAcl $installDirectory $true
    Get-ControlSpawnChildren $installDirectory | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points are not supported in an installation.' }
        $acl = Get-Acl -LiteralPath $_.FullName
        $acl.SetAccessRuleProtection($false, $false)
        foreach ($rule in @($acl.Access | Where-Object { -not $_.IsInherited })) { $acl.RemoveAccessRuleSpecific($rule) }
        Set-Acl -LiteralPath $_.FullName -AclObject $acl
    }
    $dataRoot = Join-Path $env:ProgramData 'Control Spawn'
    Set-ControlSpawnDirectoryAcl $dataRoot $false
    # Remove any explicit grants left on data files by older installations before recording trust.
    Get-ControlSpawnChildren $dataRoot | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points are not supported in Control Spawn data.' }
        $acl = Get-Acl -LiteralPath $_.FullName
        $acl.SetAccessRuleProtection($false, $false)
        foreach ($rule in @($acl.Access | Where-Object { -not $_.IsInherited })) { $acl.RemoveAccessRuleSpecific($rule) }
        Set-Acl -LiteralPath $_.FullName -AclObject $acl
    }
    $identityDirectory = Join-Path $dataRoot 'install'
    Set-ControlSpawnDirectoryAcl $identityDirectory $false
    Set-ControlSpawnDirectoryAcl (Join-Path $dataRoot 'secrets') $false
    Set-ControlSpawnDirectoryAcl (Join-Path $dataRoot 'logs') $false
    $hashes = @{}
    foreach ($name in @('ControlSpawn.exe', 'ControlSpawn.dll')) { $hashes[$name] = (Get-FileHash -LiteralPath (Join-Path $installDirectory $name) -Algorithm SHA256).Hash }
    $hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $identityDirectory 'app-hashes.json') -Encoding UTF8
    if (-not [Diagnostics.EventLog]::SourceExists('Control Spawn')) { New-EventLog -LogName Application -Source 'Control Spawn' }

    # The path goes in through New-Service or the registry, never sc.exe: Windows PowerShell 5.1 drops the quotes inside an
    # argument, leaving a path with spaces unquoted (that broke reinstall and uninstall in 1.0.0 and 1.0.1).
    $binary = '"' + $resolvedExecutable + '"'
    if ($null -eq $existingService) { New-Service -Name $serviceName -BinaryPathName $binary -DisplayName $displayName -StartupType Automatic | Out-Null }
    else { Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name ImagePath -Value $binary }
    # Plain automatic, as setup does: delayed start left fans and lighting untouched for over 2 minutes after every boot.
    & sc.exe config $serviceName start= auto obj= LocalSystem DisplayName= $displayName
    if ($LASTEXITCODE -ne 0) { throw "sc.exe config failed: $LASTEXITCODE" }
    Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name DelayedAutoStart -Value 0
    & sc.exe description $serviceName 'Control Spawn: fans, lighting, lamps and router in the background'
    if ($LASTEXITCODE -ne 0) { throw "sc.exe description failed: $LASTEXITCODE" }
    # Same recovery as setup. No run-command action: its quoted command line would go through sc.exe and lose its quotes.
    & sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/none/0
    if ($LASTEXITCODE -ne 0) { throw "sc.exe failure failed: $LASTEXITCODE" }
    & sc.exe failureflag $serviceName 1
    if ($LASTEXITCODE -ne 0) { throw "sc.exe failureflag failed: $LASTEXITCODE" }
}
# Deliberately does not start the service. Review configuration/ownership before a real deployment.
