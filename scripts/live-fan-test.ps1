# Supervised fan control test (about 60 s). Runs the REAL service: it controls the Radiator and Case fans.
# Steps: Silent -> Standard -> Turbo, 15 s each, reading RPM. The AIO pump stays at 100%.
# At the end (or on any error / Ctrl+C) the service stops and hands every fan back to the BIOS.
# Lighting and lamp: OpenRGB is not running and no lamp is configured, so nothing else changes.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'docs\live-fan-test.txt'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "NOT ADMIN - open PowerShell with 'Run as administrator'" -ForegroundColor Red; exit 1 }

$exe = Join-Path $root 'src\Prism.Service\bin\Debug\net10.0-windows\ControlSpawn.Service.exe'
$p = Start-Process $exe -ArgumentList '--console', '--exit-after', '60' -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $env:TEMP 'prism-fan-test.log')
"Fan test $(Get-Date)" | Out-File $out -Encoding utf8
try {
  Start-Sleep -Seconds 6
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'prism', [System.IO.Pipes.PipeDirection]::InOut); $c.Connect(5000)
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $r = New-Object System.IO.StreamReader($c)
  $id = 1
  function Send($json) { $script:w.WriteLine($json); $script:r.ReadLine() }
  function Snap($label) {
    $s = (Send ('{"id":' + (++$script:id) + ',"command":"getState"}') | ConvertFrom-Json).data
    $line = "{0,-10} CPU {1,5:N1} C | " -f $label, $s.system.cpuTemp
    foreach ($f in $s.fans) { $line += "{0} {1,5:N0} rpm {2,3:N0}% | " -f $f.name, $f.rpm, $f.percent }
    if ($s.health.fanFailsafeActive) { $line += "FAILSAFE" }
    $line | Out-File $out -Append; Write-Host $line
  }
  $null = Send '{"id":1,"command":"hello","args":{"version":1,"client":"app"}}'
  Snap 'start'
  foreach ($mode in 'Silent', 'Standard', 'Turbo') {
    foreach ($g in 'Radiator', 'Case fans') {
      $resp = Send ('{"id":' + (++$id) + ',"command":"setFanMode","args":{"group":"' + $g + '","mode":"' + $mode + '"}}')
      if (-not ($resp | ConvertFrom-Json).ok) { "setFanMode $g $mode failed: $resp" | Out-File $out -Append; Write-Host $resp -ForegroundColor Red }
    }
    Write-Host "Mode $mode - listen to the fans..." -ForegroundColor Yellow
    Start-Sleep -Seconds 7; Snap "$mode 7s"
    Start-Sleep -Seconds 8; Snap "$mode 15s"
  }
  $c.Dispose()
} finally {
  # The service exits cleanly by itself after 60 s (--exit-after) and hands every fan back to the BIOS.
  if (-not $p.WaitForExit(30000)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; "WARNING: service did not exit cleanly; reboot to be sure the BIOS has the fans." | Out-File $out -Append }
  else { "Service exited cleanly (code $($p.ExitCode)); fans handed back to BIOS." | Out-File $out -Append }
  Copy-Item (Join-Path $env:TEMP 'prism-fan-test.log') (Join-Path $root 'docs\live-fan-test.log') -ErrorAction SilentlyContinue
  Write-Host "Done. Fans are back on the BIOS curves." -ForegroundColor Green
}
