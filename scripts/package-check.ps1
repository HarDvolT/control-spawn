# Static inspection only: no application, service or dependency is executed.
[CmdletBinding()]
param([string]$OutputPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'build/out/ControlSpawn'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!(Test-Path -LiteralPath $OutputPath -PathType Container)) { throw "Package output missing: $OutputPath. Run build/publish.ps1 first." }
$OutputPath = (Resolve-Path -LiteralPath $OutputPath).Path
$manifest = Get-Content "$OutputPath/package-manifest.json" -Raw | ConvertFrom-Json
if ($manifest.schema -ne 1 -or $manifest.rid -ne 'win-x64' -or !$manifest.selfContained -or !$manifest.readyToRun -or $manifest.trimmed -or $manifest.configuration -ne 'Release') { throw 'Invalid publish contract.' }
$pins = Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'build/dependencies.json') -Raw | ConvertFrom-Json
if ($manifest.openrgbVersion -ne $pins.openrgb.version -or $manifest.pawnioVersion -ne $pins.pawnio.version) { throw 'Dependency versions differ from pins.' }
$files = @(Get-ChildItem $OutputPath -Recurse -File)
function Get-PeImports([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { return }
        $stream.Position = 0x3C
        $pe = $reader.ReadUInt32()
        $stream.Position = $pe
        if ($reader.ReadUInt32() -ne 0x4550) { throw "Invalid PE: $Path" }
        $machine = $reader.ReadUInt16()
        if ($machine -notin 0x8664,0x14c) { throw "Unexpected PE machine in $Path" }
        $sectionCount = $reader.ReadUInt16()
        $stream.Position = $pe + 20
        $optionalSize = $reader.ReadUInt16()
        $optional = $pe + 24
        $stream.Position = $optional
        $magic = $reader.ReadUInt16()
        $directories = $optional + 96
        if ($magic -eq 0x20b) { $directories = $optional + 112 }
        elseif ($magic -ne 0x10b) { throw "Unexpected PE optional header: $Path" }
        $sections = @()
        for ($i = 0; $i -lt $sectionCount; $i++) {
            $stream.Position = $optional + $optionalSize + 40*$i + 8
            $virtualSize = $reader.ReadUInt32()
            $rva = $reader.ReadUInt32()
            $rawSize = $reader.ReadUInt32()
            $raw = $reader.ReadUInt32()
            $sections += [pscustomobject]@{ Rva=$rva; Size=[Math]::Max($virtualSize,$rawSize); Raw=$raw }
        }
        function Convert-Rva([uint32]$Rva) {
            foreach ($section in $sections) {
                if ($Rva -ge $section.Rva -and $Rva -lt ($section.Rva + $section.Size)) { return [long]($section.Raw + $Rva - $section.Rva) }
            }
            throw "Invalid import RVA in $Path"
        }
        # Normal import directory and RVA-based delay-import directory.
        foreach ($table in @(@{Index=1;Size=20;NameOffset=12}, @{Index=13;Size=32;NameOffset=4})) {
            $stream.Position = $directories + 8*$table.Index
            $tableRva = $reader.ReadUInt32()
            $tableBytes = $reader.ReadUInt32()
            if (!$tableRva -or !$tableBytes) { continue }
            $tableOffset = Convert-Rva $tableRva
            for ($i = 0; $i -lt [Math]::Floor($tableBytes/$table.Size); $i++) {
                $descriptor = $tableOffset + $i*$table.Size
                $stream.Position = $descriptor + $table.NameOffset
                $nameRva = $reader.ReadUInt32()
                if (!$nameRva) { break }
                if ($table.Index -eq 13) {
                    $stream.Position = $descriptor
                    if (($reader.ReadUInt32() -band 1) -ne 1) { throw "Unsupported legacy delay imports: $Path" }
                }
                $stream.Position = Convert-Rva $nameRva
                $name = New-Object Text.StringBuilder
                do { $byte = $reader.ReadByte(); if ($byte) { [void]$name.Append([char]$byte) } } while ($byte -and $name.Length -lt 512)
                $name.ToString()
            }
        }
    } finally { $reader.Dispose(); $stream.Dispose() }
}
# Windows 11 inbox DLLs only. In particular, MSVC redistributable DLLs are NOT assumed installed.
$inbox = @('advapi32.dll','authz.dll','avrt.dll','bcrypt.dll','bcryptprimitives.dll','cabinet.dll','cfgmgr32.dll','clbcatq.dll','combase.dll','comctl32.dll','comdlg32.dll','credui.dll','crypt32.dll','cryptbase.dll','cryptnet.dll','cryptsp.dll','d2d1.dll','d3d9.dll','d3d11.dll','d3d12.dll','dcomp.dll','dbghelp.dll','dhcpcsvc.dll','dnsapi.dll','dsound.dll','dwmapi.dll','dwrite.dll','dxgi.dll','dxva2.dll','gdi32.dll','glu32.dll','hid.dll','imm32.dll','iphlpapi.dll','kernel32.dll','kernelbase.dll','ksuser.dll','mpr.dll','msacm32.dll','mscoree.dll','msimg32.dll','msvcrt.dll','mswsock.dll','ncrypt.dll','netapi32.dll','normaliz.dll','ntdll.dll','ntmarta.dll','ole32.dll','oleacc.dll','oleaut32.dll','opengl32.dll','pdh.dll','powrprof.dll','profapi.dll','propsys.dll','psapi.dll','rasapi32.dll','rpcrt4.dll','secur32.dll','setupapi.dll','shcore.dll','shell32.dll','shlwapi.dll','srvcli.dll','sspicli.dll','ucrtbase.dll','uiautomationcore.dll','urlmon.dll','user32.dll','userenv.dll','usp10.dll','uxtheme.dll','version.dll','wevtapi.dll','win32u.dll','windowscodecs.dll','winhttp.dll','wininet.dll','winmm.dll','winscard.dll','winspool.drv','winsta.dll','wintrust.dll','wlanapi.dll','wldap32.dll','ws2_32.dll','wtsapi32.dll','wscapi.dll','winusb.dll')
# OpenRGB's release ZIP leaves out the Visual C++ runtime and expects it installed; the installer checks for it and points
# to Microsoft's download when it's missing. Control Spawn's own folders must stay free of it.
$missing = @()
$vcRuntime = @('vcruntime140.dll','vcruntime140_1.dll','msvcp140.dll','msvcp140_1.dll','msvcp140_2.dll','msvcp140_atomic_wait.dll','msvcp140_codecvt_ids.dll','concrt140.dll','vccorlib140.dll')
foreach ($file in $files | Where-Object { $_.Extension -in '.dll','.exe' -and $_.FullName -notlike "$OutputPath\dependencies\*" }) {
    $openRgbFile = $file.FullName.StartsWith("$OutputPath\openrgb\", [StringComparison]::OrdinalIgnoreCase)
    foreach ($import in @(Get-PeImports $file.FullName)) {
        if ($import -match '^(api|ext)-ms-win-' -or $import -in $inbox) { continue }
        if ($openRgbFile -and $import -in $vcRuntime) { continue }
        # LibreHardwareMonitor ships Mono.Posix's native helper for Linux; nothing calls it on Windows.
        if ($file.Name -eq 'MonoPosixHelper.dll' -and $import -in $vcRuntime) { continue }
        $searchPaths = @($file.DirectoryName, $OutputPath)
        if ($file.FullName.StartsWith("$OutputPath\openrgb\", [StringComparison]::OrdinalIgnoreCase)) { $searchPaths += "$OutputPath/openrgb" }
        $found = $false
        foreach ($directory in $searchPaths) { if (Test-Path -LiteralPath (Join-Path $directory $import) -PathType Leaf) { $found = $true; break } }
        if ($found) { continue }
        $missing += "$($file.Name) -> $import"
    }
}
# All at once: one per run made every gap a separate rebuild.
if ($missing.Count) { throw "Native dependencies not bundled or Windows inbox:`n  " + (($missing | Sort-Object -Unique) -join "`n  ") }
if (@($files | Where-Object { $_.Extension -in '.pdb','.dbg','.ilk','.exp','.lib' -or $_.Name -match '\.vshost\.|\.Tests\.' }).Count) { throw 'Debug/build/test files found in package.' }
foreach ($required in @('service/ControlSpawn.Service.exe','service/ControlSpawn.Service.dll','service/ControlSpawn.FanGuard.exe','service/ControlSpawn.FanGuard.dll','service/ControlSpawn.Core.dll','service/ControlSpawn.Hardware.dll','service/ControlSpawn.Lighting.dll','service/ControlSpawn.Router.dll','service/LibreHardwareMonitorLib.dll','service/System.ServiceProcess.ServiceController.dll','service/Microsoft.AspNetCore.dll','service/coreclr.dll','service/hostfxr.dll','service/hostpolicy.dll','service/System.Private.CoreLib.dll','app/ControlSpawn.exe','app/ControlSpawn.dll','app/ControlSpawn.Core.dll','app/PresentationFramework.dll','app/wpfgfx_cor3.dll','app/System.Windows.Forms.dll','app/coreclr.dll','app/hostfxr.dll','app/hostpolicy.dll','app/System.Private.CoreLib.dll','openrgb/OpenRGB.exe','dependencies/PawnIO_setup.exe','installer/Configure-ControlSpawn.ps1','THIRD-PARTY-NOTICES.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $OutputPath $required) -PathType Leaf)) { throw "Missing required file: $required" }
}
$recorded = @{}
foreach ($entry in $manifest.files) {
    if ([IO.Path]::IsPathRooted($entry.path) -or $entry.path -match '(^|[\\/])\.\.([\\/]|$)') { throw 'Unsafe manifest path.' }
    if ($recorded.ContainsKey($entry.path)) { throw "Duplicate manifest path: $($entry.path)" }
    $recorded[$entry.path] = $true
    $path = Join-Path $OutputPath $entry.path
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Manifest file missing: $($entry.path)" }
    if ((Get-FileHash -LiteralPath $path).Hash -ne $entry.sha256 -or (Get-Item -LiteralPath $path).Length -ne $entry.bytes) { throw "Integrity mismatch: $($entry.path)" }
}
foreach ($file in $files) {
    $relative = $file.FullName.Substring($OutputPath.Length + 1).Replace('\','/')
    if ($relative -ne 'package-manifest.json' -and !$recorded.ContainsKey($relative)) { throw "Unrecorded file: $relative" }
}
# 1.0.3 renamed the exes; a package that still carries an old one was built from stale output.
foreach ($stale in @('service/Prism.Service.exe','service/Prism.FanGuard.exe','app/Prism.App.exe')) {
    if (Test-Path -LiteralPath (Join-Path $OutputPath $stale)) { throw "Stale pre-1.0.3 file in package: $stale" }
}
foreach ($pair in @(@('ControlSpawn.Service','service'), @('ControlSpawn.FanGuard','service'), @('ControlSpawn','app'))) {
    $name = $pair[0]; $folder = Join-Path $OutputPath $pair[1]
    $runtime = Get-Content "$folder/$name.runtimeconfig.json" -Raw | ConvertFrom-Json
    if (!$runtime.runtimeOptions.PSObject.Properties['includedFrameworks'] -or $runtime.runtimeOptions.PSObject.Properties['framework']) { throw "$name is not self-contained." }
    $deps = Get-Content "$folder/$name.deps.json" -Raw | ConvertFrom-Json
    if ($deps.runtimeTarget.name -notmatch '/win-x64$') { throw "Wrong runtime target: $name" }
    # Every runtime/native DLL declared in deps must be in that program's own folder.
    foreach ($target in $deps.targets.PSObject.Properties) {
        foreach ($library in $target.Value.PSObject.Properties) {
            foreach ($kind in @('runtime','native','resources','runtimeTargets')) {
                if (!$library.Value.PSObject.Properties[$kind]) { continue }
                foreach ($asset in $library.Value.$kind.PSObject.Properties) {
                    if ($asset.Name -notmatch '\.dll$') { continue }
                    $leaf = [IO.Path]::GetFileName($asset.Name)
                    $relative = $leaf
                    if ($kind -eq 'resources') { $relative = "$($asset.Value.locale)/$leaf" }
                    if (!(Test-Path -LiteralPath (Join-Path $folder $relative))) { throw "Missing deps asset for ${name}: $relative" }
                }
            }
        }
    }
    $version = (Get-Item "$folder/$name.exe").VersionInfo.FileVersion
    if (!$version) { throw "Missing executable version: $name" }
    Write-Host "$name file version: $version"
}
$archive = Join-Path (Split-Path $PSScriptRoot -Parent) "build/cache/$($pins.openrgb.file)"
if (!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive).Hash -ne $pins.openrgb.sha256) { throw 'Pinned OpenRGB archive absent or hash mismatch.' }
if ((Get-FileHash "$OutputPath/dependencies/PawnIO_setup.exe").Hash -ne $pins.pawnio.sha256) { throw 'PawnIO hash mismatch.' }
# Check OpenRGB bytes against the pinned ZIP, independently of the generated manifest.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $exe = @($zip.Entries | Where-Object { $_.Name -ieq 'OpenRGB.exe' })
    if ($exe.Count -ne 1) { throw 'Unexpected OpenRGB ZIP layout.' }
    $prefix = $exe[0].FullName.Substring(0, $exe[0].FullName.Length - $exe[0].Name.Length)
    foreach ($entry in $zip.Entries) {
        if (!$entry.Name -or !$entry.FullName.StartsWith($prefix)) { continue }
        $relative = $entry.FullName.Substring($prefix.Length)
        $path = Join-Path "$OutputPath/openrgb" $relative
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $expected = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') } finally { $stream.Dispose(); $sha.Dispose() }
        if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $expected) { throw "OpenRGB archive mismatch: $relative" }
    }
} finally { $zip.Dispose() }
$bytes = ($files | Measure-Object Length -Sum).Sum
Write-Host ('PASS: {0} files, {1:N2} MiB; OpenRGB {2}; PawnIO {3}; hashes and declared DLLs checked.' -f $files.Count, ($bytes / 1MB), $pins.openrgb.version, $pins.pawnio.version)
$files | Sort-Object Length -Descending | Select-Object -First 10 @{n='File';e={$_.FullName.Substring($OutputPath.Length+1)}}, @{n='MiB';e={[math]::Round($_.Length/1MB,2)}} | Format-Table -AutoSize
