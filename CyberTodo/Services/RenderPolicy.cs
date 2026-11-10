using System.Windows.Interop;
using System.Windows.Media;

namespace CyberTodo.Services;

public enum RenderPreference
{
    /// <summary>WPF yazılım rasterleştirici (varsayılan): D3D cihazı/yüzeyleri oluşturulmaz.</summary>
    Software,

    /// <summary>GPU hızlandırma (WPF varsayılanı).</summary>
    Gpu,
}

/// <summary>
/// Süreç genelinde render modu. Ölçüm (2.500 görev, ekran dışı pencere, 3 koşu medyanı):
///  - GPU:      çalışma kümesi 266 MB, private 212 MB
///  - Yazılım:  çalışma kümesi 149 MB, private  97 MB
/// Bu arayüzde (statik HUD, birkaç düz çerçeve) CPU maliyeti aynı çıktı: grafikte gezinme ve liste kaydırma
/// iki modda da ~%32-40 tek çekirdek (200 hover / 300 kaydırma karesi), boşta ~%0. GPU yolunun getirisi yok,
/// ~115 MB'a mal oluyor; ayrıca D3D "cihaz kaybı" (uyku/uyanma, sürücü sıfırlama, RDP) sorunlarından kaçınılır.
/// Çalışma anında geçiş işe yaramıyor (gizliyken pencere başına SoftwareOnly belleği düşürmedi) → süreç
/// başlarken, ilk pencereden ÖNCE seçilir. Çok yüksek çözünürlüklü ekranda sorun olursa
/// <c>CALABATICA_RENDER=gpu</c> ortam değişkeni eski davranışı geri getirir.
/// </summary>
public static class RenderPolicy
{
    public const string EnvironmentVariable = "CALABATICA_RENDER";

    /// <summary>"gpu" (büyük/küçük harf fark etmez) → GPU; boş, "software" ya da tanınmayan değer → yazılım.</summary>
    public static RenderPreference Parse(string? value) =>
        string.Equals(value?.Trim(), "gpu", StringComparison.OrdinalIgnoreCase) ? RenderPreference.Gpu : RenderPreference.Software;

    /// <summary>İlk pencere oluşturulmadan önce çağrılmalı. Uygulanan tercihi döndürür.</summary>
    public static RenderPreference Apply(string? environmentValue = null)
    {
        var preference = Parse(environmentValue ?? Environment.GetEnvironmentVariable(EnvironmentVariable));
        RenderOptions.ProcessRenderMode = preference == RenderPreference.Software ? RenderMode.SoftwareOnly : RenderMode.Default;
        return preference;
    }
}
