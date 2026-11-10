using System;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace CalabaticaSetup;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Yalnızca-kaldırıcı derlemesi (Uninstall.exe) argümansız açılsa bile kaldırma modunda çalışır.
        bool uninstall = !InstallerEngine.HasPayload
            || args.Any(static a => string.Equals(a, InstallerEngine.UninstallArgument, StringComparison.OrdinalIgnoreCase));
        bool quiet = args.Any(static a => string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase));

        // Tanılama: imza doğrulamasını tek başına dene (çıkış kodu 0 = Microsoft imzalı ve geçerli).
        int verifyIndex = Array.FindIndex(args, static a => string.Equals(a, "--verify-signature", StringComparison.OrdinalIgnoreCase));
        if (verifyIndex >= 0 && verifyIndex + 1 < args.Length)
        {
            try
            {
                RuntimeInstaller.VerifyMicrosoftSignature(args[verifyIndex + 1]);
                return 0;
            }
            catch (InvalidOperationException)
            {
                return 1;
            }
        }

        bool runtimeCheck = args.Any(static a => string.Equals(a, "--check-runtime", StringComparison.OrdinalIgnoreCase));
        if (runtimeCheck)
        {
            return RuntimeInstaller.IsInstalled() ? 0 : 1;
        }

        // Sessiz kaldırma (QuietUninstallString): arayüz yok, kullanıcı verisi korunur.
        if (uninstall && quiet)
        {
            InstallerEngine.Uninstall(deleteUserData: false, _ => { });
            InstallerEngine.ScheduleSelfDelete();
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Exit += (_, _) => InstallerEngine.ScheduleSelfDelete();
        return app.Run(new SetupWindow(uninstall, Texts.For(CultureInfo.CurrentUICulture)));
    }
}

/// <summary>Kurulum metinleri: Windows dili Türkçe ise TR, değilse EN.</summary>
public sealed class Texts
{
    public string InstallTitle = "", UninstallTitle = "", InstallLead = "", UninstallLead = "", Location = "", Desktop = "", LaunchAfter = "",
        DeleteData = "", Install = "", Uninstall = "", Cancel = "", Close = "", Launch = "", AppRunning = "", Working = "",
        Installed = "", Uninstalled = "", Failed = "", Close_ = "", Copy = "", Shortcuts = "", Registry = "", Files = "", Reinstall = "",
        RuntimeOk = "", RuntimeMissing = "", RuntimeDownload = "", RuntimeVerify = "", RuntimeInstall = "";

    public static Texts For(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName == "tr" ? Turkish : English;

    private static readonly Texts Turkish = new()
    {
        InstallTitle = "KURULUM",
        UninstallTitle = "KALDIRMA",
        InstallLead = "CALABATICA bu bilgisayara kurulacak. Yönetici izni gerekmez.",
        UninstallLead = "CALABATICA bu bilgisayardan kaldırılacak.",
        Reinstall = "CALABATICA zaten kurulu; yeni sürümle güncellenecek (görevlerin korunur).",
        Location = "KONUM",
        Desktop = "Masaüstü kısayolu oluştur",
        LaunchAfter = "Kurulumdan sonra başlat",
        DeleteData = "Görev verilerimi de sil (geri alınamaz)",
        Install = "KUR",
        Uninstall = "KALDIR",
        Cancel = "İPTAL",
        Close = "KAPAT",
        Launch = "BAŞLAT",
        AppRunning = "CALABATICA şu an çalışıyor. Devam edilirse kapatılacak (kayıtlı görevler korunur).",
        Working = "İşleniyor…",
        Close_ = "Çalışan uygulama kapatılıyor…",
        Copy = "Dosyalar kopyalanıyor…",
        Shortcuts = "Kısayollar düzenleniyor…",
        Registry = "Windows kaydı güncelleniyor…",
        Files = "Dosyalar siliniyor…",
        Installed = "Kurulum tamamlandı.",
        Uninstalled = "CALABATICA kaldırıldı.",
        Failed = "Hata: ",
        RuntimeOk = "✓ .NET 10 Masaüstü Çalışma Zamanı kurulu",
        RuntimeMissing = "! .NET 10 Masaüstü Çalışma Zamanı yok: Microsoft'tan indirilip kurulacak (~60 MB, bir kerelik, yönetici onayı istenir)",
        RuntimeDownload = ".NET 10 indiriliyor… %{0}",
        RuntimeVerify = "Microsoft imzası doğrulanıyor…",
        RuntimeInstall = ".NET 10 kuruluyor (Microsoft kurulum penceresi)…",
    };

    private static readonly Texts English = new()
    {
        InstallTitle = "SETUP",
        UninstallTitle = "UNINSTALL",
        InstallLead = "CALABATICA will be installed on this computer. No administrator rights needed.",
        UninstallLead = "CALABATICA will be removed from this computer.",
        Reinstall = "CALABATICA is already installed; it will be updated (your tasks are kept).",
        Location = "LOCATION",
        Desktop = "Create desktop shortcut",
        LaunchAfter = "Launch after setup",
        DeleteData = "Also delete my task data (cannot be undone)",
        Install = "INSTALL",
        Uninstall = "UNINSTALL",
        Cancel = "CANCEL",
        Close = "CLOSE",
        Launch = "LAUNCH",
        AppRunning = "CALABATICA is running. It will be closed if you continue (saved tasks are kept).",
        Working = "Working…",
        Close_ = "Closing the running app…",
        Copy = "Copying files…",
        Shortcuts = "Updating shortcuts…",
        Registry = "Updating Windows registration…",
        Files = "Removing files…",
        Installed = "Setup completed.",
        Uninstalled = "CALABATICA has been removed.",
        Failed = "Error: ",
        RuntimeOk = "✓ .NET 10 Desktop Runtime is installed",
        RuntimeMissing = "! .NET 10 Desktop Runtime missing: it will be downloaded from Microsoft and installed (~60 MB, one time, needs admin approval)",
        RuntimeDownload = "Downloading .NET 10… {0}%",
        RuntimeVerify = "Verifying Microsoft signature…",
        RuntimeInstall = "Installing .NET 10 (Microsoft setup window)…",
    };

    public string Step(string key) => key switch
    {
        "close" => Close_,
        "copy" => Copy,
        "shortcuts" => Shortcuts,
        "registry" => Registry,
        "files" => Files,
        "runtime-download" => string.Format(RuntimeDownload, 0),
        "runtime-verify" => RuntimeVerify,
        "runtime-install" => RuntimeInstall,
        _ => Working,
    };
}
