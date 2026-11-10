# CALABATICA — Sürüm ve güncelleme notları

## Sürüm nereden gelir?
- Başlıktaki `v1.0` uygulamanın **kendi** sürümüdür (`AssemblyInformationalVersion`).
- Yerel derlemede `CyberTodo/CyberTodo.csproj` içindeki `<Version>` kullanılır.
- GitHub'da `v1.1.0` gibi bir etiket itildiğinde `.github/workflows/release.yml` sürümü **etiketten** alır
  (`-p:Version=1.1.0`) → yayınlanan exe'nin başlığında `v1.1` yazar.

## Güncelleme denetimi (hazır)
- Uygulama açılışta (ayarlardan kapatılabilir) `https://api.github.com/repos/<depo>/releases/latest` adresini okur.
- Daha yeni sürüm varsa başlıkta `▲ v1.1` görünür; tıklayınca yayın sayfası açılır.
- Depo bilgisi derleme anında exe'ye gömülür: yayın akışı `-p:GitHubRepository=${{ github.repository }}` geçirir.
  Yerelde denemek için csproj'daki `<GitHubRepository>` değerine `kullanici/depo` yazılabilir.
- Depo boşsa denetim **kapalıdır** (ağa hiç çıkılmaz).

## Otomatik kurulum (henüz YOK — sonraki adım)
Yayın akışı şimdiden şunları üretir:
- `CALABATICA-win-x64.zip` — yayın paketi
- `CALABATICA-win-x64.zip.sha256` — bütünlük doğrulaması

`UpdateService` bu iki dosyanın indirme adreslerini (`ReleaseInfo.AssetUrl`, `ChecksumUrl`) zaten çözümlüyor.
Kalan iş: indir → SHA256 doğrula → ayrı küçük bir güncelleyici süreçle dosyaları değiştir → yeniden başlat.

## setup.exe
- Yerelde: `./build-installer.ps1 -Version 1.0.0` → `artifacts/CALABATICA-Setup-1.0.0.exe`
- Setup ~0,7 MB. Uygulama **framework-dependent tek exe** (~0,6 MB); kurulum programı .NET Framework 4.8
  (Windows 10/11'de yerleşik).
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
