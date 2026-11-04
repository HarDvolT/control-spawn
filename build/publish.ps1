# Build only. Never executes Prism, OpenRGB, PawnIO or the resulting installer.
[CmdletBinding()]
param(
    [string]$CertificateThumbprint,
    [string]$IsccPath,
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$stamp = [Guid]::NewGuid().ToString('N')
$work = Join-Path $PSScriptRoot "work/$stamp"
$out = Join-Path $PSScriptRoot 'out/Prism'
if (Test-Path $out) { throw "Output already exists: $out. Move it aside before building; stale files are never packaged." }
New-Item -ItemType Directory -Force -Path $work, "$PSScriptRoot/cache", "$PSScriptRoot/logs" | Out-Null
$pins = Get-Content "$PSScriptRoot/dependencies.json" -Raw | ConvertFrom-Json
function Get-Dependency($pin) {
    $path = Join-Path "$PSScriptRoot/cache" $pin.file
    if (!(Test-Path -LiteralPath $path)) {
        $temporary = "$path.download"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri $pin.url -OutFile $temporary -TimeoutSec 120
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $pin.sha256) {
            throw "SHA-256 mismatch: $($pin.file). Download retained as .download for diagnosis."
        }
        Move-Item -LiteralPath $temporary -Destination $path
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $pin.sha256) { throw "Corrupt cached dependency: $path" }
    return $path
}
function Sign-File([string]$path) {
    if (!$CertificateThumbprint) { return }
    $cert = Get-Item "Cert:/CurrentUser/My/$CertificateThumbprint" -ErrorAction SilentlyContinue
    if (!$cert) { $cert = Get-Item "Cert:/LocalMachine/My/$CertificateThumbprint" }
    if (!$cert.HasPrivateKey) { throw 'Signing certificate has no private key.' }
    $result = Set-AuthenticodeSignature -LiteralPath $path -Certificate $cert -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
    if ($result.Status -ne 'Valid') { throw "Signing failed for ${path}: $($result.StatusMessage)" }
}
# Check ISCC before a long publish, but missing Inno must not prevent a folder build.
if (!$IsccPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $IsccPath = $command.Source }
    # winget installs Inno Setup per user (LocalAppData) when run without admin.
    foreach ($candidate in @("${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe", "$env:ProgramFiles/Inno Setup 6/ISCC.exe", "$env:LOCALAPPDATA/Programs/Inno Setup 6/ISCC.exe")) {
        if (!$IsccPath -and (Test-Path -LiteralPath $candidate)) { $IsccPath = $candidate }
    }
}
if (!$IsccPath -and !$SkipInstaller) {
    Write-Warning 'Inno Setup 6 is absent. On a build machine, install Inno Setup 6.3+ from https://jrsoftware.org/isdl.php, then rerun or use -IsccPath. This script never installs build tools.'
}
$openrgbZip = Get-Dependency $pins.openrgb
$pawnioExe = Get-Dependency $pins.pawnio
# Build a private source snapshot so restore/bin/obj never touch src/ or other worktrees.
$snapshot = Join-Path $work 'source'
New-Item -ItemType Directory -Force -Path "$snapshot/src" | Out-Null
Copy-Item -LiteralPath "$root/Directory.Build.props" -Destination $snapshot
Get-ChildItem "$root/src" -Directory | ForEach-Object {
    $projectDirectory = $_
    Get-ChildItem $projectDirectory.FullName -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | ForEach-Object {
        $relative = $_.FullName.Substring($root.Length + 1)
        $target = Join-Path $snapshot $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $target
    }
}
$oldCliHome = $env:DOTNET_CLI_HOME
$oldFirstRun = $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE
$oldNugetPackages = $env:NUGET_PACKAGES
$oldNugetHttp = $env:NUGET_HTTP_CACHE_PATH
$oldNugetPlugins = $env:NUGET_PLUGINS_CACHE_PATH
try {
    $env:DOTNET_CLI_HOME = Join-Path $work 'dotnet-home'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:NUGET_PACKAGES = Join-Path $PSScriptRoot 'cache/nuget'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $PSScriptRoot 'cache/nuget-http'
    $env:NUGET_PLUGINS_CACHE_PATH = Join-Path $PSScriptRoot 'cache/nuget-plugins'
    # The fan guard (ControlSpawn.FanGuard.exe) is required: the service starts it as a child process before any fan write.
    foreach ($project in @('Prism.Service', 'Prism.App', 'Prism.FanGuard')) {
        $csproj = "$snapshot/src/$project/$project.csproj"
        $destination = Join-Path $work "publish/$project"
        & dotnet publish $csproj -c Release -r win-x64 --self-contained true -o $destination `
            -p:PublishReadyToRun=true -p:PublishTrimmed=false -p:PublishSingleFile=false `
            -p:DebugType=None -p:DebugSymbols=false -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $project ($LASTEXITCODE)" }
    }
} finally {
    $env:DOTNET_CLI_HOME = $oldCliHome
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = $oldFirstRun
    $env:NUGET_PACKAGES = $oldNugetPackages
    $env:NUGET_HTTP_CACHE_PATH = $oldNugetHttp
    $env:NUGET_PLUGINS_CACHE_PATH = $oldNugetPlugins
}
New-Item -ItemType Directory -Force -Path $out | Out-Null
# The app (WPF) and the service ship different builds of the same runtime DLLs (WindowsBase, System.CodeDom...), so each
# gets its own folder. The fan guard runs from the service's folder and must agree with it byte for byte.
$folders = @{ 'Prism.App' = 'app'; 'Prism.Service' = 'service'; 'Prism.FanGuard' = 'service' }
Get-ChildItem "$work/publish" -Directory | ForEach-Object {
    $publishDirectory = $_.FullName
    $folder = Join-Path $out $folders[$_.Name]
    Get-ChildItem $publishDirectory -Recurse -File | ForEach-Object {
        $target = Join-Path $folder $_.FullName.Substring($publishDirectory.Length + 1)
        if (Test-Path -LiteralPath $target) {
            if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $_.FullName).Hash) {
                throw "Conflicting publish outputs: $target"
            }
        } else {
            New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $target
        }
    }
}
$extracted = Join-Path $work 'openrgb'
Expand-Archive -LiteralPath $openrgbZip -DestinationPath $extracted
$executables = @(Get-ChildItem $extracted -Filter OpenRGB.exe -Recurse)
if ($executables.Count -ne 1) { throw 'OpenRGB archive must contain exactly one OpenRGB.exe.' }
New-Item -ItemType Directory -Force -Path "$out/openrgb", "$out/dependencies", "$out/installer" | Out-Null
Copy-Item -Path "$($executables[0].Directory.FullName)/*" -Destination "$out/openrgb" -Recurse
Copy-Item -LiteralPath $pawnioExe -Destination "$out/dependencies/PawnIO_setup.exe"
Copy-Item -LiteralPath "$root/installer/Configure-ControlSpawn.ps1" -Destination "$out/installer"
Copy-Item -LiteralPath "$PSScriptRoot/dependencies.json" -Destination "$out/dependencies"
Copy-Item -LiteralPath "$root/installer/THIRD-PARTY-NOTICES.txt" -Destination $out
Get-ChildItem "$out/app", "$out/service" -File | Where-Object { $_.Name -match '^(Prism|ControlSpawn)(\..+)?\.(exe|dll)$' } | ForEach-Object { Sign-File $_.FullName }
Sign-File "$out/installer/Configure-ControlSpawn.ps1"
$manifest = [ordered]@{ schema = 1; rid = 'win-x64'; configuration = 'Release'; selfContained = $true; readyToRun = $true; trimmed = $false; openrgbVersion = $pins.openrgb.version; pawnioVersion = $pins.pawnio.version; files = @() }
$manifest.files = @(Get-ChildItem $out -Recurse -File | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($out.Length + 1).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash; bytes = $_.Length; version = $_.VersionInfo.FileVersion }
})
$manifest | ConvertTo-Json -Depth 6 | Set-Content "$out/package-manifest.json" -Encoding UTF8
& "$root/scripts/package-check.ps1" -OutputPath $out
if (!$SkipInstaller -and $IsccPath) {
    & $IsccPath "/DPayloadDir=$out" "/DSetupOutputDir=$PSScriptRoot/out" "$root/installer/Prism.iss"
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)." }
    Sign-File "$PSScriptRoot/out/ControlSpawnSetup.exe"
    Write-Host "Installer: $PSScriptRoot/out/ControlSpawnSetup.exe (not executed)"
}
