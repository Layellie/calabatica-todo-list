# Otomatik güncellemenin indireceği paketi üretir: CALABATICA-win-x64.zip (yalnızca CALABATICA.exe) + .sha256.
# release.yml ve CI yayın provası AYNI betiği kullanır; ad ve biçim UpdateService.AssetName ile eşleşmek zorunda
# (tests: `verify-package` komutu bunu gerçek güncelleyici koduyla doğrular).
param(
    [Parameter(Mandatory)][string]$AppExe,
    [Parameter(Mandatory)][string]$OutDir
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $AppExe)) { throw "Uygulama exe'si yok: $AppExe" }
if ((Split-Path $AppExe -Leaf) -ne "CALABATICA.exe") { throw "Exe adı CALABATICA.exe olmalı (güncelleyici paketteki adı doğrular): $AppExe" }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$zip = Join-Path $OutDir "CALABATICA-win-x64.zip"
Remove-Item $zip, "$zip.sha256" -ErrorAction SilentlyContinue

Compress-Archive -Path $AppExe -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  CALABATICA-win-x64.zip" | Out-File "$zip.sha256" -Encoding ascii

Write-Host ("Paket: {0} ({1:N2} MB)  SHA256: {2}" -f $zip, ((Get-Item $zip).Length / 1MB), $hash)