using System.ComponentModel;
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
using CyberTodo.ViewModels;

namespace CyberTodo;

public partial class MainWindow
{
    /// <summary>
    /// Odak kısayolları (VM komutu değil, View işi): Ctrl+N yeni görev kutusu, Ctrl+F arama.
    /// Diyalog açıkken arka plandaki alanlara odak taşınmaz.
    /// </summary>
    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool dialogOpen = _viewModel.Editor is not null || _viewModel.IsSettingsOpen || _viewModel.IsTemplatesOpen || _viewModel.IsDataOpen
            || _viewModel.IsOnboardingOpen;
        if (dialogOpen)
        {
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            await HandleListKeyAsync(e);
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        TextBox? target = e.Key switch
        {
            Key.N => TaskTitleBox,
            Key.F => SearchBox,
            _ => null,
        };

        if (target is not null)
        {
            target.Focus();
            target.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Fare olmadan liste: ↑/↓/Home/End seç, Space tamamla, Enter düzenle, Del sil, F odak başlat/durdur.
    /// Metin kutusunda ve seçim listelerinde (kategori çipleri vb.) tuşlar kendi işini yapar; Esc metin kutusundan
    /// listeye geçirir (arama doluysa önce aramayı temizler).
    /// </summary>
    private async Task HandleListKeyAsync(KeyEventArgs e)
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        if (focused is TextBox textBox)
        {
            if (e.Key == Key.Escape && (textBox != SearchBox || _viewModel.SearchText.Length == 0))
            {
                ActiveList.Focus();
                e.Handled = true;
            }

            return;
        }

        if (focused is ListBoxItem or ListBox or ComboBox)
        {
            return;
        }

        bool hasSelection = _viewModel.SelectedTask is not null;
        switch (e.Key)
        {
            case Key.Down:
                _viewModel.MoveSelection(1);
                break;
            case Key.Up:
                _viewModel.MoveSelection(-1);
                break;
            case Key.Home:
                _viewModel.SelectEdge(last: false);
                break;
            case Key.End:
                _viewModel.SelectEdge(last: true);
                break;
            case Key.Space when hasSelection:
                e.Handled = true; // await öncesi: düğme de tetiklenmesin
                await _viewModel.CompleteSelectedAsync();
                return;
            case Key.Enter when hasSelection:
                _viewModel.EditSelected();
                break;
            case Key.Delete when hasSelection:
                e.Handled = true;
                await _viewModel.DeleteSelectedAsync();
                return;
            case Key.F when hasSelection:
                e.Handled = true;
                await _viewModel.ToggleFocusSelectedAsync();
                return;
            default:
                return;
        }

        e.Handled = true;
        if (!ActiveList.IsKeyboardFocusWithin)
        {
            ActiveList.Focus(); // seçim yapıldı: sonraki tuşlar da listeye gelsin
        }
    }
}
