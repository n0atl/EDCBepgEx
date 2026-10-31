$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$inputPath=Join-Path (Split-Path $root) 'EDCB\EpgData\7C607C60_epg.dat'
$dir=Join-Path $root 'test-work\basic-only'
New-Item -ItemType Directory -Path $dir -Force | Out-Null
$bytes=[IO.File]::ReadAllBytes($inputPath)
$output=[IO.File]::Create((Join-Path $dir 'basic.ts'))
$drop=@{}
try {
    for($i=0;$i+188 -le $bytes.Length;$i+=188) {
        $tsPid=(($bytes[$i+1] -band 31) -shl 8) -bor $bytes[$i+2]
        if($tsPid -in @(18,38,39)) {
            if(($bytes[$i+1] -band 64) -ne 0) {
                $offset=4
                if(($bytes[$i+3] -band 32) -ne 0){$offset+=1+$bytes[$i+4]}
                if($offset -lt 187){$offset+=1+$bytes[$i+$offset]}
                if($offset -lt 188){$table=$bytes[$i+$offset];$drop[$tsPid]=(($table -ge 88 -and $table -le 95) -or ($table -ge 104 -and $table -le 111))}
            }
            if($drop[$tsPid]){continue}
        }
        $output.Write($bytes,$i,188)
    }
} finally {$output.Dispose()}
$results=@()
foreach($arch in @('x86','x64')) {
    $worker=Join-Path $root "dist\native\$arch\EpgWorker.exe"
    & $worker --replay (Join-Path $dir 'basic.ts') 0 0 31840 31840 600 60 '31840:58368,31840:58369' (Join-Path $dir "$arch-basic-out.ts") (Join-Path $dir 'cancel') --basic
    if($LASTEXITCODE -ne 0){throw "$arch basic-only completion failed"}
    $results+="PASS $arch basic-mode argument and TSID/SID targets complete on terrestrial replay"
    & $worker --replay (Join-Path $dir 'basic.ts') 0 0 31840 31840 600 60 '31840:58368,31840:58369' (Join-Path $dir "$arch-detail-out.ts") (Join-Path $dir 'cancel')
    # Terrestrial EIT may advertise no separate extended tables, so both modes can complete.
    # Satellite BasicAll/HEITAll differences require satellite data for end-to-end verification.
    if($LASTEXITCODE -ne 0){throw "$arch terrestrial detailed replay regression"}
    $results+="PASS $arch terrestrial detailed mode remains compatible"
}
$results | Set-Content -LiteralPath (Join-Path $root 'basic-only-results.txt') -Encoding utf8
$results

exit 0
