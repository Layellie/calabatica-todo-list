using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.Themes;

namespace CyberTodo;

public partial class MainWindow
{
    private void ShowMini()
    {
        if (_mini is null)
        {
            _mini = new MiniWindow(_viewModel);
            ThemeApplier.Apply(_mini.Resources, _viewModel.CurrentTheme);
            _mini.IsVisibleChanged += (_, _) => EvaluateTrim();
            _mini.HideRequested += (_, _) =>
            {
                if (_viewModel.MinimizeToTray)
                {
                    _mini.Hide(); // mini mod sürer; tepsiden "Göster" tam görünüme döner
                    if (!_trayHintShown)
                    {
                        _trayHintShown = true;
                        _tray?.Notify(Localizer.Instance["Tray_Running"]);
                    }
                }
                else
                {
                    _viewModel.IsMiniMode = false; // tepsi kapalı: gizlemek uygulamayı kaybettirirdi → tam görünüme dön
                }
            };
            _mini.PlaceOnScreen();
        }

        _mini.Show();
        _mini.Activate();
    }
}
