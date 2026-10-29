# GPU flicker round C (about 1.5 min). Run as administrator.
# Same 30 s static BLUE round, with the latest Prism (GPU bus turns + 10 s GPU refresh), but with the other
# programs that also talk to the GPU closed for the test: AMD Software (RadeonSoftware, AMDRSServ) and
# Armoury Crate (its service and helper processes). Armoury Crate's service is started again at the end;
# open AMD Software from the Start menu afterwards if you want it back (or it returns on the next sign-in).
# Fans stay on Silent; everything is put back at the end (fans -> BIOS, ASUS/Corsair services restarted).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'docs\live-lighting-flicker.txt'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "NOT ADMIN - open PowerShell with 'Run as administrator'" -ForegroundColor Red; exit 1 }
$openrgb = 'C:\Users\Hakim\AppData\Local\Temp\orgb\OpenRGB.exe'
$exe = Join-Path $root 'src\Prism.Service\bin\Debug\net8.0-windows\Prism.Service.exe'
"Lighting flicker check $(Get-Date)" | Out-File $out -Encoding utf8
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
    Write-Host "ROUND $label - all lights BLUE for 30 s. Count flickers on the GPU, the RAM and the board/strip separately." -ForegroundColor Yellow
    for ($i = 30; $i -gt 0; $i -= 5) { Write-Host "  $i s left..."; Start-Sleep -Seconds 5 }
    $c.Dispose()
    $g = Read-Host "  GPU: how many flickers/flashes? (number, or 'many')"
    $m = Read-Host "  RAM sticks: how many flickers?"
    $b = Read-Host "  Motherboard / LED strip: how many flickers?"
    Log "Round $label ($($extraArgs -join ' ')): GPU=$g RAM=$m Board=$b"
    Log ("  ASUS/AMD/Corsair processes alive at the end of the round: " + ((Get-Process | Where-Object { $_.Name -match 'Armoury|asus_framework|LightingService|AMDRSServ|RadeonSoftware|iCUE|Corsair' } | Select-Object -Expand Name -Unique) -join ', '))
  } finally {
    if (-not $p.WaitForExit(40000)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; Log "WARNING: round $label service did not exit cleanly" }
  }
}

$stopped = @()
$orgb = $null
try {
  foreach ($name in 'LightingService', 'CorsairDeviceControlService', 'ArmouryCrateService') {
    $svc = Get-Service $name -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq 'Running') {
      try { Stop-Service $name -Force -ErrorAction Stop; $stopped += $name; Log "Stopped $name (will restart at the end)" }
      catch { Log "Could not stop $name ($($_.Exception.Message)); continuing with it running" }
    }
  }
  foreach ($proc in 'RadeonSoftware', 'AMDRSServ', 'AMDRSSrcExt', 'ArmouryCrate', 'ArmouryCrate.UserSessionHelper', 'ArmourySocketServer', 'ArmourySwAgent', 'asus_framework') {
    Get-Process $proc -ErrorAction SilentlyContinue | ForEach-Object {
      try { Stop-Process -Id $_.Id -Force -ErrorAction Stop; Log "Closed $($_.Name) for the test" } catch { Log "Could not close $($_.Name)" }
    }
  }

  $orgb = Start-Process $openrgb -ArgumentList '--server', '--noautoconnect' -WorkingDirectory (Split-Path $openrgb) -PassThru -WindowStyle Minimized
  Log "OpenRGB server starting..."; Start-Sleep -Seconds 18
  Round 'C' @()
  Log ("Other GPU/RGB software running during the test: " + ((Get-Process | Where-Object { $_.Name -match 'Armoury|asus_framework|AMDRSServ|RadeonSoftware|Polychrome|iCUE|SignalRgb' } | Select-Object -Expand Name -Unique) -join ', '))
} finally {
  if ($orgb) { Stop-Process -Id $orgb.Id -Force -ErrorAction SilentlyContinue; Log "OpenRGB stopped." }
  foreach ($name in ($stopped + @('LightingService', 'CorsairDeviceControlService', 'ArmouryCrateService') | Select-Object -Unique)) { $svc = Get-Service $name -ErrorAction SilentlyContinue; if ($svc -and $svc.Status -ne 'Running') { Start-Service $name -ErrorAction SilentlyContinue; Log "Restarted $name" } }
  Write-Host "Done. Fans are back on the BIOS." -ForegroundColor Green
}
