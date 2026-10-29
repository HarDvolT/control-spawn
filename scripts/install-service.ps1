[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path -LiteralPath $Executable).Path
if ([IO.Path]::GetFileName($resolvedExecutable) -ne 'Prism.Service.exe') { throw 'Expected Prism.Service.exe.' }
if ($PSCmdlet.ShouldProcess('Prism', 'Create delayed automatic Windows service and restart recovery policy')) {
    & sc.exe create Prism binPath= ('"' + $resolvedExecutable + '"') start= delayed-auto obj= LocalSystem DisplayName= 'Prism'
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed: $LASTEXITCODE" }
    & sc.exe description Prism 'Prism hardware, lighting and router background host'
    if ($LASTEXITCODE -ne 0) { throw "sc.exe description failed: $LASTEXITCODE" }
    & sc.exe failure Prism reset= 86400 actions= restart/5000/restart/15000/restart/60000
    if ($LASTEXITCODE -ne 0) { throw "sc.exe failure failed: $LASTEXITCODE" }
    & sc.exe failureflag Prism 1
    if ($LASTEXITCODE -ne 0) { throw "sc.exe failureflag failed: $LASTEXITCODE" }
}
# Deliberately does not start the service. Review configuration/ownership before a real deployment.
