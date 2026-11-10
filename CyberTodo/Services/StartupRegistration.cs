using Microsoft.Win32;

namespace CyberTodo.Services;

/// <summary>"Windows ile başlat" ayarı. Test edilebilirlik için arayüz arkasında.</summary>
public interface IStartupRegistration
{
    bool IsEnabled();

    /// <summary>Hata durumunda istisna fırlatır (çağıran kullanıcıya gösterir).</summary>
    void SetEnabled(bool enabled);
}

/// <summary>
/// Kullanıcı başına (yönetici izni gerekmez): HKCU\Software\Microsoft\Windows\CurrentVersion\Run\CALABATICA.
/// Sadece kendi değerimizi yazar/siler. Tepside başlaması için "--tray" argümanı eklenir.
/// </summary>
public sealed class RegistryStartupRegistration : IStartupRegistration
{
    public const string TrayArgument = "--tray";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CALABATICA";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string command && command.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Run anahtarı açılamadı.");

        if (enabled)
        {
            // Yol tırnak içinde: boşluklu klasörlerde doğru çalışsın.
            key.SetValue(ValueName, $"\"{ExecutablePath}\" {TrayArgument}", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static string ExecutablePath =>
        Environment.ProcessPath ?? throw new InvalidOperationException("Çalışan exe yolu bulunamadı.");
}

/// <summary>Testler ve tasarım zamanı için bellekte tutan sürüm (kayıt defterine dokunmaz).</summary>
public sealed class InMemoryStartupRegistration : IStartupRegistration
{
    private bool _enabled;

    public bool IsEnabled() => _enabled;

    public void SetEnabled(bool enabled) => _enabled = enabled;
}
