using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CalabaticaSetup;

/// <summary>
/// Kullanıcı başına kurulum (yönetici izni gerekmez):
///   %LOCALAPPDATA%\Programs\CALABATICA\CALABATICA.exe + Uninstall.exe
///   Başlat menüsü (+ isteğe bağlı masaüstü) kısayolu
///   HKCU\...\Uninstall\CALABATICA → "Programlar ve Özellikler"de görünür
/// </summary>
public static class InstallerEngine
{
    public const string AppName = "CALABATICA";
    public const string ExeName = "CALABATICA.exe";
    public const string UninstallerName = "Uninstall.exe";
    public const string UninstallArgument = "--uninstall";

    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CALABATICA";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string PayloadResource = "payload.exe";
    private const string UninstallerResource = "uninstaller.exe";

    /// <summary>false → bu derleme yalnızca kaldırıcıdır (kurulacak uygulama içermez).</summary>
    public static bool HasPayload => Assembly.GetExecutingAssembly().GetManifestResourceInfo(PayloadResource) is not null;

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    public static string ExePath => Path.Combine(InstallDir, ExeName);

    public static string UninstallerPath => Path.Combine(InstallDir, UninstallerName);

    /// <summary>Uygulamanın görev verisi (uygulama bu klasörü kullanır; kaldırmada isteğe bağlı silinir).</summary>
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CyberTodo");

    private static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

    private static string DesktopShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    public static bool IsInstalled => File.Exists(ExePath);

    public static string SetupVersion
    {
        get
        {
            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return (info ?? "0.0.0").Split('+')[0];
        }
    }

    public static bool IsAppRunning() => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)).Length > 0;

    /// <summary>Çalışan uygulamayı kapatır. Uygulama ✕ ile tepsiye gizlendiği için zorla sonlandırılır (veri her değişiklikte kaydedilir).</summary>
    public static void CloseRunningApp()
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            using (process)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (InvalidOperationException)
                {
                    // Bilinçli: süreç bu arada kendiliğinden kapanmış.
                }
            }
        }
    }

    public static void Install(bool desktopShortcut, Action<string> report)
    {
        report("close");
        CloseRunningApp();

        report("copy");
        Directory.CreateDirectory(InstallDir);

        // Önce geçici dosyaya yaz, sonra yerine koy: yarıda kalan kurulum bozuk exe bırakmasın.
        string temp = ExePath + ".new";
        using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource)
            ?? throw new InvalidOperationException("Kurulum paketi bozuk: uygulama içeriği bulunamadı."))
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            payload.CopyTo(file);
        }

        if (File.Exists(ExePath))
        {
            File.Delete(ExePath);
        }

        File.Move(temp, ExePath);

        // Kaldırıcı: içinde uygulama olmayan küçük sürüm (gömülü). Yoksa (eski/elle derleme) kendi kopyası.
        using (var uninstaller = Assembly.GetExecutingAssembly().GetManifestResourceStream(UninstallerResource))
        {
            if (uninstaller is not null)
            {
                using var file = new FileStream(UninstallerPath, FileMode.Create, FileAccess.Write, FileShare.None);
                uninstaller.CopyTo(file);
            }
            else
            {
                string self = Assembly.GetExecutingAssembly().Location;
                if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(UninstallerPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(self, UninstallerPath, overwrite: true);
                }
            }
        }

        report("shortcuts");
        CreateShortcut(StartMenuShortcut, ExePath);
        if (desktopShortcut)
        {
            CreateShortcut(DesktopShortcut, ExePath);
        }
        else if (File.Exists(DesktopShortcut))
        {
            File.Delete(DesktopShortcut);
        }

        report("registry");
        WriteUninstallEntry();
    }

    public static void Uninstall(bool deleteUserData, Action<string> report)
    {
        report("close");
        CloseRunningApp();

        report("shortcuts");
        DeleteIfExists(StartMenuShortcut);
        DeleteIfExists(DesktopShortcut);

        report("registry");
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
        using (var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
        {
            // "Windows ile başlat" açıksa yalnızca BİZİM değerimizi sil.
            if (run?.GetValue(AppName) is string command && command.IndexOf(InstallDir, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                run.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }

        report("files");
        DeleteIfExists(ExePath);
        DeleteIfExists(ExePath + ".new");

        if (deleteUserData && Directory.Exists(DataDir))
        {
            Directory.Delete(DataDir, recursive: true);
        }

        // Çalışan Uninstall.exe kendini silemez → klasör, program kapanırken (ScheduleSelfDelete) silinir.
        if (IsRunningFromInstallDir)
        {
            s_selfDeletePending = true;
        }
        else if (Directory.Exists(InstallDir))
        {
            Directory.Delete(InstallDir, recursive: true);
        }
    }

    private static bool s_selfDeletePending;

    private static bool IsRunningFromInstallDir =>
        Path.GetFullPath(Assembly.GetExecutingAssembly().Location)
            .StartsWith(Path.GetFullPath(InstallDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Uygulamadan çıkarken çağrılır: bu süreç kapandıktan ~2 sn sonra kurulum klasörünü silen gizli cmd.
    /// (timeout komutu konsol girdisi olmadan çalışmadığı için bekleme ping ile yapılır.)
    /// </summary>
    public static void ScheduleSelfDelete()
    {
        if (!s_selfDeletePending)
        {
            return;
        }

        s_selfDeletePending = false;
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{InstallDir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    public static void LaunchApp()
    {
        Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, WorkingDirectory = InstallDir });
    }

    private static void WriteUninstallEntry()
    {
        string version = FileVersionInfo.GetVersionInfo(ExePath).ProductVersion?.Split('+')[0] ?? SetupVersion;
        long sizeKb = Directory.EnumerateFiles(InstallDir).Sum(static f => new FileInfo(f).Length) / 1024;

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath, writable: true)
            ?? throw new InvalidOperationException("Kaldırma kaydı oluşturulamadı.");
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", AppName);
        key.SetValue("DisplayIcon", ExePath);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{UninstallerPath}\" {UninstallArgument}");
        key.SetValue("QuietUninstallString", $"\"{UninstallerPath}\" {UninstallArgument} --quiet");
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <summary>.lnk oluşturma: Windows'un yerleşik WScript.Shell COM nesnesi (ek kütüphane yok).</summary>
    private static void CreateShortcut(string shortcutPath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell bulunamadı (kısayol oluşturulamıyor).");
        object shell = Activator.CreateInstance(shellType)!;
        object? shortcut = null;
        try
        {
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath })!;
            var t = shortcut.GetType();
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(targetPath)! });
            t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath + ",0" });
            t.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "CALABATICA" });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut is not null)
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
