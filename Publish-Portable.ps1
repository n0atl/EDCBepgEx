param([string]$VsRoot='')
$ErrorActionPreference='Stop'
if(!$VsRoot){$VsRoot=& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath}
if(!$VsRoot){throw 'Visual Studio C++ build tools not found'}
$root=$PSScriptRoot
$target=Join-Path $root 'portable-win-x64'
& dotnet publish (Join-Path $root 'src\EpgCollector.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $target --nologo
if($LASTEXITCODE -ne 0){throw 'Portable publish failed'}
$redistRoot=Join-Path $VsRoot 'VC\Redist\MSVC'
$redist=Get-ChildItem -LiteralPath $redistRoot -Directory | Where-Object {$_.Name -match '^\d+\.\d+\.\d+$'} | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
if(!$redist){throw 'Visual C++ redistributable files not found'}
foreach($arch in @('x86','x64')){
    $native=Join-Path $target "native\$arch"
    New-Item -ItemType Directory -Path $native -Force | Out-Null
    foreach($file in @('EpgWorker.exe','EpgDataCap3.dll')){Copy-Item -LiteralPath (Join-Path $root "dist\native\$arch\$file") -Destination $native -Force}
    $crt=Get-ChildItem -LiteralPath (Join-Path $redist.FullName $arch) -Directory | Where-Object {$_.Name -match '^Microsoft\.VC\d+\.CRT$'} | Select-Object -First 1
    if(!$crt){throw "C++ CRT missing: $arch"}
    Get-ChildItem -LiteralPath $crt.FullName -Filter '*.dll' | Copy-Item -Destination $native -Force
}
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.txt') -Destination $target -Force
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $target 'README-development.md') -Force
$runtime=Get-Content -LiteralPath (Join-Path $target 'EDCBepgEx.runtimeconfig.json') -Raw | ConvertFrom-Json
$packages=if($env:NUGET_PACKAGES){$env:NUGET_PACKAGES}else{Join-Path $env:USERPROFILE '.nuget\packages'}
$licenses=Join-Path $target 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
foreach($framework in $runtime.runtimeOptions.includedFrameworks){
    $id=($framework.name+'.runtime.win-x64').ToLowerInvariant()
    $package=Join-Path $packages "$id\$($framework.version)"
    foreach($name in @('LICENSE','LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')){
        $file=Join-Path $package $name
        if(Test-Path -LiteralPath $file){Copy-Item -LiteralPath $file -Destination (Join-Path $licenses ($id+'-'+$name)) -Force}
    }
}
@'
EDCBepgEx ランタイム同梱版（Windows 64bit）

ZIPをすべて展開し、EDCBepgEx.exeを起動してください。
.NET Desktop Runtimeの事前インストールは不要です。
exe単体ではなく、このフォルダー全体をコピーしてください。

EPG保存先・BonDriver・ChSet4は使用するPCに合わせて指定してください。
既存の設定を移す場合は、旧フォルダーのconfig.jsonをコピーできます。
その場合も、設定内のパスが移行先PCで有効か確認してください。
個人設定、BonDriver、受信データ、ログはZIPに含めません。
BonDriver自身の依存DLL等は、そのBonDriverの要件に従って配置してください。

詳細な動作説明はREADME-development.mdを参照してください。
'@ | Set-Content -LiteralPath (Join-Path $target 'README.txt') -Encoding utf8
$zip=Join-Path $root 'EDCBepgEx-portable-win-x64.zip'
# Include only distribution files. Existing user settings and captures stay local.
$files=Get-ChildItem -LiteralPath $target -File | Where-Object {$_.Extension -ne '.pdb' -and $_.Name -ne 'config.json' -and $_.Name -notmatch '^EpgCollector\.(exe|dll|deps\.json|runtimeconfig\.json)$'}
$folders=Get-ChildItem -LiteralPath $target -Directory | Where-Object {$_.Name -notin @('logs','temp','work')}
Compress-Archive -LiteralPath (@($files.FullName)+@($folders.FullName)) -DestinationPath $zip -Force
Write-Output "Portable folder: $target"
Write-Output "Portable ZIP: $zip"
