[CmdletBinding()]
param([switch]$Offline)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# Unit tests only: no service executable, installer, live script, or App project is invoked.
foreach ($name in @('Hardware', 'Lighting', 'Router', 'Service')) {
    $project = Join-Path $root "tests/ControlSpawn.$name.Tests/ControlSpawn.$name.Tests.csproj"
    $arguments = @('test', $project, '-c', 'Release', '--nologo', '-m:1', '-v', 'minimal')
    if ($Offline) { $arguments += @('-p:RestoreIgnoreFailedSources=true', '-p:NuGetAudit=false') }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "$name unit tests failed ($LASTEXITCODE)." }
}
