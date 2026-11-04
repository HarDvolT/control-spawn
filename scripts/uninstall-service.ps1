[CmdletBinding(SupportsShouldProcess)]
param()
$ErrorActionPreference = 'Stop'
# ControlSpawn is the 1.0.3 key; Prism is the 1.0.0-1.0.2 key. Remove whichever is installed.
foreach ($name in 'ControlSpawn', 'Prism') {
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($null -eq $service) { continue }
    if ($PSCmdlet.ShouldProcess($name, 'Stop and remove Windows service')) {
        if ($service.Status -ne 'Stopped') {
            # Graceful stop: the service hands the fans back to the BIOS. Never kill it.
            Stop-Service -InputObject $service -ErrorAction Stop
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
        & sc.exe delete $name
        if ($LASTEXITCODE -ne 0) { throw "sc.exe delete $name failed: $LASTEXITCODE" }
    }
}
