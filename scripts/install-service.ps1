[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path -LiteralPath $Executable).Path
if ([IO.Path]::GetFileName($resolvedExecutable) -ne 'Prism.Service.exe') { throw 'Expected Prism.Service.exe.' }
$installDirectory = Split-Path -Parent $resolvedExecutable
if ((Split-Path -Leaf $installDirectory) -ne 'Prism') { throw 'Use a dedicated installation directory named Prism.' }
for ($ancestor = [IO.DirectoryInfo]$installDirectory; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Installation path cannot contain a reparse point.' }
}
foreach ($name in @('Prism.App.exe', 'Prism.App.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $installDirectory $name))) { throw "Missing installed app file: $name" }
}
function Set-PrismDirectoryAcl([string]$Path, [bool]$AllowRead) {
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
function Get-PrismChildren([string]$Path) {
    foreach ($entry in Get-ChildItem -LiteralPath $Path -Force) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points are not supported in Prism directories.' }
        $entry
        if ($entry.PSIsContainer) { Get-PrismChildren $entry.FullName }
    }
}
if ($PSCmdlet.ShouldProcess('Prism', 'Secure installation, record app hashes, and create delayed automatic service with recovery')) {
    $existingService = Get-Service -Name Prism -ErrorAction SilentlyContinue
    if ($null -ne $existingService -and $existingService.Status -ne 'Stopped') { throw 'Stop Prism before updating its installation.' }
    # Install into a dedicated directory: regular users must never be able to replace trusted binaries or dependencies.
    Set-PrismDirectoryAcl $installDirectory $true
    Get-PrismChildren $installDirectory | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points are not supported in an installation.' }
        $acl = Get-Acl -LiteralPath $_.FullName
        $acl.SetAccessRuleProtection($false, $false)
        foreach ($rule in @($acl.Access | Where-Object { -not $_.IsInherited })) { $acl.RemoveAccessRuleSpecific($rule) }
        Set-Acl -LiteralPath $_.FullName -AclObject $acl
    }
    $prismData = Join-Path $env:ProgramData 'Prism'
    Set-PrismDirectoryAcl $prismData $false
    # Remove any explicit grants left on data files by older installations before recording trust.
    Get-PrismChildren $prismData | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points are not supported in Prism data.' }
        $acl = Get-Acl -LiteralPath $_.FullName
        $acl.SetAccessRuleProtection($false, $false)
        foreach ($rule in @($acl.Access | Where-Object { -not $_.IsInherited })) { $acl.RemoveAccessRuleSpecific($rule) }
        Set-Acl -LiteralPath $_.FullName -AclObject $acl
    }
    $identityDirectory = Join-Path $prismData 'install'
    Set-PrismDirectoryAcl $identityDirectory $false
    Set-PrismDirectoryAcl (Join-Path $prismData 'secrets') $false
    Set-PrismDirectoryAcl (Join-Path $prismData 'logs') $false
    $hashes = @{}
    foreach ($name in @('Prism.App.exe', 'Prism.App.dll')) { $hashes[$name] = (Get-FileHash -LiteralPath (Join-Path $installDirectory $name) -Algorithm SHA256).Hash }
    $hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $identityDirectory 'app-hashes.json') -Encoding UTF8
    if (-not [Diagnostics.EventLog]::SourceExists('Prism')) { New-EventLog -LogName Application -Source Prism }

    if ($null -eq $existingService) {
        & sc.exe create Prism binPath= ('"' + $resolvedExecutable + '"') start= delayed-auto obj= LocalSystem DisplayName= 'Prism'
    } else {
        & sc.exe config Prism binPath= ('"' + $resolvedExecutable + '"') start= delayed-auto obj= LocalSystem DisplayName= 'Prism'
    }
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create/config failed: $LASTEXITCODE" }
    & sc.exe description Prism 'Prism hardware, lighting and router background host'
    if ($LASTEXITCODE -ne 0) { throw "sc.exe description failed: $LASTEXITCODE" }
    & sc.exe failure Prism reset= 86400 actions= restart/5000/restart/5000/run/0 command= ('"' + $env:SystemRoot + '\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -WindowStyle Hidden -Command "Write-EventLog -LogName Application -Source Prism -EntryType Error -EventId 1001 -Message ''Prism stopped after two recovery restarts; operator intervention required.''"')
    if ($LASTEXITCODE -ne 0) { throw "sc.exe failure failed: $LASTEXITCODE" }
    & sc.exe failureflag Prism 1
    if ($LASTEXITCODE -ne 0) { throw "sc.exe failureflag failed: $LASTEXITCODE" }
}
# Deliberately does not start the service. Review configuration/ownership before a real deployment.
