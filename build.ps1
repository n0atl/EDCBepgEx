param([string]$VsRoot = '', [ValidateSet('net48','net10.0-windows')][string]$Framework='net48', [string]$PlatformToolset = '', [string]$Version = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (!$VsRoot) { $VsRoot = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath }
if (!$VsRoot) { throw 'Visual Studio C++ build tools not found' }
# Prefer v143 for reproducible output; fall back to the newest toolset the installation has.
if (!$PlatformToolset) {
    # Only the newest VC\vNNN folder belongs to this MSBuild; older ones may hold leftover, unusable toolsets.
    $vcTargets = Get-ChildItem "$VsRoot\MSBuild\Microsoft\VC" -Directory | Where-Object { $_.Name -match '^v\d+$' } | Sort-Object { [int]$_.Name.Substring(1) } -Descending | Select-Object -First 1
    $toolsets = Get-ChildItem "$($vcTargets.FullName)\Platforms\x64\PlatformToolsets" -Directory | Where-Object { $_.Name -match '^v\d+$' } | ForEach-Object Name
    $PlatformToolset = if ($toolsets -contains 'v143') { 'v143' } else { $toolsets | Sort-Object { [int]$_.Substring(1) } -Descending | Select-Object -First 1 }
}
Write-Output "Visual Studio: $VsRoot / PlatformToolset: $PlatformToolset"
$reference = Join-Path (Split-Path $root) 'EpgCollector-reference'
$msbuild = Join-Path $VsRoot 'MSBuild\Current\Bin\MSBuild.exe'
foreach ($arch in @('x86','x64')) {
    $platform = if ($arch -eq 'x86') {'Win32'} else {'x64'}
    $out = Join-Path $root "dist\native\$arch\"
    New-Item -ItemType Directory -Path $out -Force | Out-Null
    & $msbuild "$reference\EpgDataCap3\EpgDataCap3\EpgDataCap3.vcxproj" /p:Configuration=Release "/p:Platform=$platform" "/p:PlatformToolset=$PlatformToolset" "/p:OutDir=$out" "/p:IntDir=$root\build\epg-$arch\" /m /v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'EPG parser build failed' }
    $vcvars = Join-Path $VsRoot 'VC\Auxiliary\Build\vcvarsall.bat'
    $worker = Join-Path $root 'native\worker.cpp'
    $command = '"{0}" {1} && cl /nologo /std:c++17 /EHsc /O2 /MT /utf-8 "{2}" /Fe:"{3}EpgWorker.exe" /Fo:"{3}worker.obj" /link /DYNAMICBASE /NXCOMPAT' -f $vcvars,$arch,$worker,$out
    & cmd.exe /d /s /c $command
    if ($LASTEXITCODE -ne 0) { throw 'Worker build failed' }
    $fixture = Join-Path $root 'native\MockBonDriver.cpp'
    $command = '"{0}" {1} && cl /nologo /std:c++17 /EHsc /O2 /MT /utf-8 /LD "{2}" /Fe:"{3}MockBonDriver.dll" /Fo:"{3}mock.obj" /link /EXPORT:CreateBonDriver' -f $vcvars,$arch,$fixture,$out
    & cmd.exe /d /s /c $command
    if ($LASTEXITCODE -ne 0) { throw 'Test fixture build failed' }
}
$project=if($Framework -eq 'net48'){"$root\src-net48\EpgCollector.Net48.csproj"}else{"$root\src\EpgCollector.csproj"}
$guiOut=if($Framework -eq 'net48'){"$root\framework48"}else{"$root\dist"}
& dotnet build $project -c Release -o $guiOut --nologo
if ($LASTEXITCODE -ne 0) { throw 'GUI build failed' }
Write-Output "Built: $guiOut\EDCBepgEx.exe"
if($Framework -eq 'net48'){& "$root\Publish-Framework48.ps1" -VsRoot $VsRoot -Version $Version}
