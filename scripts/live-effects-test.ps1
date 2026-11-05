# Supervised creative-effects + per-LED test (about 2 min). Run as administrator.
# 1. Temporarily stops the two programs that fight over the lights: ASUS "LightingService" and Corsair
#    "CorsairDeviceControlService" (iCUE). They are started again at the end.
# 2. Starts OpenRGB 1.1 as a local server (127.0.0.1:6742), the same one tested earlier.
# 3. Runs the service (ControlSpawn.Service.exe) for 105 s: fans on Silent, 12 new effects for 6 s each, then per-LED colours on the RAM and GPU.
# 4. Stops everything; fans go back to the BIOS, the ASUS/Corsair services are restarted.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'docs\live-effects-test.txt'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "NOT ADMIN - open PowerShell with 'Run as administrator'" -ForegroundColor Red; exit 1 }

$openrgb = 'C:\Users\Hakim\AppData\Local\Temp\orgb\OpenRGB.exe'
$exe = Join-Path $root 'src\ControlSpawn.Service\bin\Debug\net10.0-windows\ControlSpawn.Service.exe'
"Effects test $(Get-Date)" | Out-File $out -Encoding utf8
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
  $svcProc = Start-Process $exe -ArgumentList '--console', '--exit-after', '105' -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $env:TEMP 'controlspawn-effects-test.log')
  Start-Sleep -Seconds 8
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'controlspawn', [System.IO.Pipes.PipeDirection]::InOut); $c.Connect(5000)
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
  $script:id = 1
  function Send($cmd, $payload) {
    $script:id++; $msg = @{ id = $script:id; command = $cmd }; if ($null -ne $payload) { $msg.args = $payload }
    $script:w.WriteLine(($msg | ConvertTo-Json -Compress -Depth 5)); $script:r.ReadLine() }
  $null = Send 'hello' @{ version = 1; client = 'app' }
  foreach ($g in 'Radiator', 'Case fans') { $null = Send 'setFanMode' @{ group = $g; mode = 'Silent' } }
  $effects = 'Aurora','Comet','Fire','OceanRipple','Plasma','Scanner','Rain','Heartbeat','GradientFlow','Sparkle','LoadMeters','TemperatureFlow'
  foreach ($e in $effects) {
    $resp = Send 'setLighting' @{ effect = $e; color = '#FF4D1A'; brightness = 1; speed = 1 }
    Write-Host "Effect: $e - watch the RAM, GPU, board and strip..." -ForegroundColor Yellow
    Start-Sleep -Seconds 6
    Log ("{0,-16} ok={1} {2}" -f $e, ($resp | ConvertFrom-Json).ok, (($resp | ConvertFrom-Json).error))
  }
  # Per-LED: paint RAM 1 as an orange-to-blue gradient, RAM 2 alternating white/orange, GPU green.
  $state = (Send 'getState' $null | ConvertFrom-Json).data
  $null = Send 'setLighting' @{ effect = 'Custom'; color = '#FF4D1A'; brightness = 1; speed = 1 }
  $ram = @($state.lighting.devices | Where-Object { $_.kind -eq 'RAM' })
  $gpu = $state.lighting.devices | Where-Object { $_.kind -eq 'GPU' } | Select-Object -First 1
  if ($ram.Count -ge 1) {
    $n = $ram[0].ledCount; $cols = @(for ($i = 0; $i -lt $n; $i++) { $t = $i / [Math]::Max(1, $n - 1); '#{0:X2}{1:X2}{2:X2}' -f [int](255 - 165 * $t), [int](77 + 90 * $t), [int](26 + 229 * $t) })
    Log ("Per-LED RAM 1 gradient ok={0}" -f ((Send 'setLedColors' @{ device = $ram[0].id; colors = $cols; layer = 'custom' }) | ConvertFrom-Json).ok)
  }
  if ($ram.Count -ge 2) {
    $cols = @(for ($i = 0; $i -lt $ram[1].ledCount; $i++) { if ($i % 2) { '#FF4D1A' } else { '#FFFFFF' } })
    Log ("Per-LED RAM 2 alternating ok={0}" -f ((Send 'setLedColors' @{ device = $ram[1].id; colors = $cols; layer = 'custom' }) | ConvertFrom-Json).ok)
  }
  if ($gpu) { Log ("Per-LED GPU green ok={0}" -f ((Send 'setLedColors' @{ device = $gpu.id; colors = @(1..$gpu.ledCount | % { '#3DDC84' }); layer = 'custom' }) | ConvertFrom-Json).ok) }
  Write-Host "Per-LED: RAM 1 orange-to-blue gradient, RAM 2 white/orange stripes, GPU green..." -ForegroundColor Yellow
  Start-Sleep -Seconds 8
  foreach ($d in $state.lighting.devices) { $null = Send 'clearLedColors' @{ device = $d.id } }
  $c.Dispose()
} finally {
  if ($svcProc -and -not $svcProc.WaitForExit(60000)) { Stop-Process -Id $svcProc.Id -Force -ErrorAction SilentlyContinue; Log "WARNING: service did not exit cleanly" }
  elseif ($svcProc) { Log "Service exited cleanly; fans back on the BIOS." }
  if ($orgb) { Stop-Process -Id $orgb.Id -Force -ErrorAction SilentlyContinue; Log "OpenRGB stopped." }
  foreach ($name in $stopped) { Start-Service $name -ErrorAction SilentlyContinue; Log "Restarted $name" }
  Copy-Item (Join-Path $env:TEMP 'controlspawn-effects-test.log') (Join-Path $root 'docs\live-effects-test.log') -ErrorAction SilentlyContinue
  Write-Host "Done." -ForegroundColor Green
}
