# Runs the real Prism (fans, lights, lamps, router, automations) until you press Enter. Run as administrator.
# 1. Temporarily stops the programs that fight over the lights: ASUS "LightingService" and Corsair iCUE's service.
# 2. Starts OpenRGB as a local server, then the Prism service, then the Prism app (as you, not as administrator).
# 3. Every 3 s logs which scene automation is active and why, to the screen and docs\live-automations.txt.
# 4. On Q: closes Prism; fans go back to the BIOS and the ASUS/Corsair services are restarted.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'docs\live-automations.txt'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "NOT ADMIN - open PowerShell with 'Run as administrator'" -ForegroundColor Red; exit 1 }

$service = Join-Path $root 'src\Prism.Service\bin\Release\net8.0-windows\Prism.Service.exe'
$app = Join-Path $root 'src\Prism.App\bin\Release\net8.0-windows\Prism.App.exe'
$openrgb = 'C:\Users\Hakim\AppData\Local\Temp\orgb\OpenRGB.exe'
Get-Process Prism.App, Prism.Service -ErrorAction SilentlyContinue | Stop-Process -Force
# Always run the latest code: build the service and app first.
Write-Host 'Building the latest Prism...' -ForegroundColor Cyan
dotnet build (Join-Path $root 'Prism.sln') -c Release -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host 'Build failed - run dotnet build to see why' -ForegroundColor Red; exit 1 }
foreach ($f in $service, $app) { if (-not (Test-Path $f)) { Write-Host "Missing $f - build Release first" -ForegroundColor Red; exit 1 } }
"Live run $(Get-Date)" | Out-File $out -Encoding utf8
function Log($m) { "$(Get-Date -Format HH:mm:ss)  $m" | Out-File $out -Append -Encoding utf8; Write-Host "$(Get-Date -Format HH:mm:ss)  $m" }

Get-Process Prism.App, Prism.Service -ErrorAction SilentlyContinue | Stop-Process -Force
$stopped = @()
foreach ($name in 'LightingService', 'CorsairDeviceControlService') {
  $svc = Get-Service $name -ErrorAction SilentlyContinue
  if ($svc -and $svc.Status -eq 'Running') { Stop-Service $name -Force; $stopped += $name; Log "Stopped $name (restarted at the end)" }
}
$asusFan = Get-Service AsusFanControlService -ErrorAction SilentlyContinue
if ($asusFan -and $asusFan.Status -eq 'Running') { Log "WARNING: AsusFanControlService is running and will lock the fan chip. Fan control may not work." }

$orgb = $null; $svcProc = $null
try {
  if (Test-Path $openrgb) {
    $orgb = Start-Process $openrgb -ArgumentList '--server', '--noautoconnect' -WorkingDirectory (Split-Path $openrgb) -PassThru -WindowStyle Minimized
    Log 'OpenRGB starting (device detection takes about 15 s)...'; Start-Sleep -Seconds 18
  } else { Log "OpenRGB not found at $openrgb - lighting will be unavailable" }
  # A fresh stop file name per run, so a leftover from an earlier run can't stop this one.
  $stopFile = Join-Path $env:TEMP ('prism-live-' + [guid]::NewGuid().ToString('N') + '.stop')
  $svcProc = Start-Process $service -ArgumentList '--console', '--stop-file', $stopFile -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $env:TEMP 'prism-live.log')
  Start-Sleep -Seconds 6
  # Start the app through Explorer so it runs as you, not as administrator.
  Start-Process explorer.exe -ArgumentList ('"' + $app + '"')
  Log 'Prism is running. Try: lock the PC (Win+L), start a game, or set night hours on the Scenes page.'
  # Drop keys typed before now (like the Enter that ran this script), then stop only on Q.
  while ([Console]::KeyAvailable) { $null = [Console]::ReadKey($true) }
  Write-Host 'Press Q in this window to stop Prism.' -ForegroundColor Cyan

  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'prism', [System.IO.Pipes.PipeDirection]::InOut); $c.Connect(5000)
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
  $w.WriteLine('{"id":1,"command":"hello","args":{"version":1,"client":"app"}}'); $null = $r.ReadLine()
  $last = ''; $id = 2
  while ($true) {
    if ([Console]::KeyAvailable -and [Console]::ReadKey($true).Key -eq 'Q') { break }
    if ($svcProc.HasExited) { Log 'The Prism service stopped on its own - see prism-live.log in your temp folder'; break }
    $w.WriteLine("{""id"":$id,""command"":""getState""}"); $id++
    # Timed reads, so a slow reply can never stop Enter from working; pushed state updates are skipped.
    $line = $null
    for ($i = 0; $i -lt 20 -and -not $line; $i++) {
      $t = $r.ReadLineAsync(); if (-not $t.Wait(5000)) { break }
      if ($t.Result -match '"type":"response"') { $line = $t.Result }
    }
    $auto = if ($line) { try { ($line | ConvertFrom-Json).data.automations } catch { $null } } else { $null }
    # Empty fields are left out of the reply, so "no rule" shows up as a missing rule.
    $now = if (-not $line) { 'No reply from the Prism service' }
      elseif ($null -eq $auto) { 'Service did not report automations' }
      elseif ($auto.rule) { "Automation ON: $($auto.scene) because $($auto.detail)" }
      else { 'No automation active (your own scene)' }
    if ($now -ne $last) { Log $now; $last = $now }
    for ($s = 0; $s -lt 30 -and -not [Console]::KeyAvailable; $s++) { Start-Sleep -Milliseconds 100 }
  }
  $c.Dispose()
} finally {
  Get-Process Prism.App -ErrorAction SilentlyContinue | Stop-Process -Force
  if ($svcProc -and -not $svcProc.HasExited) {
    # Ask for a clean stop so the service hands the fans back to the BIOS; force only if it hangs.
    New-Item -ItemType File -Path $stopFile -Force | Out-Null
    if ($svcProc.WaitForExit(15000)) { Log 'Prism stopped cleanly; fans are back on the BIOS' }
    else { Stop-Process -Id $svcProc.Id -Force; Log 'WARNING: Prism did not stop in 15 s and was killed - restart the PC to be sure the BIOS has the fans' }
  }
  if ($orgb -and -not $orgb.HasExited) { Stop-Process -Id $orgb.Id -Force -ErrorAction SilentlyContinue }
  foreach ($name in $stopped) { Start-Service $name -ErrorAction SilentlyContinue; Log "Restarted $name" }
  Write-Host "Log saved to $out" -ForegroundColor Green
}
