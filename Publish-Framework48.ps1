param([string]$VsRoot='',[string]$Version='')
$ErrorActionPreference='Stop'
$project=Join-Path $PSScriptRoot 'src-net48\EpgCollector.Net48.csproj'
# Only releases (-Version 1.2.3, from a v1.2.3 tag) carry a version; other builds are labelled dev-<commit>.
if($Version){$label=$Version}else{$Version='0.0.0-dev';$commit=git -C $PSScriptRoot rev-parse --short HEAD 2>$null;$label=if($commit){"dev-$commit"}else{'dev'}}
if(!$VsRoot){$VsRoot=& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath}
if(!$VsRoot){throw 'Visual Studio C++ build tools not found'}
$root=$PSScriptRoot
$target=Join-Path $root 'framework48'
& dotnet build $project -c Release -o $target --nologo "-p:Version=$Version"
if($LASTEXITCODE -ne 0){throw '.NET Framework 4.8 build failed'}
$redistRoot=Join-Path $VsRoot 'VC\Redist\MSVC'
$redist=Get-ChildItem -LiteralPath $redistRoot -Directory | Where-Object {$_.Name -match '^\d+\.\d+\.\d+$'} | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
if(!$redist){throw 'Visual C++ redistributable files not found'}
$nativeFiles=@()
foreach($arch in @('x86','x64')){
    $native=Join-Path $target "native\$arch"
    New-Item -ItemType Directory -Path $native -Force | Out-Null
    foreach($file in @('EpgWorker.exe','EpgDataCap3.dll')){
        Copy-Item -LiteralPath (Join-Path $root "dist\native\$arch\$file") -Destination $native -Force
        $nativeFiles+=Get-Item -LiteralPath (Join-Path $native $file)
    }
    $crt=Get-ChildItem -LiteralPath (Join-Path $redist.FullName $arch) -Directory | Where-Object {$_.Name -match '^Microsoft\.VC\d+\.CRT$'} | Select-Object -First 1
    if(!$crt){throw "C++ CRT missing: $arch"}
    foreach($dll in (Get-ChildItem -LiteralPath $crt.FullName -Filter '*.dll')){
        Copy-Item -LiteralPath $dll.FullName -Destination $native -Force
        $nativeFiles+=Get-Item -LiteralPath (Join-Path $native $dll.Name)
    }
}
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.txt') -Destination $target -Force
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $target 'LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $target 'README-development.md') -Force
@'
EDCBepgEx $Version

ZIPをすべて展開し、EDCBepgEx.exeを起動してください。
この版はWindowsにインストールされている.NET Framework 4.8以降を使用します。
.NET 10 Desktop Runtimeは不要で、.NETランタイムも同梱していません。
.NET Framework 4.8未導入の古いWindowsでは、4.8の導入が必要です。

exeだけでなく、付属DLL・EDCBepgEx.exe.config・nativeフォルダーも必要です。
JSON等の機能用DLLとEPG解析用のC++ DLLは付属します。
既存のconfig.jsonをコピーすれば、Bon設定・スケジュール・列設定を引き継げます。
EPG保存先・BonDriver・ChSet4のパスは、使用するPCに合わせて確認してください。
BonDriver自身に必要なDLLは、そのBonDriverの要件に従って配置してください。

個人設定、BonDriver、受信データ、ログはこのZIPに含めません。
取得の手順・dat履歴の注意点はREADME-development.mdを参照してください。
'@.Replace('$Version',$label) | Set-Content -LiteralPath (Join-Path $target 'README.txt') -Encoding utf8
$packages=if($env:NUGET_PACKAGES){$env:NUGET_PACKAGES}else{Join-Path $env:USERPROFILE '.nuget\packages'}
$licenses=Join-Path $target 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
$assets=Get-Content -LiteralPath (Join-Path $root 'src-net48\obj\project.assets.json') -Raw | ConvertFrom-Json
$licenseFiles=@()
foreach($library in $assets.libraries.PSObject.Properties){
    if($library.Value.type -ne 'package'){continue}
    $package=Join-Path $packages $library.Value.path
    foreach($name in @('LICENSE','LICENSE.TXT','LICENSE.md','THIRD-PARTY-NOTICES.TXT')){
        $file=Join-Path $package $name
        if(Test-Path -LiteralPath $file){
            $destination=Join-Path $licenses ($library.Name.Replace('/','-')+'-'+$name)
            Copy-Item -LiteralPath $file -Destination $destination -Force
            $licenseFiles+=Get-Item -LiteralPath $destination
        }
    }
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=Join-Path $root "EDCBepgEx-$label.zip"
$stream=[System.IO.File]::Create($zip)
$archive=[System.IO.Compression.ZipArchive]::new($stream,[System.IO.Compression.ZipArchiveMode]::Create)
try{
    $managed=Get-ChildItem -LiteralPath $target -File | Where-Object {$_.Name -ne 'config.json' -and $_.Name -notmatch '^EpgCollector\.(exe|dll|exe\.config)$' -and ($_.Extension -in @('.exe','.dll','.config','.txt','.md'))}
    foreach($file in (@($managed)+@($nativeFiles)+@($licenseFiles))){
        $entry=$file.FullName.Substring($target.Length+1).Replace('\','/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$entry) | Out-Null
    }
}finally{$archive.Dispose();$stream.Dispose()}
Write-Output "Framework 4.8 folder: $target"
Write-Output "Framework 4.8 ZIP: $zip"
