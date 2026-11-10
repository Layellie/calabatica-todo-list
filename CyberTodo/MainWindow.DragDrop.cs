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
    // ───────────────────────────── Sürükle-bırak ─────────────────────────────

    private void DragSource_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CyberTask task })
        {
            _dragCandidate = task;
            _dragStart = e.GetPosition(this);
        }
    }

    /// <summary>Sistem eşiği kadar hareket edince sürükleme başlar (tıklamayla karışmaz).</summary>
    private void DragSource_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragCandidate = null;
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var task = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(CyberTask), task), DragDropEffects.Move);
        _viewModel.SetDropTarget(null); // iptal / pencere dışına bırakma
    }

    private static bool IsLowerHalf(FrameworkElement row, DragEventArgs e) => e.GetPosition(row).Y > row.ActualHeight / 2;

    private void Row_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(CyberTask)) is CyberTask dragged
            && sender is FrameworkElement { DataContext: CyberTask target } row
            && !ReferenceEquals(dragged, target))
        {
            e.Effects = DragDropEffects.Move;
            _viewModel.SetDropTarget(target, IsLowerHalf(row, e));
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private async void Row_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(CyberTask)) is CyberTask dragged && sender is FrameworkElement { DataContext: CyberTask target } row)
        {
            await _viewModel.MoveTaskAsync(dragged, target, IsLowerHalf(row, e));
        }
    }

    /// <summary>Satırların altındaki boş alan: listenin sonuna taşı.</summary>
    private void ListEnd_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(CyberTask)) ? DragDropEffects.Move : DragDropEffects.None;
        _viewModel.SetDropTarget(null);
        e.Handled = true;
    }

    private async void ListEnd_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(CyberTask)) is CyberTask dragged)
        {
            await _viewModel.MoveTaskAsync(dragged, null);
        }
    }
}
