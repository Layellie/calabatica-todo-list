# CALABATICA — Sürüm ve güncelleme notları

## Sürüm nereden gelir?
- Başlıktaki `v1.0` uygulamanın **kendi** sürümüdür (`AssemblyInformationalVersion`).
- Yerel derlemede `CyberTodo/CyberTodo.csproj` içindeki `<Version>` kullanılır.
- GitHub'da `v1.1.0` gibi bir etiket itildiğinde `.github/workflows/release.yml` sürümü **etiketten** alır
  (`-p:Version=1.1.0`) → yayınlanan exe'nin başlığında `v1.1` yazar.

## Güncelleme denetimi ve otomatik kurulum (hazır)
- Uygulama açılışta (ayarlardan kapatılabilir) `https://api.github.com/repos/<depo>/releases/latest` adresini okur.
- Daha yeni sürüm varsa başlıkta `▲ v1.1` görünür; tıklayınca yayın sayfası açılır.
- Depo bilgisi derleme anında exe'ye gömülür: yayın akışı `-p:GitHubRepository=${{ github.repository }}` geçirir.
  csproj'daki `<GitHubRepository>` varsayılanı bu depodur (yerel derlemede de çalışır); fork'ta değiştirilebilir.
- Depo boşsa denetim **kapalıdır** (ağa hiç çıkılmaz).
- GitHub `releases/latest` için 404 dönerse ayarlarda "henüz yayınlanmış sürüm yok (ya da depo gizli)" yazar.
  **Depo private ise herkese açık API sürümü göremez ve indirme çalışmaz: otomatik güncelleme için depo public olmalıdır.**

Yayın akışı güncelleme için şunları üretir (`scripts/package-update.ps1`):
- `CALABATICA-win-x64.zip` — yalnızca `CALABATICA.exe` içeren paket
- `CALABATICA-win-x64.zip.sha256` — bütünlük doğrulaması

Uygulama bu paketi kendisi kurar (`UpdateInstaller`): indir → SHA256 doğrula → zip'te tam olarak tek `CALABATICA.exe`
olduğunu, ürün adını ve sürümü denetle → çalışan exe'yi `.old` yapıp yenisini yerine koy → yeniden başlat.
Yalnızca setup ile kurulmuş kopya (`Uninstall.exe` yanında) kendini günceller; sadece https `github.com` adresleri.
SHA256 dosyanın bozulmadığını doğrular, yayıncının kim olduğunu değil (exe imzasız).

## Yayın provası (her PR'da)
Etiket atılana kadar `release.yml` hiç çalışmadığı için, bozuk bir yayın ancak yayın anında anlaşılırdı. CI bu yüzden
`release.yml`'in yaptığını her PR'da dener: `build-installer.ps1` → `scripts/package-update.ps1` → doğrulama:

```powershell
dotnet run --project tests/CalabaticaTests -c Release -- verify-package `
    --dir artifacts/release --setup artifacts/CALABATICA-Setup-0.0.1.exe --version 0.0.1
```

`verify-package` paketi **gerçek `UpdateInstaller` koduyla** kurar (SHA256, tek giriş, ürün adı, sürüm, exe değiştirme) ve
setup'ın boyutunu (varsayılan ≤ 2 MB) ile `.sha256` dosyasını denetler. Çıkış kodu = başarısız denetim sayısı.
Aynı doğrulama `release.yml`'de yayın oluşturulmadan hemen önce de çalışır; başarısızsa Release açılmaz.

## setup.exe
- Yerelde: `./build-installer.ps1 -Version 1.0.0` → `artifacts/CALABATICA-Setup-1.0.0.exe`
- Setup ~1,6 MB. Uygulama **framework-dependent, ReadyToRun tek exe** (~1,5 MB; ReadyToRun açılışı ~65 ms hızlandırır,
  ölçüm README'de); kurulum programı .NET Framework 4.8 (Windows 10/11'de yerleşik).
- Açılış ölçümü: `CALABATICA.exe --startup-benchmark rapor.txt` (+ zorunlu `CALABATICA_DATA_DIR=<boş/geçici klasör>`):
  pencere ekran dışında, odaksız açılır, gerçek veriye dokunmaz; zaman çizelgesi, JIT ve bellek raporu yazılır.
- .NET 10 Desktop Runtime yoksa setup `https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe` adresinden
  indirir (~60 MB, bir kerelik), **WinVerifyTrust + "O=Microsoft Corporation" imzalayan** kontrolünden geçirir,
  Microsoft kurulumunu `/install /passive /norestart` ile çalıştırır (UAC onayı Microsoft kurulumundan gelir).
- Tanılama: `CALABATICA-Setup-x.y.z.exe --check-runtime` (0 = kurulu), `--verify-signature <dosya>` (0 = MS imzalı).
- Self-contained seçenek 66–72 MB tutuyordu (WPF trimming desteklemiyor); bilinçli olarak bırakıldı.
- Kullanıcı başına kurar (yönetici izni yok): `%LOCALAPPDATA%\Programs\CALABATICA`, Başlat menüsü (+ isteğe bağlı
  masaüstü) kısayolu, "Programlar ve Özellikler" kaydı. Kaldırıcı (`Uninstall.exe`, ~60 KB) görev verisini
  varsayılan olarak korur; isteğe bağlı silinir.
- Exe imzasız → ilk çalıştırmada Windows SmartScreen uyarısı çıkabilir ("Ek bilgi → Yine de çalıştır").
  Kalıcı çözüm: kod imzalama sertifikası.

## İlk yükleme (sen "yükle" dediğinde)
1. Projeyi kalıcı bir klasöre taşı (şu anki klasör oturuma bağlı geçici bir klasör).
2. `git init`, ilk commit, GitHub'da depo oluştur, `git push`.
3. `git tag v1.0.0 && git push origin v1.0.0` → Actions yayını oluşturur.
