# Supervised RGB test (about 90 s). Run as administrator.
# 1. Temporarily stops the two programs that fight over the lights: ASUS "LightingService" and Corsair
#    "CorsairDeviceControlService" (iCUE). They are started again at the end.
# 2. Starts OpenRGB 1.1 as a local server (127.0.0.1:6742), the same one tested earlier.
# 3. Runs the service (ControlSpawn.Service.exe) for 75 s: fans on Silent, lighting Static orange -> Static white -> Rainbow wave -> Smart.
# 4. Stops everything; fans go back to the BIOS, the ASUS/Corsair services are restarted.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'docs\live-rgb-test.txt'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "NOT ADMIN - open PowerShell with 'Run as administrator'" -ForegroundColor Red; exit 1 }

$openrgb = 'C:\Users\Hakim\AppData\Local\Temp\orgb\OpenRGB.exe'
$exe = Join-Path $root 'src\Prism.Service\bin\Debug\net8.0-windows\ControlSpawn.Service.exe'
"RGB test $(Get-Date)" | Out-File $out -Encoding utf8
function Log($m) { $m | Out-File $out -Append; Write-Host $m }

$stopped = @()
foreach ($name in 'LightingService', 'CorsairDeviceControlService') {
  $svc = Get-Service $name -ErrorAction SilentlyContinue
  if ($svc -and $svc.Status -eq 'Running') { Stop-Service $name -Force; $stopped += $name; Log "Stopped $name (will restart at the end)" }
}
$orgb = $null; $svcProc = $null
try {
  $orgb = Start-Process $openrgb -ArgumentList '--server', '--noautoconnect' -WorkingDirectory (Split-Path $openrgb) -PassThru -WindowStyle Minimized
  Log "OpenRGB server starting (device detection takes ~15 s)..."
  Start-Sleep -Seconds 18
  $svcProc = Start-Process $exe -ArgumentList '--console', '--exit-after', '75' -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $env:TEMP 'prism-rgb-test.log')
  Start-Sleep -Seconds 8
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'prism', [System.IO.Pipes.PipeDirection]::InOut); $c.Connect(5000)
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
  $script:id = 1
  function Send($cmd, $payload) {
    $script:id++; $msg = @{ id = $script:id; command = $cmd }; if ($null -ne $payload) { $msg.args = $payload }
    $script:w.WriteLine(($msg | ConvertTo-Json -Compress -Depth 5)); $script:r.ReadLine() }
  $null = Send 'hello' @{ version = 1; client = 'app' }
  foreach ($g in 'Radiator', 'Case fans') { $null = Send 'setFanMode' @{ group = $g; mode = 'Silent' } }
  $steps = @(
    @{ effect = 'Static'; color = '#FF4D1A'; brightness = 1; speed = 1; label = 'Static orange' },
    @{ effect = 'Static'; color = '#FFFFFF'; brightness = 1; speed = 1; label = 'Static white' },
    @{ effect = 'RainbowWave'; color = '#FF4D1A'; brightness = 1; speed = 1; label = 'Rainbow wave' },
    @{ effect = 'Smart'; color = '#FF4D1A'; brightness = 1; speed = 1; label = 'Smart (CPU temperature)' })
  foreach ($s in $steps) {
    $label = $s.label; $s.Remove('label')
    $resp = Send 'setLighting' $s
    Write-Host "Lighting: $label - look at the board, LED strip, RAM and GPU..." -ForegroundColor Yellow
    Start-Sleep -Seconds 10
    $state = (Send 'getState' $null | ConvertFrom-Json).data
    $devs = ($state.lighting.devices | ForEach-Object { "$($_.name) [$($_.kind), $($_.ledCount) LEDs, connected=$($_.connected)]" }) -join '; '
    Log ("{0,-24} ok={1} | devices: {2} | warnings: {3}" -f $label, ($resp | ConvertFrom-Json).ok, $devs, (($state.health.warnings | Where-Object { $_ -match 'RGB|OpenRGB' }) -join ' '))
  }
  $c.Dispose()
} finally {
  if ($svcProc -and -not $svcProc.WaitForExit(60000)) { Stop-Process -Id $svcProc.Id -Force -ErrorAction SilentlyContinue; Log "WARNING: service did not exit cleanly" }
  elseif ($svcProc) { Log "Service exited cleanly; fans back on the BIOS." }
  if ($orgb) { Stop-Process -Id $orgb.Id -Force -ErrorAction SilentlyContinue; Log "OpenRGB stopped." }
  foreach ($name in $stopped) { Start-Service $name -ErrorAction SilentlyContinue; Log "Restarted $name" }
  Copy-Item (Join-Path $env:TEMP 'prism-rgb-test.log') (Join-Path $root 'docs\live-rgb-test.log') -ErrorAction SilentlyContinue
  Write-Host "Done." -ForegroundColor Green
}
