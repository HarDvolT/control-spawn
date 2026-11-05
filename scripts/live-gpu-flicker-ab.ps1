# GPU flicker A/B test (about 2 min). Run as administrator.
# All lights go static BLUE twice for 30 s. Count how often the GPU flashes another colour in each round.
#   Round A: Prism as normal (reads GPU temperature/load once a second).
#   Round B: Prism with GPU sensor reads switched off (--no-gpu-sensors).
# If B is clean and A flickers, the sensor reads are disturbing the GPU's lighting bus.
# Fans stay on Silent; everything is put back at the end (fans -> BIOS, ASUS/Corsair services restarted).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'docs\live-gpu-flicker-ab.txt'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "NOT ADMIN - open PowerShell with 'Run as administrator'" -ForegroundColor Red; exit 1 }
$openrgb = 'C:\Users\Hakim\AppData\Local\Temp\orgb\OpenRGB.exe'
$exe = Join-Path $root 'src\Prism.Service\bin\Debug\net10.0-windows\ControlSpawn.Service.exe'
"GPU flicker A/B $(Get-Date)" | Out-File $out -Encoding utf8
function Log($m) { $m | Out-File $out -Append; Write-Host $m }

function Round($label, $extraArgs) {
  $p = Start-Process $exe -ArgumentList (@('--console', '--exit-after', '45') + $extraArgs) -PassThru -WindowStyle Hidden
  try {
    Start-Sleep -Seconds 8
    $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'prism', [System.IO.Pipes.PipeDirection]::InOut); $c.Connect(5000)
    $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
    $w.WriteLine('{"id":1,"command":"hello","args":{"version":1,"client":"app"}}'); $null = $r.ReadLine()
    foreach ($g in 'Radiator', 'Case fans') { $w.WriteLine('{"id":2,"command":"setFanMode","args":{"group":"' + $g + '","mode":"Silent"}}'); $null = $r.ReadLine() }
    $w.WriteLine('{"id":3,"command":"setLighting","args":{"effect":"Static","color":"#2060FF","brightness":1,"speed":1}}'); $null = $r.ReadLine()
    Write-Host ""
    Write-Host "ROUND $label - all lights BLUE for 30 s. Watch the GPU and count the flashes." -ForegroundColor Yellow
    for ($i = 30; $i -gt 0; $i -= 5) { Write-Host "  $i s left..."; Start-Sleep -Seconds 5 }
    $c.Dispose()
    $n = Read-Host "  How many times did the GPU flash another colour in round $label? (number, or 'many')"
    Log "Round $label ($($extraArgs -join ' ')): $n flashes"
  } finally {
    if (-not $p.WaitForExit(40000)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; Log "WARNING: round $label service did not exit cleanly" }
  }
}

$stopped = @()
foreach ($name in 'LightingService', 'CorsairDeviceControlService') {
  $svc = Get-Service $name -ErrorAction SilentlyContinue
  if ($svc -and $svc.Status -eq 'Running') { Stop-Service $name -Force; $stopped += $name; Log "Stopped $name (will restart at the end)" }
}
$orgb = $null
try {
  $orgb = Start-Process $openrgb -ArgumentList '--server', '--noautoconnect' -WorkingDirectory (Split-Path $openrgb) -PassThru -WindowStyle Minimized
  Log "OpenRGB server starting..."; Start-Sleep -Seconds 18
  Round 'A' @()
  Round 'B' @('--no-gpu-sensors')
  Log ("Other GPU/RGB software running during the test: " + ((Get-Process | Where-Object { $_.Name -match 'Armoury|asus_framework|AMDRSServ|RadeonSoftware|Polychrome|iCUE|SignalRgb' } | Select-Object -Expand Name -Unique) -join ', '))
} finally {
  if ($orgb) { Stop-Process -Id $orgb.Id -Force -ErrorAction SilentlyContinue; Log "OpenRGB stopped." }
  foreach ($name in $stopped) { Start-Service $name -ErrorAction SilentlyContinue; Log "Restarted $name" }
  Write-Host "Done. Fans are back on the BIOS." -ForegroundColor Green
}
