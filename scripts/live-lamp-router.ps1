# Live lamp + router test (up to 5 min, including prompts). Run as administrator.
# Before you start: close the Router Signal widget and any router web page (the ZLT X17M allows ONE login).
#
# Lamp: you paste the Mi Home token when asked (input is hidden). The service stores it DPAPI-encrypted for
#       your Windows account and never prints it. Then the lamp goes orange -> blue -> dim green -> warm white.
# Router: you type the router admin password when asked (hidden). Stored DPAPI-encrypted, never printed. Prism then
#       reads the 5G signal read-only (the same 4 allowed commands as your widget).
# Leave either prompt empty to skip that part. Fans stay on Silent; the service exits cleanly at the end.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'docs\live-lamp-router.txt'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "NOT ADMIN - open PowerShell with 'Run as administrator'" -ForegroundColor Red; exit 1 }
$exe = Join-Path $root 'src\Prism.Service\bin\Debug\net10.0-windows\ControlSpawn.Service.exe'
"Lamp + router test $(Get-Date)" | Out-File $out -Encoding utf8
function Log($m) { $m | Out-File $out -Append; Write-Host $m }
function Plain($secure) { $b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure); try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($b) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b) } }

$passSecure = Read-Host "Router admin password (Enter to skip)" -AsSecureString

$errLog = Join-Path $env:TEMP 'prism-lamp-router.err'
$p = Start-Process $exe -ArgumentList '--console', '--exit-after', '300' -PassThru -WindowStyle Hidden -RedirectStandardError $errLog
try {
  Start-Sleep -Seconds 7
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'prism', [System.IO.Pipes.PipeDirection]::InOut); $c.Connect(5000)
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
  $script:id = 1
  function Send($cmd, $payload) {
    $script:id++; $msg = @{ id = $script:id; command = $cmd }; if ($null -ne $payload) { $msg.args = $payload }
    $script:w.WriteLine(($msg | ConvertTo-Json -Compress -Depth 5)); $script:r.ReadLine() | ConvertFrom-Json }
  function State { (Send 'getState' $null).data }
  $null = Send 'hello' @{ version = 1; client = 'app' }
  foreach ($g in 'Radiator', 'Case fans') { $null = Send 'setFanMode' @{ group = $g; mode = 'Silent' } }

  $discovery = Send 'discoverLamps' @{}
  if (-not $discovery.ok) { Log "Lamp discovery failed: $($discovery.error)" }
  foreach ($device in $discovery.data) {
    Log ("Found lamp: IP={0} deviceId={1} configured={2}" -f $device.ip, $device.deviceId, $device.alreadyConfigured)
  }
  foreach ($device in $discovery.data) {
    $tokenSecure = Read-Host "Token for device $($device.deviceId) at $($device.ip) (32 hex, Enter to skip)" -AsSecureString
    try { $token = (Plain $tokenSecure).Trim() } finally { $tokenSecure.Dispose() }
    if (-not $token) { Log "Lamp $($device.deviceId): skipped"; continue }
    try { $resp = Send 'setLampToken' @{ deviceId = [string]$device.deviceId; name = "Lamp $($device.deviceId)"; token = $token } } finally { $token = $null }
    Log ("Lamp {0} token saved: ok={1} {2}" -f $device.deviceId, $resp.ok, $resp.error)
    if (-not $resp.ok) { continue }
    $lampId = "lamp:$($device.deviceId)"
    $steps = @(@{ c = '#FF4D1A'; b = 80; l = 'orange' }, @{ c = '#2060FF'; b = 80; l = 'blue' }, @{ c = '#3DDC84'; b = 25; l = 'dim green' }, @{ c = '#FFB070'; b = 70; l = 'warm white' })
    foreach ($s in $steps) {
      $resp = Send 'setLamp' @{ lamp = $lampId; on = $true; brightness = $s.b; follow = 'own'; color = $s.c }
      Write-Host "Lamp $lampId -> $($s.l)... look at the lamp" -ForegroundColor Yellow
      Start-Sleep -Seconds 5
      $l = (State).lamps | Where-Object { $_.id -eq $lampId } | Select-Object -First 1
      Log ("Lamp {0} {1} ok={2} connected={3} transport={4} IP={5} error={6}" -f $lampId, $s.l, $resp.ok, $l.connected, $l.transport, $l.host, $l.error)
    }
  }

  $pass = Plain $passSecure; $passSecure.Dispose()
  if ($pass) {
    $resp = Send 'setRouterPassword' @{ password = $pass }; $pass = $null
    Log ("Router password saved: ok={0} {1}" -f $resp.ok, $resp.error)
    $resp = Send 'setServiceOptions' @{ openRgbPath = 'C:\Users\Hakim\AppData\Local\Temp\orgb\OpenRGB.exe'; launchOpenRgb = $false; routerEnabled = $true; routerHost = '192.168.0.1' }
    Log ("Router polling enabled: ok={0} {1}" -f $resp.ok, $resp.error)
    foreach ($i in 1..3) {
      Start-Sleep -Seconds 6
      $rt = (State).router
      if ($rt) { Log ("Router #{0}: connected={1} {2} {3} score={4} bars={5} verdict={6} RSRP5G={7} SINR5G={8} ping={9} error={10}" -f $i, $rt.connected, $rt.network, $rt.band, $rt.score, $rt.bars, $rt.verdict, $rt.rsrp5g, $rt.sinr5g, $rt.pingMs, $rt.error) }
      else { Log "Router #${i}: no state yet" }
    }
  } else { Log "Router: skipped" }
  $warn = (State).health.warnings | Where-Object { $_ -match 'lamp|router|Lamp|Router' }
  if ($warn) { Log ("Warnings: " + ($warn -join ' | ')) }
  $c.Dispose()
} finally {
  if (-not $p.WaitForExit(320000)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; Log "WARNING: service did not exit cleanly" }
  else { Log "Service exited cleanly; fans back on the BIOS." }
  Get-Content $errLog -ErrorAction SilentlyContinue | Where-Object { $_ -match 'lamp|router|miio|Lamp|Router' } | Select-Object -First 8 | ForEach-Object { Log "  log: $_" }
}
