using System.Windows;

namespace CyberTodo.Themes;

/// <summary>
/// Ortak tema sözlüğü (Themes/Theme.xaml) uygulama düzeyinde bir kez yüklenir; ana pencere, mini pencere ve
/// ayrı UserControl'ler StaticResource'larını buradan çözer. Pencereler yalnızca tema renklerini kendi
/// sözlüklerinde ezer (MainWindow.ApplyTheme).
/// </summary>
public static class ThemeResources
{
    public static Uri Source { get; } = new("pack://application:,,,/CALABATICA;component/Themes/Theme.xaml");

    public static void EnsureLoaded(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (!application.Resources.MergedDictionaries.Any(static d => d.Source == Source))
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = Source });
        }
    }
}
