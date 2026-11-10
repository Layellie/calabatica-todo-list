# CALABATICA → setup.exe
# Kullanım:  ./build-installer.ps1               (csproj'daki sürüm)
#            ./build-installer.ps1 -Version 1.1.0 [-Repository kullanici/depo]
# Çıktı:     artifacts/CALABATICA-Setup-<sürüm>.exe  (+ artifacts/app/CALABATICA.exe)
param(
    [string]$Version = "1.0.0",
    [string]$Repository = ""
)

$ErrorActionPreference = "Stop"
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Sürüm 1.2.3 biçiminde olmalı: $Version" }

$root = $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$appOut = Join-Path $artifacts "app"
$setupOut = Join-Path $artifacts "setup"
Remove-Item $artifacts -Recurse -Force -ErrorAction SilentlyContinue

# 1) Uygulama: framework-dependent tek exe (~0,6 MB). .NET 10 Desktop Runtime'ı setup kontrol eder/kurar.
#    (Self-contained 66-72 MB tutuyordu; WPF trimming desteklemediği için küçültülemiyor.)
#    ReadyToRun: uygulama kodu önceden derlenir (exe ~0,8 → ~1,5 MB); ölçüm: ilk çizim ~65 ms, JIT 2.8k → 2.2k metot.
$appArgs = @(
    "publish", (Join-Path $root "CyberTodo/CyberTodo.csproj"),
    "-c", "Release", "-r", "win-x64", "--self-contained", "false",
    "-p:PublishSingleFile=true",
    "-p:PublishReadyToRun=true",
    "-p:DebugType=none",
    "-p:Version=$Version",
    "-o", $appOut
)
if ($Repository) { $appArgs += "-p:GitHubRepository=$Repository" }
dotnet @appArgs
if ($LASTEXITCODE -ne 0) { throw "Uygulama yayınlanamadı." }

$payload = Join-Path $appOut "CALABATICA.exe"
if (-not (Test-Path $payload)) { throw "Beklenen çıktı yok: $payload" }

$installerProject = Join-Path $root "Installer/CalabaticaSetup.csproj"
$uninstallerOut = Join-Path $artifacts "uninstaller"

# 2a) Küçük kaldırıcı (uygulama içermez) → kurulum klasöründe Uninstall.exe olur
dotnet build $installerProject -c Release --no-incremental "-p:Version=$Version" "-p:UninstallerOnly=true" -o $uninstallerOut
if ($LASTEXITCODE -ne 0) { throw "Kaldırıcı derlenemedi." }
$uninstaller = Join-Path $uninstallerOut "CalabaticaSetup.exe"

# 2b) Asıl kurulum programı (.NET Framework 4.8): uygulama + kaldırıcı gömülü
dotnet build $installerProject -c Release --no-incremental `
    "-p:Version=$Version" "-p:PayloadPath=$payload" "-p:UninstallerPath=$uninstaller" -o $setupOut
if ($LASTEXITCODE -ne 0) { throw "Kurulum programı derlenemedi." }

$setup = Join-Path $artifacts "CALABATICA-Setup-$Version.exe"
Copy-Item (Join-Path $setupOut "CalabaticaSetup.exe") $setup -Force

$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $setup -Leaf)" | Out-File "$setup.sha256" -Encoding ascii

Write-Host ""
Write-Host "Hazır: $setup" -ForegroundColor Yellow
Write-Host ("Boyut: {0:N1} MB  SHA256: {1}" -f ((Get-Item $setup).Length / 1MB), $hash)
