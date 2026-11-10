using System.Windows.Controls;

namespace CyberTodo.Views;

/// <summary>MainWindow'dan ayrılmış bölüm (DataContext = MainViewModel, kaynaklar Themes/Theme.xaml).</summary>
public partial class TemplatesOverlay : UserControl
{
    public TemplatesOverlay() => InitializeComponent();
}
