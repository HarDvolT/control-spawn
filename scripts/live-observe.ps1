# Live read-only check: starts Prism.Service with --observe (real sensors, NO fan/RGB/lamp writes),
# reads two state snapshots over the pipe, saves them, then stops the service.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'docs\live-observe.json'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "NOT ADMIN - open PowerShell with 'Run as administrator'" -ForegroundColor Red; exit 1 }

$exe = Join-Path $root 'src\Prism.Service\bin\Debug\net8.0-windows\Prism.Service.exe'
$log = Join-Path $env:TEMP 'prism-observe.log'
$p = Start-Process $exe -ArgumentList '--observe' -PassThru -WindowStyle Hidden -RedirectStandardOutput $log
try {
  Start-Sleep -Seconds 6
  $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'prism', [System.IO.Pipes.PipeDirection]::InOut)
  $c.Connect(5000)
  $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true
  $r = New-Object System.IO.StreamReader($c)
  $w.WriteLine('{"id":1,"command":"hello","args":{"version":1,"client":"app"}}'); $null = $r.ReadLine()
  $w.WriteLine('{"id":2,"command":"getState"}'); $first = $r.ReadLine()
  Start-Sleep -Seconds 3
  $w.WriteLine('{"id":3,"command":"getState"}'); $second = $r.ReadLine()
  $c.Dispose()
  "[$first,`n$second]" | Out-File $out -Encoding utf8
  Write-Host "Saved live readings to $out" -ForegroundColor Green
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
  Copy-Item $log (Join-Path $root 'docs\live-observe.log') -ErrorAction SilentlyContinue
}
