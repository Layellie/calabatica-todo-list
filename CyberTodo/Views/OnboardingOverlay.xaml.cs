using System.Windows.Controls;

namespace CyberTodo.Views;

/// <summary>İlk açılış rehberi (DataContext = MainViewModel, kaynaklar Themes/Theme.xaml).</summary>
public partial class OnboardingOverlay : UserControl
{
    public OnboardingOverlay() => InitializeComponent();
}
