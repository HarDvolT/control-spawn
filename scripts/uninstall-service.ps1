[CmdletBinding(SupportsShouldProcess)]
param()
$ErrorActionPreference = 'Stop'
if ($PSCmdlet.ShouldProcess('Prism', 'Stop and remove Windows service')) {
    $prismService = Get-Service -Name Prism -ErrorAction Stop
    if ($prismService.Status -ne 'Stopped') {
        Stop-Service -InputObject $prismService -ErrorAction Stop
        $prismService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete Prism
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed: $LASTEXITCODE" }
}
